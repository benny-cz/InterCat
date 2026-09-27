using System.Buffers.Binary;
using System.Net;
using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>How an endpoint reads: dotted decimal for IPv4, RFC 5952's canonical text for IPv6, a bracketed IPv6 endpoint.</summary>
public sealed class EndpointTextTests
{
    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8::5ec:1")]
    [InlineData("fe80::1:2")]
    [InlineData("1:2:3:4:5:6::")]
    [InlineData("1:0:2:3:4:5:6:7")]
    [InlineData("2001:0:0:1::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348")]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void AnIpv6AddressReadsInItsCanonicalText(string canonical)
    {
        Assert.Equal(canonical, EndpointText.Ipv6(Number(canonical)));
    }

    [Theory]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
    [InlineData("0:0:0:0:0:0:0:0", "::")]
    [InlineData("::ffff:c000:0201", "::ffff:192.0.2.1")]
    public void AnyTextOfAnAddressReadsAsTheSameCanonicalText(string written, string canonical)
    {
        // Leading zeros go, hexadecimal is lowercase, the first of two equal zero runs is the one compressed, and an
        // IPv4-mapped address shows its IPv4 part (RFC 5952 §4 and §5).
        Assert.Equal(canonical, EndpointText.Ipv6(Number(written)));
    }

    [Fact]
    public void AnEndpointReadsWithItsPortAndAnIpv6OneIsBracketed()
    {
        Assert.Equal("127.0.0.1:8443", EndpointText.Endpoint(0x7F00_0001u, 8443));
        Assert.Equal("255.255.255.255:0", EndpointText.Endpoint(uint.MaxValue, 0));
        Assert.Equal("0.0.0.0", EndpointText.Ipv4(0));
        Assert.Equal("[::1]:443", EndpointText.Endpoint(UInt128.One, 443));
        Assert.Equal("[2001:db8::1]:50000", EndpointText.Endpoint(Number("2001:db8::1"), 50_000));
    }

    [Fact]
    public void AnAbbreviatedEndpointKeepsItsPortAndTheEndsOfItsAddress()
    {
        const string global = "[2001:db8:85a3:8d3:1319:8a2e:370:7348]:50000";

        // The port tells a looped process's ends apart, so it survives; the address keeps its prefix and suffix.
        Assert.Equal("[2001:db8…70:7348]:50000", EndpointText.Abbreviated(global, 24));
        Assert.Equal(24, EndpointText.Abbreviated(global, 24).Length);
        Assert.EndsWith("]:50000", EndpointText.Abbreviated(global, 12), StringComparison.Ordinal);

        // What fits is unchanged: every IPv4 endpoint, and a short IPv6 one.
        Assert.Equal("255.255.255.255:65535", EndpointText.Abbreviated("255.255.255.255:65535", 24));
        Assert.Equal("[::1]:8080", EndpointText.Abbreviated("[::1]:8080", 24));

        // The port stays while one character of each end of the address fits. With less room, or for text that is not a
        // bracketed endpoint, the text loses its end instead.
        Assert.Equal("[2…8]:50000", EndpointText.Abbreviated(global, 11));
        Assert.Equal("[2001:db8…", EndpointText.Abbreviated(global, 10));
        Assert.Equal("not an endpoint at…", EndpointText.Abbreviated("not an endpoint at all", 19));
    }

    private static UInt128 Number(string address) =>
        BinaryPrimitives.ReadUInt128BigEndian(IPAddress.Parse(address).GetAddressBytes());
}
