using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using TailRanch.Sync;
using UnityEngine.SceneManagement;

namespace TailRanch.Net;

public enum Mode { Off, Host, Client }

public class Player
{
	public byte Id;
	public string Name;
	public NetPeer Peer; // host side only
	public bool Ready;
}

public static class Session
{
	const string Key = "tailranch";
	public const byte HostId = 0;

	public static Mode Mode { get; private set; }
	public static bool IsHost => Mode == Mode.Host;
	public static bool IsClient => Mode == Mode.Client;
	public static bool Active => Mode != Mode.Off;

	public static byte LocalId;
	public static string Status = "offline";

	// Client: set once the remote world is running; stays until back at main menu so
	// the borrowed world is never written to local saves.
	public static bool InRemoteWorld;

	// Remote players by id (never contains the local player).
	public static readonly Dictionary<byte, Player> Players = new();

	static NetManager net;
	static NetPeer hostPeer;
	static readonly Dictionary<NetPeer, Player> byPeer = new();
	static readonly Action<Player, NetPacketReader>[] handlers = new Action<Player, NetPacketReader>[256];

	public static void On(Op op, Action<Player, NetPacketReader> h) => handlers[(byte)op] = h;

	public static bool InWorld => SceneManager.GetActiveScene().name == "worldGenerated"
		&& SRSingleton<SceneContext>.Instance != null && SRSingleton<SceneContext>.Instance.Player != null;

	public static int Rtt => hostPeer?.Ping ?? 0;

	static Session()
	{
		On(Op.Hello, OnHello);
		On(Op.Welcome, (_, r) => LocalId = r.GetByte());
		On(Op.World, OnWorld);
		On(Op.Ready, OnReady);
		On(Op.PlayerJoin, (_, r) => AddPlayer(r.GetByte(), r.GetString()));
		On(Op.PlayerLeave, (_, r) => RemovePlayer(r.GetByte()));
		PlayerSync.Register();
	}

	// ---------- lifecycle ----------

	public static void Host()
	{
		if (Active || !InWorld) return;
		var l = Listener();
		l.ConnectionRequestEvent += req =>
		{
			if (!Tailnet.Allowed(req.RemoteEndPoint.Address))
			{
				Plugin.Log.LogWarning($"rejected non-tailnet peer {req.RemoteEndPoint}");
				req.Reject();
				return;
			}
			req.AcceptIfKey(Key);
		};
		net = NewManager(l);
		if (!net.Start(Plugin.Port.Value))
		{
			Status = $"could not bind UDP {Plugin.Port.Value}";
			net = null;
			return;
		}
		Mode = Mode.Host;
		LocalId = HostId;
		Status = $"hosting on :{Plugin.Port.Value}";
		Plugin.Log.LogInfo(Status);
	}

	public static void Join(string target)
	{
		if (Active) return;
		if (SceneManager.GetActiveScene().name != "MainMenu")
		{
			Status = "join from the main menu";
			return;
		}
		System.Net.IPAddress ip;
		try { ip = Tailnet.Resolve(target); }
		catch (Exception e) { Status = $"resolve failed: {e.Message}"; return; }
		if (ip == null) { Status = $"{target}: no tailnet address"; return; }

		Plugin.JoinTarget.Value = target;
		net = NewManager(Listener());
		net.Start();
		hostPeer = net.Connect(ip.ToString(), Plugin.Port.Value, Key);
		Mode = Mode.Client;
		Status = $"connecting to {target} ({ip})";
		Plugin.Log.LogInfo(Status);
	}

	public static void Stop(string why)
	{
		if (!Active) return;
		net.Stop(true);
		net = null;
		hostPeer = null;
		byPeer.Clear();
		foreach (var id in Players.Keys.ToList()) PlayerSync.Despawn(id);
		Players.Clear();
		Mode = Mode.Off;
		Status = why;
		Plugin.Log.LogInfo($"session stopped: {why}");
	}

	public static void OnMainMenu()
	{
		Stop("offline");
		InRemoteWorld = false;
	}

	public static void Poll() => net?.PollEvents();

	static NetManager NewManager(EventBasedNetListener l) => new NetManager(l)
	{
		AutoRecycle = true,
		DisconnectTimeout = 15000,
		IPv6Enabled = true,
	};

