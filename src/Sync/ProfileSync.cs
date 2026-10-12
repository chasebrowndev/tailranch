using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MonomiPark.SlimeRancher.DataModel;
using TailRanch.Net;

namespace TailRanch.Sync;

// Shared progression: the ranch is shared, so personal upgrades, blueprints, gadget stock,
// slimepedia, map nodes and mail are world state. Op.Profile = kind byte, then:
//   Upgrade        *    upgrade int                       (bought: everyone has it)
//   AvailUpgrade   *    upgrade int                       (lock expired: purchasable)
//   Blueprint      *    gadgetId int
//   AvailBlueprint *    gadgetId int
//   Gadget         c→h  gadgetId int, delta int; h→c gadgetId int, total int
//   Pedia          *    count ushort, then pediaId int each
//   Map            *    zone int
//   Mail           c→h  type byte, key string (request); h→c same (host sent it)
// "*": the acting machine applies locally, host relays, receivers replay under Apply.
// Currency for purchases rides EconomySync; refinery spending rides RanchSync.
// Avail sets change inside PlayerState/GadgetDirector.Update loops, so Tick diffs them and
// both sides union (client-local unlock conditions like fabricated count / read mail count too).
// Mail is host-only: clients never add mail themselves; they forward and take the host's.
public static class ProfileSync
{
	enum Kind : byte { Upgrade, AvailUpgrade, Blueprint, AvailBlueprint, Gadget, Pedia, Map, Mail }

	const DeliveryMethod R = DeliveryMethod.ReliableOrdered;

	static readonly Type upgradePopup = AccessTools.Inner(typeof(PlayerState), "AvailUpgradePopupCreator");
	static readonly Type blueprintPopup = AccessTools.Inner(typeof(GadgetDirector), "AvailBlueprintPopupCreator");

	// Avail sets as last seen, per loaded model.
	static PlayerModel seenPlayer;
	static GadgetsModel seenGadgets;
	static readonly HashSet<PlayerState.Upgrade> knownAvailUp = new();
	static readonly HashSet<Gadget.Id> knownAvailBp = new();

	static bool Live => EconomySync.Live;
	static SceneContext Ctx => SRSingleton<SceneContext>.Instance;

	public static void Register()
	{
		Session.On(Op.Profile, (from, r) =>
		{
			var kind = (Kind)r.GetByte();
			if (kind == Kind.Gadget) { OnGadget(r); return; }
			if (kind == Kind.Mail) { OnMail(r); return; }
			if (Session.IsHost) Session.Relay(from, r, R);
			if (!Session.InWorld) return;
			Seen();
			using (Session.Apply()) Replay(kind, r);
		});
	}

	public static void Tick()
	{
		if (!Session.InWorld)
		{
			seenPlayer = null;
			seenGadgets = null;
			return;
		}
		Seen();
		if (seenPlayer.availUpgrades.Count != knownAvailUp.Count) Diff(seenPlayer.availUpgrades, knownAvailUp, Kind.AvailUpgrade);
		if (seenGadgets.availBlueprints.Count != knownAvailBp.Count) Diff(seenGadgets.availBlueprints, knownAvailBp, Kind.AvailBlueprint);
	}

	// New model (load/join): adopt its sets silently.
	static void Seen()
	{
		var gm = Ctx.GameModel;
		var pm = gm.GetPlayerModel();
		var gd = gm.GetGadgetsModel();
		if (pm != seenPlayer)
		{
			seenPlayer = pm;
			knownAvailUp.Clear();
			knownAvailUp.UnionWith(pm.availUpgrades);
		}
		if (gd != seenGadgets)
		{
			seenGadgets = gd;
			knownAvailBp.Clear();
			knownAvailBp.UnionWith(gd.availBlueprints);
		}
	}

	static void Diff<T>(ICollection<T> live, HashSet<T> known, Kind kind)
	{
		foreach (var x in live)
			if (known.Add(x) && Live) Send(kind, Convert.ToInt32(x));
		if (known.Count != live.Count)
		{
			known.Clear();
			known.UnionWith(live);
		}
	}

