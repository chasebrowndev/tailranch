using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Dev;

// TAILRANCH_SELFTEST=1 (scripts/sr.sh): once connected, each side buys and sells through the same
// game calls the shop UIs make (client first, then host), and both log a state digest to diff.
public static class SelfTest
{
	const float Settle = 12f, Step = 3f, HostOffset = 45f, DigestEvery = 10f;

	static float start = -1f, nextDigest;
	static int next;
	static readonly List<(string name, Action run)> steps = new();
	static string lastDigest;

	public static void Tick()
	{
		if (!Plugin.DevSelfTest || !Session.InWorld) return;
		var live = Session.IsHost ? Session.Players.Count > 0 : Session.InRemoteWorld;
		if (!live) return;
		var t = Time.unscaledTime;
		if (start < 0f)
		{
			start = t + Settle + (Session.IsHost ? HostOffset : 0f);
			Build();
			Plugin.Log.LogInfo($"selftest: {steps.Count} steps start in {start - t:0}s");
		}
		while (next < steps.Count && t >= start + next * Step)
		{
			var (name, run) = steps[next++];
			try
			{
				run();
				Plugin.Log.LogInfo($"selftest step {next}/{steps.Count} {name}: done ({Digest()})");
			}
			catch (Exception e) { Plugin.Log.LogError($"selftest step {next}/{steps.Count} {name}: FAILED {e}"); }
		}
		if (t >= nextDigest)
		{
			nextDigest = t + DigestEvery;
			var d = Digest();
			if (d != lastDigest) Plugin.Log.LogInfo($"selftest state: {d}");
			lastDigest = d;
		}
	}

	static SceneContext Ctx => SRSingleton<SceneContext>.Instance;
	static string Me => Session.IsHost ? "host" : "client";

	static void Build()
	{
		var ps = Ctx.PlayerState;
		steps.Add(("earn 5000", () => ps.AddCurrency(5000)));
		steps.Add(("key", () => ps.AddKey()));
		steps.Add(("personal upgrade", () =>
		{
			var up = Enum.GetValues(typeof(PlayerState.Upgrade)).Cast<PlayerState.Upgrade>().First(ps.CanGetUpgrade);
			var cost = SRSingleton<GameContext>.Instance.LookupDirector.GetUpgradeDefinition(up).Cost;
			ps.SpendCurrency(cost);
			ps.AddUpgrade(up, isFirstTime: true);
			Plugin.Log.LogInfo($"selftest: {Me} bought {up} for {cost}");
		}));
		LandPlot corral = null;
		steps.Add(("buy corral", () =>
		{
			// Client takes the first empty plot, host the last, so they never collide.
			var empties = UnityEngine.Object.FindObjectsOfType<LandPlot>().Where(p => p.typeId == LandPlot.Id.EMPTY)
				.OrderBy(p => p.GetComponentInParent<LandPlotLocation>().id).ToList();
			var plot = Session.IsHost ? empties.Last() : empties.First();
			var loc = plot.GetComponentInParent<LandPlotLocation>();
			ps.SpendCurrency(250);
			var go = loc.Replace(plot, SRSingleton<GameContext>.Instance.LookupDirector.GetPlotPrefab(LandPlot.Id.CORRAL));
			corral = go.GetComponent<LandPlot>();
			Plugin.Log.LogInfo($"selftest: {Me} built corral on {loc.id}");
		}));
		steps.Add(("corral walls", () =>
		{
			ps.SpendCurrency(150);
			corral.AddUpgrade(LandPlot.Upgrade.WALLS);
		}));
		steps.Add(("7zee rank", () =>
		{
			ps.SpendCurrency(1000);
			Ctx.ProgressDirector.AddProgress(ProgressDirector.ProgressType.CORPORATE_PARTNER);
		}));
		steps.Add(("blueprint", () =>
		{
			var g = Session.IsHost ? Gadget.Id.SPRING_PAD : Gadget.Id.MARKET_LINK;
			ps.SpendCurrency(500);
			Ctx.GadgetDirector.AddBlueprint(g);
		}));
		steps.Add(("refinery +3 pink", () => Ctx.GadgetDirector.AddToRefinery(Identifiable.Id.PINK_PLORT, 3, false)));
		steps.Add(("sell 5 pink plorts", (Action)(() =>
		{
			var market = UnityEngine.Object.FindObjectsOfType<ScorePlort>().OrderBy(s => s.GetComponentInParent<DroneNetwork>() != null).First();
			var pos = market.GetComponent<Collider>().bounds.center;
			var prefab = SRSingleton<GameContext>.Instance.LookupDirector.GetPrefab(Identifiable.Id.PINK_PLORT);
			// AsPlayer: the client side of a vac shot (unscoped client spawns are dropped).
			for (var i = 0; i < 5; i++)
				Sync.ActorSync.AsPlayer(() => SRBehaviour.InstantiateActor(prefab, MonomiPark.SlimeRancher.Regions.RegionRegistry.RegionSetId.HOME, pos + Vector3.up * (0.3f * i), Quaternion.identity));
			Plugin.Log.LogInfo($"selftest: {Me} dropped 5 pink plorts at market {market.name} {pos}");
		})));
		steps.Add(("wait", () => { }));
		steps.Add(("wait", () => { }));
		steps.Add(("final", () => { }));
	}

	static string Digest()
	{
		var ps = Ctx.PlayerState;
		var sb = new StringBuilder();
		sb.Append("money=").Append(ps.GetCurrency()).Append(" keys=").Append(ps.GetKeys());
		sb.Append(" ups=").Append(string.Join(",", Enum.GetValues(typeof(PlayerState.Upgrade)).Cast<PlayerState.Upgrade>().Where(ps.HasUpgrade)));
		sb.Append(" 7zee=").Append(Ctx.ProgressDirector.GetProgress(ProgressDirector.ProgressType.CORPORATE_PARTNER));
		sb.Append(" bp=").Append(string.Join(",", new[] { Gadget.Id.MARKET_LINK, Gadget.Id.SPRING_PAD }.Where(Ctx.GadgetDirector.HasBlueprint)));
		sb.Append(" refPink=").Append(Ctx.GadgetDirector.GetRefineryCount(Identifiable.Id.PINK_PLORT));
		sb.Append(" pinkPrice=").Append(Ctx.EconomyDirector.GetCurrValue(Identifiable.Id.PINK_PLORT));
		sb.Append(" plots=").Append(string.Join(";", Ctx.GameModel.AllLandPlots().OrderBy(kv => kv.Key)
			.Select(kv => $"{kv.Key}:{kv.Value.typeId}{(kv.Value.upgrades.Count > 0 ? "+" + string.Join("+", kv.Value.upgrades.OrderBy(u => u)) : "")}")));
		return sb.ToString();
	}
}