	static EventBasedNetListener Listener()
	{
		var l = new EventBasedNetListener();
		l.PeerConnectedEvent += peer =>
		{
			if (IsClient)
			{
				Status = "connected, requesting world";
				var w = Packets.Begin(Op.Hello);
				w.Put(Plugin.Version);
				w.Put(Plugin.PlayerName.Value);
				peer.Send(w, DeliveryMethod.ReliableOrdered);
			}
		};
		l.PeerDisconnectedEvent += (peer, info) =>
		{
			if (IsClient) Stop($"disconnected: {Reason(info)}");
			else if (byPeer.TryGetValue(peer, out var p))
			{
				byPeer.Remove(peer);
				RemovePlayer(p.Id);
				if (p.Ready) Broadcast(Packets.Begin(Op.PlayerLeave).Also(w => w.Put(p.Id)), DeliveryMethod.ReliableOrdered);
			}
		};
		l.NetworkReceiveEvent += (peer, reader, _, _) =>
		{
			var op = reader.GetByte();
			var h = handlers[op];
			if (h == null) return;
			Player from = null;
			if (IsHost && !byPeer.TryGetValue(peer, out from) && op != (byte)Op.Hello) return;
			try { h(from ?? new Player { Peer = peer }, reader); }
			catch (Exception e) { Plugin.Log.LogError($"{(Op)op}: {e}"); }
		};
		return l;
	}

	static string Reason(DisconnectInfo info)
	{
		if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
			return info.AdditionalData.GetString();
		return info.Reason.ToString();
	}

	// ---------- handshake ----------

	static void OnHello(Player p, NetPacketReader r)
	{
		var ver = r.GetString();
		var name = r.GetString();
		if (ver != Plugin.Version)
		{
			var w = new NetDataWriter();
			w.Put($"version mismatch: host {Plugin.Version}, you {ver}");
			p.Peer.Disconnect(w);
			return;
		}
		byte id = 1;
		while (byPeer.Values.Any(x => x.Id == id)) id++;
		p.Id = id;
		p.Name = name;
		byPeer[p.Peer] = p;

		p.Peer.Send(Packets.Begin(Op.Welcome).Also(w => w.Put(id)), DeliveryMethod.ReliableOrdered);
		var bytes = RemoteSave.Snapshot();
		p.Peer.Send(Packets.Begin(Op.World).Also(w => w.PutBytesWithLength(bytes)), DeliveryMethod.ReliableOrdered);
		Plugin.Log.LogInfo($"{name} joined as {id}, sent {bytes.Length / 1024} KiB world");
	}

	static void OnWorld(Player _, NetPacketReader r)
	{
		var bytes = r.GetBytesWithLength();
		Status = $"loading world ({bytes.Length / 1024} KiB)";
		RemoteSave.Load(bytes, () =>
		{
			InRemoteWorld = true;
			Status = $"connected to {Plugin.JoinTarget.Value}";
			SendToHost(Packets.Begin(Op.Ready), DeliveryMethod.ReliableOrdered);
		}, () => Stop("world load failed"));
	}

	static void OnReady(Player p, NetPacketReader _)
	{
		p.Ready = true;
		// New player learns about host + everyone else.
		p.Peer.Send(JoinPacket(HostId, Plugin.PlayerName.Value), DeliveryMethod.ReliableOrdered);
		foreach (var q in byPeer.Values.Where(q => q.Ready && q != p))
			p.Peer.Send(JoinPacket(q.Id, q.Name), DeliveryMethod.ReliableOrdered);
		Broadcast(JoinPacket(p.Id, p.Name), DeliveryMethod.ReliableOrdered, except: p);
		AddPlayer(p.Id, p.Name, p);
	}

	static NetDataWriter JoinPacket(byte id, string name) =>
		Packets.Begin(Op.PlayerJoin).Also(w => { w.Put(id); w.Put(name); });

	static void AddPlayer(byte id, string name, Player p = null)
	{
		p ??= new Player { Id = id, Name = name, Ready = true };
		Players[id] = p;
		PlayerSync.Spawn(p);
	}

	static void RemovePlayer(byte id)
	{
		Players.Remove(id);
		PlayerSync.Despawn(id);
	}

	// ---------- send ----------

	public static void SendToHost(NetDataWriter w, DeliveryMethod m) => hostPeer?.Send(w, m);

	public static void Broadcast(NetDataWriter w, DeliveryMethod m, Player except = null)
	{
		foreach (var p in byPeer.Values)
			if (p.Ready && p != except) p.Peer.Send(w, m);
	}

	// Host: to all clients. Client: to host (which relays).
	public static void Distribute(NetDataWriter w, DeliveryMethod m)
	{
		if (IsHost) Broadcast(w, m);
		else SendToHost(w, m);
	}

	// Host: forward a client's packet verbatim to everyone else.
	public static void Relay(Player from, NetPacketReader r, DeliveryMethod m)
	{
		foreach (var p in byPeer.Values)
			if (p.Ready && p != from) p.Peer.Send(r.RawData, r.UserDataOffset, r.UserDataSize, m);
	}

	public static T Also<T>(this T t, Action<T> a)
	{
		a(t);
		return t;
	}
}
