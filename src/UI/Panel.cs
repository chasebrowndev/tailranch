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

// Co-op docks at the left edge beside the game's own menus (main menu, pause menu), in the gap between the clock
// and the vitals HUD, and is never drawn over play (the right side belongs to tutorial popups); session events
// surface as capsule toasts at top-centre. IMGUI has no blur, so depth is faked: warm smoked glass dense enough
// for text over a bright sky, a lit rim, a soft shadow, generated rounded textures.
//
// Composition (EclipseOS docs/COMPOSITION.md), one hero per state, one gold per state:
//   live    header text → link meter (big round-trip + 24-sample history; GOLD = current reading and its bar)
//           → address capsule (host) → roster well (hairline rows) → bare footer link
//   joining header text → stepper (current step named large; GOLD = current step) → bare footer link
//   menu    header text → one well (name row, host row + inline Join; GOLD = Join) → recent-host chips
//   world   header text → one well (name row) → "Host this world" capsule (GOLD)
//   card    capsule toast: glyph square (gold only for hosting/connected), text, tertiary tag
public class Panel : MonoBehaviour
{
	// ── Tokens — mirror EclipseOS shell/crates/ec-ui/src/tokens.rs. No literal colour or size below this block. ──
	internal static class C
	{
		public static readonly Color Accent = Hex(0xf2c33c);
		public static readonly Color AccentText = Hex(0xf5cf5c);
		public static readonly Color AccentFill = A(Accent, 0.10f);
		public static readonly Color AccentFillHover = A(Accent, 0.18f);
		public static readonly Color AccentBorder = A(Accent, 0.30f);
		public static readonly Color AccentBorderHover = A(Accent, 0.55f);
		public static readonly Color Text = W(1f);
		public static readonly Color Text2 = W(0.64f);
		public static readonly Color Text3 = W(0.40f);
		public static readonly Color Danger = Hex(0xe0553f);
		public static readonly Color Ok = Hex(0x7fae5e);
		public static readonly Color Warn = Hex(0xd99a3e); // local: ping middle band; tokens.rs has no warn
		// GLASS_DEEP: one warm translucent ground; dense because the backdrop is a bright sky, not a known base.
		public static readonly Color Ground = new(0.050f, 0.042f, 0.030f, 0.74f);
		// A card floats over play itself (bright sky, white menu buttons), so it is denser than the docked panel.
		public static readonly Color CardGround = new(0.050f, 0.042f, 0.030f, 0.88f);
		public static readonly Color GroundTop = W(0.05f);
		public static readonly Color Rim = W(0.16f);
		public static readonly Color Well = new(0, 0, 0, 0.26f);
		public static readonly Color WellRim = W(0.07f);
		public static readonly Color WellTop = new(0, 0, 0, 0.18f);
		public static readonly Color Hairline = W(0.06f);
		public static readonly Color Lift = W(0.06f);
		public static readonly Color LiftRim = W(0.12f);
		public static readonly Color LiftHover = W(0.10f);
		public static readonly Color LiftHoverRim = W(0.22f);
		public static readonly Color Highlight = W(0.07f);
		public static readonly Color FieldFocus = W(0.05f);
		public static readonly Color History = W(0.16f);
		public static readonly Color Track = W(0.10f);
		public static readonly Color Selection = A(Accent, 0.35f);
		public const float ShadowAlpha = 0.42f, FeedShadowAlpha = 0.24f; // feed: a stack of small capsules, so a lighter drop each
		public const float Clear = 0f;
	}

	internal static class R // radius
	{
		public const float Panel = 16, Well = 10, Pill = 15, Inline = 13, Card = 20, Chip = 11, Focus = 7; // Inline: half of S.FieldH, so its 9-slice borders never overlap
	}

	internal static class S // space, px at the 800-line reference height
	{
		public const float Width = 268, ChipW = 92, DockedCards = 2, Margin = 28, MenuTop = 0.36f; // MenuTop: fraction of the screen under the title logo
		public const int PadX = 18, PadTop = 16, PadBottom = 12;
		public const float Block = 14, RowH = 28, FieldH = 26, PillH = 30, ChipH = 22, Hair = 1, Label = 46;
		public const float ChipGap = 6, ChipPadX = 10, InlineGap = 6, DotSize = 6, PingDot = 5;
		public const float MeterH = 52, Bars = 24, BarW = 3, BarGap = 2, BarMaxH = 26, BarMinH = 2, BarFloorMs = 8, BarHeadroom = 1.25f;
		public const float StepH = 52, StepBar = 3, StepGap = 4;
		public const float CardH = 40, CardGap = 8, CardMinW = 220, CardMaxW = 460, CardPadX = 16, CardGlyph = 6, CardGlyphGap = 12, CardTagGap = 18;
		public const float Shadow = 26, ShadowDrop = 8, CardShadow = 20, CardDrop = 5, Slide = 14, DockSlide = 18;
		public const int Tex = 64, TexSmall = 32, ShadowTex = 96;
		// Chat (Chat.cs): bottom-left column above the vitals; ChatBottom is a fraction of the screen height.
		public const float ChatW = 300, ChatBottom = 0.79f, ChatLineH = 26, ChatLinePadX = 12, ChatLineGap = 6, ChatNameGap = 8;
		public const float ChatHistMax = 196, ChatWellPad = 10, ChatFootH = 16, ChatFootGap = 6, ChatShadow = 14, ChatDrop = 3;
		public const int ChatFeed = 5, ChatHistory = 60;
	}

