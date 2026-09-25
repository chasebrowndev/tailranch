using LiteNetLib.Utils;
using UnityEngine;

namespace TailRanch.Net;

public enum Op : byte
{
	Hello,       // c→h  version, name
	Welcome,     // h→c  your id
	World,       // h→c  save bytes
	Ready,       // c→h  world loaded
	PlayerJoin,  // h→*  id, name
	PlayerLeave, // h→*  id
	PlayerPos,   // *    id, pos, yaw, pitch
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
}
