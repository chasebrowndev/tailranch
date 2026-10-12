using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MonomiPark.SlimeRancher.DataModel;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Host owns the world clock. Clients run their own TimeDirector.Update replacement that
// predicts locally and eases toward the host. While a session is active, pausing never
// zeroes timeScale (the shared world keeps running behind menus); input still pauses.
public static class ClockSync
{
	const float SendInterval = 1f;
	static float nextSend;
	static double? host; // client: predicted host worldTime

	static readonly AccessTools.FieldRef<TimeDirector, WorldModel> worldModel = AccessTools.FieldRefAccess<TimeDirector, WorldModel>("worldModel");
	static readonly AccessTools.FieldRef<TimeDirector, float> timeFactor = AccessTools.FieldRefAccess<TimeDirector, float>("timeFactor");
	static readonly AccessTools.FieldRef<TimeDirector, float> ffTimeFactor = AccessTools.FieldRefAccess<TimeDirector, float>("ffTimeFactor");
	static readonly AccessTools.FieldRef<TimeDirector, float> savedTimeScale = AccessTools.FieldRefAccess<TimeDirector, float>("savedTimeScale");
	static readonly AccessTools.FieldRef<TimeDirector, int> pauserCount = AccessTools.FieldRefAccess<TimeDirector, int>("pauserCount");
	static readonly AccessTools.FieldRef<TimeDirector, int> specialPauserCount = AccessTools.FieldRefAccess<TimeDirector, int>("specialPauserCount");

	public static void Register()
	{
		Session.On(Op.Clock, (_, r) =>
		{
			var t = r.GetDouble();
			var ff = r.GetNullableDouble();
			var td = Director();
			if (!Session.IsClient || !Session.InRemoteWorld || !td) return;
			var m = worldModel(td);
			if (ff <= m.worldTime) ff = null; // stale: already reached locally
			m.fastForwardUntil = ff;
			var rate = Rate(td, ff.HasValue);
			host = t + Session.Rtt * 0.001 * rate;
			if (Math.Abs(host.Value - m.worldTime) > rate * 0.5f) m.worldTime = host.Value;
		});
		Session.PlayerReady += p => Session.SendTo(p, Packet(), DeliveryMethod.ReliableSequenced);
	}

	public static void Tick()
	{
		// Catches pauses that predate the session (e.g. hosting from behind the pause menu).
		if (Session.Active && Time.timeScale == 0f) Time.timeScale = 1f;
		if (!Session.InRemoteWorld) host = null;
		if (!Session.IsHost || !Session.InWorld || Time.unscaledTime < nextSend) return;
		nextSend = Time.unscaledTime + SendInterval;
		Session.Broadcast(Packet(), DeliveryMethod.ReliableSequenced);
	}

	static NetDataWriter Packet()
	{
		var m = worldModel(Director());
		var w = Packets.Begin(Op.Clock);
		w.Put(m.worldTime);
		w.Put(m.fastForwardUntil);
		return w;
	}

	static TimeDirector Director()
	{
		var sc = SRSingleton<SceneContext>.Instance;
		return sc ? sc.TimeDirector : null;
	}

	static float Rate(TimeDirector td, bool ff) => ff ? ffTimeFactor(td) : timeFactor(td);

	// What timeScale would be without our pause override: 0 whenever the game would have frozen.
	public static float TimeScale()
	{
		if (Session.Active)
		{
			var td = Director();
			if (td && ((pauserCount(td) > 0 && !Levels.isSpecialNonAlloc()) || specialPauserCount(td) > 0)) return 0f;
		}
		return Time.timeScale;
	}

	[HarmonyPatch(typeof(TimeDirector), nameof(TimeDirector.Update))]
	static class ClientClock
	{
		static bool Prefix(TimeDirector __instance)
		{
			if (!Session.InRemoteWorld) return true;
			var m = worldModel(__instance);
			var dt = Time.deltaTime * Rate(__instance, m.fastForwardUntil.HasValue);
			if (host.HasValue)
			{
				host += dt;
				// Ease toward the host but never step backward (OnPassedHour/OnPassedTime would re-fire).
				m.worldTime += Math.Max(0, dt + (host.Value - m.worldTime) * Math.Min(1f, Time.deltaTime));
			}
			else m.worldTime += dt;
			if (m.fastForwardUntil.HasValue && m.worldTime >= m.fastForwardUntil.Value)
			{
				m.worldTime = m.fastForwardUntil.Value;
				if (host > m.worldTime) host = m.worldTime;
				m.fastForwardUntil = null;
			}
			return false;
		}
	}

	// Clients can't sleep the shared world.
	[HarmonyPatch(typeof(TimeDirector), nameof(TimeDirector.FastForwardTo))]
	static class NoClientFastForward
	{
		static bool Prefix() => !Session.InRemoteWorld;
	}

	[HarmonyPatch(typeof(RanchHouseUI), nameof(RanchHouseUI.SleepUntilMorning))]
	static class NoClientSleep
	{
		static bool Prefix() => !Session.InRemoteWorld;
	}

	[HarmonyPatch(typeof(RanchHouseUI), nameof(RanchHouseUI.OnEnable))]
	static class NoClientSleepButton
	{
		static void Postfix(RanchHouseUI __instance)
		{
			if (Session.InRemoteWorld) Traverse.Create(__instance).Field("sleepButton").Property("interactable").SetValue(false);
		}
	}

	// Without the fast-forward a client death would lock the player until real time reaches dawn.
	[HarmonyPatch(typeof(LockOnDeath), nameof(LockOnDeath.LockUntil))]
	static class ClientDeath
	{
		static void Prefix(ref double unlockWorldTime, float delayTime)
		{
			var td = Director();
			if (!Session.InRemoteWorld || !td) return;
			unlockWorldTime = Math.Min(unlockWorldTime, td.WorldTime() + (delayTime + 1f) * timeFactor(td));
		}
	}

	[HarmonyPatch(typeof(TimeDirector), nameof(TimeDirector.Pause))]
	static class NoFreeze
	{
		static void Postfix(TimeDirector __instance)
		{
			if (!Session.Active || Time.timeScale != 0f) return;
			ref var saved = ref savedTimeScale(__instance);
			if (saved == 0f) saved = 1f; // DelayedUnpause restores this
			Time.timeScale = saved;
		}
	}

	// These read timeScale == 0 as "a menu is open"; feed them the would-be value instead.
	// InputDirector switches SRInput to PAUSE mode on it, which disables move/look/vac/shoot.
	[HarmonyPatch]
	static class MenuTimeScale
	{
		static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
		{
			AccessTools.Method(typeof(InputDirector), nameof(InputDirector.Update)),
			AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.Update)),
			AccessTools.Method(typeof(TimeDirector), nameof(TimeDirector.OnUnpause)),
		};

		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) =>
			code.MethodReplacer(AccessTools.PropertyGetter(typeof(Time), nameof(Time.timeScale)), AccessTools.Method(typeof(ClockSync), nameof(TimeScale)));
	}
}
