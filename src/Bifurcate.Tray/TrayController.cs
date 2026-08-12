using System.Diagnostics;
using System.IO;
using System.Windows;
using Bifurcate.Core;
using WinForms = System.Windows.Forms;

namespace Bifurcate.Tray;

/// <summary>
/// The tray icon: colour of the worst status line, a menu, and a balloon when something breaks
/// while nobody is looking at the dashboard.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>The lines worth interrupting someone over.</summary>
    private static readonly StatusKind[] Notifiable = [StatusKind.Tunnel, StatusKind.ExternalIp];

    private readonly StatusService _status;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _startupItem;
    private readonly Dictionary<StatusKind, StatusSeverity> _lastSeverity = [];

    private DashboardWindow? _dashboard;

    public TrayController(StatusService status)
    {
        _status = status;
        _status.Updated += OnUpdated;

        _startupItem = new WinForms.ToolStripMenuItem("Launch at sign-in", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
            Checked = StartupManager.IsEnabled(),
        };

        WinForms.ContextMenuStrip menu = new();
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open dashboard", null, (_, _) => ShowDashboard())
        {
            Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
        });
        menu.Items.Add(new WinForms.ToolStripMenuItem("Refresh now", null, async (_, _) =>
            await _status.RefreshAsync().ConfigureAwait(true)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        menu.Items.Add(BuildThemeMenu());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => Application.Current.Shutdown()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = IconFactory.Tray(StatusSeverity.Neutral),
            Text = BifurcateInfo.ProductName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowDashboard();

        // The icon is drawn in the status colour, and which shade of it depends on the taskbar.
        ThemeManager.Changed += OnThemeChanged;
    }

    private static WinForms.ToolStripMenuItem BuildThemeMenu()
    {
        WinForms.ToolStripMenuItem parent = new("Theme");
        foreach (ThemeChoice choice in Enum.GetValues<ThemeChoice>())
        {
            parent.DropDownItems.Add(new WinForms.ToolStripMenuItem(
                ThemeManager.Label(choice), null, (_, _) => ThemeManager.Set(choice))
            {
                Tag = choice,
            });
        }

        // Ticked when the menu opens, so it is right even if the theme was changed from settings.
        parent.DropDownOpening += (_, _) =>
        {
            foreach (WinForms.ToolStripMenuItem item in parent.DropDownItems)
            {
                item.Checked = (ThemeChoice)item.Tag! == ThemeManager.Choice;
            }
        };

        return parent;
    }

    private void OnThemeChanged()
    {
        if (_status.Latest is not null) { _icon.Icon = IconFactory.Tray(_status.Latest.Worst); }
    }

    public void ShowDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(_status);

            // Closing the window leaves the tray icon running, which is the point of a tray app.
            _dashboard.Closing += (_, args) =>
            {
                args.Cancel = true;
                _dashboard!.Hide();
            };
        }

        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized) { _dashboard.WindowState = WindowState.Normal; }
        _dashboard.Activate();

        _ = _status.RefreshAsync();
    }

    private void ShowSettings()
    {
        SetupWindow.Run(_status.Config, _dashboard);

        // Always, not only on save: the window also toggles sign-in startup, which the status shows.
        _ = _status.RefreshAsync();
    }

    private void ToggleStartup()
    {
        if (StartupManager.IsEnabled())
        {
            StartupManager.Disable();
        }
        else
        {
            StartupManager.Enable(Environment.ProcessPath!, "--minimized");
        }

        _startupItem.Checked = StartupManager.IsEnabled();
        _ = _status.RefreshAsync();
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(BifurcateInfo.LogDirectory);
        Process.Start(new ProcessStartInfo(BifurcateInfo.LogDirectory) { UseShellExecute = true });
    }

    private void OnUpdated(StatusUpdate update)
    {
        _icon.Icon = IconFactory.Tray(update.Worst);
        _icon.Text = Tooltip(update);
        _startupItem.Checked = update.Snapshot.StartupEnabled;

        foreach (StatusKind kind in Notifiable)
        {
            StatusLine line = update.Lines.First(candidate => candidate.Kind == kind);
            bool wasFine = !_lastSeverity.TryGetValue(kind, out StatusSeverity previous)
                || previous != StatusSeverity.Bad;

            _lastSeverity[kind] = line.Severity;

            // Only on the transition into trouble, so a persistent problem is not a repeating alert.
            if (line.Severity == StatusSeverity.Bad && wasFine)
            {
                _icon.ShowBalloonTip(8000, BifurcateInfo.ProductName, line.Text, WinForms.ToolTipIcon.Warning);
            }
        }
    }

    private static string Tooltip(StatusUpdate update)
    {
        string text = string.Join(
            Environment.NewLine,
            update.Lines
                .Where(line => line.Kind is StatusKind.Tunnel or StatusKind.Routing)
                .Select(line => line.Text)
                .Prepend(BifurcateInfo.ProductName));

        // The shell truncates anything past this anyway.
        return text.Length <= 127 ? text : text[..127];
    }

    public void Dispose()
    {
        ThemeManager.Changed -= OnThemeChanged;
        _status.Updated -= OnUpdated;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
