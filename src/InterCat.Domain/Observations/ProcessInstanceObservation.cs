namespace InterCat.Domain;

/// <summary>
/// One admitted process lifecycle record. The start key is the sequence number with the creation time, so
/// a reused PID never merges two instances (I12, R22). A rundown record is witnessed presence, not a
/// creation time (section 18.5).
/// </summary>
public sealed record ProcessInstanceObservation
{
    public required ObservationId Id { get; init; }
    public required ObservationKind Kind { get; init; }
    public required long TimestampUtcTicks { get; init; }
    public required int ProcessId { get; init; }
    public long? SequenceNumber { get; init; }
    public long? CreateFileTime { get; init; }
    public long? ExitFileTime { get; init; }
    public int? ParentProcessId { get; init; }
    public long? ExitCode { get; init; }
}
