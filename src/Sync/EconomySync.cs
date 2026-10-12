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

namespace TailRanch.Sync;

// Shared wallet + keys: clients send deltas, host owns the totals. Only the machine that
// earned/spent runs AddCurrency/SpendCurrency (popups, achievements, currencyEverCollected
// stay local stats); everyone else just gets PlayerModel.SetCurrency/SetKeys.
// Plort market + rancher exchange: host simulates (daily prices, offer generation), clients
// mirror its state and forward their player actions (sales, deposits, chosen offers).
public static class EconomySync
{
	const DeliveryMethod RO = DeliveryMethod.ReliableOrdered;
	const float MarketInterval = 0.5f;

	static int moneyDelta, keysDelta;
	static bool moneyDirty, keysDirty, marketDirty, exchangeDirty;
	static float nextMarket;

	static readonly AccessTools.FieldRef<AccessDoorModel, GameObject> doorObj = AccessTools.FieldRefAccess<AccessDoorModel, GameObject>("gameObj");
	static readonly AccessTools.FieldRef<ExchangeAcceptor, ExchangeDirector.Awarder[]> awardersOf = AccessTools.FieldRefAccess<ExchangeAcceptor, ExchangeDirector.Awarder[]>("awarders");
	static readonly FieldInfo currValueMap = AccessTools.Field(typeof(EconomyDirector), "currValueMap");
	static readonly Type currValueEntry = AccessTools.Inner(typeof(EconomyDirector), "CurrValueEntry");
	static readonly FieldInfo currValue = AccessTools.Field(currValueEntry, "currValue");
	static readonly FieldInfo prevValue = AccessTools.Field(currValueEntry, "prevValue");

	// Every acceptor ever woken, so the host can hand a client's deposit the right awarders.
	static readonly List<ExchangeAcceptor> acceptors = new();
	// Progress types inside AddProgress (sent as +1 deltas rather than values).
	static readonly HashSet<ProgressDirector.ProgressType> adding = new();
	// Client: the rancher progress SelectDailyOffer adds locally; the host adds it when it takes the offer.
	static ProgressDirector.ProgressType? creatingFor;

	// Local change worth broadcasting: not a replay, and (client) not during the world load.
	internal static bool Live => Session.Active && !Session.Applying && (Session.IsHost || Session.InRemoteWorld);

	// Client inside the borrowed world, acting locally (not replaying).
	static bool ClientLive => Session.IsClient && Session.InRemoteWorld && !Session.Applying;

	static SceneContext Ctx => SRSingleton<SceneContext>.Instance;
	static PlayerModel Wallet => Ctx.GameModel.GetPlayerModel();
	static WorldModel World => Ctx.GameModel.GetWorldModel();

