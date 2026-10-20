using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// How a channel is named: its two ends' endpoints, the one that sorts first first. A name is shown whole wherever there
/// is room for it, and a chip too narrow for two IPv6 endpoints shows each abbreviated, keeping its port.
/// </summary>
public static class ChannelNames
{
    public const string Separator = " ↔ ";

    /// <summary>What joins a connection's two ends where the capture saw which made it: an arrow at the end connected to.</summary>
    public const string Made = " → ";

    /// <summary>The characters each endpoint keeps in an abbreviated name; an IPv4 endpoint always fits.</summary>
    public const int AbbreviatedEndpointLength = 24;

    public static string Of(string firstEndpoint, string secondEndpoint)
    {
        ArgumentNullException.ThrowIfNull(firstEndpoint);
        ArgumentNullException.ThrowIfNull(secondEndpoint);
        return firstEndpoint + Separator + secondEndpoint;
    }

    /// <summary>
    /// A paired channel as a list of them reads it (§7.1, R5): the processes at its two ends, the one that made the
    /// connection first with an arrow at the one it connected to, as the overview names a relationship's, or joined by
    /// "and" where the capture saw neither open it, as for a UDP flow; then its transport, and its endpoints in the same
    /// order, compacted - "client.exe · PID 100 → server.exe · PID 200 · TCP · :50000 → :8080 on 127.0.0.1".
    /// <paramref name="nameOf"/> names a process, or gives null for one it cannot.
    /// </summary>
    public static string Ends(Channel channel, Func<ProcessInstanceId, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(nameOf);

        // The end the connection was made from leads: its endpoint, which its maker held, is one of the name's two.
        int split = channel.Name.IndexOf(Separator, StringComparison.Ordinal);
        bool made = channel.ConnectedFrom is not null && split > 0;
        bool swap = made && string.Equals(channel.Name[(split + Separator.Length)..], channel.ConnectedFrom, StringComparison.Ordinal);
        string first = Named(swap ? channel.SecondHolder : channel.FirstHolder);
        string second = Named(swap ? channel.FirstHolder : channel.SecondHolder);
        string endpoints = Compact(channel.Name, swap);
        return first + (made ? Made : " and ") + second + " · " + MechanismText.Name(channel.Mechanism) + " · "
            + (made ? endpoints.Replace(Separator, Made, StringComparison.Ordinal) : endpoints);

        string Named(ProcessInstanceId? holder) => holder is { } id ? nameOf(id) ?? "an unlisted process" : "an unknown process";
    }

    /// <summary>
    /// A channel name with each endpoint abbreviated to <see cref="AbbreviatedEndpointLength"/> characters, the middle of
    /// an address going before its port (<see cref="EndpointText.Abbreviated"/>). Text that is not one name of two ends
    /// is returned as it was.
    /// </summary>
    public static string Abbreviated(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int split = name.IndexOf(Separator, StringComparison.Ordinal);
        if (split < 0 || name.IndexOf(Separator, split + Separator.Length, StringComparison.Ordinal) >= 0)
        {
            return name;
        }

        return Of(
            EndpointText.Abbreviated(name[..split], AbbreviatedEndpointLength),
            EndpointText.Abbreviated(name[(split + Separator.Length)..], AbbreviatedEndpointLength));
    }

    /// <summary>
    /// A channel name for a narrow row. The ports lead, since they are what tell one host's channels apart, and a host
    /// both ends share is said once after them: "127.0.0.1:50000 ↔ 127.0.0.1:8080" reads ":50000 ↔ :8080 on 127.0.0.1".
    /// Ends on two hosts are abbreviated as <see cref="Abbreviated"/> does. <paramref name="swap"/> names the second end
    /// first, so a holder's own end can lead. Text that is not one name of two ends is returned as it was.
    /// </summary>
    public static string Compact(string name, bool swap = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        int split = name.IndexOf(Separator, StringComparison.Ordinal);
        if (split < 0 || name.IndexOf(Separator, split + Separator.Length, StringComparison.Ordinal) >= 0)
        {
            return name;
        }

        string first = name[..split];
        string second = name[(split + Separator.Length)..];
        if (swap)
        {
            (first, second) = (second, first);
        }

        // An IPv6 endpoint is bracketed, so its port follows the last colon as an IPv4 endpoint's does.
        int firstPort = first.LastIndexOf(':');
        int secondPort = second.LastIndexOf(':');
        return firstPort > 0 && secondPort > 0 && first.AsSpan(0, firstPort).SequenceEqual(second.AsSpan(0, secondPort))
            ? $"{first[firstPort..]}{Separator}{second[secondPort..]} on {first[..firstPort]}"
            : Of(EndpointText.Abbreviated(first, AbbreviatedEndpointLength), EndpointText.Abbreviated(second, AbbreviatedEndpointLength));
    }
}
