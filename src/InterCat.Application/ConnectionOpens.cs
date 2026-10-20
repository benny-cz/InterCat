using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Who made and who accepted each TCP connection, counted for each process instance as the capture saw the connection
/// open at either end (`relations-v1` §3, §5): a paired connection is one its connecting end made and its other end
/// accepted - a process connected to itself both - and a one-sided connection, most often to another host, is one its
/// holder made by a connect or accepted by an accept. A process's card, the ranking by connections made or accepted and
/// an export of it all read these, so they agree; the graph's arrowheads draw the same openings edge by edge.
/// </summary>
/// <remarks>
/// A connection the capture saw open neither way, or whose ends say both connected or both accepted, counts for neither
/// end, and so does one whose binding the evidence policy does not admit or whose two ends the capture could not tell
/// apart from another connection's: the counts are of what the evidence says, never a guess.
/// </remarks>
public static class ConnectionOpens
{
    /// <summary>
    /// Each instance's connections made and accepted under <paramref name="policy"/>, of those whose first record, at
    /// either end, has a native reading within <paramref name="native"/> - for a connection whose open the capture saw,
    /// its open - or of every one where it is null. An instance that made and accepted none is absent.
    /// </summary>
    public static Dictionary<ProcessInstanceId, (long Made, long Accepted)> Count(
        TransportRelationIndex relations,
        EvidencePolicy policy,
        TimeRange? native)
    {
        ArgumentNullException.ThrowIfNull(relations);
        var counts = new Dictionary<ProcessInstanceId, (long Made, long Accepted)>();
        foreach (TransportRelation relation in relations.Relations.Where(relation => Counted(relation, policy, native)))
        {
            bool firstConnected = relation.Opening == RelationOpening.FirstConnected;
            Add(counts, (firstConnected ? relation.First : relation.Second).Id, 1, 0);
            Add(counts, (firstConnected ? relation.Second : relation.First).Id, 0, 1);
        }

        foreach (TransportConnection connection in relations.OneSided.Where(connection => Counted(connection, policy, native)))
        {
            bool connected = connection.Opening == ConnectionOpening.Connected;
            Add(counts, connection.Holder.Id, connected ? 1 : 0, connected ? 0 : 1);
        }

        return counts;
    }

    /// <summary>
    /// One instance's connections made and accepted under <paramref name="policy"/>, as <see cref="Count"/> counts them over
    /// the whole session, end by end: one for each process at the other end of some - itself for one it made to itself,
    /// which it both made and accepted - in the order of their ids, and last one with no process for those whose other end
    /// no record holds. They add up to its count; an end it made and accepted none with is absent.
    /// </summary>
    public static IReadOnlyList<PeerConnections> With(TransportRelationIndex relations, EvidencePolicy policy, ProcessInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(relations);
        var peers = new Dictionary<ProcessInstanceId, (ProcessInstance Peer, long Made, long Accepted)>();
        foreach (TransportRelation relation in relations.Relations.Where(relation => Counted(relation, policy, null)))
        {
            (ProcessInstance connected, ProcessInstance accepted) = relation.Opening == RelationOpening.FirstConnected
                ? (relation.First, relation.Second)
                : (relation.Second, relation.First);
            if (connected.Id == instance)
            {
                Tally(accepted, 1, 0);
            }

            if (accepted.Id == instance)
            {
                Tally(connected, 0, 1);
            }
        }

        long made = 0;
        long acceptedUnheld = 0;
        foreach (TransportConnection connection in relations.OneSided
            .Where(connection => connection.Holder.Id == instance && Counted(connection, policy, null)))
        {
            bool connected = connection.Opening == ConnectionOpening.Connected;
            made += connected ? 1 : 0;
            acceptedUnheld += connected ? 0 : 1;
        }

        return
        [
            .. peers.Values
                .OrderBy(entry => entry.Peer.Id.ToString(), StringComparer.Ordinal)
                .Select(entry => new PeerConnections(entry.Peer, entry.Made, entry.Accepted)),
            .. made + acceptedUnheld > 0 ? [new PeerConnections(null, made, acceptedUnheld)] : Array.Empty<PeerConnections>(),
        ];

        void Tally(ProcessInstance peer, long madeTo, long acceptedFrom)
        {
            (ProcessInstance Peer, long Made, long Accepted) held = peers.GetValueOrDefault(peer.Id, (peer, 0, 0));
            peers[peer.Id] = (peer, held.Made + madeTo, held.Accepted + acceptedFrom);
        }
    }

    /// <summary>
    /// Whether a paired connection counts: a TCP connection whose opening the capture saw at one end alone, admitted by the
    /// policy, its first record within <paramref name="native"/>.
    /// </summary>
    private static bool Counted(TransportRelation relation, EvidencePolicy policy, TimeRange? native) =>
        relation.Mechanism == Mechanism.Tcp
        && relation.Opening != RelationOpening.NotWitnessed
        && SessionOverviewProjector.Admitted(relation.Strength, policy)
        && Within(native, relation.FirstNativeTicks);

    /// <summary>Whether a one-sided connection counts, as a paired one does.</summary>
    private static bool Counted(TransportConnection connection, EvidencePolicy policy, TimeRange? native) =>
        connection.Mechanism == Mechanism.Tcp
        && connection.Opening != ConnectionOpening.NotWitnessed
        && SessionOverviewProjector.Admitted(connection.Strength, policy)
        && Within(native, connection.FirstNativeTicks);

    /// <summary>
    /// Whether the session records any connection's open at all, at either end of a paired connection or at a one-sided
    /// one's: where it records none - a capture that collected no connect or accept, or an import whose source recorded none
    /// - how many connections a process made or accepted is unknown, never none (R21).
    /// </summary>
    public static bool Recorded(TransportRelationIndex relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        return relations.Relations.Any(relation => relation.Opening != RelationOpening.NotWitnessed || relation.OpenWitnessed)
            || relations.OneSided.Any(connection => connection.Opening != ConnectionOpening.NotWitnessed);
    }

    /// <summary>Whether a native reading is within a half-open interval, as a record's is; every reading is, with none.</summary>
    private static bool Within(TimeRange? native, long ticks) =>
        native is not { } range || (ticks >= range.StartTicks && ticks < range.EndTicks);

    private static void Add(Dictionary<ProcessInstanceId, (long Made, long Accepted)> counts, ProcessInstanceId process, long made, long accepted)
    {
        (long Made, long Accepted) held = counts.GetValueOrDefault(process);
        counts[process] = (held.Made + made, held.Accepted + accepted);
    }
}

/// <summary>
/// The TCP connections one process instance made and accepted with one other end, as <see cref="ConnectionOpens"/> counts
/// them: <see cref="Made"/> it connected to that end, and <see cref="Accepted"/> that end connected to it. <see cref="Peer"/>
/// is the process there, or null for its connections whose other end no record holds - a remote host's, or a process the
/// capture did not observe - which no process is at.
/// </summary>
public sealed record PeerConnections(ProcessInstance? Peer, long Made, long Accepted);
