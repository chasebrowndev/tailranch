using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MonomiPark.SlimeRancher.DataModel;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// World objects persisted in the world save: gordos, treasure pods, echo note gordos, puzzle slots,
// switches, oases, fire columns/firestorm, gordo snares. The snapshot carries their state at join;
// these keep live changes in sync. Reward/prize spawns happen on the host only and reach clients
// as actors (ActorSync); clients replay the visuals.
public static class WorldSync
{
	const DeliveryMethod R = DeliveryMethod.ReliableOrdered;

	// GadgetState kinds.
	const byte Slot = 0, Switch = 1, Oasis = 2, Column = 3, Storm = 4, Snare = 5;

	static readonly AccessTools.FieldRef<GordoModel, GameObject> gordoObj = AccessTools.FieldRefAccess<GordoModel, GameObject>("gameObj");
	static readonly AccessTools.FieldRef<GordoEat, SnareModel> gordoSnare = AccessTools.FieldRefAccess<GordoEat, SnareModel>("snareModel");
	static readonly MethodInfo setEaten = AccessTools.Method(typeof(GordoEat), "SetEatenCount");
	static readonly MethodInfo reachedTarget = AccessTools.Method(typeof(GordoEat), "ReachedTarget");
	static readonly MethodInfo immediateReached = AccessTools.Method(typeof(GordoEat), "ImmediateReachedTarget");

	static readonly AccessTools.FieldRef<TreasurePodModel, GameObject> podObj = AccessTools.FieldRefAccess<TreasurePodModel, GameObject>("gameObj");
	static readonly AccessTools.FieldRef<TreasurePod, TreasurePodModel> podModel = AccessTools.FieldRefAccess<TreasurePod, TreasurePodModel>("model");

	static readonly AccessTools.FieldRef<EchoNoteGordoModel, GameObject> echoObj = AccessTools.FieldRefAccess<EchoNoteGordoModel, GameObject>("gameObject");
	static readonly AccessTools.FieldRef<EchoNoteGordo, EchoNoteGordoModel> echoModel = AccessTools.FieldRefAccess<EchoNoteGordo, EchoNoteGordoModel>("model");
	static readonly AccessTools.FieldRef<EchoNoteGordo, TeleportSource> echoTeleporter = AccessTools.FieldRefAccess<EchoNoteGordo, TeleportSource>("teleporter");
	static readonly AccessTools.FieldRef<EchoNoteGordo, SECTR_AudioCueInstance> echoActiveCue = AccessTools.FieldRefAccess<EchoNoteGordo, SECTR_AudioCueInstance>("onActiveCueInstance");

	static readonly AccessTools.FieldRef<PuzzleSlotModel, GameObject> slotObj = AccessTools.FieldRefAccess<PuzzleSlotModel, GameObject>("gameObj");
	static readonly AccessTools.FieldRef<PuzzleSlot, PuzzleSlotModel> slotModel = AccessTools.FieldRefAccess<PuzzleSlot, PuzzleSlotModel>("model");

	static readonly AccessTools.FieldRef<MasterSwitchModel, GameObject> switchObj = AccessTools.FieldRefAccess<MasterSwitchModel, GameObject>("gameObj");
	static readonly AccessTools.FieldRef<WorldStateMasterSwitch, MasterSwitchModel> switchModel = AccessTools.FieldRefAccess<WorldStateMasterSwitch, MasterSwitchModel>("model");
	static readonly MethodInfo setStateForAll = AccessTools.Method(typeof(WorldStateMasterSwitch), "SetStateForAll");

	static readonly AccessTools.FieldRef<OasisModel, GameObject> oasisObj = AccessTools.FieldRefAccess<OasisModel, GameObject>("gameObj");