	public static void Register()
	{
		if (Plugin.I) Plugin.I.gameObject.AddComponent<Pump>();

		// Money/Keys: c→h delta int; h→c total int.
		Session.On(Op.Money, (_, r) => OnTotal(r.GetInt(), ref moneyDirty, moneyDelta, m => m.currency, (m, v) => m.SetCurrency(v)));
		Session.On(Op.Keys, (_, r) => OnTotal(r.GetInt(), ref keysDirty, keysDelta, m => m.keys, (m, v) => m.SetKeys(v)));

		// Door: * doorId string, state int.
		Session.On(Op.Door, (from, r) =>
		{
			if (Session.IsHost) Session.Relay(from, r, RO);
			var id = r.GetString();
			var state = (AccessDoor.State)r.GetInt();
			if (!Session.InWorld || !Ctx.GameModel.AllDoors().TryGetValue(id, out var m)) return;
			var door = doorObj(m)?.GetComponent<AccessDoor>();
			if (door == null || door.CurrState == state) return;
			using (Session.Apply()) door.CurrState = state;
		});

		// Progress: c→h progressType int, value int, add bool (add: AddProgress delta; else a SetProgress
		// value, kept with Max since counters only grow); h→c progressType int, total int.
		Session.On(Op.Progress, (from, r) =>
		{
			var type = (ProgressDirector.ProgressType)r.GetInt();
			var value = r.GetInt();
			var add = Session.IsHost && r.GetBool();
			if (!Session.InWorld) return;
			var pd = Ctx.ProgressDirector;
			var cur = pd.GetProgress(type);
			var next = !Session.IsHost ? value : add ? cur + value : Math.Max(cur, value);
			if (next != cur) using (Session.Apply()) pd.SetProgress(type, next);
			if (Session.IsHost) Session.Broadcast(ProgressPacket(type), RO);
		});

		// MarketState:
		//   c→h  ident int, count int  (a sale on the client; host registers the saturation)
		//   h→c  count ushort, then per ident: ident int, currValue float, prevValue float, saturation float
		Session.On(Op.MarketState, (_, r) =>
		{
			if (!Session.InWorld) return;
			var econ = Ctx.EconomyDirector;
			if (Session.IsHost)
			{
				var id = (Identifiable.Id)r.GetInt();
				var count = r.GetInt();
				if (count <= 0 || !World.marketSaturation.ContainsKey(id)) return;
				using (Session.Apply()) econ.RegisterSold(id, count);
				marketDirty = true;
				return;
			}
			ReadMarket(r, econ);
		});

		// ExchangeState: kind byte, then
		//   0 Full    h→c  offerCount byte, per offer: type int + Offer; nextDailyOfferCreateTime double,
		//                  lastOfferRancherIds (count byte + strings), pendingOfferRancherIds (same)
		//   1 Accept  c→h  type int, ident int  (deposit into an acceptor; host awards)
		//   2 Put     c→h  type int + Offer     (offer the client created: chosen daily / progress offer)
		//   3 Clear   c→h  type int             (client-simulated crate spawned the rewards)
		// Offer: offerId string, rancherId string, expireTime double, earlyExchangeTime double,
		//   requests count byte (ident int, count int, progress int, specReward int),
		//   rewards count byte (ident int, count int, specReward int)
		Session.On(Op.ExchangeState, (from, r) =>
		{
			var kind = (ExchangeKind)r.GetByte();
			if (!Session.InWorld) return;
			var ed = Ctx.ExchangeDirector;
			if (!Session.IsHost)
			{
				if (kind == ExchangeKind.Full) ReadExchange(r, ed);
				return;
			}
			var type = (ExchangeDirector.OfferType)r.GetInt();
			switch (kind)
			{
				case ExchangeKind.Accept:
				{
					// Not under Apply: crate spawns and progress awards must broadcast from here.
					var id = (Identifiable.Id)r.GetInt();
					if (!ed.TryAccept(type, id, AwardersFor(type))) RanchSync.SendShortfall(from, id, 1); // refused: the client gets its item back
					break;
				}
				case ExchangeKind.Put:
					var offer = ReadOffer(r);
					var w = World;
					var rancher = type == ExchangeDirector.OfferType.GENERAL ? RancherProgress(offer.rancherId) : null;
					if (w.currOffers.ContainsKey(type))
					{
						// Refused: undo the rancher progress the sender added locally.
						if (rancher is { } rp) Session.SendTo(from, ProgressPacket(rp), RO);
						break;
					}
					w.currOffers[type] = offer;
					if (type == ExchangeDirector.OfferType.GENERAL) w.pendingOfferRancherIds.Clear();
					if (rancher is { } p) Ctx.ProgressDirector.AddProgress(p); // not under Apply: total broadcasts
					ed.onOfferChanged?.Invoke();
					exchangeDirty = true;
					break;
				case ExchangeKind.Clear:
					ed.RewardsDidSpawn(type);
					break;
			}
			exchangeDirty = true;
		});

		Session.PlayerReady += p =>
		{
			if (!Session.InWorld) return;
			Session.SendTo(p, Packets.Begin(Op.Money).Also(w => w.Put(Wallet.currency)), RO);
			Session.SendTo(p, Packets.Begin(Op.Keys).Also(w => w.Put(Wallet.keys)), RO);
			Session.SendTo(p, MarketPacket(), RO);
			Session.SendTo(p, ExchangePacket(), RO);
		};
	}

	enum ExchangeKind : byte { Full, Accept, Put, Clear }

	static NetDataWriter ProgressPacket(ProgressDirector.ProgressType type) =>
		Packets.Begin(Op.Progress).Also(w => { w.Put((int)type); w.Put(Ctx.ProgressDirector.GetProgress(type)); });

	static ProgressDirector.ProgressType? RancherProgress(string rancherId)
	{
		try { return string.IsNullOrEmpty(rancherId) ? null : ProgressDirector.GetRancherProgressType(rancherId); }
		catch (ArgumentException) { return null; }
	}

	// ---------- wallet ----------

