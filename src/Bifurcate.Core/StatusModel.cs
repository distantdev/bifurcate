namespace Bifurcate.Core;

public enum StatusKind
{
    KeepAlive,
    Tunnel,
    Routing,
    ExternalIp,
    Network,
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

    /// <summary>True when every configured prefix is present on the tunnel adapter right now.</summary>
    public bool ConfiguredRoutesLive { get; init; }

    public bool FirewallRulesCurrent { get; init; }

    /// <summary>Adapter Windows would use to reach the public internet, or null if unknown.</summary>
    public string? PublicEgressAdapter { get; init; }

    /// <summary>Null while the probe is in flight.</summary>
    public ProbeResult? Probe { get; init; }

    /// <summary>Null while in flight, empty when the lookup failed.</summary>
    public string? PublicIp { get; init; }

    public bool StartupEnabled { get; init; }
}
