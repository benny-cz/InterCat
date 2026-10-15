using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// One other host's records as a scope (§6.3, §6.4): every record of the one-sided connections whose other end is one
/// address, whichever process held them, as the evidence policy admits their binding. Its key names the address -
/// "host:142.250.1.1" - so the timeline's focus, an evidence page and an interval's bytes read it where they read one
/// channel's. Who is at the address is not observed (P7).
/// </summary>
public static class HostScopeKeys
{
    /// <summary>What every host scope's key begins with.</summary>
    public const string Prefix = "host:";

    /// <summary>The key of the scope of the connections to <paramref name="address"/>, as every surface writes one.</summary>
    public static string Of(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return Prefix + address.Trim();
    }

    /// <summary>Whether a key names a host's scope.</summary>
    public static bool IsHost(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && key.Length > Prefix.Length;

    /// <summary>The address a host scope's key names.</summary>
    public static string AddressOf(string key) => IsHost(key)
        ? key[Prefix.Length..]
        : throw new ArgumentException("The key names no host's connections.", nameof(key));

    /// <summary>
    /// The channels of the one-sided connections a host scope's key names, by channel number, as a row's test reads them:
    /// those whose other end is its address - written as every surface writes one, compared ignoring case - and whose
    /// binding <paramref name="policy"/> admits. Null when none is.
    /// </summary>
    internal static bool[]? Channels(TransportRelationIndex relations, string key, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(relations);
        string address = AddressOf(key);
        int[] channels = [.. relations.OneSided
            .Where(connection => SessionOverviewProjector.Admitted(connection.Strength, policy)
                && string.Equals(EndpointAddress(connection.RemoteEndpoint), address, StringComparison.OrdinalIgnoreCase))
            .Select(connection => connection.Channel)];
        if (channels.Length == 0)
        {
            return null;
        }

        bool[] members = new bool[channels.Max() + 1];
        foreach (int channel in channels)
        {
            members[channel] = true;
        }

        return members;
    }

    /// <summary>An endpoint's address: inside the brackets of "[2001:db8::1]:443", before the last colon of "142.250.1.1:443".</summary>
    internal static string EndpointAddress(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.StartsWith('[') && endpoint.IndexOf(']', StringComparison.Ordinal) is > 0 and var close
            ? endpoint[1..close]
            : endpoint.LastIndexOf(':') is > 0 and var colon ? endpoint[..colon] : endpoint;
    }
}