	static readonly AccessTools.FieldRef<GordoSnare, SnareModel> snareModel = AccessTools.FieldRefAccess<GordoSnare, SnareModel>("model");
	static readonly MethodInfo snareGordo = AccessTools.Method(typeof(GordoSnare), "SnareGordo", new[] { typeof(Identifiable.Id) });
	static readonly MethodInfo attachBait = AccessTools.Method(typeof(GordoSnare), "AttachBait");

	static readonly MethodInfo gateSequence = AccessTools.Method(typeof(PuzzleGateActivator), "DoDeactivateSequence");

	// Host: TreasurePod.HasKey answers true while opening a pod for a client (their key, not ours).
	static bool forceKey;
	// Client: echo note gordos already asked of the host.
	static readonly HashSet<string> echoRequested = new();
	// Client: popped echo gordos whose local pop animation may still be running; hidden at the deadline.
	static readonly List<(EchoNoteGordo g, float at)> echoHide = new();
	static readonly Dictionary<string, FireColumn> columns = new();
	static (int mode, double end, double next) lastStorm = (-1, 0, 0);

	static GameModel Game => SRSingleton<SceneContext>.Instance.GameModel;
	static bool Live => EconomySync.Live;

	public static void Register()
	{
		// GordoState: key string, count int. c→h: count is a delta the client ate locally.
		// h→c: count is the host total (-1 = burst). key = gordo id, or "site:<siteId>" for a snared gordo.
		Session.On(Op.GordoState, (from, r) =>
		{
			var key = r.GetString();
			var v = r.GetInt();
			if (!Session.InWorld) return;
			var g = Gordo(key);
			if (g == null) return;
			var target = g.GetTargetCount();
			var old = g.GetEatenCount();
			using (Session.Apply())
			{
				if (Session.IsHost)
				{
					var now = old;
					if (old != -1)
					{
						now = old + v;
						setEaten.Invoke(g, new object[] { now });
						if (old < target && now >= target) Burst(g);
					}
					Session.Broadcast(GordoPacket(key, now), R);
					return;
				}
				if (old == -1 || old >= target) return; // burst done or under way here
				if (v == -1 || v >= target)
				{
					setEaten.Invoke(g, new object[] { Mathf.Max(v, target) });
					Burst(g);
				}
				else setEaten.Invoke(g, new object[] { v });
			}
		});

		// PodState: podId string. c→h: request to open (client had the key). h→c: pod opened.
		// The host opens it for real (prizes spawn there); clients only play the open.
		Session.On(Op.PodState, (from, r) =>
		{
			var id = r.GetString();
			if (!Session.InWorld || !Game.AllPods().TryGetValue(id, out var m)) return;
			var pod = podObj(m)?.GetComponent<TreasurePod>();
			if (pod == null || pod.CurrState == TreasurePod.State.OPEN) return;
			using (Session.Apply())
			{
				if (Session.IsHost)
				{
					if (pod.isActiveAndEnabled)
					{
						forceKey = true;
						try { pod.Activate(); }
						finally { forceKey = false; }
					}
					else OpenDormant(pod);
					Session.Relay(from, r, R);
					return;
				}
				pod.CurrState = TreasurePod.State.OPEN;
				if (!pod.isActiveAndEnabled) return;
				if (pod.openCue != null) SECTR_AudioSystem.Play(pod.openCue, pod.transform.position, loop: false);
				if (pod.openFX != null) SRBehaviour.InstantiateDynamic(pod.openFX, pod.transform.position, pod.transform.rotation);
			}
		});

		// EchoState: echoNoteGordoId string, state int (EchoNoteGordoModel.State, always POPPED).
		// c→h: client's local pop reached the burst (spawn is host-only). h→c: popped.
		Session.On(Op.EchoState, (from, r) =>
		{
			var id = r.GetString();
			var state = (EchoNoteGordoModel.State)r.GetInt();
			if (!Session.InWorld || state != EchoNoteGordoModel.State.POPPED) return;
			var m = Game.GetEchoNoteGordoModel(id);
			var g = m == null ? null : echoObj(m)?.GetComponent<EchoNoteGordo>();
			if (g == null) return;
			if (m.state == EchoNoteGordoModel.State.POPPED)
			{
				if (Session.IsHost) Session.SendTo(from, EchoPacket(id), R);
				return;
			}
			using (Session.Apply())
			{
				if (Session.IsHost)
				{
					if (g.gameObject.activeInHierarchy)
					{
						m.state = EchoNoteGordoModel.State.POPPING_2;
						g.OnAnimationEvent_Popped(); // spawns the notes, unlocks instruments
					}
					if (m.state != EchoNoteGordoModel.State.POPPED) UnlockEcho(g, m); // region asleep here: no notes
					HideEcho(g);
					Session.Broadcast(EchoPacket(id), R);
					return;
				}
				var animating = m.state == EchoNoteGordoModel.State.POPPING_2;
				UnlockEcho(g, m);
				// Mid-animation, its StateExit hides the gordo; make sure it ends up hidden either way.
				if (animating) echoHide.Add((g, Time.time + 3f));
				else HideEcho(g);
			}
		});

		// GadgetState: kind byte, id string, state int, then per kind:
		//   Slot   puzzle slot id, 1 = filled                       (symmetric)
		//   Switch master switch id, SwitchHandler.State            (symmetric)
		//   Oasis  oasis id, 1 = live                               (symmetric)
		//   Column fire column position key, 1 = fire activated     (symmetric)
		//   Storm  "", FirestormActivator.Mode, endFirestormTime double, endFirecolumnsTime double, nextFirestormTime double (h→c)
		//   Snare  gadget site id, bait Identifiable.Id, gordo Identifiable.Id int (bait symmetric; snared gordo h→c)
		Session.On(Op.GadgetState, (from, r) =>
		{
			if (Session.IsHost) Session.Relay(from, r, R);
			var kind = r.GetByte();
			var id = r.GetString();
			var state = r.GetInt();
			if (!Session.InWorld) return;
			using (Session.Apply())
				switch (kind)
				{
					case Slot: ApplySlot(id); break;
					case Switch: ApplySwitch(id, (SwitchHandler.State)state); break;
					case Oasis: ApplyOasis(id); break;
					case Column: FindColumn(id)?.ActivateFire(); break;
					case Storm: ApplyStorm((FirestormActivator.Mode)state, r.GetDouble(), r.GetDouble(), r.GetDouble()); break;
					case Snare: ApplySnare(id, (Identifiable.Id)state, (Identifiable.Id)r.GetInt()); break;
				}
		});

		Session.PlayerReady += p =>
		{
			if (Session.InWorld) Session.SendTo(p, StormPacket(), R);
		};
	}

