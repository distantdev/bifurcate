using System.Net;
using System.Net.Sockets;

namespace Bifurcate.Core;

/// <summary>A CIDR prefix such as 10.0.0.0/16, with containment testing.</summary>
public readonly record struct NetworkPrefix(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string? text, out NetworkPrefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(text)) { return false; }

        string[] parts = text.Split('/', 2);
        if (parts.Length != 2) { return false; }
        if (!IPAddress.TryParse(parts[0], out IPAddress? network)) { return false; }
        if (!int.TryParse(parts[1], out int length)) { return false; }

        int max = network.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (length < 0 || length > max) { return false; }

        prefix = new NetworkPrefix(network, length);
        return true;
    }

    /// <summary>The /32 used when a hostname is pinned onto the tunnel.</summary>
    public static string IPv4HostRoute(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(
                nameof(address), address.AddressFamily, "Host routes are IPv4 /32 prefixes.");
        }

        return $"{address}/32";
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Network.AddressFamily) { return false; }

        byte[] target = address.GetAddressBytes();
        byte[] network = Network.GetAddressBytes();
        int fullBytes = PrefixLength / 8;
        int remainingBits = PrefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (target[i] != network[i]) { return false; }
        }

        if (remainingBits == 0) { return true; }

        int mask = 0xFF << (8 - remainingBits);
        return (target[fullBytes] & mask) == (network[fullBytes] & mask);
    }

    public override string ToString() => $"{Network}/{PrefixLength}";
}
