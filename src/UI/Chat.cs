using System.Collections.Generic;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using TailRanch.Net;
using UnityEngine;
using UnityEngine.SceneManagement;
using static TailRanch.UI.Panel;

namespace TailRanch.UI;

// Text chat. Enter opens, Enter sends, Esc closes. Clients send text only; the host stamps sender id + name,
// rate-limits per sender and relays to everyone (sender included, so what you see is what the host accepted).
//
// Input: while typing, the game's own input-mode stack holds InputMode.NONE (SRInput.SetInputMode, as LockOnDeath
// does), which disables every InControl action set (move, look, vac, pause). The cursor follows TimeDirector's own
// Enable/DisableCursor. Enter is the open key: its only default binding is PlayerActions.accept, which nothing reads.
//
// Composition, two states:
//   idle  quiet feed: the last few lines as small smoked capsules above the vitals, each fading after M.ChatLife
//   open  glass column: header text → history well (hanging-indent rows) → composer capsule (GOLD rim + caret:
//         the one live value, what you are typing) → bare footer (keys left, counter right)
public class Chat : MonoBehaviour
{
	public const int MaxLen = 200;
	const float Burst = 4, Refill = 0.8f; // per sender: 4 back to back, then one every 1.25 s
	const string Field = "tailranch-chat";

	class Line
	{
		public string Name, Text;
		public float Born;
	}

	static readonly List<Line> lines = new();
	static readonly Dictionary<byte, (float tokens, float at)> buckets = new();
	static float deniedAt = -10;

	public static bool Typing { get; private set; }

	bool open, focus, holding, latched, wasActive;
	float openVis;
	string draft = "";
	vp_FPInput fp;
	int demoStep;
	float demoNext;
	GUIStyle ground, feedBg, feedShadow, well, composer, title, chipTop, nick, text, placeholder, foot, footRight;

	// ---------- network ----------

	public static void Register() => Session.On(Op.Chat, OnChat);

	static void OnChat(Player from, NetPacketReader r)
	{
		if (Session.IsHost)
		{
			if (from.Ready) Stamp(from.Id, from.Name, r.GetString());
			return;
		}
		var id = r.GetByte();
		var who = r.GetString();
		Add(who, Clean(r.GetString()));
	}

	// Returns false when the local rate limit holds the line back (the draft stays in the composer).
	static bool Send(string msg)
	{
		msg = Clean(msg);
		if (msg.Length == 0 || !Session.Active) return true;
		if (!Allow(Session.LocalId))
		{
			deniedAt = Time.unscaledTime;
			return false;
		}
		if (Session.IsHost) Stamp(Session.HostId, Plugin.Name, msg, true);
		else Session.SendToHost(Packets.Begin(Op.Chat).Also(w => w.Put(msg)), DeliveryMethod.ReliableOrdered);
		return true;
	}

	// Host: the only place a chat line becomes real. The id comes from the connection, never the packet.
	static void Stamp(byte id, string who, string msg, bool checkedLocally = false)
	{
		msg = Clean(msg);
		if (msg.Length == 0) return;
		if (!checkedLocally && !Allow(id))
		{
			Plugin.Log.LogInfo($"chat: dropped a line from {who} ({id}): rate limit");
			return;
		}
		Add(who, msg);
		Session.Broadcast(Packets.Begin(Op.Chat).Also(w => { w.Put(id); w.Put(who); w.Put(msg); }), DeliveryMethod.ReliableOrdered);
	}

	static bool Allow(byte id)
	{
		var now = Time.unscaledTime;
		var (tokens, at) = buckets.TryGetValue(id, out var b) ? b : (Burst, now);
		tokens = Mathf.Min(Burst, tokens + (now - at) * Refill);
		var ok = tokens >= 1;
		buckets[id] = (ok ? tokens - 1 : tokens, now);
		return ok;
	}