	public static void Tick()
	{
		if (echoHide.Count > 0)
			for (int i = echoHide.Count - 1; i >= 0; i--)
			{
				if (echoHide[i].g != null && Time.time < echoHide[i].at) continue;
				if (echoHide[i].g != null) HideEcho(echoHide[i].g);
				echoHide.RemoveAt(i);
			}

		if (!Session.IsHost || !Session.InWorld) return;
		var wm = Game.GetWorldModel();
		var cur = ((int)wm.currFirestormMode, wm.endFirestormTime, wm.nextFirestormTime);
		if (cur == lastStorm) return;
		lastStorm = cur;
		Session.Broadcast(StormPacket(), R);
	}

	// ---------- packets ----------

	static NetDataWriter GordoPacket(string key, int v) => Packets.Begin(Op.GordoState).Also(w => { w.Put(key); w.Put(v); });

	static NetDataWriter EchoPacket(string id) => Packets.Begin(Op.EchoState).Also(w => { w.Put(id); w.Put((int)EchoNoteGordoModel.State.POPPED); });

	static NetDataWriter StatePacket(byte kind, string id, int state) =>
		Packets.Begin(Op.GadgetState).Also(w => { w.Put(kind); w.Put(id); w.Put(state); });

	static NetDataWriter StormPacket()
	{
		var wm = Game.GetWorldModel();
		var w = StatePacket(Storm, "", (int)wm.currFirestormMode);
		w.Put(wm.endFirestormTime);
		w.Put(wm.endFirecolumnsTime);
		w.Put(wm.nextFirestormTime);
		return w;
	}

