using System;
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
using UnityEngine.SceneManagement;
using SetId = MonomiPark.SlimeRancher.Regions.RegionRegistry.RegionSetId;

namespace TailRanch.Sync;

// Host simulates; clients view. Owner table: actorId -> player simulating it (missing = host).
// Client ids are (LocalId << 48) | n so they never collide with host ids.
public static class ActorSync
{
	const float MotionInterval = 0.1f, ReleaseAfter = 3f;
	const int PerPacket = 22; // 3 + 22 * 48 bytes
	const long Block = 1L << 48;
	const DeliveryMethod RO = DeliveryMethod.ReliableOrdered;

	static readonly Dictionary<long, GameObject> objs = new();
	static readonly Dictionary<GameObject, long> idOf = new();
	static readonly Dictionary<long, SetId> regionOf = new();
	static readonly Dictionary<long, byte> owner = new();
	static readonly HashSet<long> pendingSpawns = new();
	static readonly HashSet<long> pendingDespawns = new();
	static readonly Dictionary<long, (Vector3 pos, Quaternion rot)> lastSent = new();
	static readonly Dictionary<long, float> restSince = new();
	static readonly List<GameObject> loadStrays = new();
	static readonly HashSet<Identifiable.Id> logged = new();
	static readonly List<long> batch = new(), dead = new(), release = new();
	static long localNext;
	static float nextMotion;
	static bool live, entered;
	static int scope; // >0 inside a player action: client spawns/despawns are legit and broadcast

	delegate GameObject InstFn(GameModel m, long id, GameObject prefab, SetId set, Vector3 pos, Quaternion rot, bool nonActorOk, bool skipNotify);
	delegate void RegFn(GameModel m, long id, GameObject go, SetId set, bool nonActorOk, bool skipNotify);
	static readonly InstFn instantiate = AccessTools.MethodDelegate<InstFn>(AccessTools.Method(typeof(GameModel), "InstantiateActor",
		new[] { typeof(long), typeof(GameObject), typeof(SetId), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(bool) }));
	static readonly RegFn register = AccessTools.MethodDelegate<RegFn>(AccessTools.Method(typeof(GameModel), "RegisterActor"));
	static readonly AccessTools.FieldRef<GameModel, long> nextActorId = AccessTools.FieldRefAccess<GameModel, long>("nextActorId");
	static readonly AccessTools.FieldRef<GameModel, Dictionary<long, ActorModel>> actors = AccessTools.FieldRefAccess<GameModel, Dictionary<long, ActorModel>>("actors");

	// True when another machine simulates this actor (local copy is a puppet).
	// Consumers (eat, market, silo, incinerator, collectors) must ignore such actors.
	public static bool IsRemoteOwned(long actorId) => Session.Active && !Owns(actorId);
	public static bool IsRemoteOwned(GameObject go) => go && idOf.TryGetValue(go, out var id) && IsRemoteOwned(id);

	static bool Owns(long id) => Session.IsClient ? owner.TryGetValue(id, out var o) && o == Session.LocalId : !owner.ContainsKey(id);

