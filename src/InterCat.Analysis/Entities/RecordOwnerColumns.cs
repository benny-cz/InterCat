using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// The owner column of one segment read under `process-binding-v3` (`contracts/entities-v1.md` §2): the owner a record's
/// payload names, or, for a mechanism whose records are raised in the process they describe, the process that raised it
/// (<see cref="RecordAttribution"/>). The header column is read only in a segment where such a record names no owner, so
/// a segment of transfers, which always name theirs, reads no more than it did.
/// </summary>
internal ref struct RecordOwnerColumns
{
    private readonly SegmentReaderV1 segment;
    private readonly SegmentColumnSlice owners;
    private SegmentColumnSlice headers;
    private bool headed;

    public RecordOwnerColumns(SegmentReaderV1 segment)
    {
        this.segment = segment;
        owners = segment.Slice(SegmentColumnId.OwnerProcessId);
    }

    /// <summary>The PID row <paramref name="row"/> belongs to, given the mechanism the caller has read for it.</summary>
    public int? At(int row, Mechanism mechanism)
    {
        if (owners.SignedAt(row) is { } owner)
        {
            return (int)owner;
        }

        if (!RecordAttribution.RaisedByTheirProcess(mechanism))
        {
            return null;
        }

        if (!headed)
        {
            headers = segment.Slice(SegmentColumnId.HeaderProcessId);
            headed = true;
        }

        return (int)headers.SignedAt(row)!.Value;
    }
}
