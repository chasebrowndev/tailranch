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
	const float MotionInterval = 0.1f, ReleaseAfter = 3f, ClaimWait = 1.5f;
	const int PerPacket = 22; // 3 + 22 * 48 bytes
	const long Block = 1L << 48;
	const DeliveryMethod RO = DeliveryMethod.ReliableOrdered;
	const byte Unknown = 0xFF; // ActorDeny owner: host has no such actor
	const byte Upsert = 0x80;  // state flag: only update an existing actor, never create

	static readonly Dictionary<long, GameObject> objs = new();
	static readonly Dictionary<GameObject, long> idOf = new();
	static readonly Dictionary<long, SetId> regionOf = new();
	static readonly Dictionary<long, byte> owner = new();
	static readonly HashSet<long> pendingSpawns = new();
	static readonly HashSet<long> pendingDespawns = new();
	static readonly HashSet<long> pendingState = new();
	static readonly List<(long id, Vector3 pos, Quaternion rot, Vector3 vel)> relay = new();
	static readonly Dictionary<long, (Vector3 pos, Quaternion rot)> lastSent = new();
	static readonly Dictionary<long, float> restSince = new();
	// Client: claims sent, host's grant/deny not back yet. Vacced items wait on these before turning into ammo.
	static readonly HashSet<long> awaiting = new();
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
	delegate void InitStateFn(ResourceCycle rc, ResourceCycle.State state, double progressTime);
	static readonly InitStateFn setInitState = AccessTools.MethodDelegate<InitStateFn>(AccessTools.Method(typeof(ResourceCycle), "SetInitState"));
	static readonly AccessTools.FieldRef<ResourceCycle, ProduceModel> produce = AccessTools.FieldRefAccess<ResourceCycle, ProduceModel>("model");
	static readonly AccessTools.FieldRef<SlimeFeral, SlimeModel> feralModel = AccessTools.FieldRefAccess<SlimeFeral, SlimeModel>("model");
	static readonly FieldInfo slotted = AccessTools.Field(typeof(AttachFashions), "slotted");

	// True when another machine simulates this actor (local copy is a puppet).
	// Consumers (eat, market, silo, incinerator, collectors) must ignore such actors.
	public static bool IsRemoteOwned(long actorId) => Session.Active && !Owns(actorId);
	public static bool IsRemoteOwned(GameObject go) => go && idOf.TryGetValue(go, out var id) && IsRemoteOwned(id);
	public static long IdOf(GameObject go) => go && idOf.TryGetValue(go, out var id) ? id : 0;
	public static GameObject Find(long id) => objs.TryGetValue(id, out var go) && go ? go : null;

	static bool Owns(long id) => Session.IsClient ? owner.TryGetValue(id, out var o) && o == Session.LocalId : !owner.ContainsKey(id);

	// Client: run a player action outside the patched entry points (spawns/despawns broadcast).
	internal static void AsPlayer(Action a)
	{
		scope++;
		try { a(); }
		finally { scope--; }
	}

	public static void Register()
	{
		SceneManager.sceneLoaded += (_, _) => Purge();
		Session.PlayerReady += OnReady;

		// ActorSpawn: actorId long, ident int, regionSet int, pos, rot, vel, then a state block (WriteState).
		// Known id = upsert: only the state block applies (motion carries the transform). Host takes full state
		// from the owner, looks (fashions/appearance) from anyone, and re-sends its own view to the rest.
		Session.On(Op.ActorSpawn, (from, r) =>
		{
			var id = r.GetLong();
			var ident = (Identifiable.Id)r.GetInt();
			var set = (SetId)r.GetInt();
			var pos = r.GetVector3();
			var rot = r.GetQuaternion();
			var vel = r.GetVector3();
			var st = ReadState(r);
			if (Session.IsClient && !Enter()) return;
			if (objs.TryGetValue(id, out var have) && have)
			{
				var full = Session.IsClient || owner.TryGetValue(id, out var o) && o == from.Id;
				using (Session.Apply()) ApplyState(have, st, full);
				if (Session.IsHost) Session.Broadcast(SpawnPacket(id, have, true), RO, from);
				return;
			}
			if ((st.flags & Upsert) != 0) return;
			if (Session.IsHost)
			{
				if (id / Block != from.Id) return;
				Session.Relay(from, r, RO);
			}
			var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(ident);
			if (!prefab) return;
			GameObject go;
			using (Session.Apply()) go = instantiate(SRSingleton<SceneContext>.Instance.GameModel, id, prefab, set, pos, rot, false, false);
			if (!go) return;
			var rb = go.GetComponent<Rigidbody>();
			if (rb && !rb.isKinematic) rb.velocity = vel;
			if (Session.IsHost) owner[id] = from.Id;
			using (Session.Apply()) ApplyState(go, st, true);
			Puppetize(id, go);
		});

		// ActorDespawn: actorId long. Host: the sender must own it, or it's host-owned and not in the host's vac.
		// Refused: the sender gets it back (ActorSpawn, plus ActorClaim when another client owns it).
		Session.On(Op.ActorDespawn, (from, r) =>
		{
			var id = r.GetLong();
			if (Session.IsClient)
			{
				if (!Enter() || !objs.TryGetValue(id, out var g)) return;
				if (g) using (Session.Apply()) Destroyer.DestroyActor(g, "TailRanch.Despawn");
				else Forget(id, null);
				return;
			}
			if (!objs.TryGetValue(id, out var go)) return;
			if (!go)
			{
				Forget(id, null);
				Session.Relay(from, r, RO);
				return;
			}
			var cur = owner.TryGetValue(id, out var c) ? c : Session.HostId;
			if (cur == Session.HostId ? Captive(go) : cur != from.Id)
			{
				Session.SendTo(from, SpawnPacket(id, go, false), RO);
				if (cur != Session.HostId) Session.SendTo(from, Claim(id, cur), RO);
				return;
			}
			Session.Relay(from, r, RO);
			using (Session.Apply()) Destroyer.DestroyActor(go, "TailRanch.Despawn");
		});

		// ActorMotion: count ushort, then per actor id long, pos, rot, vel. Host forwards only entries the sender owns.
		Session.On(Op.ActorMotion, (from, r) =>
		{
			if (Session.IsClient && !Enter()) return;
			relay.Clear();
			int n = r.GetUShort();
			for (var i = 0; i < n; i++)
			{
				var id = r.GetLong();
				var pos = r.GetVector3();
				var rot = r.GetQuaternion();
				var vel = r.GetVector3();
				if (Session.IsHost ? !(owner.TryGetValue(id, out var o) && o == from.Id) : Owns(id)) continue;
				if (Session.IsHost) relay.Add((id, pos, rot, vel));
				if (!objs.TryGetValue(id, out var go) || !go) continue;
				var p = go.GetComponent<ActorPuppet>();
				if (!p) p = ActorPuppet.Set(go, true, true);
				if (p) p.Push(pos, rot, vel);
			}
			if (relay.Count == 0) return;
			var w = Packets.Begin(Op.ActorMotion);
			w.Put((ushort)relay.Count);
			foreach (var m in relay)
			{
				w.Put(m.id);
				w.Put(m.pos);
				w.Put(m.rot);
				w.Put(m.vel);
			}
			Session.Broadcast(w, DeliveryMethod.Sequenced, from);
		});

		// ActorClaim: actorId long, owner byte. c→h request; h→c announcement. Refusals answer with ActorDeny.
		Session.On(Op.ActorClaim, (from, r) =>
		{
			var id = r.GetLong();
			var o = r.GetByte();
			if (Session.IsHost)
			{
				if (!objs.TryGetValue(id, out var go) || !go)
				{
					Session.SendTo(from, Deny(id, Unknown), RO);
					return;
				}
				var cur = owner.TryGetValue(id, out var c) ? c : Session.HostId;
				if (cur != Session.HostId ? cur != from.Id : Captive(go))
				{
					Session.SendTo(from, Deny(id, cur), RO);
					return;
				}
				owner[id] = from.Id;
				lastSent.Remove(id);
				ActorPuppet.Set(go, true, true);
				Session.Broadcast(Claim(id, from.Id), RO, from);
				Session.SendTo(from, Claim(id, from.Id), RO); // grant: the requester may consume it now
				return;
			}
			if (!Enter()) return;
			awaiting.Remove(id);
			// Our own grant: Grab already took it (or we've since released it; the host saw that after).
			if (o == Session.LocalId) return;
			var mine = Owns(id);
			if (o == Session.HostId) owner.Remove(id);
			else owner[id] = o;
			restSince.Remove(id);
			if (!objs.TryGetValue(id, out var g) || !g) return;
			if (o == Session.LocalId) ActorPuppet.Set(g, false, true);
			else if (mine) Revoke(g);
			else ActorPuppet.Set(g, true, true);
		});

		// ActorDeny h→c (requester only): actorId long, owner byte = who simulates it now
		// (Unknown 0xFF: host has no such actor, drop the local copy). Client stops simulating and follows the owner.
		Session.On(Op.ActorDeny, (from, r) =>
		{
			var id = r.GetLong();
			var o = r.GetByte();
			if (Session.IsHost || !Enter() || o == Session.LocalId) return;
			awaiting.Remove(id);
			restSince.Remove(id);
			lastSent.Remove(id);
			if (o == Unknown)
			{
				owner.Remove(id);
				if (objs.TryGetValue(id, out var dead) && dead) using (Session.Apply()) Destroyer.DestroyActor(dead, "TailRanch.Deny");
				return;
			}
			if (o == Session.HostId) owner.Remove(id);
			else owner[id] = o;
			if (objs.TryGetValue(id, out var go) && go) Revoke(go);
		});

		// ActorRelease: actorId long. c→h hand back; h→c the host simulates it again.
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
			var mine = Owns(id);
			owner.Remove(id);
			awaiting.Remove(id);
			restSince.Remove(id);
			if (!objs.TryGetValue(id, out var g) || !g) return;
			if (mine) Revoke(g);
			else ActorPuppet.Set(g, true, true);
		});
	}

	static bool Captive(GameObject go)
	{
		var vac = go.GetComponent<Vacuumable>();
		return vac && (vac.isCaptive() || vac.isHeld());
	}

	// Client: lost an actor it was simulating. Let go of it in the vac and follow the owner again.
	static void Revoke(GameObject go)
	{
		var vac = go.GetComponent<Vacuumable>();
		if (vac && vac.isHeld())
		{
			var player = SRSingleton<SceneContext>.Instance.Player;
			var gun = player ? player.GetComponentInChildren<WeaponVacuum>() : null;
			if (gun) gun.DropAllVacced();
			else vac.release();
		}
		else if (vac && (vac.isCaptive() || vac.Pending)) vac.release();
		if (vac) vac.PreventCaptureFor(1f);
		ActorPuppet.Set(go, true, true);
	}

	static void Puppetize(long id, GameObject go)
	{
		if (Session.IsClient) ActorPuppet.Set(go, !Owns(id), true);
		else if (!Owns(id)) ActorPuppet.Set(go, true, true);
	}

	static NetDataWriter Claim(long id, byte o) => Packets.Begin(Op.ActorClaim).Also(w => { w.Put(id); w.Put(o); });
	static NetDataWriter Release(long id) => Packets.Begin(Op.ActorRelease).Also(w => w.Put(id));
	static NetDataWriter Deny(long id, byte o) => Packets.Begin(Op.ActorDeny).Also(w => { w.Put(id); w.Put(o); });

	static NetDataWriter SpawnPacket(long id, GameObject go, bool upsert)
	{
		var t = go.transform;
		var rb = go.GetComponent<Rigidbody>();
		var w = Packets.Begin(Op.ActorSpawn);
		w.Put(id);
		w.Put((int)Identifiable.GetId(go));
		w.Put((int)(regionOf.TryGetValue(id, out var s) ? s : SetId.HOME));
		w.Put(t.position);
		w.Put(t.rotation);
		w.Put(rb ? rb.velocity : Vector3.zero);
		WriteState(w, go, upsert);
		return w;
	}

	// ---------- per-actor state (trails ActorSpawn) ----------
	// flags byte, then for each set bit in order:
	//   1 fashions: count byte + ident int each   2 appearance: SaveSet byte
	//   4 produce: state byte + progressTime double   8 feral: bool   0x80 Upsert (no payload)
	class ActorState
	{
		public byte flags;
		public readonly List<Identifiable.Id> fashions = new();
		public SlimeAppearance.AppearanceSaveSet look;
		public ResourceCycle.State prod;
		public double t;
		public bool feral;
	}

	static readonly ActorState state = new();

	static void WriteState(NetDataWriter w, GameObject go, bool upsert)
	{
		var fa = go.GetComponent<AttachFashions>();
		var look = go.GetComponent<SlimeAppearanceApplicator>();
		var app = look && look.Appearance && look.Appearance.SaveSet != SlimeAppearance.AppearanceSaveSet.NONE ? look.Appearance : null;
		var rc = go.GetComponent<ResourceCycle>();
		var pm = rc ? produce(rc) : null;
		var sf = go.GetComponent<SlimeFeral>();
		var sm = sf ? feralModel(sf) : null;
		w.Put((byte)((fa ? 1 : 0) | (app ? 2 : 0) | (pm != null ? 4 : 0) | (sm != null ? 8 : 0) | (upsert ? Upsert : 0)));
		if (fa)
		{
			var list = fa.GetAllFashions();
			w.Put((byte)list.Count);
			foreach (var f in list) w.Put((int)f);
		}
		if (app) w.Put((byte)app.SaveSet);
		if (pm != null)
		{
			w.Put((byte)pm.state);
			w.Put(pm.progressTime);
		}
		if (sm != null) w.Put(sm.isFeral);
	}

	static ActorState ReadState(NetPacketReader r)
	{
		var s = state;
		s.flags = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
		s.fashions.Clear();
		if ((s.flags & 1) != 0)
			for (int n = r.GetByte(), i = 0; i < n; i++) s.fashions.Add((Identifiable.Id)r.GetInt());
		if ((s.flags & 2) != 0) s.look = (SlimeAppearance.AppearanceSaveSet)r.GetByte();
		if ((s.flags & 4) != 0)
		{
			s.prod = (ResourceCycle.State)r.GetByte();
			s.t = r.GetDouble();
		}
		if ((s.flags & 8) != 0) s.feral = r.GetBool();
		return s;
	}

	// Caller wraps in Session.Apply(). `full` = sender simulates it: produce/feral too, not just looks.
	static void ApplyState(GameObject go, ActorState s, bool full)
	{
		var fa = go.GetComponent<AttachFashions>();
		if (fa && (s.flags & 1) != 0 && !new HashSet<Identifiable.Id>(fa.GetAllFashions()).SetEquals(s.fashions))
		{
			var d = (IDictionary)slotted.GetValue(fa);
			foreach (var e in d.Values)
			{
				var att = Traverse.Create(e).Field<GameObject>("gameObj").Value;
				if (att) Destroyer.Destroy(att, "TailRanch.Fashion");
			}
			d.Clear();
			fa.SetFashions(new List<Identifiable.Id>(s.fashions));
		}

		var look = go.GetComponent<SlimeAppearanceApplicator>();
		if (look && look.SlimeDefinition && (s.flags & 2) != 0)
		{
			var app = look.SlimeDefinition.GetAppearanceForSet(s.look);
			if (app)
			{
				var dir = SRSingleton<SceneContext>.Instance.SlimeAppearanceDirector;
				if (dir && dir.GetChosenSlimeAppearance(look.SlimeDefinition) != app) dir.UpdateChosenSlimeAppearance(look.SlimeDefinition, app);
				if (look.Appearance != app)
				{
					look.Appearance = app;
					look.ApplyAppearance();
				}
			}
		}
		if (!full) return;

		var rc = go.GetComponent<ResourceCycle>();
		var pm = rc ? produce(rc) : null;
		if (pm != null && (s.flags & 4) != 0)
		{
			if (pm.state == s.prod) pm.progressTime = s.t;
			else
			{
				if (s.prod >= ResourceCycle.State.EDIBLE && pm.state < ResourceCycle.State.EDIBLE)
				{
					rc.Detach(null);
					Unpin(go);
				}
				setInitState(rc, s.prod, s.t);
			}
		}

		var sf = go.GetComponent<SlimeFeral>();
		var sm = sf ? feralModel(sf) : null;
		if (sm != null && (s.flags & 8) != 0 && sm.isFeral != s.feral)
		{
			if (s.feral) sf.SetFeral();
			else sf.ClearFeral();
		}
	}

	// Produce came off its joint: it is a free body now, also once the puppet lets go.
	static void Unpin(GameObject go)
	{
		var p = go.GetComponent<ActorPuppet>();
		if (p) p.Unpin();
		else if (go.GetComponent<Rigidbody>() is { } rb && rb) rb.isKinematic = false;
	}

	// Queue a state upsert. ownedOnly: simulation state (produce, feral) only travels from the simulating machine.
	static void Mark(GameObject go, bool ownedOnly)
	{
		if (!Session.Active || Session.Applying || (Session.IsClient && !entered) || !go || !idOf.TryGetValue(go, out var id)) return;
		if (ownedOnly && !Owns(id)) return;
		pendingState.Add(id);
	}

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
			if (objs.TryGetValue(id, out var go) && go) Session.Distribute(SpawnPacket(id, go, false), RO);
		foreach (var id in pendingState)
			if (!pendingSpawns.Contains(id) && objs.TryGetValue(id, out var go) && go) Session.Distribute(SpawnPacket(id, go, true), RO);
		pendingSpawns.Clear();
		pendingState.Clear();
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
				var rest = (!go.activeInHierarchy || !rb || rb.isKinematic || rb.velocity.sqrMagnitude < 0.01f) && !Captive(go);
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
			Session.SendToHost(SpawnPacket(id, objs[id], true), RO); // final state (ripeness, feral) before handing back
			batch.Clear();
			batch.Add(id);
			SendMotion(batch, w => Session.SendToHost(w, RO));
			Session.SendToHost(Release(id), RO);
			owner.Remove(id);
			awaiting.Remove(id);
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
		else if (objs.TryGetValue(id, out var cur) && !ReferenceEquals(cur, go))
		{
			// Stale object for a reused id: drop only its reverse mapping.
			idOf.Remove(go);
			return;
		}
		if ((object)go != null) idOf.Remove(go);
		objs.Remove(id);
		regionOf.Remove(id);
		owner.Remove(id);
		awaiting.Remove(id);
		lastSent.Remove(id);
		restSince.Remove(id);
		pendingState.Remove(id);
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
		awaiting.Clear();
		pendingSpawns.Clear();
		pendingDespawns.Clear();
		pendingState.Clear();
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
		if (objs.TryGetValue(id, out var prev) && (object)prev != null && !ReferenceEquals(prev, go)) idOf.Remove(prev);
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
			awaiting.Add(id);
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

	static bool Awaiting(GameObject go) => go && idOf.TryGetValue(go, out var id) && awaiting.Contains(id);

	// A denied/revoked actor still animating into the vac must not turn into ammo, nor one whose
	// claim is unanswered (the host may refuse it and respawn it: a duplicate).
	[HarmonyPatch(typeof(Vacuumable), nameof(Vacuumable.TryConsume))]
	static class OnConsume
	{
		static bool Prefix(Vacuumable __instance, ref bool __result)
		{
			if (Session.Applying || !IsRemoteOwned(__instance.gameObject) && !Awaiting(__instance.gameObject)) return true;
			__result = false;
			return false;
		}
	}

	// Client: hold the item at the nozzle (MoveTowards stays on) until the host answers the claim.
	[HarmonyPatch(typeof(WeaponVacuum), "StartConsuming")]
	static class ConsumeAwaitsClaim
	{
		static void Postfix(Vacuumable vacuumable, ref IEnumerator __result)
		{
			if (Session.IsClient) __result = AwaitClaim(__result, vacuumable);
		}

		static IEnumerator AwaitClaim(IEnumerator inner, Vacuumable vac)
		{
			if (!inner.MoveNext()) yield break;
			yield return inner.Current;
			var until = Time.time + ClaimWait;
			while (vac && Awaiting(vac.gameObject) && Time.time < until) yield return null;
			while (inner.MoveNext()) yield return inner.Current;
		}
	}

	// Hats: the machine simulating the fashion item resolves the hit; the wearer's looks go out as an upsert.
	[HarmonyPatch]
	static class FashionHit
	{
		static IEnumerable<MethodBase> TargetMethods() => Named(typeof(Fashion), "OnCollisionEnter").Concat(Named(typeof(FashionRemover), "OnCollisionEnter"));

		static bool Prefix(Component __instance) => !IsRemoteOwned(__instance.gameObject);
	}

	[HarmonyPatch]
	static class LooksChanged
	{
		static IEnumerable<MethodBase> TargetMethods() => Named(typeof(AttachFashions), "Attach").Concat(Named(typeof(AttachFashions), "DetachAll"));

		static void Postfix(Component __instance) => Mark(__instance.gameObject, false);
	}

	[HarmonyPatch]
	static class SimStateChanged
	{
		static IEnumerable<MethodBase> TargetMethods() => Named(typeof(SlimeFeral), "MakeFeral").Concat(Named(typeof(SlimeFeral), "MakeNotFeral"))
			.Concat(Named(typeof(ResourceCycle), "Ripen")).Concat(Named(typeof(ResourceCycle), "MakeEdible")).Concat(Named(typeof(ResourceCycle), "Rot"));

		static void Postfix(Component __instance) => Mark(__instance.gameObject, true);
	}

	// Ripe produce dropping off its joint on any machine (local ripening included).
	[HarmonyPatch(typeof(ResourceCycle), "MakeEdible")]
	static class ProduceDetached
	{
		static void Postfix(ResourceCycle __instance)
		{
			var p = __instance.GetComponent<ActorPuppet>();
			if (p) p.Unpin();
		}
	}

	// Chosen style (UI, treasure pod) re-skins every slime of that kind; push each one's new look.
	[HarmonyPatch(typeof(SlimeAppearanceDirector), nameof(SlimeAppearanceDirector.UpdateChosenSlimeAppearance))]
	static class StyleChosen
	{
		static void Postfix()
		{
			if (!Session.Active || Session.Applying) return;
			foreach (var kv in objs)
				if (kv.Value && kv.Value.GetComponent<SlimeAppearanceApplicator>()) Mark(kv.Value, false);
		}
	}

	// Player actions on the client: spawns/despawns inside are real and broadcast.
	[HarmonyPatch]
	static class PlayerAction
	{
		static IEnumerable<MethodBase> TargetMethods() => Named(typeof(WeaponVacuum), "Expel")
			.Concat(Named(typeof(WeaponVacuum), "ConsumeVacItem"))
			.Concat(Named(typeof(ToyShopUI), "InstantiateToy"))
			.Concat(Named(typeof(SiloCatcher), "OnTriggerStay"))
			.Concat(Named(typeof(RescueAmmoOnDeath), "SpawnRescuedItems")); // only ever the local player's own death

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
			(typeof(TransformChanceOnReproduce), "MaybeTransform"),
			(typeof(GlitchTarrSterilizeOnWater), "WillDestroyAfterTime"), (typeof(LemonPhasor), "OnTriggerEnter"),
			(typeof(DirectedCrateSpawner), "Spawn"), (typeof(DirectedAuxItemSpawner), "Spawn"), (typeof(PollenCloudController), "Update"),
		}.SelectMany(x => Named(x.Item1, x.Item2))
			.Where(m => m.Name != "Grow" || m.GetParameters().Length == 0); // Grow(GameObject) re-attaches synced produce

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
