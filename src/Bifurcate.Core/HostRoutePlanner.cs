namespace Bifurcate.Core;

/// <summary>One hostname and the /32s it resolved to. An empty list means lookup failed or had no IPv4.</summary>
public sealed record HostResolution(string Host, IReadOnlyList<string> Prefixes);

/// <summary>
/// Which host-derived /32s to add or drop on the VPN profile, without touching user-configured
/// subnets or any other routes the connection already had.
/// </summary>
public sealed record HostRoutePlan(
    IReadOnlyList<string> ToAdd,
    IReadOnlyList<string> ToRemove,
    IReadOnlyDictionary<string, string[]> NextState,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Pure decision for host routes. DNS and CIM stay outside so the add/keep/drop cases are tests
/// rather than something verified against a live VPN.
/// </summary>
public static class HostRoutePlanner
{
    private const int MaxPrefixesPerHost = 8;

    private static readonly StringComparer Text = StringComparer.OrdinalIgnoreCase;

    public static HostRoutePlan Plan(
        IReadOnlyList<string> tunnelRoutes,
        IReadOnlyList<string> tunnelHosts,
        IReadOnlyList<string> profileRoutes,
        IReadOnlyDictionary<string, string[]> previousState,
        IReadOnlyList<HostResolution> resolutions,
        IReadOnlyDictionary<string, string[]>? managedState = null)
    {
        Dictionary<string, string[]> next = new(Text);
        List<string> unresolved = [];

        foreach (string host in tunnelHosts)
        {
            IReadOnlyList<string> resolved = PrefixesFor(host, resolutions);
            if (resolved.Count > 0)
            {
                // Keep recently seen prefixes too. Azure SQL DNS can flip while a client still
                // has the previous A record cached, and dropping it dumps that connection onto Wi-Fi.
                string[] previously = TryGet(previousState, host, out string[] kept) ? kept : [];
                next[host] = Merge(resolved, previously);
                continue;
            }

            if (TryGet(previousState, host, out string[] remembered) && remembered.Length > 0)
            {
                // Last known /32s stay up across a DNS blip so Azure SQL does not fall back to the ISP.
                next[host] = DistinctSorted(remembered);
                continue;
            }

            unresolved.Add(host);
        }

        HashSet<string> wanted = Flatten(next);
        HashSet<string> previouslyManaged = Flatten(managedState ?? previousState);
        HashSet<string> profile = new(profileRoutes, Text);
        HashSet<string> staticRoutes = new(tunnelRoutes, Text);

        string[] toAdd = [.. wanted.Where(prefix => !profile.Contains(prefix)).OrderBy(prefix => prefix, Text)];
        string[] toRemove =
        [
            .. previouslyManaged
                .Where(prefix => !wanted.Contains(prefix) && !staticRoutes.Contains(prefix))
                .OrderBy(prefix => prefix, Text)
        ];

        return new HostRoutePlan(toAdd, toRemove, next, unresolved);
    }

    private static IReadOnlyList<string> PrefixesFor(string host, IReadOnlyList<HostResolution> resolutions)
    {
        foreach (HostResolution resolution in resolutions)
        {
            if (Text.Equals(resolution.Host, host)) { return resolution.Prefixes; }
        }

        return [];
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, string[]> state, string host, out string[] prefixes)
    {
        foreach ((string key, string[] value) in state)
        {
            if (Text.Equals(key, host))
            {
                prefixes = value;
                return true;
            }
        }

        prefixes = [];
        return false;
    }

    private static HashSet<string> Flatten(IReadOnlyDictionary<string, string[]> state)
    {
        HashSet<string> prefixes = new(Text);
        foreach (string[] values in state.Values)
        {
            foreach (string prefix in values) { prefixes.Add(prefix); }
        }

        return prefixes;
    }

    private static string[] Merge(IReadOnlyList<string> resolved, IReadOnlyList<string> previously)
    {
        List<string> combined = [];
        HashSet<string> seen = new(Text);

        foreach (string prefix in resolved.Concat(previously))
        {
            if (!seen.Add(prefix)) { continue; }

            combined.Add(prefix);
            if (combined.Count == MaxPrefixesPerHost) { break; }
        }

        return DistinctSorted(combined);
    }

    private static string[] DistinctSorted(IReadOnlyList<string> prefixes) =>
        [.. prefixes.Distinct(Text).OrderBy(prefix => prefix, Text)];
}
