using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using TailRanch.Dev;
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
	// Host: reliable broadcasts that happened after this joiner's snapshot but before
	// their world finished loading; flushed on Ready.
	public System.Collections.Generic.List<(byte[] data, DeliveryMethod m)> Pending = new();
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
	static bool loadingRemote;

	// Remote players by id (never contains the local player).
	public static readonly Dictionary<byte, Player> Players = new();

	static NetManager net;
	static NetPeer hostPeer;
	static readonly Dictionary<NetPeer, Player> byPeer = new();
	static readonly Action<Player, NetPacketReader>[] handlers = new Action<Player, NetPacketReader>[256];
	// Results from ThreadPool work, run on the main thread by Poll.
	static readonly ConcurrentQueue<Action> mainQueue = new();

	public static void On(Op op, Action<Player, NetPacketReader> h) => handlers[(byte)op] = h;

	// True while replaying a remote change; sync patches must not re-broadcast then.
	public static bool Applying { get; private set; }

	public static IDisposable Apply() => new ApplyScope();

	sealed class ApplyScope : IDisposable
	{
		readonly bool prev = Applying;
		public ApplyScope() => Applying = true;
		public void Dispose() => Applying = prev;
	}

	// Host: fired right after a joiner's save snapshot is taken (start buffering world
	// events for them), and when they report their world loaded (flush + initial state).
	public static event Action<Player> PlayerHello;
	public static event Action<Player> PlayerReady;

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
		ClockSync.Register();
		ActorSync.Register();
		EconomySync.Register();
		RanchSync.Register();
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
		loadingRemote = false;
	}

	public static void Poll()
	{
		net?.PollEvents();
		while (mainQueue.TryDequeue(out var a))
		{
			try { a(); }
			catch (Exception e) { Plugin.Log.LogError(e); }
		}
	}

	// PauseMenu.Quit's path (PauseMenu.cs:129) minus the save: the borrowed world is never saved.
	static void ReturnToMenu()
	{
		Plugin.Log.LogInfo($"returning to main menu: {Status}");
		UnityEngine.Time.timeScale = 1f;
		SRSingleton<SceneContext>.Instance?.OnSessionEnded();
		SceneManager.LoadScene("MainMenu");
	}

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
				Send(peer, w, DeliveryMethod.ReliableOrdered);
			}
		};
		l.PeerDisconnectedEvent += (peer, info) =>
		{
			if (IsClient)
			{
				var inWorld = InRemoteWorld;
				Stop($"{(inWorld || loadingRemote ? "host lost" : "disconnected")}: {Reason(info)}");
				// Mid-load, OnWorld's callback returns to the menu once the scene is up.
				if (inWorld) ReturnToMenu();
			}
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
			DevAuto.Count(false, op, reader.UserDataSize);
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

		SendTo(p, Packets.Begin(Op.Welcome).Also(w => w.Put(id)), DeliveryMethod.ReliableOrdered);
		// Pull reads live transforms so it stays here; serialize + gzip on the ThreadPool.
		var state = RemoteSave.Pull();
		PlayerHello?.Invoke(p);
		Plugin.Log.LogInfo($"{name} joined as {id}, packing world");
		ThreadPool.QueueUserWorkItem(_ =>
		{
			try
			{
				var gz = RemoteSave.Pack(state, out var raw);
				mainQueue.Enqueue(() => SendWorld(p, gz, raw));
			}
			catch (Exception e)
			{
				// A live list aliased by the snapshot changed mid-write; redo it where nothing moves.
				mainQueue.Enqueue(() =>
				{
					Plugin.Log.LogWarning($"async world pack failed, packing on main thread: {e.Message}");
					var gz = RemoteSave.Pack(state, out var raw);
					SendWorld(p, gz, raw);
				});
			}
		});
	}

	static void SendWorld(Player p, byte[] gz, int raw)
	{
		if (!byPeer.TryGetValue(p.Peer, out var cur) || cur != p || p.Peer.ConnectionState != ConnectionState.Connected)
		{
			Plugin.Log.LogInfo($"{p.Name} left before the world was sent");
			return;
		}
		// Own writer so the shared one doesn't keep a multi-MiB buffer. LiteNetLib caps a message at
		// 65535 fragments (~31 MiB at the 508-byte minimum MTU), far above a gzipped save.
		var w = new NetDataWriter(false, gz.Length + 8);
		w.Put((byte)Op.World);
		w.PutBytesWithLength(gz);
		SendTo(p, w, DeliveryMethod.ReliableOrdered);
		Plugin.Log.LogInfo($"sent {p.Name} world: {gz.Length / 1024} KiB gzip, {raw / 1024} KiB raw");
	}

	static void OnWorld(Player _, NetPacketReader r)
	{
		var gz = r.GetBytesWithLength();
		var bytes = RemoteSave.Unpack(gz);
		Status = $"loading world ({gz.Length / 1024} KiB)";
		Plugin.Log.LogInfo($"world received: {gz.Length / 1024} KiB gzip, {bytes.Length / 1024} KiB raw");
		loadingRemote = true;
		RemoteSave.Load(bytes, () =>
		{
			loadingRemote = false;
			InRemoteWorld = true;
			if (!IsClient)
			{
				ReturnToMenu();
				return;
			}
			Status = $"connected to {Plugin.JoinTarget.Value}";
			SendToHost(Packets.Begin(Op.Ready), DeliveryMethod.ReliableOrdered);
		}, () =>
		{
			loadingRemote = false;
			Stop("world load failed");
		});
	}

	static void OnReady(Player p, NetPacketReader _)
	{
		p.Ready = true;
		foreach (var (data, m) in p.Pending) p.Peer.Send(data, m);
		p.Pending = null;
		// New player learns about host + everyone else.
		SendTo(p, JoinPacket(HostId, Plugin.PlayerName.Value), DeliveryMethod.ReliableOrdered);
		foreach (var q in byPeer.Values.Where(q => q.Ready && q != p))
			SendTo(p, JoinPacket(q.Id, q.Name), DeliveryMethod.ReliableOrdered);
		Broadcast(JoinPacket(p.Id, p.Name), DeliveryMethod.ReliableOrdered, except: p);
		AddPlayer(p.Id, p.Name, p);
		PlayerReady?.Invoke(p);
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

	static void Send(NetPeer peer, NetDataWriter w, DeliveryMethod m)
	{
		if (peer == null) return;
		peer.Send(w, m);
		DevAuto.Count(true, w.Data[0], w.Length);
	}

	public static void SendToHost(NetDataWriter w, DeliveryMethod m) => Send(hostPeer, w, m);

	// Host: to one client.
	public static void SendTo(Player p, NetDataWriter w, DeliveryMethod m) => Send(p.Peer, w, m);

	public static void Broadcast(NetDataWriter w, DeliveryMethod m, Player except = null)
	{
		foreach (var p in byPeer.Values)
			if (p == except) continue;
			else if (p.Ready) Send(p.Peer, w, m);
			else if (Reliable(m)) p.Pending?.Add((w.CopyData(), m));
	}

	static bool Reliable(DeliveryMethod m) => m != DeliveryMethod.Unreliable && m != DeliveryMethod.Sequenced;

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
			if (p == from) continue;
			else if (p.Ready)
			{
				p.Peer.Send(r.RawData, r.UserDataOffset, r.UserDataSize, m);
				DevAuto.Count(true, r.RawData[r.UserDataOffset], r.UserDataSize);
			}
			else if (Reliable(m))
			{
				var data = new byte[r.UserDataSize];
				Buffer.BlockCopy(r.RawData, r.UserDataOffset, data, 0, data.Length);
				p.Pending?.Add((data, m));
			}
	}

	public static T Also<T>(this T t, Action<T> a)
	{
		a(t);
		return t;
	}
}
