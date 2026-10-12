using System;
using HarmonyLib;
using LiteNetLib;
using MonomiPark.SlimeRancher.DataModel;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Shared wallet + keys: clients send deltas, host owns the totals. Only the machine that
// earned/spent runs AddCurrency/SpendCurrency (popups, achievements, currencyEverCollected
// stay local stats); everyone else just gets PlayerModel.SetCurrency/SetKeys.
public static class EconomySync
{
	static int moneyDelta, keysDelta;
	static bool moneyDirty, keysDirty;

	static readonly AccessTools.FieldRef<AccessDoorModel, GameObject> doorObj = AccessTools.FieldRefAccess<AccessDoorModel, GameObject>("gameObj");

	// Local change worth broadcasting: not a replay, and (client) not during the world load.
	internal static bool Live => Session.Active && !Session.Applying && (Session.IsHost || Session.InRemoteWorld);

	static PlayerModel Wallet => SRSingleton<SceneContext>.Instance.GameModel.GetPlayerModel();

	public static void Register()
	{
		if (Plugin.I) Plugin.I.gameObject.AddComponent<Pump>();

		Session.On(Op.Money, (_, r) => OnTotal(r.GetInt(), ref moneyDirty, m => m.currency, (m, v) => m.SetCurrency(v)));
		Session.On(Op.Keys, (_, r) => OnTotal(r.GetInt(), ref keysDirty, m => m.keys, (m, v) => m.SetKeys(v)));

		Session.On(Op.Door, (from, r) =>
		{
			if (Session.IsHost) Session.Relay(from, r, DeliveryMethod.ReliableOrdered);
			var id = r.GetString();
			var state = (AccessDoor.State)r.GetInt();
			if (!Session.InWorld || !SRSingleton<SceneContext>.Instance.GameModel.AllDoors().TryGetValue(id, out var m)) return;
			var door = doorObj(m)?.GetComponent<AccessDoor>();
			if (door == null || door.CurrState == state) return;
			using (Session.Apply()) door.CurrState = state;
		});

		Session.On(Op.Progress, (from, r) =>
		{
			if (Session.IsHost) Session.Relay(from, r, DeliveryMethod.ReliableOrdered);
			var type = (ProgressDirector.ProgressType)r.GetInt();
			var value = r.GetInt();
			if (!Session.InWorld) return;
			using (Session.Apply()) SRSingleton<SceneContext>.Instance.ProgressDirector.SetProgress(type, value);
		});

		Session.PlayerReady += p =>
		{
			Session.SendTo(p, Packets.Begin(Op.Money).Also(w => w.Put(Wallet.currency)), DeliveryMethod.ReliableOrdered);
			Session.SendTo(p, Packets.Begin(Op.Keys).Also(w => w.Put(Wallet.keys)), DeliveryMethod.ReliableOrdered);
		};
	}

	// Host: v is a client's delta (clamped at 0, then re-broadcast as a total). Client: v is the total.
	static void OnTotal(int v, ref bool dirty, Func<PlayerModel, int> get, Action<PlayerModel, int> set)
	{
		if (!Session.InWorld) return;
		var m = Wallet;
		using (Session.Apply())
		{
			if (Session.IsHost)
			{
				set(m, Math.Max(0, get(m) + v));
				dirty = true;
			}
			else set(m, v);
		}
	}

	static void Changed(ref int delta, ref bool dirty, int adjust)
	{
		if (!Live) return;
		if (Session.IsHost) dirty = true;
		else delta += adjust;
	}

	static void Flush()
	{
		if (!Session.Active || !Session.InWorld || (Session.IsClient && !Session.InRemoteWorld))
		{
			moneyDelta = keysDelta = 0;
			moneyDirty = keysDirty = false;
			return;
		}
		if (Session.IsHost)
		{
			if (moneyDirty) Session.Broadcast(Packets.Begin(Op.Money).Also(w => w.Put(Wallet.currency)), DeliveryMethod.ReliableOrdered);
			if (keysDirty) Session.Broadcast(Packets.Begin(Op.Keys).Also(w => w.Put(Wallet.keys)), DeliveryMethod.ReliableOrdered);
			moneyDirty = keysDirty = false;
			return;
		}
		if (moneyDelta != 0) Session.SendToHost(Packets.Begin(Op.Money).Also(w => w.Put(moneyDelta)), DeliveryMethod.ReliableOrdered);
		if (keysDelta != 0) Session.SendToHost(Packets.Begin(Op.Keys).Also(w => w.Put(keysDelta)), DeliveryMethod.ReliableOrdered);
		moneyDelta = keysDelta = 0;
	}

	sealed class Pump : MonoBehaviour
	{
		void LateUpdate() => Flush();
	}

	[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.AddCurrency))]
	static class AddCurrency
	{
		static void Postfix(int adjust) => Changed(ref moneyDelta, ref moneyDirty, adjust);
	}

	[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.SpendCurrency))]
	static class SpendCurrency
	{
		static void Postfix(int adjust) => Changed(ref moneyDelta, ref moneyDirty, -adjust);
	}

	[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.AddKey))]
	static class AddKey
	{
		static void Postfix() => Changed(ref keysDelta, ref keysDirty, 1);
	}

	[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.SpendKey))]
	static class SpendKey
	{
		static void Postfix(bool __result)
		{
			if (__result) Changed(ref keysDelta, ref keysDirty, -1);
		}
	}

	[HarmonyPatch(typeof(AccessDoor), nameof(AccessDoor.CurrState), MethodType.Setter)]
	static class DoorState
	{
		static void Prefix(AccessDoor __instance, out AccessDoor.State __state) => __state = __instance.CurrState;

		static void Postfix(AccessDoor __instance, AccessDoor.State value, AccessDoor.State __state)
		{
			if (!Live || value == __state) return;
			var w = Packets.Begin(Op.Door);
			w.Put(__instance.id);
			w.Put((int)value);
			Session.Distribute(w, DeliveryMethod.ReliableOrdered);
		}
	}

	// SetProgress/SetUniqueProgress/AddProgress all funnel here.
	[HarmonyPatch(typeof(ProgressDirector), "NoteProgressChanged")]
	static class ProgressChanged
	{
		static void Postfix(ProgressDirector __instance, ProgressDirector.ProgressType type)
		{
			if (!Live) return;
			var w = Packets.Begin(Op.Progress);
			w.Put((int)type);
			w.Put(__instance.GetProgress(type));
			Session.Distribute(w, DeliveryMethod.ReliableOrdered);
		}
	}

	// Sale points: only the actor's owner sells it.
	[HarmonyPatch(typeof(ScorePlort), "OnTriggerEnter")]
	static class MarketOwnerOnly
	{
		static bool Prefix(Collider col) => !ActorSync.IsRemoteOwned(col.gameObject);
	}
}
