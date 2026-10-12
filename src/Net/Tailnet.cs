using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace TailRanch.Net;

// Tailscale addresses: 100.64.0.0/10 (CGNAT) and fd7a:115c:a1e0::/48.
public static class Tailnet
{
	public static bool Allowed(IPAddress a)
	{
		if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
		if (IPAddress.IsLoopback(a)) return Plugin.DevLoopback || Plugin.AllowLoopback.Value;
		var b = a.GetAddressBytes();
		if (a.AddressFamily == AddressFamily.InterNetwork)
			return b[0] == 100 && (b[1] & 0xC0) == 64;
		return b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
	}

	// MagicDNS name or literal IP → first tailnet address, IPv4 preferred.
	public static IPAddress Resolve(string target)
	{
		if (IPAddress.TryParse(target, out var ip)) return ip;
		var all = Dns.GetHostAddresses(target);
		return all.Where(Allowed).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
	}
}
