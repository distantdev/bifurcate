using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

/// <summary>
/// The live routing table, which is what traffic actually follows. A VPN profile's saved settings
/// are only read when the connection is dialed, so the two can disagree until a reconnect.
/// </summary>
public sealed class RoutingInspector : IDisposable
{
    private const string DefaultRoute = "0.0.0.0/0";

    /// <summary>Any routable public address works; nothing is sent to it.</summary>
    private static readonly IPAddress EgressTestTarget = IPAddress.Parse("8.8.8.8");

    private readonly CimSession _session = Cim.CreateSession();

    /// <summary>A default route on the tunnel means every packet is going through the VPN right now.</summary>
    public bool HasDefaultRoute(string interfaceAlias) =>
        LiveRoutes(interfaceAlias).Contains(DefaultRoute);

    public IReadOnlyList<string> LiveRoutes(string interfaceAlias)
    {
        IEnumerable<CimInstance> routes = _session.QueryInstances(
            Cim.StandardNamespace, "WQL", "SELECT DestinationPrefix, InterfaceAlias FROM MSFT_NetRoute");

        return
        [
            .. routes
                .Where(r => Cim.StringOf(r, "InterfaceAlias")
                    .Equals(interfaceAlias, StringComparison.OrdinalIgnoreCase))
                .Select(r => Cim.StringOf(r, "DestinationPrefix"))
                .Where(p => p.Length > 0)
        ];
    }

    /// <summary>True when every one of the given prefixes is present on the adapter right now.</summary>
    public bool AllRoutesLive(string interfaceAlias, IEnumerable<string> prefixes)
    {
        IReadOnlyList<string> live = LiveRoutes(interfaceAlias);
        return prefixes.All(p => live.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which adapter Windows would use to reach the public internet, or null if it cannot tell.
    /// This is the leak verdict: it needs no configuration and no third-party service, unlike
    /// comparing a public IP against a known VPN address.
    /// </summary>
    /// <remarks>
    /// Connecting a UDP socket sends no packets. It only asks the stack to pick a source address,
    /// which is exactly the routing decision being asked about.
    /// </remarks>
    public static string? PublicEgressInterfaceAlias()
    {
        IPAddress? source = PublicEgressSourceAddress();
        if (source is null) { return null; }

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.Equals(source)) { return adapter.Name; }
            }
        }

        return null;
    }

    public static IPAddress? PublicEgressSourceAddress()
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(EgressTestTarget, 65530));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public void Dispose() => _session.Dispose();
}
