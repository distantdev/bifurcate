using Bifurcate.Core;

namespace Bifurcate.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bifurcate-tests-" + Guid.NewGuid());

    private string PathFor(string name) => Path.Combine(_directory, name);

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void AMissingFileIsReportedAsFirstRunRatherThanAnError()
    {
        ConfigLoadResult result = ConfigStore.Load(PathFor("absent.json"));

        Assert.True(result.Missing);
        Assert.False(result.Ok);
        Assert.Null(result.Config);
    }

    [Fact]
    public void MalformedJsonIsReportedWithoutThrowing()
    {
        string path = PathFor("broken.json");
        File.WriteAllText(path, "{ this is not json ");

        ConfigLoadResult result = ConfigStore.Load(path);

        Assert.False(result.Missing);
        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidationFailuresSurfaceOnLoad()
    {
        string path = PathFor("invalid.json");
        File.WriteAllText(path, """{ "vpnConnectionName": "", "probe": { "host": "" } }""");

        ConfigLoadResult result = ConfigStore.Load(path);

        Assert.False(result.Ok);
        Assert.NotNull(result.Config);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void SaveThenLoadPreservesEveryField()
    {
        string path = PathFor("round-trip.json");
        BifurcateConfig original = new()
        {
            VpnConnectionName = "CorpVpn",
            TunnelRoutes = ["10.50.0.0/16", "172.16.0.0/12"],
            TunnelHosts = ["sql.example.com"],
            Probe = new ProbeConfig { Type = ProbeKind.Tcp, Host = "10.50.0.24", Port = 1433, TimeoutMs = 2500 },
            PublicIpUrl = "https://example.com/ip",
            KnownVpnEgressIps = ["203.0.113.9"],
            SweepIntervalSeconds = 120,
            Hardening = new HardeningConfig
            {
                Enabled = true,
                SetNetworkPrivate = false,
                BlockInboundTcpPorts = [445],
                BlockInboundUdpPorts = [137, 138],
            },
        };

        ConfigStore.Save(original, path);
        ConfigLoadResult result = ConfigStore.Load(path);

        Assert.True(result.Ok);
        BifurcateConfig loaded = result.Config!;
        Assert.Equal(original.VpnConnectionName, loaded.VpnConnectionName);
        Assert.Equal(original.TunnelRoutes, loaded.TunnelRoutes);
        Assert.Equal(original.TunnelHosts, loaded.TunnelHosts);
        Assert.Equal(ProbeKind.Tcp, loaded.Probe.Type);
        Assert.Equal(1433, loaded.Probe.Port);
        Assert.Equal(2500, loaded.Probe.TimeoutMs);
        Assert.Equal(original.PublicIpUrl, loaded.PublicIpUrl);
        Assert.Equal(original.KnownVpnEgressIps, loaded.KnownVpnEgressIps);
        Assert.Equal(120, loaded.SweepIntervalSeconds);
        Assert.False(loaded.Hardening.SetNetworkPrivate);
        Assert.Equal(original.Hardening.BlockInboundTcpPorts, loaded.Hardening.BlockInboundTcpPorts);
    }

    [Fact]
    public void ProbeKindIsStoredAsAReadableNameNotANumber()
    {
        string json = ConfigStore.Serialize(BifurcateConfig.CreateSample() with
        {
            Probe = new ProbeConfig { Type = ProbeKind.Tcp, Host = "10.0.0.1", Port = 443 },
        });

        Assert.Contains("\"Tcp\"", json);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreToleratedSoTheSampleFileCanBeAnnotated()
    {
        string path = PathFor("annotated.json");
        File.WriteAllText(path, """
            {
              // which VPN to manage
              "vpnConnectionName": "CorpVpn",
              "tunnelRoutes": [ "10.50.0.0/16", ],
              "probe": { "type": "Icmp", "host": "10.50.0.10" },
            }
            """);

        ConfigLoadResult result = ConfigStore.Load(path);

        Assert.True(result.Ok);
        Assert.Equal("CorpVpn", result.Config!.VpnConnectionName);
    }

    [Fact]
    public void UnknownKeysAreIgnoredSoAnOlderBuildStillStarts()
    {
        string path = PathFor("future.json");
        File.WriteAllText(path, """
            {
              "vpnConnectionName": "CorpVpn",
              "tunnelRoutes": [ "10.50.0.0/16" ],
              "probe": { "type": "Icmp", "host": "10.50.0.10" },
              "somethingAddedLater": { "nested": true }
            }
            """);

        Assert.True(ConfigStore.Load(path).Ok);
    }

    [Fact]
    public void SameAsComparesByValueBecauseRecordEqualityWouldCompareArraysByReference()
    {
        BifurcateConfig left = BifurcateConfig.CreateSample();
        BifurcateConfig right = BifurcateConfig.CreateSample();

        // Proof the workaround is needed: identical configs are not equal as records.
        Assert.NotEqual(left, right);
        Assert.True(ConfigStore.SameAs(left, right));
    }

    [Fact]
    public void SameAsDetectsARealChange()
    {
        BifurcateConfig left = BifurcateConfig.CreateSample();
        BifurcateConfig right = left with { TunnelRoutes = ["10.99.0.0/16"] };

        Assert.False(ConfigStore.SameAs(left, right));
    }

    [Fact]
    public void SameAsHandlesNulls()
    {
        Assert.True(ConfigStore.SameAs(null, null));
        Assert.False(ConfigStore.SameAs(BifurcateConfig.CreateSample(), null));
        Assert.False(ConfigStore.SameAs(null, BifurcateConfig.CreateSample()));
    }

    [Fact]
    public void SaveCreatesTheDirectoryAndLeavesNoTempFileBehind()
    {
        string path = Path.Combine(_directory, "nested", "deeper", "config.json");

        ConfigStore.Save(BifurcateConfig.CreateSample(), path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory left behind is not worth failing a test run */ }
    }
}
