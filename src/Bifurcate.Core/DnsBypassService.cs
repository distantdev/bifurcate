using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

public enum DnsBypassAction
{
    None,
    Applied,
    Removed,
    Warning,
}

public sealed record DnsBypassOutcome(DnsBypassAction Action, string Detail)
{
    public static readonly DnsBypassOutcome NoChange = new(DnsBypassAction.None, "");
}

public sealed record NrptRuleInfo(
    string Name,
    string[] Namespaces,
    string[] NameServers,
    string Comment,
    string DisplayName);

/// <summary>
/// Manages Windows Name Resolution Policy Table (NRPT) rules to ensure configured domains bypass
/// the VPN DNS and are resolved using the regular physical network connection's DNS servers.
/// </summary>
public sealed class DnsBypassService : IDisposable
{
    private readonly CimSession _session = Cim.CreateSession();

    public DnsBypassOutcome Sweep(BifurcateConfig config, NetworkProfileInfo? tunnel = null)
    {
        IReadOnlyList<NrptRuleInfo> ownedRules = GetOwnedRules();

        if (config.DnsBypass.Domains.Length == 0)
        {
            if (ownedRules.Count == 0) { return DnsBypassOutcome.NoChange; }

            int removedCount = 0;
            foreach (NrptRuleInfo rule in ownedRules)
            {
                if (RemoveRule(rule.Name)) { removedCount++; }
            }

            return new DnsBypassOutcome(DnsBypassAction.Removed,
                $"removed {removedCount} bypass rule(s) because no domains are configured");
        }

        IReadOnlyList<string> targetDnsServers;
        if (config.DnsBypass.DnsServers.Length > 0)
        {
            targetDnsServers = config.DnsBypass.DnsServers;
        }
        else
        {
            IReadOnlyList<IPAddress> detected = DetectPrimaryLanDnsServers(
                config.VpnConnectionName, tunnel?.InterfaceAlias);
            targetDnsServers = [.. detected.Select(ip => ip.ToString())];
        }

        if (targetDnsServers.Count == 0)
        {
            return new DnsBypassOutcome(DnsBypassAction.Warning,
                "No physical LAN DNS server detected to resolve bypass domains");
        }

        HashSet<string> desiredNamespaces =
        [
            .. config.DnsBypass.Domains.Select(BifurcateConfig.NormalizeBypassNamespace)
        ];

        List<string> changes = [];

        // Remove obsolete rules, rules whose DNS servers are out of date, or duplicates
        HashSet<string> seenNamespaces = [];
        foreach (NrptRuleInfo rule in ownedRules)
        {
            bool ruleMatchesAnyDesired = rule.Namespaces.Any(desiredNamespaces.Contains);
            bool serversMatch = new HashSet<string>(rule.NameServers, StringComparer.OrdinalIgnoreCase).SetEquals(targetDnsServers);
            bool isDuplicate = rule.Namespaces.Any(ns => !seenNamespaces.Add(ns));

            if (!ruleMatchesAnyDesired || !serversMatch || isDuplicate)
            {
                if (RemoveRule(rule.Name))
                {
                    changes.Add($"removed '{string.Join(",", rule.Namespaces)}'");
                }
            }
        }

        // Re-query current owned rules to see what needs to be created
        ownedRules = GetOwnedRules();
        HashSet<string> currentNamespaces =
        [
            .. ownedRules.SelectMany(r => r.Namespaces)
        ];

        foreach (string desiredNs in desiredNamespaces)
        {
            if (!currentNamespaces.Contains(desiredNs, StringComparer.OrdinalIgnoreCase))
            {
                if (AddRule(desiredNs, targetDnsServers))
                {
                    changes.Add($"added '{desiredNs}' -> {string.Join(",", targetDnsServers)}");
                }
            }
        }

        return changes.Count > 0
            ? new DnsBypassOutcome(DnsBypassAction.Applied, string.Join("; ", changes))
            : DnsBypassOutcome.NoChange;
    }