	static NetDataWriter SnarePacket(GordoSnare s)
	{
		var m = snareModel(s);
		var w = StatePacket(Snare, m.siteId, (int)m.baitTypeId);
		w.Put((int)m.gordoTypeId);
		return w;
	}

	// ---------- gordos ----------

	static string Key(GordoEat g)
	{
		var snare = gordoSnare(g);
		if (snare != null) return "site:" + snare.siteId;
		var id = g.id;
		return string.IsNullOrEmpty(id) ? null : id;
	}

	static GordoEat Gordo(string key)
	{
		if (key.StartsWith("site:"))
			return Game.AllGadgetSites().TryGetValue(key.Substring(5), out var site) && site.attached?.transform
				? site.attached.transform.GetComponentInChildren<GordoEat>(true) : null;
		var m = Game.GetGordoModel(key);
		return m == null ? null : gordoObj(m)?.GetComponent<GordoEat>();
	}

	// Strain + burst; GiveRewards is host-only (ActorSync), so clients just play it.
	static void Burst(GordoEat g)
	{
		if (g.isActiveAndEnabled) g.StartCoroutine((IEnumerator)reachedTarget.Invoke(g, null));
		else immediateReached.Invoke(g, null);
	}

	// ---------- pods / echoes ----------

	// Host: the pod's region is asleep, so Activate's coroutines can't run. Queue the prizes; its
	// OnEnable spawns the queue once the region wakes.
	static void OpenDormant(TreasurePod pod)
	{
		var m = podModel(pod);
		pod.CurrState = TreasurePod.State.OPEN;
		if (pod.blueprint != Gadget.Id.NONE) SRSingleton<SceneContext>.Instance.GadgetDirector.AddBlueprint(pod.blueprint);
		if (pod.unlockedSlimeAppearance != null)
			SRSingleton<SceneContext>.Instance.SlimeAppearanceDirector.UnlockAppearance(pod.unlockedSlimeAppearanceDefinition, pod.unlockedSlimeAppearance);
		if (pod.spawnObjs != null)
			foreach (var o in pod.spawnObjs) m.spawnQueue.Enqueue(Identifiable.GetId(o));
	}

	static void UnlockEcho(EchoNoteGordo g, EchoNoteGordoModel m)
	{
		m.state = EchoNoteGordoModel.State.POPPED;
		var instruments = SRSingleton<SceneContext>.Instance.InstrumentDirector;
		instruments.UnlockNextInstrument();
		foreach (var i in g.bonusInstruments) instruments.UnlockInstrument(i);
		echoTeleporter(g).waitForExternalActivation = false;
		g.ring.SetActive(true);
	}

	static void HideEcho(EchoNoteGordo g)
	{
		echoActiveCue(g).Stop(false);
		echoTeleporter(g).waitForExternalActivation = false;
		g.gordo.SetActive(false);
		g.ring.SetActive(true);
	}

	static IEnumerator Nothing()
	{
		yield break;
	}

	// ---------- gadget states ----------

	static void ApplySlot(string id)
	{
		if (!Game.AllSlots().TryGetValue(id, out var m) || m.filled) return;
		m.filled = true;
		var slot = slotObj(m)?.GetComponent<PuzzleSlot>();
		if (slot == null) return;
		slot.OnFilledChangedFromModel();
		if (!slot.isActiveAndEnabled) return;
		if (slot.changeFX != null) SRBehaviour.SpawnAndPlayFX(slot.changeFX, slot.transform.position, slot.transform.rotation);
		if (slot.localFillCue != null) SECTR_AudioSystem.Play(slot.localFillCue, slot.transform.position, loop: false);
	}

