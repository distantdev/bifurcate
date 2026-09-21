using Bifurcate.Core;

namespace Bifurcate.Tests;

public class BifurcateConfigTests
{
    [Fact]
    public void TheShippedSampleIsValid()
    {
        Assert.Empty(BifurcateConfig.CreateSample().Validate());
    }

    [Fact]
    public void AMissingVpnNameIsReported()
    {
        BifurcateConfig config = Valid() with { VpnConnectionName = "  " };

        Assert.Contains(config.Validate(), error => error.Contains("vpnConnectionName"));
    }

    [Theory]
    [InlineData("10.0.0.0")]        // no prefix length
    [InlineData("10.0.0.0/33")]     // out of range for IPv4
    [InlineData("not-an-address/8")]
    [InlineData("10.0.0.0/abc")]
    public void ABadTunnelRouteIsReported(string route)
    {
        BifurcateConfig config = Valid() with { TunnelRoutes = [route] };

        Assert.Contains(config.Validate(), error => error.Contains("tunnelRoutes"));
    }

    [Fact]
    public void ABadTunnelHostIsReported()
    {
        BifurcateConfig config = Valid() with { TunnelHosts = ["not a host"] };

        Assert.Contains(config.Validate(), error => error.Contains("tunnelHosts"));
    }

    [Fact]
    public void ACidrInTunnelHostsIsRejectedSoItGoesInTunnelRoutesInstead()
    {
        BifurcateConfig config = Valid() with { TunnelHosts = ["10.0.0.0/16"] };

        Assert.Contains(config.Validate(), error => error.Contains("tunnelRoutes"));
    }

    [Fact]
    public void AnIPv6TunnelHostIsRejected()
    {
        BifurcateConfig config = Valid() with { TunnelHosts = ["2001:db8::1"] };

        Assert.Contains(config.Validate(), error => error.Contains("IPv6"));
    }

