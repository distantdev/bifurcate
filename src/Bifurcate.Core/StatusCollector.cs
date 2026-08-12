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
            routesLive = vpn is not null
                && vpn.Routes.Length > 0
                && _routes.AllRoutesLive(tunnel.InterfaceAlias, vpn.Routes);
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

        return added;
    }

    public IReadOnlyList<VpnConnectionInfo> ListVpnConnections() => _vpn.List();

    public void Dispose()
    {
        _vpn.Dispose();
        _profiles.Dispose();
        _routes.Dispose();
    }
}