	internal static class Z // type size
	{
		public const int Title = 14, Body = 12, Small = 11, Micro = 9, Big = 28, Unit = 12, Step = 15, Kick = 14;
	}

	internal static class M // motion, seconds
	{
		public const float Dock = 0.22f, Ease = 14f, CardEase = 12f, CardLife = 4.5f, CardIn = 0.28f, CardOut = 0.45f;
		public const float Sample = 0.5f, Pulse = 4f;
		public const float ChatLife = 9f, ChatIn = 0.25f, ChatOut = 1.2f, ChatOpen = 0.16f, ChatDenied = 1.5f;
	}

	const string RecentKey = "TailRanch.Recent";
	const int MaxCards = 4, PingGood = 80, PingOk = 160;

	internal static Color Hex(int h) => new(((h >> 16) & 255) / 255f, ((h >> 8) & 255) / 255f, (h & 255) / 255f);
	internal static Color W(float a) => new(1, 1, 1, a);
	internal static Color A(Color c, float a) => new(c.r, c.g, c.b, a);

	enum Tone { Plain, Live, Alarm }

	class Card
	{
		public string Tag, Text;
		public Tone Tone;
		public float Born, Y = -1;
	}

	// Docked panel visibility (0..1) and its bottom edge (reference px), so the chat feed can sit beneath it.
	internal static float DockVis, DockBottom;

	readonly List<Card> cards = new();
	readonly Dictionary<byte, string> known = new();
	readonly int[] hist = new int[(int)S.Bars];
	int histHead, histCount;
	bool wasActive, wasRemote, hinted;
	string lastStatus = "offline";
	float quietUntil, vis, height = 160, shownH = 160, nextFind, nextSample, upSince;
	MainMenuUI mainMenu;
	string target, address, copyText;
	List<string> recent;
	GUIStyle box, well, cardBg, shadow, cardShadow, title, sub, chipTop, chipBottom, big, unit, caption, stepName, micro, label, cardText, cardTag;
	GUIStyle primary, inline, capsule, copy, quiet, field, chip, kick, placeholder, ping, tag;
	Texture2D white, dotTex;

	void Start()
	{
		target = Plugin.JoinTarget.Value;
		recent = PlayerPrefs.GetString(RecentKey, "").Split('\n').Where(s => s.Length > 0).ToList();
		SceneManager.activeSceneChanged += (_, s) =>
		{
			if (s.name != "worldGenerated" || Session.Active || hinted) return;
			hinted = true;
			Push("co-op", "Host from the pause menu", Tone.Plain);
		};
		if (Plugin.DevCards)
		{
			Push("hosting", "framework  ·  100.101.7.12", Tone.Live);
			Push("connected", "framework.tail0c3e.ts.net", Tone.Live);
			Push("joined", "mochi", Tone.Plain);
			Push("left", "mochi", Tone.Plain);
			Push("copied", "100.101.7.12", Tone.Plain);
			Push("co-op", "Host from the pause menu", Tone.Plain);
			Push("co-op", "Session closed", Tone.Plain);
			Push("disconnected", "timed out", Tone.Alarm);
			Push("error", "host unreachable: framework", Tone.Alarm);
		}
	}

	void Update()
	{
		Track();
		Sample();
		var dt = Time.unscaledDeltaTime;
		vis = Mathf.MoveTowards(vis, Docked() && !Chat.Typing ? 1 : 0, dt / M.Dock); // the left column is chat's while typing
		DockVis = vis;
		shownH = Mathf.Lerp(shownH, height, 1 - Mathf.Exp(-M.Ease * dt));
		if (!Plugin.DevCards) cards.RemoveAll(c => Time.unscaledTime - c.Born > M.CardLife);
		var y = S.Margin;
		for (var i = cards.Count - 1; i >= 0; i--) // newest on top, older ones slide down
		{
			var c = cards[i];
			c.Y = c.Y < 0 ? y - S.Slide : Mathf.Lerp(c.Y, y, 1 - Mathf.Exp(-M.CardEase * dt));
			y += S.CardH + S.CardGap;
		}
	}

