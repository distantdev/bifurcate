using System.Net;

namespace Bifurcate.Core;

public enum ProbeHostRouting
{
    /// <summary>The host is a name, so it cannot be compared against the routes.</summary>
    Unknown,

    Inside,
    Outside,
}

public enum ProbeKind
{
    /// <summary>ICMP echo. Cheapest, but plenty of corporate networks drop it.</summary>
    Icmp,

    /// <summary>TCP connect to a host and port. Use when ICMP is filtered.</summary>
    Tcp,
}

public sealed record ProbeConfig
{
    public ProbeKind Type { get; init; } = ProbeKind.Icmp;

    /// <summary>
    /// A host inside the tunnel. It has to fall inside one of <see cref="BifurcateConfig.TunnelRoutes"/>,
    /// otherwise subnet-only mode sends the probe over the local connection and it never tests the VPN.
    /// </summary>
    public string Host { get; init; } = "";

    public int Port { get; init; }

    public int TimeoutMs { get; init; } = 3000;
}

public sealed record HardeningConfig
{
    public bool Enabled { get; init; } = true;

    public bool SetNetworkPrivate { get; init; } = true;

    public int[] BlockInboundTcpPorts { get; init; } = [139, 445];

    public int[] BlockInboundUdpPorts { get; init; } = [137, 138, 1900, 5353, 5355];
}

public sealed record BifurcateConfig
{
    /// <summary>Name of the Windows VPN connection to manage, exactly as it appears in Settings.</summary>
    public string VpnConnectionName { get; init; } = "";

    /// <summary>Prefixes that ride the tunnel in subnet-only mode, where there is no default route.</summary>
    public string[] TunnelRoutes { get; init; } = [];

    /// <summary>
    /// Hostnames (or IPv4 addresses) that should use the tunnel in subnet-only mode. Each is
    /// resolved to a /32 and added as a VPN route, so only that destination rides the tunnel.
    /// </summary>
    public string[] TunnelHosts { get; init; } = [];

    public ProbeConfig Probe { get; init; } = new();

    /// <summary>
    /// Service returning the caller's public IP as plain text. Blank disables the lookup; the
    /// routing verdict still works, since that comes from the local route table.
    /// </summary>
    public string PublicIpUrl { get; init; } = "https://api.ipify.org";

    /// <summary>
    /// Optional. Addresses known to belong to the VPN's egress. Only used to add certainty to the
    /// External IP status line; leaving it empty is fine.
    /// </summary>
    public string[] KnownVpnEgressIps { get; init; } = [];

    public int SweepIntervalSeconds { get; init; } = 60;

    public HardeningConfig Hardening { get; init; } = new();

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(VpnConnectionName))
        {
            errors.Add("vpnConnectionName is required. Set it to the name of your Windows VPN connection.");
        }

        foreach (string route in TunnelRoutes)
        {
            if (!NetworkPrefix.TryParse(route, out _))
            {
                errors.Add($"tunnelRoutes contains '{route}', which is not a CIDR prefix such as 10.0.0.0/16.");
            }
        }

        foreach (string host in TunnelHosts)
        {
            string problem = ValidateTunnelHost(host);
            if (problem.Length > 0) { errors.Add(problem); }
        }

        if (string.IsNullOrWhiteSpace(Probe.Host))
        {
            errors.Add("probe.host is required. Use a host inside the tunnel that answers.");
        }

        if (Probe.Type == ProbeKind.Tcp && Probe.Port is < 1 or > 65535)
        {
            errors.Add("probe.port must be between 1 and 65535 when probe.type is Tcp.");
        }

        if (Probe.TimeoutMs is < 100 or > 30000)
        {
            errors.Add("probe.timeoutMs must be between 100 and 30000.");
        }

        if (!string.IsNullOrWhiteSpace(PublicIpUrl)
            && (!Uri.TryCreate(PublicIpUrl, UriKind.Absolute, out Uri? url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)))
        {
            errors.Add("publicIpUrl must be a full http or https URL, or blank to disable the lookup.");
        }

        foreach (string ip in KnownVpnEgressIps)
        {
            if (!IPAddress.TryParse(ip, out _))
            {
                errors.Add($"knownVpnEgressIps contains '{ip}', which is not an IP address.");
            }
        }

        if (SweepIntervalSeconds is < 10 or > 3600)
        {
            errors.Add("sweepIntervalSeconds must be between 10 and 3600.");
        }

        foreach (int port in Hardening.BlockInboundTcpPorts.Concat(Hardening.BlockInboundUdpPorts))
        {
            if (port is < 1 or > 65535)
            {
                errors.Add($"hardening port {port} is outside 1-65535.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Whether the probe host falls inside the configured routes. In subnet-only mode a host that
    /// falls outside them is probed over the local connection instead of the tunnel, so the
    /// keep-alive silently stops working. A name on <see cref="TunnelHosts"/> counts as inside.
    /// Any other name cannot be range-checked, which is <see cref="ProbeHostRouting.Unknown"/>
    /// rather than a false accusation.
    /// </summary>
    public ProbeHostRouting ClassifyProbeHost()
    {
        if (!IPAddress.TryParse(Probe.Host, out IPAddress? host))
        {
            // A name on the host list is sent through the tunnel as a /32, so the keep-alive is
            // testing the VPN even though the name cannot be range-checked against CIDR prefixes.
            return TunnelHosts.Any(candidate =>
                candidate.Equals(Probe.Host, StringComparison.OrdinalIgnoreCase))
                ? ProbeHostRouting.Inside
                : ProbeHostRouting.Unknown;
        }

        foreach (string route in TunnelRoutes)
        {
            if (NetworkPrefix.TryParse(route, out NetworkPrefix prefix) && prefix.Contains(host))
            {
                return ProbeHostRouting.Inside;
            }
        }

        return ProbeHostRouting.Outside;
    }

    private static string ValidateTunnelHost(string host)
    {
        if (host.Contains('/'))
        {
            return $"tunnelHosts contains '{host}', which looks like a prefix. Put CIDR ranges in tunnelRoutes instead.";
        }

        UriHostNameType kind = Uri.CheckHostName(host);
        return kind switch
        {
            UriHostNameType.Dns or UriHostNameType.IPv4 => "",
            UriHostNameType.IPv6 =>
                $"tunnelHosts contains '{host}', which is IPv6. Only IPv4 host routes are supported.",
            _ => $"tunnelHosts contains '{host}', which is not a hostname or IPv4 address.",
        };
    }

    public static BifurcateConfig CreateSample() => new()
    {
        VpnConnectionName = "YourVpnName",
        TunnelRoutes = ["10.0.0.0/16"],
        Probe = new ProbeConfig { Type = ProbeKind.Icmp, Host = "10.0.0.10", TimeoutMs = 3000 },
    };
}
