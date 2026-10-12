using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TailRanch.Net;
using TailRanch.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TailRanch.UI;

// Co-op docks at the right edge beside the game's own menus (main menu, pause menu) and is never drawn
// over play; session events surface as glass cards at top-centre. IMGUI has no blur, so depth is faked
// with layered translucency, a lit hairline rim and a soft shadow.
public class Panel : MonoBehaviour
{
	static readonly Color Gold = RemoteAvatar.Gold;
	static readonly Color Text = new(0.94f, 0.93f, 0.9f);
	static readonly Color Dim = new(0.72f, 0.7f, 0.66f, 0.75f);
	static readonly Color Good = new(0.42f, 0.86f, 0.62f);
	static readonly Color Warn = new(0.96f, 0.74f, 0.32f);
	static readonly Color Bad = new(0.96f, 0.43f, 0.4f);
	static readonly Color Idle = new(1, 1, 1, 0.25f);
	const float Width = 264, Margin = 28, Fade = 0.18f, Shadow = 24;
	const float CardW = 300, CardH = 50, CardGap = 8, CardLife = 4.5f, CardIn = 0.25f, CardOut = 0.4f;
	const string RecentKey = "TailRanch.Recent";

	class Card
	{
		public string Tag, Text;
		public Color Accent;
		public float Born, Y = -1;
	}

	readonly List<Card> cards = new();
	readonly Dictionary<byte, string> known = new();
	bool wasActive, wasRemote, hinted;
	string lastStatus = "offline";
	float quietUntil, vis, height = 160, shownH = 160, nextFind;
	MainMenuUI mainMenu;
	string target, address, copyText;
	List<string> recent;
	GUIStyle box, card, shadow, title, label, dim, micro, microRight, ping, placeholder, tag, cardText;
	GUIStyle button, ghost, danger, field, row, kick;
	Texture2D panelTex, white, dotTex;

	void Start()
	{
		target = Plugin.JoinTarget.Value;
		recent = PlayerPrefs.GetString(RecentKey, "").Split('\n').Where(s => s.Length > 0).ToList();
		SceneManager.activeSceneChanged += (_, s) =>
		{
			if (s.name != "worldGenerated" || Session.Active || hinted) return;
			hinted = true;
			Push("co-op", "Host from the pause menu", Gold);
		};
	}

	void Update()
	{
		Track();
		var dt = Time.unscaledDeltaTime;
		vis = Mathf.MoveTowards(vis, Docked() ? 1 : 0, dt / Fade);
		shownH = Mathf.Lerp(shownH, height, 1 - Mathf.Exp(-14f * dt));
		cards.RemoveAll(c => Time.unscaledTime - c.Born > CardLife);
		var y = Margin;
		foreach (var c in cards)
		{
			c.Y = c.Y < 0 ? y - 14 : Mathf.Lerp(c.Y, y, 1 - Mathf.Exp(-12f * dt));
			y += CardH + CardGap;
		}
	}

	// Session state is polled, not hooked: Session.cs stays the only owner of its transitions.
	void Track()
	{
		var active = Session.Active;
		var now = Time.unscaledTime;
		if (active && !wasActive && Session.IsHost)
		{
			LocalAddress();
			Push("hosting", address ?? $"port {Plugin.Port.Value}", Gold);
		}
		if (!active && wasActive)
		{
			if (Session.Status == "offline") Push("co-op", "Session closed", Dim);
			else Push("disconnected", Session.Status, Bad);
		}
		else if (!active && Session.Status != lastStatus && Session.Status != "offline")
			Push("error", Session.Status, Bad);
		if (Session.InRemoteWorld && !wasRemote && active)
		{
			quietUntil = now + 3; // the host and existing players arrive as joins right after Ready
			Push("connected", Plugin.JoinTarget.Value, Good);
			Remember(Plugin.JoinTarget.Value);
		}
		if (active)
		{
			foreach (var p in Session.Players.Values)
			{
				if (known.ContainsKey(p.Id)) continue;
				known[p.Id] = p.Name;
				if (now > quietUntil) Push("joined", p.Name, Good);
			}
			foreach (var id in known.Keys.Where(id => !Session.Players.ContainsKey(id)).ToList())
			{
				Push("left", known[id], Dim);
				known.Remove(id);
			}
		}
		else known.Clear();
		wasActive = active;
		wasRemote = Session.InRemoteWorld;
		lastStatus = Session.Status;
	}