	// Session state is polled, not hooked: Session.cs stays the only owner of its transitions.
	void Track()
	{
		var active = Session.Active;
		var now = Time.unscaledTime;
		if (active && !wasActive)
		{
			upSince = now;
			histHead = histCount = 0;
			if (Session.IsHost)
			{
				LocalAddress();
				Push("hosting", address ?? $"port {Plugin.Port.Value}", Tone.Live);
			}
		}
		if (!active && wasActive)
		{
			if (Session.Status == "offline") Push("co-op", "Session closed", Tone.Plain);
			else Push("disconnected", Session.Status, Tone.Alarm);
		}
		else if (!active && Session.Status != lastStatus && Session.Status != "offline")
			Push("error", Session.Status, Tone.Alarm);
		if (Session.InRemoteWorld && !wasRemote && active)
		{
			quietUntil = now + 3; // the host and existing players arrive as joins right after Ready
			Push("connected", Plugin.JoinTarget.Value, Tone.Live);
			Remember(Plugin.JoinTarget.Value);
		}
		if (active)
		{
			foreach (var p in Session.Players.Values)
			{
				if (known.ContainsKey(p.Id)) continue;
				known[p.Id] = p.Name;
				if (now > quietUntil) Push("joined", p.Name, Tone.Plain);
			}
			foreach (var id in known.Keys.Where(id => !Session.Players.ContainsKey(id)).ToList())
			{
				Push("left", known[id], Tone.Plain);
				known.Remove(id);
			}
		}
		else known.Clear();
		wasActive = active;
		wasRemote = Session.InRemoteWorld;
		lastStatus = Session.Status;
	}

	// Round trip the meter shows: the host averages its peers, a client reads its link to the host. -1 = nothing to measure.
	static int LinkMs()
	{
		if (Session.IsHost)
		{
			var pings = Session.Players.Values.Where(p => p.Peer != null).Select(p => p.Peer.Ping).ToList();
			return pings.Count > 0 ? (int)pings.Average() : -1;
		}
		return Session.IsClient && Session.InRemoteWorld ? Session.Rtt : -1;
	}

	void Sample()
	{
		if (!Session.Active || Time.unscaledTime < nextSample) return;
		nextSample = Time.unscaledTime + M.Sample;
		hist[histHead] = Plugin.DevPingDemo ? (int)(20 + 50 * Mathf.PerlinNoise(Time.unscaledTime * 0.4f, 0)) : LinkMs();
		histHead = (histHead + 1) % hist.Length;
		histCount = Mathf.Min(histCount + 1, hist.Length);
	}

	int Latest => histCount == 0 ? -1 : hist[(histHead - 1 + hist.Length) % hist.Length];

	void Push(string t, string text, Tone tone)
	{
		cards.Add(new Card { Tag = t.ToUpperInvariant(), Text = text, Tone = tone, Born = Time.unscaledTime });
		if (!Plugin.DevCards && cards.Count > MaxCards) cards.RemoveAt(0);
	}

