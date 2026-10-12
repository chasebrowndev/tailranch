using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using MonomiPark.SlimeRancher.Regions;
using TailRanch.Net;
using UnityEngine;

namespace TailRanch.Sync;

// Where everyone is (zone + region set, avatar visibility, map markers) and host-driven ranch drones.
public static class PresenceSync
{
	const float RegionInterval = 0.5f, RegionRefresh = 5f;
	const float DroneInterval = 0.1f, DroneRefresh = 1f;
	const int DroneBytes = 900; // flush a DroneMotion packet past this (unreliable must fit one MTU)

	// Every known player's zone and region set, local player included. For UI.
	public static readonly Dictionary<byte, ZoneDirector.Zone> Zones = new();
	public static readonly Dictionary<byte, RegionRegistry.RegionSetId> Sets = new();

	static ZoneDirector.Zone sentZone = (ZoneDirector.Zone)int.MinValue;
	static RegionRegistry.RegionSetId sentSet;
	static float nextRegion, nextRegionRefresh, nextDrone, nextDroneRefresh, nextResolve;

	// Remote avatars found by name (PlayerSync owns them); renderers toggled when in another region set.
	class View
	{
		public GameObject go;
		public Renderer[] renderers;
		public bool hidden;
	}
	static readonly Dictionary<byte, View> views = new();

	// Every live drone (both sides). Host: last sent state. Client: interpolating view.
	class Track
	{
		public string site;
		public bool siteResolved;
		public byte anim;
		public Vector3 pos = Vector3.positiveInfinity;
		public Quaternion rot;
		public byte sentAnim = byte.MaxValue;
		public int ident = -1, count = -1;
		public DronePuppet view;
	}
	static readonly Dictionary<Drone, Track> drones = new();
	static readonly Dictionary<string, Drone> bySite = new();

	public static ZoneDirector.Zone ZoneOf(byte id) => Zones.TryGetValue(id, out var z) ? z : ZoneDirector.Zone.NONE;

	public static RegionRegistry.RegionSetId SetOf(byte id) => Sets.TryGetValue(id, out var s) ? s : RegionRegistry.RegionSetId.UNSET;

	public static void Register()
	{
		// PlayerRegion  *  id byte, zone sbyte (ZoneDirector.Zone), regionSet sbyte (RegionRegistry.RegionSetId)
		// c→h on change / every 5 s; host keeps it and relays; joiners get everyone's on Ready.
		Session.On(Op.PlayerRegion, (from, r) =>
		{
			var id = r.GetByte();
			if (Session.IsHost)
			{
				if (id != from.Id) return;
				Session.Relay(from, r, DeliveryMethod.ReliableOrdered);
			}
			Zones[id] = (ZoneDirector.Zone)r.GetSByte();
			Sets[id] = (RegionRegistry.RegionSetId)r.GetSByte();
		});

		// DroneMotion  h→c  count byte, then per drone: siteId string (owning GadgetSite), pos, rot,
		// anim byte (DroneAnimator.Id), ident int (carried Identifiable.Id, NONE when empty), count byte
		Session.On(Op.DroneMotion, (_, r) =>
		{
			if (!Session.IsClient || !Session.InRemoteWorld || !Session.InWorld) return;
			int n = r.GetByte();
			for (var i = 0; i < n; i++)
			{
				var site = r.GetString();
				var pos = r.GetVector3();
				var rot = r.GetQuaternion();
				var anim = r.GetByte();
				var ident = r.GetInt();
				var count = r.GetByte();
				var d = Resolve(site);
				if (!d) continue;
				var t = drones[d];
				if (!t.view) t.view = d.gameObject.GetComponent<DronePuppet>() ?? d.gameObject.AddComponent<DronePuppet>();
				t.view.Push(pos, rot);
				using (Session.Apply())
				{
					if (anim != t.sentAnim && d.animator)
					{
						t.sentAnim = anim;
						d.animator.SetAnimation((DroneAnimator.Id)anim);
					}
					if (ident != t.ident || count != t.count)
					{
						t.ident = ident;
						t.count = count;
						d.ammo.Clear();
						if (ident != (int)Identifiable.Id.NONE && count > 0)
							d.ammo.MaybeAddToSpecificSlot((Identifiable.Id)ident, null, 0, count, true);
					}
				}
			}
		});

		Session.PlayerReady += p =>
		{
			foreach (var id in Zones.Keys.ToList())
				if (id != p.Id) Session.SendTo(p, Region(id, Zones[id], SetOf(id)), DeliveryMethod.ReliableOrdered);
			nextDroneRefresh = 0;
		};
	}

