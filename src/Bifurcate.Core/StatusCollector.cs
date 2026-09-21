using System.Net.Sockets;
using System.Net;
using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

/// <summary>
/// Gathers the machine state the dashboard needs. Everything here is readable by a standard user.
/// Probe results are passed in rather than fetched, because those are slow and the caller runs
/// them off the UI thread.
/// </summary>
public sealed class StatusCollector : IDisposable
{
    private readonly VpnProfileService _vpn = new();
    private readonly NetworkProfileService _profiles = new();
    private readonly RoutingInspector _routes = new();
    private readonly FirewallService _firewall = new();
    private readonly HostRouteStateStore _hostRoutes = new();
    private readonly DnsBypassService _dnsBypass = new();

    public StatusSnapshot Collect(BifurcateConfig config, ProbeResult? probe, string? publicIp)
    {
        VpnConnectionInfo? vpn = _vpn.Get(config.VpnConnectionName);
        NetworkProfileInfo? tunnel = _profiles.FindTunnel(config.VpnConnectionName);

        bool defaultRoute = false;
        bool routesLive = false;
        bool rulesCurrent = false;

        if (tunnel is not null)
        {
            defaultRoute = _routes.HasDefaultRoute(tunnel.InterfaceAlias);
            // Only the configured subnets have to be live. Host /32s are written while the
            // session is up, and they can take a moment to appear; that is not a reconnect.
            routesLive = vpn is not null
                && vpn.Routes.Length > 0
                && (config.TunnelRoutes.Length == 0
                    || _routes.AllRoutesLive(tunnel.InterfaceAlias, config.TunnelRoutes));
            rulesCurrent = _firewall.RulesAreCurrent(tunnel.InterfaceAlias, config.Hardening);
        }

        return new StatusSnapshot
        {
            Config = config,
            Service = ServiceStateReader.Read(),
            Vpn = vpn,
            Tunnel = tunnel,
            DefaultRouteOnTunnel = defaultRoute,
            ConfiguredRoutesLive = routesLive,
            FirewallRulesCurrent = rulesCurrent,
            PublicEgressAdapter = RoutingInspector.PublicEgressInterfaceAlias(),
            Probe = probe,
            PublicIp = publicIp,
            DnsBypass = _dnsBypass.ReadStatus(config, tunnel),
            StartupEnabled = StartupManager.IsEnabled(),
        };
    }

    /// <summary>
    /// Switches routing mode: the split-tunneling flag, the routes it needs, in one place. The
    /// change lands on the saved profile and applies when the connection is next dialed.
    /// </summary>
    public IReadOnlyList<string> SetRoutingMode(BifurcateConfig config, bool subnetOnly)
    {
        _vpn.SetSplitTunneling(config.VpnConnectionName, subnetOnly);

        if (!subnetOnly) { return []; }

        VpnConnectionInfo? current = _vpn.Get(config.VpnConnectionName);
        List<string> added = [];

        foreach (string prefix in config.TunnelRoutes)
        {
            if (current is not null && current.Routes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _vpn.AddRoute(config.VpnConnectionName, prefix);
            added.Add(prefix);
        }

        HostRouteSyncResult hosts = SyncTunnelHosts(config);
        added.AddRange(hosts.Added);

        return added;
    }

    /// <summary>
    /// Resolves <see cref="BifurcateConfig.TunnelHosts"/> and writes /32s onto the VPN profile.
    /// Stale host routes from the last sync are removed; user-configured subnets are left alone.
    /// Add-VpnConnectionRoute also updates the live table when the tunnel is up, so an IP change
    /// does not wait for a reconnect.
    /// </summary>
    public HostRouteSyncResult SyncTunnelHosts(BifurcateConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.VpnConnectionName)) { return HostRouteSyncResult.None; }

        VpnConnectionInfo? current = _vpn.Get(config.VpnConnectionName);
        if (current is null) { return HostRouteSyncResult.None; }

        IReadOnlyDictionary<string, HostRouteEntry[]> ledger = _hostRoutes.Load();
        if (config.TunnelHosts.Length == 0 && ledger.Count == 0) { return HostRouteSyncResult.None; }