	// Shown only while the game's own menu is up (sub-screens like Options deactivate it).
	bool Docked()
	{
		if (SceneManager.GetActiveScene().name == "MainMenu")
		{
			if (!mainMenu && Time.unscaledTime > nextFind)
			{
				mainMenu = FindObjectOfType<MainMenuUI>();
				// The message-of-the-day (a Slime Rancher 2 ad) occupies the slot the panel docks into.
				if (mainMenu) mainMenu.transform.Find("MessageOfTheDay")?.gameObject.SetActive(false);
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
		var s = Mathf.Clamp(Screen.height / 800f, 0.9f, 2.5f);
		var prevMatrix = GUI.matrix;
		var prevColor = GUI.color;
		var prevCursor = GUI.skin.settings.cursorColor;
		var prevSel = GUI.skin.settings.selectionColor;
		GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
		GUI.skin.settings.cursorColor = C.Accent;
		GUI.skin.settings.selectionColor = C.Selection;

		if (vis > 0) DrawPanel(Screen.width / s, Screen.height / s);
		DrawCards(Screen.width / s);

		GUI.matrix = prevMatrix;
		GUI.color = prevColor;
		GUI.skin.settings.cursorColor = prevCursor;
		GUI.skin.settings.selectionColor = prevSel;
	}

	static bool Repaint => Event.current.type == EventType.Repaint;
	static bool OnMenu => SceneManager.GetActiveScene().name == "MainMenu";

	void DrawPanel(float sw, float sh)
	{
		var e = vis * vis * (3 - 2 * vis);
		var x = S.Margin - S.DockSlide * (1 - e);
		var y = Mathf.Max(S.Margin, (sh - shownH) * 0.5f);
		if (OnMenu) y = Mathf.Max(y, sh * S.MenuTop); // below the title logo
		DockBottom = y + shownH;
		GUI.color = W(e);
		if (Repaint) shadow.Draw(new Rect(x - S.Shadow, y - S.Shadow + S.ShadowDrop, S.Width + 2 * S.Shadow, height + 2 * S.Shadow), false, false, false, false);

		GUILayout.BeginArea(new Rect(x, y, S.Width, sh - y));
		GUILayout.BeginVertical(box);
		var phase = Phase();
		Header(phase, e);
		GUILayout.Space(S.Block);
		if (!Session.Active) Offline();
		else if (phase >= 0) Stepper(phase, e);
		else Live(e);
		GUILayout.EndVertical();
		if (Repaint) height = GUILayoutUtility.GetLastRect().height;
		GUILayout.EndArea();
		GUI.color = Color.white;
	}

	// Header: title + one-line subtitle at the left, a two-line status chip at the right (state / measurement).
	void Header(int phase, float e)
	{
		var errored = !Session.Active && Session.Status != "offline";
		GUILayout.BeginHorizontal();
		GUILayout.BeginVertical(GUILayout.Width(S.Width - 2 * S.PadX - S.ChipW));
		GUILayout.BeginHorizontal();
		var pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * M.Pulse);
		Dot(errored ? C.Danger : !Session.Active ? C.Text3 : phase >= 0 ? A(C.Text, 0.3f + 0.6f * pulse) : C.Text2, e);
		GUILayout.Label("TailRanch", title);
		GUILayout.EndHorizontal();
		sub.normal.textColor = errored ? C.Danger : C.Text2;
		GUILayout.Label(errored || Session.Active ? Subtitle(Session.Status) : "co-op over your tailnet", sub);
		GUILayout.EndVertical();
		GUILayout.FlexibleSpace();
		GUILayout.BeginVertical(GUILayout.Width(S.ChipW));
		GUILayout.Label(Session.IsHost ? "HOST" : Session.IsClient ? "CLIENT" : "OFFLINE", chipTop);
		GUILayout.Label(Measure(phase), chipBottom);
		GUILayout.EndVertical();
		GUILayout.EndHorizontal();
	}

	string Measure(int phase)
	{
		if (!Session.Active) return $"udp {Plugin.Port.Value}";
		if (phase >= 0) return $"step {phase + 1} of 3";
		var n = Session.Players.Count + 1;
		var up = (int)(Time.unscaledTime - upSince);
		return $"{n} {(n == 1 ? "player" : "players")} · {up / 60:00}:{up % 60:00}";
	}

	void Live(float e)
	{
		Meter(e);
		GUILayout.Space(S.Block);
		if (Session.IsHost)
		{
			if (GUILayout.Button(address ?? "address unknown", capsule) && copyText != null)
			{
				GUIUtility.systemCopyBuffer = copyText;
				Push("copied", copyText, Tone.Plain);
			}
			var r = GUILayoutUtility.GetLastRect();
			if (Repaint) GUI.Label(new Rect(r.x, r.y, r.width - S.ChipPadX - S.InlineGap, r.height), "COPY", copy);
			GUILayout.Space(S.Block);
		}
		GUILayout.BeginVertical(well);
		Row(Plugin.Name, "you", -1, null);
		foreach (var p in Session.Players.Values.ToList()) // kick disconnects synchronously and edits Players
		{
			Hairline();
			var ms = Session.IsHost ? p.Peer?.Ping ?? -1 : p.Id == Session.HostId ? Session.Rtt : -1;
			Row(p.Name, p.Id == Session.HostId ? "host" : "", ms, Session.IsHost ? p : null);
		}
		GUILayout.EndVertical();
		Footer("Disconnect");
	}

	// The hero: one big round-trip reading (the pane's only gold) beside its last 12 s as a bar history.
	void Meter(float e)
	{
		var r = GUILayoutUtility.GetRect(1, S.MeterH, GUILayout.ExpandWidth(true), GUILayout.Height(S.MeterH));
		if (!Repaint) return;
		var v = Latest;
		var live = v >= 0;
		var num = live ? v.ToString() : "—";
		big.normal.textColor = live ? C.AccentText : C.Text3;
		var nw = big.CalcSize(new GUIContent(num));
		GUI.Label(new Rect(r.x, r.y, nw.x, nw.y), num, big);
		if (live) GUI.Label(new Rect(r.x + nw.x + 3, r.y, 40, nw.y - 5), "ms", unit);
		var cap = !live ? Session.IsHost ? "waiting for players" : "linking" : Session.IsHost ? "avg round trip" : "round trip to host";
		GUI.Label(new Rect(r.x, r.y + nw.y + 1, r.width * 0.5f, Z.Small + 4), cap, caption);

		var bw = S.Bars * (S.BarW + S.BarGap) - S.BarGap;
		var x0 = r.xMax - bw;
		var floor = r.y + nw.y - 5; // bars stand on the number's baseline
		var max = Mathf.Max(S.BarFloorMs, hist.Max() * S.BarHeadroom); // relative: a calm 1 ms link still shows its shape
		for (var i = 0; i < hist.Length; i++)
		{
			var k = (histHead + i) % hist.Length; // oldest → newest, left → right
			var age = hist.Length - 1 - i;
			var has = age < histCount && hist[k] >= 0;
			var h = has ? Mathf.Max(S.BarMinH, S.BarMaxH * hist[k] / max) : S.BarMinH;
			GUI.color = W(e) * (has ? age == 0 ? C.Accent : C.History : C.Track);
			GUI.DrawTexture(new Rect(x0 + i * (S.BarW + S.BarGap), floor - h, S.BarW, h), white);
		}
		GUI.color = W(e);
	}

	// "connecting to 1.2.3.4 (1.2.3.4)" → "connecting to 1.2.3.4"; a MagicDNS name keeps its resolved IP.
	static string Subtitle(string s)
	{
		var open = s.LastIndexOf(" (");
		if (open < 0 || !s.EndsWith(")")) return s;
		var inner = s.Substring(open + 2, s.Length - open - 3);
		return s.Substring(0, open).EndsWith(" " + inner) ? s.Substring(0, open) : s;
	}

	// Join progress inferred from Session.Status: link → world transfer → load. -1 when not joining.
	static int Phase()
	{
		if (!Session.IsClient || Session.InRemoteWorld) return -1;
		var s = Session.Status;
		return s.StartsWith("connecting") ? 0 : s.StartsWith("connected, requesting") ? 1 : s.StartsWith("loading world") ? 2 : -1;
	}

	static readonly string[] StepNames = { "Linking", "Fetching world", "Loading" };
	static readonly string[] StepTags = { "LINK", "WORLD", "LOAD" };

	// The joining hero: the current step named large in gold over a three-part track.
	void Stepper(int phase, float e)
	{
		var r = GUILayoutUtility.GetRect(1, S.StepH, GUILayout.ExpandWidth(true), GUILayout.Height(S.StepH));
		if (Repaint)
		{
			GUI.Label(new Rect(r.x, r.y, r.width, Z.Step + 6), StepNames[phase] + "…", stepName);
			var w = (r.width - 2 * S.StepGap) / 3;
			var by = r.y + Z.Step + 14;
			var pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * M.Pulse);
			for (var i = 0; i < 3; i++)
			{
				var bx = r.x + i * (w + S.StepGap);
				GUI.color = W(e) * (i < phase ? C.Text2 : i == phase ? A(C.Accent, 0.55f + 0.45f * pulse) : C.Track);
				GUI.DrawTexture(new Rect(bx, by, w, S.StepBar), white);
				GUI.color = W(e);
				micro.normal.textColor = i == phase ? C.Text : C.Text3;
				GUI.Label(new Rect(bx, by + 8, w, Z.Micro + 4), StepTags[i], micro);
			}
			GUI.color = W(e);
		}
		Footer("Cancel");
	}

