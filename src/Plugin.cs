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
	public const string Version = "0.2.0";

	public static Plugin I;
	public static ManualLogSource Log;

	public static ConfigEntry<int> Port;
	public static ConfigEntry<string> PlayerName;
	public static ConfigEntry<string> JoinTarget;
	public static ConfigEntry<bool> AllowLoopback;

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

	public static string Name => DevName.Length > 0 ? DevName : PlayerName.Value;

	void Awake()
	{
		Application.runInBackground = true; // two instances on one machine: unfocused window must keep ticking
		I = this;
		Log = Logger;
		Port = Config.Bind("Net", "Port", 7777, "UDP port the host listens on");
		PlayerName = Config.Bind("Net", "PlayerName", System.Environment.MachineName, "Name shown above your avatar");
		JoinTarget = Config.Bind("Net", "JoinTarget", "", "Last joined tailnet host (MagicDNS name or 100.x IP)");
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
		gameObject.AddComponent<Panel>();
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