	public static void Register()
	{
		SceneManager.sceneLoaded += (_, _) => Purge();
		Session.PlayerReady += OnReady;

		Session.On(Op.ActorSpawn, (from, r) =>
		{
			var id = r.GetLong();
			var ident = (Identifiable.Id)r.GetInt();
			var set = (SetId)r.GetInt();
			var pos = r.GetVector3();
			var rot = r.GetQuaternion();
			var vel = r.GetVector3();
			if (!Accept(from, r, id)) return;
			if (objs.TryGetValue(id, out var have) && have) return;
			var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(ident);
			if (!prefab) return;
			GameObject go;
			using (Session.Apply()) go = instantiate(SRSingleton<SceneContext>.Instance.GameModel, id, prefab, set, pos, rot, false, false);
			if (!go) return;
			var rb = go.GetComponent<Rigidbody>();
			if (rb && !rb.isKinematic) rb.velocity = vel;
			if (Session.IsHost) owner[id] = from.Id;
			Puppetize(id, go);
		});

		Session.On(Op.ActorDespawn, (from, r) =>
		{
			var id = r.GetLong();
			if (!Accept(from, r, -1)) return;
			if (!objs.TryGetValue(id, out var go)) return;
			if (go) using (Session.Apply()) Destroyer.DestroyActor(go, "TailRanch.Despawn");
			else Forget(id, null);
		});

		Session.On(Op.ActorMotion, (from, r) =>
		{
			if (Session.IsHost) Session.Relay(from, r, DeliveryMethod.Sequenced);
			else if (!Enter()) return;
			int n = r.GetUShort();
			for (var i = 0; i < n; i++)
			{
				var id = r.GetLong();
				var pos = r.GetVector3();
				var rot = r.GetQuaternion();
				var vel = r.GetVector3();
				if (Session.IsHost ? !(owner.TryGetValue(id, out var o) && o == from.Id) : Owns(id)) continue;
				if (!objs.TryGetValue(id, out var go) || !go) continue;
				var p = go.GetComponent<ActorPuppet>() ?? ActorPuppet.Set(go, true, true);
				if (p) p.Push(pos, rot, vel);
			}
		});

		Session.On(Op.ActorClaim, (from, r) =>
		{
			var id = r.GetLong();
			var o = r.GetByte();
			if (Session.IsHost)
			{
				if (!objs.TryGetValue(id, out var go) || !go)
				{
					Session.SendTo(from, Claim(id, Session.HostId), RO);
					return;
				}
				var cur = owner.TryGetValue(id, out var c) ? c : Session.HostId;
				var vac = go.GetComponent<Vacuumable>();
				if (cur != Session.HostId ? cur != from.Id : vac && (vac.isCaptive() || vac.isHeld()))
				{
					Session.SendTo(from, Claim(id, cur), RO);
					return;
				}
				owner[id] = from.Id;
				lastSent.Remove(id);
				ActorPuppet.Set(go, true, true);
				Session.Broadcast(Claim(id, from.Id), RO, from);
				return;
			}
			if (!Enter()) return;
			if (o == Session.HostId) owner.Remove(id);
			else owner[id] = o;
			restSince.Remove(id);
			if (objs.TryGetValue(id, out var g) && g) ActorPuppet.Set(g, o != Session.LocalId, true);
		});

		Session.On(Op.ActorRelease, (from, r) =>
		{
			var id = r.GetLong();
			if (Session.IsHost)
			{
				if (!owner.TryGetValue(id, out var c) || c != from.Id) return;
				owner.Remove(id);
				lastSent.Remove(id);
				if (objs.TryGetValue(id, out var go) && go) ActorPuppet.Set(go, false, false);
				Session.Relay(from, r, RO);
				return;
			}
			if (!Enter()) return;
			owner.Remove(id);
			restSince.Remove(id);
			if (objs.TryGetValue(id, out var g) && g) ActorPuppet.Set(g, true, true);
		});
	}

	// Host: validate + relay client spawn/despawn. Client: drop until the remote world is up.
	static bool Accept(Player from, NetPacketReader r, long id)
	{
		if (Session.IsClient) return Enter();
		if (id >= 0 && id / Block != from.Id) return false;
		Session.Relay(from, r, RO);
		return true;
	}

	static void Puppetize(long id, GameObject go)
	{
		if (Session.IsClient) ActorPuppet.Set(go, !Owns(id), true);
		else if (!Owns(id)) ActorPuppet.Set(go, true, true);
	}

	static NetDataWriter Claim(long id, byte o) => Packets.Begin(Op.ActorClaim).Also(w => { w.Put(id); w.Put(o); });
	static NetDataWriter Release(long id) => Packets.Begin(Op.ActorRelease).Also(w => w.Put(id));

	// Client: first moment in the remote world. Strays spawned during load go; everything else becomes a puppet.
	static bool Enter()
	{
		if (entered) return true;
		if (!Session.IsClient || !Session.InRemoteWorld || !Session.InWorld) return false;
		entered = live = true;
		using (Session.Apply())
			foreach (var go in loadStrays)
				if (go) Destroyer.DestroyActor(go, "TailRanch.Stray");
		loadStrays.Clear();
		foreach (var kv in objs)
			if (kv.Value) ActorPuppet.Set(kv.Value, !Owns(kv.Key), true);
		return true;
	}

