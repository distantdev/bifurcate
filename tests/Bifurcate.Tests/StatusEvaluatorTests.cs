using Bifurcate.Core;

namespace Bifurcate.Tests;

public class StatusEvaluatorTests
{
    [Fact]
    public void ProducesTheSixLinesInAFixedOrder()
    {
        IReadOnlyList<StatusLine> lines = StatusEvaluator.Evaluate(TestState.Healthy());

        StatusKind[] expected =
        [
            StatusKind.KeepAlive,
            StatusKind.Tunnel,
            StatusKind.Routing,
            StatusKind.ExternalIp,
            StatusKind.Network,
            StatusKind.Startup,
        ];

        Assert.Equal(expected, lines.Select(line => line.Kind).ToArray());
    }

    [Fact]
    public void HealthySetupIsGreenAcrossTheBoard()
    {
        IReadOnlyList<StatusLine> lines = StatusEvaluator.Evaluate(TestState.Healthy());

        Assert.All(lines, line => Assert.Equal(StatusSeverity.Good, line.Severity));
    }

    // --- Keep-Alive ---

    [Theory]
    [InlineData(ServiceState.Running, StatusSeverity.Good)]
    [InlineData(ServiceState.Stopped, StatusSeverity.Bad)]
    [InlineData(ServiceState.NotInstalled, StatusSeverity.Bad)]
    [InlineData(ServiceState.Unknown, StatusSeverity.Neutral)]
    public void KeepAliveReflectsServiceState(ServiceState state, StatusSeverity expected)
    {
        StatusLine line = Evaluate(TestState.Healthy() with { Service = state }, StatusKind.KeepAlive);

        Assert.Equal(expected, line.Severity);
        Assert.StartsWith("Keep-Alive: ", line.Text);
    }

    // --- Tunnel ---

    [Fact]
    public void TunnelSaysCheckingWhileTheProbeIsInFlight()
    {
        StatusLine line = Evaluate(TestState.Healthy() with { Probe = null }, StatusKind.Tunnel);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains("Checking", line.Text);
    }

