using InterCat.Application;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// An other host's address scope (§6.3): the range its standard puts it in - a multicast group's, the broadcast address, a
/// link-local or a private network's - read from the address alone, never a guess at who is at it (P7).
/// </summary>
public sealed class AddressScopeTests
{
    [Theory(DisplayName = "§6.3: an address's scope is the range its standard puts it in, at each range's edges, an IPv4-mapped address by its IPv4 address")]
    [InlineData("224.0.0.0", "multicast")]
    [InlineData("224.0.0.251", "multicast")]
    [InlineData("239.255.255.250", "multicast")]
    [InlineData("223.255.255.255", null)]
    [InlineData("240.0.0.1", null)]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("255.255.255.254", null)]
    [InlineData("169.254.0.1", "link-local")]
    [InlineData("169.253.255.255", null)]
    [InlineData("10.0.0.1", "private")]
    [InlineData("11.0.0.1", null)]
    [InlineData("172.16.0.1", "private")]
    [InlineData("172.31.255.255", "private")]
    [InlineData("172.15.255.255", null)]
    [InlineData("172.32.0.1", null)]
    [InlineData("192.168.1.20", "private")]
    [InlineData("192.169.0.1", null)]
    [InlineData("8.8.8.8", null)]
    [InlineData("ff02::fb", "multicast")]
    [InlineData("FF05::1:3", "multicast")]
    [InlineData("fe80::1", "link-local")]
    [InlineData("febf::1", "link-local")]
    [InlineData("fec0::1", null)]
    [InlineData("fc00::1", "private")]
    [InlineData("fd12:3456::1", "private")]
    [InlineData("fe00::1", null)]
    [InlineData("2001:db8::1", null)]
    [InlineData("::ffff:224.0.0.251", "multicast")]
    [InlineData("::ffff:10.0.0.1", "private")]
    [InlineData("::ffff:142.250.1.1", null)]
    [InlineData("not an address", null)]
    public void AnAddressScopeIsItsStandardsRange(string address, string? scope) => Assert.Equal(scope, AddressScope.Of(address));

    [Fact(DisplayName = "§6.3: what an address's scope means is said for who receives what is sent there, and nothing for an address with none")]
    public void AScopeSaysWhoReceives()
    {
        Assert.Equal("A multicast group's address: whoever joined the group receives what is sent to it",
            AddressScope.Meaning("239.255.255.250"));
        Assert.Equal("The broadcast address: every host of the network receives what is sent to it", AddressScope.Meaning("255.255.255.255"));
        Assert.Equal("A link-local address: a host on this machine's own network link", AddressScope.Meaning("fe80::1"));
        Assert.Equal("A private network's address, never the internet's", AddressScope.Meaning("192.168.1.20"));
        Assert.Null(AddressScope.Meaning("142.250.1.1"));
        Assert.Throws<ArgumentNullException>(() => AddressScope.Of(null!));
    }
}
