using System.Windows;
using System.Windows.Controls;
using Bifurcate.Core;

namespace Bifurcate.Tray;

public partial class SetupWindow : Window
{
    private sealed record ThemeOption(ThemeChoice Choice, string Label);

    /// <summary>A yes or no setting shown as a two item list, to match the other rows.</summary>
    private sealed record Switch(bool On, string Label);

    private readonly BifurcateConfig _startingPoint;
    private bool _loaded;

    private SetupWindow(BifurcateConfig? existing)
    {
        InitializeComponent();

        _startingPoint = existing ?? BifurcateConfig.CreateSample() with
        {
            VpnConnectionName = "",
            TunnelRoutes = [],
            Probe = new ProbeConfig { Host = "" },
        };

        Icon = IconFactory.WindowImage();
        Intro.Text = existing is null
            ? $"{BifurcateInfo.ProductName} keeps a VPN tunnel from idling out and keeps this PC hidden " +
              "from other machines on it. Point it at your VPN to get started."
            : "These settings are shared by the background service and the dashboard.";

        ProbeTypeList.ItemsSource = Enum.GetValues<ProbeKind>();

        List<ThemeOption> themes = [.. Enum.GetValues<ThemeChoice>()
            .Select(choice => new ThemeOption(choice, ThemeManager.Label(choice)))];

        Offer(HardeningList, "Hidden");
        Offer(StartupList, "Launch at sign-in");
        Choose(StartupList, StartupManager.IsEnabled());

        ThemeList.DisplayMemberPath = nameof(ThemeOption.Label);
        ThemeList.ItemsSource = themes;
        ThemeList.SelectedItem = themes.First(theme => theme.Choice == ThemeManager.Choice);

        Fill(_startingPoint);
        Loaded += async (_, _) => await LoadVpnConnectionsAsync().ConfigureAwait(true);
    }

    /// <summary>The saved configuration, or null if the window was cancelled.</summary>
    public BifurcateConfig? Result { get; private set; }

    public static BifurcateConfig? Run(BifurcateConfig? existing, Window? owner = null)
    {
        SetupWindow window = new(existing);
        if (owner is not null && owner.IsVisible) { window.Owner = owner; }

        return window.ShowDialog() == true ? window.Result : null;
    }

    private void Fill(BifurcateConfig config)
    {
        RoutesBox.Text = string.Join(", ", config.TunnelRoutes);
        ProbeTypeList.SelectedItem = config.Probe.Type;
        ProbeHostBox.Text = config.Probe.Host;
        ProbePortBox.Text = config.Probe.Port.ToString();
        IntervalBox.Text = config.SweepIntervalSeconds.ToString();
        PublicIpBox.Text = config.PublicIpUrl;
        Choose(HardeningList, config.Hardening.Enabled);
        UpdatePortVisibility();
    }

    private static void Offer(ComboBox list, string onLabel)
    {
        list.DisplayMemberPath = nameof(Switch.Label);
        list.ItemsSource = new List<Switch> { new(true, onLabel), new(false, "Disabled") };
    }

    private static void Choose(ComboBox list, bool on) =>
        list.SelectedItem = list.Items.Cast<Switch>().First(option => option.On == on);

    private static bool IsOn(ComboBox list) => list.SelectedItem is Switch { On: true };

    private async Task LoadVpnConnectionsAsync()
    {
        List<VpnConnectionInfo> connections;
        try
        {
            using VpnProfileService service = new();
            connections = [.. await Task.Run(service.List).ConfigureAwait(true)];
        }
        catch (Exception ex)
        {
            ShowProblems($"Could not read the VPN connections on this PC. {ex.Message}");
            return;
        }

        VpnList.ItemsSource = connections;
        VpnList.DisplayMemberPath = nameof(VpnConnectionInfo.Name);

        VpnList.SelectedItem = connections
            .FirstOrDefault(connection => connection.Name == _startingPoint.VpnConnectionName);

        if (VpnList.SelectedItem is null && connections.Count == 1)
        {
            VpnList.SelectedIndex = 0;
        }

        if (connections.Count == 0)
        {
            ShowProblems("This PC has no VPN connections. Add one in Windows Settings first, then reopen this window.");
        }

        // From here on, changing the dropdown is the user's doing and may overwrite the routes.
        _loaded = true;

        // On first run there is nothing to preserve, so take what the connection already knows.
        if (RoutesBox.Text.Length == 0) { CopyRoutesFromSelection(); }
    }