	void Push(string t, string text, Color accent)
	{
		cards.Add(new Card { Tag = t.ToUpperInvariant(), Text = text, Accent = accent, Born = Time.unscaledTime });
		if (cards.Count > 4) cards.RemoveAt(0);
	}

	// Shown only while the game's own menu is up (sub-screens like Options deactivate it).
	bool Docked()
	{
		if (SceneManager.GetActiveScene().name == "MainMenu")
		{
			if (!mainMenu && Time.unscaledTime > nextFind)
			{
				mainMenu = FindObjectOfType<MainMenuUI>();
				nextFind = Time.unscaledTime + 0.5f;
			}
			return mainMenu && mainMenu.gameObject.activeInHierarchy;
		}
		var pm = SRSingleton<PauseMenu>.Instance;
		return pm && pm.pauseUI && pm.pauseUI.activeInHierarchy;
	}

	void Remember(string host)
	{
		if (string.IsNullOrEmpty(host)) return;
		recent.Remove(host);
		recent.Insert(0, host);
		if (recent.Count > 3) recent.RemoveRange(3, recent.Count - 3);
		PlayerPrefs.SetString(RecentKey, string.Join("\n", recent));
		PlayerPrefs.Save();
	}

	// What a friend types to join: this machine's tailnet IP (always works) and hostname (MagicDNS, usually).
	void LocalAddress()
	{
		string ip = null, name = null;
		try
		{
			ip = NetworkInterface.GetAllNetworkInterfaces()
				.SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
				.Where(a => !IPAddress.IsLoopback(a) && Tailnet.Allowed(a))
				.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
				.FirstOrDefault()?.ToString();
		}
		catch (System.Exception e) { Plugin.Log.LogWarning($"interfaces: {e.Message}"); }
		try { name = Dns.GetHostName().ToLowerInvariant(); }
		catch { }
		copyText = ip ?? name;
		address = ip != null && name != null ? $"{name}  ·  {ip}" : copyText;
	}

	void OnGUI()
	{
		if (vis <= 0 && cards.Count == 0) return;
		Style();
		var s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
		var prevMatrix = GUI.matrix;
		var prevColor = GUI.color;
		var prevCursor = GUI.skin.settings.cursorColor;
		var prevSel = GUI.skin.settings.selectionColor;
		GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
		GUI.skin.settings.cursorColor = Gold;
		GUI.skin.settings.selectionColor = new Color(Gold.r, Gold.g, Gold.b, 0.35f);

		if (vis > 0) DrawPanel(Screen.width / s, Screen.height / s);
		DrawCards(Screen.width / s);

		GUI.matrix = prevMatrix;
		GUI.color = prevColor;
		GUI.skin.settings.cursorColor = prevCursor;
		GUI.skin.settings.selectionColor = prevSel;
	}

