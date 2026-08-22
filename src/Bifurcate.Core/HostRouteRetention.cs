namespace Bifurcate.Core;

public sealed record HostRouteEntry
{
    public string Prefix { get; init; } = "";

    public DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>
/// Extra /32s for a host are kept after DNS moves, then dropped once they have not shown up
/// for <see cref="Duration"/>. That covers client caches without leaving dead routes forever.
/// </summary>
public static class HostRouteRetention
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(24);

    public static Dictionary<string, string[]> Prefixes(
        IReadOnlyDictionary<string, HostRouteEntry[]> ledger,
        DateTimeOffset utcNow,
        TimeSpan? retention = null)
    {
        TimeSpan keep = retention ?? Duration;
        Dictionary<string, string[]> map = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string host, HostRouteEntry[] entries) in ledger)
        {
            string[] fresh =
            [
                .. entries
                    .Where(entry => utcNow - entry.LastSeenUtc <= keep)
                    .Select(entry => entry.Prefix)
            ];

            if (fresh.Length > 0) { map[host] = fresh; }
        }

        return map;
    }

    public static Dictionary<string, string[]> AllPrefixes(
        IReadOnlyDictionary<string, HostRouteEntry[]> ledger)
    {
        Dictionary<string, string[]> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string host, HostRouteEntry[] entries) in ledger)
        {
            map[host] = [.. entries.Select(entry => entry.Prefix)];
        }

        return map;
    }

    public static Dictionary<string, HostRouteEntry[]> Stamp(
        IReadOnlyDictionary<string, string[]> next,
        IReadOnlyDictionary<string, HostRouteEntry[]> previous,
        IReadOnlyList<HostResolution> resolutions,
        DateTimeOffset utcNow)
    {
        Dictionary<string, HostRouteEntry[]> stamped = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string host, string[] prefixes) in next)
        {
            HashSet<string> seenNow = new(
                PrefixesFor(host, resolutions),
                StringComparer.OrdinalIgnoreCase);

            Dictionary<string, DateTimeOffset> prior = new(StringComparer.OrdinalIgnoreCase);
            if (TryGet(previous, host, out HostRouteEntry[] old))
            {
                foreach (HostRouteEntry entry in old) { prior[entry.Prefix] = entry.LastSeenUtc; }
            }

            stamped[host] =
            [
                .. prefixes.Select(prefix => new HostRouteEntry
                {
                    Prefix = prefix,
                    LastSeenUtc = seenNow.Contains(prefix) || !prior.ContainsKey(prefix)
                        ? utcNow
                        : prior[prefix],
                })
            ];
        }

        return stamped;
    }

    private static IReadOnlyList<string> PrefixesFor(string host, IReadOnlyList<HostResolution> resolutions)
    {
        foreach (HostResolution resolution in resolutions)
        {
            if (resolution.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                return resolution.Prefixes;
            }
        }

        return [];
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, HostRouteEntry[]> ledger, string host, out HostRouteEntry[] entries)
    {
        foreach ((string key, HostRouteEntry[] value) in ledger)
        {
            if (key.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                entries = value;
                return true;
            }
        }

        entries = [];
        return false;
    }
}