	public static void Tick()
	{
		if (!Session.Active || !Session.InWorld)
		{
			if (Zones.Count > 0 || views.Count > 0) Reset();
			return;
		}
		if (Session.IsClient && !Session.InRemoteWorld) return;
		var now = Time.unscaledTime;
		if (now >= nextRegion)
		{
			nextRegion = now + RegionInterval;
			ReportRegion(now);
			Prune();
		}
		UpdateViews();
		if (Session.IsHost && now >= nextDrone)
		{
			nextDrone = now + DroneInterval;
			var full = now >= nextDroneRefresh;
			if (full) nextDroneRefresh = now + DroneRefresh;
			SendDrones(full);
		}
	}

	static void Reset()
	{
		Zones.Clear();
		Sets.Clear();
		views.Clear();
		sentZone = (ZoneDirector.Zone)int.MinValue;
		foreach (var t in drones.Values) t.view = null;
	}

	// ---------- regions ----------

	static NetDataWriter Region(byte id, ZoneDirector.Zone zone, RegionRegistry.RegionSetId set) =>
		Packets.Begin(Op.PlayerRegion).Also(w =>
		{
			w.Put(id);
			w.Put((sbyte)zone);
			w.Put((sbyte)set);
		});

	static void ReportRegion(float now)
	{
		var ctx = SRSingleton<SceneContext>.Instance;
		var tracker = ctx.Player.GetComponent<PlayerZoneTracker>();
		var zone = tracker ? tracker.GetCurrentZone() : ZoneDirector.Zone.NONE;
		var set = ctx.RegionRegistry.GetCurrentRegionSetId();
		Zones[Session.LocalId] = zone;
		Sets[Session.LocalId] = set;
		if (zone == sentZone && set == sentSet && now < nextRegionRefresh) return;
		sentZone = zone;
		sentSet = set;
		nextRegionRefresh = now + RegionRefresh;
		Session.Distribute(Region(Session.LocalId, zone, set), DeliveryMethod.ReliableOrdered);
	}

	static void Prune()
	{
		foreach (var id in Zones.Keys.ToList())
			if (id != Session.LocalId && !Session.Players.ContainsKey(id))
			{
				Zones.Remove(id);
				Sets.Remove(id);
			}
		foreach (var id in views.Keys.ToList())
			if (!Session.Players.ContainsKey(id)) views.Remove(id);
	}

	// Hide avatars in another region set (their coordinates mean nothing here); give each a map marker.
	static void UpdateViews()
	{
		var local = SRSingleton<SceneContext>.Instance.RegionRegistry.GetCurrentRegionSetId();
		foreach (var p in Session.Players.Values)
		{
			if (!views.TryGetValue(p.Id, out var v) || !v.go)
			{
				var go = GameObject.Find($"TailRanch Player {p.Id}");
				if (!go) continue;
				views[p.Id] = v = new View { go = go, renderers = go.GetComponentsInChildren<Renderer>(true) };
				RemoteMapMarker.Attach(go, p);
			}
			var set = SetOf(p.Id);
			var hide = set != RegionRegistry.RegionSetId.UNSET && set != local;
			if (hide == v.hidden) continue;
			v.hidden = hide;
			foreach (var r in v.renderers)
				if (r) r.enabled = !hide;
		}
	}

	// ---------- drones ----------

