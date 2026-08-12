using System.Net.NetworkInformation;
using System.Windows.Threading;
using Bifurcate.Core;

namespace Bifurcate.Tray;

public sealed record StatusUpdate
{
    public required StatusSnapshot Snapshot { get; init; }

    public required IReadOnlyList<StatusLine> Lines { get; init; }

    public required DateTime AtLocalTime { get; init; }

    public StatusSeverity Worst => SeverityPalette.Worst(Lines);
}

/// <summary>
/// Owns the refresh loop: reload config, probe, read machine state, evaluate. One instance for the
/// whole app, so the tray icon and the window always agree and the work happens once.
/// </summary>
public sealed class StatusService : IDisposable
{
    private readonly StatusCollector _collector = new();
    private readonly PublicIpProbe _publicIpProbe = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settle;

    public StatusService(BifurcateConfig config, TimeSpan interval)
    {
        Config = config;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        // Windows reports an address change several times as an adapter comes or goes, so the
        // refresh waits for the burst to stop rather than running once per event.
        _settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _settle.Tick += async (_, _) =>
        {
            _settle.Stop();
            await RefreshAsync().ConfigureAwait(true);
        };
    }

    public event Action<StatusUpdate>? Updated;

    public event Action<string>? ConfigProblem;

    public BifurcateConfig Config { get; private set; }

    public StatusUpdate? Latest { get; private set; }

    public void Start()
    {
        // The interval alone would leave a dropped VPN unreported for up to that long, which is too
        // late to be worth telling someone about.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _timer.Start();
    }

    /// <summary>
    /// Refreshes everything. Overlapping calls are dropped rather than queued, since the caller
    /// only ever wants the current state and the CIM session is not reentrant.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (!await _oneAtATime.WaitAsync(0).ConfigureAwait(true)) { return; }

        try
        {
            ReloadConfig();

            Task<ProbeResult> probeTask = TunnelProbe.RunAsync(Config.Probe, CancellationToken.None);
            Task<string?> ipTask = _publicIpProbe.GetAsync(Config.PublicIpUrl, CancellationToken.None);
            await Task.WhenAll(probeTask, ipTask).ConfigureAwait(true);

            BifurcateConfig config = Config;
            ProbeResult probe = probeTask.Result;
            string publicIp = ipTask.Result ?? "";

            // CIM and COM calls block, so they stay off the UI thread.
            StatusSnapshot snapshot = await Task
                .Run(() => _collector.Collect(config, probe, publicIp))
                .ConfigureAwait(true);

            Latest = new StatusUpdate
            {
                Snapshot = snapshot,
                Lines = StatusEvaluator.Evaluate(snapshot),
                AtLocalTime = DateTime.Now,
            };

            Updated?.Invoke(Latest);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Switches routing mode on the saved profile. No elevation needed.</summary>
    public async Task<IReadOnlyList<string>> SetRoutingModeAsync(bool subnetOnly)
    {
        BifurcateConfig config = Config;
        IReadOnlyList<string> added = await Task
            .Run(() => _collector.SetRoutingMode(config, subnetOnly))
            .ConfigureAwait(true);

        await RefreshAsync().ConfigureAwait(true);
        return added;
    }

    public Task<IReadOnlyList<VpnConnectionInfo>> ListVpnConnectionsAsync() =>
        Task.Run(() => _collector.ListVpnConnections());

    // Raised on a thread pool thread, so the restart hops back to the dispatcher that owns the timer.
    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _settle.Dispatcher.BeginInvoke(() =>
        {
            _settle.Stop();
            _settle.Start();
        });

    private void ReloadConfig()
    {
        ConfigLoadResult result = ConfigStore.Load();

        if (result.Ok)
        {
            Config = result.Config!;
            return;
        }

        // Keep running on the last good config rather than blanking the dashboard.
        if (result.Errors.Count > 0)
        {
            ConfigProblem?.Invoke(string.Join(" ", result.Errors));
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _settle.Stop();
        _timer.Stop();
        _collector.Dispose();
        _publicIpProbe.Dispose();
        _oneAtATime.Dispose();
    }
}
