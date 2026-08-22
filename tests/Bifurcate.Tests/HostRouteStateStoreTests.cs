using Bifurcate.Core;

namespace Bifurcate.Tests;

public class HostRouteStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bifurcate-host-routes-" + Guid.NewGuid());

    public HostRouteStateStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void AMissingFileLoadsAsEmpty()
    {
        HostRouteStateStore store = new(Path.Combine(_directory, "absent.json"));

        Assert.Empty(store.Load());
        Assert.Empty(store.ManagedPrefixes());
    }

    [Fact]
    public void SaveThenLoadPreservesHostPrefixesAndLastSeen()
    {
        string path = Path.Combine(_directory, "host-routes.json");
        HostRouteStateStore store = new(path);
        DateTimeOffset seen = DateTimeOffset.Parse("2026-08-19T22:00:00Z");

        store.Save(new Dictionary<string, HostRouteEntry[]>
        {
            ["sql.example.com"] =
            [
                new HostRouteEntry { Prefix = "192.0.2.10/32", LastSeenUtc = seen },
            ],
        });

        IReadOnlyDictionary<string, HostRouteEntry[]> loaded = new HostRouteStateStore(path).Load();
        Assert.Equal("192.0.2.10/32", loaded["SQL.example.com"][0].Prefix);
        Assert.Equal(seen, loaded["SQL.example.com"][0].LastSeenUtc);
        Assert.Contains("192.0.2.10/32", new HostRouteStateStore(path).ManagedPrefixes());
    }

    [Fact]
    public void LegacyStringArraysLoadAsSeenJustNow()
    {
        string path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, """{ "sql.example.com": [ "192.0.2.10/32" ] }""");
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-19T22:00:00Z");

        HostRouteEntry entry = new HostRouteStateStore(path).Load(now)["sql.example.com"][0];
        Assert.Equal("192.0.2.10/32", entry.Prefix);
        Assert.Equal(now, entry.LastSeenUtc);
    }

    [Fact]
    public void BrokenJsonLoadsAsEmptyRatherThanThrowing()
    {
        string path = Path.Combine(_directory, "broken.json");
        File.WriteAllText(path, "{ nope");

        Assert.Empty(new HostRouteStateStore(path).Load());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
