using UnityEngine;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TailRanch.Dev;
using TailRanch.Net;
using TailRanch.Sync;
using TailRanch.UI;
using UnityEngine.SceneManagement;

namespace TailRanch;

[BepInPlugin(Guid, "TailRanch", Version)]
public class Plugin : BaseUnityPlugin
{
	public const string Guid = "dev.chasebrown.tailranch";
	public const string Version = "0.4.3";
	static int failedHooks;
	// The menu's version label (Application.version says 1.4.3 on 1.4.4). Needs GameContext, so only valid from the menu on.
	public static string GameBuild => SRSingleton<GameContext>.Instance ? SRSingleton<GameContext>.Instance.MessageDirector.Get("build", "m.version") : "?";
	public static string Unsupported =>
		GameBuild != GameVersion ? $"Slime Rancher {GameBuild} is unsupported (needs {GameVersion})"
		: failedHooks > 0 ? $"{failedHooks} game hooks failed; sync may break" : null;
	public const string GameVersion = "1.4.4"; // the only game build TailRanch is written and tested against

	public static Plugin I;
	public static ManualLogSource Log;

	public static ConfigEntry<int> Port;
	public static ConfigEntry<string> PlayerName;
	public static ConfigEntry<string> JoinTarget;
	public static ConfigEntry<bool> AllowLoopback;
	public static ConfigEntry<string> PlayerId, Password, JoinPassword;
	public static ConfigEntry<bool> ShareMoney, ShareUpgrades, ShareBlueprints, ShareProgress;

	// Dev harness (scripts/sr.sh), env-only so they never persist into the config.
	public static readonly bool DevAutoHost = System.Environment.GetEnvironmentVariable("TAILRANCH_AUTOHOST") == "1";
	public static readonly string DevAutoJoin = System.Environment.GetEnvironmentVariable("TAILRANCH_AUTOJOIN") ?? "";
	public static readonly bool DevStats = System.Environment.GetEnvironmentVariable("TAILRANCH_STATS") == "1";
	public static readonly string DevName = System.Environment.GetEnvironmentVariable("TAILRANCH_NAME") ?? "";
	public static readonly bool DevLoopback = System.Environment.GetEnvironmentVariable("TAILRANCH_LOOPBACK") == "1";
	public static readonly bool DevPause = System.Environment.GetEnvironmentVariable("TAILRANCH_PAUSE") is "1" or "solo";
	// TAILRANCH_PAUSE=solo: autohost loads the world but never hosts, then pauses (the offline in-world panel).
	public static readonly bool DevSolo = System.Environment.GetEnvironmentVariable("TAILRANCH_PAUSE") == "solo";
	// Pins one event card of every kind on screen (they otherwise fade after 4.5 s).
	public static readonly bool DevCards = System.Environment.GetEnvironmentVariable("TAILRANCH_CARDS") == "1";
	// Feeds the panel's latency meter a synthetic 20-70 ms round trip (loopback reads a flat 0 ms).
	public static readonly bool DevPingDemo = System.Environment.GetEnvironmentVariable("TAILRANCH_PINGDEMO") == "1";
	// Each instance chats on a loop over the real network path; the client also opens its composer with a draft.
	public static readonly bool DevChatDemo = System.Environment.GetEnvironmentVariable("TAILRANCH_CHATDEMO") == "1";
	// The host's panel opens on its manage view.
	public static readonly bool DevManage = System.Environment.GetEnvironmentVariable("TAILRANCH_MANAGE") == "1";
	// The host steps 4 m ahead and turns around once a peer joins, so the peer sees its avatar head-on.
	public static readonly bool DevNudge = System.Environment.GetEnvironmentVariable("TAILRANCH_NUDGE") == "1";
	// Scripted purchases/sales on both sides with state digests to diff (Dev/SelfTest).
	public static readonly bool DevSelfTest = System.Environment.GetEnvironmentVariable("TAILRANCH_SELFTEST") == "1";
	// Scripted vac/death/hen/garden/walk checks with logs and screenshots (Dev/Probe).
	public static readonly (int min, int max) DevLag = System.Environment.GetEnvironmentVariable("TAILRANCH_LAG") is { Length: > 0 } lag
		&& lag.Split(',') is { Length: 2 } mm && int.TryParse(mm[0], out var lo) && int.TryParse(mm[1], out var hi) ? (lo, hi) : (0, 0);
	public static readonly bool DevProbe = System.Environment.GetEnvironmentVariable("TAILRANCH_PROBE") == "1";

