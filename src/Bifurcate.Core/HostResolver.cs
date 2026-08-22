using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bifurcate.Core;

/// <summary>
/// Turns a tunnel host into IPv4 addresses. Names go through DNS; a dotted IPv4 is used as-is.
/// IPv6 is ignored, matching the /32 routes the VPN profile accepts here.
/// </summary>
public static class HostResolver
{
    public static IReadOnlyList<IPAddress> ResolveIPv4(string host) =>
        ResolveIPv4(host, []);

    /// <summary>
    /// When <paramref name="dnsServers"/> is set (the tunnel adapter's DNS), those servers are
    /// asked first so the /32 matches what the SQL client will use. The process resolver is only
    /// a fallback when the VPN DNS cannot be reached.
    /// </summary>
    public static IReadOnlyList<IPAddress> ResolveIPv4(string host, IReadOnlyList<IPAddress> dnsServers)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            IPAddress? literal = AsIPv4(parsed);
            return literal is null ? [] : [literal];
        }

        if (dnsServers.Count > 0)
        {
            DnsAQuery.Lookup viaTunnel = DnsAQuery.Query(host, dnsServers);
            if (viaTunnel.ServerAnswered) { return viaTunnel.Addresses; }
        }

        IPAddress[] answers = Dns.GetHostAddresses(host);
        return [.. answers.Select(AsIPv4).OfType<IPAddress>().Distinct()];
    }

    public static IReadOnlyList<IPAddress> IPv4DnsServers(string interfaceAlias)
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!adapter.Name.Equals(interfaceAlias, StringComparison.OrdinalIgnoreCase)) { continue; }

            return
            [
                .. adapter.GetIPProperties().DnsAddresses
                    .Select(AsIPv4)
                    .OfType<IPAddress>()
            ];
        }

        return [];
    }

    private static IPAddress? AsIPv4(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork) { return address; }
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : null;
    }
}
