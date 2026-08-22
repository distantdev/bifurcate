using System.Net;
using Bifurcate.Core;

namespace Bifurcate.Tests;

public class HostResolverTests
{
    [Fact]
    public void ADottedIPv4IsUsedWithoutDns()
    {
        IPAddress[] addresses = [.. HostResolver.ResolveIPv4("192.0.2.10")];

        Assert.Equal([IPAddress.Parse("192.0.2.10")], addresses);
        Assert.Equal("192.0.2.10/32", NetworkPrefix.IPv4HostRoute(addresses[0]));
    }

    [Fact]
    public void AnIPv6LiteralYieldsNoHostRoute()
    {
        Assert.Empty(HostResolver.ResolveIPv4("2001:db8::1"));
    }
}