        List<HostResolution> resolutions = [];
        IReadOnlyList<IPAddress> dnsServers = DnsServersFor(config);
        foreach (string host in config.TunnelHosts)
        {
            resolutions.Add(ResolveHost(host, dnsServers));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Dictionary<string, string[]> fresh = HostRouteRetention.Prefixes(ledger, now);
        Dictionary<string, string[]> managed = HostRouteRetention.AllPrefixes(ledger);

        HostRoutePlan plan = HostRoutePlanner.Plan(
            config.TunnelRoutes, config.TunnelHosts, current.Routes, fresh, resolutions, managed);

        return ApplyHostRoutePlan(config, plan, ledger, resolutions, now);
    }

    /// <summary>
    /// Drops remembered extra /32s and leaves only what DNS returns right now. Subnets are untouched.
    /// </summary>
    public HostRouteSyncResult ForgetRememberedHostRoutes(BifurcateConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.VpnConnectionName)) { return HostRouteSyncResult.None; }

        VpnConnectionInfo? current = _vpn.Get(config.VpnConnectionName);
        if (current is null) { return HostRouteSyncResult.None; }

        IReadOnlyDictionary<string, HostRouteEntry[]> ledger = _hostRoutes.Load();
        List<HostResolution> resolutions = [];
        IReadOnlyList<IPAddress> dnsServers = DnsServersFor(config);
        foreach (string host in config.TunnelHosts)
        {
            resolutions.Add(ResolveHost(host, dnsServers));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        HostRoutePlan plan = HostRoutePlanner.Plan(
            config.TunnelRoutes,
            config.TunnelHosts,
            current.Routes,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            resolutions,
            HostRouteRetention.AllPrefixes(ledger));

        return ApplyHostRoutePlan(config, plan, ledger, resolutions, now);
    }

    private HostRouteSyncResult ApplyHostRoutePlan(
        BifurcateConfig config,
        HostRoutePlan plan,
        IReadOnlyDictionary<string, HostRouteEntry[]> ledger,
        IReadOnlyList<HostResolution> resolutions,
        DateTimeOffset utcNow)
    {
        List<string> added = [];
        foreach (string prefix in plan.ToAdd)
        {
            if (TryChangeRoute(() => _vpn.AddRoute(config.VpnConnectionName, prefix)))
            {
                added.Add(prefix);
            }
        }

        List<string> removed = [];
        foreach (string prefix in plan.ToRemove)
        {
            if (TryChangeRoute(() => _vpn.RemoveRoute(config.VpnConnectionName, prefix)))
            {
                removed.Add(prefix);
            }
        }

        try
        {
            _hostRoutes.Save(HostRouteRetention.Stamp(plan.NextState, ledger, resolutions, utcNow));
        }
        catch (IOException)
        {
            // Next sweep retries. Losing the ledger can leave a stale /32, which is extra VPN
            // traffic rather than a leak onto the ISP.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the profile routes still changed; the ledger is best-effort.
        }

        return new HostRouteSyncResult(added, removed, plan.Unresolved);
    }

    private static HostResolution ResolveHost(string host, IReadOnlyList<IPAddress> dnsServers)
    {
        try
        {
            string[] prefixes =
            [
                .. HostResolver.ResolveIPv4(host, dnsServers).Select(NetworkPrefix.IPv4HostRoute)
            ];
            return new HostResolution(host, prefixes);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new HostResolution(host, []);
        }
    }

    private IReadOnlyList<IPAddress> DnsServersFor(BifurcateConfig config)
    {
        NetworkProfileInfo? tunnel = _profiles.FindTunnel(config.VpnConnectionName);
        return tunnel is null ? [] : HostResolver.IPv4DnsServers(tunnel.InterfaceAlias);
    }

    private static bool TryChangeRoute(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (CimException)
        {
            return false;
        }
    }

    public IReadOnlyList<VpnConnectionInfo> ListVpnConnections() => _vpn.List();

    public void Dispose()
    {
        _vpn.Dispose();
        _profiles.Dispose();
        _routes.Dispose();
        _dnsBypass.Dispose();
    }
}
