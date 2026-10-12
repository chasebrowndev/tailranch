using System.Collections.Generic;
using LiteNetLib;
using MonomiPark.SlimeRancher.Regions;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

public static class PlayerSync
{
	const float SendInterval = 0.05f;
	static float nextSend;
	static readonly Dictionary<byte, RemoteAvatar> avatars = new();
	static readonly Dictionary<byte, Vector3> heard = new();
	static readonly Dictionary<byte, Keep> keeps = new();
	static List<Region> inner = new(), outer = new();

	// Host: regions held loaded/awake around a remote avatar, mirroring RegionLoader.
	class Keep
	{
		public readonly List<Region> loaded = new(), awake = new();
		public RegionRegistry.RegionSetId set = RegionRegistry.RegionSetId.UNSET;
		public Vector3 at = Vector3.positiveInfinity;
	}

	public static void Register()
	{
		Session.On(Op.PlayerPos, (from, r) =>
		{
			var id = r.GetByte();
			if (Session.IsHost)
			{
				if (id != from.Id) return;
				Session.Relay(from, r, DeliveryMethod.Sequenced);
			}
			var pos = r.GetVector3();
			var yaw = r.GetFloat();
			var pitch = r.GetFloat();
			// Avatar missing (joined before our world was up) or lost to a scene load: (re)create on demand.
			if ((!avatars.TryGetValue(id, out var a) || !a) && id != Session.LocalId && Session.InWorld && Session.Players.TryGetValue(id, out var p))
				avatars[id] = a = RemoteAvatar.Create(p);
			if (!a) return;
			heard[id] = pos;
			a.Push(pos, yaw, pitch);
		});
	}

	public static void Tick()
	{
		if (!Session.Active || !Session.InWorld) return;
		if (Session.IsHost) KeepAwake();
		if (Time.unscaledTime < nextSend) return;
		if (Session.IsClient && !Session.InRemoteWorld) return;
		nextSend = Time.unscaledTime + SendInterval;

		var player = SRSingleton<SceneContext>.Instance.Player.transform;
		var cam = Camera.main;
		var w = Packets.Begin(Op.PlayerPos);
		w.Put(Session.LocalId);
		w.Put(player.position);
		w.Put(player.eulerAngles.y);
		w.Put(cam ? cam.transform.eulerAngles.x : 0f);
		Session.Distribute(w, DeliveryMethod.Sequenced);
	}

	// Only replaces the avatar: a re-announced player keeps the actors it simulates.
	public static void Spawn(Player p)
	{
		if (avatars.TryGetValue(p.Id, out var old) && old) Object.Destroy(old.gameObject);
		avatars.Remove(p.Id);
		if (!Session.InWorld) return;
		avatars[p.Id] = RemoteAvatar.Create(p);
	}

	public static void Rename(byte id, string name)
	{
		if (avatars.TryGetValue(id, out var a) && a) a.SetName(name);
	}

	// Player gone: avatar, held regions, and (host) every actor they simulated returns to the host.
	public static void Despawn(byte id)
	{
		if (avatars.TryGetValue(id, out var a) && a) Object.Destroy(a.gameObject);
		avatars.Remove(id);
		heard.Remove(id);
		if (keeps.TryGetValue(id, out var k) && Session.InWorld
			&& k.set == SRSingleton<SceneContext>.Instance.RegionRegistry.GetCurrentRegionSetId())
		{
			foreach (var r in k.loaded)
				if (r) r.RemoveNonProxiedReference();
			foreach (var r in k.awake)
				if (r) r.RemoveNonHibernateReference();
		}
		keeps.Remove(id);
		ActorSync.PlayerLeft(id);
	}

	static void KeepAwake()
	{
		var ctx = SRSingleton<SceneContext>.Instance;
		var reg = ctx.RegionRegistry;
		var set = reg.GetCurrentRegionSetId();
		var loader = ctx.Player.GetComponentInChildren<RegionLoader>();
		var load = loader ? loader.LoadSize : new Vector3(20f, 10f, 20f);
		var wake = loader ? loader.WakeSize : new Vector3(20f, 10f, 20f);
		foreach (var kv in heard)
		{
			if (!keeps.TryGetValue(kv.Key, out var k)) keeps[kv.Key] = k = new Keep();
			if (k.set != set)
			{
				// Region set change zeroed every count; just forget.
				k.loaded.Clear();
				k.awake.Clear();
				k.set = set;
				k.at = Vector3.positiveInfinity;
			}
			if ((kv.Value - k.at).sqrMagnitude < 1f) continue;
			k.at = kv.Value;
			Hold(reg, k.loaded, kv.Value, load, true);
			Hold(reg, k.awake, kv.Value, wake, false);
		}
	}

	static void Hold(RegionRegistry reg, List<Region> held, Vector3 pos, Vector3 size, bool proxy)
	{
		reg.GetContaining(ref inner, new Bounds(pos, size));
		reg.GetContaining(ref outer, new Bounds(pos, size * 1.1f));
		for (var i = held.Count - 1; i >= 0; i--)
		{
			var r = held[i];
			if (outer.Contains(r)) continue;
			held.RemoveAt(i);
			if (!r) continue;
			if (proxy) r.RemoveNonProxiedReference();
			else r.RemoveNonHibernateReference();
		}
		foreach (var r in inner)
		{
			if (held.Contains(r)) continue;
			if (proxy) r.AddNonProxiedReference();
			else r.AddNonHibernateReference();
			held.Add(r);
		}
	}
}
