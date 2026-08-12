using System.Windows;
using Bifurcate.Core;
using Microsoft.Win32;

namespace Bifurcate.Tray;

public enum ThemeChoice
{
    /// <summary>Follow the light or dark setting Windows is using for apps.</summary>
    System,

    Light,
    Dark,
}

/// <summary>
/// Light, dark, or follow Windows. WPF's Fluent theme restyles the controls on its own; this tracks
/// which theme ended up in effect so the parts painted by hand, the status colours and the tray
/// icon, can match.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string ThemeValue = "Theme";

    /// <summary>Raised on the UI thread after the effective theme changes.</summary>
    public static event Action? Changed;

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    /// <summary>Whether the windows are currently dark.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Whether the taskbar is dark, which is a separate Windows setting and the one that decides
    /// what the tray icon has to contrast against.
    /// </summary>
    public static bool TrayIsDark { get; private set; }

    /// <summary>Shared by the tray menu and the settings window so they read the same.</summary>
    public static string Label(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => "Light",
        ThemeChoice.Dark => "Dark",
        _ => "System",
    };

    public static void Initialize()
    {
        Choice = Load();
        Apply();

        // Fires for the app and taskbar theme switches, among much else, so the work is to
        // re-read and compare rather than to trust the notification.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    public static void Set(ThemeChoice choice)
    {
        if (choice == Choice) { return; }

        Choice = choice;
        Save(choice);
        Apply();
    }

    private static void Apply()
    {
        bool wasDark = IsDark;
        bool trayWasDark = TrayIsDark;

        IsDark = Choice switch
        {
            ThemeChoice.Light => false,
            ThemeChoice.Dark => true,
            _ => WindowsPrefersDark("AppsUseLightTheme"),
        };

        TrayIsDark = WindowsPrefersDark("SystemUsesLightTheme");

#pragma warning disable WPF0001 // ThemeMode is still experimental in this version of WPF.
        Application.Current.ThemeMode = Choice switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        if (IsDark != wasDark || TrayIsDark != trayWasDark) { Changed?.Invoke(); }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) { return; }

        // Raised on a system thread, and everything downstream of it touches UI objects.
        Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    /// <summary>The value is "use light", so a zero means dark and a missing key means light.</summary>
    private static bool WindowsPrefersDark(string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(valueName) is int useLight && useLight == 0;
    }

    private static ThemeChoice Load()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BifurcateInfo.PreferencesRegistryKey);
        return Enum.TryParse(key?.GetValue(ThemeValue) as string, out ThemeChoice stored)
            ? stored
            : ThemeChoice.System;
    }

    private static void Save(ThemeChoice choice)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(BifurcateInfo.PreferencesRegistryKey);
        key.SetValue(ThemeValue, choice.ToString());
    }
}