	static string Clean(string s)
	{
		if (string.IsNullOrEmpty(s)) return "";
		var sb = new StringBuilder(Mathf.Min(s.Length, MaxLen));
		foreach (var ch in s)
		{
			if (sb.Length >= MaxLen) break;
			sb.Append(char.IsControl(ch) ? ' ' : ch);
		}
		return sb.ToString().Trim();
	}

	static void Add(string who, string msg)
	{
		if (msg.Length == 0) return;
		lines.Add(new Line { Name = who, Text = msg, Born = Time.unscaledTime });
		if (lines.Count > S.ChatHistory) lines.RemoveAt(0);
		Plugin.Log.LogInfo($"chat {who}: {msg}");
	}

	// ---------- input ----------

	int Handle => GetInstanceID();

	void Start() => SceneManager.activeSceneChanged += (_, _) => Close(true);

	static bool InSession => Session.Active && Session.InWorld && (Session.IsHost || Session.InRemoteWorld);

	void Update()
	{
		var active = InSession;
		if (active && !wasActive)
		{
			lines.Clear();
			buckets.Clear();
		}
		wasActive = active;
		if (open && !active) Close(true);
		Typing = open;
		openVis = Mathf.MoveTowards(openVis, open ? 1 : 0, Time.unscaledDeltaTime / M.ChatOpen);

		var keysUp = !Input.GetKey(KeyCode.Escape) && !Input.GetKey(KeyCode.Return) && !Input.GetKey(KeyCode.KeypadEnter);
		if (keysUp) latched = false; // a held Enter auto-repeats; it must not send, reopen, send...
		if (open)
		{
			// Unpausing (clicking Resume under the composer) relocks the cursor; typing keeps it free.
			var td = Td;
			if (Cursor.lockState == CursorLockMode.Locked && td) td.EnableCursor(Fp);
		}
		// The mode is released only once Enter/Esc are up: an action set re-enabled under a held key reads a fresh
		// press (Esc would reopen the pause menu, or fire cancel on release).
		else if (holding && keysUp)
		{
			holding = false;
			SRInput.Instance.ClearInputMode(Handle);
		}
		if (Plugin.DevChatDemo) Demo();
	}

	static TimeDirector Td
	{
		get
		{
			var sc = SRSingleton<SceneContext>.Instance;
			return sc ? sc.TimeDirector : null;
		}
	}

	vp_FPInput Fp => fp ? fp : fp = FindObjectOfType<vp_FPInput>();

	bool CanOpen()
	{
		if (open || !InSession || GUIUtility.keyboardControl != 0) return false;
		var mode = SRInput.Instance.GetInputMode();
		return mode == SRInput.InputMode.DEFAULT || mode == SRInput.InputMode.PAUSE || holding;
	}

	void Open(string text = "")
	{
		open = focus = true;
		draft = text;
		if (holding) holding = false; // still on the stack from the last close
		else SRInput.Instance.SetInputMode(SRInput.InputMode.NONE, Handle);
		var td = Td;
		if (td) td.EnableCursor(Fp);
	}

	// now: release the input mode immediately (scene change, session gone) instead of once Enter/Esc are up.
	void Close(bool now)
	{
		if (open)
		{
			open = false;
			GUIUtility.keyboardControl = 0;
			var td = Td; // TimeDirector's own policy: free while anything pauses, locked otherwise
			if (td)
			{
				if (td.HasPauser()) td.EnableCursor(Fp);
				else td.DisableCursor(Fp);
			}
			holding = true;
		}
		if (holding && now)
		{
			holding = false;
			SRInput.Instance.ClearInputMode(Handle);
		}
	}

