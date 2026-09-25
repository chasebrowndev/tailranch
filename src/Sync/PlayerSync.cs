using System.Collections.Generic;
using LiteNetLib;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

public static class PlayerSync
{
	const float SendInterval = 0.05f;
	static float nextSend;
	static readonly Dictionary<byte, RemoteAvatar> avatars = new();

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
			if (avatars.TryGetValue(id, out var a) && a) a.Push(pos, yaw, pitch);
		});
	}

	public static void Tick()
	{
		if (!Session.Active || !Session.InWorld || Time.unscaledTime < nextSend) return;
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

	public static void Spawn(Player p)
	{
		Despawn(p.Id);
		if (!Session.InWorld) return;
		avatars[p.Id] = RemoteAvatar.Create(p);
	}

	public static void Despawn(byte id)
	{
		if (avatars.TryGetValue(id, out var a) && a) Object.Destroy(a.gameObject);
		avatars.Remove(id);
	}
}
