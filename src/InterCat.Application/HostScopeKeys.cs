using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// One other host's records as a scope (§6.3, §6.4): every record of the one-sided connections whose other end is one
/// address, whichever process held them, as the evidence policy admits their binding. Its key names the address -
/// "host:142.250.1.1" - so the timeline's focus, an evidence page and an interval's bytes read it where they read one
/// channel's. A graph's edge to other hosts names its hosts, and the processes whose connections ran to them -
/// "host:142.250.1.1,20.42.65.1@processes:…" - so its records are its own connections', the processes' alone. Who is at
/// an address is not observed (P7).
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

    // Between a key's addresses, and before the processes that held its connections.
    private const char Separator = ',';
    private const char HoldersMark = '@';

    /// <summary>
    /// The key of the scope of the connections to any of <paramref name="addresses"/>, each written as every surface
    /// writes one, held by any of <paramref name="holders"/> where some are named, else by any process: a graph's edge to
    /// other hosts. One address and no holder make <see cref="Of(string)"/>'s key.
    /// </summary>
    public static string Of(IEnumerable<string> addresses, IReadOnlyCollection<ProcessInstanceId>? holders = null)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        string[] named = [.. addresses.Select(address => address.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        if (named.Length == 0 || named.Any(address => address.Length == 0 || address.Contains(Separator, StringComparison.Ordinal)
            || address.Contains(HoldersMark, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A host scope names one or more addresses, none empty.", nameof(addresses));
        }

        return Prefix + string.Join(Separator, named)
            + (holders is { Count: > 0 } ? HoldersMark + ProcessSetFilter.KeyOf(holders) : string.Empty);
    }

    /// <summary>Whether a key names a host's scope.</summary>
    public static bool IsHost(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && key.Length > Prefix.Length;

    /// <summary>The address a host scope's key names; for a key of several, each of them, a comma between.</summary>
    public static string AddressOf(string key) => string.Join(Separator, AddressesOf(key));

    /// <summary>The addresses a host scope's key names: one host's, or each of an edge's.</summary>
    public static IReadOnlyList<string> AddressesOf(string key) => IsHost(key)
        ? key[Prefix.Length..(key.IndexOf(HoldersMark, StringComparison.Ordinal) is >= 0 and var mark ? mark : key.Length)]
            .Split(Separator)
        : throw new ArgumentException("The key names no host's connections.", nameof(key));

    /// <summary>The processes whose connections a host scope's key names; none where it names any process's.</summary>
    public static IReadOnlyList<ProcessInstanceId> HoldersOf(string key) =>
        IsHost(key) && key.IndexOf(HoldersMark, StringComparison.Ordinal) is >= 0 and var mark
        && ProcessSetFilter.TryParse(key[(mark + 1)..], out ProcessInstanceId[] holders)
            ? holders
            : [];

    /// <summary>A host scope's key without the processes it names: its hosts' connections, whichever process held them.</summary>
    public static string HostsOf(string key) => Prefix + AddressOf(key);

    /// <summary>The hosts a host scope's key names, in words: its address, or how many hosts.</summary>
    public static string Named(string key) => AddressesOf(key) is [{ } only] ? only : CountText.Of(AddressesOf(key).Count, "host");

    /// <summary>
    /// The channels of the one-sided connections a host scope's key names, by channel number, as a row's test reads them:
    /// those whose other end is one of its addresses - written as every surface writes one, compared ignoring case - and
    /// whose binding <paramref name="policy"/> admits; the processes it names are an owner scope of their own. Null when
    /// none is.
    /// </summary>
    internal static bool[]? Channels(TransportRelationIndex relations, string key, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(relations);
        IReadOnlyList<string> addresses = AddressesOf(key);
        int[] channels = [.. relations.OneSided
            .Where(connection => SessionOverviewProjector.Admitted(connection.Strength, policy)
                && addresses.Any(address => Reaches(connection, address)))
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

    /// <summary>
    /// Whether a one-sided connection's other end is <paramref name="address"/>, written as every surface writes one and
    /// compared ignoring case: the one test of which connections a host's scope holds, wherever they are read.
    /// </summary>
    internal static bool Reaches(TransportConnection connection, string address) =>
        string.Equals(EndpointAddress(connection.RemoteEndpoint), address, StringComparison.OrdinalIgnoreCase);

    /// <summary>An endpoint's address: inside the brackets of "[2001:db8::1]:443", before the last colon of "142.250.1.1:443".</summary>
    internal static string EndpointAddress(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.StartsWith('[') && endpoint.IndexOf(']', StringComparison.Ordinal) is > 0 and var close
            ? endpoint[1..close]
            : endpoint.LastIndexOf(':') is > 0 and var colon ? endpoint[..colon] : endpoint;
    }
}
