namespace InterCat.Domain;

/// <summary>
/// One Unix-domain socket row as a coverage evaluation reads it (ADR-074): a call Winsock AFD's gate admitted, owned by
/// the process its socket's create named, its socket numbered in the capture rather than named by a kernel address
/// (R22). A transfer's bytes are its call's completion, in the <c>CompletedIo</c> domain; a row of no transfer carries
/// none, which is not a zero (R3).
/// </summary>
public sealed record UnixSocketObservation
{
    /// <summary>The record's ordinal in its capture, which orders two rows of one reading.</summary>
    public required long RecordOrdinal { get; init; }

    /// <summary>When the record was raised, on the wall clock its capture's calibration reads it at.</summary>
    public required long TimestampUtcTicks { get; init; }

    /// <summary>The process its socket's create named, whichever thread raised the record.</summary>
    public required int OwnerProcessId { get; init; }

    public required ObservationKind Kind { get; init; }

    /// <summary>The socket's number in the capture: one incarnation, from its create to its close (ADR-073).</summary>
    public required long Socket { get; init; }

    /// <summary>The bytes a send or receive completed; null for a row of no transfer.</summary>
    public long? Bytes { get; init; }

    public long? Status { get; init; }
}
