using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bifurcate.Core;

public sealed record ProbeResult
{
    public bool Reachable { get; init; }

    public int LatencyMs { get; init; }

    public static readonly ProbeResult Unreachable = new();
}

/// <summary>
/// Reaches a host inside the tunnel. Doubles as the keep-alive: the traffic itself is what stops
/// the session idling out, so this runs on a schedule whether anyone is watching or not.
/// </summary>
public static class TunnelProbe
{
    public static Task<ProbeResult> RunAsync(ProbeConfig probe, CancellationToken cancellationToken) =>
        probe.Type switch
        {
            ProbeKind.Tcp => TcpAsync(probe, cancellationToken),
            ProbeKind.Icmp => IcmpAsync(probe),
            _ => Task.FromResult(ProbeResult.Unreachable),
        };

    private static async Task<ProbeResult> IcmpAsync(ProbeConfig probe)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(probe.Host, probe.TimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new ProbeResult { Reachable = true, LatencyMs = (int)reply.RoundtripTime }
                : ProbeResult.Unreachable;
        }
        catch (Exception ex) when (ex is PingException or SocketException or ArgumentException)
        {
            return ProbeResult.Unreachable;
        }
    }

    private static async Task<ProbeResult> TcpAsync(ProbeConfig probe, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probe.TimeoutMs);

        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(probe.Host, probe.Port, timeout.Token).ConfigureAwait(false);
            return new ProbeResult { Reachable = true, LatencyMs = (int)elapsed.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return ProbeResult.Unreachable;
        }
    }
}

/// <summary>
/// Looks up the address the outside world sees. Supporting detail for the routing verdict rather
/// than the verdict itself, since that comes from the local route table.
/// </summary>
public sealed class PublicIpProbe : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) { return null; }

        try
        {
            string body = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            string trimmed = body.Trim();

            // Some endpoints answer with an error page rather than a failure status.
            return IPAddress.TryParse(trimmed, out _) ? trimmed : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