	public static string Name => DevName.Length > 0 ? DevName : PlayerName.Value.Trim().Length > 0 ? PlayerName.Value.Trim() : System.Environment.MachineName;

	void Awake()
	{
		Application.runInBackground = true; // two instances on one machine: unfocused window must keep ticking
		I = this;
		Log = Logger;
		Port = Config.Bind("Net", "Port", 7777, "UDP port the host listens on");
		PlayerName = Config.Bind("Net", "PlayerName", System.Environment.MachineName, "Name shown above your avatar");
		JoinTarget = Config.Bind("Net", "JoinTarget", "", "Last joined tailnet host (MagicDNS name or 100.x IP)");
		PlayerId = Config.Bind("Net", "PlayerId", "", "Stable id the host keys your unshared progress by (generated; keep it to keep your progress)");
		if (PlayerId.Value.Length == 0) PlayerId.Value = System.Guid.NewGuid().ToString("N");
		Password = Config.Bind("Net", "Password", "", "Host: password joiners must give (empty = open)");
		JoinPassword = Config.Bind("Net", "JoinPassword", "", "Password sent when joining");
		ShareMoney = Config.Bind("Share", "Money", true, "Host: one wallet and key count for everyone (off: per player)");
		ShareUpgrades = Config.Bind("Share", "Upgrades", true, "Host: personal upgrades shared (off: per player)");
		ShareBlueprints = Config.Bind("Share", "Blueprints", true, "Host: blueprints and gadget stock shared (off: per player)");
		ShareProgress = Config.Bind("Share", "Progress", true, "Host: slimepedia, map and mail shared (off: per player)");
		AllowLoopback = Config.Bind("Dev", "AllowLoopback", false, "Accept 127.0.0.1 peers (local two-instance testing)");

		// Per class so one stale target logs instead of killing every other patch.
		var harmony = new Harmony(Guid);
		int ok = 0, failed = 0;
		foreach (var t in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
		{
			if (!t.IsDefined(typeof(HarmonyPatch), false)) continue;
			try { harmony.CreateClassProcessor(t).Patch(); ok++; }
			catch (System.Exception e) { failed++; Log.LogError($"patch {t.FullName} failed: {e.InnerException?.Message ?? e.Message}"); }
		}
		Log.LogInfo($"patched {ok} classes, {failed} failed");
		failedHooks = failed;
		gameObject.AddComponent<Panel>();
		gameObject.AddComponent<Chat>();
		SceneManager.sceneLoaded += (scene, _) =>
		{
			Log.LogInfo($"scene loaded: {scene.name}");
			if (scene.name == "MainMenu")
			{
				try { Session.OnMainMenu(); }
				catch (System.Exception e) { Log.LogError(e); }
			}
		};
		Log.LogInfo($"TailRanch {Version} loaded");
	}

	// Each tick isolated: one module throwing every frame must not starve the others (or the net poll).
	static readonly (string name, System.Action tick)[] ticks =
	{
		("Session", Session.Poll),
		("DevAuto", DevAuto.Tick),
		("PlayerSync", PlayerSync.Tick),
		("ClockSync", ClockSync.Tick),
		("ActorSync", ActorSync.Tick),
		("WorldSync", WorldSync.Tick),
		("PresenceSync", PresenceSync.Tick),
		("ProfileSync", ProfileSync.Tick),
		("Profiles", Profiles.Tick),
		("SelfTest", SelfTest.Tick),
		("Probe", Probe.Tick),
	};
	static readonly string[] lastError = new string[ticks.Length];

	void Update()
	{
		for (var i = 0; i < ticks.Length; i++)
		{
			try { ticks[i].tick(); }
			catch (System.Exception e)
			{
				// Log each distinct failure once instead of every frame.
				var msg = e.ToString();
				if (msg == lastError[i]) continue;
				lastError[i] = msg;
				Log.LogError($"{ticks[i].name}.Tick: {msg}");
			}
		}
	}

	void OnApplicationQuit()
	{
		try { Session.Stop("quit"); }
		catch (System.Exception e) { Log.LogError(e); }
	}
}
