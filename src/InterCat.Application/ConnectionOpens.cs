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
internal static class ConnectionOpens
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
        foreach (TransportRelation relation in relations.Relations)
        {
            if (relation.Mechanism != Mechanism.Tcp
                || relation.Opening == RelationOpening.NotWitnessed
                || !SessionOverviewProjector.Admitted(relation.Strength, policy)
                || !Within(native, relation.FirstNativeTicks))
            {
                continue;
            }

            bool firstConnected = relation.Opening == RelationOpening.FirstConnected;
            Add(counts, (firstConnected ? relation.First : relation.Second).Id, 1, 0);
            Add(counts, (firstConnected ? relation.Second : relation.First).Id, 0, 1);
        }

        foreach (TransportConnection connection in relations.OneSided)
        {
            if (connection.Mechanism != Mechanism.Tcp
                || connection.Opening == ConnectionOpening.NotWitnessed
                || !SessionOverviewProjector.Admitted(connection.Strength, policy)
                || !Within(native, connection.FirstNativeTicks))
            {
                continue;
            }

            bool connected = connection.Opening == ConnectionOpening.Connected;
            Add(counts, connection.Holder.Id, connected ? 1 : 0, connected ? 0 : 1);
        }

        return counts;
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