	// Host: v is a client's delta (clamped at 0, then re-broadcast as a total). Client: v is the
	// total, plus whatever this frame earned/spent that Flush hasn't sent yet.
	static void OnTotal(int v, ref bool dirty, int unsent, Func<PlayerModel, int> get, Action<PlayerModel, int> set)
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
			else set(m, Math.Max(0, v + unsent));
		}
	}

	static void Changed(ref int delta, ref bool dirty, int adjust)
	{
		if (!Live) return;
		if (Session.IsHost) dirty = true;
		else delta += adjust;
	}

	// ---------- market ----------

	static NetDataWriter MarketPacket()
	{
		var w = Packets.Begin(Op.MarketState);
		var map = (IDictionary)currValueMap.GetValue(Ctx.EconomyDirector);
		var sat = World.marketSaturation;
		w.Put((ushort)map.Count);
		foreach (DictionaryEntry e in map)
		{
			var id = (Identifiable.Id)e.Key;
			w.Put((int)id);
			w.Put((float)currValue.GetValue(e.Value));
			w.Put((float)prevValue.GetValue(e.Value));
			w.Put(sat.TryGetValue(id, out var s) ? s : 0f);
		}
		return w;
	}

	static void ReadMarket(NetDataReader r, EconomyDirector econ)
	{
		var map = (IDictionary)currValueMap.GetValue(econ);
		var sat = World.marketSaturation;
		int n = r.GetUShort();
		for (int i = 0; i < n; i++)
		{
			var id = (Identifiable.Id)r.GetInt();
			float curr = r.GetFloat(), prev = r.GetFloat(), s = r.GetFloat();
			sat[id] = s;
			var entry = map[id];
			if (entry == null) continue;
			currValue.SetValue(entry, curr);
			prevValue.SetValue(entry, prev);
		}
		using (Session.Apply()) econ.didUpdateDelegate?.Invoke();
	}

	// ---------- exchange ----------

	static ExchangeDirector.Awarder[] AwardersFor(ExchangeDirector.OfferType type)
	{
		acceptors.RemoveAll(a => !a);
		var a = acceptors.FirstOrDefault(x => x.offerTypes != null && x.offerTypes.Contains(type));
		return (a ? awardersOf(a) : null) ?? new ExchangeDirector.Awarder[0];
	}

	static NetDataWriter ExchangePacket()
	{
		var w = Packets.Begin(Op.ExchangeState);
		var m = World;
		w.Put((byte)ExchangeKind.Full);
		w.Put((byte)m.currOffers.Count);
		foreach (var kv in m.currOffers)
		{
			w.Put((int)kv.Key);
			WriteOffer(w, kv.Value);
		}
		w.Put(m.nextDailyOfferCreateTime);
		WriteIds(w, m.lastOfferRancherIds);
		WriteIds(w, m.pendingOfferRancherIds);
		return w;
	}

	static void ReadExchange(NetDataReader r, ExchangeDirector ed)
	{
		var m = World;
		var offers = new Dictionary<ExchangeDirector.OfferType, ExchangeDirector.Offer>();
		int n = r.GetByte();
		for (int i = 0; i < n; i++)
		{
			var type = (ExchangeDirector.OfferType)r.GetInt();
			offers[type] = ReadOffer(r);
		}
		m.currOffers.Clear();
		foreach (var kv in offers) m.currOffers[kv.Key] = kv.Value;
		m.nextDailyOfferCreateTime = r.GetDouble();
		ReadIds(r, m.lastOfferRancherIds);
		ReadIds(r, m.pendingOfferRancherIds);
		using (Session.Apply()) ed.onOfferChanged?.Invoke();
	}

	static void WriteOffer(NetDataWriter w, ExchangeDirector.Offer o)
	{
		w.Put(o.offerId ?? "");
		w.Put(o.rancherId ?? "");
		w.Put(o.expireTime);
		w.Put(o.earlyExchangeTime);
		w.Put((byte)o.requests.Count);
		foreach (var q in o.requests)
		{
			w.Put((int)q.id);
			w.Put(q.count);
			w.Put(q.progress);
			w.Put((int)q.specReward);
		}
		w.Put((byte)o.rewards.Count);
		foreach (var e in o.rewards)
		{
			w.Put((int)e.id);
			w.Put(e.count);
			w.Put((int)e.specReward);
		}
	}

	static ExchangeDirector.Offer ReadOffer(NetDataReader r)
	{
		var offerId = r.GetString();
		var rancherId = r.GetString();
		var expire = r.GetDouble();
		var early = r.GetDouble();
		var requests = new List<ExchangeDirector.RequestedItemEntry>();
		int n = r.GetByte();
		for (int i = 0; i < n; i++)
		{
			var id = (Identifiable.Id)r.GetInt();
			int count = r.GetInt(), progress = r.GetInt();
			requests.Add(new ExchangeDirector.RequestedItemEntry(id, count, progress, (ExchangeDirector.NonIdentReward)r.GetInt()));
		}
		var rewards = new List<ExchangeDirector.ItemEntry>();
		n = r.GetByte();
		for (int i = 0; i < n; i++)
		{
			var id = (Identifiable.Id)r.GetInt();
			var count = r.GetInt();
			rewards.Add(new ExchangeDirector.ItemEntry(id, count, (ExchangeDirector.NonIdentReward)r.GetInt()));
		}
		return new ExchangeDirector.Offer(offerId, rancherId, expire, early, requests, rewards);
	}

	static void WriteIds(NetDataWriter w, List<string> ids)
	{
		w.Put((byte)ids.Count);
		foreach (var id in ids) w.Put(id ?? "");
	}

	static void ReadIds(NetDataReader r, List<string> into)
	{
		into.Clear();
		int n = r.GetByte();
		for (int i = 0; i < n; i++) into.Add(r.GetString());
	}

	static void SendExchange(ExchangeKind kind, ExchangeDirector.OfferType type, Action<NetDataWriter> body = null)
	{
		var w = Packets.Begin(Op.ExchangeState);
		w.Put((byte)kind);
		w.Put((int)type);
		body?.Invoke(w);
		Session.SendToHost(w, RO);
	}

	// ---------- pump ----------

	static void Flush()
	{
		if (!Session.Active || !Session.InWorld || (Session.IsClient && !Session.InRemoteWorld))
		{
			moneyDelta = keysDelta = 0;
			moneyDirty = keysDirty = marketDirty = exchangeDirty = false;
			return;
		}
		if (Session.IsHost)
		{
			if (moneyDirty) Session.Broadcast(Packets.Begin(Op.Money).Also(w => w.Put(Wallet.currency)), RO);
			if (keysDirty) Session.Broadcast(Packets.Begin(Op.Keys).Also(w => w.Put(Wallet.keys)), RO);
			if (exchangeDirty) Session.Broadcast(ExchangePacket(), RO);
			if (marketDirty && Time.unscaledTime >= nextMarket)
			{
				Session.Broadcast(MarketPacket(), RO);
				nextMarket = Time.unscaledTime + MarketInterval;
				marketDirty = false;
			}
			moneyDirty = keysDirty = exchangeDirty = false;
			return;
		}
		if (moneyDelta != 0) Session.SendToHost(Packets.Begin(Op.Money).Also(w => w.Put(moneyDelta)), RO);
		if (keysDelta != 0) Session.SendToHost(Packets.Begin(Op.Keys).Also(w => w.Put(keysDelta)), RO);
		moneyDelta = keysDelta = 0;
	}

	sealed class Pump : MonoBehaviour
	{
		void LateUpdate() => Flush();
	}

	// ---------- wallet / door / progress hooks ----------

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
			Session.Distribute(w, RO);
		}
	}

	[HarmonyPatch(typeof(ProgressDirector), nameof(ProgressDirector.AddProgress))]
	static class ProgressAdd
	{
		static void Prefix(ProgressDirector.ProgressType type, out bool __state) => __state = adding.Add(type);

		static void Finalizer(ProgressDirector.ProgressType type, bool __state)
		{
			if (__state) adding.Remove(type);
		}
	}

	// SetProgress/SetUniqueProgress/AddProgress all funnel here. Host: total. Client: +1 from
	// AddProgress (concurrent adds both count), else the value.
	[HarmonyPatch(typeof(ProgressDirector), "NoteProgressChanged")]
	static class ProgressChanged
	{
		static void Postfix(ProgressDirector __instance, ProgressDirector.ProgressType type)
		{
			if (!Live) return;
			if (Session.IsHost)
			{
				Session.Broadcast(ProgressPacket(type), RO);
				return;
			}
			if (type == creatingFor) return;
			var add = adding.Contains(type);
			var w = Packets.Begin(Op.Progress);
			w.Put((int)type);
			w.Put(add ? 1 : __instance.GetProgress(type));
			w.Put(add);
			Session.SendToHost(w, RO);
		}
	}

	// ---------- market hooks ----------

	// Sale points: only the actor's owner sells it.
	[HarmonyPatch(typeof(ScorePlort), "OnTriggerEnter")]
	static class MarketOwnerOnly
	{
		static bool Prefix(Collider col) => !ActorSync.IsRemoteOwned(col.gameObject);
	}

	// Client: daily price reset + saturation recovery are the host's; prices arrive as MarketState.
	[HarmonyPatch(typeof(EconomyDirector), nameof(EconomyDirector.Update))]
	static class MarketTick
	{
		static bool Prefix() => !Session.InRemoteWorld;
	}

	[HarmonyPatch(typeof(EconomyDirector), nameof(EconomyDirector.ResetPrices))]
	static class MarketReset
	{
		static void Postfix()
		{
			if (Session.IsHost) marketDirty = true;
		}
	}

	// Client sale: forward it so the host's saturation counts it once (local copy keeps the
	// UI's sold counter responsive until the host's state replaces it).
	[HarmonyPatch(typeof(EconomyDirector), nameof(EconomyDirector.RegisterSold))]
	static class MarketSold
	{
		static void Postfix(Identifiable.Id id, int count)
		{
			if (Session.IsHost) marketDirty = true;
			else if (ClientLive)
			{
				var w = Packets.Begin(Op.MarketState);
				w.Put((int)id);
				w.Put(count);
				Session.SendToHost(w, RO);
			}
		}
	}

	// ---------- exchange hooks ----------

	// Client: offer expiry, daily offer generation and recurring offers are the host's.
	[HarmonyPatch(typeof(ExchangeDirector), nameof(ExchangeDirector.Update))]
	static class ExchangeTick
	{
		static bool Prefix() => !Session.InRemoteWorld;
	}

	[HarmonyPatch(typeof(ExchangeDirector), "OfferDidChange")]
	static class ExchangeChanged
	{
		static void Postfix()
		{
			if (Session.IsHost) exchangeDirty = true;
		}
	}

	[HarmonyPatch(typeof(ExchangeAcceptor), nameof(ExchangeAcceptor.Awake))]
	static class AcceptorAwake
	{
		static void Postfix(ExchangeAcceptor __instance) => acceptors.Add(__instance);
	}

	// Deposits: only the actor's owner feeds the acceptor.
	[HarmonyPatch(typeof(ExchangeAcceptor), nameof(ExchangeAcceptor.OnTriggerEnter))]
	static class AcceptorOwnerOnly
	{
		static bool Prefix(Collider col) => !ActorSync.IsRemoteOwned(col.gameObject);
	}

	// Client deposit: count it on the mirror (so a full request stops eating items) but never
	// award locally; the host runs the real TryAccept and its awarders.
	[HarmonyPatch(typeof(ExchangeDirector), nameof(ExchangeDirector.TryAccept))]
	static class ExchangeAccept
	{
		static bool Prefix(ExchangeDirector __instance, ExchangeDirector.OfferType type, Identifiable.Id id, ref bool __result)
		{
			if (!ClientLive) return true;
			__result = false;
			if (!World.currOffers.TryGetValue(type, out var offer)) return false;
			var req = offer.requests.FirstOrDefault(q => q.id == id && !q.IsComplete());
			if (req == null) return false;
			req.progress++;
			__instance.onOfferChanged?.Invoke();
			SendExchange(ExchangeKind.Accept, type, w => w.Put((int)id));
			return __result = true;
		}
	}

	// Client picks a daily rancher / starts a progress offer: runs locally (chat UI, rancher
	// progress shown at once), then hands the created offer to the host, which adds the rancher
	// progress itself only if it takes the offer.
	[HarmonyPatch]
	static class ExchangeCreate
	{
		static IEnumerable<MethodBase> TargetMethods()
		{
			yield return AccessTools.Method(typeof(ExchangeDirector), nameof(ExchangeDirector.SelectDailyOffer));
			yield return AccessTools.Method(typeof(ExchangeDirector), nameof(ExchangeDirector.MaybeStartNext));
		}

		static void Prefix(object[] __args, out HashSet<ExchangeDirector.OfferType> __state)
		{
			__state = ClientLive ? new HashSet<ExchangeDirector.OfferType>(World.currOffers.Keys) : null;
			if (__state != null && __args[0] is string rancherId) creatingFor = RancherProgress(rancherId);
		}

		static Exception Finalizer(Exception __exception)
		{
			creatingFor = null;
			return __exception;
		}

		static void Postfix(HashSet<ExchangeDirector.OfferType> __state)
		{
			if (__state == null || !ClientLive) return;
			foreach (var kv in World.currOffers)
				if (!__state.Contains(kv.Key))
				{
					var offer = kv.Value;
					SendExchange(ExchangeKind.Put, kv.Key, w => WriteOffer(w, offer));
				}
		}
	}

	// Client-simulated crate broke open: the host clears the offer (rewards came as ActorSpawn).
	[HarmonyPatch(typeof(ExchangeDirector), nameof(ExchangeDirector.RewardsDidSpawn))]
	static class ExchangeRewarded
	{
		static void Prefix(ExchangeDirector.OfferType type)
		{
			if (ClientLive) SendExchange(ExchangeKind.Clear, type);
		}
	}
}
