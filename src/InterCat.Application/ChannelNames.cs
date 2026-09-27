using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// How a channel is named: its two ends' endpoints, the one that sorts first first. A name is shown whole wherever there
/// is room for it, and a chip too narrow for two IPv6 endpoints shows each abbreviated, keeping its port.
/// </summary>
public static class ChannelNames
{
    public const string Separator = " ↔ ";

    /// <summary>The characters each endpoint keeps in an abbreviated name; an IPv4 endpoint always fits.</summary>
    public const int AbbreviatedEndpointLength = 24;

    public static string Of(string firstEndpoint, string secondEndpoint)
    {
        ArgumentNullException.ThrowIfNull(firstEndpoint);
        ArgumentNullException.ThrowIfNull(secondEndpoint);
        return firstEndpoint + Separator + secondEndpoint;
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
}