    [Fact]
    public void TunnelUnreachableWhileDisconnectedIsNotAnError()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Tunnel = null,
            Probe = ProbeResult.Unreachable,
        };

        StatusLine line = Evaluate(state, StatusKind.Tunnel);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains("not connected", line.Text);
    }

    [Fact]
    public void TunnelUnreachableWhileConnectedIsAnError()
    {
        StatusLine line = Evaluate(
            TestState.Healthy() with { Probe = ProbeResult.Unreachable }, StatusKind.Tunnel);

        Assert.Equal(StatusSeverity.Bad, line.Severity);
        Assert.Contains("No answer from 10.50.0.10", line.Text);
    }

    [Fact]
    public void TunnelReportsLatencyWhenReachable()
    {
        StatusLine line = Evaluate(TestState.Healthy(), StatusKind.Tunnel);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("answered in 22 ms", line.Text);
    }

    [Fact]
    public void TunnelWarnsWhenSubnetOnlyProbeIsOutsideTheTunnelRoutes()
    {
        // Answered over the local connection, which proves nothing about the VPN.
        StatusSnapshot state = TestState.Healthy() with { Config = TestState.Config(probeHost: "192.168.1.1") };

        StatusLine line = Evaluate(state, StatusKind.Tunnel);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("outside your tunnel routes", line.Text);
    }

    [Fact]
    public void TunnelDoesNotWarnAboutAHostNameItCannotRangeCheck()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(probeHost: "fileserver.corp.example"),
        };

        Assert.Equal(StatusSeverity.Good, Evaluate(state, StatusKind.Tunnel).Severity);
    }

    [Fact]
    public void TunnelDoesNotWarnAboutAnOutsideProbeInFullTunnelMode()
    {
        // With a default route everything rides the tunnel, so the routes list is irrelevant.
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(probeHost: "192.168.1.1"),
            Vpn = TestState.Vpn(splitTunneling: false),
            DefaultRouteOnTunnel = true,
        };

        Assert.Equal(StatusSeverity.Good, Evaluate(state, StatusKind.Tunnel).Severity);
    }

    // --- Routing ---

    [Fact]
    public void RoutingReportsAMissingVpnByName()
    {
        StatusLine line = Evaluate(TestState.Healthy() with { Vpn = null }, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains(TestState.VpnName, line.Text);
    }

    [Fact]
    public void RoutingListsTheLiveSubnets()
    {
        StatusLine line = Evaluate(TestState.Healthy(), StatusKind.Routing);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("10.50.0.0/16 only, others not tunneled", line.Text);
    }

    [Fact]
    public void RoutingWarnsWhenSubnetOnlySessionStillCarriesADefaultRoute()
    {
        StatusSnapshot state = TestState.Healthy() with { DefaultRouteOnTunnel = true };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("reconnect to apply", line.Text);
    }

    [Fact]
    public void RoutingWarnsWhenConfiguredSubnetsAreNotLiveYet()
    {
        StatusSnapshot state = TestState.Healthy() with { ConfiguredRoutesLive = false };

        Assert.Equal(StatusSeverity.Warning, Evaluate(state, StatusKind.Routing).Severity);
    }

    [Fact]
    public void RoutingFlagsSubnetOnlyWithNoSubnetsAsBroken()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: true, routes: []),
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Bad, line.Severity);
        Assert.Contains("no subnets are set", line.Text);
    }

    [Fact]
    public void RoutingFlagsAnUnresolvedTunnelHostWhenThereAreNoSubnetsYet()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: true, routes: []),
            UnresolvedTunnelHosts = ["sql.example.com"],
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Bad, line.Severity);
        Assert.Contains("sql.example.com did not resolve", line.Text);
    }

    [Fact]
    public void RoutingWarnsWhenATunnelHostDidNotResolveAlongsideWorkingSubnets()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            UnresolvedTunnelHosts = ["sql.example.com"],
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("sql.example.com did not resolve", line.Text);
    }

    [Fact]
    public void RoutingShowsTheMainSubnetAndAHostCount()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(tunnelHosts: ["sql.example.com", "app.example.com"]),
            Vpn = TestState.Vpn(
                splitTunneling: true,
                routes: ["10.50.0.0/16", "192.0.2.10/32", "192.0.2.11/32"]),
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Equal("Routing: 10.50.0.0/16 + 2 hosts only, others not tunneled", line.Text);
    }

    [Fact]
    public void RoutingUsesASingularHostCount()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(tunnelHosts: ["sql.example.com"]),
            Vpn = TestState.Vpn(splitTunneling: true, routes: ["10.50.0.0/16", "192.0.2.10/32"]),
        };

        Assert.Contains("10.50.0.0/16 + 1 host only", Evaluate(state, StatusKind.Routing).Text);
    }

    [Fact]
    public void RoutingCountsMultipleUnresolvedHostsRatherThanListingThem()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            UnresolvedTunnelHosts = ["sql.example.com", "app.example.com"],
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("2 hosts did not resolve", line.Text);
    }

    [Fact]
    public void RoutingIsGoodInFullTunnelModeWithADefaultRoute()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: false),
            DefaultRouteOnTunnel = true,
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("All traffic goes through the VPN", line.Text);
    }

    [Fact]
    public void RoutingWarnsWhenFullTunnelIsSavedButTheSessionPredatesIt()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: false),
            DefaultRouteOnTunnel = false,
        };

        StatusLine line = Evaluate(state, StatusKind.Routing);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("reconnect to apply", line.Text);
    }

    [Fact]
    public void RoutingDoesNotDemandAReconnectWhileDisconnected()
    {
        // Nothing is live to contradict the saved setting, so this is not a warning.
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: false, connected: false),
            Tunnel = null,
            DefaultRouteOnTunnel = false,
        };

        Assert.Equal(StatusSeverity.Good, Evaluate(state, StatusKind.Routing).Severity);
    }

    // --- External IP ---

    [Fact]
    public void ExternalIpSaysCheckingBeforeAnythingIsKnown()
    {
        StatusSnapshot state = TestState.Healthy() with { PublicIp = null, PublicEgressAdapter = null };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains("Checking", line.Text);
    }

    [Fact]
    public void ExternalIpAdmitsWhenItCannotCheck()
    {
        StatusSnapshot state = TestState.Healthy() with { PublicIp = "", PublicEgressAdapter = null };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains("Could not check", line.Text);
    }

    [Fact]
    public void ExternalIpIsGoodWhenSubnetOnlyTrafficUsesTheLocalConnection()
    {
        StatusLine line = Evaluate(TestState.Healthy(), StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("203.0.113.9 - from your ISP, as intended", line.Text);
    }

    [Fact]
    public void ExternalIpFlagsALeakWhenSubnetOnlyTrafficExitsThroughTheVpn()
    {
        StatusSnapshot state = TestState.Healthy() with { PublicEgressAdapter = TestState.Alias };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Bad, line.Severity);
        Assert.Contains("from the VPN, leaking past Subnet Only", line.Text);
    }

    [Fact]
    public void ExternalIpIsGoodWhenFullTunnelTrafficExitsThroughTheVpn()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: false),
            DefaultRouteOnTunnel = true,
            PublicEgressAdapter = TestState.Alias,
        };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("from the VPN, as intended", line.Text);
    }

    [Fact]
    public void ExternalIpWarnsWhenFullTunnelTrafficStillExitsLocally()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Vpn = TestState.Vpn(splitTunneling: false),
            DefaultRouteOnTunnel = true,
        };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("from your ISP, not the VPN yet", line.Text);
    }

    [Fact]
    public void ExternalIpTrustsAKnownEgressAddressEvenWhenTheAdapterLooksLocal()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(knownEgressIps: ["203.0.113.9"]),
            PublicEgressAdapter = TestState.LocalAdapter,
        };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Bad, line.Severity);
        Assert.Contains("leaking", line.Text);
    }

    [Fact]
    public void ExternalIpFallsBackToTheAdapterNameWhenTheAddressLookupFails()
    {
        StatusSnapshot state = TestState.Healthy() with { PublicIp = "" };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("via Wi-Fi", line.Text);
    }

    [Fact]
    public void ExternalIpStaysNeutralWhileTheVpnIsDisconnected()
    {
        StatusSnapshot state = TestState.Healthy() with { Tunnel = null };

        StatusLine line = Evaluate(state, StatusKind.ExternalIp);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains("203.0.113.9", line.Text);
    }

    // --- Network ---

    [Fact]
    public void NetworkIsGoodWhenPrivateAndRulesArePresent()
    {
        StatusLine line = Evaluate(TestState.Healthy(), StatusKind.Network);

        Assert.Equal(StatusSeverity.Good, line.Severity);
        Assert.Contains("hidden from others", line.Text);
    }

    [Fact]
    public void NetworkFlagsAPublicTunnelAsExposed()
    {
        StatusSnapshot state = TestState.Healthy() with { Tunnel = TestState.Tunnel(NetworkCategory.Public) };

        StatusLine line = Evaluate(state, StatusKind.Network);

        Assert.Equal(StatusSeverity.Bad, line.Severity);
        Assert.Contains("exposed", line.Text);
    }

    [Fact]
    public void NetworkWarnsWhenPrivateButRulesAreMissing()
    {
        StatusSnapshot state = TestState.Healthy() with { FirewallRulesCurrent = false };

        StatusLine line = Evaluate(state, StatusKind.Network);

        Assert.Equal(StatusSeverity.Warning, line.Severity);
        Assert.Contains("file sharing is not blocked", line.Text);
    }

    [Fact]
    public void NetworkIgnoresTheCategoryWhenPrivateEnforcementIsOff()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(setNetworkPrivate: false),
            Tunnel = TestState.Tunnel(NetworkCategory.Public),
        };

        Assert.Equal(StatusSeverity.Good, Evaluate(state, StatusKind.Network).Severity);
    }

    [Fact]
    public void NetworkStaysQuietWhenHardeningIsDisabled()
    {
        StatusSnapshot state = TestState.Healthy() with
        {
            Config = TestState.Config(hardeningEnabled: false),
            FirewallRulesCurrent = false,
        };

        StatusLine line = Evaluate(state, StatusKind.Network);

        Assert.Equal(StatusSeverity.Neutral, line.Severity);
        Assert.Contains("turned off", line.Text);
    }

    [Fact]
    public void NetworkIsNeutralWhileDisconnected()
    {
        StatusSnapshot state = TestState.Healthy() with { Tunnel = null };

        Assert.Equal(StatusSeverity.Neutral, Evaluate(state, StatusKind.Network).Severity);
    }

    // --- Startup ---

    [Theory]
    [InlineData(true, StatusSeverity.Good)]
    [InlineData(false, StatusSeverity.Neutral)]
    public void StartupReflectsTheAutostartEntry(bool enabled, StatusSeverity expected)
    {
        StatusSnapshot state = TestState.Healthy() with { StartupEnabled = enabled };

        Assert.Equal(expected, Evaluate(state, StatusKind.Startup).Severity);
    }

    private static StatusLine Evaluate(StatusSnapshot state, StatusKind kind) =>
        StatusEvaluator.Evaluate(state).Line(kind);
}
