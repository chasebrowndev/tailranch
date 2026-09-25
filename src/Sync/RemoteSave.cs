using System;
using System.IO;
using HarmonyLib;
using TailRanch.Net;

namespace TailRanch.Sync;

// Join = host serializes its live game with the game's own save writer; client feeds
// those bytes through the normal load path under a virtual save name.
public static class RemoteSave
{
	public const string Name = "tailranch_remote";
	static byte[] pending;

	public static byte[] Snapshot()
	{
		var saved = SRSingleton<GameContext>.Instance.AutoSaveDirector.SavedGame;
		saved.Pull(SRSingleton<SceneContext>.Instance.GameModel);
		using var ms = new MemoryStream();
		saved.Save(ms);
		return ms.ToArray();
	}

	public static void Load(byte[] bytes, Action onLoaded, Action onError)
	{
		pending = bytes;
		SceneContext.onSceneLoaded += _ => onLoaded();
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