    public IReadOnlyList<string> RemoveOwnedRules()
    {
        List<string> removed = [];
        foreach (NrptRuleInfo rule in GetOwnedRules())
        {
            if (RemoveRule(rule.Name))
            {
                removed.Add(rule.DisplayName.Length > 0 ? rule.DisplayName : rule.Name);
            }
        }
        return removed;
    }

    public DnsBypassStatus ReadStatus(BifurcateConfig config, NetworkProfileInfo? tunnel = null)
    {
        if (config.DnsBypass.Domains.Length == 0)
        {
            return new DnsBypassStatus { Enabled = false, DomainCount = 0 };
        }

        IReadOnlyList<string> targetDnsServers;
        if (config.DnsBypass.DnsServers.Length > 0)
        {
            targetDnsServers = config.DnsBypass.DnsServers;
        }
        else
        {
            IReadOnlyList<IPAddress> detected = DetectPrimaryLanDnsServers(
                config.VpnConnectionName, tunnel?.InterfaceAlias);
            targetDnsServers = [.. detected.Select(ip => ip.ToString())];
        }

        if (targetDnsServers.Count == 0)
        {
            return new DnsBypassStatus
            {
                Enabled = true,
                DomainCount = config.DnsBypass.Domains.Length,
                DnsServers = [],
                RulesApplied = false,
                Warning = "no LAN DNS server detected",
            };
        }

        IReadOnlyList<NrptRuleInfo> ownedRules = GetOwnedRules();
        HashSet<string> desiredNamespaces =
        [
            .. config.DnsBypass.Domains.Select(BifurcateConfig.NormalizeBypassNamespace)
        ];

        bool allApplied = desiredNamespaces.All(desired =>
            ownedRules.Any(rule =>
                rule.Namespaces.Contains(desired, StringComparer.OrdinalIgnoreCase)
                && new HashSet<string>(rule.NameServers, StringComparer.OrdinalIgnoreCase).SetEquals(targetDnsServers)));

        return new DnsBypassStatus
        {
            Enabled = true,
            DomainCount = config.DnsBypass.Domains.Length,
            DnsServers = targetDnsServers,
            RulesApplied = allApplied,
            Warning = allApplied ? null : "Rules not applied",
        };
    }

    public IReadOnlyList<NrptRuleInfo> GetOwnedRules()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig");
            if (key is null) { return []; }

            List<NrptRuleInfo> rules = [];
            foreach (string subKeyName in key.GetSubKeyNames())
            {
                using Microsoft.Win32.RegistryKey? subKey = key.OpenSubKey(subKeyName);
                if (subKey is null) { continue; }

                string comment = subKey.GetValue("Comment") as string ?? "";
                string displayName = subKey.GetValue("DisplayName") as string ?? "";

                if (IsOwned(comment, displayName))
                {
                    string[] namespaces = ParseMultiValue(subKey.GetValue("Name"));
                    string[] nameServers = ParseMultiValue(subKey.GetValue("GenericDNSServers"));
                    rules.Add(new NrptRuleInfo(subKeyName, namespaces, nameServers, comment, displayName));
                }
            }

