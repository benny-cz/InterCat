namespace InterCat.Domain;

/// <summary>
/// One admitted RPC call record. The source exposes an interface, an operation number, a transport and a
/// status, and no size at all, so this shape carries no byte field: an RPC operation never becomes a byte
/// claim (I11, P4, R3). The durable schema of section 7.3 replaces it in M1 (IC-013).
/// </summary>
public sealed record RpcCallObservation
{
    public required ObservationId Id { get; init; }
    public required ObservationKind Kind { get; init; }
    public required Direction Direction { get; init; }
    public required long TimestampUtcTicks { get; init; }
    public required long SourceTicks { get; init; }
    public required int ProcessId { get; init; }
    public int ThreadId { get; init; }

    /// <summary>The activity the record belongs to, which is how a start reaches its completion.</summary>
    public Guid ActivityId { get; init; }

    /// <summary>The RPC interface, when the descriptor carried one. Null on a completion record.</summary>
    public Guid? InterfaceUuid { get; init; }

    public long? ProcedureNumber { get; init; }

    /// <summary>The transport sequence the call used, as the source reported it.</summary>
    public long? Protocol { get; init; }

    /// <summary>Completion status, present only on a completion record.</summary>
    public long? Status { get; init; }

    public required QualityLevel AttributionQuality { get; init; }
}
