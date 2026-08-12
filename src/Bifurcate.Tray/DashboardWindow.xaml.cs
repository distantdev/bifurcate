using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bifurcate.Core;

namespace Bifurcate.Tray;

/// <summary>One row of the dashboard, shaped for binding.</summary>
public sealed record StatusLineView(string Text, Brush Color);

public partial class DashboardWindow : Window
{
    private readonly StatusService _status;

    public DashboardWindow(StatusService status)
    {
        InitializeComponent();

        _status = status;
        Icon = IconFactory.WindowImage();
        Heading.Text = $"{BifurcateInfo.ProductName} - {status.Config.VpnConnectionName}";
        Footer.Text = "Checking...";

        _status.Updated += Render;
        _status.ConfigProblem += OnConfigProblem;

        // WPF restyles the controls itself, but the status colours and the icon are painted here.
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;

        // A window that has been sitting hidden shows stale state otherwise.
        Activated += async (_, _) => await _status.RefreshAsync().ConfigureAwait(true);

        // Keeps the focus ring off a routing button, where it reads as the mode being active.
        Loaded += (_, _) => Focus();

        if (_status.Latest is not null) { Render(_status.Latest); }
    }

    private void Render(StatusUpdate update)
    {
        Heading.Text = $"{BifurcateInfo.ProductName} - {update.Snapshot.Config.VpnConnectionName}";

        StatusLines.ItemsSource = update.Lines
            .Select(line => new StatusLineView(line.Text, SeverityPalette.Brush(line.Severity)))
            .ToList();

        // Which mode is saved, shown without relying on which button happens to have focus.
        bool subnetOnly = update.Snapshot.Vpn?.SplitTunneling ?? false;
        RouteAllButton.FontWeight = subnetOnly ? FontWeights.Normal : FontWeights.SemiBold;
        SubnetOnlyButton.FontWeight = subnetOnly ? FontWeights.SemiBold : FontWeights.Normal;
        Tint(RouteAllButton, active: !subnetOnly);
        Tint(SubnetOnlyButton, active: subnetOnly);

        bool haveVpn = update.Snapshot.Vpn is not null;
        RouteAllButton.IsEnabled = haveVpn;
        SubnetOnlyButton.IsEnabled = haveVpn;

        Footer.Text = $"Checked at {update.AtLocalTime:HH:mm:ss}. " +
                      $"Rechecks every {update.Snapshot.Config.SweepIntervalSeconds} seconds.";
    }

    /// <summary>
    /// Marks the saved routing mode. The inactive one clears the property rather than restoring a
    /// remembered brush, so it goes back to whatever the current theme's button style paints.
    /// </summary>
    private static void Tint(Button button, bool active)
    {
        if (active)
        {
            button.Background = ThemeManager.IsDark
                ? SeverityPalette.ActiveChoiceDark
                : SeverityPalette.ActiveChoice;
        }
        else
        {
            button.ClearValue(BackgroundProperty);
        }
    }

    private void OnThemeChanged()
    {
        Icon = IconFactory.WindowImage();
        if (_status.Latest is not null) { Render(_status.Latest); }
    }

    private void OnConfigProblem(string message) =>
        Footer.Text = $"Using the last good settings. {message}";

    private async void OnRouteAllClick(object sender, RoutedEventArgs e) =>
        await ApplyRoutingMode(subnetOnly: false).ConfigureAwait(true);

    private async void OnSubnetOnlyClick(object sender, RoutedEventArgs e) =>
        await ApplyRoutingMode(subnetOnly: true).ConfigureAwait(true);

    private async Task ApplyRoutingMode(bool subnetOnly)
    {
        RouteAllButton.IsEnabled = false;
        SubnetOnlyButton.IsEnabled = false;

        try
        {
            IReadOnlyList<string> added = await _status.SetRoutingModeAsync(subnetOnly).ConfigureAwait(true);

            string mode = subnetOnly ? "Subnet only" : "All traffic";
            string routes = added.Count > 0 ? $" Added {string.Join(", ", added)}." : "";
            Footer.Text = $"{mode} saved.{routes} Reconnect the VPN to apply it.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not change the routing mode.\n\n{ex.Message}",
                BifurcateInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RouteAllButton.IsEnabled = true;
            SubnetOnlyButton.IsEnabled = true;
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SetupWindow.Run(_status.Config, this);

        // Always, not only on save: the window also toggles sign-in startup, which the status shows.
        await _status.RefreshAsync().ConfigureAwait(true);
    }
}
