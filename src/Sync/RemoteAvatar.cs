using System.Collections.Generic;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Placeholder body: SR1 ships no third-person player model.
public class RemoteAvatar : MonoBehaviour
{
	public static readonly Color Gold = new Color(0.83f, 0.65f, 0.17f);
	const float InterpDelay = 0.1f;

	struct Snap
	{
		public float t;
		public Vector3 pos;
		public float yaw, pitch;
	}

	readonly List<Snap> snaps = new();
	Transform head;
	Transform label;

	public static RemoteAvatar Create(Player p)
	{
		var root = new GameObject($"TailRanch Player {p.Id}");
		var a = root.AddComponent<RemoteAvatar>();

		var body = Part(PrimitiveType.Capsule, root.transform, new Vector3(0, -0.2f, 0), new Vector3(0.7f, 0.8f, 0.7f));
		a.head = Part(PrimitiveType.Cube, root.transform, new Vector3(0, 0.75f, 0), new Vector3(0.45f, 0.45f, 0.45f));
		Part(PrimitiveType.Cube, a.head, new Vector3(0, 0, 0.55f), new Vector3(0.6f, 0.2f, 0.2f)); // visor = look dir
		body.GetComponent<Renderer>().material.color = new Color(0.06f, 0.06f, 0.06f);

		var text = new GameObject("name");
		text.transform.SetParent(root.transform, false);
		text.transform.localPosition = new Vector3(0, 1.35f, 0);
		var tm = text.AddComponent<TextMesh>();
		tm.text = p.Name;
		tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
		text.GetComponent<MeshRenderer>().material = tm.font.material;
		tm.fontSize = 48;
		tm.characterSize = 0.05f;
		tm.anchor = TextAnchor.MiddleCenter;
		tm.color = Gold;
		a.label = text.transform;
		return a;
	}

	static Transform Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale)
	{
		var go = GameObject.CreatePrimitive(type);
		Object.Destroy(go.GetComponent<Collider>());
		go.transform.SetParent(parent, false);
		go.transform.localPosition = pos;
		go.transform.localScale = scale;
		go.GetComponent<Renderer>().material.color = Gold;
		return go.transform;
	}

	public void Push(Vector3 pos, float yaw, float pitch)
	{
		if (snaps.Count == 0) transform.position = pos;
		snaps.Add(new Snap { t = Time.unscaledTime, pos = pos, yaw = yaw, pitch = pitch });
		if (snaps.Count > 32) snaps.RemoveAt(0);
	}

	void Update()
	{
		if (snaps.Count == 0) return;
		var t = Time.unscaledTime - InterpDelay;
		while (snaps.Count > 2 && snaps[1].t <= t) snaps.RemoveAt(0);

		Snap a = snaps[0], b = snaps.Count > 1 ? snaps[1] : snaps[0];
		var k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 1f;
		transform.position = Vector3.Lerp(a.pos, b.pos, k);
		transform.rotation = Quaternion.Euler(0, Mathf.LerpAngle(a.yaw, b.yaw, k), 0);
		head.localRotation = Quaternion.Euler(Mathf.LerpAngle(a.pitch, b.pitch, k), 0, 0);

		var cam = Camera.main;
		if (cam) label.rotation = Quaternion.LookRotation(label.position - cam.transform.position);
	}
}
