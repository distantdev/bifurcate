namespace Bifurcate.Core;

/// <summary>
/// Product identity and every path or name derived from it. Nothing else in the codebase should
/// hardcode the product name, so a rebrand is a single edit here.
/// </summary>
public static class BifurcateInfo
{
    public const string ProductName = "Bifurcate";
    public const string ServiceName = "Bifurcate";
    public const string ServiceDisplayName = "Bifurcate VPN Privacy";

    public const string ServiceDescription =
        "Keeps a VPN tunnel from idling out and keeps this PC hidden from other machines on it, " +
        "by holding the network profile Private and blocking inbound file sharing and discovery " +
        "on the tunnel adapter only.";

    public const string SmbRuleName = ProductName + "-Block-Inbound-SMB";
    public const string DiscoveryRuleName = ProductName + "-Block-Inbound-Discovery";

    /// <summary>Every rule this tool owns starts with this, which is what cleanup matches on.</summary>
    public const string RulePrefix = ProductName + "-Block-Inbound-";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Per-user files the tray can write without elevation. The VPN profile is per-user too, so the
    /// host-route ledger lives here rather than under ProgramData.
    /// </summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    /// <summary>
    /// Hostnames to /32 prefixes last applied to the VPN profile, so a later sync can drop stale
    /// ones without touching subnets the user configured.
    /// </summary>
    public static string HostRouteStatePath { get; } = Path.Combine(UserDataDirectory, "host-routes.json");

    /// <summary>Value name used under HKCU Run for the tray app's sign-in autostart.</summary>
    public const string StartupRegistryValue = ProductName;

    public const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Per-user preferences that are nobody else's business, under HKCU. Anything the service acts
    /// on belongs in the config file instead, which is admin-writable on purpose.
    /// </summary>
    public const string PreferencesRegistryKey = @"Software\" + ProductName;
}
