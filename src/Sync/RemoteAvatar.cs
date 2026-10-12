using System.Collections.Generic;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Visual-only gold slime rebuilt from appearance data; the actor prefab is never instantiated
// (that would register it, run AI and physics). Player pivot is at the feet.
public class RemoteAvatar : MonoBehaviour
{
	public static readonly Color Gold = new Color(0.83f, 0.65f, 0.17f);
	const float InterpDelay = 0.1f;
	const float Height = 1.1f;

	struct Snap
	{
		public float t;
		public Vector3 pos;
		public float yaw, pitch;
	}

	readonly List<Snap> snaps = new();
	Transform body, head, label;
	float pitchScale = 1f, phase, speed;

	public static RemoteAvatar Create(Player p)
	{
		var root = new GameObject($"TailRanch Player {p.Id}");
		var a = root.AddComponent<RemoteAvatar>();
		a.body = new GameObject("body").transform;
		a.body.SetParent(root.transform, false);
		a.phase = Random.value * 10f;

		float top = 0;
		try { top = a.BuildSlime(); }
		catch (System.Exception e) { Plugin.Log.LogWarning($"slime avatar: {e.Message}"); }
		if (top <= 0)
		{
			if (a.head) Destroy(a.head.gameObject);
			top = a.BuildPrimitive();
		}

		var text = new GameObject("name");
		text.transform.SetParent(root.transform, false);
		text.transform.localPosition = new Vector3(0, top + 0.35f, 0);
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

	float BuildSlime()
	{
		var ctx = SRSingleton<GameContext>.Instance;
		var prefab = ctx && ctx.LookupDirector ? ctx.LookupDirector.GetPrefab(Identifiable.Id.GOLD_SLIME) : null;
		var app = prefab ? prefab.GetComponentInChildren<SlimeAppearanceApplicator>(true) : null;
		if (!app) return 0;
		var look = app.Appearance;
		if (!look && app.SlimeDefinition && app.SlimeDefinition.AppearancesDefault?.Length > 0) look = app.SlimeDefinition.AppearancesDefault[0];
		if (!look || look.Structures == null) return 0;

		// Meshes live on SlimeAppearanceObject prefabs that the applicator attaches at runtime, so the actor prefab itself has no renderers.
		var bones = new Dictionary<SlimeAppearance.SlimeBone, Transform>();
		if (app.Bones != null)
			foreach (var bm in app.Bones)
				if (bm.BoneObject) bones[bm.Bone] = bm.BoneObject.transform;
		var rootT = app.RootAppearanceObject ? app.RootAppearanceObject.transform : prefab.transform;
		var toLocal = prefab.transform.worldToLocalMatrix;

		Material eyes = null, mouth = null;
		if (look.Face && look.Face.ExpressionFaces != null)
			foreach (var f in look.Face.ExpressionFaces)
				if (f.SlimeExpression == SlimeFace.SlimeExpression.Happy) { eyes = f.Eyes; mouth = f.Mouth; }

		var parts = new List<(Mesh mesh, Material[] mats, Matrix4x4 m)>();
		foreach (var s in look.Structures)
		{
			var objs = s?.Element ? s.Element.Prefabs : null;
			if (objs == null) continue;
			for (var i = 0; i < objs.Length; i++)
			{
				var o = objs[i];
				if (!o || (!o.IgnoreLODIndex && o.LODIndex != 0)) continue;
				var smr = o.GetComponent<SkinnedMeshRenderer>();
				var mf = o.GetComponent<MeshFilter>();
				var mesh = smr ? smr.sharedMesh : mf ? mf.sharedMesh : null;
				if (!mesh) continue;

				var em = s.ElementMaterials;
				var src = em != null && i < em.Length && em[i].OverrideDefaults ? em[i].Materials : s.DefaultMaterials;
				var mats = new List<Material>(src ?? new Material[0]);
				// Faces are extra material slots past the submesh count; Unity redraws the last submesh with each.
				if (s.SupportsFaces && s.FaceRules != null && i < s.FaceRules.Length)
				{
					if (s.FaceRules[i].ShowEyes && eyes) mats.Add(eyes);
					if (s.FaceRules[i].ShowMouth && mouth) mats.Add(mouth);
				}

				var bind = smr ? mesh.bindposes : null;
				var ri = bind != null && o.AttachedBones != null ? System.Array.IndexOf(o.AttachedBones, o.RootBone) : -1;
				Matrix4x4 m;
				if (ri >= 0 && ri < bind.Length && bones.TryGetValue(o.RootBone, out var rb))
					m = toLocal * rb.localToWorldMatrix * bind[ri];
				else
				{
					var parent = o.ParentBone != SlimeAppearance.SlimeBone.None && bones.TryGetValue(o.ParentBone, out var pb) ? pb : rootT;
					var lt = o.transform;
					m = toLocal * parent.localToWorldMatrix * Matrix4x4.TRS(lt.localPosition, lt.localRotation, lt.localScale);
				}
				parts.Add((mesh, mats.ToArray(), m));
			}
		}
		if (parts.Count == 0) return 0;

		var b = new Bounds();
		var first = true;
		foreach (var (mesh, _, m) in parts)
		{
			var mb = mesh.bounds;
			for (var c = 0; c < 8; c++)
			{
				var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) * 2 - 1, (c & 2) - 1, (c & 4) / 2 - 1)));
				if (first) b = new Bounds(p, Vector3.zero);
				else b.Encapsulate(p);
				first = false;
			}
		}
		if (b.size.y < 0.01f) return 0;

		var k = Height / b.size.y;
		head = new GameObject("tilt").transform;
		head.SetParent(body, false);
		head.localPosition = new Vector3(0, Height * 0.5f, 0);
		var model = new GameObject("model").transform;
		model.SetParent(head, false);
		model.localScale = Vector3.one * k;
		model.localPosition = new Vector3(-b.center.x * k, -b.min.y * k - Height * 0.5f, -b.center.z * k);

		foreach (var (mesh, mats, m) in parts)
		{
			var go = new GameObject(mesh.name);
			go.transform.SetParent(model, false);
			go.transform.localPosition = m.GetColumn(3);
			go.transform.localRotation = m.rotation;
			go.transform.localScale = m.lossyScale;
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			go.AddComponent<MeshRenderer>().sharedMaterials = mats;
		}
		pitchScale = 0.2f;
		return Height;
	}

	float BuildPrimitive()
	{
		var torso = Part(PrimitiveType.Capsule, body, new Vector3(0, 0.55f, 0), new Vector3(0.6f, 0.55f, 0.6f));
		torso.GetComponent<Renderer>().material.color = new Color(0.06f, 0.06f, 0.06f);
		head = Part(PrimitiveType.Cube, body, new Vector3(0, 1.32f, 0), new Vector3(0.42f, 0.42f, 0.42f));
		Part(PrimitiveType.Cube, head, new Vector3(0, 0, 0.55f), new Vector3(0.6f, 0.2f, 0.2f)); // visor = look dir
		pitchScale = 1f;
		return 1.55f;
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
		var dt = Time.unscaledDeltaTime;
		if (snaps.Count > 0)
		{
			var t = Time.unscaledTime - InterpDelay;
			while (snaps.Count > 2 && snaps[1].t <= t) snaps.RemoveAt(0);

			Snap a = snaps[0], b = snaps.Count > 1 ? snaps[1] : snaps[0];
			var k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 1f;
			var pos = Vector3.Lerp(a.pos, b.pos, k);
			var d = pos - transform.position;
			if (dt > 0) speed = Mathf.Lerp(speed, new Vector2(d.x, d.z).magnitude / dt, 1f - Mathf.Exp(-8f * dt));
			transform.position = pos;
			transform.rotation = Quaternion.Euler(0, Mathf.LerpAngle(a.yaw, b.yaw, k), 0);
			head.localRotation = Quaternion.Euler(Mathf.DeltaAngle(0, Mathf.LerpAngle(a.pitch, b.pitch, k)) * pitchScale, 0, 0);
		}

		// Idle breathing squash; quicker hops while moving.
		var mv = Mathf.Clamp01(speed / 7f);
		phase += dt * Mathf.Lerp(2.2f, 9f, mv);
		var s = Mathf.Sin(phase) * Mathf.Lerp(0.03f, 0.07f, mv);
		body.localScale = new Vector3(1f - s * 0.5f, 1f + s, 1f - s * 0.5f);
		body.localPosition = new Vector3(0, Mathf.Max(0, -Mathf.Cos(phase)) * 0.08f * mv, 0);

		var cam = Camera.main;
		if (cam) label.rotation = Quaternion.LookRotation(label.position - cam.transform.position);
	}
}
