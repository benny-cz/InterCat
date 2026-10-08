using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class ProcessInstanceIndex
{
    /// <summary>
    /// Adds to <paramref name="kept"/> the released rows an interval release keeps so that every process
    /// <paramref name="processIds"/> names keeps the instances the whole capture gave it (ADR-043): every lifecycle record
    /// of its PID, which decides each instance's lifetime, epoch, key, name and exit; and, for a PID no lifecycle record
    /// names, the first record that names it, which witnesses its one instance and keys it. A parent an instance links to
    /// is kept the same way, and its own parent, so a retained process keeps its ancestry. Instances are built from these
    /// alone (`entities-v1` §3), so every record that still names one of these PIDs binds as it did before the release.
    /// </summary>
    /// <param name="released">Whether the record with this ordinal is released; only a released row is added.</param>
    internal void KeepAcross(IEnumerable<int> processIds, Func<ulong, bool> released, HashSet<RowAddress> kept)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(released);
        ArgumentNullException.ThrowIfNull(kept);
        var pids = new HashSet<int>(processIds);
        var pending = new Queue<int>(pids);
        while (pending.TryDequeue(out int pid))
        {
            if (!instancesByPid.TryGetValue(pid, out int[]? indexes))
            {
                continue;
            }

            foreach (int index in indexes)
            {
                if (Instances[index].Parent is { } parent && Find(parent) is { } linked && pids.Add(linked.ProcessId))
                {
                    pending.Enqueue(linked.ProcessId);
                }
            }
        }

        var witnessedByLifecycle = new HashSet<int>();
        foreach (LifecycleRow row in evidence.Lifecycle)
        {
            _ = witnessedByLifecycle.Add(row.ProcessId);
            if (pids.Contains(row.ProcessId) && released(row.Record.Ordinal))
            {
                _ = kept.Add(RowOf(row.Record));
            }
        }

        foreach ((int pid, RecordKey witness) in evidence.FirstActivity)
        {
            if (pids.Contains(pid) && !witnessedByLifecycle.Contains(pid) && released(witness.Ordinal))
            {
                _ = kept.Add(RowOf(witness));
            }
        }
    }

    /// <summary>The row a record is, as an interval release names the rows it keeps.</summary>
    private static RowAddress RowOf(RecordKey record) => new(record.Stream, record.Epoch, record.Ordinal, record.FactKey);
}

/// <summary>
/// One row of a capture: its raw locator and fact key, without the capture and derivation every row of one generation
/// shares. An interval release names the rows it keeps of the records it releases by it (ADR-043).
/// </summary>
internal readonly record struct RowAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey)
{
    /// <summary>The row at <paramref name="row"/> of <paramref name="segment"/>, an observation or a field segment's.</summary>
    public static RowAddress At(SegmentReaderV1 segment, int row)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new(
            (uint)segment.Slice(SegmentColumnId.RawStreamId).UnsignedAt(row)!.Value,
            (uint)segment.Slice(SegmentColumnId.RawSourceEpoch).UnsignedAt(row)!.Value,
            segment.Slice(SegmentColumnId.RawRecordOrdinal).UnsignedAt(row)!.Value,
            new FactKey(
                segment.Slice(SegmentColumnId.FactKeyHigh).UnsignedAt(row)!.Value,
                segment.Slice(SegmentColumnId.FactKeyLow).UnsignedAt(row)!.Value));
    }
}