    /// <summary>
    /// Copies what the selected connection already knows, so most people only have to fill in the
    /// health check.
    /// </summary>
    private void OnVpnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) { CopyRoutesFromSelection(); }
    }

    private void CopyRoutesFromSelection()
    {
        if (VpnList.SelectedItem is VpnConnectionInfo { Routes.Length: > 0 } connection)
        {
            RoutesBox.Text = string.Join(", ", connection.Routes);
        }
    }

    private void OnProbeTypeChanged(object sender, SelectionChangedEventArgs e) => UpdatePortVisibility();

    /// <summary>
    /// The theme is a per-user preference in the registry rather than part of the shared settings
    /// file, so it applies on the spot and Save and Cancel have nothing to do with it.
    /// </summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeList.SelectedItem is ThemeOption option) { ThemeManager.Set(option.Choice); }
    }

    /// <summary>Also per-user, also applied on the spot. A no-op when the list is being filled in.</summary>
    private void OnStartupChanged(object sender, SelectionChangedEventArgs e)
    {
        bool wanted = IsOn(StartupList);
        if (wanted == StartupManager.IsEnabled()) { return; }

        if (wanted)
        {
            StartupManager.Enable(Environment.ProcessPath!, "--minimized");
        }
        else
        {
            StartupManager.Disable();
        }
    }

    private void UpdatePortVisibility()
    {
        bool isTcp = ProbeTypeList.SelectedItem is ProbeKind.Tcp;
        ProbePortBox.IsEnabled = isTcp;
        ProbePortBox.ToolTip = isTcp ? "Port to connect to" : "Only used for a TCP check";
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        BifurcateConfig candidate = Build();
        TestButton.IsEnabled = false;
        TestResult.Text = $"Checking {candidate.Probe.Host}...";

        try
        {
            ProbeResult result = await TunnelProbe
                .RunAsync(candidate.Probe, CancellationToken.None)
                .ConfigureAwait(true);

            TestResult.Text = result.Reachable
                ? $"{candidate.Probe.Host} answered in {result.LatencyMs} ms."
                : $"No answer from {candidate.Probe.Host}. Connect the VPN and try again, or use a TCP check if ping is blocked.";

            if (result.Reachable && candidate.ClassifyProbeHost() == ProbeHostRouting.Outside)
            {
                TestResult.Text += " It is outside the subnets above, so in Subnet Only mode it would " +
                                   "not travel through the tunnel.";
            }
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        BifurcateConfig candidate = Build();
        IReadOnlyList<string> errors = candidate.Validate();

        if (errors.Count > 0)
        {
            ShowProblems(string.Join(Environment.NewLine, errors));
            return;
        }

        SaveResult saved = ConfigApplier.Save(candidate);
        switch (saved.Outcome)
        {
            case SaveOutcome.Saved:
                Result = candidate;
                WarnIfServiceMissing();
                DialogResult = true;
                return;

            case SaveOutcome.Declined:
                ShowProblems("Saving needs administrator approval, because the background service " +
                             "acts on these settings for the whole machine.");
                return;

            case SaveOutcome.Failed:
                ShowProblems($"Could not save. {saved.Error}");
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(saved), saved.Outcome, "Unhandled save outcome.");
        }
    }

    private void WarnIfServiceMissing()
    {
        if (ServiceStateReader.Read() != ServiceState.NotInstalled) { return; }

        MessageBox.Show(this,
            "Settings saved.\n\nThe background service is not installed yet, so nothing will hold the " +
            "tunnel open. Run install.ps1 from an elevated PowerShell prompt to install it.",
            BifurcateInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private BifurcateConfig Build() => _startingPoint with
    {
        VpnConnectionName = (VpnList.SelectedItem as VpnConnectionInfo)?.Name ?? "",
        TunnelRoutes = Split(RoutesBox.Text),
        Probe = new ProbeConfig
        {
            Type = ProbeTypeList.SelectedItem is ProbeKind kind ? kind : ProbeKind.Icmp,
            Host = ProbeHostBox.Text.Trim(),
            Port = int.TryParse(ProbePortBox.Text, out int port) ? port : 0,
            TimeoutMs = _startingPoint.Probe.TimeoutMs,
        },
        SweepIntervalSeconds = int.TryParse(IntervalBox.Text, out int interval) ? interval : 0,
        PublicIpUrl = PublicIpBox.Text.Trim(),
        Hardening = _startingPoint.Hardening with { Enabled = IsOn(HardeningList) },
    };

    private static string[] Split(string text) =>
        [.. text.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private void ShowProblems(string message)
    {
        Problems.Text = message;
        Problems.Foreground = SeverityPalette.Brush(StatusSeverity.Bad);
        Problems.Visibility = Visibility.Visible;
    }
}
