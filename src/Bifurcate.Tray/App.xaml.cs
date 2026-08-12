using System.Windows;
using Bifurcate.Core;

namespace Bifurcate.Tray;

public partial class App : Application
{
    /// <summary>Passed by the sign-in autostart entry so it comes up as a tray icon only.</summary>
    private const string MinimizedFlag = "--minimized";

    private SingleInstance? _instance;
    private StatusService? _status;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The elevated write helper. No window, no tray icon, just save and report a code.
        if (e.Args.Length >= 2 && e.Args[0] == ConfigApplier.ApplyVerb)
        {
            Shutdown(ConfigApplier.ApplyFromFile(e.Args[1]));
            return;
        }

        _instance = SingleInstance.Create();
        if (!_instance.IsFirst)
        {
            _instance.RequestShow();
            Shutdown(0);
            return;
        }

        // Before any window exists, so setup comes up in the right theme on a first run too.
        ThemeManager.Initialize();

        BifurcateConfig? config = LoadOrSetUpConfig();
        if (config is null)
        {
            Shutdown(0);
            return;
        }

        _status = new StatusService(config, TimeSpan.FromSeconds(config.SweepIntervalSeconds));
        _tray = new TrayController(_status);
        _instance.ListenForShowRequests(() => _tray.ShowDashboard());

        _status.Start();
        _ = _status.RefreshAsync();

        if (!e.Args.Contains(MinimizedFlag, StringComparer.OrdinalIgnoreCase))
        {
            _tray.ShowDashboard();
        }
    }

    /// <summary>Returns a usable config, running setup first if there is not one yet.</summary>
    private static BifurcateConfig? LoadOrSetUpConfig()
    {
        ConfigLoadResult result = ConfigStore.Load();
        if (result.Ok) { return result.Config; }

        if (!result.Missing)
        {
            MessageBox.Show(
                $"The settings file needs attention:\n\n{string.Join("\n", result.Errors)}",
                $"{BifurcateInfo.ProductName} Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return SetupWindow.Run(result.Config);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        _tray?.Dispose();
        _status?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