	void Footer(string what)
	{
		GUILayout.Space(S.Block * 0.5f);
		if (GUILayout.Button(what, quiet)) Session.Stop("offline");
	}

	void Offline()
	{
		GUILayout.BeginVertical(well);
		GUILayout.BeginHorizontal();
		GUILayout.Label("Name", label, GUILayout.Width(S.Label));
		if (Plugin.DevName.Length > 0) // env override wins; never let it reach the config
		{
			GUI.enabled = false;
			GUILayout.TextField(Plugin.DevName, 32, field);
			GUI.enabled = true;
		}
		else Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, 32, field);
		GUILayout.EndHorizontal();
		if (OnMenu)
		{
			Hairline();
			GUILayout.BeginHorizontal();
			GUILayout.Label("Host", label, GUILayout.Width(S.Label));
			target = GUILayout.TextField(target, 64, field);
			if (target.Length == 0 && Repaint)
			{
				var r = GUILayoutUtility.GetLastRect();
				GUI.Label(r, "MagicDNS name or 100.x", placeholder);
			}
			GUILayout.Space(S.InlineGap);
			if (GUILayout.Button("Join", inline, GUILayout.Width(inline.CalcSize(new GUIContent("Join")).x)) && target.Trim().Length > 0)
				Session.Join(target.Trim());
			GUILayout.EndHorizontal();
		}
		GUILayout.EndVertical();

