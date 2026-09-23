namespace InterCat.Domain;

/// <summary>
/// Which endpoint a transport descriptor names first. A record keeps its endpoints as the source named them - the first
/// in the source columns (R1) - so a reader that needs the record owner's own end reads it through this orientation,
/// which was measured against a truth workload for every admitted descriptor rather than assumed (§7.4).
/// </summary>
public enum EndpointOrientation
{
    /// <summary>The owner's own endpoint first, the other end second: every admitted TCP descriptor, and a UDP send.</summary>
    OwnerFirst = 1,

    /// <summary>The datagram's origin first and its destination second: a UDP receive names its sender first.</summary>
    OriginFirst = 2,
}

/// <summary>The measured orientation of the admitted transport descriptors.</summary>
public static class TransportEndpoints
{
    /// <summary>
    /// The orientation of an admitted transport record by its mechanism and kind. FX-TCP-001 measured every TCP kind
    /// owner-first; FX-UDP-001 measured a UDP send owner-first and a UDP receive origin-first, 16 of 16 each way.
    /// </summary>
    public static EndpointOrientation OrientationOf(Mechanism mechanism, ObservationKind kind) =>
        mechanism == Mechanism.Udp && kind == ObservationKind.Receive
            ? EndpointOrientation.OriginFirst
            : EndpointOrientation.OwnerFirst;
}