	static void ApplySwitch(string id, SwitchHandler.State state)
	{
		if (!Game.AllSwitches().TryGetValue(id, out var m) || m.state == state) return;
		var sw = switchObj(m)?.GetComponent<WorldStateMasterSwitch>();
		if (sw == null) m.state = state;
		else setStateForAll.Invoke(sw, new object[] { state, !sw.isActiveAndEnabled });
	}

	static void ApplyOasis(string id)
	{
		if (!Game.AllOases().TryGetValue(id, out var m) || m.isLive) return;
		var oasis = oasisObj(m)?.GetComponent<global::Oasis>();
		if (oasis == null) return;
		// The water trigger does the full activation (indicator, cue, FX) and calls SetLive.
		var trigger = Object.FindObjectsOfType<OasisWaterTrigger>().FirstOrDefault(t => t.oasisToScale == oasis);
		if (trigger != null) trigger.AddLiquid(Identifiable.Id.MAGIC_WATER_LIQUID, 1f);
		if (!oasis.IsLive()) oasis.SetLive(false);
	}

	static string ColumnKey(FireColumn c)
	{
		var p = c.transform.position;
		return $"{Mathf.RoundToInt(p.x * 10)},{Mathf.RoundToInt(p.y * 10)},{Mathf.RoundToInt(p.z * 10)}";
	}

	// Fire columns have no persisted id; they're static scene objects, so position is stable.
	static FireColumn FindColumn(string key)
	{
		if (columns.TryGetValue(key, out var c) && c != null) return c;
		columns.Clear();
		foreach (var col in Resources.FindObjectsOfTypeAll<FireColumn>())
			if (col.gameObject.scene.IsValid()) columns[ColumnKey(col)] = col;
		return columns.TryGetValue(key, out c) ? c : null;
	}

	// Client: the host owns the storm schedule; nearby columns still trigger locally (and sync).
	static void ApplyStorm(FirestormActivator.Mode mode, double end, double endColumns, double next)
	{
		if (Session.IsHost) return;
		var wm = Game.GetWorldModel();
		var time = SRSingleton<SceneContext>.Instance.TimeDirector;
		var prev = wm.currFirestormMode;
		wm.currFirestormMode = mode;
		wm.endFirestormTime = end;
		wm.endFirecolumnsTime = endColumns;
		wm.nextFirestormTime = next;
		if (mode == FirestormActivator.Mode.IDLE) wm.nextFirecolumnTime = double.PositiveInfinity;
		else if (mode == FirestormActivator.Mode.PREPARING && prev != mode) wm.nextFirecolumnTime = time.HoursFromNow(0.5f);
		else if (double.IsPositiveInfinity(wm.nextFirecolumnTime)) wm.nextFirecolumnTime = time.WorldTime();
	}

	static void ApplySnare(string siteId, Identifiable.Id bait, Identifiable.Id gordo)
	{
		if (!Game.AllGadgetSites().TryGetValue(siteId, out var site) || !site.attached?.transform) return;
		var s = site.attached.transform.GetComponentInChildren<GordoSnare>(true);
		if (s == null || s.HasSnaredGordo()) return;
		if (gordo != Identifiable.Id.NONE)
		{
			if (!s.IsBaited()) attachBait.Invoke(s, new object[] { bait != Identifiable.Id.NONE ? bait : Identifiable.Id.CARROT_VEGGIE });
			snareGordo.Invoke(s, new object[] { gordo });
		}
		else if (bait != Identifiable.Id.NONE && !s.IsBaited()) attachBait.Invoke(s, new object[] { bait });
	}

	// ---------- patches ----------