    [Fact]
    public void AHostnameAndDottedIPv4AreValidTunnelHosts()
    {
        BifurcateConfig config = Valid() with
        {
            TunnelHosts = ["tspan-prod.database.windows.net", "192.0.2.10"],
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void AnEmptyProbeHostIsReported()
    {
        BifurcateConfig config = Valid() with { Probe = new ProbeConfig { Host = "" } };

        Assert.Contains(config.Validate(), error => error.Contains("probe.host"));
    }

    [Fact]
    public void TcpProbesRequireAPort()
    {
        BifurcateConfig config = Valid() with
        {
            Probe = new ProbeConfig { Type = ProbeKind.Tcp, Host = "10.0.0.10", Port = 0 },
        };

        Assert.Contains(config.Validate(), error => error.Contains("probe.port"));
    }

    [Fact]
    public void IcmpProbesDoNotRequireAPort()
    {
        BifurcateConfig config = Valid() with
        {
            Probe = new ProbeConfig { Type = ProbeKind.Icmp, Host = "10.0.0.10", Port = 0 },
        };

        Assert.Empty(config.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(60000)]
    public void AnUnreasonableProbeTimeoutIsReported(int timeoutMs)
    {
        BifurcateConfig config = Valid() with
        {
            Probe = new ProbeConfig { Host = "10.0.0.10", TimeoutMs = timeoutMs },
        };

        Assert.Contains(config.Validate(), error => error.Contains("probe.timeoutMs"));
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    public void ABadPublicIpUrlIsReported(string url)
    {
        BifurcateConfig config = Valid() with { PublicIpUrl = url };

        Assert.Contains(config.Validate(), error => error.Contains("publicIpUrl"));
    }

    [Fact]
    public void ABlankPublicIpUrlIsAllowedBecauseItDisablesTheLookup()
    {
        Assert.Empty((Valid() with { PublicIpUrl = "" }).Validate());
    }

    [Fact]
    public void ABadKnownEgressAddressIsReported()
    {
        BifurcateConfig config = Valid() with { KnownVpnEgressIps = ["nonsense"] };

        Assert.Contains(config.Validate(), error => error.Contains("knownVpnEgressIps"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(4000)]
    public void AnUnreasonableSweepIntervalIsReported(int seconds)
    {
        BifurcateConfig config = Valid() with { SweepIntervalSeconds = seconds };

        Assert.Contains(config.Validate(), error => error.Contains("sweepIntervalSeconds"));
    }

    [Fact]
    public void AnOutOfRangeHardeningPortIsReported()
    {
        BifurcateConfig config = Valid() with
        {
            Hardening = new HardeningConfig { BlockInboundTcpPorts = [70000] },
        };

        Assert.Contains(config.Validate(), error => error.Contains("70000"));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnceRatherThanOneAtATime()
    {
        BifurcateConfig config = new()
        {
            VpnConnectionName = "",
            TunnelRoutes = ["garbage"],
            Probe = new ProbeConfig { Host = "" },
            SweepIntervalSeconds = 1,
        };

        Assert.True(config.Validate().Count >= 4);
    }

    [Theory]
    [InlineData("10.50.0.10", ProbeHostRouting.Inside)]
    [InlineData("10.50.255.254", ProbeHostRouting.Inside)]
    [InlineData("10.51.0.1", ProbeHostRouting.Outside)]
    [InlineData("192.168.1.1", ProbeHostRouting.Outside)]
    [InlineData("fileserver.corp.example", ProbeHostRouting.Unknown)]
    public void ProbeHostIsClassifiedAgainstTheTunnelRoutes(string host, ProbeHostRouting expected)
    {
        BifurcateConfig config = Valid() with
        {
            TunnelRoutes = ["10.50.0.0/16"],
            Probe = new ProbeConfig { Host = host },
        };

        Assert.Equal(expected, config.ClassifyProbeHost());
    }

    [Fact]
    public void ProbeHostWithNoRoutesConfiguredIsOutside()
    {
        BifurcateConfig config = Valid() with
        {
            TunnelRoutes = [],
            Probe = new ProbeConfig { Host = "10.50.0.10" },
        };

        Assert.Equal(ProbeHostRouting.Outside, config.ClassifyProbeHost());
    }

    [Fact]
    public void AProbeHostNameOnTheTunnelHostListIsInside()
    {
        BifurcateConfig config = Valid() with
        {
            TunnelHosts = ["sql.example.com"],
            Probe = new ProbeConfig { Host = "SQL.example.com" },
        };

        Assert.Equal(ProbeHostRouting.Inside, config.ClassifyProbeHost());
    }

    [Fact]
    public void ValidDnsBypassDomainsAndServersPassValidation()
    {
        BifurcateConfig config = Valid() with
        {
            DnsBypass = new DnsBypassConfig
            {
                Domains = ["home.arpa", ".local", "my-nas.lan"],
                DnsServers = ["192.168.1.1", "10.0.0.1"],
            },
        };

        Assert.Empty(config.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.0/24")]
    [InlineData("bad domain with spaces")]
    public void BadDnsBypassDomainIsReported(string domain)
    {
        BifurcateConfig config = Valid() with
        {
            DnsBypass = new DnsBypassConfig { Domains = [domain] },
        };

        Assert.Contains(config.Validate(), error => error.Contains("dnsBypass.domains"));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    [InlineData("2001:db8::1")]
    public void BadDnsBypassServerIsReported(string ip)
    {
        BifurcateConfig config = Valid() with
        {
            DnsBypass = new DnsBypassConfig { DnsServers = [ip] },
        };

        Assert.Contains(config.Validate(), error => error.Contains("dnsBypass.dnsServers"));
    }

    [Theory]
    [InlineData("local", ".local")]
    [InlineData(".local", ".local")]
    [InlineData("home.arpa", ".home.arpa")]
    [InlineData(".HOME.ARPA", ".home.arpa")]
    [InlineData("  .MyNas.Lan  ", ".mynas.lan")]
    public void NormalizeBypassNamespacePrependsDotAndLowercases(string input, string expected)
    {
        Assert.Equal(expected, BifurcateConfig.NormalizeBypassNamespace(input));
    }

    private static BifurcateConfig Valid() => new()
    {
        VpnConnectionName = "CorpVpn",
        TunnelRoutes = ["10.50.0.0/16"],
        Probe = new ProbeConfig { Type = ProbeKind.Icmp, Host = "10.50.0.10", TimeoutMs = 3000 },
    };
}
