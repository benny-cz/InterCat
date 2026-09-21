namespace InterCat.Domain;

/// <summary>
/// One admitted named-pipe operation, normalised for the M0 feasibility measurement. Requested and
/// completed bytes are separate domains and are never merged (P3, I6). A resource name is present only
/// when the capture observed the create that named it; otherwise it stays null rather than guessed (R21).
/// The durable observation schema of section 7.3 replaces this shape in M1 (IC-013).
/// </summary>
public sealed record PipeOperationObservation
{
    public required ObservationId Id { get; init; }
    public required ObservationKind Kind { get; init; }
    public required Direction Direction { get; init; }
    public required long TimestampUtcTicks { get; init; }
    public required long SourceTicks { get; init; }

    /// <summary>The process the event header attributes the operation to.</summary>
    public required int ProcessId { get; init; }

    public int ThreadId { get; init; }

    /// <summary>The I/O request this record belongs to, used to pair an operation with its completion.</summary>
    public ulong IrpKey { get; init; }

    /// <summary>The open instance. Reusable, so it identifies an instance only inside its observed lifetime (R22).</summary>
    public ulong FileObject { get; init; }

    /// <summary>The name key of the object, stable across handles to one name.</summary>
    public ulong FileKey { get; init; }

    /// <summary>Bounded copy of the object path, when a naming record supplied one.</summary>
    public string? ResourceName { get; init; }

    public bool ResourceNameTruncated { get; init; }

    /// <summary>Bytes the caller asked for, in the <c>RequestedIo</c> domain. Never a transfer claim (P3).</summary>
    public long? RequestedBytes { get; init; }

    /// <summary>Bytes a completion reported, in the <c>CompletedIo</c> domain. Null until a completion pairs.</summary>
    public long? CompletedBytes { get; init; }

    /// <summary>The completion status, when a completion paired with this operation.</summary>
    public long? Status { get; init; }

    public required QualityLevel AttributionQuality { get; init; }
}