	// Only the food's owner feeds it (both machines see the collision); the count then syncs.
	[HarmonyPatch(typeof(GordoEat), nameof(GordoEat.MaybeEat))]
	static class GordoFeed
	{
		static bool Prefix(GordoEat __instance, Collider col, out int __state)
		{
			__state = int.MinValue;
			if (ActorSync.IsRemoteOwned(col.gameObject)) return false;
			__state = __instance.GetEatenCount();
			return true;
		}

		static void Postfix(GordoEat __instance, bool __result, int __state)
		{
			if (!__result || __state == int.MinValue || !Live) return;
			var key = Key(__instance);
			if (key == null) return;
			var now = __instance.GetEatenCount();
			if (Session.IsHost) Session.Broadcast(GordoPacket(key, now), R);
			else Session.SendToHost(GordoPacket(key, now - __state), R);
		}
	}

	[HarmonyPatch(typeof(TreasurePod), nameof(TreasurePod.Activate))]
	static class PodOpen
	{
		static void Prefix(TreasurePod __instance, out bool __state) => __state = __instance.CurrState == TreasurePod.State.LOCKED;

		static void Postfix(TreasurePod __instance, bool __state)
		{
			if (!__state || __instance.CurrState != TreasurePod.State.OPEN || !Live) return;
			Session.Distribute(Packets.Begin(Op.PodState).Also(w => w.Put(__instance.id)), R);
		}
	}

	[HarmonyPatch(typeof(TreasurePod), nameof(TreasurePod.HasKey))]
	static class PodForceKey
	{
		static bool Prefix(ref bool __result)
		{
			if (!forceKey) return true;
			__result = true;
			return false;
		}
	}

	// Prizes spawn on the host only; the client's queue is dropped (host spawns arrive as actors).
	[HarmonyPatch(typeof(TreasurePod), "SpawnQueuedPrizeObjs")]
	static class PodPrizesHostOnly
	{
		static bool Prefix(TreasurePod __instance, ref IEnumerator __result)
		{
			if (!Session.IsClient) return true;
			podModel(__instance)?.spawnQueue.Clear();
			__result = Nothing();
			return false;
		}
	}

	// ActorSync blocks the method itself on clients; postfixes still run.
	[HarmonyPatch(typeof(EchoNoteGordo), nameof(EchoNoteGordo.OnAnimationEvent_Popped))]
	static class EchoPopped
	{
		static void Postfix(EchoNoteGordo __instance)
		{
			if (!Live) return;
			var m = echoModel(__instance);
			if (m == null) return;
			var id = __instance.id;
			if (Session.IsHost && m.state == EchoNoteGordoModel.State.POPPED)
				Session.Broadcast(EchoPacket(id), R);
			else if (Session.IsClient && m.state == EchoNoteGordoModel.State.POPPING_2 && echoRequested.Add(id))
				Session.SendToHost(EchoPacket(id), R);
		}
	}

	[HarmonyPatch(typeof(PuzzleSlot), nameof(PuzzleSlot.OnTriggerEnter))]
	static class SlotFill
	{
		static bool Prefix(PuzzleSlot __instance, Collider collider, out bool __state)
		{
			__state = false;
			if (ActorSync.IsRemoteOwned(collider.gameObject)) return false;
			__state = __instance.IsLocked();
			return true;
		}

		static void Postfix(PuzzleSlot __instance, bool __state)
		{
			if (!__state || __instance.IsLocked() || !Live) return;
			Session.Distribute(StatePacket(Slot, __instance.id, 1), R);
		}
	}

	[HarmonyPatch(typeof(WorldStateMasterSwitch), nameof(WorldStateMasterSwitch.Activate))]
	static class SwitchFlip
	{
		static void Prefix(WorldStateMasterSwitch __instance, out SwitchHandler.State __state) => __state = switchModel(__instance).state;

		static void Postfix(WorldStateMasterSwitch __instance, SwitchHandler.State __state)
		{
			var now = switchModel(__instance).state;
			if (now == __state || !Live) return;
			Session.Distribute(StatePacket(Switch, __instance.id, (int)now), R);
		}
	}

