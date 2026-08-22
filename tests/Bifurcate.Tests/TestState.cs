using Bifurcate.Core;

namespace Bifurcate.Tests;

/// <summary>Builders for evaluator input, so each test only states what it actually cares about.</summary>
internal static class TestState
{
    public const string VpnName = "CorpVpn";
    public const string Alias = "CorpVpn";
    public const string LocalAdapter = "Wi-Fi";
    public const string InsideHost = "10.50.0.10";

    public static BifurcateConfig Config(
        string probeHost = InsideHost,
        string[]? tunnelRoutes = null,
        string[]? tunnelHosts = null,
        bool hardeningEnabled = true,
        bool setNetworkPrivate = true,
        string[]? knownEgressIps = null) => new()
        {
            VpnConnectionName = VpnName,
            TunnelRoutes = tunnelRoutes ?? ["10.50.0.0/16"],
            TunnelHosts = tunnelHosts ?? [],
            Probe = new ProbeConfig { Type = ProbeKind.Icmp, Host = probeHost, TimeoutMs = 3000 },
            KnownVpnEgressIps = knownEgressIps ?? [],
            Hardening = new HardeningConfig
            {
                Enabled = hardeningEnabled,
                SetNetworkPrivate = setNetworkPrivate,
            },
        };

    public static VpnConnectionInfo Vpn(bool splitTunneling, string[]? routes = null, bool connected = true) => new()
    {
        Name = VpnName,
        ServerAddress = "198.51.100.7",
        SplitTunneling = splitTunneling,
        ConnectionStatus = connected ? "Connected" : "Disconnected",
        TunnelType = "L2tp",
        Routes = routes ?? (splitTunneling ? ["10.50.0.0/16"] : []),
    };

    public static NetworkProfileInfo Tunnel(NetworkCategory category = NetworkCategory.Private) => new()
    {
        Name = VpnName,
        InterfaceAlias = Alias,
        InterfaceIndex = 42,
        Category = category,
    };

    /// <summary>A fully healthy subnet-only setup. Tests mutate one thing at a time from here.</summary>
    public static StatusSnapshot Healthy() => new()
    {
        Config = Config(),
        Service = ServiceState.Running,
        Vpn = Vpn(splitTunneling: true),
        Tunnel = Tunnel(),
        DefaultRouteOnTunnel = false,
        ConfiguredRoutesLive = true,
        FirewallRulesCurrent = true,
        PublicEgressAdapter = LocalAdapter,
        Probe = new ProbeResult { Reachable = true, LatencyMs = 22 },
        PublicIp = "203.0.113.9",
        StartupEnabled = true,
    };

    public static StatusLine Line(this IReadOnlyList<StatusLine> lines, StatusKind kind) =>
        lines.Single(line => line.Kind == kind);
}
