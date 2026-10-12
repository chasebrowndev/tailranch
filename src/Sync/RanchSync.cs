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

public static class RanchSync
{
	const DeliveryMethod R = DeliveryMethod.ReliableOrdered;
	// SiloState storageType for the incinerator ash trough (LandPlotModel.ashUnits, one float).
	const int AshStorage = -1;

	static readonly AccessTools.FieldRef<LandPlotModel, GameObject> plotObj = AccessTools.FieldRefAccess<LandPlotModel, GameObject>("gameObj");
	static readonly MethodInfo updateAsh = AccessTools.Method(typeof(FillableAshSource), "UpdateAshPosition");

	// Every silo-like Ammo → its owning storage; player ammo never lands here.
	static readonly ConditionalWeakTable<Ammo, SiloStorage> silos = new();
	static readonly HashSet<SiloStorage> dirtySilos = new();
	static readonly HashSet<FillableAshSource> dirtyAsh = new();
	static readonly HashSet<Gadget> dirtyGadgets = new();

	static GameModel Game => SRSingleton<SceneContext>.Instance.GameModel;
	static bool Live => EconomySync.Live;
	static bool Puppet => Session.InRemoteWorld;

	public static void Register()
	{
		if (Plugin.I) Plugin.I.gameObject.AddComponent<Pump>();

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
			var plot = Plot(r.GetString())?.GetComponentInChildren<LandPlot>();
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

		On(Op.SiloState, r =>
		{
			var id = r.GetString();
			var type = r.GetInt();
			if (type == AshStorage)
			{
				var units = r.GetFloat();
				var m = Game.GetLandPlotModel(id);
				var ash = Plot(id)?.GetComponentInChildren<FillableAshSource>(true);
				if (m == null) return;
				m.ashUnits = units;
				if (ash) updateAsh.Invoke(ash, null);
				return;
			}
			var data = ReadAmmo(r);
			var ammo = FindSilo(id, type)?.LocalAmmo;
			if (ammo == null) return;
			ammo.Clear();
			ammo.FromSerializable(data);
		});

		On(Op.FeederSpeed, r =>
		{
			var feeder = Plot(r.GetString())?.GetComponentInChildren<SlimeFeeder>(true);
			var speed = (SlimeFeeder.FeedSpeed)r.GetInt();
			if (feeder) feeder.SetFeederSpeed(speed);
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
		return m == null ? null : plotObj(m);
	}

	static string PlotId(Component c) => c.GetComponentInParent<LandPlotLocation>()?.id;

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

	// ---------- silo contents (AmmoDataV02: id, count, emotions) ----------

	static void WriteAmmo(NetDataWriter w, List<AmmoDataV02> data)
	{
		w.Put((byte)data.Count);
		foreach (var d in data)
		{
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

	static void Flush()
	{
		if (!Live || !Session.InWorld)
		{
			dirtySilos.Clear();
			dirtyAsh.Clear();
			dirtyGadgets.Clear();
			return;
		}
		foreach (var s in dirtySilos)
		{
			if (!s) continue;
			var id = s is LinkedSiloStorage ? s.GetComponentInParent<GadgetSite>()?.id : PlotId(s);
			if (id == null) continue;
			var w = Packets.Begin(Op.SiloState);
			w.Put(id);
			w.Put((int)s.type);
			WriteAmmo(w, s.LocalAmmo.ToSerializable());
			Session.Distribute(w, R);
		}
		dirtySilos.Clear();
		foreach (var a in dirtyAsh)
		{
			var id = a ? PlotId(a) : null;
			var m = id == null ? null : Game.GetLandPlotModel(id);
			if (m == null) continue;
			var w = Packets.Begin(Op.SiloState);
			w.Put(id);
			w.Put(AshStorage);
			w.Put(m.ashUnits);
			Session.Distribute(w, R);
		}
		dirtyAsh.Clear();
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

	sealed class Pump : MonoBehaviour
	{
		void LateUpdate() => Flush();
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

	[HarmonyPatch(typeof(SiloStorage), nameof(SiloStorage.Awake))]
	static class TrackSilo
	{
		static void Postfix(SiloStorage __instance)
		{
			silos.Remove(__instance.LocalAmmo);
			silos.Add(__instance.LocalAmmo, __instance);
		}
	}

	// Every Ammo mutator (others funnel into these); silo catchers, collectors, feeders,
	// drones and fast-forward all end up here.
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

		static void Postfix(Ammo __instance)
		{
			if (Live && silos.TryGetValue(__instance, out var s)) dirtySilos.Add(s);
		}
	}

	[HarmonyPatch(typeof(FillableAshSource), nameof(FillableAshSource.AddAsh))]
	static class AddAsh
	{
		static void Postfix(FillableAshSource __instance)
		{
			if (Live) dirtyAsh.Add(__instance);
		}
	}

	[HarmonyPatch(typeof(FillableAshSource), nameof(FillableAshSource.ConsumeAsh))]
	static class ConsumeAsh
	{
		static void Postfix(FillableAshSource __instance)
		{
			if (Live) dirtyAsh.Add(__instance);
		}
	}

	// ---------- gadgets ----------

	[HarmonyPatch(typeof(GameModel), "InstantiateGadget", typeof(GameObject), typeof(GadgetSiteModel), typeof(bool))]
	static class GadgetPlace
	{
		static void Postfix(GameObject __result, bool skipNotify)
		{
			if (!skipNotify && Live && __result.GetComponent<Gadget>() is { } g) dirtyGadgets.Add(g);
		}
	}

	[HarmonyPatch(typeof(Gadget), nameof(Gadget.AddRotation))]
	static class GadgetRotate
	{
		static void Postfix(Gadget __instance)
		{
			if (Live && __instance.GetComponentInParent<GadgetSite>()) dirtyGadgets.Add(__instance);
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

	[HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.TryToSpendFromRefinery))]
	static class RefinerySpend
	{
		static void Postfix(GadgetDefinition.CraftCost[] costs, bool __result)
		{
			if (!Live || !__result) return;
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

	// Collector joints on actors another machine simulates are dropped before they can be stored.
	[HarmonyPatch]
	static class CollectorOwner
	{
		static readonly FieldInfo joints = AccessTools.Field(typeof(PlortCollector), "joints");
		static readonly Type jointRef = AccessTools.Inner(typeof(PlortCollector), "JointReference");
		static readonly FieldInfo jointField = AccessTools.Field(jointRef, "joint");
		static readonly MethodInfo destroy = AccessTools.Method(jointRef, "Destroy");

		static IEnumerable<MethodBase> TargetMethods()
		{
			yield return AccessTools.Method(typeof(PlortCollector), nameof(PlortCollector.Update));
			yield return AccessTools.Method(typeof(PlortCollector), "DoCollection");
		}

		static bool Prefix(PlortCollector __instance, MethodBase __originalMethod)
		{
			if (Puppet) return false;
			if (__originalMethod.Name == nameof(PlortCollector.Update)) DropRemote(__instance);
			return true;
		}

		static void Postfix(PlortCollector __instance) => DropRemote(__instance);

		static void DropRemote(PlortCollector c)
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
