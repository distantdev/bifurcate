using Bifurcate.Core;

namespace Bifurcate.Tests;

public class HostRouteRetentionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-19T22:00:00Z");

    [Fact]
    public void FreshDropsPrefixesOlderThanADay()
    {
        Dictionary<string, HostRouteEntry[]> ledger = new()
        {
            ["sql.example.com"] =
            [
                new HostRouteEntry { Prefix = "192.0.2.10/32", LastSeenUtc = Now },
                new HostRouteEntry { Prefix = "192.0.2.11/32", LastSeenUtc = Now.AddHours(-25) },
            ],
        };

        Dictionary<string, string[]> fresh = HostRouteRetention.Prefixes(ledger, Now);
        Dictionary<string, string[]> all = HostRouteRetention.AllPrefixes(ledger);

        Assert.Equal(["192.0.2.10/32"], fresh["sql.example.com"]);
        Assert.Equal(["192.0.2.10/32", "192.0.2.11/32"], all["sql.example.com"]);
    }

    [Fact]
    public void StampRefreshesLastSeenOnlyForAddressesDnsReturned()
    {
        Dictionary<string, HostRouteEntry[]> previous = new()
        {
            ["sql.example.com"] =
            [
                new HostRouteEntry { Prefix = "192.0.2.10/32", LastSeenUtc = Now.AddHours(-3) },
            ],
        };
        Dictionary<string, string[]> next = new()
        {
            ["sql.example.com"] = ["192.0.2.10/32", "192.0.2.11/32"],
        };
        HostResolution[] resolutions = [new("sql.example.com", ["192.0.2.11/32"])];

        Dictionary<string, HostRouteEntry[]> stamped =
            HostRouteRetention.Stamp(next, previous, resolutions, Now);

        HostRouteEntry[] entries = stamped["sql.example.com"];
        Assert.Equal(Now.AddHours(-3), entries.Single(e => e.Prefix == "192.0.2.10/32").LastSeenUtc);
        Assert.Equal(Now, entries.Single(e => e.Prefix == "192.0.2.11/32").LastSeenUtc);
    }
}
