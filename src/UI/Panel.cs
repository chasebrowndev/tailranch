using TailRanch.Net;
using TailRanch.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TailRanch.UI;

// F8. Gold on near-black, hard edges.
public class Panel : MonoBehaviour
{
	static readonly Color Bg = new Color(0.04f, 0.04f, 0.04f, 0.94f);
	static readonly Color Dim = new Color(0.5f, 0.42f, 0.2f);

	bool open;
	bool styled;
	GUIStyle box, label, dim, button, field;
	string target;

	void Start() => target = Plugin.JoinTarget.Value;

	void Update()
	{
		if (Input.GetKeyDown(KeyCode.F8))
		{
			open = !open;
			if (!open && Session.InWorld) vp_Utility.LockCursor = true;
		}
	}

	void OnGUI()
	{
		var menu = SceneManager.GetActiveScene().name == "MainMenu";
		if (!open && !menu) return;
		if (open && Session.InWorld) vp_Utility.LockCursor = false;
		Style();

		GUILayout.BeginArea(new Rect(16, 16, 320, 400), box);
		GUILayout.Label("TAILRANCH", label);
		GUILayout.Label(Session.Status, dim);
		GUILayout.Space(8);

		if (!Session.Active)
		{
			GUILayout.Label("name", dim);
			Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, 32, field);
			if (menu)
			{
				GUILayout.Label("host (magicdns / 100.x)", dim);
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
			GUILayout.Label($"{Plugin.PlayerName.Value} (you)", label);
			foreach (var p in Session.Players.Values)
				GUILayout.Label(p.Peer != null ? $"{p.Name}  {p.Peer.Ping} ms" : p.Name, label);
			if (Session.IsClient) GUILayout.Label($"rtt {Session.Rtt} ms", dim);
			GUILayout.Space(8);
			if (GUILayout.Button("DISCONNECT", button)) Session.Stop("offline");
		}
		GUILayout.EndArea();
	}

	void Style()
	{
		if (styled) return;
		styled = true;
		var bg = Tex(Bg);
		var gold = Tex(RemoteAvatar.Gold);
		var black = Tex(new Color(0.08f, 0.08f, 0.08f));
		box = new GUIStyle { normal = { background = bg }, padding = new RectOffset(12, 12, 12, 12), border = new RectOffset(0, 0, 0, 0) };
		label = new GUIStyle { normal = { textColor = RemoteAvatar.Gold }, fontSize = 14, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 2, 2) };
		dim = new GUIStyle(label) { normal = { textColor = Dim }, fontStyle = FontStyle.Normal, fontSize = 12, wordWrap = true };
		button = new GUIStyle
		{
			normal = { background = gold, textColor = Color.black },
			hover = { background = Tex(new Color(0.95f, 0.78f, 0.3f)), textColor = Color.black },
			active = { background = black, textColor = RemoteAvatar.Gold },
			alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13,
			fixedHeight = 30, margin = new RectOffset(0, 0, 6, 0),
		};
		field = new GUIStyle
		{
			normal = { background = black, textColor = RemoteAvatar.Gold },
			focused = { background = black, textColor = Color.white },
			padding = new RectOffset(6, 6, 6, 6), fontSize = 13, fixedHeight = 28, margin = new RectOffset(0, 0, 2, 6),
		};
	}

	static Texture2D Tex(Color c)
	{
		var t = new Texture2D(1, 1);
		t.SetPixel(0, 0, c);
		t.Apply();
		return t;
	}
}
