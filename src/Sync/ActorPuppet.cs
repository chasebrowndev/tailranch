using System;
using System.Collections.Generic;
using UnityEngine;

namespace TailRanch.Sync;

// Local view of an actor this machine doesn't simulate. `driven`: kinematic, follows motion
// snapshots. `aiOff`: autonomous behaviours (eating, moving, producing, transforming) disabled.
// Hibernation (RegionMember) toggles behaviours and isKinematic behind our back, so state is
// re-enforced every Update instead of applied once.
public class ActorPuppet : MonoBehaviour
{
	const float InterpDelay = 0.1f;

	static readonly Type[] Autonomous =
	{
		typeof(SlimeSubbehaviourPlexer), typeof(SlimeSubbehaviour), typeof(SlimeEat), typeof(SlimeEatTrigger),
		typeof(SlimeEatWater), typeof(SlimeEatAsh), typeof(Reproduce), typeof(TransformAfterTime),
		typeof(TransformChanceOnReproduce), typeof(GoldSlimeProducePlorts), typeof(LuckySlimeProduceCoins),
		typeof(ReactToShock), typeof(DestroyAfterTime), typeof(DestroyPlortAfterTime), typeof(PollenCloudDestructor),
		typeof(PollenCloudController), typeof(KeepUpright), typeof(SlimeFeral), typeof(MaybeCullOnReenable),
		typeof(RescueAmmoOnDeath), typeof(TarrSterilizeOnWater),
	};

	struct Snap
	{
		public float t;
		public Vector3 pos, vel;
		public Quaternion rot;
	}

	readonly List<Snap> snaps = new();
	readonly List<Behaviour> off = new();
	Behaviour[] targets;
	Rigidbody rb;
	bool driven, aiOff, kinematicApplied;
	bool wasKinematic;
	RigidbodyInterpolation wasInterp;

	public static ActorPuppet Set(GameObject go, bool driven, bool aiOff)
	{
		if (!go) return null;
		var p = go.GetComponent<ActorPuppet>();
		if (!p)
		{
			if (!driven && !aiOff) return null;
			p = go.AddComponent<ActorPuppet>();
			p.rb = go.GetComponent<Rigidbody>();
			var list = new List<Behaviour>();
			foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
				if (Array.Exists(Autonomous, t => t.IsInstanceOfType(b))) list.Add(b);
			p.targets = list.ToArray();
		}
		p.driven = driven;
		p.aiOff = aiOff;
		if (driven) p.snaps.Clear();
		if (go.activeInHierarchy) p.Enforce();
		return p;
	}

	public void Push(Vector3 pos, Quaternion rot, Vector3 vel)
	{
		if (!isActiveAndEnabled)
		{
			// Hibernated: no interpolation, just keep the transform current.
			transform.SetPositionAndRotation(pos, rot);
			snaps.Clear();
		}
		if (snaps.Count == 0) transform.SetPositionAndRotation(pos, rot);
		snaps.Add(new Snap { t = Time.unscaledTime, pos = pos, rot = rot, vel = vel });
		if (snaps.Count > 32) snaps.RemoveAt(0);
	}

	void Enforce()
	{
		if (aiOff)
		{
			foreach (var b in targets)
				if (b && b.enabled)
				{
					b.enabled = false;
					off.Add(b);
				}
		}
		else if (off.Count > 0)
		{
			foreach (var b in off)
				if (b) b.enabled = true;
			off.Clear();
		}

		if (rb)
		{
			if (driven && !kinematicApplied)
			{
				wasKinematic = rb.isKinematic;
				wasInterp = rb.interpolation;
				rb.interpolation = RigidbodyInterpolation.None;
				kinematicApplied = true;
			}
			else if (!driven && kinematicApplied)
			{
				kinematicApplied = false;
				rb.isKinematic = wasKinematic;
				rb.interpolation = wasInterp;
				if (snaps.Count > 0)
				{
					var last = snaps[snaps.Count - 1];
					transform.SetPositionAndRotation(last.pos, last.rot);
					if (!rb.isKinematic) rb.velocity = last.vel;
				}
				snaps.Clear();
			}
			if (driven && !rb.isKinematic) rb.isKinematic = true;
		}

		if (!driven && !aiOff) Destroy(this);
	}

	void FixedUpdate()
	{
		if (driven && rb && !rb.isKinematic) rb.isKinematic = true;
	}

	void Update()
	{
		Enforce();
		if (!driven || snaps.Count == 0) return;
		var t = Time.unscaledTime - InterpDelay;
		while (snaps.Count > 2 && snaps[1].t <= t) snaps.RemoveAt(0);

		Snap a = snaps[0], b = snaps.Count > 1 ? snaps[1] : snaps[0];
		var k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 1f;
		transform.SetPositionAndRotation(Vector3.Lerp(a.pos, b.pos, k), Quaternion.Slerp(a.rot, b.rot, k));
	}
}
