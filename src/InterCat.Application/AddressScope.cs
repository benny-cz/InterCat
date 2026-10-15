using System.Net;
using System.Net.Sockets;

namespace InterCat.Application;

/// <summary>
/// The range an address another host's connections ran to falls in, as its standard defines it (§6.3): a multicast
/// group's, the network's broadcast, a link-local address or a private network's. It is read from the address alone, so
/// it is a fact of the address and never a guess at who is at it (P7); a public address has none. A multicast group is
/// no one host: whoever joined it receives what is sent to it.
/// </summary>
public static class AddressScope
{
    /// <summary>
    /// The scope of <paramref name="address"/>, IPv4 dotted or IPv6 in RFC 5952's text, an IPv4-mapped address by its IPv4
    /// address: "multicast" (224.0.0.0/4, ff00::/8), "broadcast" (255.255.255.255), "link-local" (169.254.0.0/16,
    /// fe80::/10) or "private" (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, fc00::/7); null for any other address, and for
    /// text that is none. A network's own broadcast address is not known from its address alone, so it has none.
    /// </summary>
    public static string? Of(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IPAddress.TryParse(address, out IPAddress? parsed))
        {
            return null;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        byte[] bytes = parsed.GetAddressBytes();
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] >= 224 && bytes[0] <= 239 ? "multicast"
                : bytes.All(part => part == 255) ? "broadcast"
                : bytes[0] == 169 && bytes[1] == 254 ? "link-local"
                : bytes[0] == 10 || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) || (bytes[0] == 192 && bytes[1] == 168) ? "private"
                : null;
        }

        return bytes[0] == 0xFF ? "multicast"
            : bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80 ? "link-local"
            : (bytes[0] & 0xFE) == 0xFC ? "private"
            : null;
    }

    /// <summary>
    /// What a scope means for who received what was sent there, for a host's card and description: "A multicast group's
    /// address: whoever joined the group receives what is sent to it". Null for an address with none.
    /// </summary>
    public static string? Meaning(string address) => Of(address) switch
    {
        "multicast" => "A multicast group's address: whoever joined the group receives what is sent to it",
        "broadcast" => "The broadcast address: every host of the network receives what is sent to it",
        "link-local" => "A link-local address: a host on this machine's own network link",
        "private" => "A private network's address, never the internet's",
        _ => null,
    };
}
