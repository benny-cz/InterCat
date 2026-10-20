using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One other host's traffic (§6.3): the connections a census counts as to its address - exactly those its processes' host
/// links hold - the records they hold, and what their transfers measured each way, at this machine's end as a connection's
/// are. Who is at the address is not observed (P7).
/// </summary>
public sealed record HostTraffic(
    OtherHost Host,
    long Sends,
    long SentBytes,
    long UnmeasuredSends,
    long Receives,
    long ReceivedBytes,
    long UnmeasuredReceives)
{
    /// <summary>What its connections' transfers measured, both directions, in the words one connection's are said in.</summary>
    public string Transfers(IFormatProvider? culture = null) =>
        ConnectionSummary.TransfersOf(Sends, SentBytes, UnmeasuredSends, Receives, ReceivedBytes, UnmeasuredReceives, culture);

    /// <summary>
    /// What a ranking reads of it: its records, or the bytes its measured sends, receives or both carried, of how many, and
    /// how many stated no size, so a host none of whose transfers measured one is unmeasured, never a zero (R21). Null for a
    /// metric a host holds nothing of.
    /// </summary>
    public RankedValue? RankedBy(RankingMetric metric) => metric switch
    {
        RankingMetric.Records => new(metric, Host.Records, Host.Records, 0),
        RankingMetric.BytesSent => Ranked(metric, SentBytes, Sends, UnmeasuredSends),
        RankingMetric.BytesReceived => Ranked(metric, ReceivedBytes, Receives, UnmeasuredReceives),
        RankingMetric.EndpointBytes => Ranked(metric, SentBytes + ReceivedBytes, Sends + Receives,
            UnmeasuredSends + UnmeasuredReceives),
        _ => null,
    };

    private static RankedValue Ranked(RankingMetric metric, long bytes, long transfers, long unmeasured) =>
        new(metric, transfers > unmeasured ? bytes : null, transfers - unmeasured, unmeasured);
}

/// <summary>
/// What one process's connections over one transport to one other host carried (§6.3), at this machine's end, as its
/// sends and receives measured them: the address as the census writes it. Who is at the address is not observed (P7).
/// </summary>
public sealed record HostReachTraffic(ProcessInstanceId Process, Mechanism Mechanism, string Address, TransportBytes Bytes);

/// <summary>
/// What each other host's connections carried, read for a ranking of the hosts by it (§6.3, R18): the census names the
/// hosts and which connections are theirs, opening no segment; their bytes take one pass over the segments their
/// lifetimes meet.
/// </summary>
public static class SessionHostTraffic
{
    /// <summary>Whether hosts rank by <paramref name="metric"/>: their records, or the bytes their connections sent, received or both.</summary>
    public static bool Ranks(RankingMetric metric) => metric is RankingMetric.Records or RankingMetric.BytesSent
        or RankingMetric.BytesReceived or RankingMetric.EndpointBytes;

