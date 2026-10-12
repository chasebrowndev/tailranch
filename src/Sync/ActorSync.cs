using UnityEngine;

namespace TailRanch.Sync;

public static class ActorSync
{
	public static void Register() { }
	public static void Tick() { }

	// True when another machine simulates this actor (local copy is a puppet).
	// Consumers (eat, market, silo, incinerator, collectors) must ignore such actors.
	public static bool IsRemoteOwned(long actorId) => false;
	public static bool IsRemoteOwned(GameObject go) => false;
}