	static void Replay(Kind kind, NetPacketReader r)
	{
		var ctx = Ctx;
		switch (kind)
		{
			case Kind.Upgrade:
			{
				var u = (PlayerState.Upgrade)r.GetInt();
				if (ctx.PlayerState.HasUpgrade(u)) return;
				// The buyer's SPARE_KEY key went through PlayerModel.AddKey, invisible to EconomySync:
				// the host grants its own copy (matching the buyer) and pushes the total.
				var key = Session.IsHost && u == PlayerState.Upgrade.SPARE_KEY;
				ctx.PlayerState.AddUpgrade(u, key);
				if (key) BroadcastKeys();
				return;
			}
			case Kind.AvailUpgrade:
			{
				var u = (PlayerState.Upgrade)r.GetInt();
				var m = seenPlayer;
				m.upgradeLocks.Remove(u);
				if (m.upgrades.Contains(u) || m.availUpgrades.Contains(u)) return;
				m.availUpgrades.Add(u);
				knownAvailUp.Add(u);
				Popup(upgradePopup, u);
				return;
			}
			case Kind.Blueprint:
				ctx.GadgetDirector.AddBlueprint((Gadget.Id)r.GetInt());
				return;
			case Kind.AvailBlueprint:
			{
				var g = (Gadget.Id)r.GetInt();
				var m = seenGadgets;
				ctx.GadgetDirector.blueprintLocks.Remove(g);
				m.blueprintLockData.Remove(g);
				if (!m.availBlueprints.Add(g)) return;
				knownAvailBp.Add(g);
				// Viktor rewards grant the blueprint outright and never show this popup.
				if (!m.blueprints.Contains(g)) Popup(blueprintPopup, g);
				return;
			}
			case Kind.Pedia:
			{
				var ids = new PediaDirector.Id[r.GetUShort()];
				for (var i = 0; i < ids.Length; i++) ids[i] = (PediaDirector.Id)r.GetInt();
				ctx.PediaDirector.Unlock(ids); // no popup: that's for whoever found it
				return;
			}
			case Kind.Map:
				ctx.PlayerState.UnlockMap((ZoneDirector.Zone)r.GetInt());
				return;
		}
	}

	static void Popup(Type creator, object id)
	{
		if (creator == null) return;
		Ctx.PopupDirector.QueueForPopup((PopupDirector.PopupCreator)Activator.CreateInstance(creator, id));
	}

	static NetDataWriter Begin(Kind k)
	{
		var w = Packets.Begin(Op.Profile);
		w.Put((byte)k);
		return w;
	}

	static void Send(Kind k, int v)
	{
		var w = Begin(k);
		w.Put(v);
		Session.Distribute(w, R);
	}

	static void BroadcastKeys() =>
		Session.Broadcast(Packets.Begin(Op.Keys).Also(w => w.Put(Ctx.PlayerState.GetKeys())), R);

	// ---------- gadget stock (host-owned totals, like the wallet) ----------

	static void OnGadget(NetPacketReader r)
	{
		var id = (Gadget.Id)r.GetInt();
		var v = r.GetInt();
		if (!Session.InWorld) return;
		var counts = Ctx.GameModel.GetGadgetsModel().gadgets;
		var total = Session.IsHost ? Math.Max(0, counts.Get(id) + v) : v;
		if (total > 0) counts[id] = total;
		else counts.Remove(id);
		if (Session.IsHost) SendGadget(id, 0);
	}

	// Client: delta to host. Host: current total to everyone.
	static void SendGadget(Gadget.Id id, int delta)
	{
		var w = Begin(Kind.Gadget);
		w.Put((int)id);
		if (Session.IsHost)
		{
			w.Put(Ctx.GameModel.GetGadgetsModel().gadgets.Get(id));
			Session.Broadcast(w, R);
		}
		else
		{
			w.Put(delta);
			Session.SendToHost(w, R);
		}
	}

	// Fabricate (AddGadget), place (SpendGadget), pick up (AddGadget), RemoveGadget.
	[HarmonyPatch]
	static class GadgetCount
	{
		static IEnumerable<MethodBase> TargetMethods() =>
			new[] { nameof(GadgetDirector.AddGadget), nameof(GadgetDirector.SpendGadget), nameof(GadgetDirector.RemoveGadget) }
				.Select(n => AccessTools.Method(typeof(GadgetDirector), n, new[] { typeof(Gadget.Id) }));

