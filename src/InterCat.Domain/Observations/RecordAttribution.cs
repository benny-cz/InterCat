namespace InterCat.Domain;

/// <summary>
/// Which process a record belongs to when its payload names none. A kernel source raises a record in whatever context the
/// kernel was in, so its event header's process is context only and never an owner (§4.1). A user-mode provider raises
/// its records in the process it describes. Where that was measured for a mechanism, the header's process is the owner
/// (ADR-030).
/// </summary>
public static class RecordAttribution
{
    /// <summary>
    /// Whether a mechanism's records are raised in the process they describe. Measured for RPC on FX-RPC-001: every truth
    /// call's start was raised in the calling process, and 122 of 122 server calls to its interface in the service host
    /// that served them.
    /// </summary>
    public static bool RaisedByTheirProcess(Mechanism mechanism) => mechanism == Mechanism.Rpc;

    /// <summary>
    /// The PID a record belongs to: the owner its payload names, or the process that raised it when its mechanism's
    /// records are raised in the process they describe. Null when neither applies.
    /// </summary>
    public static int? OwnerOf(int? payloadOwner, Mechanism mechanism, int headerProcessId) =>
        payloadOwner ?? (RaisedByTheirProcess(mechanism) ? headerProcessId : null);
}
