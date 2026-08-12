namespace Bifurcate.Core;

public enum HardeningAction
{
    /// <summary>Already in the desired state. The common case, and deliberately not logged.</summary>
    None,

    Applied,

    /// <summary>Rules withdrawn, either because the tunnel went away or hardening was turned off.</summary>
    Removed,
}

public sealed record HardeningOutcome(HardeningAction Action, string Detail)
{
    public static readonly HardeningOutcome NoChange = new(HardeningAction.None, "");
}

/// <summary>
/// Keeps the tunnel adapter Private and its inbound file sharing and discovery blocked, and takes
/// the rules back down when the tunnel disappears so a later adapter cannot inherit them.
/// Needs administrator rights, which is why it lives in the service.
/// </summary>
public sealed class Hardener(NetworkProfileService profiles, FirewallService firewall)
{
    public HardeningOutcome Sweep(BifurcateConfig config)
    {
        if (!config.Hardening.Enabled)
        {
            return firewall.AnyRulesPresent()
                ? Remove("hardening is turned off in the config")
                : HardeningOutcome.NoChange;
        }

        NetworkProfileInfo? tunnel = profiles.FindTunnel(config.VpnConnectionName);
        if (tunnel is null)
        {
            return firewall.AnyRulesPresent()
                ? Remove($"'{config.VpnConnectionName}' is no longer connected")
                : HardeningOutcome.NoChange;
        }

        bool categoryWrong = config.Hardening.SetNetworkPrivate
            && tunnel.Category != NetworkCategory.Private;
        bool rulesWrong = !firewall.RulesAreCurrent(tunnel.InterfaceAlias, config.Hardening);

        if (!categoryWrong && !rulesWrong) { return HardeningOutcome.NoChange; }

        List<string> changes = [];

        if (categoryWrong)
        {
            profiles.SetCategory(tunnel.InterfaceAlias, NetworkCategory.Private);
            changes.Add($"category {tunnel.Category} to Private");
        }

        if (rulesWrong)
        {
            firewall.EnsureRules(tunnel.InterfaceAlias, config.Hardening);
            changes.Add("inbound block rules");
        }

        return new HardeningOutcome(HardeningAction.Applied,
            $"{string.Join(" and ", changes)} on '{tunnel.InterfaceAlias}'");
    }

    private HardeningOutcome Remove(string reason)
    {
        IReadOnlyList<string> removed = firewall.RemoveOwnedRules();
        return new HardeningOutcome(HardeningAction.Removed,
            $"removed {removed.Count} rule(s) because {reason}");
    }
}
