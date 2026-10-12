using TailRanch.Net;
using TailRanch.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TailRanch.UI;

// F8. Dark liquid glass, gold accent. IMGUI has no blur, so depth is faked with layered translucency, a lit rim and a soft shadow.
public class Panel : MonoBehaviour
{
	static readonly Color Gold = RemoteAvatar.Gold;
	static readonly Color Text = new Color(0.93f, 0.91f, 0.86f);
	static readonly Color Dim = new Color(0.62f, 0.58f, 0.5f, 0.85f);
	const float Width = 312, Margin = 20, Fade = 0.15f, Shadow = 28;

	bool open;
	float vis, height = 200;
	GUIStyle box, title, label, dim, button, ghost, field, shadow;
	Texture2D panelTex, lineTex, dotTex;
	string target;

	void Start() => target = Plugin.JoinTarget.Value;

	void Update()
	{
		if (Input.GetKeyDown(KeyCode.F8))
		{
			open = !open;
			if (!open && Session.InWorld) vp_Utility.LockCursor = true;
		}
		var show = open || SceneManager.GetActiveScene().name == "MainMenu";
		vis = Mathf.MoveTowards(vis, show ? 1 : 0, Time.unscaledDeltaTime / Fade);
	}

	void OnGUI()
	{
		var menu = SceneManager.GetActiveScene().name == "MainMenu";
		if (vis <= 0 && !open && !menu) return;
		if (open && Session.InWorld) vp_Utility.LockCursor = false;
		Style();

		var e = vis * vis * (3 - 2 * vis);
		var x = Margin - 14 * (1 - e);
		var y = Margin - 6 * (1 - e);
		var prevColor = GUI.color;
		var prevCursor = GUI.skin.settings.cursorColor;
		var prevSel = GUI.skin.settings.selectionColor;
		GUI.color = new Color(1, 1, 1, e);
		GUI.skin.settings.cursorColor = Gold;
		GUI.skin.settings.selectionColor = new Color(Gold.r, Gold.g, Gold.b, 0.35f);

		if (Event.current.type == EventType.Repaint)
			shadow.Draw(new Rect(x - Shadow, y - Shadow + 8, Width + 2 * Shadow, height + 2 * Shadow), false, false, false, false);

		GUILayout.BeginArea(new Rect(x, y, Width, Screen.height - y));
		GUILayout.BeginVertical(box);
		GUILayout.Label("T A I L R A N C H", title);
		Divider();
		GUILayout.BeginHorizontal();
		var dot = GUILayoutUtility.GetRect(8, 8, GUILayout.Width(8), GUILayout.Height(8));
		dot.y += 5;
		if (Event.current.type == EventType.Repaint)
		{
			var c = GUI.color;
			GUI.color = Session.Active ? new Color(Gold.r, Gold.g, Gold.b, e) : new Color(0.45f, 0.45f, 0.45f, e);
			GUI.DrawTexture(dot, dotTex);
			GUI.color = c;
		}
		GUILayout.Space(6);
		GUILayout.Label(Session.Status, dim);
		GUILayout.EndHorizontal();
		GUILayout.Space(10);

		if (!Session.Active)
		{
			GUILayout.Label("NAME", dim);
			Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, 32, field);
			if (menu)
			{
				GUILayout.Label("HOST  ·  MAGICDNS / 100.X", dim);
				target = GUILayout.TextField(target, 64, field);
				if (GUILayout.Button("JOIN", button) && target.Trim().Length > 0) Session.Join(target.Trim());
			}
			else if (Session.InRemoteWorld)
				GUILayout.Label("disconnected — quit to menu", label);
			else if (GUILayout.Button("HOST", button))
				Session.Host();
		}
		else
		{
			Row(Plugin.PlayerName.Value, "you");
			foreach (var p in Session.Players.Values)
				Row(p.Name, p.Peer != null ? $"{p.Peer.Ping} ms" : "");
			if (Session.IsClient) GUILayout.Label($"rtt {Session.Rtt} ms", dim);
			GUILayout.Space(8);
			if (GUILayout.Button("DISCONNECT", ghost)) Session.Stop("offline");
		}
		GUILayout.EndVertical();
		if (Event.current.type == EventType.Repaint) height = GUILayoutUtility.GetLastRect().height;
		GUILayout.EndArea();

