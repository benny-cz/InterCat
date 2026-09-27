using System.Globalization;
using System.Text;

namespace InterCat.Domain;

/// <summary>
/// How an endpoint reads wherever InterCat shows one. A row keeps an IPv4 address as a 32-bit number and an IPv6 address
/// as a 128-bit one, each the address's bytes in network order (`segment-v1` §5), and every surface writes them alike.
/// </summary>
public static class EndpointText
{
    /// <summary>An IPv4 address in dotted-decimal form.</summary>
    public static string Ipv4(uint address) => string.Create(
        CultureInfo.InvariantCulture,
        $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}");

    /// <summary>
    /// An IPv6 address in RFC 5952's canonical text: lowercase hexadecimal without leading zeros, the longest run of two
    /// or more zero groups written "::" (the first of equal runs), and an IPv4-mapped address as ::ffff:a.b.c.d.
    /// </summary>
    public static string Ipv6(UInt128 address)
    {
        if (address >> 32 == 0xFFFF) return "::ffff:" + Ipv4((uint)address);
        Span<ushort> groups = stackalloc ushort[8];
        for (int index = 0; index < groups.Length; index++) groups[index] = (ushort)(address >> (112 - (16 * index)));

        int runStart = -1;
        int runLength = 1;
        for (int index = 0; index < groups.Length;)
        {
            if (groups[index] != 0)
            {
                index++;
                continue;
            }

            int start = index;
            while (index < groups.Length && groups[index] == 0) index++;
            if (index - start > runLength) (runStart, runLength) = (start, index - start);
        }

        var text = new StringBuilder(39);
        for (int index = 0; index < groups.Length; index++)
        {
            if (index == runStart)
            {
                text.Append("::");
                index += runLength - 1;
                continue;
            }

            if (index > 0 && index != runStart + runLength) text.Append(':');
            text.Append(groups[index].ToString("x", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>An IPv4 endpoint, "a.b.c.d:port".</summary>
    public static string Endpoint(uint address, ushort port) =>
        Ipv4(address) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>An IPv6 endpoint, "[address]:port", bracketed as a URI writes one so the port cannot read as a group.</summary>
    public static string Endpoint(UInt128 address, ushort port) =>
        "[" + Ipv6(address) + "]:" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// An endpoint's text in at most <paramref name="maximumLength"/> characters. An IPv6 endpoint loses the middle of
    /// its address, never its port, because the port is what tells two ends of one host apart: "[2001:db8…70:7348]:50000".
    /// Any other text too long for the room loses its end. An IPv4 endpoint is at most 21 characters.
    /// </summary>
    public static string Abbreviated(string endpoint, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        if (endpoint.Length <= maximumLength)
        {
            return endpoint;
        }

        int close = endpoint.LastIndexOf("]:", StringComparison.Ordinal);
        int room = maximumLength - (endpoint.Length - close) - 2;
        if (!endpoint.StartsWith('[') || close < 1 || room < 2)
        {
            return endpoint[..(maximumLength - 1)] + "…";
        }

        int head = (room + 1) / 2;
        int tail = room - head;
        return "[" + endpoint[1..(1 + head)] + "…" + endpoint[(close - tail)..];
    }
}
