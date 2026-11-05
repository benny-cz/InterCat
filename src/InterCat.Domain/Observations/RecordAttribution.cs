namespace InterCat.Domain;

/// <summary>
/// Which process a record belongs to when its payload names none. A kernel source raises a record in whatever context the
/// kernel was in, so its event header's process is context only and never an owner (§4.1). A user-mode provider raises
/// its records in the process it describes. Where that was measured for a mechanism, the header's process is the owner
/// (ADR-030), but never an inventory's: a rundown is raised wherever it ran, never in the process it describes.
/// </summary>
public static class RecordAttribution
{
    /// <summary>
    /// Whether a mechanism's records are raised in the process they describe. Measured for RPC on FX-RPC-001: every truth
    /// call's start was raised in the calling process, and 122 of 122 server calls to its interface in the service host
    /// that served them. Measured for HTTP on FX-HTTP-001 (ADR-037): 444 of 444 of WinINet's capture records were raised
    /// in the client process that made the exchange, and none of a decoy's reached a session scoped to that client; and on
    /// FX-HTTP-005, an asynchronous client's too, raised on whichever of its threads WinINet finished each step on.
    /// Measured for COM calls on FX-COM-001 (ADR-077): in four runs every call's client start and stop were raised in the
    /// calling process and its served start and stop in the serving one; under a session's process filter the two
    /// processes' every call arrived and none of a decoy pair's; and FX-COM-002's calls across an apartment were raised in
    /// their own process at both ends. Measured for named synchronization objects' handles on FX-SYNC-001 and FX-SYNC-002
    /// (ADR-082's fifth addendum): in four runs each, every handle's creation and close was raised in the process that made
    /// and closed it, the handle a duplication made closed in its target. A duplication names the process it made its
    /// handle in, and the stop's rundown each handle's holder, so both bind by their payload; no kernel handle's record is
    /// admitted, as its header names whichever process the kernel ran for (ADR-083's second addendum). Measured for window
    /// messages on FX-WMSG-001 (ADR-086): in three runs every message one process sent another's window had its start and
    /// stop raised in the sender and its retrieval's start and stop in the receiver, and every message it posted, the close
    /// among them, its queueing raised in the poster and its retrieval in the receiver; a send's start names the receiving
    /// process only as its other end, never its owner (ADR-087's second addendum).
    /// </summary>
    public static bool RaisedByTheirProcess(Mechanism mechanism) =>
        mechanism is Mechanism.Rpc or Mechanism.Http or Mechanism.ComCall or Mechanism.Synchronization or Mechanism.WindowMessage;

    /// <summary>
    /// Whether a record whose payload names no owner binds to the process that raised it: its mechanism's records are
    /// raised in the process they describe, and it is no inventory, which is raised wherever its rundown ran - FX-SYNC-002's
    /// handle rundowns in processes other than the holders they named, and an object type's naming no process at all.
    /// </summary>
    public static bool HeaderBinds(Mechanism mechanism, ObservationKind kind) =>
        kind != ObservationKind.Inventory && RaisedByTheirProcess(mechanism);

    /// <summary>
    /// The PID a record belongs to: the owner its payload names, or the process that raised it when its header binds it
    /// (<see cref="HeaderBinds"/>). Null when neither applies.
    /// </summary>
    public static int? OwnerOf(int? payloadOwner, Mechanism mechanism, ObservationKind kind, int headerProcessId) =>
        payloadOwner ?? (HeaderBinds(mechanism, kind) ? headerProcessId : null);
}
