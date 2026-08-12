using System.Net.NetworkInformation;
using Bifurcate.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifurcate.Service;

/// <summary>
/// Holds the tunnel open and keeps it hardened. Runs as LocalSystem, which is what makes the
/// network category and firewall changes possible without ever prompting anyone.
/// </summary>
public sealed class KeepAliveWorker(ILogger<KeepAliveWorker> log) : BackgroundService
{
    /// <summary>An adapter needs a moment after a network change before it has a profile.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _wake = new(0);
    private readonly NetworkProfileService _profiles = new();
    private readonly FirewallService _firewall = new();

    private BifurcateConfig? _config;
    private bool _configComplained;
    private bool _networkChanged;
    private string _lastTunnelState = "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("{Product} service starting. Config: {ConfigPath}",
            BifurcateInfo.ProductName, BifurcateInfo.ConfigPath);

        Hardener hardener = new(_profiles, _firewall);
        ReloadConfig(initial: true);

        using IDisposable watcher = ConfigStore.Watch(() =>
        {
            ReloadConfig(initial: false);
            Wake(networkChanged: false);
        });

        NetworkAddressChangedEventHandler onAddressChanged = (_, _) => Wake(networkChanged: true);
        NetworkAvailabilityChangedEventHandler onAvailabilityChanged = (_, _) => Wake(networkChanged: true);
        NetworkChange.NetworkAddressChanged += onAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += onAvailabilityChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_networkChanged)
                {
                    _networkChanged = false;
                    await Task.Delay(SettleDelay, stoppingToken).ConfigureAwait(false);
                }

                await SweepAsync(hardener, stoppingToken).ConfigureAwait(false);
                await WaitForNextSweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= onAvailabilityChanged;
            log.LogInformation("{Product} service stopping.", BifurcateInfo.ProductName);
        }
    }

    private async Task SweepAsync(Hardener hardener, CancellationToken cancellationToken)
    {
        BifurcateConfig? config = _config;
        if (config is null) { return; }

        NetworkProfileInfo? tunnel;
        try
        {
            tunnel = _profiles.FindTunnel(config.VpnConnectionName);
            LogTunnelTransition(tunnel, config);

            HardeningOutcome outcome = hardener.Sweep(config);
            if (outcome.Action != HardeningAction.None)
            {
                log.LogInformation("Hardening {Action}: {Detail}", outcome.Action, outcome.Detail);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            log.LogError(ex, "Hardening needs administrator rights. Run this as a service, not as a plain user.");
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sweep failed.");
            return;
        }

        // The keep-alive itself. Pointless while the tunnel is down, and it would only stall the
        // loop for the probe timeout.
        if (tunnel is null) { return; }

        ProbeResult result = await TunnelProbe.RunAsync(config.Probe, cancellationToken).ConfigureAwait(false);
        if (!result.Reachable)
        {
            log.LogWarning("Keep-alive probe to {Host} got no answer while the tunnel was up.",
                config.Probe.Host);
        }
    }

    private void LogTunnelTransition(NetworkProfileInfo? tunnel, BifurcateConfig config)
    {
        string state = tunnel is null ? "down" : "up";
        if (state == _lastTunnelState) { return; }

        _lastTunnelState = state;

        if (tunnel is null)
        {
            log.LogInformation("'{VpnName}' is not connected.", config.VpnConnectionName);
        }
        else
        {
            log.LogInformation("'{VpnName}' is connected on '{Alias}' ({Category}).",
                config.VpnConnectionName, tunnel.InterfaceAlias, tunnel.Category);
        }
    }

    private async Task WaitForNextSweepAsync(CancellationToken cancellationToken)
    {
        int seconds = _config?.SweepIntervalSeconds ?? 60;
        await _wake.WaitAsync(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
    }

    private void Wake(bool networkChanged)
    {
        if (networkChanged) { _networkChanged = true; }

        // A count above one would only cause back-to-back sweeps of the same state.
        if (_wake.CurrentCount == 0) { _wake.Release(); }
    }

    private void ReloadConfig(bool initial)
    {
        ConfigLoadResult result = ConfigStore.Load();

        if (result.Ok)
        {
            bool changed = !ConfigStore.SameAs(_config, result.Config);
            _config = result.Config;
            _configComplained = false;

            if (initial)
            {
                log.LogInformation(
                    "Managing '{VpnName}'. Probe {ProbeType} {Host}, sweep every {Interval}s, hardening {Hardening}.",
                    _config!.VpnConnectionName, _config.Probe.Type, _config.Probe.Host,
                    _config.SweepIntervalSeconds, _config.Hardening.Enabled ? "on" : "off");
            }
            else if (changed)
            {
                log.LogInformation("Config reloaded.");
            }

            WarnIfProbeCannotTestTheTunnel(_config!);
            return;
        }

        // A missing or broken config is usually a one-off state during setup, so say it once rather
        // than every sweep.
        if (!_configComplained)
        {
            _configComplained = true;
            _config = null;

            if (result.Missing)
            {
                log.LogWarning("No config yet at {Path}. Waiting for setup to write one.",
                    BifurcateInfo.ConfigPath);
            }
            else
            {
                log.LogError("Config is not usable, so nothing will be enforced: {Errors}",
                    string.Join(" | ", result.Errors));
            }
        }
    }

    private void WarnIfProbeCannotTestTheTunnel(BifurcateConfig config)
    {
        if (config.ClassifyProbeHost() == ProbeHostRouting.Outside)
        {
            log.LogWarning(
                "Probe host {Host} is outside {Routes}. In subnet-only mode the keep-alive will " +
                "travel over the local connection and never touch the tunnel.",
                config.Probe.Host, string.Join(", ", config.TunnelRoutes));
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        _profiles.Dispose();
        base.Dispose();
    }
}
