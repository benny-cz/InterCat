using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class RpcCallIndex
{
    /// <summary>
    /// What an interval release owes the calls of <paramref name="segments"/> (ADR-043): each call, and each ambiguous run
    /// of one, stays whole, so a call open across the boundary keeps its start and pairs, times and names its interface as
    /// before, and every later record of its key pairs as it did (`operations-v1` §3). A call whose records all go takes
    /// nothing from its key's walk: it opened and closed it. Pairing reads the records' keys and order alone, so neither
    /// their fields nor their processes' instances are read here.
    /// </summary>
    internal static ReleaseDependencies ReleaseDependenciesOf(
        IReadOnlyList<SegmentReaderV1> segments,
        Func<ulong, bool> released,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var dependencies = new ReleaseDependencies(released);

        // Only a call or ambiguous run one of whose records goes owes the release anything, and only one with an activity
        // id has records of its own to keep. A key's records pair apart from every other key's (§3), so read just the keys
        // a released record names, every record of each: their calls pair as they do among all, and a release of a
        // window's oldest stretch reads a fraction of the session's calls rather than every one (revision 871).
        HashSet<CallKey> keys = KeysReleased(segments, released, cancellationToken);
        if (keys.Count == 0)
        {
            return dependencies;
        }

        // A key's call is a start and a stop, so the records are sized for two a key, and a few more for ambiguous runs: a
        // list grown by doubling would ask for one contiguous array twice its records at the end (revision 871).
        var collected = new List<CallRecord>((keys.Count * 2) + (keys.Count >> 6) + 1);
        var interfaces = new List<Guid>();
        var interfaceIndex = new Dictionary<Guid, int>();
        var fields = new Dictionary<RecordAddress, FieldSlot>();
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Collect(segments[position], position, fields, interfaces, interfaceIndex, collected, keys);
        }

        int[] order = CanonicalOrder(collected, cancellationToken);
        var groups = new List<int[]>();
        _ = Walk(CollectionsMarshal.AsSpan(collected), order, cancellationToken, groups);
        foreach (int[] group in groups)
        {
            dependencies.Together([.. group.Select(rank => RowOf(collected[order[rank]].Address))]);
        }

        return dependencies;
    }

    /// <summary>The keys of the call records <paramref name="released"/> gives up that carry an activity id, which calls pair by.</summary>
    private static HashSet<CallKey> KeysReleased(
        IReadOnlyList<SegmentReaderV1> segments,
        Func<ulong, bool> released,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<CallKey>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            bool opened = false;
            SegmentColumnSlice kinds = default;
            SegmentColumnSlice directions = default;
            SegmentColumnSlice ordinals = default;
            SegmentColumnSlice activities = default;
            var owners = new RecordOwnerColumns(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Rpc)
                {
                    continue;
                }

                if (!opened)
                {
                    kinds = segment.Slice(SegmentColumnId.ObservationKind);
                    directions = segment.Slice(SegmentColumnId.Direction);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    activities = segment.Slice(SegmentColumnId.ActivityId);
                    opened = true;
                }

                // A call record as Collect reads one: a side, a start or a stop, and the process it belongs to.
                RpcCallSide? side = (Direction)directions.UnsignedAt(row)!.Value switch
                {
                    Direction.Outbound => RpcCallSide.Client,
                    Direction.Inbound => RpcCallSide.Server,
                    _ => null,
                };
                if (released(ordinals.UnsignedAt(row)!.Value)
                    && side is { } callSide
                    && (ObservationKind)kinds.UnsignedAt(row)!.Value is ObservationKind.RequestStart or ObservationKind.RequestEnd
                    && owners.At(row, Mechanism.Rpc) is { } processId
                    && activities.GuidAt(row) is { } activity)
                {
                    _ = keys.Add(new(processId, callSide, activity));
                }
            }
        }

        return keys;
    }

    /// <summary>Where a call's records are: its start's row and its stop's, those it has.</summary>
    internal IEnumerable<(int Segment, int Row)> RowsAt(int call)
    {
        Entry entry = entries[call];
        if (entry.StartSegment >= 0)
        {
            yield return (entry.StartSegment, entry.StartRow);
        }

        if (entry.StopSegment >= 0)
        {
            yield return (entry.StopSegment, entry.StopRow);
        }
    }

    private static RowAddress RowOf(RecordAddress address) => new(address.Stream, address.Epoch, address.Ordinal, address.FactKey);
}
