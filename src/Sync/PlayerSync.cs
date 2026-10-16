using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
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
			var vac = ReadVac(r);
			// Avatar missing (joined before our world was up) or lost to a scene load: (re)create on demand.
			if ((!avatars.TryGetValue(id, out var a) || !a) && id != Session.LocalId && Session.InWorld && Session.Players.TryGetValue(id, out var p))
				avatars[id] = a = RemoteAvatar.Create(p);
			if (!a) return;
			heard[id] = pos;
			a.Push(pos, yaw, pitch, vac);
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
		WriteVac(w, player);
		Session.Distribute(w, DeliveryMethod.Sequenced);
	}

	// The local vac as others see it: cone on, the spiral/dial values VacColorAnimator drives, and shots and
	// captures as running counters (a dropped packet just plays them one update late).
	static WeaponVacuum vac;
	static VacColorAnimator vacColors;
	static byte shots, captures;
	static readonly AccessTools.FieldRef<WeaponVacuum, GameObject> vacFX = AccessTools.FieldRefAccess<WeaponVacuum, GameObject>("vacFX");
	static readonly AccessTools.FieldRef<WeaponVacuum, GameObject> held = AccessTools.FieldRefAccess<WeaponVacuum, GameObject>("held");
	static readonly AccessTools.FieldRef<VacColorAnimator, Material> dialMat = AccessTools.FieldRefAccess<VacColorAnimator, Material>("vacDialMat");
	static readonly AccessTools.FieldRef<VacColorAnimator, float> colorVal = AccessTools.FieldRefAccess<VacColorAnimator, float>("colorVal");

	static void WriteVac(NetDataWriter w, Transform player)
	{
		if (!vac)
		{
			vac = player.GetComponentInChildren<WeaponVacuum>(true);
			vacColors = vac ? vac.GetComponentInChildren<VacColorAnimator>(true) : null;
		}
		var flags = (byte)((vac && vacFX(vac) && vacFX(vac).activeSelf ? 1 : 0) | (vac && held(vac) ? 2 : 0));
		var dial = vacColors ? dialMat(vacColors) : null;
		w.Put(flags);
		w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(vacColors ? colorVal(vacColors) : 0.5f) * 255f));
		w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(dial ? dial.GetFloat(RemoteAvatar.AmmoFullnessId) : 0f) * 255f));
		Color32 c = dial ? dial.GetColor(RemoteAvatar.AmmoColorId) : Color.black;
		w.Put(c.r); w.Put(c.g); w.Put(c.b); w.Put(c.a);
		w.Put(shots);
		w.Put(captures);
		if ((flags & 2) != 0) w.Put(ActorSync.IdOf(held(vac)));
	}

	static RemoteAvatar.VacView ReadVac(NetDataReader r)
	{
		var flags = r.GetByte();
		var v = new RemoteAvatar.VacView
		{
			cone = (flags & 1) != 0,
			holding = (flags & 2) != 0,
			spiral = r.GetByte() / 255f,
			fullness = r.GetByte() / 255f,
			ammo = new Color32(r.GetByte(), r.GetByte(), r.GetByte(), r.GetByte()),
			shots = r.GetByte(),
			captures = r.GetByte(),
		};
		if (v.holding) v.heldId = r.GetLong();
		return v;
	}

	[HarmonyPatch(typeof(WeaponVacuum), "ShootEffect")]
	static class CountShots
	{
		static void Postfix() => shots++;
	}

	[HarmonyPatch(typeof(WeaponVacuum), "CaptureEffect")]
	static class CountCaptures
	{
		static void Postfix() => captures++;
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