	void DrawPanel(float sw, float sh)
	{
		var e = vis * vis * (3 - 2 * vis);
		var x = sw - Margin - Width + 18 * (1 - e);
		var y = Mathf.Max(Margin, (sh - shownH) * 0.5f);
		var repaint = Event.current.type == EventType.Repaint;
		GUI.color = new Color(1, 1, 1, e);
		if (repaint) shadow.Draw(new Rect(x - Shadow, y - Shadow + 6, Width + 2 * Shadow, height + 2 * Shadow), false, false, false, false);

		GUILayout.BeginArea(new Rect(x, y, Width, sh - y));
		GUILayout.BeginVertical(box);

		GUILayout.BeginHorizontal();
		var phase = Phase();
		var pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 4);
		Dot(!Session.Active ? Idle : phase >= 0 ? new Color(Warn.r, Warn.g, Warn.b, pulse) : Good, e);
		GUILayout.Label("TailRanch", title);
		GUILayout.FlexibleSpace();
		GUILayout.Label(Session.IsHost ? "HOST" : Session.IsClient ? "CLIENT" : "OFFLINE", micro);
		GUILayout.EndHorizontal();
		GUILayout.Label(Session.Status, dim);
		if (phase >= 0) Progress(phase, e);
		Divider(e);

		if (!Session.Active)
		{
			GUILayout.Label("NAME", micro);
			if (Plugin.DevName.Length > 0) // env override wins; never let it reach the config
			{
				GUI.enabled = false;
				GUILayout.TextField(Plugin.DevName, 32, field);
				GUI.enabled = true;
			}
			else Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, 32, field);
			if (SceneManager.GetActiveScene().name == "MainMenu")
			{
				GUILayout.Label("HOST", micro);
				target = GUILayout.TextField(target, 64, field);
				if (target.Length == 0 && repaint)
				{
					var r = GUILayoutUtility.GetLastRect();
					GUI.Label(new Rect(r.x + 10, r.y, r.width - 20, r.height), "MagicDNS name or 100.x", placeholder);
				}
				if (GUILayout.Button("Join", button) && target.Trim().Length > 0) Session.Join(target.Trim());
				if (recent.Count > 0)
				{
					GUILayout.Space(10);
					GUILayout.Label("RECENT", micro);
					foreach (var h in recent.ToList())
						if (GUILayout.Button(h, row))
						{
							target = h;
							Session.Join(h);
						}
				}
			}
			else if (Session.InRemoteWorld)
				GUILayout.Label("Disconnected. Quit to the main menu.", label);
			else if (GUILayout.Button("Host this world", button))
				Session.Host();
		}
		else
		{
			if (Session.IsHost)
			{
				GUILayout.Label("YOUR ADDRESS", micro);
				if (GUILayout.Button(address ?? "unknown", row) && copyText != null)
				{
					GUIUtility.systemCopyBuffer = copyText;
					Push("copied", copyText, Gold);
				}
				var r = GUILayoutUtility.GetLastRect();
				if (repaint) GUI.Label(new Rect(r.xMax - 48, r.y, 40, r.height), "COPY", microRight);
				GUILayout.Space(10);
			}
			GUILayout.Label("PLAYERS", micro);
			PlayerRow(Plugin.Name, "you", -1, null);
			foreach (var p in Session.Players.Values.ToList()) // kick disconnects synchronously and edits Players
			{
				var ms = Session.IsHost ? p.Peer?.Ping ?? -1 : p.Id == Session.HostId ? Session.Rtt : -1;
				PlayerRow(p.Name, p.Id == Session.HostId ? "host" : "", ms, Session.IsHost ? p : null);
			}
			GUILayout.Space(6);
			var connecting = Session.IsClient && !Session.InRemoteWorld;
			if (GUILayout.Button(connecting ? "Cancel" : "Disconnect", danger)) Session.Stop("offline");
		}

		GUILayout.EndVertical();
		if (repaint) height = GUILayoutUtility.GetLastRect().height;
		GUILayout.EndArea();
		if (repaint) Ticks(new Rect(x, y, Width, height), e);
		GUI.color = Color.white;
	}

	void DrawCards(float sw)
	{
		if (Event.current.type != EventType.Repaint) return;
		var now = Time.unscaledTime;
		foreach (var c in cards)
		{
			if (c.Y < 0) continue;
			var age = now - c.Born;
			var a = Mathf.Clamp01(age / CardIn) * Mathf.Clamp01((CardLife - age) / CardOut);
			a = a * a * (3 - 2 * a);
			var r = new Rect((sw - CardW) * 0.5f, c.Y, CardW, CardH);
			GUI.color = new Color(1, 1, 1, a);
			shadow.Draw(new Rect(r.x - Shadow, r.y - Shadow + 4, r.width + 2 * Shadow, r.height + 2 * Shadow), false, false, false, false);
			card.Draw(r, false, false, false, false);
			GUI.color = new Color(c.Accent.r, c.Accent.g, c.Accent.b, c.Accent.a * a);
			GUI.DrawTexture(new Rect(r.x + 14, r.y + 13, 3, CardH - 26), dotTex);
			GUI.Label(new Rect(r.x + 28, r.y + 9, CardW - 42, 14), c.Tag, tag);
			GUI.color = new Color(1, 1, 1, a);
			GUI.Label(new Rect(r.x + 28, r.y + 23, CardW - 42, 20), c.Text, cardText);
		}
		GUI.color = Color.white;
	}

	// Join progress inferred from Session.Status: link → world transfer → load. -1 when not joining.
	static int Phase()
	{
		if (!Session.IsClient || Session.InRemoteWorld) return -1;
		var s = Session.Status;
		return s.StartsWith("connecting") ? 0 : s.StartsWith("connected, requesting") ? 1 : s.StartsWith("loading world") ? 2 : -1;
	}

	void Progress(int phase, float e)
	{
		GUILayout.Space(6);
		var r = GUILayoutUtility.GetRect(1, 3, GUILayout.ExpandWidth(true), GUILayout.Height(3));
		if (Event.current.type == EventType.Repaint)
		{
			var w = (r.width - 8) / 3;
			for (var i = 0; i < 3; i++)
			{
				var c = i <= phase ? Gold : Color.white;
				var a = i < phase ? 0.9f : i == phase ? 0.4f + 0.35f * Mathf.Sin(Time.unscaledTime * 5) : 0.1f;
				GUI.color = new Color(c.r, c.g, c.b, a * e);
				GUI.DrawTexture(new Rect(r.x + i * (w + 4), r.y, w, 3), white);
			}
			GUI.color = new Color(1, 1, 1, e);
		}
		GUILayout.BeginHorizontal();
		GUILayout.Label("LINK", micro);
		GUILayout.FlexibleSpace();
		GUILayout.Label("WORLD", micro);
		GUILayout.FlexibleSpace();
		GUILayout.Label("LOAD", micro);
		GUILayout.EndHorizontal();
	}

	void PlayerRow(string name, string note, int ms, Player kickable)
	{
		GUILayout.BeginHorizontal(GUILayout.Height(24));
		GUILayout.Label(name, label);
		GUILayout.FlexibleSpace();
		if (ms >= 0)
		{
			ping.normal.textColor = ms < 80 ? Good : ms < 160 ? Warn : Bad;
			GUILayout.Label($"{ms} ms", ping);
		}
		if (note.Length > 0) GUILayout.Label(note, micro);
		if (kickable != null && GUILayout.Button("×", kick, GUILayout.Width(22))) kickable.Peer?.Disconnect();
		GUILayout.EndHorizontal();
	}

	void Dot(Color c, float e)
	{
		var r = GUILayoutUtility.GetRect(7, 7, GUILayout.Width(7), GUILayout.Height(7));
		r.y += 5;
		if (Event.current.type == EventType.Repaint)
		{
			GUI.color = new Color(c.r, c.g, c.b, c.a * 0.25f * e);
			GUI.DrawTexture(new Rect(r.x - 3, r.y - 3, 13, 13), dotTex);
			GUI.color = new Color(c.r, c.g, c.b, c.a * e);
			GUI.DrawTexture(r, dotTex);
			GUI.color = new Color(1, 1, 1, e);
		}
		GUILayout.Space(8);
	}

	void Divider(float e)
	{
		GUILayout.Space(8);
		var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
		if (Event.current.type == EventType.Repaint)
		{
			GUI.color = new Color(1, 1, 1, 0.08f * e);
			GUI.DrawTexture(r, white);
			GUI.color = new Color(1, 1, 1, e);
		}
		GUILayout.Space(10);
	}

	// HUD corner brackets just outside the glass: the one deliberately un-game-like touch.
	void Ticks(Rect r, float e)
	{
		const float L = 10, O = 6;
		float l = r.x - O, t = r.y - O, rt = r.xMax + O, b = r.yMax + O;
		GUI.color = new Color(Gold.r, Gold.g, Gold.b, 0.55f * e);
		GUI.DrawTexture(new Rect(l, t, L, 1), white);
		GUI.DrawTexture(new Rect(l, t, 1, L), white);
		GUI.DrawTexture(new Rect(rt - L, t, L, 1), white);
		GUI.DrawTexture(new Rect(rt - 1, t, 1, L), white);
		GUI.DrawTexture(new Rect(l, b - 1, L, 1), white);
		GUI.DrawTexture(new Rect(l, b - L, 1, L), white);
		GUI.DrawTexture(new Rect(rt - L, b - 1, L, 1), white);
		GUI.DrawTexture(new Rect(rt - 1, b - L, 1, L), white);
		GUI.color = new Color(1, 1, 1, e);
	}

	void Style()
	{
		if (panelTex) return; // textures are DontSave; rebuild only if something still destroyed them
		var w = Color.white;
		var clear = new Color(0, 0, 0, 0);
		var gold = new System.Func<float, Color>(a => new Color(Gold.r, Gold.g, Gold.b, a));
		panelTex = Rounded(64, 16, new Color(0.055f, 0.06f, 0.07f, 0.6f), new Color(1, 1, 1, 0.16f), new Color(1, 1, 1, 0.05f), 18);
		var cardTex = Rounded(48, 12, new Color(0.055f, 0.06f, 0.07f, 0.72f), new Color(1, 1, 1, 0.14f), new Color(1, 1, 1, 0.05f), 12);
		var shadowTex = Soft(96, 28, 14, 0.35f);
		white = New(1, 1);
		white.SetPixel(0, 0, w);
		white.Apply(false, true);
		dotTex = Rounded(16, 8, w, w, w, 0);

		var primary = Rounded(48, 10, gold(0.12f), gold(0.65f), new Color(1, 1, 1, 0.05f), 12);
		var primaryHover = Rounded(48, 10, gold(0.9f), new Color(1f, 0.92f, 0.6f, 0.8f), new Color(1, 1, 1, 0.2f), 12);
		var primaryActive = Rounded(48, 10, new Color(0.62f, 0.47f, 0.1f, 1f), new Color(0.9f, 0.75f, 0.35f, 0.5f), new Color(0, 0, 0, 0.15f), 12);
		var glass = Rounded(48, 10, new Color(1, 1, 1, 0.04f), new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.04f), 12);
		var dangerHover = Rounded(48, 10, new Color(Bad.r, Bad.g, Bad.b, 0.14f), new Color(Bad.r, Bad.g, Bad.b, 0.7f), new Color(1, 1, 1, 0.04f), 12);
		var dangerActive = Rounded(48, 10, new Color(Bad.r, Bad.g, Bad.b, 0.3f), new Color(Bad.r, Bad.g, Bad.b, 0.85f), clear, 0);
		var rowHover = Rounded(48, 8, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.1f), clear, 0);
		// Inset: dark top rim reads as a recessed well.
		var inset = Rounded(48, 9, new Color(0, 0, 0, 0.35f), new Color(1, 1, 1, 0.08f), new Color(0, 0, 0, 0.2f), 8);
		var insetFocus = Rounded(48, 9, new Color(0, 0, 0, 0.45f), gold(0.7f), new Color(0, 0, 0, 0.2f), 8);

		shadow = new GUIStyle { normal = { background = shadowTex }, border = new RectOffset(44, 44, 44, 44) };
		box = new GUIStyle { normal = { background = panelTex }, border = new RectOffset(16, 16, 16, 16), padding = new RectOffset(18, 18, 14, 16) };
		card = new GUIStyle { normal = { background = cardTex }, border = new RectOffset(12, 12, 12, 12) };
		title = new GUIStyle { normal = { textColor = Text }, fontSize = 13, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 2) };
		label = new GUIStyle { normal = { textColor = Text }, fontSize = 12, alignment = TextAnchor.MiddleLeft, fixedHeight = 22, clipping = TextClipping.Clip };
		dim = new GUIStyle { normal = { textColor = Dim }, fontSize = 11, wordWrap = true, margin = new RectOffset(0, 0, 2, 2) };
		micro = new GUIStyle { normal = { textColor = Dim }, fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, margin = new RectOffset(0, 0, 4, 3) };
		microRight = new GUIStyle(micro) { normal = { textColor = gold(0.8f) }, alignment = TextAnchor.MiddleRight, margin = new RectOffset() };
		ping = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, fixedHeight = 22, margin = new RectOffset(0, 6, 0, 0) };
		placeholder = new GUIStyle { normal = { textColor = new Color(Dim.r, Dim.g, Dim.b, 0.45f) }, fontSize = 12, alignment = TextAnchor.MiddleLeft };
		tag = new GUIStyle { normal = { textColor = w }, fontSize = 9, fontStyle = FontStyle.Bold };
		cardText = new GUIStyle { normal = { textColor = Text }, fontSize = 12, clipping = TextClipping.Clip };
		button = new GUIStyle
		{
			normal = { background = primary, textColor = Gold },
			hover = { background = primaryHover, textColor = new Color(0.08f, 0.06f, 0.02f) },
			active = { background = primaryActive, textColor = new Color(1f, 0.95f, 0.8f) },
			border = new RectOffset(12, 12, 12, 12),
			alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 12,
			fixedHeight = 32, margin = new RectOffset(0, 0, 8, 0),
		};
		ghost = new GUIStyle(button)
		{
			normal = { background = glass, textColor = Text },
			hover = { background = Rounded(48, 10, new Color(1, 1, 1, 0.08f), new Color(1, 1, 1, 0.28f), new Color(1, 1, 1, 0.06f), 12), textColor = Color.white },
			active = { background = glass, textColor = Gold },
		};
		danger = new GUIStyle(ghost)
		{
			hover = { background = dangerHover, textColor = Bad },
			active = { background = dangerActive, textColor = Color.white },
		};
		field = new GUIStyle
		{
			normal = { background = inset, textColor = Text },
			hover = { background = inset, textColor = Text },
			focused = { background = insetFocus, textColor = Color.white },
			border = new RectOffset(11, 11, 11, 11),
			padding = new RectOffset(10, 10, 7, 7), fontSize = 12, fixedHeight = 30, margin = new RectOffset(0, 0, 2, 8),
			clipping = TextClipping.Clip,
		};
		row = new GUIStyle
		{
			normal = { textColor = Text },
			hover = { background = rowHover, textColor = Color.white },
			active = { background = rowHover, textColor = Gold },
			border = new RectOffset(9, 9, 9, 9),
			padding = new RectOffset(10, 10, 0, 0), fontSize = 12, fixedHeight = 28, alignment = TextAnchor.MiddleLeft,
			clipping = TextClipping.Clip, margin = new RectOffset(0, 0, 1, 1),
		};
		kick = new GUIStyle(row)
		{
			normal = { textColor = Dim },
			hover = { background = rowHover, textColor = Bad },
			padding = new RectOffset(), fontSize = 14, fixedHeight = 22, alignment = TextAnchor.MiddleCenter,
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
}
