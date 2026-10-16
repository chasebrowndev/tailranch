using System.Collections.Generic;
using System.Linq;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Visual-only Beatrix (the ranch house menu's rigged model, idles only), else a gold slime rebuilt
// from appearance data; actor prefabs are never instantiated (that would register them, run AI
// and physics). Player pivot is at the feet.
public class RemoteAvatar : MonoBehaviour
{
	public static readonly Color Gold = new Color(0.83f, 0.65f, 0.17f);
	const float InterpDelay = 0.1f;
	const float Height = 1.1f, HumanHeight = 1.75f;
	// Full walk and full sprint, m/s: the controller's Default and Run presets (acceleration 0.25/0.45 at damping 0.15).
	const float WalkSpeed = 10f, RunSpeed = 18f;

	struct Snap
	{
		public float t;
		public Vector3 pos;
		public float yaw, pitch;
	}

	readonly List<Snap> snaps = new();
	Transform body, head, label;
	TextMesh tag, shade;
	// Nametag: warm white over a soft dark under-copy so it holds over a bright sky; the slime itself carries the gold.
	static readonly Color TagInk = new(1f, 0.97f, 0.9f), ShadeInk = new(0, 0, 0, 0.55f);
	static readonly Vector3 ShadeOffset = new(0.012f, -0.012f, 0.01f);
	float pitchScale = 1f, phase, speed, tagAlpha = 1f, nextOcclude, vy, look, air;
	bool occluded, human;
	// Beatrix only has idles: the walk, run, jump and look are bone offsets layered on top in LateUpdate.
	Transform lThigh, lCalf, rThigh, rCalf, lArm, lFore, rArm, rFore, headBone;

	// Their vac, mirrored from PlayerSync: the game's own cone, spiral/dial colors and shot/capture effects.
	public struct VacView
	{
		public bool cone, holding;
		public float spiral, fullness;
		public Color32 ammo;
		public byte shots, captures;
		public long heldId;
	}
	public static readonly int SpiralColorId = PropertyId("PROPERTY_SPIRAL_COLOR"), AmmoFullnessId = PropertyId("PROPERTY_AMMO_FULLNESS"), AmmoColorId = PropertyId("PROPERTY_AMMO_COLOR");
	static int PropertyId(string name) => (int)HarmonyLib.AccessTools.Field(typeof(VacColorAnimator), name).GetValue(null);
	VacView vac;
	bool vacHeard;
	Transform vacAttach, spiralBone, triggerBone, muzzle;
	Quaternion spiralRest, triggerRest;
	Vector3 spiralAxis, triggerAxis;
	Material vacMat, dialMat;
	GameObject cone, shootFX, captureFX;
	float spin, spinAngle, squeeze, kick, heldRad;
	long heldRadOf;
	bool humming;
	SECTR_AudioCueInstance hum;
	// The local vac's cues, copied once and made positional: the first-person ones are tuned to sit on the camera.
	static SECTR_AudioCue startCue, runCue, endCue, shootCue, heldCue;

	public static RemoteAvatar Create(Player p)
	{
		var root = new GameObject($"TailRanch Player {p.Id}");
		var a = root.AddComponent<RemoteAvatar>();
		a.body = new GameObject("body").transform;
		a.body.SetParent(root.transform, false);
		a.phase = Random.value * 10f;

		float top = 0;
		try { top = a.BuildBeatrix(); }
		catch (System.Exception e) { Plugin.Log.LogWarning($"beatrix avatar: {e.Message}"); }
		if (top <= 0)
		{
			if (a.head) Destroy(a.head.gameObject);
			try { top = a.BuildSlime(); }
			catch (System.Exception e) { Plugin.Log.LogWarning($"slime avatar: {e.Message}"); }
		}
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
		tm.color = TagInk;
		var under = new GameObject("shade");
		under.transform.SetParent(text.transform, false);
		under.transform.localPosition = ShadeOffset; // behind along the label's forward, which faces away from the camera
		var sm = under.AddComponent<TextMesh>();
		sm.text = tm.text;
		sm.font = tm.font;
		under.GetComponent<MeshRenderer>().material = tm.font.material;
		sm.fontSize = tm.fontSize;
		sm.characterSize = tm.characterSize;
		sm.anchor = tm.anchor;
		sm.color = ShadeInk;
		a.label = text.transform;
		a.tag = tm;
		a.shade = sm;
		return a;
	}

	public void SetName(string name) => tag.text = shade.text = name;

	static GameObject beatrixPrefab;

	// RanchHouseUI isn't referenced from code: the house's activator holds the prefab, so it's in memory once the world loads.
	float BuildBeatrix()
	{
		if (!beatrixPrefab)
			foreach (var ui in Resources.FindObjectsOfTypeAll<RanchHouseUI>())
				if (ui.beatrixPrefab) { beatrixPrefab = ui.beatrixPrefab; break; }
		if (!beatrixPrefab) return 0;

		var anim = beatrixPrefab.GetComponentInChildren<Animator>(true);
		if (!anim) return 0;
		// Facing: the menu camera looks at her face, so she faces it.
		var cam = beatrixPrefab.GetComponentInChildren<Camera>(true);
		var src = anim.transform;
		var face = cam ? Vector3.ProjectOnPlane(cam.transform.position - src.position, Vector3.up) : src.forward;
		if (face.sqrMagnitude < 1e-4f) face = src.forward;

		head = new GameObject("tilt").transform;
		head.SetParent(body, false);
		var model = Instantiate(src.gameObject, head, false);
		model.name = "beatrix";
		model.transform.localPosition = Vector3.zero;
		model.transform.localRotation = Quaternion.Inverse(Quaternion.LookRotation(face)) * src.rotation;
		model.transform.localScale = src.lossyScale;
		// Visual only: no cameras, lights, audio, colliders or scripts besides the animator.
		// Joints first: the ponytail is a physics chain, and a Rigidbody can't be removed while a joint needs it.
		foreach (var j in model.GetComponentsInChildren<Joint>(true)) DestroyImmediate(j);
		foreach (var c in model.GetComponentsInChildren<Component>(true))
		{
			if (c is Transform || c is Animator || c is Renderer || c is MeshFilter) continue;
			if (c is Behaviour bh) bh.enabled = false;
			if (c is Collider || c is Rigidbody || c is Camera || c is Light || c is AudioSource || c is AudioListener || c is MonoBehaviour)
				DestroyImmediate(c);
		}
		foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0; // her menu layer is culled by the main camera
		var a = model.GetComponent<Animator>();
		a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
		a.updateMode = AnimatorUpdateMode.UnscaledTime;
		a.SetInteger("IdleIndex", Random.Range(0, 8));
		model.SetActive(true);

		var rs = model.GetComponentsInChildren<Renderer>(true);
		if (rs.Length == 0) return 0;
		var b = rs[0].bounds;
		foreach (var r in rs)
		{
			if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
			b.Encapsulate(r.bounds);
		}
		if (b.size.y < 0.01f) return 0;
		var k = HumanHeight / b.size.y;
		model.transform.localScale *= k;
		var c0 = head.InverseTransformPoint(b.center);
		var lo = head.InverseTransformPoint(b.min).y;
		model.transform.localPosition = new Vector3(-c0.x * k, -lo * k, -c0.z * k);
		FindBones(a);
		try { BuildVac(model); }
		catch (System.Exception e) { Plugin.Log.LogWarning($"avatar vac: {e.Message}"); }
		Plugin.Log.LogInfo($"beatrix avatar: {rs.Length} renderers, scale {k:0.###}, humanoid {a.isHuman}, bones " +
			string.Join(" ", new[] { lThigh, lCalf, rThigh, rCalf, lArm, lFore, rArm, rFore, headBone }.Select(t => t ? t.name : "-")));
		human = true;
		pitchScale = 0f;
		return HumanHeight;
	}

	// The menu Beatrix holds a vac (rWristJ/vacAttach, barrel along +Z). Give it a muzzle in the game's "vac shape"
	// convention (up = where it sucks and shoots, world scale 1) and a copy of the local vac's cone under it.
	void BuildVac(GameObject model)
	{
		vacAttach = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "vacAttach");
		if (!vacAttach) return;
		Transform Bone(string n) => vacAttach.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
		spiralBone = Bone("bone_spiral");
		triggerBone = Bone("bone_trigger");
		if (spiralBone) { spiralRest = spiralBone.localRotation; spiralAxis = spiralBone.InverseTransformDirection(vacAttach.forward); }
		if (triggerBone) { triggerRest = triggerBone.localRotation; triggerAxis = triggerBone.InverseTransformDirection(vacAttach.right); }
		foreach (var r in vacAttach.GetComponentsInChildren<Renderer>(true))
			if (r.name == "vac") vacMat = r.material;
			else if (r.name == "Vac Display") dialMat = r.material;
		var barrel = Bone("bone_barrel");
		muzzle = new GameObject("muzzle").transform;
		muzzle.SetParent(vacAttach, false);
		muzzle.position = (barrel ? barrel.position : vacAttach.position) + vacAttach.forward * 0.15f;
		muzzle.localRotation = Quaternion.FromToRotation(Vector3.up, Vector3.forward);
		muzzle.localScale = Vector3.one / vacAttach.lossyScale.x;

		var wv = SRSingleton<SceneContext>.Instance.Player.GetComponentInChildren<WeaponVacuum>(true);
		if (!wv) return;
		shootFX = wv.shootFX;
		captureFX = wv.captureFX;
		if (!runCue)
		{
			startCue = Positional(wv.vacStartCue); runCue = Positional(wv.vacRunCue); endCue = Positional(wv.vacEndCue);
			shootCue = Positional(wv.vacShootCue); heldCue = Positional(wv.vacHeldCue);
		}
		var fx = HarmonyLib.AccessTools.FieldRefAccess<WeaponVacuum, GameObject>("vacFX")(wv);
		if (!fx) return;
		cone = Instantiate(fx, muzzle, false);
		cone.name = "cone";
		cone.transform.localPosition = fx.transform.localPosition;
		cone.transform.localRotation = fx.transform.localRotation;
		cone.transform.localScale = fx.transform.localScale;
		foreach (var t in cone.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0; // the first-person vac draws on its own camera's layer
		cone.SetActive(false);
	}

	static SECTR_AudioCue Positional(SECTR_AudioCue c)
	{
		if (!c) return null;
		var p = Instantiate(c);
		p.Spatialization = SECTR_AudioCue.Spatializations.Local3D;
		p.MinDistance = 3f;
		p.MaxDistance = 40f;
		return p;
	}

	void Sound(SECTR_AudioCue cue) { if (cue && muzzle) SECTR_AudioSystem.Play(cue, muzzle, Vector3.zero, false); }

	void OnDestroy() => hum.Stop(true);

	void FindBones(Animator a)
	{
		if (a.isHuman)
		{
			lThigh = a.GetBoneTransform(HumanBodyBones.LeftUpperLeg); lCalf = a.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
			rThigh = a.GetBoneTransform(HumanBodyBones.RightUpperLeg); rCalf = a.GetBoneTransform(HumanBodyBones.RightLowerLeg);
			lArm = a.GetBoneTransform(HumanBodyBones.LeftUpperArm); lFore = a.GetBoneTransform(HumanBodyBones.LeftLowerArm);
			rArm = a.GetBoneTransform(HumanBodyBones.RightUpperArm); rFore = a.GetBoneTransform(HumanBodyBones.RightLowerArm);
			headBone = a.GetBoneTransform(HumanBodyBones.Head);
			return;
		}
		// Generic rig: match common bone names.
		var all = a.GetComponentsInChildren<Transform>(true);
		Transform Find(bool left, params string[] keys) => all.FirstOrDefault(t =>
		{
			var n = t.name.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace(".", "");
			if (!keys.Any(n.Contains)) return false;
			var l = n.StartsWith("l") && !n.StartsWith("leg") && !n.StartsWith("low") || n.Contains("left") || n.EndsWith("l");
			var r = n.StartsWith("r") || n.Contains("right") || n.EndsWith("r");
			return left ? l && !r : r && !l;
		});
		lThigh = Find(true, "thigh", "upleg", "upperleg"); lCalf = Find(true, "calf", "shin", "knee", "lowerleg");
		rThigh = Find(false, "thigh", "upleg", "upperleg"); rCalf = Find(false, "calf", "shin", "knee", "lowerleg");
		lArm = Find(true, "upperarm", "uparm"); lFore = Find(true, "forearm", "lowerarm", "elbow");
		rArm = Find(false, "upperarm", "uparm"); rFore = Find(false, "forearm", "lowerarm", "elbow");
		headBone = all.FirstOrDefault(t => t.name.ToLowerInvariant().EndsWith("head"));
		Plugin.Log.LogInfo("beatrix rig: " + string.Join(",", all.Select(t => t.name)));
	}

	static void Turn(Transform t, float deg, Vector3 axis)
	{
		if (t) t.rotation = Quaternion.AngleAxis(deg, axis) * t.rotation;
	}

	// After the Animator: walk blending into a run on the ground; in the air the stride stops and she
	// takes a jump pose (knee up) while rising, legs bent and arms up while falling or hovering.
	void LateUpdate()
	{
		if (!human) return;
		var right = transform.right;
		var mv = Mathf.Clamp01(speed / WalkSpeed);
		var run = Mathf.Clamp01((speed - WalkSpeed) / (RunSpeed - WalkSpeed));
		var w = mv * (1f - air);
		var rise = air * Mathf.Clamp01(vy / 3f);
		var fall = air - rise;
		var sw = Mathf.Sin(phase);
		var cs = Mathf.Cos(phase);
		float Lr(float walk, float sprint) => Mathf.Lerp(walk, sprint, run);
		var thigh = Lr(38f, 55f) * w * sw;
		var kneeL = (Lr(55f, 100f) * Mathf.Max(0, cs) + Lr(10f, 20f)) * w;
		var kneeR = (Lr(55f, 100f) * Mathf.Max(0, -cs) + Lr(10f, 20f)) * w;
		// Negative angle about right = swing forward.
		Turn(lThigh, -thigh - 55f * rise - 25f * fall, right);
		Turn(rThigh, thigh + 15f * rise - 10f * fall, right);
		Turn(lCalf, kneeL + 80f * rise + 40f * fall, right);
		Turn(rCalf, kneeR + 35f * rise + 30f * fall, right);
		Turn(lArm, Lr(28f, 50f) * w * sw + 25f * rise - 50f * fall, right);
		Turn(lFore, -Lr(20f, 85f) * w - 30f * air, right);
		Turn(headBone, Mathf.Clamp(look, -40f, 40f) * 0.6f, right);
		if (!vacAttach)
		{
			Turn(rArm, -Lr(28f, 50f) * w * sw - 35f * rise - 50f * fall, right);
			Turn(rFore, -Lr(20f, 85f) * w - 30f * air, right);
			return;
		}
		// The vac arm doesn't swing: whatever the idle, stride or jump did, the shoulder turns so the barrel points
		// where they look, so the cone and shots line up with what they're vacuuming. Recoil kicks it up.
		var aim = Quaternion.Euler(look - 12f * kick, transform.eulerAngles.y, 0) * Vector3.forward;
		if (rArm) rArm.rotation = Quaternion.FromToRotation(vacAttach.forward, aim) * rArm.rotation;
		if (spiralBone) spiralBone.localRotation = spiralRest * Quaternion.AngleAxis(spinAngle, spiralAxis);
		if (triggerBone) triggerBone.localRotation = triggerRest * Quaternion.AngleAxis(-25f * squeeze, triggerAxis);
		// What they hold rides their barrel instead of trailing their first-person copy's stream: the game pins
		// it one radius out along the vac's up (lock joint) and turns it to face the holder.
		if (vac.heldId != 0 && muzzle && ActorSync.Find(vac.heldId) is { } h && ActorSync.IsRemoteOwned(h))
		{
			if (heldRadOf != vac.heldId) { heldRadOf = vac.heldId; heldRad = PhysicsUtil.RadiusOfObject(h); }
			h.transform.position = muzzle.position + muzzle.up * heldRad;
			h.transform.LookAt(headBone ? headBone.position : transform.position + Vector3.up * HumanHeight, Vector3.up);
		}
	}

	// Their vac: cone and colors follow the latest state; the spiral spins up, the trigger squeezes, shots kick.
	void UpdateVac(float dt)
	{
		if (!vacAttach) return;
		var on = vac.cone;
		if (cone && cone.activeSelf != on) cone.SetActive(on);
		if (vacMat) vacMat.SetFloat(SpiralColorId, vac.spiral);
		if (dialMat)
		{
			dialMat.SetFloat(SpiralColorId, vac.spiral);
			dialMat.SetFloat(AmmoFullnessId, vac.fullness);
			dialMat.SetColor(AmmoColorId, vac.ammo);
		}
		spin = Mathf.MoveTowards(spin, on ? 1f : 0f, dt * 3f);
		spinAngle = (spinAngle + dt * 1080f * spin) % 360f;
		squeeze = Mathf.MoveTowards(squeeze, on || vac.holding ? 1f : 0f, dt * 10f);
		kick = Mathf.MoveTowards(kick, 0f, dt * 5f);
		// Same as the game's VacAudioHandler: start + looping run while vacuuming (holding counts), end when let go.
		if (humming != (on || vac.holding))
		{
			humming = !humming;
			if (Plugin.DevProbe) Plugin.Log.LogInfo($"probe client: vac sound {(humming ? "on" : "off")}, cues {(bool)startCue}{(bool)runCue}{(bool)endCue}{(bool)shootCue}");
			if (humming)
			{
				Sound(startCue);
				if (runCue && muzzle) hum = SECTR_AudioSystem.Play(runCue, muzzle, Vector3.zero, true);
			}
			else
			{
				hum.Stop(false);
				Sound(endCue);
			}
		}
	}

	void PlayVacEvents(VacView v)
	{
		if (vacHeard && muzzle)
		{
			for (var n = Mathf.Min((byte)(v.shots - vac.shots), 3); n > 0; n--)
			{
				if (shootFX) SRBehaviour.SpawnAndPlayFX(shootFX, muzzle.position, muzzle.rotation);
				Sound(shootCue);
				kick = 1f;
			}
			for (var n = Mathf.Min((byte)(v.captures - vac.captures), 3); n > 0; n--)
				if (captureFX) SRBehaviour.SpawnAndPlayFX(captureFX, muzzle.position, muzzle.rotation);
			if (v.holding && !vac.holding) Sound(heldCue);
		}
		vac = v;
		vacHeard = true;
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
		Object.DestroyImmediate(go.GetComponent<Collider>()); // deferred Destroy would leave a solid collider for a frame
		go.transform.SetParent(parent, false);
		go.transform.localPosition = pos;
		go.transform.localScale = scale;
		go.GetComponent<Renderer>().material.color = Gold;
		return go.transform;
	}

	public void Push(Vector3 pos, float yaw, float pitch, VacView v)
	{
		PlayVacEvents(v);
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
			if (dt > 0)
			{
				speed = Mathf.Lerp(speed, new Vector2(d.x, d.z).magnitude / dt, 1f - Mathf.Exp(-8f * dt));
				vy = Mathf.Lerp(vy, d.y / dt, 1f - Mathf.Exp(-8f * dt));
			}
			transform.position = pos;
			transform.rotation = Quaternion.Euler(0, Mathf.LerpAngle(a.yaw, b.yaw, k), 0);
			look = Mathf.DeltaAngle(0, Mathf.LerpAngle(a.pitch, b.pitch, k));
			head.localRotation = Quaternion.Euler(look * pitchScale, 0, 0);
		}

		var mv = Mathf.Clamp01(speed / 7f);
		if (human)
		{
			// Airborne = nothing under the feet (the avatar has no colliders of its own), not vertical speed:
			// a jump's apex or a jetpack hover has none either.
			var grounded = Physics.Raycast(transform.position + Vector3.up * 0.3f, Vector3.down, 0.6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
			air = Mathf.MoveTowards(air, grounded ? 0f : 1f, dt * 6f);
			// Lean into the run with a stride bob; the limbs follow phase in LateUpdate. The stride holds in the air.
			var run = Mathf.Clamp01((speed - WalkSpeed) / (RunSpeed - WalkSpeed));
			mv = Mathf.Clamp01(speed / WalkSpeed);
			phase += dt * Mathf.Lerp(0f, 10f, mv) * Mathf.Lerp(1f, 1.5f, run) * (1f - air);
			body.localRotation = Quaternion.Euler(Mathf.Lerp(6f, 15f, run) * mv, 0, 0);
			body.localPosition = new Vector3(0, Mathf.Abs(Mathf.Sin(phase)) * Mathf.Lerp(0.06f, 0.1f, run) * mv * (1f - air), 0);
			UpdateVac(dt);
		}
		else
		{
		// Idle breathing squash; quicker hops while moving.
		phase += dt * Mathf.Lerp(2.2f, 9f, mv);
		var s = Mathf.Sin(phase) * Mathf.Lerp(0.03f, 0.07f, mv);
		body.localScale = new Vector3(1f - s * 0.5f, 1f + s, 1f - s * 0.5f);
		body.localPosition = new Vector3(0, Mathf.Max(0, -Mathf.Cos(phase)) * 0.08f * mv, 0);
		}

		var cam = Camera.main;
		if (!cam) return;
		label.rotation = Quaternion.LookRotation(label.position - cam.transform.position);
		// The font shader draws through walls: dim the tag when occluded, fade it out with distance.
		var from = cam.transform.position;
		if (Time.unscaledTime > nextOcclude)
		{
			nextOcclude = Time.unscaledTime + 0.2f;
			occluded = Physics.Linecast(from, label.position, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
		}
		var dist = Vector3.Distance(from, label.position);
		var want = (occluded ? 0.6f : 1f) * (1f - Mathf.InverseLerp(80f, 300f, dist));
		tagAlpha = Mathf.Lerp(tagAlpha, want, 1f - Mathf.Exp(-8f * dt));
		tag.color = new Color(TagInk.r, TagInk.g, TagInk.b, tagAlpha);
		shade.color = new Color(0, 0, 0, ShadeInk.a * tagAlpha);
	}
}