		GUI.color = prevColor;
		GUI.skin.settings.cursorColor = prevCursor;
		GUI.skin.settings.selectionColor = prevSel;
	}

	void Row(string name, string info)
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label(name, label);
		GUILayout.FlexibleSpace();
		GUILayout.Label(info, dim);
		GUILayout.EndHorizontal();
	}

	void Divider()
	{
		var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
		if (Event.current.type == EventType.Repaint) GUI.DrawTexture(r, lineTex);
		GUILayout.Space(8);
	}

	void Style()
	{
		if (panelTex) return; // textures are DontSave; rebuild only if something still destroyed them
		var white = Color.white;
		panelTex = Rounded(64, 14, new Color(0.075f, 0.075f, 0.085f, 0.78f), new Color(1, 1, 1, 0.22f), new Color(1, 1, 1, 0.07f), 16);
		var shadowTex = Soft(96, 28, 14, 0.55f);
		lineTex = Line(128, new Color(Gold.r, Gold.g, Gold.b, 0.7f));
		dotTex = Rounded(16, 8, white, white, white, 0);

		var goldTex = Rounded(48, 10, new Color(Gold.r, Gold.g, Gold.b, 0.95f), new Color(1f, 0.92f, 0.6f, 0.7f), new Color(1, 1, 1, 0.22f), 12);
		var goldHover = Rounded(48, 10, new Color(0.95f, 0.78f, 0.3f, 1f), new Color(1f, 0.95f, 0.7f, 0.85f), new Color(1, 1, 1, 0.28f), 12);
		var goldActive = Rounded(48, 10, new Color(0.62f, 0.47f, 0.1f, 1f), new Color(0.9f, 0.75f, 0.35f, 0.5f), new Color(0, 0, 0, 0.15f), 12);
		var glass = Rounded(48, 10, new Color(1, 1, 1, 0.05f), new Color(Gold.r, Gold.g, Gold.b, 0.55f), new Color(1, 1, 1, 0.06f), 12);
		var glassHover = Rounded(48, 10, new Color(Gold.r, Gold.g, Gold.b, 0.18f), new Color(Gold.r, Gold.g, Gold.b, 0.85f), new Color(1, 1, 1, 0.08f), 12);
		var glassActive = Rounded(48, 10, new Color(Gold.r, Gold.g, Gold.b, 0.32f), new Color(Gold.r, Gold.g, Gold.b, 0.9f), new Color(0, 0, 0, 0.1f), 12);
		// Inset: dark top rim reads as a recessed well.
		var inset = Rounded(48, 9, new Color(0, 0, 0, 0.42f), new Color(1, 1, 1, 0.1f), new Color(0, 0, 0, 0.25f), 8);
		var insetFocus = Rounded(48, 9, new Color(0, 0, 0, 0.5f), new Color(Gold.r, Gold.g, Gold.b, 0.8f), new Color(0, 0, 0, 0.25f), 8);

		shadow = new GUIStyle { normal = { background = shadowTex }, border = new RectOffset(44, 44, 44, 44) };
		box = new GUIStyle { normal = { background = panelTex }, border = new RectOffset(16, 16, 16, 16), padding = new RectOffset(18, 18, 16, 18) };
		title = new GUIStyle { normal = { textColor = Gold }, fontSize = 13, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 8) };
		label = new GUIStyle { normal = { textColor = Text }, fontSize = 13, margin = new RectOffset(0, 0, 3, 3) };
		dim = new GUIStyle { normal = { textColor = Dim }, fontSize = 11, wordWrap = true, margin = new RectOffset(0, 0, 3, 3) };
		button = new GUIStyle
		{
			normal = { background = goldTex, textColor = new Color(0.08f, 0.06f, 0.02f) },
			hover = { background = goldHover, textColor = new Color(0.08f, 0.06f, 0.02f) },
			active = { background = goldActive, textColor = new Color(1f, 0.95f, 0.8f) },
			border = new RectOffset(12, 12, 12, 12),
			alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 12,
			fixedHeight = 34, margin = new RectOffset(0, 0, 8, 0),
		};
		ghost = new GUIStyle(button)
		{
			normal = { background = glass, textColor = Gold },
			hover = { background = glassHover, textColor = new Color(1f, 0.88f, 0.5f) },
			active = { background = glassActive, textColor = Color.white },
		};
		field = new GUIStyle
		{
			normal = { background = inset, textColor = Text },
			hover = { background = inset, textColor = Text },
			focused = { background = insetFocus, textColor = Color.white },
			border = new RectOffset(11, 11, 11, 11),
			padding = new RectOffset(10, 10, 7, 7), fontSize = 13, fixedHeight = 30, margin = new RectOffset(0, 0, 3, 8),
			clipping = TextClipping.Clip,
		};
	}

	static Texture2D New(int w, int h) => new(w, h, TextureFormat.RGBA32, false)
	{
		wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave,
	};

	// Signed distance to a rounded rect inset by `inset` px; negative inside.
	static float Sdf(float px, float py, int size, float inset, float r)
	{
		var h = size * 0.5f - inset;
		var qx = Mathf.Max(Mathf.Abs(px - size * 0.5f) - (h - r), 0);
		var qy = Mathf.Max(Mathf.Abs(py - size * 0.5f) - (h - r), 0);
		return Mathf.Sqrt(qx * qx + qy * qy) - r;
	}

	// Anti-aliased rounded rect: fill, 1px rim brighter toward the top, highlight band in the top cap (stays put under 9-slicing).
	static Texture2D Rounded(int size, float r, Color fill, Color rim, Color top, float topH)
	{
		var t = New(size, size);
		var px = new Color[size * size];
		for (var y = 0; y < size; y++)
		for (var x = 0; x < size; x++)
		{
			float fx = x + 0.5f, fy = y + 0.5f;
			var d = Sdf(fx, fy, size, 0, r);
			var c = fill;
			if (topH > 0)
			{
				var k = Mathf.Clamp01(1 - (size - fy) / topH) * top.a;
				c = new Color(Mathf.Lerp(c.r, top.r, k), Mathf.Lerp(c.g, top.g, k), Mathf.Lerp(c.b, top.b, k), c.a + (1 - c.a) * k);
			}
			var rk = Mathf.Clamp01(1 - Mathf.Abs(d + 0.75f)) * Mathf.Lerp(0.35f, 1f, fy / size);
			c = Color.Lerp(c, rim, rk);
			c.a *= Mathf.Clamp01(0.5f - d);
			px[y * size + x] = c;
		}
		t.SetPixels(px);
		t.Apply(false, true);
		return t;
	}

	static Texture2D Soft(int size, float inset, float r, float alpha)
	{
		var t = New(size, size);
		var px = new Color[size * size];
		for (var y = 0; y < size; y++)
		for (var x = 0; x < size; x++)
		{
			var d = Sdf(x + 0.5f, y + 0.5f, size, inset, r);
			var k = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-6, inset, d));
			px[y * size + x] = new Color(0, 0, 0, alpha * k * k);
		}
		t.SetPixels(px);
		t.Apply(false, true);
		return t;
	}

	static Texture2D Line(int w, Color c)
	{
		var t = New(w, 1);
		for (var x = 0; x < w; x++)
		{
			var k = Mathf.Sin(Mathf.PI * (x + 0.5f) / w);
			t.SetPixel(x, 0, new Color(c.r, c.g, c.b, c.a * k * k));
		}
		t.Apply(false, true);
		return t;
	}
}
