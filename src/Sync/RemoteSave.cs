using System;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using MonomiPark.SlimeRancher.Persist;
using TailRanch.Net;

namespace TailRanch.Sync;

// Join = host serializes its live game with the game's own save writer; client feeds
// those bytes through the normal load path under a virtual save name.
public static class RemoteSave
{
	public const string Name = "tailranch_remote";
	static byte[] pending;

	// Main thread. Pull builds a fresh GameV12 (SavedGame.cs:792), so the returned object
	// stays put while the game keeps running and later saves Pull new ones.
	public static GameV12 Pull()
	{
		var saved = SRSingleton<GameContext>.Instance.AutoSaveDirector.SavedGame;
		saved.Pull(SRSingleton<SceneContext>.Instance.GameModel);
		return saved.GameState;
	}

	// Any thread. A few lists in the pulled state still alias live models (player upgrades,
	// gordo fashions, ammo emotions), so a concurrent change can make this throw.
	public static byte[] Pack(GameV12 state, out int raw)
	{
		using var ms = new MemoryStream();
		state.Write(ms);
		raw = (int)ms.Length;
		using var gz = new MemoryStream();
		using (var z = new GZipStream(gz, CompressionMode.Compress, true))
			z.Write(ms.GetBuffer(), 0, raw);
		return gz.ToArray();
	}

	public static byte[] Unpack(byte[] bytes)
	{
		using var z = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
		using var ms = new MemoryStream();
		z.CopyTo(ms);
		return ms.ToArray();
	}

	public static void Load(byte[] bytes, Action onLoaded, Action onError)
	{
		pending = bytes;
		// A failed load reverts to MainMenu without unsubscribing this, and MainMenu's SceneContext fires it.
		SceneContext.onSceneLoaded += _ => { if (!Levels.isMainMenu()) onLoaded(); };
		SRSingleton<GameContext>.Instance.AutoSaveDirector.BeginLoad("tailranch", Name, onError);
	}

	[HarmonyPatch(typeof(FileStorageProvider), nameof(FileStorageProvider.GetGameData))]
	static class GetGameData
	{
		static bool Prefix(string name, MemoryStream dataStream)
		{
			if (name != Name || pending == null) return true;
			dataStream.Write(pending, 0, pending.Length);
			return false;
		}
	}

	[HarmonyPatch(typeof(FileStorageProvider), nameof(FileStorageProvider.GetGameId))]
	static class GetGameId
	{
		static bool Prefix(string name, ref string __result)
		{
			if (name != Name) return true;
			__result = "tailranch";
			return false;
		}
	}

	// Host is the only writer; a client never saves the borrowed world.
	[HarmonyPatch(typeof(AutoSaveDirector), "SaveGame")]
	static class NoClientSave
	{
		static bool Prefix() => !(Session.IsClient || Session.InRemoteWorld);
	}
}
