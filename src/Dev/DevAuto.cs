using System.Linq;
using System.Text;
using TailRanch.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TailRanch.Dev;

// Env-driven auto host/join + packet stats for scripts/sr.sh.
public static class DevAuto
{
	const float MenuSettle = 2f, JoinRetry = 5f, StatsInterval = 5f;
	const int MaxJoinTries = 24;

	static float menuSince = -1f;
	static bool hostLoadStarted, hosted, joined;
	static int joinTries;
	static float nextJoin, nextStats;

	static readonly long[] txCount = new long[256], txBytes = new long[256], rxCount = new long[256], rxBytes = new long[256];

	public static void Tick()
	{
		if (SceneManager.GetActiveScene().name == "MainMenu")
		{
			if (menuSince < 0f) menuSince = Time.unscaledTime;
		}
		else menuSince = -1f;
		var menuReady = menuSince >= 0f && Time.unscaledTime - menuSince >= MenuSettle;

		if (Plugin.DevAutoHost) AutoHost(menuReady);
		if (Plugin.DevAutoJoin.Length > 0) AutoJoin(menuReady);
		if (Plugin.DevStats && Time.unscaledTime >= nextStats) FlushStats();
	}

	static void AutoHost(bool menuReady)
	{
		if (!hostLoadStarted && menuReady)
		{
			hostLoadStarted = true;
			var asd = SRSingleton<GameContext>.Instance.AutoSaveDirector;
			var newest = asd.AvailableGamesByGameName().Values.SelectMany(l => l)
				.Where(s => !s.isInvalid && !s.gameOver)
				.OrderByDescending(s => s.saveTimestamp).FirstOrDefault();
			if (newest != null)
			{
				Plugin.Log.LogInfo($"dev autohost: loading {newest.displayName} ({newest.saveName})");
				asd.BeginLoad(newest.name, newest.saveName, () => Plugin.Log.LogError("dev autohost: load failed"));
			}
			else
			{
				Plugin.Log.LogInfo("dev autohost: no saves, starting a new game");
				asd.LoadNewGame("TailRanch", Identifiable.Id.PINK_SLIME, PlayerState.GameMode.CLASSIC,
					() => Plugin.Log.LogError("dev autohost: new game failed"));
			}
		}
		if (!hosted && Session.InWorld)
		{
			hosted = true;
			Session.Host();
		}
	}

	static void AutoJoin(bool menuReady)
	{
		if (joined) return;
		if (Session.InRemoteWorld)
		{
			joined = true;
			return;
		}
		if (!menuReady || Session.Active || joinTries >= MaxJoinTries || Time.unscaledTime < nextJoin) return;
		joinTries++;
		nextJoin = Time.unscaledTime + JoinRetry;
		Plugin.Log.LogInfo($"dev autojoin: {Plugin.DevAutoJoin} (try {joinTries}/{MaxJoinTries})");
		Session.Join(Plugin.DevAutoJoin);
	}

	public static void Count(bool sent, byte op, int bytes)
	{
		if (!Plugin.DevStats) return;
		if (sent)
		{
			txCount[op]++;
			txBytes[op] += bytes;
		}
		else
		{
			rxCount[op]++;
			rxBytes[op] += bytes;
		}
	}

	static void FlushStats()
	{
		nextStats = Time.unscaledTime + StatsInterval;
		var sb = new StringBuilder();
		Append(sb, "tx", txCount, txBytes);
		Append(sb, "rx", rxCount, rxBytes);
		if (sb.Length > 0) Plugin.Log.LogInfo($"stats{sb}");
	}

	static void Append(StringBuilder sb, string dir, long[] count, long[] bytes)
	{
		var head = false;
		for (var i = 0; i < 256; i++)
		{
			if (count[i] == 0) continue;
			if (!head)
			{
				sb.Append(' ').Append(dir);
				head = true;
			}
			sb.Append(' ').Append((Op)i).Append('=').Append(count[i]).Append('/').Append(bytes[i]).Append('B');
			count[i] = 0;
			bytes[i] = 0;
		}
	}
}
