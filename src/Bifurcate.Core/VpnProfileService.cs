using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

public sealed record VpnConnectionInfo
{
    public string Name { get; init; } = "";

    public string ServerAddress { get; init; } = "";

    /// <summary>True means subnet-only: no default route, so only <see cref="Routes"/> use the tunnel.</summary>
    public bool SplitTunneling { get; init; }

    public string ConnectionStatus { get; init; } = "";

    public string TunnelType { get; init; } = "";

    /// <summary>Prefixes saved on the profile. They are applied when the connection is next dialed.</summary>
    public string[] Routes { get; init; } = [];

    public bool IsConnected =>
        ConnectionStatus.Equals("Connected", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads and writes the per-user VPN profile. This has to run in the signed-in user's context: the
/// profile lives in that user's phonebook, so a LocalSystem service cannot reach it.
/// </summary>
public sealed class VpnProfileService : IDisposable
{
    private readonly CimSession _session = Cim.CreateSession();

    public IReadOnlyList<VpnConnectionInfo> List()
    {
        CimMethodResult result = Cim.InvokeStatic(_session, Cim.VpnNamespace, "PS_VpnConnection", "Get");
        return [.. Cim.CmdletOutput(result).Select(Map)];
    }

    public VpnConnectionInfo? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }

        try
        {
            CimMethodResult result = Cim.InvokeStatic(
                _session, Cim.VpnNamespace, "PS_VpnConnection", "Get", Cim.In("Name", new[] { name }));
            return Cim.CmdletOutput(result).Select(Map).FirstOrDefault();
        }
        catch (CimException)
        {
            // No such connection is reported as a CIM error rather than an empty result.
            return null;
        }
    }

    public void SetSplitTunneling(string name, bool splitTunneling) =>
        Cim.InvokeStatic(_session, Cim.VpnNamespace, "PS_VpnConnection", "Set",
            Cim.In("Name", name),
            Cim.In("SplitTunneling", splitTunneling));

    public void AddRoute(string connectionName, string destinationPrefix) =>
        Cim.InvokeStatic(_session, Cim.VpnNamespace, "PS_VpnConnectionRoute", "Add",
            Cim.In("ConnectionName", connectionName),
            Cim.In("DestinationPrefix", destinationPrefix));

    public void RemoveRoute(string connectionName, string destinationPrefix) =>
        Cim.InvokeStatic(_session, Cim.VpnNamespace, "PS_VpnConnectionRoute", "Remove",
            Cim.In("ConnectionName", connectionName),
            Cim.In("DestinationPrefix", destinationPrefix));

    private static VpnConnectionInfo Map(CimInstance instance) => new()
    {
        Name = Cim.StringOf(instance, "Name"),
        ServerAddress = Cim.StringOf(instance, "ServerAddress"),
        SplitTunneling = Cim.BoolOf(instance, "SplitTunneling"),
        ConnectionStatus = Cim.StringOf(instance, "ConnectionStatus"),
        TunnelType = Cim.StringOf(instance, "TunnelType"),
        Routes = instance.CimInstanceProperties["Routes"]?.Value is CimInstance[] routes
            ? [.. routes.Select(r => Cim.StringOf(r, "DestinationPrefix")).Where(p => p.Length > 0)]
            : [],
    };

    public void Dispose() => _session.Dispose();
}