    /// <summary>
    /// Every host <paramref name="links"/> reached, with what its connections carried, ranked by <paramref name="metric"/>:
    /// those whose transfers measured it first, the largest first; then those whose transfers stated no size; then those
    /// that made none; ties by records, then connections, then address. Each host is counted as the census counts it -
    /// its connections made and accepted, and their ports - so a byte ranking lists what a records ranking does. Within
    /// <paramref name="interval"/> (workspace ticks) when one is given, where a connection, and a host, with no record in it
    /// is not listed.
    /// </summary>
    public static IReadOnlyList<HostTraffic> Of(
        SessionStore store,
        IReadOnlyCollection<HostLink> links,
        RankingMetric metric = RankingMetric.Records,
        TimeRange? interval = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(links);
        if (!Ranks(metric))
        {
            throw new ArgumentOutOfRangeException(nameof(metric), metric, "Hosts rank by their records or their bytes.");
        }

        // A host's connections are those its processes' host links hold: by holder, transport and address, as the census
        // counted them.
        HashSet<string> held = new(links.SelectMany(link => link.Addresses.Select(address => Key(link.Process, link.Mechanism,
            address.Address))), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<(TransportConnection Connection, ConnectionSummary Summary)> connections = held.Count == 0 ? []
            : SessionConnections.Held(store, connection => held.Contains(Key(connection.Holder.Id, connection.Mechanism,
                HostScopeKeys.EndpointAddress(connection.RemoteEndpoint))), interval, policy, cancellationToken);
        HostTraffic[] hosts = [.. connections
            .GroupBy(pair => HostScopeKeys.EndpointAddress(pair.Summary.RemoteEndpoint), StringComparer.OrdinalIgnoreCase)
            .Select(host => new HostTraffic(
                new OtherHost(
                    host.Key,
                    host.Count(pair => pair.Summary.Mechanism == Mechanism.Tcp),
                    host.Count(pair => pair.Summary.Mechanism == Mechanism.Udp),
                    host.Select(pair => pair.Connection.Holder.Id).Distinct().Count(),
                    host.Sum(pair => pair.Summary.Records))
                {
                    // As the census counts them: an accepted connection at its process's own port, which it listened at,
                    // and any other at the host's port, where it ran to.
                    Ports = [.. host.Where(pair => pair.Summary.Opening != ConnectionOpening.Accepted)
                        .Select(pair => PortOf(pair.Summary.RemoteEndpoint)).OfType<ushort>().Distinct().Order()],
                    Connected = host.Count(pair => pair.Summary.Opening == ConnectionOpening.Connected),
                    Accepted = host.Count(pair => pair.Summary.Opening == ConnectionOpening.Accepted),
                    AcceptedPorts = [.. host.Where(pair => pair.Summary.Opening == ConnectionOpening.Accepted)
                        .Select(pair => PortOf(pair.Summary.LocalEndpoint)).OfType<ushort>().Distinct().Order()],
                },
                host.Sum(pair => pair.Summary.Sends),
                host.Sum(pair => pair.Summary.SentBytes),
                host.Sum(pair => pair.Summary.UnmeasuredSends),
                host.Sum(pair => pair.Summary.Receives),
                host.Sum(pair => pair.Summary.ReceivedBytes),
                host.Sum(pair => pair.Summary.UnmeasuredReceives)))];
        Array.Sort(hosts, (left, right) => Compare(left, right, metric));
        return hosts;
    }

    /// <summary>
    /// What each process's connections over each transport to each address <paramref name="links"/> name carried, over the
    /// whole session, in one pass over the segments their lifetimes meet: one reach for each that holds any connection,
    /// the addresses as the census writes them. A graph's edge to other hosts sums its own (§6.3).
    /// </summary>
    public static IReadOnlyList<HostReachTraffic> OfReaches(
        SessionStore store,
        IReadOnlyCollection<HostLink> links,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(links);

        // Each reach is written as the census wrote its address, which the connections' own endpoints may write otherwise.
        Dictionary<string, (ProcessInstanceId Process, Mechanism Mechanism, string Address)> reaches = new(StringComparer.OrdinalIgnoreCase);
        foreach (HostLink link in links)
        {
            foreach (HostAddress address in link.Addresses)
            {
                reaches.TryAdd(Key(link.Process, link.Mechanism, address.Address), (link.Process, link.Mechanism, address.Address));
            }
        }

        if (reaches.Count == 0)
        {
            return [];
        }

        IReadOnlyList<(TransportConnection Connection, ConnectionSummary Summary)> connections = SessionConnections.Held(store,
            connection => reaches.ContainsKey(Key(connection.Holder.Id, connection.Mechanism,
                HostScopeKeys.EndpointAddress(connection.RemoteEndpoint))), null, policy, cancellationToken);
        return [.. connections
            .GroupBy(pair => Key(pair.Connection.Holder.Id, pair.Connection.Mechanism,
                HostScopeKeys.EndpointAddress(pair.Connection.RemoteEndpoint)), StringComparer.OrdinalIgnoreCase)
            .Select(reach => (Reach: reaches[reach.Key], Bytes: reach.Aggregate(TransportBytes.None, (sum, pair) => sum.Plus(new(
                pair.Summary.SentBytes, pair.Summary.Sends - pair.Summary.UnmeasuredSends, pair.Summary.UnmeasuredSends,
                pair.Summary.ReceivedBytes, pair.Summary.Receives - pair.Summary.UnmeasuredReceives,
                pair.Summary.UnmeasuredReceives)))))
            .Select(reach => new HostReachTraffic(reach.Reach.Process, reach.Reach.Mechanism, reach.Reach.Address, reach.Bytes))];
    }

    /// <summary>
    /// A ranking's order (§5.2): measured first, the largest first, a measured zero among them; then those whose records
    /// stated no size; then those with none; ties by records, connections and address, as the busiest hosts are named.
    /// </summary>
    private static int Compare(HostTraffic left, HostTraffic right, RankingMetric metric)
    {
        static int Tier(RankedValue value) => value.Value is not null ? 0 : value.Holds ? 1 : 2;
        RankedValue one = left.RankedBy(metric)!, other = right.RankedBy(metric)!;
        int order = Tier(one).CompareTo(Tier(other));
        if (order == 0) order = (other.Value ?? 0).CompareTo(one.Value ?? 0);
        if (order == 0) order = right.Host.Records.CompareTo(left.Host.Records);
        if (order == 0)
        {
            order = (right.Host.TcpConnections + right.Host.UdpFlows).CompareTo(left.Host.TcpConnections + left.Host.UdpFlows);
        }

        return order != 0 ? order : string.CompareOrdinal(left.Host.Address, right.Host.Address);
    }

    private static string Key(ProcessInstanceId process, Mechanism mechanism, string address) =>
        string.Create(CultureInfo.InvariantCulture, $"{process}|{(int)mechanism}|{address}");

    /// <summary>The port an endpoint names after its last colon: 443 of "142.250.1.1:443" and of "[2001:db8::1]:443".</summary>
    private static ushort? PortOf(string endpoint) =>
        endpoint.LastIndexOf(':') is > 0 and var colon
        && ushort.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port)
            ? port
            : null;
}
