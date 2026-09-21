namespace InterCat.Domain;

public readonly record struct CaptureId(Guid Value)
{
    public static CaptureId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ProcessInstanceId(Guid Value)
{
    public static ProcessInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct RawRecordId(
    CaptureId CaptureId,
    uint StreamId,
    uint SourceEpoch,
    ulong RecordOrdinal);

public readonly record struct ObservationId(RawRecordId RawRecordId, ushort FactIndex);
