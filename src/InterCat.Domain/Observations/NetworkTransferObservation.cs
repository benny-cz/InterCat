namespace InterCat.Domain;

/// <summary>
/// The connection instance an observation belongs to, built from the endpoint pair rather than from a
/// reusable number alone (R22). Two independently proven connections never collapse into one (I12).
/// </summary>
public readonly record struct FlowKey(uint LocalAddress, int LocalPort, uint RemoteAddress, int RemotePort)
{
    /// <summary>The same connection seen from the other endpoint.</summary>
    public FlowKey Mirror() => new(RemoteAddress, RemotePort, LocalAddress, LocalPort);
}

/// <summary>
/// One admitted transport observation, normalised for the M0 vertical path. Source-named endpoints are
/// preserved as delivered; which side is local is derived from direction rather than assumed (R1).
/// An unknown value stays null (R2, R3). The durable schema of section 7.3 replaces this shape in M1 (IC-013).
/// </summary>
public sealed record NetworkTransferObservation
{
    public required ObservationId Id { get; init; }
    public required Mechanism Mechanism { get; init; }
    public required ObservationKind Kind { get; init; }
    public required Direction Direction { get; init; }
    public required long TimestampUtcTicks { get; init; }

    /// <summary>The source clock reading in its original encoding (I8).</summary>
    public required long SourceTicks { get; init; }

    /// <summary>The owning process the payload names, which may differ from the event header (section 4.1).</summary>
    public int? OwnerProcessId { get; init; }

    public int? HeaderProcessId { get; init; }

    public uint SourceAddress { get; init; }
    public int SourcePort { get; init; }
    public uint DestinationAddress { get; init; }
    public int DestinationPort { get; init; }

    /// <summary>The connection instance with the observing side first, derived from <see cref="Direction"/>.</summary>
    public FlowKey Flow { get; init; }

    /// <summary>Observed bytes in <see cref="ByteDomain"/>. Null means the source exposed no size (R3).</summary>
    public long? ByteCount { get; init; }

    public ByteDomain? ByteDomain { get; init; }

    /// <summary>How well the owning process is established for this record (section 7.3).</summary>
    public required QualityLevel AttributionQuality { get; init; }
}
