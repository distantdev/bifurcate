using System.Runtime.InteropServices;

namespace Bifurcate.Core;

/// <summary>
/// The inbound block rules, scoped to a single adapter so the LAN, Wi-Fi, and any other VPN keep
/// working untouched. Reading needs no rights, writing needs administrator.
/// </summary>
/// <remarks>
/// Uses the INetFwPolicy2 COM API rather than CIM. Creating a rule through MSFT_NetFirewallRule
/// means creating several associated filter instances by hand, while COM sets ports and interface
/// scope as plain properties on one object.
/// </remarks>
public sealed class FirewallService
{
    private const int DirectionInbound = 1;   // NET_FW_RULE_DIR_IN
    private const int ActionBlock = 0;        // NET_FW_ACTION_BLOCK
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int AllProfiles = 0x7FFFFFFF; // NET_FW_PROFILE2_ALL

    public bool RulesAreCurrent(string interfaceAlias, HardeningConfig hardening)
    {
        dynamic policy = CreatePolicy();

        return IsCurrent(policy, BifurcateInfo.SmbRuleName, ProtocolTcp,
                   hardening.BlockInboundTcpPorts, interfaceAlias)
            && IsCurrent(policy, BifurcateInfo.DiscoveryRuleName, ProtocolUdp,
                   hardening.BlockInboundUdpPorts, interfaceAlias);
    }

    public void EnsureRules(string interfaceAlias, HardeningConfig hardening)
    {
        dynamic policy = CreatePolicy();

        // Replace rather than patch. An adapter can be renamed or recreated, which leaves a rule
        // that exists but no longer covers anything.
        RemoveByName(policy, BifurcateInfo.SmbRuleName);
        RemoveByName(policy, BifurcateInfo.DiscoveryRuleName);

        AddRule(policy, BifurcateInfo.SmbRuleName, ProtocolTcp,
            hardening.BlockInboundTcpPorts, interfaceAlias);
        AddRule(policy, BifurcateInfo.DiscoveryRuleName, ProtocolUdp,
            hardening.BlockInboundUdpPorts, interfaceAlias);
    }

    public bool AnyRulesPresent()
    {
        List<string> owned = OwnedRuleNames(CreatePolicy());
        return owned.Count > 0;
    }

    /// <summary>Removes every rule this tool owns and returns their names.</summary>
    public IReadOnlyList<string> RemoveOwnedRules()
    {
        dynamic policy = CreatePolicy();
        List<string> names = OwnedRuleNames(policy);
        foreach (string name in names) { RemoveByName(policy, name); }
        return names;
    }

    private static dynamic CreatePolicy()
    {
        Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException(
                "The Windows Firewall COM API (HNetCfg.FwPolicy2) is unavailable.");

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create the Windows Firewall policy object.");
    }

    private static void AddRule(dynamic policy, string name, int protocol, int[] ports, string interfaceAlias)
    {
        Type type = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new InvalidOperationException("The firewall rule COM class is unavailable.");

        dynamic rule = Activator.CreateInstance(type)!;
        rule.Name = name;
        rule.Description = $"Created by {BifurcateInfo.ProductName}. Removed automatically when the tunnel goes away.";
        rule.Direction = DirectionInbound;
        rule.Action = ActionBlock;
        rule.Protocol = protocol;
        rule.LocalPorts = FormatPorts(ports);
        rule.Interfaces = new object[] { interfaceAlias };
        rule.Profiles = AllProfiles;
        rule.Enabled = true;

        policy.Rules.Add(rule);
    }

    private static bool IsCurrent(
        dynamic policy, string name, int protocol, int[] ports, string interfaceAlias)
    {
        dynamic? rule = FindByName(policy, name);
        if (rule is null) { return false; }

        try
        {
            if (!(bool)rule.Enabled) { return false; }
            if ((int)rule.Direction != DirectionInbound) { return false; }
            if ((int)rule.Action != ActionBlock) { return false; }
            if ((int)rule.Protocol != protocol) { return false; }
            if (NormalizePorts((string?)rule.LocalPorts) != NormalizePorts(FormatPorts(ports))) { return false; }

            // Held in a typed local first. Calling the extension method straight off a dynamic
            // expression fails at run time, because the binder cannot see extension methods.
            List<string> boundTo = BoundInterfaces(rule);
            return boundTo.Contains(interfaceAlias, StringComparer.OrdinalIgnoreCase);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static List<string> BoundInterfaces(dynamic rule)
    {
        try
        {
            // Unscoped rules report an empty array on some builds and null on others.
            return rule.Interfaces is object[] values
                ? [.. values.Select(v => v?.ToString() ?? "").Where(v => v.Length > 0)]
                : [];
        }
        catch (COMException)
        {
            return [];
        }
    }

    private static dynamic? FindByName(dynamic policy, string name)
    {
        try { return policy.Rules.Item(name); }
        catch (FileNotFoundException) { return null; }
        catch (COMException) { return null; }
    }

    private static List<string> OwnedRuleNames(dynamic policy)
    {
        List<string> names = [];

        foreach (dynamic rule in policy.Rules)
        {
            string ruleName;
            try { ruleName = (string?)rule.Name ?? ""; }
            catch (COMException) { continue; }

            if (ruleName.StartsWith(BifurcateInfo.RulePrefix, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(ruleName);
            }
        }

        return names;
    }

    private static void RemoveByName(dynamic policy, string name)
    {
        try { policy.Rules.Remove(name); }
        catch (FileNotFoundException) { /* already gone */ }
        catch (COMException) { /* already gone */ }
    }

    private static string FormatPorts(int[] ports) => string.Join(",", ports);

    private static string NormalizePorts(string? ports) =>
        string.IsNullOrWhiteSpace(ports)
            ? ""
            : string.Join(",", ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, out int value) ? value : -1)
                .OrderBy(p => p));
}
