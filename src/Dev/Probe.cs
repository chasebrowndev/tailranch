using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TailRanch.Net;
using TailRanch.Sync;
using UnityEngine;
using SetId = MonomiPark.SlimeRancher.Regions.RegionRegistry.RegionSetId;

namespace TailRanch.Dev;

// TAILRANCH_PROBE=1 (scripts/sr.sh): scripted checks for per-player vac, deaths, chickens, gardens and the
// Beatrix walk. Both sides time from the client becoming ready; screenshots land next to Player.log.
public static class Probe
{
	static float zero = -1f;
	static int next;
	static readonly List<(float at, string name, Action run)> steps = new();
	static GameObject probeRunner;
	static float walkUntil, beat;
	static Vector3 walkCenter;

	static SceneContext Ctx => SRSingleton<SceneContext>.Instance;
	static string Me => Session.IsHost ? "host" : "client";
	static void Log(string s) => Plugin.Log.LogInfo($"probe {Me} {Time.unscaledTime - zero:0.0}s: {s}");

	public static void Tick()
	{
		if (!Plugin.DevProbe) return;
		if (Time.unscaledTime >= beat) { beat = Time.unscaledTime + 5f; Plugin.Log.LogInfo($"probe beat frame {Time.frameCount} t {Time.unscaledTime:0} dt {Time.deltaTime:0.000} scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}"); }
		if (!Session.InWorld) return;
		if (Session.IsHost && zero < 0f && next == 0 && steps.Count == 0)
		{
			// Before anyone joins: the host's vac holds carrots a joiner must not inherit.
			for (var i = 0; i < 20; i++) Ctx.PlayerState.Ammo.MaybeAddToSlot(Identifiable.Id.CARROT_VEGGIE, null);
			steps.Add((0, "seeded", () => { }));
			Plugin.Log.LogInfo($"probe host: seeded vac {Vac()}");
		}
		var live = Session.IsHost ? Session.Players.Values.Any(p => p.Ready) : Session.InRemoteWorld;
		if (!live) return;
		var t = Time.unscaledTime;
		if (zero < 0f)
		{
			zero = t;
			steps.Clear();
			Build();
		}
		while (next < steps.Count && t - zero >= steps[next].at)
		{
			var (_, name, run) = steps[next++];
			try { run(); }
			catch (Exception e) { Log($"{name} FAILED {e}"); }
		}
		if (t < walkUntil) Walk(t);
	}

	static void Build()
	{
		if (Session.IsHost)
		{
			At(0.5f, "vac modes", () => Log("vac modes " + string.Join(",", Enum.GetNames(VacModeType))));
			At(5, "vac show", () =>
			{
				var av = UnityEngine.Object.FindObjectOfType<RemoteAvatar>();
				var f = Vector3.ProjectOnPlane(av.transform.forward, Vector3.up).normalized;
				walkCenter = av.transform.position + f * 6f;
				walkSide = Vector3.Cross(Vector3.up, f);
				walkStart = Time.unscaledTime;
				walkUntil = walkStart + 26f;
			});
			// A largo right at the nozzle as the vac comes on: it gets held, carried through the walk and run, then shot.
			At(7.8f, "largo", () =>
			{
				var o = Ctx.Player.GetComponentInChildren<WeaponVacuum>().vacOrigin.transform;
				var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(Identifiable.Id.PINK_ROCK_LARGO);
				SRBehaviour.InstantiateActor(prefab, SetId.HOME, o.position + o.up * 1.2f, Quaternion.identity);
			});
			foreach (var at in new[] { 9f, 15f, 21f, 27f, 29f })
				At(at, "held", () => { var h = HarmonyLib.AccessTools.Field(typeof(WeaponVacuum), "held").GetValue(Ctx.Player.GetComponentInChildren<WeaponVacuum>()) as GameObject; Log($"held {(h ? h.name + " id " + ActorSync.IdOf(h) : "nothing")}"); });
		}
		else
		{
			// Host (from ~8 s here): stand 0-3, vac + largo 3-6 (level), 6-8 (looking down 30), 8-11 level, walk 11-17, run 17-23, shoot 23-26.
			var plan = new List<(float, string)> { (10f, "idle"), (12.5f, "hold"), (13.5f, "hold"), (15.5f, "hold down"), (18f, "hold"),
				(21f, "walk hold"), (22.5f, "walk hold"), (27f, "run hold"), (28.5f, "run hold"), (31.3f, "shoot"), (31.6f, "shoot"), (32.5f, "shoot") };
			foreach (var (at, what) in plan.OrderBy(x => x.Item1))
			{
				var n = ++shots;
				At(at - 0.05f, $"aim {n}", Aim);
				At(at, $"shot {n}", () => { Shot(n); Log($"shot {n} {what}: {AvatarState()} {LargoState()}"); });
			}
		}
		At(36, "done", () => Log("done"));
	}

