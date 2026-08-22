using System.Net;
using Bifurcate.Core;

namespace Bifurcate.Tests;

public class NetworkPrefixTests
{
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.200.0.0/16")]
    [InlineData("192.168.1.5/32")]
    [InlineData("0.0.0.0/0")]
    [InlineData("fd00::/8")]
    public void ValidPrefixesParse(string text)
    {
        Assert.True(NetworkPrefix.TryParse(text, out NetworkPrefix prefix));
        Assert.Equal(text, prefix.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("garbage/16")]
    public void InvalidPrefixesDoNotParse(string? text)
    {
        Assert.False(NetworkPrefix.TryParse(text, out _));
    }

    [Fact]
    public void IPv6AllowsPrefixesBeyondThirtyTwo()
    {
        Assert.True(NetworkPrefix.TryParse("fd00::/64", out _));
    }

    [Theory]
    [InlineData("10.200.0.0/16", "10.200.50.21", true)]
    [InlineData("10.200.0.0/16", "10.200.0.0", true)]
    [InlineData("10.200.0.0/16", "10.200.255.255", true)]
    [InlineData("10.200.0.0/16", "10.201.0.1", false)]
    [InlineData("10.0.0.0/8", "10.200.50.21", true)]
    [InlineData("192.168.1.5/32", "192.168.1.5", true)]
    [InlineData("192.168.1.5/32", "192.168.1.6", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void ContainmentIsExact(string prefixText, string addressText, bool expected)
    {
        Assert.True(NetworkPrefix.TryParse(prefixText, out NetworkPrefix prefix));

        Assert.Equal(expected, prefix.Contains(IPAddress.Parse(addressText)));
    }

    [Theory]
    [InlineData("10.16.0.0/12", "10.16.0.1", true)]
    [InlineData("10.16.0.0/12", "10.31.255.255", true)]
    [InlineData("10.16.0.0/12", "10.32.0.1", false)]
    [InlineData("10.16.0.0/12", "10.15.255.255", false)]
    public void ContainmentHandlesPrefixesThatAreNotByteAligned(
        string prefixText, string addressText, bool expected)
    {
        Assert.True(NetworkPrefix.TryParse(prefixText, out NetworkPrefix prefix));

        Assert.Equal(expected, prefix.Contains(IPAddress.Parse(addressText)));
    }

    [Fact]
    public void AddressesOfADifferentFamilyAreNeverContained()
    {
        Assert.True(NetworkPrefix.TryParse("10.0.0.0/8", out NetworkPrefix prefix));

        Assert.False(prefix.Contains(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public void IPv4HostRouteIsASlashThirtyTwo()
    {
        Assert.Equal("192.0.2.10/32", NetworkPrefix.IPv4HostRoute(IPAddress.Parse("192.0.2.10")));
    }
}
