using LiteNetLib.Utils;
using UnityEngine;

namespace TailRanch.Net;

public enum Op : byte
{
	Hello,       // c→h  version, name
	Welcome,     // h→c  your id
	World,       // h→c  gzipped save bytes
	Ready,       // c→h  world loaded
	PlayerJoin,  // h→*  id, name
	PlayerLeave, // h→*  id
	PlayerPos,   // *    id, pos, yaw, pitch

	Clock,        // h→c  worldTime double, fastForwardUntil double?
	ActorSpawn,   // *    actorId long, ident int, regionSet int, pos, rot, vel
	ActorDespawn, // *    actorId long
	ActorMotion,  // *    count ushort, then per actor: actorId long, pos, rot, vel
	ActorClaim,   // *    actorId long, owner byte (c→h request; h→* grant, requester included)
	ActorRelease, // *    actorId long (back to host)
	Money,        // c→h  delta int; h→c total int
	Keys,         // c→h  delta int; h→c total int
	Door,         // *    doorId string, state int
	Progress,     // *    progressType int, value int
	PlotReplace,  // *    plotId string, plotType int
	PlotUpgrade,  // *    plotId string, upgrade int
	SiloState,    // h→c  plotId string, storageType int, then full ammo slots + ack bool (RanchSync)
	FeederSpeed,  // *    plotId string, speed int
	GadgetPlace,  // *    siteId string, gadgetId int, yRot float
	GadgetRemove, // *    siteId string
	Refinery,     // c→h  ident int, delta int; h→c ident int, total int

	// Gap-fix wave: layouts documented next to each handler in the owning file.
	ActorDeny,      // ActorSync
	GordoState,     // WorldSync
	PodState,       // WorldSync
	EchoState,      // WorldSync
	GadgetState,    // WorldSync
	PlayerRegion,   // PresenceSync
	DroneMotion,    // PresenceSync
	SiloOp,         // RanchSync
	CollectorPull,  // RanchSync
	MarketState,    // EconomySync
	ExchangeState,  // EconomySync
	StorageState,   // RanchSync (decorizer, glitch storage)
	Profile,        // ProfileSync (kind byte + payload: upgrades, blueprints, gadget counts, pedia, mail)
}

public static class Packets
{
	static readonly NetDataWriter w = new NetDataWriter(true, 256);

	// Shared writer: build and send before calling Begin again.
	public static NetDataWriter Begin(Op op)
	{
		w.Reset();
		w.Put((byte)op);
		return w;
	}

	public static void Put(this NetDataWriter w, Vector3 v)
	{
		w.Put(v.x);
		w.Put(v.y);
		w.Put(v.z);
	}

	public static Vector3 GetVector3(this NetDataReader r) => new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

	public static void Put(this NetDataWriter w, Quaternion q)
	{
		w.Put(q.x);
		w.Put(q.y);
		w.Put(q.z);
		w.Put(q.w);
	}

	public static Quaternion GetQuaternion(this NetDataReader r) => new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());

	public static void Put(this NetDataWriter w, double? d)
	{
		w.Put(d.HasValue);
		if (d.HasValue) w.Put(d.Value);
	}

	public static double? GetNullableDouble(this NetDataReader r) => r.GetBool() ? r.GetDouble() : null;
}