		static void Prefix(GadgetDirector __instance, Gadget.Id gadget, out int __state) => __state = __instance.GetGadgetCount(gadget);

		static void Postfix(GadgetDirector __instance, Gadget.Id gadget, int __state)
		{
			var d = __instance.GetGadgetCount(gadget) - __state;
			if (d != 0 && Live) SendGadget(gadget, d);
		}
	}

	// ---------- mail (host-only) ----------

	static NetDataWriter MailPacket(MailDirector.Type type, string key)
	{
		var w = Begin(Kind.Mail);
		w.Put((byte)type);
		w.Put(key);
		return w;
	}

	static void OnMail(NetPacketReader r)
	{
		var type = (MailDirector.Type)r.GetByte();
		var key = r.GetString();
		if (!Session.InWorld) return;
		// Host: a client's tracker fired (e.g. it read the prerequisite mail); send for real, postfix broadcasts.
		if (Session.IsHost) Ctx.MailDirector.SendMail(type, key);
		else using (Session.Apply()) Ctx.MailDirector.SendMail(type, key);
	}

	// Covers SendMailIfExists, progress trackers, rancher intros, partner/secret-style timers.
	[HarmonyPatch(typeof(MailDirector), nameof(MailDirector.SendMail))]
	static class SendMail
	{
		static bool Prefix(MailDirector.Type type, string key, ref bool __result)
		{
			if (!Session.IsClient || Session.Applying) return true;
			if (Session.InRemoteWorld && !Ctx.GameModel.GetMailModel().allMailDict.ContainsKey(new MailDirector.Mail(type, key)))
				Session.SendToHost(MailPacket(type, key), R);
			__result = false;
			return false;
		}

		static void Postfix(MailDirector.Type type, string key, bool __result)
		{
			if (__result && Session.IsHost && !Session.Applying) Session.Broadcast(MailPacket(type, key), R);
		}
	}

	// ---------- upgrades, blueprints, pedia, maps ----------

	[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.AddUpgrade))]
	static class AddUpgrade
	{
		static void Prefix(PlayerState __instance, PlayerState.Upgrade upgrade, out bool __state) => __state = __instance.HasUpgrade(upgrade);

		static void Postfix(PlayerState.Upgrade upgrade, bool isFirstTime, bool __state)
		{
			if (__state || !Live) return;
			Send(Kind.Upgrade, (int)upgrade);
			if (Session.IsHost && isFirstTime && upgrade == PlayerState.Upgrade.SPARE_KEY) BroadcastKeys();
		}
	}

	[HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.AddBlueprint))]
	static class AddBlueprint
	{
		static void Postfix(Gadget.Id blueprint, bool __result)
		{
			if (__result && Live) Send(Kind.Blueprint, (int)blueprint);
		}
	}

	// Every unlock path (Unlock, UnlockWithoutPopup, UnlockScience, popup Create) lands here.
	[HarmonyPatch(typeof(PediaModel), nameof(PediaModel.Unlock))]
	static class PediaUnlock
	{
		static void Prefix(PediaModel __instance, PediaDirector.Id[] ids, out PediaDirector.Id[] __state) =>
			__state = Live && ids != null ? ids.Where(i => !__instance.unlocked.Contains(i)).Distinct().ToArray() : null;

		static void Postfix(PediaDirector.Id[] __state)
		{
			if (__state == null || __state.Length == 0) return;
			var w = Begin(Kind.Pedia);
			w.Put((ushort)__state.Length);
			foreach (var id in __state) w.Put((int)id);
			Session.Distribute(w, R);
		}
	}

	[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.UnlockMap))]
	static class UnlockMap
	{
		static void Prefix(PlayerState __instance, ZoneDirector.Zone zone, out bool __state) => __state = __instance.HasUnlockedMap(zone);

		static void Postfix(ZoneDirector.Zone zone, bool __state)
		{
			if (!__state && Live) Send(Kind.Map, (int)zone);
		}
	}
}
