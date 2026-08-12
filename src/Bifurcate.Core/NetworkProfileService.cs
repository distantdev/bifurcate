using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

public enum NetworkCategory
{
    Public = 0,
    Private = 1,
    DomainAuthenticated = 2,
}

public sealed record NetworkProfileInfo
{
    public string Name { get; init; } = "";

    public string InterfaceAlias { get; init; } = "";

    public uint InterfaceIndex { get; init; }

    public NetworkCategory Category { get; init; }
}

/// <summary>
/// The tunnel's network profile: whether Windows sees it at all, and its Public/Private category.
/// Reading needs no rights, changing the category needs administrator.
/// </summary>
/// <remarks>
/// Uses CIM rather than the INetworkListManager COM API because MSFT_NetConnectionProfile exposes
/// InterfaceAlias directly. The COM path only offers an adapter GUID, which would then have to be
/// mapped back to an alias for no benefit.
/// </remarks>
public sealed class NetworkProfileService : IDisposable
{
    private readonly CimSession _session = Cim.CreateSession();

    public IReadOnlyList<NetworkProfileInfo> List() => [.. Query().Select(Map)];

    /// <summary>
    /// Finds the tunnel by name. Matches the profile name or the adapter alias, because a RAS
    /// adapter is usually named after the connection but is not guaranteed to be.
    /// </summary>
    public NetworkProfileInfo? FindTunnel(string vpnName)
    {
        if (string.IsNullOrWhiteSpace(vpnName)) { return null; }
        return List().FirstOrDefault(p => Matches(p.Name, vpnName) || Matches(p.InterfaceAlias, vpnName));
    }

    public void SetCategory(string interfaceAlias, NetworkCategory category)
    {
        CimInstance? instance = Query()
            .FirstOrDefault(i => Cim.StringOf(i, "InterfaceAlias")
                .Equals(interfaceAlias, StringComparison.OrdinalIgnoreCase));

        if (instance is null)
        {
            throw new InvalidOperationException($"No network profile on interface '{interfaceAlias}'.");
        }

        instance.CimInstanceProperties["NetworkCategory"].Value = (uint)category;
        _session.ModifyInstance(Cim.StandardNamespace, instance);
    }

    private static bool Matches(string value, string vpnName) =>
        value.Contains(vpnName, StringComparison.OrdinalIgnoreCase);

    private List<CimInstance> Query() =>
        [.. _session.QueryInstances(Cim.StandardNamespace, "WQL", "SELECT * FROM MSFT_NetConnectionProfile")];

    private static NetworkProfileInfo Map(CimInstance instance) => new()
    {
        Name = Cim.StringOf(instance, "Name"),
        InterfaceAlias = Cim.StringOf(instance, "InterfaceAlias"),
        InterfaceIndex = Cim.UIntOf(instance, "InterfaceIndex"),
        Category = (NetworkCategory)Cim.UIntOf(instance, "NetworkCategory"),
    };

    public void Dispose() => _session.Dispose();
}