	[HarmonyPatch(typeof(global::Oasis), nameof(global::Oasis.SetLive))]
	static class OasisLive
	{
		static void Prefix(global::Oasis __instance, out bool __state) => __state = !__instance.IsLive();

		static void Postfix(global::Oasis __instance, bool __state)
		{
			if (!__state || !__instance.IsLive() || !Live) return;
			Session.Distribute(StatePacket(Oasis, __instance.id, 1), R);
		}
	}

	// The oasis's slime/animal burst is world sim: host only.
	[HarmonyPatch(typeof(global::Oasis), "TriggerAllSpawners")]
	static class OasisSpawnersHostOnly
	{
		static bool Prefix() => !Session.IsClient;
	}

	[HarmonyPatch(typeof(FireColumn), nameof(FireColumn.ActivateFire))]
	static class ColumnFire
	{
		static void Prefix(FireColumn __instance, out bool __state) => __state = !__instance.IsFireActive();

		static void Postfix(FireColumn __instance, bool __state)
		{
			if (!__state || !__instance.IsFireActive() || !Live) return;
			Session.Distribute(StatePacket(Column, ColumnKey(__instance), 1), R);
		}
	}

	// Storm start/stop schedule is the host's (random); clients get it via GadgetState Storm.
	[HarmonyPatch]
	static class StormHostOnly
	{
		static IEnumerable<MethodBase> TargetMethods() => new[] { "MaybeStartFirestorm", "MaybeShutdownFirestorm", "CheckProgress" }
			.Select(n => AccessTools.Method(typeof(FirestormActivator), n));

		static bool Prefix() => !Session.IsClient;
	}

	// Snaring is world sim (GadgetDirector.Update at 5am): host only; result goes out as GadgetState Snare.
	[HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.CheckGordoSnares))]
	static class SnareCheckHostOnly
	{
		static bool Prefix(out List<GordoSnare> __state)
		{
			__state = Session.IsHost ? GordoSnare.AllGordoSnares.Where(s => s && !s.HasSnaredGordo()).ToList() : null;
			return !Session.InRemoteWorld;
		}

		static void Postfix(List<GordoSnare> __state)
		{
			if (__state == null || !Live) return;
			foreach (var s in __state)
				if (s && s.HasSnaredGordo()) Session.Broadcast(SnarePacket(s), R);
		}
	}

	// Baiting: the food's owner attaches it, everyone else mirrors.
	[HarmonyPatch(typeof(GordoSnare), nameof(GordoSnare.OnTriggerEnter))]
	static class SnareBait
	{
		static bool Prefix(GordoSnare __instance, Collider col, out bool __state)
		{
			__state = false;
			if (ActorSync.IsRemoteOwned(col.gameObject)) return false;
			__state = !__instance.IsBaited();
			return true;
		}

		static void Postfix(GordoSnare __instance, bool __state)
		{
			if (!__state || !__instance.IsBaited() || !Live) return;
			Session.Distribute(SnarePacket(__instance), R);
		}
	}

	// Slime gate opened elsewhere (Door replay): play its deactivate sequence here too.
	[HarmonyPatch(typeof(AccessDoor), nameof(AccessDoor.CurrState), MethodType.Setter)]
	static class GateSequence
	{
		static void Prefix(AccessDoor __instance, out AccessDoor.State __state) => __state = __instance.CurrState;

		static void Postfix(AccessDoor __instance, AccessDoor.State value, AccessDoor.State __state)
		{
			if (!Session.Applying || value != AccessDoor.State.OPEN || __state == AccessDoor.State.OPEN) return;
			foreach (var gate in Object.FindObjectsOfType<PuzzleGateActivator>())
				if (gate.gateDoor == __instance && gate.isActiveAndEnabled)
					gate.StartCoroutine((IEnumerator)gateSequence.Invoke(gate, null));
		}
	}
}