	static string SiteOf(Drone d, Track t)
	{
		if (t.siteResolved) return t.site;
		t.siteResolved = true;
		try
		{
			var site = d.gadget ? d.gadget.GetComponentInParent<GadgetSite>() : null;
			t.site = site ? site.id : null;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"drone site id: {e.Message}");
		}
		return t.site;
	}

	static Drone Resolve(string site)
	{
		if (bySite.TryGetValue(site, out var d) && d && drones.ContainsKey(d)) return d;
		if (Time.unscaledTime < nextResolve) return null;
		nextResolve = Time.unscaledTime + 1f;
		bySite.Clear();
		foreach (var kv in drones)
			if (kv.Key && SiteOf(kv.Key, kv.Value) is { } s) bySite[s] = kv.Key;
		return bySite.TryGetValue(site, out d) ? d : null;
	}

	static void SendDrones(bool full)
	{
		NetDataWriter w = null;
		byte n = 0;
		foreach (var kv in drones)
		{
			var d = kv.Key;
			var t = kv.Value;
			if (!d || !d.gadget) continue;
			var region = d.gadget.region;
			if (region && region.Hibernated) continue;
			if (SiteOf(d, t) == null) continue;
			var tr = d.transform;
			var ident = (int)d.ammo.GetSlotName();
			var count = Mathf.Clamp(d.ammo.GetSlotCount(), 0, 255);
			if (!full && (tr.position - t.pos).sqrMagnitude < 0.0001f && Quaternion.Angle(tr.rotation, t.rot) < 0.5f
				&& t.anim == t.sentAnim && ident == t.ident && count == t.count) continue;
			t.pos = tr.position;
			t.rot = tr.rotation;
			t.sentAnim = t.anim;
			t.ident = ident;
			t.count = count;

			if (w == null)
			{
				w = Packets.Begin(Op.DroneMotion);
				w.Put((byte)0);
				n = 0;
			}
			w.Put(t.site);
			w.Put(t.pos);
			w.Put(t.rot);
			w.Put(t.anim);
			w.Put(ident);
			w.Put((byte)count);
			n++;
			if (w.Length >= DroneBytes || n == byte.MaxValue)
			{
				w.Data[1] = n;
				Session.Broadcast(w, DeliveryMethod.Sequenced);
				w = null;
			}
		}
		if (w == null) return;
		w.Data[1] = n;
		Session.Broadcast(w, DeliveryMethod.Sequenced);
	}

	[HarmonyPatch(typeof(Drone), nameof(Drone.Awake))]
	static class DroneAwake
	{
		static void Postfix(Drone __instance) => drones[__instance] = new Track();
	}

	[HarmonyPatch(typeof(Drone), nameof(Drone.OnDestroy))]
	static class DroneDestroy
	{
		static void Postfix(Drone __instance)
		{
			if (drones.TryGetValue(__instance, out var t) && t.site != null) bySite.Remove(t.site);
			drones.Remove(__instance);
		}
	}

	// Host: remember the current animation so it rides along with motion.
	[HarmonyPatch(typeof(DroneAnimator), nameof(DroneAnimator.SetAnimation))]
	static class DroneAnim
	{
		static void Postfix(DroneAnimator __instance, DroneAnimator.Id id)
		{
			var d = __instance.parent;
			if (d && drones.TryGetValue(d, out var t) && !Session.Applying) t.anim = (byte)id;
		}
	}
}

// Client view of a host-simulated drone: kinematic, follows DroneMotion snapshots. The drone's
// AI (DroneSubbehaviourPlexer.RegistryFixedUpdate) is already suppressed by RanchSync.
public class DronePuppet : MonoBehaviour
{
	const float InterpDelay = 0.1f;

	struct Snap
	{
		public float t;
		public Vector3 pos;
		public Quaternion rot;
	}

	readonly List<Snap> snaps = new();
	Rigidbody rb;

	void Awake()
	{
		rb = GetComponent<Rigidbody>();
		Pin();
	}

	void Pin()
	{
		if (!rb || rb.isKinematic) return;
		rb.isKinematic = true;
		rb.interpolation = RigidbodyInterpolation.None;
	}

	public void Push(Vector3 pos, Quaternion rot)
	{
		Pin();
		if (!isActiveAndEnabled) snaps.Clear();
		if (snaps.Count == 0) transform.SetPositionAndRotation(pos, rot);
		snaps.Add(new Snap { t = Time.unscaledTime, pos = pos, rot = rot });
		if (snaps.Count > 32) snaps.RemoveAt(0);
	}

	void FixedUpdate() => Pin();