	static Vector3 walkSide;
	static readonly Type VacModeType = HarmonyLib.AccessTools.Inner(typeof(WeaponVacuum), "VacMode");
	public static string ForceVac; // probe only: a VacMode name held regardless of input

	[HarmonyLib.HarmonyPatch(typeof(WeaponVacuum), "UpdateVacModeForInputs")]
	static class HoldVacMode
	{
		static bool Prefix(WeaponVacuum __instance)
		{
			if (!Plugin.DevProbe || ForceVac == null) return true;
			HarmonyLib.AccessTools.Field(typeof(WeaponVacuum), "vacMode").SetValue(__instance, Enum.Parse(VacModeType, ForceVac));
			return false;
		}
	}

	static string PathTo(Transform t, Transform root)
	{
		var s = t.name;
		while (t != root && t.parent) { t = t.parent; s = t.name + "/" + s; }
		return s;
	}

	static void VacDump()
	{
		var vac = Ctx.Player.GetComponentInChildren<WeaponVacuum>(true);
		var root = vac.transform;
		Log($"vac at {PathTo(root, Ctx.Player.transform)}");
		foreach (var f in typeof(WeaponVacuum).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
		{
			var v = f.GetValue(vac);
			var str = v is UnityEngine.Object o ? (o ? o.name : "null") : v?.ToString();
			Log($"vac field {f.FieldType.Name} {f.Name} = {str}");
		}
		foreach (var an in root.GetComponentsInChildren<Animator>(true))
			Log($"vac animator {PathTo(an.transform, root)} ctrl {(an.runtimeAnimatorController ? an.runtimeAnimatorController.name : "-")} params " +
				string.Join(", ", an.parameters.Select(x => $"{x.type} {x.name}")));
		foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
			Log($"vac particles {PathTo(ps.transform, root)} active {ps.gameObject.activeInHierarchy}");
		foreach (var r in root.GetComponentsInChildren<Renderer>(true))
			Log($"vac renderer {r.GetType().Name} {PathTo(r.transform, root)} mats " + string.Join(",", r.sharedMaterials.Where(m => m).Select(m => m.name + ":" + m.shader.name)));
		foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
			Log($"vac script {mb.GetType().Name} on {PathTo(mb.transform, root)}");
	}

	static void Fields(object o, string tag)
	{
		const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
		foreach (var f in o.GetType().GetFields(all))
		{
			var v = f.GetValue(o);
			var str = v is UnityEngine.Object uo ? (uo ? uo.name : "null") : v is System.Collections.IEnumerable en && !(v is string) ? "[" + string.Join(",", en.Cast<object>().Select(x => x is UnityEngine.Object xo ? (xo ? xo.name : "null") : x?.ToString())) + "]" : v?.ToString();
			Log($"{tag} field {f.FieldType.Name} {f.Name} = {str}");
		}
		Log($"{tag} methods " + string.Join(",", o.GetType().GetMethods(all).Select(m => m.Name)));
	}

	static void VacDump2()
	{
		var vac = Ctx.Player.GetComponentInChildren<WeaponVacuum>(true);
		var color = vac.GetComponentInChildren<VacColorAnimator>(true);
		Fields(color, "colorAnim");
		var fx = (GameObject)HarmonyLib.AccessTools.Field(typeof(WeaponVacuum), "vacFX").GetValue(vac);
		Log($"vacFX path {PathTo(fx.transform, Ctx.Player.transform)} active {fx.activeSelf} scale {fx.transform.lossyScale} vacscale {vac.transform.lossyScale}");
		foreach (var c in fx.GetComponentsInChildren<Component>(true))
			Log($"vacFX comp {c.GetType().Name} on {PathTo(c.transform, fx.transform)}");
		var origin = (GameObject)HarmonyLib.AccessTools.Field(typeof(WeaponVacuum), "vacOrigin").GetValue(vac);
		Log($"vacOrigin {PathTo(origin.transform, Ctx.Player.transform)} vacFX local to origin {origin.transform.InverseTransformPoint(fx.transform.position)}");
		var shoot = (GameObject)HarmonyLib.AccessTools.Field(typeof(WeaponVacuum), "shootFX").GetValue(vac);
		Log($"shootFX scene {shoot.scene.IsValid()} comps " + string.Join(",", shoot.GetComponentsInChildren<Component>(true).Select(c => c.GetType().Name).Distinct()));

		// The menu Beatrix's vac, before the avatar strips it.
		GameObject prefab = null;
		foreach (var ui in Resources.FindObjectsOfTypeAll<RanchHouseUI>()) if (ui.beatrixPrefab) { prefab = ui.beatrixPrefab; break; }
		var attach = prefab.GetComponentsInChildren<Transform>(true).First(t => t.name == "vacAttach");
		foreach (var c in attach.GetComponentsInChildren<Component>(true))
			Log($"menu vac comp {c.GetType().Name} on {PathTo(c.transform, attach)}");
		foreach (var r in attach.GetComponentsInChildren<Renderer>(true))
			Log($"menu vac renderer {r.name} mats " + string.Join(",", r.sharedMaterials.Where(m => m).Select(m => m.name + ":" + m.shader.name)));

		// Avatar vac geometry in vacAttach space.
		var av = UnityEngine.Object.FindObjectOfType<RemoteAvatar>();
		var at = av.GetComponentsInChildren<Transform>(true).First(t => t.name == "vacAttach");
		foreach (var t in at.GetComponentsInChildren<Transform>(true))
			Log($"avatar vac bone {t.name} local {at.InverseTransformPoint(t.position)}");
		var vr = at.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "vac");
		Log($"avatar vac bounds center {at.InverseTransformPoint(vr.bounds.center)} size {vr.bounds.size} attach scale {at.lossyScale}");
		Log($"avatar vac in model space: barrel-vac dir {av.transform.InverseTransformDirection(at.Find("Vac/bone_vac/bone_barrel") ? at.Find("Vac/bone_vac/bone_barrel").position - at.Find("Vac/bone_vac").position : Vector3.zero)}");
	}

