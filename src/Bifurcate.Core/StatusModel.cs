namespace Bifurcate.Core;

public enum StatusKind
{
    KeepAlive,
    Tunnel,
    Routing,
    ExternalIp,
    Network,
    DnsBypass,
    Startup,
}

public enum StatusSeverity
{
    /// <summary>Not applicable or not known yet.</summary>
    Neutral,

    Good,

    /// <summary>Working, but not the way it was asked for. Usually means reconnect.</summary>
    Warning,

    Bad,
}

public sealed record StatusLine(StatusKind Kind, string Text, StatusSeverity Severity);

public enum ServiceState
{
    Unknown,
    NotInstalled,
    Running,
    Stopped,
}

/// <summary>
/// Everything the dashboard needs, gathered once so the evaluation itself stays pure and testable.
/// Nulls mean "not known yet" rather than "false", which is what lets the probe lines say
/// "checking..." instead of briefly claiming failure.
/// </summary>
public sealed record StatusSnapshot
{
    public BifurcateConfig Config { get; init; } = new();

    public ServiceState Service { get; init; } = ServiceState.Unknown;

    /// <summary>The saved VPN profile, or null when no connection by that name exists.</summary>
    public VpnConnectionInfo? Vpn { get; init; }

    /// <summary>The live network profile for the tunnel, or null when it is not connected.</summary>
    public NetworkProfileInfo? Tunnel { get; init; }

    public bool DefaultRouteOnTunnel { get; init; }

    /// <summary>
    /// True when the configured tunnel subnets are on the adapter right now. Host /32s are left
    /// out: they are applied while connected and can lag a moment without needing a reconnect.
    /// </summary>
    public bool ConfiguredRoutesLive { get; init; }

    public bool FirewallRulesCurrent { get; init; }

    /// <summary>Adapter Windows would use to reach the public internet, or null if unknown.</summary>
    public string? PublicEgressAdapter { get; init; }

    /// <summary>Null while the probe is in flight.</summary>
    public ProbeResult? Probe { get; init; }

    /// <summary>Null while in flight, empty when the lookup failed.</summary>
    public string? PublicIp { get; init; }

    /// <summary>
    /// Tunnel hosts that have no IPv4 yet, so Subnet Only would send them out the ISP. Empty when
    /// every name resolved or last-known /32s are still in place.
    /// </summary>
    public IReadOnlyList<string> UnresolvedTunnelHosts { get; init; } = [];

    public DnsBypassStatus DnsBypass { get; init; } = new();

    public bool StartupEnabled { get; init; }
}

public sealed record DnsBypassStatus
{
    public bool Enabled { get; init; }
    public int DomainCount { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public bool RulesApplied { get; init; }
    public string? Warning { get; init; }
}
