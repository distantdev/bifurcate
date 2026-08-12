using Microsoft.Win32;

namespace Bifurcate.Core;

/// <summary>
/// Sign-in autostart for the tray app, via the per-user Run key. A scheduled task would need
/// administrator rights to register; this does not, which is the whole point of the tray app
/// staying unelevated.
/// </summary>
public static class StartupManager
{
    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BifurcateInfo.StartupRegistryKey);
        return key?.GetValue(BifurcateInfo.StartupRegistryValue) is not null;
    }

    public static void Enable(string executablePath, string? arguments = null)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(BifurcateInfo.StartupRegistryKey);
        string command = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {arguments}";

        key.SetValue(BifurcateInfo.StartupRegistryValue, command);
    }

    public static void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser
            .OpenSubKey(BifurcateInfo.StartupRegistryKey, writable: true);
        key?.DeleteValue(BifurcateInfo.StartupRegistryValue, throwOnMissingValue: false);
    }
}