	// Strings, fields and calls a method's IL touches, in order.
	static void Il(Type t, string name)
	{
		foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly).Where(x => x.Name == name))
		{
			var il = m.GetMethodBody()?.GetILAsByteArray();
			if (il == null) continue;
			var mod = m.Module;
			var outp = new List<string>();
			for (var i = 0; i < il.Length - 4; i++)
			{
				var op = il[i];
				if (op != 0x72 && op != 0x28 && op != 0x6F && op != 0x7B && op != 0x7D && op != 0x7E && op != 0x73) continue;
				var tok = BitConverter.ToInt32(il, i + 1);
				try
				{
					if (op == 0x72) outp.Add("\"" + mod.ResolveString(tok) + "\"");
					else if (op == 0x7B || op == 0x7D || op == 0x7E) { var f = mod.ResolveField(tok); outp.Add((op == 0x7D ? "set " : "") + f.DeclaringType.Name + "." + f.Name); }
					else { var mm = mod.ResolveMethod(tok); outp.Add(mm.DeclaringType.Name + "." + mm.Name + "()"); }
					i += 4;
				}
				catch { }
			}
			Log($"IL {t.Name}.{name}: " + string.Join(" ", outp));
		}
	}

	static void RigDump()
	{
		var av = UnityEngine.Object.FindObjectOfType<RemoteAvatar>();
		foreach (var r in av.GetComponentsInChildren<Renderer>(true))
			Log($"avatar renderer {r.GetType().Name} {PathTo(r.transform, av.transform)} bones {(r is SkinnedMeshRenderer sm ? string.Join(",", sm.bones.Take(6).Select(x => x ? x.name : "-")) + $" ({sm.bones.Length})" : "-")}");
	}

	static int shots;
	static float walkStart;

	static string AvatarState()
	{
		var av = UnityEngine.Object.FindObjectOfType<RemoteAvatar>();
		float F(string n) => (float)HarmonyLib.AccessTools.Field(typeof(RemoteAvatar), n).GetValue(av);
		return $"speed {F("speed"):0.0} vy {F("vy"):0.0} air {F("air"):0.00}";
	}

	// The controller's state presets (Run, Crouch...): which motor values each one overrides.
	static void States()
	{
		var c = Ctx.Player.GetComponentInChildren<vp_FPController>();
		Log($"fixedDelta {Time.fixedDeltaTime} accel {c.MotorAcceleration} damping {c.MotorDamping}");
		foreach (var st in c.States)
		{
			var txt = st.TextAsset ? st.TextAsset.text : "";
			Log($"state {st.Name} enabled {st.Enabled}: " + string.Join(" | ", txt.Split('\n').Where(l => l.Contains("Motor")).Select(l => l.Trim())));
		}
	}

	// Client: look at the host's avatar.
	static void Aim()
	{
		var av = UnityEngine.Object.FindObjectOfType<RemoteAvatar>();
		var cam = Ctx.Player.GetComponentInChildren<vp_FPCamera>();
		var d = av.transform.position + Vector3.up * 0.9f - cam.transform.position;
		cam.SetRotation(new Vector2(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg));
	}

	// Host, before the gait test: the real walk and run speeds, from the player's own controller driven by fake input.
	class SpeedProbe : MonoBehaviour
	{
		float start;
		Vector3 mark;
		vp_FPInput input;
		vp_FPPlayerEventHandler ev;

		void Start()
		{
			start = Time.unscaledTime;
			input = Ctx.Player.GetComponentInChildren<vp_FPInput>();
			ev = Ctx.Player.GetComponentInChildren<vp_FPPlayerEventHandler>();
			input.enabled = false;
		}

		int stage;

		void Update()
		{
			var e = Time.unscaledTime - start;
			var p = Ctx.Player.transform.position;
			ev.InputAllowGameplay.Set(true);
			ev.InputMoveVector.Set(Vector2.up);
			if (stage == 0 && e >= 1f) { stage = 1; mark = p; Log($"allow {ev.InputAllowGameplay.Get()} throttle {ev.MotorThrottle.Get()} move {ev.InputMoveVector.Get()}"); }
			if (stage == 1 && e >= 2f) { stage = 2; Log($"walk speed {Flat(p - mark):0.00} m/s, throttle {ev.MotorThrottle.Get()}"); ev.Run.TryStart(); }
			if (stage == 2 && e >= 3f) { stage = 3; mark = p; }
			if (stage == 3 && e >= 4f)
			{
				Log($"run speed {Flat(p - mark):0.00} m/s, run active {ev.Run.Active}, throttle {ev.MotorThrottle.Get()}");
				ev.Run.TryStop();
				ev.InputMoveVector.Set(Vector2.zero);
				input.enabled = true;
				Destroy(gameObject);
			}
		}

		static float Flat(Vector3 d) => new Vector2(d.x, d.z).magnitude;
	}

	static Vector3 coopCenter;

	static LandPlot CoopPlot() => UnityEngine.Object.FindObjectsOfType<LandPlot>().FirstOrDefault(p => p.typeId == LandPlot.Id.COOP);

	// Host: an existing coop or a new one on an empty plot, packed with 10 hens.
	static void Coop()
	{
		var coop = CoopPlot();
		if (!coop)
		{
			var plot = UnityEngine.Object.FindObjectsOfType<LandPlot>().Where(p => p.typeId == LandPlot.Id.EMPTY)
				.OrderBy(p => (p.transform.position - Ctx.Player.transform.position).sqrMagnitude).First();
			var loc = plot.GetComponentInParent<LandPlotLocation>();
			coop = loc.Replace(plot, SRSingleton<GameContext>.Instance.LookupDirector.GetPlotPrefab(LandPlot.Id.COOP)).GetComponent<LandPlot>();
		}
		coopCenter = coop.transform.position;
		var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(Identifiable.Id.HEN);
		for (var i = 0; i < 10; i++)
			SRBehaviour.InstantiateActor(prefab, SetId.HOME, coopCenter + new Vector3(UnityEngine.Random.Range(-3f, 3f), 1f, UnityEngine.Random.Range(-3f, 3f)), Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0));
		Log($"coop at {coopCenter}, spawned 10 hens");
		var t = Ctx.Player.transform;
		tr(t, coopCenter + new Vector3(0, 1.5f, -9f), Quaternion.LookRotation(coopCenter - (coopCenter + new Vector3(0, 0, -9f))));
	}

	static void tr(Transform t, Vector3 pos, Quaternion rot) => t.SetPositionAndRotation(pos, rot);

	static void HenOwners()
	{
		var coop = CoopPlot();
		if (!coop) { Log("client: no coop"); return; }
		coopCenter = coop.transform.position;
		tr(Ctx.Player.transform, coopCenter + new Vector3(-9f, 1.5f, 0), Quaternion.LookRotation(Vector3.right));
		foreach (var g in HensNear(coopCenter))
		{
			var p = g.GetComponent<ActorPuppet>();
			var rb = g.GetComponent<Rigidbody>();
			Log($"hen puppet {(p ? "yes" : "no")} kin {(rb && rb.isKinematic)} anim {g.GetComponent<Animator>()?.enabled}");
		}
	}

	static IEnumerable<GameObject> HensNear(Vector3 c) => UnityEngine.Object.FindObjectsOfType<Identifiable>()
		.Where(i => i.id == Identifiable.Id.HEN && (i.transform.position - c).sqrMagnitude < 8f * 8f).Select(i => i.gameObject);

	// Every hen in the coop, every frame for 4 s: jerk (change of per-frame step), yaw reversals, facing alignment.
	class CrowdProbe : MonoBehaviour
	{
		class H { public Transform t; public Vector3 pos, step; public float yaw, dyaw, path, jerk; public int flips; }
		readonly List<H> hens = new();
		float end;
		int frames;

		void Start()
		{
			foreach (var g in HensNear(coopCenter))
				hens.Add(new H { t = g.transform, pos = g.transform.position, yaw = g.transform.eulerAngles.y });
			end = Time.unscaledTime + 4f;
		}

		void LateUpdate()
		{
			frames++;
			foreach (var h in hens)
			{
				if (!h.t) continue;
				var step = h.t.position - h.pos;
				h.jerk += (step - h.step).magnitude;
				h.path += step.magnitude;
				h.step = step;
				h.pos = h.t.position;
				var dy = Mathf.DeltaAngle(h.yaw, h.t.eulerAngles.y);
				if (Mathf.Abs(dy) > 0.5f && Mathf.Abs(h.dyaw) > 0.5f && Mathf.Sign(dy) != Mathf.Sign(h.dyaw)) h.flips++;
				if (Mathf.Abs(dy) > 0.5f) h.dyaw = dy;
				h.yaw = h.t.eulerAngles.y;
			}
			if (Time.unscaledTime < end) return;
			var live = hens.Where(h => h.t).ToList();
			var sum = live.Aggregate(Vector2.zero, (a, h) => a + new Vector2(Mathf.Cos(h.yaw * Mathf.Deg2Rad), Mathf.Sin(h.yaw * Mathf.Deg2Rad)));
			var tilt = live.Select(h => Vector3.Angle(h.t.up, Vector3.up)).ToList();
			Plugin.Log.LogInfo($"probe {Me}: tilt {string.Join(" ", tilt.Select(a => a.ToString("0")))}, owned-ish kinematic {string.Join("", live.Select(h => h.t.GetComponent<Rigidbody>().isKinematic ? "k" : "P"))}");
			Plugin.Log.LogInfo($"probe {Me}: crowd {live.Count} hens over {frames} frames: path {live.Sum(h => h.path):0.0} m, jerk {live.Sum(h => h.jerk):0.00} m, yaw flips {live.Sum(h => h.flips)}, facing alignment {(live.Count > 0 ? sum.magnitude / live.Count : 0):0.00}, yaws {string.Join(" ", live.Select(h => h.yaw.ToString("0")))}");
			Destroy(gameObject);
		}
	}

	static void At(float at, string name, Action run) => steps.Add((at, name, run));

	static string Vac()
	{
		var a = Ctx.PlayerState.Ammo;
		var parts = new List<string>();
		for (var i = 0; i < a.GetUsableSlotCount(); i++)
			if (a.GetSlotCount(i) > 0) parts.Add($"{a.GetSlotName(i)}x{a.GetSlotCount(i)}");
		return parts.Count == 0 ? "[empty]" : "[" + string.Join(" ", parts) + "]";
	}

	static void Fill()
	{
		for (var i = 0; i < 10; i++) Ctx.PlayerState.Ammo.MaybeAddToSlot(Identifiable.Id.BEET_VEGGIE, null);
	}

	static void Kill() => DeathHandler.Kill(Ctx.Player, DeathHandler.Source.KILL_ON_TRIGGER, null, "probe");

	static IEnumerable<GameObject> HenObjs() => UnityEngine.Object.FindObjectsOfType<Identifiable>()
		.Where(i => i.id == Identifiable.Id.HEN && (i.transform.position - Ctx.Player.transform.position).sqrMagnitude < 40f * 40f)
		.Select(i => i.gameObject).OrderBy(g => g.transform.position.x);

	static void Hens()
	{
		foreach (var g in HenObjs())
		{
			var rb = g.GetComponent<Rigidbody>();
			Log($"hen {g.transform.position} yaw {g.transform.eulerAngles.y:0} up {Vector3.Angle(g.transform.up, Vector3.up):0} kin {(rb ? rb.isKinematic : false)} vel {(rb ? rb.velocity.magnitude : 0):0.00}");
		}
	}

	static void HenComponents()
	{
		var g = HenObjs().FirstOrDefault();
		if (!g) { Log("no hens near"); return; }
		var comps = g.GetComponentsInChildren<Component>(true).Select(c =>
			c is Behaviour b ? $"{c.GetType().Name}{(b.enabled ? "" : "(off)")}" : c.GetType().Name).Distinct();
		Log("hen comps: " + string.Join(",", comps));
		foreach (var an in g.GetComponentsInChildren<Animator>(true))
			Log($"hen animator {an.name} rootMotion {an.applyRootMotion} enabled {an.enabled}");
	}

	static void Garden()
	{
		var empties = UnityEngine.Object.FindObjectsOfType<LandPlot>().Where(p => p.typeId == LandPlot.Id.EMPTY)
			.OrderBy(p => p.GetComponentInParent<LandPlotLocation>().id).ToList();
		if (empties.Count == 0) { Log("garden: no empty plot"); return; }
		var plot = empties[empties.Count / 2];
		var loc = plot.GetComponentInParent<LandPlotLocation>();
		var go = loc.Replace(plot, SRSingleton<GameContext>.Instance.LookupDirector.GetPlotPrefab(LandPlot.Id.GARDEN));
		var garden = go.GetComponent<LandPlot>();
		garden.GetComponentInChildren<GardenCatcher>(true).Plant(Identifiable.Id.POGO_FRUIT, false);
		Log($"garden {loc.id}: planted POGO_FRUIT, attached {garden.HasAttached()}");
	}

	static void GardenSeen()
	{
		foreach (var p in UnityEngine.Object.FindObjectsOfType<LandPlot>().Where(p => p.typeId == LandPlot.Id.GARDEN))
			Log($"garden {p.GetComponentInParent<LandPlotLocation>().id}: attached {p.HasAttached()} crop {p.GetAttachedCropId()}");
	}

	static void Walk(float t)
	{
		// Side-on to the client: stand, vacuum, look down, shoot, then circle walking and running with the vac on.
		var e = t - walkStart;
		var vacName = Enum.GetNames(VacModeType).First(n => n.StartsWith("VAC"));
		ForceVac = e < 3f ? null : e < 23f ? vacName : "SHOOT";
		float pitch = e >= 6f && e < 8f ? 30f : 0f, v = e < 11f || e >= 23f ? 0f : e < 17f ? 10f : 18f, r = 6f;
		Vector3 pos, fwd;
		if (v == 0f) { pos = walkCenter; fwd = walkSide; }
		else
		{
			walkAngle += v / r * Time.unscaledDeltaTime;
			var a = walkAngle;
			pos = walkCenter + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
			fwd = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a));
		}
		var tr = Ctx.Player.transform;
		var mask = Physics.DefaultRaycastLayers & ~(1 << tr.gameObject.layer);
		if (Physics.Raycast(pos + Vector3.up * 5f, Vector3.down, out var hit, 20f, mask, QueryTriggerInteraction.Ignore)) pos.y = hit.point.y;
		tr.position = pos;
		tr.rotation = Quaternion.LookRotation(fwd);
		Ctx.Player.GetComponentInChildren<vp_FPCamera>().SetRotation(new Vector2(pitch, tr.eulerAngles.y));
		if (t + Time.unscaledDeltaTime >= walkUntil) ForceVac = null;
	}

	static float walkAngle;

	// Client: the host's largo, from their avatar's feet and barrel.
	static string LargoState()
	{
		var av = UnityEngine.Object.FindObjectOfType<RemoteAvatar>();
		var l = UnityEngine.Object.FindObjectsOfType<Identifiable>().FirstOrDefault(i => i.id == Identifiable.Id.PINK_ROCK_LARGO);
		if (!l || !av) return "largo none";
		var m = av.GetComponentsInChildren<Transform>().FirstOrDefault(x => x.name == "muzzle");
		return $"largo {(l.transform.position - av.transform.position).magnitude:0.00} m from feet, {(m ? (l.transform.position - m.position).magnitude : -1f):0.00} m from muzzle, remote-owned {ActorSync.IsRemoteOwned(l.gameObject)}";
	}

	static void Shot(int n)
	{
		var path = Path.Combine(Application.persistentDataPath, $"probe_{n}.png");
		ScreenCapture.CaptureScreenshot(path);
		Log($"screenshot {path}");
	}

	// One hen, every frame for 2 s: how far it moves and turns per frame, and how often the direction flips.
	class JitterProbe : MonoBehaviour
	{
		GameObject hen;
		Vector3 lastPos, lastStep;
		float lastYaw, end, path, turn;
		int frames, flips;

		void Start()
		{
			hen = HenObjs().FirstOrDefault();
			end = Time.unscaledTime + 2f;
			if (hen) { lastPos = hen.transform.position; lastYaw = hen.transform.eulerAngles.y; }
		}

		void LateUpdate()
		{
			if (!hen) { Destroy(gameObject); return; }
			var p = hen.transform.position;
			var step = p - lastPos;
			if (step.sqrMagnitude > 1e-6f && lastStep.sqrMagnitude > 1e-6f && Vector3.Dot(step, lastStep) < 0) flips++;
			if (step.sqrMagnitude > 1e-6f) lastStep = step;
			path += step.magnitude;
			turn += Mathf.Abs(Mathf.DeltaAngle(lastYaw, hen.transform.eulerAngles.y));
			lastPos = p;
			lastYaw = hen.transform.eulerAngles.y;
			frames++;
			if (Time.unscaledTime < end) return;
			Plugin.Log.LogInfo($"probe {Me}: hen jitter over {frames} frames: path {path:0.00} m, turned {turn:0} deg, direction flips {flips}");
			Destroy(gameObject);
		}
	}
}
