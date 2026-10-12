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

	void Awake()
	{
		I = this;
		Log = Logger;
		Port = Config.Bind("Net", "Port", 7777, "UDP port the host listens on");
		PlayerName = Config.Bind("Net", "PlayerName", System.Environment.MachineName, "Name shown above your avatar");
		JoinTarget = Config.Bind("Net", "JoinTarget", "", "Last joined tailnet host (MagicDNS name or 100.x IP)");
		AllowLoopback = Config.Bind("Dev", "AllowLoopback", false, "Accept 127.0.0.1 peers (local two-instance testing)");
		if (System.Environment.GetEnvironmentVariable("TAILRANCH_NAME") is { Length: > 0 } name) PlayerName.Value = name;
		if (System.Environment.GetEnvironmentVariable("TAILRANCH_LOOPBACK") == "1") AllowLoopback.Value = true;

		new Harmony(Guid).PatchAll();
		gameObject.AddComponent<Panel>();
		SceneManager.sceneLoaded += (scene, _) =>
		{
			if (scene.name == "MainMenu")
				Session.OnMainMenu();
		};
		Log.LogInfo($"TailRanch {Version} loaded");
	}

	void Update()
	{
		Session.Poll();
		DevAuto.Tick();
		PlayerSync.Tick();
		ClockSync.Tick();
		ActorSync.Tick();
	}

	void OnApplicationQuit() => Session.Stop("quit");
}
