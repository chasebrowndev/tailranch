using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MonomiPark.SlimeRancher.DataModel;
using MonomiPark.SlimeRancher.Persist;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Ranch storage is host-authoritative: the host broadcasts full contents (SiloState,
// StorageState) after any change; clients mutate optimistically and send deltas (SiloOp,
// StorageState deltas) that the host validates, applies and answers with the full state.
public static class RanchSync
{
	const DeliveryMethod R = DeliveryMethod.ReliableOrdered;
	// SiloState/SiloOp storageType for the incinerator ash trough (LandPlotModel.ashUnits, one float).
	const int AshStorage = -1;

	// CollectorPull kinds.
	const byte PullStart = 0, PullStored = 1, PullRequest = 2;
	// StorageState kinds.
	const byte DecorState = 0, DecorDelta = 1, DecorSelect = 2, DecorCleanup = 3, GlitchState = 4, GlitchDelta = 5, Shortfall = 6;
	const int GlitchMax = 300; // GlitchStorage.MAX_COUNT
	const float AckTimeout = 2f; // client stops waiting for a SiloOp answer (host lost the silo)

	static readonly AccessTools.FieldRef<LandPlotModel, GameObject> plotObj = AccessTools.FieldRefAccess<LandPlotModel, GameObject>("gameObj");
	static readonly AccessTools.FieldRef<Ammo, AmmoModel> ammoModel = AccessTools.FieldRefAccess<Ammo, AmmoModel>("ammoModel");
	static readonly AccessTools.FieldRef<FillableAshSource, LandPlotModel> ashModel = AccessTools.FieldRefAccess<FillableAshSource, LandPlotModel>("plotModel");
	static readonly MethodInfo updateAsh = AccessTools.Method(typeof(FillableAshSource), "UpdateAshPosition");
	static readonly AccessTools.FieldRef<DecorizerModel, ReferenceCount<Identifiable.Id>> decorContents = AccessTools.FieldRefAccess<DecorizerModel, ReferenceCount<Identifiable.Id>>("contents");
	static readonly AccessTools.FieldRef<DecorizerModel, Dictionary<string, DecorizerModel.Settings>> decorSettings = AccessTools.FieldRefAccess<DecorizerModel, Dictionary<string, DecorizerModel.Settings>>("settings");

	// Every silo-like Ammo → its owning storage; player ammo never lands here.
	static readonly ConditionalWeakTable<Ammo, SiloStorage> silos = new();
	// Host: storages to broadcast.
	static readonly HashSet<SiloStorage> dirtySilos = new();
	static readonly HashSet<FillableAshSource> dirtyAsh = new();
	static readonly HashSet<string> dirtyGlitch = new();
	static bool dirtyDecor;
	// Client: contents before the first unsent local change (diffed into SiloOps), and ash deltas.
	static readonly Dictionary<Ammo, (Identifiable.Id id, int n)[]> baseline = new();
	static readonly Dictionary<FillableAshSource, float> ashDelta = new();
	// Client: SiloOps sent per silo and not yet answered (count, last send time).
	static readonly Dictionary<Ammo, (int n, float at)> inFlight = new();
	// Client: actors a silo ejected into the vac, until captured (destroyed) — take-back candidates.
	static readonly List<GameObject> ejected = new();
	static readonly HashSet<Gadget> dirtyGadgets = new();

	static GameModel Game => SRSingleton<SceneContext>.Instance.GameModel;
	static bool Live => EconomySync.Live;
	static bool Puppet => Session.InRemoteWorld;

	public static void Register()
	{
		Wake();

		On(Op.PlotReplace, r =>
		{
			var loc = Plot(r.GetString())?.GetComponent<LandPlotLocation>();
			var type = (LandPlot.Id)r.GetInt();
			var old = loc ? loc.GetComponentInChildren<LandPlot>() : null;
			if (old == null || old.typeId == type) return;
			loc.Replace(old, SRSingleton<GameContext>.Instance.LookupDirector.GetPlotPrefab(type));
		});

		// upgrade >= 0: LandPlot.Upgrade. < 0: garden crop, -1 - Identifiable.Id (NONE = clear crop).
		On(Op.PlotUpgrade, r =>
		{
			var plot = InPlot<LandPlot>(r.GetString());
			var v = r.GetInt();
			if (plot == null) return;
			if (v >= 0)
			{
				if (!plot.HasUpgrade((LandPlot.Upgrade)v)) plot.AddUpgrade((LandPlot.Upgrade)v);
				return;
			}
			var crop = (Identifiable.Id)(-1 - v);
			if (crop == Identifiable.Id.NONE)
			{
				if (plot.HasAttached()) plot.DestroyAttached();
			}
			else if (!plot.HasAttached())
				plot.GetComponentInChildren<GardenCatcher>(true)?.Plant(crop, isReplacement: false);
		});

		// h→c  storageId string (plotId, or siteId for warp depots), storageType int, then
		//      ash: units float | ammo: n byte × (ident int, [count int, emo byte × (emotion byte, value float)]), ack bool,
		//      [ack: m byte × (ident int, shortfall int)]
		// ack: the host's answer to one of this client's SiloOps (sent to the requester only).
		// shortfall = requested - applied: > 0 adds that didn't fit, < 0 removes the host couldn't cover.
		Session.On(Op.SiloState, (_, r) =>
		{
			var id = r.GetString();
			var type = r.GetInt();
			if (Session.IsHost || !Session.InWorld) return;
			if (type == AshStorage)
			{
				var units = r.GetFloat();
				var m = Game.GetLandPlotModel(id);
				if (m == null) return;
				m.ashUnits = units;
				var ash = InPlot<FillableAshSource>(id);
				if (ash) updateAsh.Invoke(ash, null);
				return;
			}
			var data = ReadAmmo(r);
			var ack = r.GetBool();
			if (ack)
				for (int i = 0, n = r.GetByte(); i < n; i++) Reconcile((Identifiable.Id)r.GetInt(), r.GetInt());
			var ammo = FindSilo(id, type)?.LocalAmmo;
			if (Slots(ammo) == null) return;
			if (ack && inFlight.TryGetValue(ammo, out var f))
			{
				if (f.n <= 1) inFlight.Remove(ammo);
				else inFlight[ammo] = (f.n - 1, f.at);
			}
			// Unsent local edits are diffed against the pre-edit contents; send them before overwriting.
			if (baseline.TryGetValue(ammo, out var before))
			{
				SendOps(ammo, before);
				baseline.Remove(ammo);
			}
			// Older than ops still in flight: it would resurrect what we took out (dup) or drop what we
			// put in. Their ack carries newer contents.
			if (inFlight.TryGetValue(ammo, out f) && Time.time - f.at < AckTimeout) return;
			inFlight.Remove(ammo);
			using (Session.Apply())
			{
				ammo.Clear();
				ammo.FromSerializable(data);
			}
		});

		// c→h  storageId string, storageType int, then
		//      ash: delta float | ammo: n byte × (slot byte, ident int, delta int)
		// Host applies what still fits and answers the sender with an ack SiloState (its corrected
		// contents plus the per-op shortfall the client must reconcile), then broadcasts the change.
		Session.On(Op.SiloOp, (from, r) =>
		{
			var id = r.GetString();
			var type = r.GetInt();
			if (!Session.IsHost || !Session.InWorld) return;
			if (type == AshStorage)
			{
				var d = r.GetFloat();
				var ash = InPlot<FillableAshSource>(id);
				var m = ash ? ashModel(ash) : null;
				if (m == null) return;
				m.ashUnits = Mathf.Clamp(m.ashUnits + d, 0f, ash.maxUnits);
				updateAsh.Invoke(ash, null);
				MarkAsh(ash);
				return;
			}
			var n = r.GetByte();
			var ops = new (int slot, Identifiable.Id id, int delta)[n];
			for (var i = 0; i < n; i++) ops[i] = (r.GetByte(), (Identifiable.Id)r.GetInt(), r.GetInt());
			var s = FindSilo(id, type);
			var gone = s == null || Slots(s.LocalAmmo) == null;
			var miss = new List<(Identifiable.Id id, int n)>();
			foreach (var op in ops)
			{
				var gap = gone ? op.delta : op.delta - ApplyOp(s.LocalAmmo, op.slot, op.id, op.delta);
				if (gap != 0) miss.Add((op.id, gap));
			}
			NetDataWriter w;
			if (gone)
			{
				// No such silo here: ack at once with empty contents and every op as shortfall, so the
				// client refunds now instead of waiting out AckTimeout.
				w = Packets.Begin(Op.SiloState);
				w.Put(id);
				w.Put(type);
				w.Put((byte)0);
				w.Put(true);
			}
			else w = SiloPacket(s, true);
			w.Put((byte)miss.Count);
			foreach (var m in miss)
			{
				w.Put((int)m.id);
				w.Put(m.n);
			}
			Session.SendTo(from, w, R);
			if (!gone) MarkSilo(s);
		});

		On(Op.FeederSpeed, r =>
		{
			var feeder = InPlot<SlimeFeeder>(r.GetString());
			var speed = (SlimeFeeder.FeedSpeed)r.GetInt();
			if (feeder) feeder.SetFeederSpeed(speed);
		});

		// plotId string, kind byte, n byte.
		//   h→c  PullStart: collector cycle began (n = plorts captured) → clients run the cyclone FX
		//   h→c  PullStored: n plorts reached the collector → clients play collectFX
		//   c→h  PullRequest: client pressed the collector button → host StartCollection
		Session.On(Op.CollectorPull, (_, r) =>
		{
			var id = r.GetString();
			var kind = r.GetByte();
			var n = r.GetByte();
			if (!Session.InWorld) return;
			var c = InPlot<PlortCollector>(id);
			if (c == null) return;
			if (kind == PullRequest)
			{
				if (Session.IsHost) c.StartCollection();
				return;
			}
			if (Session.IsHost) return;
			using (Session.Apply())
			{
				if (kind == PullStart) Collector.forceUntil(c) = Collector.timeDir(c).HoursFromNow(1f / 6f);
				else if (c.collectFX && c.collectPt)
					for (var i = 0; i < Math.Min((int)n, 6); i++) SRBehaviour.SpawnAndPlayFX(c.collectFX, c.collectPt.position, c.collectPt.rotation);
			}
		});

		// kind byte, then:
		//   h→c  DecorState:   n ushort × (ident int, count int), m ushort × (decorizerId string, selected int)
		//   c→h  DecorDelta:   ident int, delta int
		//   c→h  DecorSelect:  decorizerId string, selected int
		//   c→h  DecorCleanup: decorizerId string, n ushort × ident int (host runs DecorizerStorage.Cleanup)
		//   h→c  GlitchState:  storageId string, ident int, count int
		//   c→h  GlitchDelta:  storageId string, ident int, delta int (±1)
		//   h→c  Shortfall:    ident int, gap int (the part of a delta the host could not apply; the client reconciles)
		Session.On(Op.StorageState, (from, r) =>
		{
			var kind = r.GetByte();
			if (!Session.InWorld) return;
			var decor = Game.GetDecorizerModel();
			switch (kind)
			{
				case DecorState when !Session.IsHost:
				{
					var contents = new Dictionary<Identifiable.Id, int>(Identifiable.idComparer);
					for (int i = 0, n = r.GetUShort(); i < n; i++) contents[(Identifiable.Id)r.GetInt()] = r.GetInt();
					var sel = new List<(string, Identifiable.Id)>();
					for (int i = 0, n = r.GetUShort(); i < n; i++) sel.Add((r.GetString(), (Identifiable.Id)r.GetInt()));
					using (Session.Apply())
					{
						decorContents(decor) = new ReferenceCount<Identifiable.Id>(contents, Identifiable.idComparer);
						// In place: DecorizerStorage keeps a reference to its Settings object.
						foreach (var (sid, s) in sel) decor.GetSettings(sid).selected = s;
					}
					break;
				}
				case DecorDelta when Session.IsHost:
				{
					var id = (Identifiable.Id)r.GetInt();
					var d = Math.Max(-1, Math.Min(1, r.GetInt()));
					var applied = 0;
					if (d > 0 && decor.Add(id)) applied = 1;
					else if (d < 0 && decor.GetCount(id) > 0 && decor.Remove(id)) applied = -1;
					SendShortfall(from, id, d - applied);
					dirtyDecor = true;
					break;
				}
				case DecorSelect when Session.IsHost:
				{
					var sid = r.GetString();
					var id = (Identifiable.Id)r.GetInt();
					decor.GetSettings(sid).selected = id == Identifiable.Id.NONE || decor.GetCount(id) > 0 ? id : Identifiable.Id.NONE;
					dirtyDecor = true;
					break;
				}
				case DecorCleanup when Session.IsHost:
				{
					var sid = r.GetString();
					var ids = new List<Identifiable.Id>();
					for (int i = 0, n = r.GetUShort(); i < n; i++) ids.Add((Identifiable.Id)r.GetInt());
					UnityEngine.Object.FindObjectsOfType<DecorizerStorage>().FirstOrDefault(s => s.id == sid)?.Cleanup(ids);
					dirtyDecor = true;
					break;
				}
				case GlitchState when !Session.IsHost:
				{
					var sid = r.GetString();
					var id = (Identifiable.Id)r.GetInt();
					var count = r.GetInt();
					if (!Game.Glitch.storage.ContainsKey(sid)) return;
					var m = Game.Glitch.storage[sid];
					m.id = id;
					m.count = count;
					break;
				}
				case GlitchDelta when Session.IsHost:
				{
					var sid = r.GetString();
					var id = (Identifiable.Id)r.GetInt();
					var d = Math.Max(-1, Math.Min(1, r.GetInt()));
					if (!Game.Glitch.storage.ContainsKey(sid))
					{
						SendShortfall(from, id, d);
						return;
					}
					var m = Game.Glitch.storage[sid];
					var was = m.count;
					if (d > 0 && (m.count == 0 || m.id == id))
					{
						m.id = id;
						m.count = Math.Min(GlitchMax, m.count + d);
					}
					else if (d < 0 && m.id == id)
					{
						m.count = Math.Max(0, m.count + d);
						if (m.count == 0) m.id = Identifiable.Id.NONE;
					}
					SendShortfall(from, id, d - (m.count - was));
					MarkGlitch(sid);
					break;
				}
				case Shortfall when !Session.IsHost:
				{
					var id = (Identifiable.Id)r.GetInt();
					var gap = r.GetInt();
					Reconcile(id, Math.Max(-1, Math.Min(1, gap)));
					break;
				}
			}
		});

		Session.On(Op.GadgetPlace, (from, r) =>
		{
			var siteId = r.GetString();
			var id = (Gadget.Id)r.GetInt();
			var rot = r.GetFloat();
			if (!Session.InWorld || !Game.AllGadgetSites().TryGetValue(siteId, out var site)) return;
			if (site.HasAttached() && site.attached.ident != id) return; // host: site taken, drop
			if (Session.IsHost) Session.Relay(from, r, R);
			using (Session.Apply())
			{
				var go = site.HasAttached() ? site.attached.transform.gameObject
					: Game.InstantiateGadget(SRSingleton<GameContext>.Instance.LookupDirector.GetGadgetDefinition(id).prefab, site);
				go.GetComponent<Gadget>()?.SetRotation(rot);
			}
		});

		On(Op.GadgetRemove, r =>
		{
			if (!Game.AllGadgetSites().TryGetValue(r.GetString(), out var site)) return;
			var gs = site.transform.GetComponent<GadgetSite>();
			if (gs && gs.HasAttached()) gs.DestroyAttached();
		});

		Session.On(Op.Refinery, (_, r) =>
		{
			var id = (Identifiable.Id)r.GetInt();
			var v = r.GetInt();
			if (!Session.InWorld) return;
			var counts = Game.GetGadgetsModel().craftMatCounts;
			if (Session.IsHost)
			{
				counts[id] = Math.Max(0, counts.Get(id) + v);
				SendRefinery(id, 0);
			}
			else counts[id] = v;
		});

		// c→h  gadgetId int: the client wants to fabricate. The host spends the costs from the shared
		//      refinery (its own definition, never the client's numbers).
		// h→c  gadgetId int, ok bool: the client crafts only now, so a gadget never exists before it's paid for.
		Session.On(Op.Craft, (from, r) =>
		{
			var id = (Gadget.Id)r.GetInt();
			if (!Session.InWorld) return;
			if (!Session.IsHost)
			{
				Fabricator.Answer(id, r.GetBool());
				return;
			}
			GadgetDefinition def;
			try { def = SRSingleton<GameContext>.Instance.LookupDirector.GetGadgetDefinition(id); }
			catch (KeyNotFoundException) { return; }
			var ok = SRSingleton<SceneContext>.Instance.GadgetDirector.TryToSpendFromRefinery(def.craftCosts); // the spend postfix broadcasts the totals
			Session.SendTo(from, Packets.Begin(Op.Craft).Also(w => { w.Put((int)id); w.Put(ok); }), R);
			if (!ok) foreach (var c in def.craftCosts) SendRefinery(c.id, 0); // resync the requester's mirror
		});
	}

	// Symmetric world event: host relays to the other clients, everyone replays under Apply.
	static void On(Op op, Action<NetPacketReader> apply) => Session.On(op, (from, r) =>
	{
		if (Session.IsHost) Session.Relay(from, r, R);
		if (!Session.InWorld) return;
		using (Session.Apply()) apply(r);
	});

	static GameObject Plot(string id)
	{
		var m = Game.GetLandPlotModel(id);
		var go = m == null ? null : plotObj(m);
		return go ? go : null; // destroyed plot objects must not reach a ?. chain
	}

	static T InPlot<T>(string id) where T : Component
	{
		var p = Plot(id);
		return p ? p.GetComponentInChildren<T>(true) : null;
	}

	// GetComponentInParent skips inactive objects; storages can sit under inactive upgrade roots.
	static T Up<T>(Component c) where T : Component
	{
		for (var t = c ? c.transform : null; t; t = t.parent)
			if (t.GetComponent<T>() is { } found) return found;
		return null;
	}

	static string PlotId(Component c) => Up<LandPlotLocation>(c)?.id;

	static string StorageId(SiloStorage s) => s is LinkedSiloStorage ? Up<GadgetSite>(s)?.id : PlotId(s);

	static SiloStorage FindSilo(string id, int type)
	{
		var plot = Plot(id);
		if (plot) return plot.GetComponentsInChildren<SiloStorage>(true).FirstOrDefault(s => (int)s.type == type && !(s is LinkedSiloStorage));
		if (Game.AllGadgetSites().TryGetValue(id, out var site) && site.HasAttached())
			return site.attached.transform.GetComponentInChildren<LinkedSiloStorage>(true);
		return null;
	}

	static void Send(Op op, string id, int v)
	{
		if (!Live || id == null) return;
		var w = Packets.Begin(op);
		w.Put(id);
		w.Put(v);
		Session.Distribute(w, R);
	}

	// Client: delta to host. Host: current total to everyone.
	static void SendRefinery(Identifiable.Id id, int delta)
	{
		var w = Packets.Begin(Op.Refinery);
		w.Put((int)id);
		if (Session.IsHost)
		{
			w.Put(Game.GetGadgetsModel().craftMatCounts.Get(id));
			Session.Broadcast(w, R);
		}
		else
		{
			w.Put(delta);
			Session.SendToHost(w, R);
		}
	}

	static void SendPull(PlortCollector c, byte kind, int n)
	{
		var id = PlotId(c);
		if (id == null) return;
		var w = Packets.Begin(Op.CollectorPull);
		w.Put(id);
		w.Put(kind);
		w.Put((byte)Math.Min(n, 255));
		if (Session.IsHost) Session.Broadcast(w, R);
		else Session.SendToHost(w, R);
	}

	// Host: a client's decorizer/glitch delta only partly applied (full, wrong item, already emptied),
	// or an exchange deposit the host refused.
	public static void SendShortfall(Player to, Identifiable.Id id, int gap)
	{
		if (gap == 0 || to == null) return;
		var w = Packets.Begin(Op.StorageState);
		w.Put(Shortfall);
		w.Put((int)id);
		w.Put(gap);
		Session.SendTo(to, w, R);
	}

	static void SendStorage(byte kind, Action<NetDataWriter> body)
	{
		var w = Packets.Begin(Op.StorageState);
		w.Put(kind);
		body(w);
		if (Session.IsHost) Session.Broadcast(w, R);
		else Session.SendToHost(w, R);
	}

	// ---------- coalescing pump ----------

	static Pump pump;

	// Own DontDestroyOnLoad object, recreated if anything ever destroys it.
	static void Wake()
	{
		if (pump) return;
		var go = new GameObject("TailRanch.RanchPump");
		UnityEngine.Object.DontDestroyOnLoad(go);
		pump = go.AddComponent<Pump>();
	}

	static void MarkSilo(SiloStorage s)
	{
		Wake();
		dirtySilos.Add(s);
	}

	static void MarkAsh(FillableAshSource a)
	{
		Wake();
		dirtyAsh.Add(a);
	}

	static void MarkGlitch(string id)
	{
		Wake();
		if (id != null) dirtyGlitch.Add(id);
	}

	static void MarkDecor()
	{
		Wake();
		dirtyDecor = true;
	}

	sealed class Pump : MonoBehaviour
	{
		void LateUpdate() => Flush();
	}

	static void Flush()
	{
		if (!Live || !Session.InWorld)
		{
			dirtySilos.Clear();
			dirtyAsh.Clear();
			dirtyGlitch.Clear();
			dirtyDecor = false;
			baseline.Clear();
			inFlight.Clear();
			ejected.Clear();
			ashDelta.Clear();
			dirtyGadgets.Clear();
			return;
		}
		if (Session.IsHost) FlushHost();
		else FlushClient();
		// Deferred so the post-placement RotateToPlayer is included.
		foreach (var g in dirtyGadgets)
		{
			var site = g ? g.GetComponentInParent<GadgetSite>() : null;
			if (site == null) continue;
			var w = Packets.Begin(Op.GadgetPlace);
			w.Put(site.id);
			w.Put((int)g.id);
			w.Put(g.GetRotation());
			Session.Distribute(w, R);
		}
		dirtyGadgets.Clear();
	}

	static void FlushHost()
	{
		foreach (var s in dirtySilos)
		{
			var id = s ? StorageId(s) : null;
			if (id == null || Slots(s.LocalAmmo) == null) continue;
			Session.Broadcast(SiloPacket(s, false), R);
		}
		dirtySilos.Clear();
		foreach (var a in dirtyAsh)
		{
			var id = a ? PlotId(a) : null;
			var m = a ? ashModel(a) : null;
			if (id == null || m == null) continue;
			var w = Packets.Begin(Op.SiloState);
			w.Put(id);
			w.Put(AshStorage);
			w.Put(m.ashUnits);
			Session.Broadcast(w, R);
		}
		dirtyAsh.Clear();
		if (dirtyDecor)
		{
			var decor = Game.GetDecorizerModel();
			var contents = decorContents(decor).ToList();
			var settings = decorSettings(decor).ToList();
			SendStorage(DecorState, w =>
			{
				w.Put((ushort)contents.Count);
				foreach (var kv in contents)
				{
					w.Put((int)kv.Key);
					w.Put(kv.Value);
				}
				w.Put((ushort)settings.Count);
				foreach (var kv in settings)
				{
					w.Put(kv.Key);
					w.Put((int)kv.Value.selected);
				}
			});
			dirtyDecor = false;
		}
		foreach (var sid in dirtyGlitch)
		{
			if (!Game.Glitch.storage.ContainsKey(sid)) continue;
			var m = Game.Glitch.storage[sid];
			SendStorage(GlitchState, w =>
			{
				w.Put(sid);
				w.Put((int)m.id);
				w.Put(m.count);
			});
		}
		dirtyGlitch.Clear();
	}

	static NetDataWriter SiloPacket(SiloStorage s, bool ack)
	{
		var w = Packets.Begin(Op.SiloState);
		w.Put(StorageId(s));
		w.Put((int)s.type);
		WriteAmmo(w, s.LocalAmmo);
		w.Put(ack);
		return w;
	}

	static void FlushClient()
	{
		foreach (var kv in baseline) SendOps(kv.Key, kv.Value);
		baseline.Clear();
		foreach (var kv in ashDelta)
		{
			var id = kv.Key ? PlotId(kv.Key) : null;
			if (id == null || kv.Value == 0f) continue;
			var w = Packets.Begin(Op.SiloOp);
			w.Put(id);
			w.Put(AshStorage);
			w.Put(kv.Value);
			Session.SendToHost(w, R);
		}
		ashDelta.Clear();
	}

	// ---------- silo contents ----------

	static Ammo.Slot[] Slots(Ammo a) => a == null ? null : ammoModel(a)?.slots;

	static (Identifiable.Id id, int n)[] Snap(Ammo a)
	{
		var slots = Slots(a);
		if (slots == null) return null;
		var snap = new (Identifiable.Id, int)[slots.Length];
		for (var i = 0; i < slots.Length; i++)
			snap[i] = slots[i] == null ? (Identifiable.Id.NONE, 0) : (slots[i].id, slots[i].count);
		return snap;
	}

	// Client: per-slot diff of local edits since `before` → one SiloOp.
	static void SendOps(Ammo ammo, (Identifiable.Id id, int n)[] before)
	{
		if (before == null || !silos.TryGetValue(ammo, out var s) || !s) return;
		var id = StorageId(s);
		var now = Snap(ammo);
		if (id == null || now == null) return;
		var ops = new List<(int slot, Identifiable.Id id, int delta)>();
		for (var i = 0; i < Math.Min(before.Length, now.Length); i++)
		{
			var (b, c) = (before[i], now[i]);
			if (b.id == c.id)
			{
				if (c.n != b.n && c.id != Identifiable.Id.NONE) ops.Add((i, c.id, c.n - b.n));
				continue;
			}
			if (b.id != Identifiable.Id.NONE && b.n > 0) ops.Add((i, b.id, -b.n));
			if (c.id != Identifiable.Id.NONE && c.n > 0) ops.Add((i, c.id, c.n));
		}
		if (ops.Count == 0) return;
		var w = Packets.Begin(Op.SiloOp);
		w.Put(id);
		w.Put((int)s.type);
		w.Put((byte)ops.Count);
		foreach (var op in ops)
		{
			w.Put((byte)op.slot);
			w.Put((int)op.id);
			w.Put(op.delta);
		}
		Session.SendToHost(w, R);
		inFlight[ammo] = ((inFlight.TryGetValue(ammo, out var f) ? f.n : 0) + 1, Time.time);
	}

	// Host: apply one client delta up to what fits / what the host has; returns the applied delta.
	// The client reconciles the rest (refund or take back), so nothing overflows.
	static int ApplyOp(Ammo ammo, int slot, Identifiable.Id id, int delta)
	{
		var len = Slots(ammo).Length;
		if (delta > 0)
		{
			var before = Count(ammo, id);
			if (slot < len)
			{
				var room = Math.Min(delta, ammo.GetSlotMaxCount(id, slot) - ammo.GetSlotCount(slot));
				if (room > 0) ammo.MaybeAddToSpecificSlot(id, null, slot, room, false);
			}
			for (var i = 0; i < delta && Count(ammo, id) - before < delta && ammo.MaybeAddToSlot(id, null); i++) { }
			return Math.Min(delta, Count(ammo, id) - before);
		}
		int? idx = slot < len && ammo.GetSlotName(slot) == id ? slot : ammo.GetAmmoIdx(id);
		if (idx is not { } k) return 0;
		var take = Math.Min(-delta, ammo.GetSlotCount(k));
		if (take > 0) ammo.Decrement(k, take);
		return -Math.Max(take, 0);
	}

	static int Count(Ammo ammo, Identifiable.Id id) => Slots(ammo).Sum(x => x != null && x.id == id ? x.count : 0);

	// Client: the host applied `gap` less than a SiloOp asked. Adds that didn't fit go back to the
	// vac, or drop at the player's feet; removes it couldn't cover are taken back from the
	// still-flying ejected actors, then the vac.
	static void Reconcile(Identifiable.Id id, int gap)
	{
		var ctx = SRSingleton<SceneContext>.Instance;
		var vac = ctx.PlayerState?.Ammo;
		if (vac == null || id == Identifiable.Id.NONE) return;
		if (gap > 0)
		{
			var drop = 0;
			using (Session.Apply())
				for (var i = 0; i < gap; i++)
					if (!vac.MaybeAddToSlot(id, null)) drop++;
			var prefab = drop > 0 && ctx.Player ? SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(id) : null;
			if (prefab == null) return;
			var t = ctx.Player.transform;
			var set = ctx.RegionRegistry.GetCurrentRegionSetId();
			ActorSync.AsPlayer(() =>
			{
				for (var i = 0; i < drop; i++)
					SRBehaviour.InstantiateActor(prefab, set, t.position + t.forward + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.3f, Quaternion.identity);
			});
			return;
		}
		var owe = -gap;
		ejected.RemoveAll(go => !go);
		ActorSync.AsPlayer(() =>
		{
			for (var i = ejected.Count - 1; i >= 0 && owe > 0; i--)
			{
				var go = ejected[i];
				if (Identifiable.GetId(go) != id || ActorSync.IsRemoteOwned(go)) continue;
				ejected.RemoveAt(i);
				Destroyer.DestroyActor(go, "TailRanch.SiloTakeBack");
				owe--;
			}
		});
		using (Session.Apply())
			while (owe > 0 && vac.GetAmmoIdx(id) is { } k)
			{
				var take = Math.Min(owe, vac.GetSlotCount(k));
				vac.Decrement(k, take);
				owe -= take;
			}
	}

	// AmmoDataV02 per slot: id, count, emotions. Raw slot ids (ToSerializable reports water as MAGIC_WATER).
	static void WriteAmmo(NetDataWriter w, Ammo ammo)
	{
		var data = ammo.ToSerializable();
		var slots = Slots(ammo);
		w.Put((byte)data.Count);
		for (var i = 0; i < data.Count; i++)
		{
			var d = data[i];
			if (slots[i] != null) d.id = slots[i].id;
			w.Put((int)d.id);
			if (d.id == Identifiable.Id.NONE) continue;
			w.Put(d.count);
			var emo = d.emotionData?.emotionData;
			w.Put((byte)(emo?.Count ?? 0));
			if (emo == null) continue;
			foreach (var kv in emo)
			{
				w.Put((byte)kv.Key);
				w.Put(kv.Value);
			}
		}
	}

	static List<AmmoDataV02> ReadAmmo(NetDataReader r)
	{
		var n = r.GetByte();
		var data = new List<AmmoDataV02>(n);
		for (var i = 0; i < n; i++)
		{
			var d = new AmmoDataV02 { id = (Identifiable.Id)r.GetInt(), emotionData = new SlimeEmotionDataV02() };
			if (d.id != Identifiable.Id.NONE)
			{
				d.count = r.GetInt();
				var e = r.GetByte();
				for (var j = 0; j < e; j++) d.emotionData.emotionData[(SlimeEmotions.Emotion)r.GetByte()] = r.GetFloat();
			}
			data.Add(d);
		}
		return data;
	}

	// ---------- plots ----------

	[HarmonyPatch(typeof(LandPlotLocation), nameof(LandPlotLocation.Replace))]
	static class PlotReplace
	{
		static void Postfix(LandPlotLocation __instance, GameObject replacementPrefab) =>
			Send(Op.PlotReplace, __instance.id, (int)replacementPrefab.GetComponent<LandPlot>().typeId);
	}

	[HarmonyPatch(typeof(LandPlot), nameof(LandPlot.AddUpgrade))]
	static class PlotUpgrade
	{
		static void Postfix(LandPlot __instance, LandPlot.Upgrade upgrade) => Send(Op.PlotUpgrade, PlotId(__instance), (int)upgrade);
	}

	// Garden planting/clearing rides PlotUpgrade (negative values). Replacement replant is
	// a deterministic side effect of DELUXE_GARDEN, so it isn't sent.
	[HarmonyPatch(typeof(GardenCatcher), nameof(GardenCatcher.Plant))]
	static class Plant
	{
		static void Postfix(GardenCatcher __instance, Identifiable.Id cropId, bool isReplacement)
		{
			if (!isReplacement) Send(Op.PlotUpgrade, PlotId(__instance), -1 - (int)cropId);
		}
	}

	[HarmonyPatch(typeof(LandPlot), nameof(LandPlot.DestroyAttached))]
	static class ClearCrop
	{
		static void Postfix(LandPlot __instance) => Send(Op.PlotUpgrade, PlotId(__instance), -1);
	}

	[HarmonyPatch(typeof(SlimeFeeder), nameof(SlimeFeeder.SetFeederSpeed))]
	static class FeederSpeed
	{
		static void Postfix(SlimeFeeder __instance, SlimeFeeder.FeedSpeed speed) => Send(Op.FeederSpeed, PlotId(__instance), (int)speed);
	}

	// ---------- silos ----------

	// InitAmmo runs from Awake and InitModel, so storages under inactive roots are tracked too.
	[HarmonyPatch(typeof(SiloStorage), "InitAmmo")]
	static class TrackSilo
	{
		static void Postfix(SiloStorage __instance)
		{
			var a = __instance.LocalAmmo;
			if (a == null) return;
			silos.Remove(a);
			silos.Add(a, __instance);
		}
	}

	// Every Ammo mutator (others funnel into these): catchers, collectors, feeders, drones,
	// fast-forward. Host: mark for SiloState. Client: remember pre-edit contents for SiloOp.
	[HarmonyPatch]
	static class AmmoChanged
	{
		static IEnumerable<MethodBase> TargetMethods()
		{
			var t = typeof(Ammo);
			yield return AccessTools.Method(t, nameof(Ammo.DecrementSelectedAmmo));
			yield return AccessTools.Method(t, nameof(Ammo.Decrement), new[] { typeof(int), typeof(int) });
			yield return AccessTools.Method(t, nameof(Ammo.Clear), new[] { typeof(int) });
			yield return AccessTools.Method(t, nameof(Ammo.MaybeAddToSpecificSlot), new[] { typeof(Identifiable.Id), typeof(Identifiable), typeof(int), typeof(int), typeof(bool) });
			yield return AccessTools.Method(t, nameof(Ammo.MaybeAddToSlot), new[] { typeof(Identifiable.Id), typeof(Identifiable) });
			yield return AccessTools.Method(t, nameof(Ammo.Replace), new[] { typeof(Identifiable.Id), typeof(Identifiable.Id) });
			yield return AccessTools.Method(t, nameof(Ammo.ReplaceWithQuicksilverAmmo));
		}

		static void Prefix(Ammo __instance)
		{
			if (!Live || Session.IsHost || baseline.ContainsKey(__instance) || !silos.TryGetValue(__instance, out _)) return;
			var snap = Snap(__instance);
			if (snap == null) return;
			Wake();
			baseline[__instance] = snap;
		}

		static void Postfix(Ammo __instance)
		{
			if (Live && Session.IsHost && silos.TryGetValue(__instance, out var s)) MarkSilo(s);
		}
	}

	// SiloCatcher.OnTriggerStay is ForceJoint's only caller: the ejected actor is on its way to the vac.
	[HarmonyPatch(typeof(WeaponVacuum), nameof(WeaponVacuum.ForceJoint))]
	static class SiloEject
	{
		static void Postfix(Vacuumable vacuumable)
		{
			if (!Live || Session.IsHost || !vacuumable) return;
			ejected.RemoveAll(go => !go);
			ejected.Add(vacuumable.gameObject);
		}
	}

	[HarmonyPatch(typeof(FillableAshSource), nameof(FillableAshSource.AddAsh))]
	static class AddAsh
	{
		static void Prefix(FillableAshSource __instance, out float __state) => __state = ashModel(__instance)?.ashUnits ?? 0f;

		static void Postfix(FillableAshSource __instance, float __state)
		{
			if (!Live) return;
			if (Session.IsHost)
			{
				MarkAsh(__instance);
				return;
			}
			var m = ashModel(__instance);
			if (m == null) return;
			Wake();
			ashDelta.TryGetValue(__instance, out var d);
			ashDelta[__instance] = d + m.ashUnits - __state;
		}
	}

	// Only host-simulated slimes eat ash.
	[HarmonyPatch(typeof(FillableAshSource), nameof(FillableAshSource.ConsumeAsh))]
	static class ConsumeAsh
	{
		static bool Prefix() => !Puppet;

		static void Postfix(FillableAshSource __instance)
		{
			if (Live && Session.IsHost) MarkAsh(__instance);
		}
	}

	// ---------- decorizer + glitch (Viktor) storage ----------

	[HarmonyPatch(typeof(DecorizerModel), nameof(DecorizerModel.Add))]
	static class DecorAdd
	{
		static void Postfix(Identifiable.Id id, bool __result) => DecorChanged(id, 1, __result);
	}

	[HarmonyPatch(typeof(DecorizerModel), nameof(DecorizerModel.Remove))]
	static class DecorRemove
	{
		static void Postfix(Identifiable.Id id, bool __result) => DecorChanged(id, -1, __result);
	}

	static void DecorChanged(Identifiable.Id id, int d, bool ok)
	{
		if (!ok || !Live) return;
		if (Session.IsHost) MarkDecor();
		else SendStorage(DecorDelta, w =>
		{
			w.Put((int)id);
			w.Put(d);
		});
	}

	[HarmonyPatch(typeof(DecorizerStorage), nameof(DecorizerStorage.selected), MethodType.Setter)]
	static class DecorSelected
	{
		static void Postfix(DecorizerStorage __instance, Identifiable.Id value)
		{
			if (!Live) return;
			if (Session.IsHost) MarkDecor();
			else SendStorage(DecorSelect, w =>
			{
				w.Put(__instance.id);
				w.Put((int)value);
			});
		}
	}

	// Cleanup pulls loose actors out of the cell; on a client those are host puppets.
	[HarmonyPatch(typeof(DecorizerStorage), nameof(DecorizerStorage.Cleanup))]
	static class DecorCleanupRoute
	{
		static bool Prefix(DecorizerStorage __instance, IEnumerable<Identifiable.Id> ids)
		{
			if (!Puppet) return true;
			if (Live)
			{
				var list = ids.ToList();
				SendStorage(DecorCleanup, w =>
				{
					w.Put(__instance.id);
					w.Put((ushort)list.Count);
					foreach (var id in list) w.Put((int)id);
				});
			}
			return false;
		}
	}

	[HarmonyPatch(typeof(GlitchStorage), nameof(GlitchStorage.Add))]
	static class GlitchAdd
	{
		static void Postfix(GlitchStorage __instance, Identifiable.Id id, bool __result) => GlitchChanged(__instance, id, 1, __result);
	}

	[HarmonyPatch(typeof(GlitchStorage), nameof(GlitchStorage.Remove))]
	static class GlitchRemove
	{
		static void Prefix(GlitchStorage __instance, out Identifiable.Id __state) => __state = __instance.selected;

		static void Postfix(GlitchStorage __instance, Identifiable.Id __state, bool __result) => GlitchChanged(__instance, __state, -1, __result);
	}

	static void GlitchChanged(GlitchStorage s, Identifiable.Id id, int d, bool ok)
	{
		if (!ok || !Live) return;
		var sid = s.id;
		if (Session.IsHost) MarkGlitch(sid);
		else SendStorage(GlitchDelta, w =>
		{
			w.Put(sid);
			w.Put((int)id);
			w.Put(d);
		});
	}

	// ---------- gadgets ----------

	[HarmonyPatch(typeof(GameModel), "InstantiateGadget", typeof(GameObject), typeof(GadgetSiteModel), typeof(bool))]
	static class GadgetPlace
	{
		static void Postfix(GameObject __result, bool skipNotify)
		{
			if (skipNotify || !Live || !(__result.GetComponent<Gadget>() is { } g)) return;
			Wake();
			dirtyGadgets.Add(g);
		}
	}

	[HarmonyPatch(typeof(Gadget), nameof(Gadget.AddRotation))]
	static class GadgetRotate
	{
		static void Postfix(Gadget __instance)
		{
			if (!Live || !__instance.GetComponentInParent<GadgetSite>()) return;
			Wake();
			dirtyGadgets.Add(__instance);
		}
	}

	[HarmonyPatch(typeof(GameModel), nameof(GameModel.DestroyGadgetModel))]
	static class GadgetRemove
	{
		static void Postfix(string siteId, GameObject gameObj)
		{
			if (!Live) return;
			if (gameObj && gameObj.GetComponent<Gadget>() is { } g) dirtyGadgets.Remove(g);
			var w = Packets.Begin(Op.GadgetRemove);
			w.Put(siteId);
			Session.Distribute(w, R);
		}
	}

	[HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.AddToRefinery), typeof(Identifiable.Id), typeof(int), typeof(bool))]
	static class RefineryAdd
	{
		static void Postfix(Identifiable.Id id, int __result)
		{
			if (Live && __result > 0) SendRefinery(id, __result);
		}
	}

	// Client: Fabricate asks the host first and reruns with its verdict standing in for the refinery spend.
	[HarmonyPatch(typeof(FabricateGadgetUI), nameof(FabricateGadgetUI.Fabricate))]
	static class Fabricator
	{
		internal static bool? verdict;
		static FabricateGadgetUI ui;
		static float askedAt = -10;

		static bool Prefix(FabricateGadgetUI __instance, Gadget.Id id)
		{
			if (!Live || !Session.IsClient || verdict != null) return true;
			var gd = SRSingleton<SceneContext>.Instance.GadgetDirector;
			if (!gd.CanAddGadget(SRSingleton<GameContext>.Instance.LookupDirector.GetGadgetDefinition(id))) return true; // the game shows its own error
			if (Time.unscaledTime - askedAt < 5f) return false; // one craft in flight
			askedAt = Time.unscaledTime;
			ui = __instance;
			Session.SendToHost(Packets.Begin(Op.Craft).Also(w => w.Put((int)id)), R);
			return false;
		}

		internal static void Answer(Gadget.Id id, bool ok)
		{
			askedAt = -10;
			// Not under Apply: the gadget-count deltas must reach the host.
			if (!ui)
			{
				if (!ok) return;
				var gd = SRSingleton<SceneContext>.Instance.GadgetDirector;
				gd.AddGadget(id);
				if (SRSingleton<GameContext>.Instance.LookupDirector.GetGadgetDefinition(id).buyInPairs) gd.AddGadget(id);
				return;
			}
			verdict = ok;
			try { ui.Fabricate(id); } // purchase or "insufficient resources" cue, FX, rebuild
			finally { verdict = null; }
		}
	}

	[HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.TryToSpendFromRefinery))]
	static class RefinerySpend
	{
		static bool Prefix(ref bool __result)
		{
			if (Fabricator.verdict == null) return true;
			__result = Fabricator.verdict.Value; // the host already spent
			return false;
		}

		static void Postfix(GadgetDefinition.CraftCost[] costs, bool __result)
		{
			if (!Live || !__result || Fabricator.verdict != null) return;
			foreach (var c in costs)
				if (c.amount > 0) SendRefinery(c.id, -c.amount);
		}
	}

	// ---------- consumers: only the actor's owner consumes it ----------

	[HarmonyPatch(typeof(SiloCatcher), nameof(SiloCatcher.OnTriggerEnter))]
	static class SiloCatcherOwner
	{
		static bool Prefix(Collider collider) => !ActorSync.IsRemoteOwned(collider.gameObject);
	}

	[HarmonyPatch(typeof(GardenCatcher), nameof(GardenCatcher.OnTriggerEnter))]
	static class GardenCatcherOwner
	{
		static bool Prefix(Collider col) => !ActorSync.IsRemoteOwned(col.gameObject);
	}

	[HarmonyPatch(typeof(Incinerate), "OnCollisionEnter")]
	static class IncinerateOwner
	{
		static bool Prefix(Collision col) => !ActorSync.IsRemoteOwned(col.gameObject);
	}

	// ---------- plort collectors: host collects, clients mirror the FX ----------

	static class Collector
	{
		internal static readonly AccessTools.FieldRef<PlortCollector, double> forceUntil = AccessTools.FieldRefAccess<PlortCollector, double>("forceCollectUntil");
		internal static readonly AccessTools.FieldRef<PlortCollector, TimeDirector> timeDir = AccessTools.FieldRefAccess<PlortCollector, TimeDirector>("timeDir");
		internal static readonly AccessTools.FieldRef<PlortCollector, SECTR_AudioSource> vacAudio = AccessTools.FieldRefAccess<PlortCollector, SECTR_AudioSource>("vacAudio");
		internal static readonly AccessTools.FieldRef<PlortCollector, int> animId = AccessTools.FieldRefAccess<PlortCollector, int>("animCycloneActiveId");
		internal static readonly AccessTools.FieldRef<PlortCollector, SiloStorage> storage = AccessTools.FieldRefAccess<PlortCollector, SiloStorage>("storage");
		static readonly FieldInfo joints = AccessTools.Field(typeof(PlortCollector), "joints");
		static readonly Type jointRef = AccessTools.Inner(typeof(PlortCollector), "JointReference");
		static readonly FieldInfo jointField = AccessTools.Field(jointRef, "joint");
		static readonly MethodInfo destroy = AccessTools.Method(jointRef, "Destroy");
		// Animator lives in UnityEngine.AnimationModule, which the project does not reference.
		static readonly FieldInfo anim = AccessTools.Field(typeof(PlortCollector), "collectAnim");
		static readonly MethodInfo setBool = AccessTools.Method(anim.FieldType, "SetBool", new[] { typeof(int), typeof(bool) });

		internal static int Joints(PlortCollector c) => ((IList)joints.GetValue(c)).Count;

		// Joints on actors another machine simulates are dropped before they can be stored.
		internal static void DropRemote(PlortCollector c)
		{
			var list = (IList)joints.GetValue(c);
			for (var i = list.Count - 1; i >= 0; i--)
			{
				var body = (jointField.GetValue(list[i]) as SpringJoint)?.connectedBody;
				if (body == null || !ActorSync.IsRemoteOwned(body.gameObject)) continue;
				destroy.Invoke(list[i], null);
				list.RemoveAt(i);
			}
		}

		internal static int Stored(PlortCollector c)
		{
			var slots = storage(c) is { } s && s ? Slots(s.GetRelevantAmmo()) : null;
			return slots?.Sum(x => x?.count ?? 0) ?? 0;
		}

		// Client: Update's cyclone anim + vac audio, driven by forceCollectUntil from PullStart.
		internal static void Visual(PlortCollector c)
		{
			var td = timeDir(c);
			if (td == null) return;
			var on = !td.HasReached(forceUntil(c));
			if (anim.GetValue(c) is UnityEngine.Object an && an && setBool != null) setBool.Invoke(an, new object[] { animId(c), on });
			var a = vacAudio(c);
			if (a == null) return;
			if (on && !a.IsPlaying) a.Play();
			else if (!on && a.IsPlaying) a.Stop(false);
		}
	}

	[HarmonyPatch(typeof(PlortCollector), nameof(PlortCollector.Update))]
	static class CollectorUpdate
	{
		static bool Prefix(PlortCollector __instance, out int __state)
		{
			__state = 0;
			if (Puppet)
			{
				Collector.Visual(__instance);
				return false;
			}
			Collector.DropRemote(__instance);
			if (Live) __state = Collector.Stored(__instance);
			return true;
		}

		static void Postfix(PlortCollector __instance, int __state)
		{
			if (Puppet) return;
			Collector.DropRemote(__instance);
			if (!Live) return;
			var n = Collector.Stored(__instance) - __state;
			if (n > 0) SendPull(__instance, PullStored, n);
		}
	}

	[HarmonyPatch(typeof(PlortCollector), "DoCollection")]
	static class CollectorCollect
	{
		static bool Prefix() => !Puppet;

		static void Postfix(PlortCollector __instance)
		{
			if (Puppet) return;
			Collector.DropRemote(__instance);
			if (Live) SendPull(__instance, PullStart, Collector.Joints(__instance));
		}
	}

	// Collector button on a client: ask the host to run it.
	[HarmonyPatch(typeof(PlortCollector), nameof(PlortCollector.StartCollection))]
	static class CollectorButton
	{
		static bool Prefix(PlortCollector __instance)
		{
			if (!Puppet) return true;
			if (Live) SendPull(__instance, PullRequest, 0);
			return false;
		}
	}

	// ---------- client: ranch automation runs on the host only ----------

	[HarmonyPatch]
	static class HostOnlyAutomation
	{
		static IEnumerable<MethodBase> TargetMethods()
		{
			yield return AccessTools.Method(typeof(SlimeFeeder), nameof(SlimeFeeder.Update));
			yield return AccessTools.Method(typeof(SlimeFeeder), "ProcessFeedOperation");
			yield return AccessTools.Method(typeof(PlortCollector), nameof(PlortCollector.FastForward));
			yield return AccessTools.Method(typeof(RanchCellFastForwarder), nameof(RanchCellFastForwarder.Update));
			yield return AccessTools.Method(typeof(RanchCellFastForwarder), "OnHibernationStateChanged");
			yield return AccessTools.Method(typeof(RanchCellFastForwarder), "OnFastForwardChanged");
			yield return AccessTools.Method(typeof(DroneSubbehaviourPlexer), nameof(DroneSubbehaviourPlexer.RegistryFixedUpdate));
			yield return AccessTools.Method(typeof(DroneSubbehaviourPlexer), nameof(DroneSubbehaviourPlexer.OnFastForward));
		}

		static bool Prefix() => !Puppet;
	}
}
