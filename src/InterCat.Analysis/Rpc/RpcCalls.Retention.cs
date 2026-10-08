using System.Runtime.InteropServices;
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
        var collected = new List<CallRecord>();
        var interfaces = new List<Guid>();
        var interfaceIndex = new Dictionary<Guid, int>();
        var fields = new Dictionary<RecordAddress, FieldSlot>();
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Collect(segments[position], position, fields, interfaces, interfaceIndex, collected);
        }

        int[] order = CanonicalOrder(collected, cancellationToken);
        var groups = new List<int[]>();
        _ = Walk(CollectionsMarshal.AsSpan(collected), order, cancellationToken, groups);
        var dependencies = new ReleaseDependencies(released);
        foreach (int[] group in groups)
        {
            dependencies.Together([.. group.Select(rank => RowOf(collected[order[rank]].Address))]);
        }

        return dependencies;
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