            return rules;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string[] ParseMultiValue(object? val) => val switch
    {
        string[] arr => arr,
        string s => s.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    public bool AddRule(string normalizedNamespace, IReadOnlyList<string> nameServers)
    {
        string displayName = $"{BifurcateInfo.DnsBypassRulePrefix} {normalizedNamespace}";
        string[] nsArray = [normalizedNamespace];
        string[] serverArray = [.. nameServers];

        try
        {
            CimMethodParametersCollection parameters =
            [
                CimMethodParameter.Create("GpoName", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("DANameServers", (string[]?)null, CimType.StringArray, CimFlags.In),
                CimMethodParameter.Create("DAIPsecRequired", false, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("DAIPsecEncryptionType", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("DAProxyServerName", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("DnsSecEnable", false, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("DnsSecIPsecRequired", false, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("DnsSecIPsecEncryptionType", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("NameServers", serverArray, CimType.StringArray, CimFlags.In),
                CimMethodParameter.Create("NameEncoding", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("Namespace", nsArray, CimType.StringArray, CimFlags.In),
                CimMethodParameter.Create("Server", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("DAProxyType", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("DnsSecValidationRequired", false, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("DAEnable", false, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("IPsecTrustAuthority", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("Comment", BifurcateInfo.DnsBypassComment, CimType.String, CimFlags.In),
                CimMethodParameter.Create("DisplayName", displayName, CimType.String, CimFlags.In),
                CimMethodParameter.Create("PassThru", false, CimType.Boolean, CimFlags.In),
            ];

            CimMethodResult result = _session.InvokeMethod(Cim.DnsNamespace, "PS_DnsClientNrptRule", "Add", parameters);
            if (result.ReturnValue?.Value is 0 or 0u) { return true; }
        }
        catch (CimException)
        {
            // Fall back to PowerShell execution
        }

        return RunPowerShellAdd(normalizedNamespace, serverArray, displayName);
    }

    public bool RemoveRule(string ruleName)
    {
        try
        {
            CimMethodParametersCollection parameters =
            [
                CimMethodParameter.Create("GpoName", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("Name", ruleName, CimType.String, CimFlags.In),
                CimMethodParameter.Create("Server", (string?)null, CimType.String, CimFlags.In),
                CimMethodParameter.Create("PassThru", false, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("Force", true, CimType.Boolean, CimFlags.In),
            ];

            CimMethodResult result = _session.InvokeMethod(Cim.DnsNamespace, "PS_DnsClientNrptRule", "Remove", parameters);
            if (result.ReturnValue?.Value is 0 or 0u) { return true; }
        }
        catch (CimException)
        {
            // Fall back to PowerShell execution
        }

        return RunPowerShellRemove(ruleName);
    }

    public static IReadOnlyList<IPAddress> DetectPrimaryLanDnsServers(
        string? vpnConnectionName, string? tunnelInterfaceAlias)
    {
        NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();

        List<NetworkInterface> candidates = adapters
            .Where(a => a.OperationalStatus == OperationalStatus.Up)
            .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                     && a.NetworkInterfaceType != NetworkInterfaceType.Ppp)
            .Where(a => string.IsNullOrWhiteSpace(vpnConnectionName)
                     || !a.Name.Equals(vpnConnectionName, StringComparison.OrdinalIgnoreCase))
            .Where(a => string.IsNullOrWhiteSpace(tunnelInterfaceAlias)
                     || !a.Name.Equals(tunnelInterfaceAlias, StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<NetworkInterface> withGateway = candidates
            .Where(a => a.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .ToList();

        List<NetworkInterface> searchPool = withGateway.Count > 0 ? withGateway : candidates;

        List<NetworkInterface> sorted = searchPool
            .OrderBy(a =>
            {
                try { return a.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue; }
                catch { return int.MaxValue; }
            })
            .ToList();

        foreach (NetworkInterface adapter in sorted)
        {
            List<IPAddress> dnsIps = adapter.GetIPProperties().DnsAddresses
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .ToList();

            if (dnsIps.Count > 0)
            {
                return dnsIps;
            }
        }

        return [];
    }

    private static bool IsOwned(string comment, string displayName) =>
        comment.Equals(BifurcateInfo.DnsBypassComment, StringComparison.OrdinalIgnoreCase)
        || displayName.StartsWith(BifurcateInfo.DnsBypassRulePrefix, StringComparison.OrdinalIgnoreCase);

    private static bool RunPowerShellAdd(string ns, string[] servers, string displayName)
    {
        string serverArgs = string.Join("','", servers);
        string script = $"Add-DnsClientNrptRule -Namespace @('{ns}') -NameServers @('{serverArgs}') -Comment '{BifurcateInfo.DnsBypassComment}' -DisplayName '{displayName}'";
        return RunPowerShell(script);
    }

    private static bool RunPowerShellRemove(string ruleName)
    {
        string script = $"Remove-DnsClientNrptRule -Name '{ruleName}' -Force";
        return RunPowerShell(script);
    }

    private static bool RunPowerShell(string script)
    {
        try
        {
            ProcessStartInfo psi = new("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(10000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _session.Dispose();
}
