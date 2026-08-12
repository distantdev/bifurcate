namespace Bifurcate.Core;

/// <summary>
/// Turns a snapshot into the six dashboard lines. Deliberately pure, with no Windows calls, so the
/// wording and the colors are covered by tests instead of being verified by squinting at a window.
/// </summary>
public static class StatusEvaluator
{
    public static IReadOnlyList<StatusLine> Evaluate(StatusSnapshot state) =>
    [
        KeepAlive(state),
        Tunnel(state),
        Routing(state),
        ExternalIp(state),
        Network(state),
        Startup(state),
    ];

    private static StatusLine KeepAlive(StatusSnapshot state) => state.Service switch
    {
        ServiceState.Running => new(StatusKind.KeepAlive,
            "Keep-Alive: Running, holding the VPN open", StatusSeverity.Good),

        ServiceState.Stopped => new(StatusKind.KeepAlive,
            "Keep-Alive: Installed but stopped", StatusSeverity.Bad),

        ServiceState.NotInstalled => new(StatusKind.KeepAlive,
            "Keep-Alive: Background service is not installed", StatusSeverity.Bad),

        ServiceState.Unknown => new(StatusKind.KeepAlive,
            "Keep-Alive: Cannot read the service state", StatusSeverity.Neutral),

        _ => throw new ArgumentOutOfRangeException(nameof(state), state.Service, "Unhandled service state."),
    };

    private static StatusLine Tunnel(StatusSnapshot state)
    {
        string host = state.Config.Probe.Host;

        if (state.Probe is null)
        {
            return new(StatusKind.Tunnel, "Tunnel: Checking...", StatusSeverity.Neutral);
        }

        if (!state.Probe.Reachable)
        {
            return state.Tunnel is null
                ? new(StatusKind.Tunnel, "Tunnel: VPN is not connected", StatusSeverity.Neutral)
                : new(StatusKind.Tunnel, $"Tunnel: No answer from {host}", StatusSeverity.Bad);
        }

        // A reachable probe proves nothing if the packets never entered the tunnel, which is what
        // happens in subnet-only mode when the host sits outside the configured routes.
        bool subnetOnly = state.Vpn?.SplitTunneling ?? false;
        if (subnetOnly && state.Config.ClassifyProbeHost() == ProbeHostRouting.Outside)
        {
            return new(StatusKind.Tunnel,
                $"Tunnel: {host} answered, but it is outside your tunnel routes",
                StatusSeverity.Warning);
        }

        return new(StatusKind.Tunnel,
            $"Tunnel: {host} answered in {state.Probe.LatencyMs} ms", StatusSeverity.Good);
    }

    private static StatusLine Routing(StatusSnapshot state)
    {
        if (state.Vpn is null)
        {
            return new(StatusKind.Routing,
                $"Routing: No VPN named '{state.Config.VpnConnectionName}' on this PC",
                StatusSeverity.Neutral);
        }

        bool connected = state.Tunnel is not null;

        if (!state.Vpn.SplitTunneling)
        {
            // Routing is read when the connection is dialed, so a live session can still be
            // following the previous mode.
            return connected && !state.DefaultRouteOnTunnel
                ? new(StatusKind.Routing, "Routing: All traffic - reconnect to apply", StatusSeverity.Warning)
                : new(StatusKind.Routing, "Routing: All traffic goes through the VPN", StatusSeverity.Good);
        }

        if (state.Vpn.Routes.Length == 0)
        {
            return new(StatusKind.Routing,
                "Routing: Subnet only, but no subnets are set", StatusSeverity.Bad);
        }

        if (connected && (state.DefaultRouteOnTunnel || !state.ConfiguredRoutesLive))
        {
            return new(StatusKind.Routing, "Routing: Subnet only - reconnect to apply", StatusSeverity.Warning);
        }

        return new(StatusKind.Routing,
            $"Routing: {string.Join(", ", state.Vpn.Routes)} only, others not tunneled", StatusSeverity.Good);
    }

    private static StatusLine ExternalIp(StatusSnapshot state)
    {
        bool haveIp = !string.IsNullOrEmpty(state.PublicIp);

        if (state.PublicIp is null && state.PublicEgressAdapter is null)
        {
            return new(StatusKind.ExternalIp, "External IP: Checking...", StatusSeverity.Neutral);
        }

        if (!haveIp && state.PublicEgressAdapter is null)
        {
            return new(StatusKind.ExternalIp, "External IP: Could not check right now", StatusSeverity.Neutral);
        }

        // Without a lookup the address is unknown, but the adapter still says which way traffic left.
        string subject = haveIp ? state.PublicIp! : $"unknown, leaving via {state.PublicEgressAdapter}";

        if (state.Vpn is null || state.Tunnel is null)
        {
            return new(StatusKind.ExternalIp, $"External IP: {subject}", StatusSeverity.Neutral);
        }

        // The route table is the authority. A configured egress address only adds certainty, since
        // observing that address is direct proof the traffic came out of the VPN.
        bool viaVpn =
            (haveIp && state.Config.KnownVpnEgressIps.Contains(state.PublicIp!, StringComparer.OrdinalIgnoreCase))
            || (state.PublicEgressAdapter is not null
                && state.PublicEgressAdapter.Equals(state.Tunnel.InterfaceAlias, StringComparison.OrdinalIgnoreCase));

        // Each case names where the traffic came out and then whether that is what was asked for,
        // so the two halves of the pair read as opposites rather than as unrelated sentences.
        return (viaVpn, state.Vpn.SplitTunneling) switch
        {
            (true, true) => new(StatusKind.ExternalIp,
                $"External IP: {subject} - from the VPN, leaking past Subnet Only", StatusSeverity.Bad),

            (true, false) => new(StatusKind.ExternalIp,
                $"External IP: {subject} - from the VPN, as intended", StatusSeverity.Good),

            (false, true) => new(StatusKind.ExternalIp,
                $"External IP: {subject} - from your ISP, as intended", StatusSeverity.Good),

            (false, false) => new(StatusKind.ExternalIp,
                $"External IP: {subject} - from your ISP, not the VPN yet", StatusSeverity.Warning),
        };
    }

    private static StatusLine Network(StatusSnapshot state)
    {
        if (!state.Config.Hardening.Enabled)
        {
            return new(StatusKind.Network, "Network: Hardening is turned off", StatusSeverity.Neutral);
        }

        if (state.Tunnel is null)
        {
            return new(StatusKind.Network, "Network: VPN is not connected", StatusSeverity.Neutral);
        }

        if (state.Config.Hardening.SetNetworkPrivate && state.Tunnel.Category != NetworkCategory.Private)
        {
            return new(StatusKind.Network,
                $"Network: {state.Tunnel.Category} - this PC is exposed", StatusSeverity.Bad);
        }

        return state.FirewallRulesCurrent
            ? new(StatusKind.Network, "Network: Private, hidden from others on the VPN", StatusSeverity.Good)
            : new(StatusKind.Network, "Network: Private, but file sharing is not blocked", StatusSeverity.Warning);
    }

    private static StatusLine Startup(StatusSnapshot state) => state.StartupEnabled
        ? new(StatusKind.Startup, "Startup: Opens automatically when you sign in", StatusSeverity.Good)
        : new(StatusKind.Startup, "Startup: Opens only when you launch it", StatusSeverity.Neutral);
}