		if (OnMenu)
		{
			if (recent.Count > 0) Recent();
		}
		else if (Session.InRemoteWorld)
		{
			GUILayout.Space(S.Block);
			GUILayout.Label("Disconnected. Quit to the main menu.", sub);
		}
		else
		{
			GUILayout.Space(S.Block);
			if (GUILayout.Button("Host this world", primary)) Session.Host();
		}
	}

	// Recent hosts as a wrapping row of chips: one tap rejoins.
	void Recent()
	{
		GUILayout.Space(S.Block);
		micro.normal.textColor = C.Text3;
		GUILayout.Label("RECENT", micro);
		GUILayout.Space(S.ChipGap);
		var avail = S.Width - 2 * S.PadX;
		var used = 0f;
		GUILayout.BeginHorizontal();
		foreach (var h in recent.ToList())
		{
			var w = Mathf.Min(avail, chip.CalcSize(new GUIContent(h)).x);
			if (used > 0 && used + w > avail)
			{
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
				GUILayout.Space(S.ChipGap);
				GUILayout.BeginHorizontal();
				used = 0;
			}
			if (GUILayout.Button(h, chip, GUILayout.Width(w)))
			{
				target = h;
				Session.Join(h);
			}
			GUILayout.Space(S.ChipGap);
			used += w + S.ChipGap;
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
	}

	void Row(string name, string tagText, int ms, Player kickable)
	{
		GUILayout.BeginHorizontal(GUILayout.Height(S.RowH));
		GUILayout.Label(name, label);
		GUILayout.FlexibleSpace();
		if (ms >= 0)
		{
			var r = GUILayoutUtility.GetRect(S.PingDot, S.RowH, GUILayout.Width(S.PingDot));
			if (Repaint)
			{
				var keep = GUI.color;
				GUI.color = keep * (ms < PingGood ? C.Ok : ms < PingOk ? C.Warn : C.Danger);
				GUI.DrawTexture(new Rect(r.x, r.center.y - S.PingDot * 0.5f, S.PingDot, S.PingDot), dotTex);
				GUI.color = keep;
			}
			GUILayout.Label($"{ms} ms", ping);
		}
		if (tagText.Length > 0) GUILayout.Label(tagText, tag);
		if (kickable != null && GUILayout.Button("×", kick, GUILayout.Width(S.RowH - 6))) kickable.Peer?.Disconnect();
		GUILayout.EndHorizontal();
	}

	void Dot(Color c, float e)
	{
		var r = GUILayoutUtility.GetRect(S.DotSize, Z.Title + 4, GUILayout.Width(S.DotSize));
		if (Repaint)
		{
			GUI.color = W(e) * c;
			GUI.DrawTexture(new Rect(r.x, r.center.y - S.DotSize * 0.5f - 1, S.DotSize, S.DotSize), dotTex);
			GUI.color = W(e);
		}
		GUILayout.Space(S.InlineGap + 2);
	}

	void Hairline()
	{
		var r = GUILayoutUtility.GetRect(1, S.Hair, GUILayout.ExpandWidth(true), GUILayout.Height(S.Hair));
		if (!Repaint) return;
		var keep = GUI.color;
		GUI.color = keep * C.Hairline;
		GUI.DrawTexture(r, white);
		GUI.color = keep;
	}

	void DrawCards(float sw)
	{
		if (!Repaint) return;
		var now = Time.unscaledTime;
		var w = S.CardMinW; // one width for the whole stack: a ragged column of capsules reads as clutter
		foreach (var c in cards)
			w = Mathf.Max(w, S.CardPadX + S.CardGlyph + S.CardGlyphGap + cardText.CalcSize(new GUIContent(c.Text)).x + S.CardTagGap + cardTag.CalcSize(new GUIContent(c.Tag)).x + S.CardPadX);
		w = Mathf.Min(w, S.CardMaxW);
		for (var i = cards.Count - 1; i >= 0; i--)
		{
			var c = cards[i];
			if (c.Y < 0 || vis > 0 && cards.Count - 1 - i >= S.DockedCards) continue; // a menu is up: keep clear of its buttons
			var age = now - c.Born;
			var a = Mathf.Clamp01(age / M.CardIn) * (Plugin.DevCards ? 1 : Mathf.Clamp01((M.CardLife - age) / M.CardOut));
			a = a * a * (3 - 2 * a);
			var gw = cardTag.CalcSize(new GUIContent(c.Tag)).x;
			var r = new Rect(Mathf.Round((sw - w) * 0.5f), c.Y, w, S.CardH);
			GUI.color = W(a);
			cardShadow.Draw(new Rect(r.x - S.CardShadow, r.y - S.CardShadow + S.CardDrop, r.width + 2 * S.CardShadow, r.height + 2 * S.CardShadow), false, false, false, false);
			cardBg.Draw(r, false, false, false, false);
			GUI.color = W(a) * (c.Tone == Tone.Live ? C.Accent : c.Tone == Tone.Alarm ? C.Danger : C.Text3);
			GUI.DrawTexture(new Rect(r.x + S.CardPadX, r.center.y - S.CardGlyph * 0.5f, S.CardGlyph, S.CardGlyph), dotTex);
			GUI.color = W(a);
			var tx = r.x + S.CardPadX + S.CardGlyph + S.CardGlyphGap;
			GUI.Label(new Rect(tx, r.y, r.xMax - S.CardPadX - gw - S.CardTagGap - tx, r.height), c.Text, cardText);
			GUI.Label(new Rect(r.xMax - S.CardPadX - gw, r.y, gw, r.height), c.Tag, cardTag);
		}
		GUI.color = Color.white;
	}

	void Style()
	{
		if (white) return; // textures are DontSave; rebuild only if something still destroyed them
		white = New(1, 1);
		white.SetPixel(0, 0, Color.white);
		white.Apply(false, true);
		var none = new Color(0, 0, 0, C.Clear);
		dotTex = Rounded(S.TexSmall, S.TexSmall * 0.5f, Color.white, Color.white, none, 0);
		var panelTex = Rounded(S.Tex, R.Panel, C.Ground, C.Rim, C.GroundTop, 30);
		var cardTex = Rounded((int)S.CardH + 8, R.Card, C.CardGround, C.Rim, C.GroundTop, 14);
		var wellTex = Rounded(S.TexSmall, R.Well, C.Well, C.WellRim, C.WellTop, 8);
		var liftTex = Rounded(S.TexSmall, R.Pill, C.Lift, C.LiftRim, C.Highlight, 10);
		var liftHoverTex = Rounded(S.TexSmall, R.Pill, C.LiftHover, C.LiftHoverRim, C.Highlight, 10);
		var chipTex = Rounded(S.TexSmall, R.Chip, C.Lift, C.LiftRim, C.Highlight, 8);
		var chipHoverTex = Rounded(S.TexSmall, R.Chip, C.LiftHover, C.LiftHoverRim, C.Highlight, 8);
		var goldTex = Rounded(S.TexSmall, R.Pill, C.AccentFill, C.AccentBorder, C.Highlight, 10);
		var goldHoverTex = Rounded(S.TexSmall, R.Pill, C.AccentFillHover, C.AccentBorderHover, C.Highlight, 10);
		var inlineTex = Rounded(S.TexSmall, R.Inline, C.AccentFill, C.AccentBorder, C.Highlight, 8);
		var inlineHoverTex = Rounded(S.TexSmall, R.Inline, C.AccentFillHover, C.AccentBorderHover, C.Highlight, 8);
		var focusTex = Rounded(S.TexSmall, R.Focus, C.FieldFocus, C.LiftRim, none, 0);
		var hoverTex = Rounded(S.TexSmall, R.Focus, C.FieldFocus, none, none, 0);

		RectOffset Pad(int x, int y) => new(x, x, y, y);
		var slice15 = Pad((int)R.Pill, (int)R.Pill);
		shadow = new GUIStyle { normal = { background = Soft(S.ShadowTex, 30, 16, C.ShadowAlpha) }, border = Pad(46, 46) };
		box = new GUIStyle { normal = { background = panelTex }, border = Pad((int)R.Panel, (int)R.Panel), padding = new RectOffset(S.PadX, S.PadX, S.PadTop, S.PadBottom) };
		cardShadow = new GUIStyle { normal = { background = Soft(S.Tex, 20, 20, C.ShadowAlpha) }, border = Pad(31, 31) };
		cardBg = new GUIStyle { normal = { background = cardTex }, border = Pad((int)R.Card, (int)R.Card) };
		well = new GUIStyle { normal = { background = wellTex }, border = Pad((int)R.Well, (int)R.Well), padding = new RectOffset(12, 6, 1, 1) };

		title = new GUIStyle { normal = { textColor = C.Text }, fontSize = Z.Title, fontStyle = FontStyle.Bold, fixedHeight = Z.Title + 4 };
		sub = new GUIStyle { normal = { textColor = C.Text2 }, fontSize = Z.Small, wordWrap = true, margin = new RectOffset(0, 0, 3, 0) };
		chipTop = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Micro, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight, fixedHeight = Z.Title + 4, padding = new RectOffset(0, 0, 4, 0) };
		chipBottom = new GUIStyle { normal = { textColor = C.Text2 }, fontSize = Z.Small, alignment = TextAnchor.UpperRight, margin = new RectOffset(0, 0, 3, 0) };
		big = new GUIStyle { fontSize = Z.Big, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
		unit = new GUIStyle { normal = { textColor = C.Text2 }, fontSize = Z.Unit, alignment = TextAnchor.LowerLeft };
		caption = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Small };
		stepName = new GUIStyle { normal = { textColor = C.AccentText }, fontSize = Z.Step, fontStyle = FontStyle.Bold };
		micro = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Micro, fontStyle = FontStyle.Bold };
		label = new GUIStyle { normal = { textColor = C.Text }, fontSize = Z.Body, alignment = TextAnchor.MiddleLeft, fixedHeight = S.RowH, clipping = TextClipping.Clip };
		ping = new GUIStyle { normal = { textColor = C.Text2 }, fontSize = Z.Small, alignment = TextAnchor.MiddleRight, fixedHeight = S.RowH, margin = new RectOffset(5, 6, 0, 0) };
		tag = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Small, alignment = TextAnchor.MiddleRight, fixedHeight = S.RowH, margin = new RectOffset(0, 6, 0, 0) };
		placeholder = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Body, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 0, 0), clipping = TextClipping.Clip };
		cardText = new GUIStyle { normal = { textColor = C.Text }, fontSize = Z.Body, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
		cardTag = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Micro, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
		copy = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Micro, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };

		primary = new GUIStyle
		{
			normal = { background = goldTex, textColor = C.AccentText },
			hover = { background = goldHoverTex, textColor = C.AccentText },
			active = { background = goldTex, textColor = C.Text },
			border = slice15, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = Z.Body,
			fixedHeight = S.PillH, padding = Pad(16, 0), margin = new RectOffset(0, 0, 0, 0),
		};
		inline = new GUIStyle(primary) { border = Pad((int)R.Inline, (int)R.Inline), fixedHeight = S.FieldH, padding = Pad(14, 0), margin = new RectOffset(0, 0, 1, 1) };
		inline.normal.background = inline.active.background = inlineTex;
		inline.hover.background = inlineHoverTex;
		capsule = new GUIStyle
		{
			normal = { background = liftTex, textColor = C.Text },
			hover = { background = liftHoverTex, textColor = C.Text },
			active = { background = liftTex, textColor = C.Text2 },
			border = slice15, alignment = TextAnchor.MiddleLeft, fontSize = Z.Body,
			fixedHeight = S.PillH, padding = new RectOffset(14, 52, 0, 0), clipping = TextClipping.Clip,
		};
		chip = new GUIStyle
		{
			normal = { background = chipTex, textColor = C.Text2 },
			hover = { background = chipHoverTex, textColor = C.Text },
			active = { background = chipTex, textColor = C.Text3 },
			border = Pad((int)R.Chip, (int)R.Chip), alignment = TextAnchor.MiddleCenter, fontSize = Z.Small,
			fixedHeight = S.ChipH, padding = Pad((int)S.ChipPadX, 0), clipping = TextClipping.Clip,
		};
		quiet = new GUIStyle
		{
			normal = { textColor = C.Text3 },
			hover = { textColor = C.Danger },
			active = { textColor = C.Text },
			alignment = TextAnchor.MiddleLeft, fontSize = Z.Body, fixedHeight = S.RowH,
		};
		field = new GUIStyle
		{
			normal = { textColor = C.Text },
			hover = { background = hoverTex, textColor = C.Text },
			focused = { background = focusTex, textColor = C.Text },
			border = Pad((int)R.Focus, (int)R.Focus), padding = new RectOffset(8, 8, 0, 0), alignment = TextAnchor.MiddleLeft,
			fontSize = Z.Body, fixedHeight = S.FieldH, margin = new RectOffset(0, 0, 1, 1), clipping = TextClipping.Clip,
		};
		kick = new GUIStyle
		{
			normal = { textColor = C.Text3 },
			hover = { background = hoverTex, textColor = C.Danger },
			border = Pad((int)R.Focus, (int)R.Focus), alignment = TextAnchor.MiddleCenter, fontSize = Z.Kick,
			fixedHeight = S.RowH - 6, margin = new RectOffset(0, 0, 3, 3),
		};
	}

	internal static Texture2D New(int w, int h) => new(w, h, TextureFormat.RGBA32, false)
	{
		wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave,
	};

	// Signed distance to a rounded rect inset by `inset` px; negative inside.
	internal static float Sdf(float px, float py, int size, float inset, float r)
	{
		var h = size * 0.5f - inset;
		var qx = Mathf.Max(Mathf.Abs(px - size * 0.5f) - (h - r), 0);
		var qy = Mathf.Max(Mathf.Abs(py - size * 0.5f) - (h - r), 0);
		return Mathf.Sqrt(qx * qx + qy * qy) - r;
	}

	// Anti-aliased rounded rect: fill, 1px rim brighter toward the top, highlight band in the top cap (stays put under 9-slicing).
	internal static Texture2D Rounded(int size, float r, Color fill, Color rim, Color top, float topH)
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

	internal static Texture2D Soft(int size, float inset, float r, float alpha)
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