	// Host: Session already flushed buffered reliable ops; add what the world snapshot lacks.
	static void OnReady(Player p)
	{
		foreach (var kv in owner) Session.SendTo(p, Claim(kv.Key, kv.Value), RO);
		batch.Clear();
		foreach (var kv in objs)
			if (kv.Value) batch.Add(kv.Key);
		SendMotion(batch, w => Session.SendTo(p, w, RO));
	}

	// Host: player left; take back what they simulated.
	public static void PlayerLeft(byte id)
	{
		if (!Session.IsHost)
		{
			foreach (var k in owner.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) owner.Remove(k);
			return;
		}
		foreach (var k in owner.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
		{
			owner.Remove(k);
			lastSent.Remove(k);
			if (objs.TryGetValue(k, out var go) && go) ActorPuppet.Set(go, false, false);
			Session.Broadcast(Release(k), RO);
		}
	}

	public static void Tick()
	{
		if (!Session.Active)
		{
			if (live) Reset();
			return;
		}
		live = true;
		if (!Session.InWorld) return;
		if (Session.IsClient && !Enter()) return;
		Flush();
		if (Time.unscaledTime < nextMotion) return;
		nextMotion = Time.unscaledTime + MotionInterval;
		Motion();
	}

	static void Flush()
	{
		foreach (var id in pendingSpawns)
		{
			if (!objs.TryGetValue(id, out var go) || !go) continue;
			var t = go.transform;
			var rb = go.GetComponent<Rigidbody>();
			var w = Packets.Begin(Op.ActorSpawn);
			w.Put(id);
			w.Put((int)Identifiable.GetId(go));
			w.Put((int)(regionOf.TryGetValue(id, out var s) ? s : SetId.HOME));
			w.Put(t.position);
			w.Put(t.rotation);
			w.Put(rb ? rb.velocity : Vector3.zero);
			Session.Distribute(w, RO);
		}
		pendingSpawns.Clear();
		foreach (var id in pendingDespawns)
			Session.Distribute(Packets.Begin(Op.ActorDespawn).Also(w => w.Put(id)), RO);
		pendingDespawns.Clear();
	}

	static void Motion()
	{
		batch.Clear();
		dead.Clear();
		release.Clear();
		var now = Time.unscaledTime;
		var anyone = Session.IsClient || Session.Players.Count > 0;
		foreach (var kv in objs)
		{
			var id = kv.Key;
			var go = kv.Value;
			if (!go)
			{
				dead.Add(id);
				continue;
			}
			if (!Owns(id)) continue;
			var rb = go.GetComponent<Rigidbody>();
			if (Session.IsClient)
			{
				var vac = go.GetComponent<Vacuumable>();
				var rest = (!go.activeInHierarchy || !rb || rb.isKinematic || rb.velocity.sqrMagnitude < 0.01f)
					&& !(vac && (vac.isCaptive() || vac.isHeld()));
				if (!rest) restSince.Remove(id);
				else if (!restSince.TryGetValue(id, out var t0)) restSince[id] = now;
				else if (now - t0 > ReleaseAfter) release.Add(id);
			}
			if (!anyone || !rb || !go.activeInHierarchy) continue;
			var t = go.transform;
			if (lastSent.TryGetValue(id, out var last)
				&& (t.position - last.pos).sqrMagnitude < 0.0025f
				&& Quaternion.Angle(t.rotation, last.rot) < 4f) continue;
			lastSent[id] = (t.position, t.rotation);
			batch.Add(id);
		}
		SendMotion(batch, w => Session.Distribute(w, DeliveryMethod.Sequenced));

		foreach (var id in dead)
		{
			if (Owns(id) && !pendingSpawns.Remove(id)) pendingDespawns.Add(id);
			Forget(id, null);
		}

		foreach (var id in release)
		{
			batch.Clear();
			batch.Add(id);
			SendMotion(batch, w => Session.SendToHost(w, RO));
			Session.SendToHost(Release(id), RO);
			owner.Remove(id);
			restSince.Remove(id);
			ActorPuppet.Set(objs[id], true, true);
		}
	}

	static void SendMotion(List<long> ids, Action<NetDataWriter> send)
	{
		for (var i = 0; i < ids.Count; i += PerPacket)
		{
			var n = Math.Min(PerPacket, ids.Count - i);
			var w = Packets.Begin(Op.ActorMotion);
			w.Put((ushort)n);
			for (var j = i; j < i + n; j++)
			{
				var go = objs[ids[j]];
				var rb = go.GetComponent<Rigidbody>();
				w.Put(ids[j]);
				w.Put(go.transform.position);
				w.Put(go.transform.rotation);
				w.Put(rb ? rb.velocity : Vector3.zero);
			}
			send(w);
		}
	}

	static void Forget(long id, GameObject go)
	{
		if ((object)go == null) objs.TryGetValue(id, out go);
		if ((object)go != null) idOf.Remove(go);
		objs.Remove(id);
		regionOf.Remove(id);
		owner.Remove(id);
		lastSent.Remove(id);
		restSince.Remove(id);
	}

	static void Purge()
	{
		foreach (var id in objs.Where(kv => !kv.Value).Select(kv => kv.Key).ToList()) Forget(id, null);
		loadStrays.RemoveAll(go => !go);
	}

	static void Reset()
	{
		live = entered = false;
		foreach (var go in objs.Values)
			if (go && go.GetComponent<ActorPuppet>()) ActorPuppet.Set(go, false, false);
		owner.Clear();
		pendingSpawns.Clear();
		pendingDespawns.Clear();
		lastSent.Clear();
		restSince.Clear();
		loadStrays.Clear();
		localNext = 0;
		scope = 0;
	}

	static void LogOnce(Identifiable.Id ident, string what)
	{
		if (logged.Add(ident)) Plugin.Log.LogInfo($"ActorSync: {what} {ident}");
	}

	static long NextLocalId(GameModel gm)
	{
		var lo = Session.LocalId * Block;
		if (localNext < lo || localNext >= lo + Block)
		{
			localNext = lo;
			foreach (var id in actors(gm).Keys)
				if (id >= localNext && id < lo + Block) localNext = id + 1;
		}
		return localNext++;
	}

	// Host: loading a save holding client ids bumps nextActorId past them; pull it back below the first client block.
	static void FixHostNext(GameModel gm)
	{
		ref var next = ref nextActorId(gm);
		if (next < Block) return;
		next = 100;
		foreach (var id in actors(gm).Keys)
			if (id < Block && id >= next) next = id + 1;
	}

	static void Track(long id, GameObject go, SetId set, bool skipNotify)
	{
		var ident = Identifiable.GetId(go);
		if (ident == Identifiable.Id.NONE || ident == Identifiable.Id.QUICKSILVER_SLIME || Identifiable.SCENE_OBJECTS.Contains(ident)) return;
		objs[id] = go;
		idOf[go] = id;
		regionOf[id] = set;
		if (!Session.Active || Session.Applying || skipNotify) return;
		if (Session.IsClient)
		{
			if (!entered)
			{
				loadStrays.Add(go);
				return;
			}
			if (scope == 0)
			{
				LogOnce(ident, "dropped unscoped client spawn");
				using (Session.Apply()) Destroyer.DestroyActor(go, "TailRanch.Unscoped");
				return;
			}
			owner[id] = Session.LocalId;
			ActorPuppet.Set(go, false, true);
		}
		pendingSpawns.Add(id);
	}

	// Client: non-owned actors only die when the host says so (or inside a player action).
	static bool Blocked(GameObject go)
	{
		if (!Session.IsClient || !entered || Session.Applying || scope > 0) return false;
		if (!go || !idOf.TryGetValue(go, out var id) || Owns(id)) return false;
		LogOnce(Identifiable.GetId(go), "blocked client destroy of");
		return true;
	}

	// Client: claim on vac contact. Host: vac on a client-owned actor takes it back.
	static void Grab(GameObject go)
	{
		if (!Session.Active || Session.Applying || !go || !idOf.TryGetValue(go, out var id) || Owns(id)) return;
		if (Session.IsClient)
		{
			if (!entered) return;
			owner[id] = Session.LocalId;
			restSince.Remove(id);
			lastSent.Remove(id);
			ActorPuppet.Set(go, false, true);
			Session.SendToHost(Claim(id, Session.LocalId), RO);
			return;
		}
		owner.Remove(id);
		lastSent.Remove(id);
		ActorPuppet.Set(go, false, false);
		Session.Broadcast(Release(id), RO);
	}

	static IEnumerable<MethodBase> Named(Type t, string name) => AccessTools.GetDeclaredMethods(t).Where(m => m.Name == name);

	static IEnumerator Scoped(IEnumerator inner)
	{
		while (true)
		{
			bool more;
			scope++;
			try { more = inner.MoveNext(); }
			finally { scope--; }
			if (!more) yield break;
			yield return inner.Current;
		}
	}

	// ---------- patches ----------

	[HarmonyPatch(typeof(GameModel), nameof(GameModel.InstantiateActor),
		new[] { typeof(GameObject), typeof(SetId), typeof(Vector3), typeof(Quaternion), typeof(bool) })]
	static class NewActorId
	{
		static bool Prefix(GameModel __instance, GameObject prefab, SetId regionSetId, Vector3 pos, Quaternion rot, bool nonActorOk, ref GameObject __result)
		{
			if (!Session.IsClient)
			{
				FixHostNext(__instance);
				return true;
			}
			__result = instantiate(__instance, NextLocalId(__instance), prefab, regionSetId, pos, rot, nonActorOk, false);
			return false;
		}
	}

	[HarmonyPatch(typeof(GameModel), nameof(GameModel.RegisterStartingActor))]
	static class StartingActorId
	{
		static bool Prefix(GameModel __instance, GameObject actorObj, SetId regionSetId)
		{
			if (!Session.IsClient)
			{
				FixHostNext(__instance);
				return true;
			}
			register(__instance, NextLocalId(__instance), actorObj, regionSetId, false, false);
			return false;
		}
	}

	[HarmonyPatch(typeof(GameModel), "RegisterActor")]
	static class OnRegister
	{
		static void Postfix(long actorId, GameObject gameObj, SetId regionSetId, bool skipNotify) => Track(actorId, gameObj, regionSetId, skipNotify);
	}

	[HarmonyPatch(typeof(GameModel), nameof(GameModel.DestroyActorModel))]
	static class OnDestroyModel
	{
		static bool Prefix(GameObject gameObj) => !Blocked(gameObj);
	}

	[HarmonyPatch(typeof(Destroyer), nameof(Destroyer.Destroy),
		new[] { typeof(UnityEngine.Object), typeof(float), typeof(string), typeof(bool), typeof(bool) })]
	static class OnDestroy
	{
		static bool Prefix(UnityEngine.Object instance)
		{
			if (instance is not GameObject go || !idOf.TryGetValue(go, out var id)) return true;
			if (Blocked(go)) return false;
			if (Session.Active && !Session.Applying && (!Session.IsClient || entered) && !pendingSpawns.Remove(id)) pendingDespawns.Add(id);
			Forget(id, go);
			return true;
		}
	}

	[HarmonyPatch(typeof(Vacuumable), nameof(Vacuumable.capture))]
	static class OnCapture
	{
		static void Prefix(Vacuumable __instance) => Grab(__instance.gameObject);
	}

	// Kinematic objects (puppets) get Pending instead of capture.
	[HarmonyPatch(typeof(Vacuumable), nameof(Vacuumable.Pending), MethodType.Setter)]
	static class OnPending
	{
		static void Prefix(Vacuumable __instance, bool value)
		{
			if (value) Grab(__instance.gameObject);
		}
	}

	// Player actions on the client: spawns/despawns inside are real and broadcast.
	[HarmonyPatch]
	static class PlayerAction
	{
		static IEnumerable<MethodBase> TargetMethods() => Named(typeof(WeaponVacuum), "Expel")
			.Concat(Named(typeof(WeaponVacuum), "ConsumeVacItem"))
			.Concat(Named(typeof(ToyShopUI), "InstantiateToy"))
			.Concat(Named(typeof(SiloCatcher), "OnTriggerStay"));

		static void Prefix() => scope++;
		static void Finalizer() => scope--;
	}

	[HarmonyPatch(typeof(TreasurePod), "SpawnQueuedPrizeObjs")]
	static class TreasurePrizes
	{
		static void Postfix(ref IEnumerator __result) => __result = Scoped(__result);
	}

	// Crates break on whichever machine simulates them.
	[HarmonyPatch]
	static class CrateBreak
	{
		static IEnumerable<MethodBase> TargetMethods() => Named(typeof(BreakOnImpactBase), "BreakOpen").Concat(Named(typeof(ExchangeBreakOnImpact), "BreakOpen"));

		static bool Prefix(MonoBehaviour __instance, out bool __state)
		{
			__state = false;
			if (IsRemoteOwned(__instance.gameObject)) return false;
			scope++;
			return __state = true;
		}

		static void Finalizer(bool __state)
		{
			if (__state) scope--;
		}
	}

	// Autonomous spawners: host-only. Their output arrives as ActorSpawn.
	[HarmonyPatch]
	static class HostOnlySpawners
	{
		static IEnumerable<MethodBase> TargetMethods() => new (Type, string)[]
		{
			(typeof(SpawnResource), "Spawn"), (typeof(SlimeEat), "Produce"), (typeof(SlimeEatWater), "Produce"),
			(typeof(GordoRewardsBase), "GiveRewards"), (typeof(EchoNoteGordo), "OnAnimationEvent_Popped"),
			(typeof(Extractor), "SpawnItem"), (typeof(GadgetChickenCloner), "OnTriggerEnter"), (typeof(HydroTurret), "Update"),
			(typeof(EchoNet), "SpawnAt"), (typeof(GingerPatchNode), "Grow"), (typeof(KookadobaPatchNode), "Grow"),
			(typeof(ExchangeEjector), "Eject"), (typeof(DirectRewardEjector), "Eject"), (typeof(FireColumn), "SpawnFireball"),
			(typeof(FashionPod), "Update"), (typeof(GoldSlimeProducePlorts), "ProduceAt"), (typeof(ReactToShock), "MaybeCreatePlorts"),
			(typeof(RescueAmmoOnDeath), "SpawnRescuedItems"), (typeof(TransformChanceOnReproduce), "MaybeTransform"),
			(typeof(GlitchTarrSterilizeOnWater), "WillDestroyAfterTime"), (typeof(LemonPhasor), "OnTriggerEnter"),
			(typeof(DirectedCrateSpawner), "Spawn"), (typeof(DirectedAuxItemSpawner), "Spawn"), (typeof(PollenCloudController), "Update"),
		}.SelectMany(x => Named(x.Item1, x.Item2));

		static bool Prefix() => !Session.IsClient;
	}

	[HarmonyPatch]
	static class HostOnlyCoroutines
	{
		static IEnumerable<MethodBase> TargetMethods() => new (Type, string)[]
		{
			(typeof(DirectedActorSpawner), "Spawn"), (typeof(DirectedAnimalSpawner), "Spawn"), (typeof(SlimeEat), "ProduceAfterDelay"),
			(typeof(SlimeEatWater), "ProduceAfterDelay"), (typeof(SlimeEatAsh), "ProduceAfterDelay"), (typeof(Nutcracker), "DoCrack"),
		}.SelectMany(x => Named(x.Item1, x.Item2));

		static bool Prefix(ref IEnumerator __result)
		{
			if (!Session.IsClient) return true;
			__result = Enumerable.Empty<object>().GetEnumerator();
			return false;
		}
	}
}