	void Update()
	{
		if (snaps.Count == 0) return;
		var t = Time.unscaledTime - InterpDelay;
		while (snaps.Count > 2 && snaps[1].t <= t) snaps.RemoveAt(0);
		Snap a = snaps[0], b = snaps.Count > 1 ? snaps[1] : snaps[0];
		var k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 1f;
		transform.SetPositionAndRotation(Vector3.Lerp(a.pos, b.pos, k), Quaternion.Slerp(a.rot, b.rot, k));
	}
}

// Remote player on the map: the local player's arrow marker, tinted gold, with a name label.
// UnityEngine.UI isn't referenced, so Graphic/Text are driven through reflection.
public class RemoteMapMarker : DisplayOnMap
{
	static readonly Type graphicT = AccessTools.TypeByName("UnityEngine.UI.Graphic");
	static readonly Type textT = AccessTools.TypeByName("UnityEngine.UI.Text");
	static readonly AccessTools.FieldRef<MapUI, float> mainAdjust = AccessTools.FieldRefAccess<MapUI, float>("mainRotationAdjustment");
	static readonly AccessTools.FieldRef<MapUI, float> desertAdjust = AccessTools.FieldRefAccess<MapUI, float>("desertRotationAdjustment");

	byte id;
	string playerName;

	public static void Attach(GameObject avatar, Player p)
	{
		var player = SRSingleton<SceneContext>.Instance.Player;
		var own = player ? player.GetComponent<PlayerDisplayOnMap>() : null;
		if (!own || !own.markerPrefab || !SRSingleton<Map>.Instance) return;
		try
		{
			// Inactive so markerPrefab is set before DisplayOnMap.Awake instantiates it.
			var go = new GameObject("map");
			go.SetActive(false);
			go.transform.SetParent(avatar.transform, false);
			var m = go.AddComponent<RemoteMapMarker>();
			m.markerPrefab = own.markerPrefab;
			m.id = p.Id;
			m.playerName = p.Name;
			go.SetActive(true);
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"map marker: {e.Message}");
		}
	}

	public override void Awake()
	{
		base.Awake();
		var marker = GetMarker();
		if (!marker || graphicT == null) return;
		foreach (var g in marker.GetComponentsInChildren(graphicT, true))
			Traverse.Create(g).Property("color").SetValue(RemoteAvatar.Gold);
		if (textT == null) return;
		var label = new GameObject("name", typeof(RectTransform));
		var rt = (RectTransform)label.transform;
		rt.SetParent(marker.transform, false);
		rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
		rt.pivot = new Vector2(0.5f, 0f);
		rt.anchoredPosition = new Vector2(0f, 2f);
		rt.sizeDelta = new Vector2(240f, 28f);
		var text = Traverse.Create(label.AddComponent(textT));
		text.Property("font").SetValue(Resources.GetBuiltinResource<Font>("Arial.ttf"));
		text.Property("fontSize").SetValue(18);
		text.Property("alignment").SetValue(TextAnchor.LowerCenter);
		text.Property("horizontalOverflow").SetValue(HorizontalWrapMode.Overflow);
		text.Property("raycastTarget").SetValue(false);
		text.Property("color").SetValue(RemoteAvatar.Gold);
		text.Property("text").SetValue(playerName);
	}

	public override ZoneDirector.Zone GetZoneId() => PresenceSync.ZoneOf(id);

	public override RegionRegistry.RegionSetId GetRegionSetId()
	{
		var s = PresenceSync.SetOf(id);
		return s == RegionRegistry.RegionSetId.UNSET ? RegionRegistry.RegionSetId.HOME : s;
	}

	// The map only charts the home world and the desert.
	public override bool ShowOnMap()
	{
		var s = GetRegionSetId();
		return s == RegionRegistry.RegionSetId.HOME || s == RegionRegistry.RegionSetId.DESERT;
	}

	public override Quaternion GetCurrentRotation() => transform.rotation;

	public override void Refresh()
	{
		var ui = SRSingleton<Map>.Instance ? SRSingleton<Map>.Instance.mapUI : null;
		var marker = GetMarker();
		if (!ui || !marker) return;
		var adj = GetRegionSetId() == RegionRegistry.RegionSetId.DESERT ? desertAdjust(ui) : mainAdjust(ui);
		marker.Rotate(Quaternion.Euler(adj, transform.eulerAngles.y, 0f));
	}
}
