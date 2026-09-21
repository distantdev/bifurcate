using Bifurcate.Core;

namespace Bifurcate.Service;

/// <summary>
/// Prints everything the tool can see about the current setup and exits. Turns "it says the wrong
/// thing" into a single command whose output can be read or pasted somewhere.
/// </summary>
internal static class Diagnostics
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"{BifurcateInfo.ProductName} check");
        Console.WriteLine($"  config file   : {BifurcateInfo.ConfigPath}");

        ConfigLoadResult load = ConfigStore.Load();
        if (load.Config is null)
        {
            Console.WriteLine(load.Missing
                ? "  config        : missing. Run the tray app to set it up."
                : "  config        : unreadable.");
            foreach (string error in load.Errors) { Console.WriteLine($"                  {error}"); }
            return 1;
        }

        if (load.Errors.Count > 0)
        {
            Console.WriteLine("  config        : has problems, nothing will be enforced:");
            foreach (string error in load.Errors) { Console.WriteLine($"                  {error}"); }
        }
        else
        {
            Console.WriteLine("  config        : valid");
        }

        BifurcateConfig config = load.Config;
        Console.WriteLine($"  vpn name      : {config.VpnConnectionName}");
        Console.WriteLine($"  tunnel routes : {Join(config.TunnelRoutes)}");
        Console.WriteLine($"  tunnel hosts  : {Join(config.TunnelHosts)}");
        Console.WriteLine($"  dns bypass    : {Join(config.DnsBypass.Domains)}");
        Console.WriteLine($"  probe         : {config.Probe.Type} {config.Probe.Host}" +
                          $"{(config.Probe.Type == ProbeKind.Tcp ? ":" + config.Probe.Port : "")}" +
                          $" ({config.ClassifyProbeHost()} the tunnel routes)");
        Console.WriteLine($"  service       : {ServiceStateReader.Read()}");
        Console.WriteLine();

        ProbeResult probe = await TunnelProbe.RunAsync(config.Probe, cancellationToken).ConfigureAwait(false);
        using PublicIpProbe publicIpProbe = new();
        string publicIp = await publicIpProbe.GetAsync(config.PublicIpUrl, cancellationToken)
            .ConfigureAwait(false) ?? "";

        using StatusCollector collector = new();
        StatusSnapshot snapshot = collector.Collect(config, probe, publicIp);

        Console.WriteLine("observed state");
        Console.WriteLine($"  vpn profile      : {Describe(snapshot.Vpn)}");
        Console.WriteLine($"  tunnel adapter   : {Describe(snapshot.Tunnel)}");
        Console.WriteLine($"  default route    : {snapshot.DefaultRouteOnTunnel}");
        Console.WriteLine($"  routes live      : {snapshot.ConfiguredRoutesLive}");
        Console.WriteLine($"  firewall rules   : {(snapshot.FirewallRulesCurrent ? "current" : "missing or wrong")}");
        Console.WriteLine($"  dns bypass       : enabled={snapshot.DnsBypass.Enabled} rulesApplied={snapshot.DnsBypass.RulesApplied} servers=[{string.Join(", ", snapshot.DnsBypass.DnsServers)}]");
        Console.WriteLine($"  public egress    : {snapshot.PublicEgressAdapter ?? "unknown"}");
        Console.WriteLine($"  public address   : {(publicIp.Length == 0 ? "unknown" : publicIp)}");
        Console.WriteLine($"  probe            : reachable={probe.Reachable} {probe.LatencyMs}ms");
        Console.WriteLine($"  autostart (tray) : {snapshot.StartupEnabled}");
        Console.WriteLine();

        Console.WriteLine("dashboard");
        foreach (StatusLine line in StatusEvaluator.Evaluate(snapshot))
        {
            Console.WriteLine($"  [{line.Severity,-7}] {line.Text}");
        }

        return 0;
    }

    private static string Join(string[] values) => values.Length == 0 ? "(none)" : string.Join(", ", values);

    private static string Describe(VpnConnectionInfo? vpn) => vpn is null
        ? "not found"
        : $"{vpn.Name} status={vpn.ConnectionStatus} splitTunneling={vpn.SplitTunneling} " +
          $"routes=[{Join(vpn.Routes)}] type={vpn.TunnelType}";

    private static string Describe(NetworkProfileInfo? tunnel) => tunnel is null
        ? "not connected"
        : $"'{tunnel.InterfaceAlias}' category={tunnel.Category} index={tunnel.InterfaceIndex}";
}
