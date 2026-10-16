using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using LiteNetLib;
using MonomiPark.SlimeRancher;
using MonomiPark.SlimeRancher.DataModel;
using MonomiPark.SlimeRancher.Persist;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

[Flags]
public enum Shared : byte { None = 0, Money = 1, Upgrades = 2, Blueprints = 4, Progress = 8, All = 15 }

// What the host shares between players. Host: its config. Client: the host's flags, from Welcome.
public static class Share
{
	public static Shared Remote = Shared.All;

	public static Shared Flags => Session.IsClient ? Remote
		: (Plugin.ShareMoney.Value ? Shared.Money : 0) | (Plugin.ShareUpgrades.Value ? Shared.Upgrades : 0)
		| (Plugin.ShareBlueprints.Value ? Shared.Blueprints : 0) | (Plugin.ShareProgress.Value ? Shared.Progress : 0);

	public static bool Money => (Flags & Shared.Money) != 0;
	public static bool Upgrades => (Flags & Shared.Upgrades) != 0;
	public static bool Blueprints => (Flags & Shared.Blueprints) != 0;
	public static bool Progress => (Flags & Shared.Progress) != 0;
}

// Unshared state lives on the host, per save and per player id (names change, ids don't):
// BepInEx/config/TailRanch/profiles/<save>/<uid>.dat = the client's PlayerV14 then PediaV03, in the game's
// own persist format. The client uploads them (Op.Private, gzipped) every 15 s (the vac goes stale fast) and whenever it pauses,
// even while everything is shared, so unsharing later forks from current values, not a stale file.
// On join the host overlays the vac, position and unshared fields onto that player's world snapshot.
// No file yet = an empty vac and the rest forked from the host's state.
public static class Profiles
{
	const float Interval = 15f;
	static float next;

	static readonly MethodInfo pullPlayer = AccessTools.Method(typeof(SavedGame), "Pull", new[] { typeof(GameModel), typeof(PlayerV14) });
	static readonly MethodInfo pullPedia = AccessTools.Method(typeof(SavedGame), "Pull", new[] { typeof(GameModel), typeof(PediaV03) });
	static readonly MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");

	public static void Register()
	{
		// c→h  gzipped PlayerV14 + PediaV03 bytes
		Session.On(Op.Private, (from, r) =>
		{
			var gz = r.GetBytesWithLength();
			if (!Session.IsHost || !from.Ready || string.IsNullOrEmpty(from.Uid) || !Session.InWorld) return;
			var bytes = RemoteSave.Unpack(gz);
			Read(bytes); // throws on garbage before it can replace a good file
			var path = PathOf(from.Uid);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllBytes(path, bytes);
		});
	}

	public static void Tick()
	{
		if (Time.unscaledTime >= next) Upload();
	}

	// Client: send this player's own sections.
	public static void Upload()
	{
		if (!Session.IsClient || !Session.InRemoteWorld || !Session.InWorld) return;
		next = Time.unscaledTime + Interval;
		var saved = SRSingleton<GameContext>.Instance.AutoSaveDirector.SavedGame;
		var gm = SRSingleton<SceneContext>.Instance.GameModel;
		var player = new PlayerV14();
		var pedia = new PediaV03();
		pullPlayer.Invoke(saved, new object[] { gm, player });
		pullPedia.Invoke(saved, new object[] { gm, pedia });
		using var ms = new MemoryStream();
		player.Write(ms);
		pedia.Write(ms);
		Session.SendToHost(Packets.Begin(Op.Private).Also(w => w.PutBytesWithLength(RemoteSave.Gzip(ms))), DeliveryMethod.ReliableOrdered);
	}

	// Host, on join: the snapshot with this player's unshared fields. A shallow copy, so the host's own
	// SavedGame state (and any other joiner's pending snapshot) is never touched.
	// The vac, health/energy and where they stood are always per player; a first joiner starts with an empty vac.
	public static GameV12 Apply(GameV12 state, string uid)
	{
		var flags = Share.Flags;
		var s = (GameV12)clone.Invoke(state, null);
		var p = s.player = (PlayerV14)clone.Invoke(state.player, null);
		var path = PathOf(uid);
		PlayerV14 mine = null;
		PediaV03 minePedia = null;
		if (File.Exists(path))
		{
			try { (mine, minePedia) = Read(File.ReadAllBytes(path)); }
			catch (Exception e) { Plugin.Log.LogError($"profile {path}: {e.Message}"); }
		}
		if (mine == null)
		{
			p.ammo = new Dictionary<PlayerState.AmmoMode, List<AmmoDataV02>>();
			foreach (var kv in state.player.ammo)
				p.ammo[kv.Key] = kv.Value.Select(_ => new AmmoDataV02 { id = Identifiable.Id.NONE, count = 0, emotionData = new SlimeEmotionDataV02() }).ToList();
			Plugin.Log.LogInfo($"joiner {uid} has no profile yet: empty vac, rest from host");
			return s;
		}
		p.ammo = mine.ammo;
		p.health = mine.health;
		p.energy = mine.energy;
		p.radiation = mine.radiation;
		p.playerPos = mine.playerPos;
		p.playerRotEuler = mine.playerRotEuler;
		p.regionSetId = mine.regionSetId;
		Plugin.Log.LogInfo($"joiner gets their own vac/position{(flags == Shared.All ? "" : $" and {~flags & Shared.All}")} from {path}");
		if ((flags & Shared.Money) == 0)
		{
			p.currency = mine.currency;
			p.keys = mine.keys;
		}
		if ((flags & Shared.Upgrades) == 0)
		{
			p.upgrades = mine.upgrades;
			p.availUpgrades = mine.availUpgrades;
			p.upgradeLocks = mine.upgradeLocks;
		}
		if ((flags & Shared.Blueprints) == 0)
		{
			p.blueprints = mine.blueprints;
			p.availBlueprints = mine.availBlueprints;
			p.blueprintLocks = mine.blueprintLocks;
			p.gadgets = mine.gadgets;
		}
		if ((flags & Shared.Progress) == 0)
		{
			p.mail = mine.mail;
			p.unlockedZoneMaps = mine.unlockedZoneMaps;
			var pd = s.pedia = (PediaV03)clone.Invoke(state.pedia, null);
			pd.unlockedIds = minePedia.unlockedIds;
			pd.progressGivenForPediaCount = minePedia.progressGivenForPediaCount;
		}
		return s;
	}

	static (PlayerV14, PediaV03) Read(byte[] bytes)
	{
		using var ms = new MemoryStream(bytes);
		var player = new PlayerV14();
		var pedia = new PediaV03();
		player.Load(ms);
		pedia.Load(ms);
		return (player, pedia);
	}

	static string PathOf(string uid) => Path.Combine(Path.Combine(Path.Combine(Path.Combine(Paths.ConfigPath, "TailRanch"), "profiles"),
		Safe(SRSingleton<GameContext>.Instance.AutoSaveDirector.SavedGame.GetName())), Safe(uid) + ".dat");

	static string Safe(string s)
	{
		foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
		return s.Length > 0 ? s : "_";
	}

	// Pausing is the way out of the world (quit, disconnect): upload first.
	[HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.PauseGame))]
	static class OnPause
	{
		static void Postfix() => Upload();
	}
}