	void Demo()
	{
		if (!InSession || Session.Players.Count == 0) return;
		var now = Time.unscaledTime;
		if (demoNext == 0) demoNext = now + (Session.IsHost ? 3 : 5);
		if (now < demoNext) return;
		demoNext = now + 6;
		string[] host = { "welcome in! the ranch is all yours", "gordo by the reef is almost full", "careful, tarr spotted near the lab", "selling plorts at 1.4x right now" };
		string[] client = { "hey! loaded in fine", "grabbing carrots on the way", "wow the sky is pretty here", "heading to the dry reef" };
		var script = Session.IsHost ? host : client;
		Send(script[demoStep % script.Length]);
		if (demoStep == 1 && Session.IsHost) Send(new string('w', 40) + " " + new string('x', 230)); // cap: host keeps MaxLen
		if (demoStep == 1 && Session.IsClient)
			for (var i = 0; i < 6; i++) // bypasses the local limiter so the host's own limit is what gets exercised
				Session.SendToHost(Packets.Begin(Op.Chat).Also(w => w.Put($"burst {i}")), DeliveryMethod.ReliableOrdered);
		if (demoStep == 2 && Session.IsClient && !open && CanOpen())
		{
			Open("on my way, bringing carrots");
			Plugin.Log.LogInfo($"dev: chat composer opened (input mode {SRInput.Instance.GetInputMode()}, cursor {Cursor.lockState})");
		}
		demoStep++;
	}

	// ---------- draw ----------

	void OnGUI()
	{
		var e = Event.current;
		if (e.type == EventType.KeyDown)
		{
			var enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
			if (enter && latched)
			{
				if (open) e.Use();
			}
			else if (!open && enter && CanOpen())
			{
				Open();
				latched = true;
				e.Use();
			}
			else if (open && e.keyCode == KeyCode.Escape)
			{
				Close(false);
				e.Use();
			}
			else if (open && enter)
			{
				if (Send(draft)) Close(false);
				latched = true;
				e.Use();
			}
			else if (open && e.character == '\n') e.Use();
		}
		if (!open && openVis <= 0 && !AnyFresh()) return;

		Style();
		var s = Mathf.Clamp(Screen.height / 800f, 0.9f, 2.5f);
		var sw = Screen.width / s;
		var sh = Screen.height / s;
		var prevMatrix = GUI.matrix;
		var prevColor = GUI.color;
		var prevCursor = GUI.skin.settings.cursorColor;
		var prevSel = GUI.skin.settings.selectionColor;
		GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));
		GUI.skin.settings.cursorColor = C.Accent;
		GUI.skin.settings.selectionColor = C.Selection;

		var x = S.Margin; // the co-op panel yields the left column while typing; the feed stays under it otherwise
		var bottom = sh * S.ChatBottom;
		if (openVis < 1) DrawFeed(x, bottom, 1 - openVis);
		if (openVis > 0) DrawOpen(x, bottom, openVis);

		GUI.matrix = prevMatrix;
		GUI.color = prevColor;
		GUI.skin.settings.cursorColor = prevCursor;
		GUI.skin.settings.selectionColor = prevSel;
	}

	static bool AnyFresh() => lines.Count > 0 && Time.unscaledTime - lines[lines.Count - 1].Born < M.ChatLife;

	static float Ease(float t) => t * t * (3 - 2 * t);

	float NameW(Line l) => nick.CalcSize(new GUIContent(l.Name)).x;

	// Height of one line laid out with a hanging indent: name, then the text wrapping under itself.
	// nw: the name column (one shared width in the history well, so the messages start on one edge).
	float LineH(Line l, float w, float nw) => Mathf.Max(text.lineHeight, text.CalcHeight(new GUIContent(l.Text), w - nw - S.ChatNameGap));

	void DrawLine(Line l, Rect r, float nw)
	{
		GUI.Label(new Rect(r.x, r.y, nw, text.lineHeight), l.Name, nick);
		GUI.Label(new Rect(r.x + nw + S.ChatNameGap, r.y, r.width - nw - S.ChatNameGap, r.height), l.Text, text);
	}

	// Idle: newest at the bottom, each its own capsule sized to its text, gone after M.ChatLife.
	void DrawFeed(float x, float bottom, float vis)
	{
		if (Event.current.type != EventType.Repaint) return;
		var now = Time.unscaledTime;
		var y = bottom;
		var shown = 0;
		for (var i = lines.Count - 1; i >= 0 && shown < S.ChatFeed; i--, shown++)
		{
			var l = lines[i];
			var age = now - l.Born;
			if (age >= M.ChatLife) break;
			var a = Ease(Mathf.Clamp01(age / M.ChatIn) * Mathf.Clamp01((M.ChatLife - age) / M.ChatOut)) * vis;
			var inner = S.ChatW - 2 * S.ChatLinePadX;
			var oneLine = NameW(l) + S.ChatNameGap + text.CalcSize(new GUIContent(l.Text)).x;
			var tw = Mathf.Min(inner, oneLine + 1);
			var th = LineH(l, inner, NameW(l));
			var padY = (S.ChatLineH - text.lineHeight) * 0.5f;
			var h = th + 2 * padY;
			y -= h;
			if (DockVis > 0 && y < DockBottom + S.ChatLineGap) break; // a menu is up: only what fits under the panel
			var r = new Rect(x, y, tw + 2 * S.ChatLinePadX, h);
			GUI.color = W(a);
			feedShadow.Draw(new Rect(r.x - S.ChatShadow, r.y - S.ChatShadow + S.ChatDrop, r.width + 2 * S.ChatShadow, r.height + 2 * S.ChatShadow), false, false, false, false);
			feedBg.Draw(r, false, false, false, false);
			DrawLine(l, new Rect(r.x + S.ChatLinePadX, r.y + padY, tw, th), NameW(l));
			y -= S.ChatLineGap;
		}
		GUI.color = Color.white;
	}

	void DrawOpen(float x, float bottom, float vis)
	{
		var e = Ease(vis);
		var w = S.ChatW;
		var inner = w - 2 * S.PadX;
		var rowW = inner - 2 * S.ChatWellPad;

		// History: as many of the newest lines as fit, oldest on top.
		var heights = new List<float>();
		var histH = 0f;
		var nameCol = 0f;
		for (var i = lines.Count - 1; i >= 0 && i >= lines.Count - S.ChatFeed * 3; i--) nameCol = Mathf.Max(nameCol, NameW(lines[i]));
		for (var i = lines.Count - 1; i >= 0; i--)
		{
			var h = LineH(lines[i], rowW, nameCol);
			var add = h + (heights.Count > 0 ? S.ChatLineGap : 0);
			if (histH + add > S.ChatHistMax - 2 * S.ChatWellPad) break;
			heights.Add(h);
			histH += add;
		}
		var wellH = (heights.Count > 0 ? histH : text.lineHeight) + 2 * S.ChatWellPad;
		var headH = Z.Title + 4;
		var total = S.PadTop + headH + S.Block + wellH + S.Block + S.PillH + S.ChatFootGap + S.ChatFootH + S.PadBottom;
		var y = bottom - total + S.Slide * (1 - e) * 0.5f;
		var box = new Rect(x, y, w, total);

		GUI.color = W(e);
		if (Event.current.type == EventType.Repaint)
		{
			feedShadow.Draw(new Rect(box.x - S.Shadow, box.y - S.Shadow + S.ShadowDrop, box.width + 2 * S.Shadow, box.height + 2 * S.Shadow), false, false, false, false);
			ground.Draw(box, false, false, false, false);
		}
		var cx = x + S.PadX;
		var cy = y + S.PadTop;

		// Header: title left, who can hear you right.
		GUI.Label(new Rect(cx, cy, inner, headH), "Chat", title);
		var n = Session.Players.Count + 1;
		GUI.Label(new Rect(cx, cy, inner, headH), $"{n} {(n == 1 ? "PLAYER" : "PLAYERS")}", chipTop);
		cy += headH + S.Block;

		// History well.
		var wr = new Rect(cx, cy, inner, wellH);
		if (Event.current.type == EventType.Repaint)
		{
			well.Draw(wr, false, false, false, false);
			if (heights.Count == 0) GUI.Label(new Rect(wr.x + S.ChatWellPad, wr.y + S.ChatWellPad, rowW, text.lineHeight), "No messages yet", placeholder);
			var ly = wr.y + S.ChatWellPad;
			for (var k = heights.Count - 1; k >= 0; k--)
			{
				var l = lines[lines.Count - 1 - k];
				DrawLine(l, new Rect(wr.x + S.ChatWellPad, ly, rowW, heights[k]), nameCol);
				ly += heights[k] + S.ChatLineGap;
			}
		}
		cy += wellH + S.Block;

		// Composer: the hero and the one gold.
		var cr = new Rect(cx, cy, inner, S.PillH);
		GUI.SetNextControlName(Field);
		var next = GUI.TextField(cr, draft, MaxLen, composer);
		if (open) draft = next.Replace("\n", "");
		if (draft.Length == 0 && Event.current.type == EventType.Repaint) GUI.Label(cr, "Say something", placeholder);
		if (open && GUI.GetNameOfFocusedControl() != Field) GUI.FocusControl(Field);
		else if (open && focus) // focusing selects all; a prefilled draft should read as text with the caret after it
		{
			((TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl)).MoveTextEnd();
			focus = false;
		}
		cy += S.PillH + S.ChatFootGap;

		// Footer: keys, then the counter (or the limiter's reason) at the right.
		var denied = Time.unscaledTime - deniedAt < M.ChatDenied;
		GUI.Label(new Rect(cx + S.ChatLinePadX * 0.5f, cy, inner, S.ChatFootH), "ENTER  send     ESC  close", foot);
		footRight.normal.textColor = denied ? C.Danger : draft.Length > MaxLen * 0.9f ? C.Warn : C.Text3;
		GUI.Label(new Rect(cx, cy, inner - S.ChatLinePadX * 0.5f, S.ChatFootH), denied ? "slow down" : $"{draft.Length} / {MaxLen}", footRight);
		GUI.color = Color.white;
	}

	void Style()
	{
		if (ground != null && ground.normal.background) return;
		RectOffset Pad(int v) => new(v, v, v, v);
		var none = new Color(0, 0, 0, C.Clear);
		ground = new GUIStyle { normal = { background = Rounded(S.Tex, R.Panel, C.Ground, C.Rim, C.GroundTop, 30) }, border = Pad((int)R.Panel) };
		feedBg = new GUIStyle { normal = { background = Rounded(S.TexSmall, R.Inline, C.CardGround, C.Rim, C.GroundTop, 10) }, border = Pad((int)R.Inline) };
		feedShadow = new GUIStyle { normal = { background = Soft(S.Tex, 20, 20, C.FeedShadowAlpha) }, border = Pad(31) };
		well = new GUIStyle { normal = { background = Rounded(S.TexSmall, R.Well, C.Well, C.WellRim, C.WellTop, 8) }, border = Pad((int)R.Well) };
		var gold = Rounded(S.TexSmall, R.Pill, C.Well, C.AccentBorderHover, none, 0);
		composer = new GUIStyle
		{
			normal = { background = gold, textColor = C.Text },
			hover = { background = gold, textColor = C.Text },
			focused = { background = gold, textColor = C.Text },
			border = Pad((int)R.Pill), padding = new RectOffset(14, 14, 0, 0), alignment = TextAnchor.MiddleLeft,
			fontSize = Z.Body, clipping = TextClipping.Clip, richText = false,
		};
		title = new GUIStyle { normal = { textColor = C.Text }, fontSize = Z.Title, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
		chipTop = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Micro, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
		nick = new GUIStyle { normal = { textColor = C.Text2 }, fontSize = Z.Body, fontStyle = FontStyle.Bold, richText = false };
		text = new GUIStyle { normal = { textColor = C.Text }, fontSize = Z.Body, wordWrap = true, richText = false };
		placeholder = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Body, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(14, 14, 0, 0) };
		foot = new GUIStyle { normal = { textColor = C.Text3 }, fontSize = Z.Micro, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
		footRight = new GUIStyle(foot) { alignment = TextAnchor.MiddleRight };
	}
}
