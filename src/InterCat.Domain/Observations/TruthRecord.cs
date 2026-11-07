using System.Text.Json.Serialization;

namespace InterCat.Domain;

/// <summary>What a truth workload recorded. The log is an oracle for a test, never an input to capture (section 13.1).</summary>
public enum TruthEventKind
{
    ProcessStarted = 1,
    ListenerBound = 2,
    ConnectionAccepted = 3,
    ConnectionEstablished = 4,
    MessageSent = 5,
    MessageReceived = 6,
    ConnectionClosed = 7,
    ProcessExiting = 8,
    Failure = 9,

    /// <summary>A call the workload issued, for mechanisms whose unit of work is a call rather than a message.</summary>
    CallIssued = 10,

    /// <summary>The completion the workload observed for a call it issued.</summary>
    CallCompleted = 11,

    /// <summary>A call the workload served, logged by its server as it ran: the other end of a call another process issued.</summary>
    CallServed = 12,

    /// <summary>A named kernel object the workload created, by its name; the status names its kind.</summary>
    ObjectCreated = 13,

    /// <summary>A named kernel object another process created, which the workload opened by its name.</summary>
    ObjectOpened = 14,

    /// <summary>A signal the workload gave an object it holds a handle to: an event set, a semaphore or a mutex released.</summary>
    ObjectSignaled = 15,

    /// <summary>A wait of the workload's on an object that ended satisfied; the status says whether it blocked first.</summary>
    WaitEnded = 16,

    /// <summary>A handle to a named object that the workload closed.</summary>
    HandleClosed = 17,

    /// <summary>
    /// A handle the workload duplicated from a process into a process, its own or another's, logged by the process that
    /// asked for it with both ends (<see cref="TruthRecord.Duplication"/>); the status names the object's kind.
    /// </summary>
    HandleDuplicated = 18,

    /// <summary>A view of a section the workload mapped into its own address space, by the section's name; its declared bytes are the view's size.</summary>
    ViewMapped = 19,

    /// <summary>A view of a section the workload unmapped from its own address space, by the section's name.</summary>
    ViewUnmapped = 20,
}

/// <summary>
/// A handle duplication's two ends as the process that asked for it logged them: the process the handle was duplicated
/// from and its value there, and the process the duplication made a handle in and that handle's value.
/// </summary>
public sealed record HandleDuplication(int SourceProcessId, long SourceHandle, int TargetProcessId, long TargetHandle);

/// <summary>
/// A cooperative message as the workload numbered it (ADR-104): its channel's identity, epoch and sequence, and on a
/// consumption the reader that consumed it, as the process that published or consumed it logged them.
/// </summary>
public sealed record CooperativeTruth(Guid Channel, long Epoch, long Sequence, long? Consumer);

/// <summary>
/// One line of an independent truth log. It carries the scenario, the process instance that produced it,
/// a monotonic reading, the declared and completed sizes, and the endpoint it used (section 13.1).
/// </summary>
public sealed record TruthRecord
{
    public required string ScenarioId { get; init; }
    public required string Role { get; init; }
    public required long Sequence { get; init; }
    public required TruthEventKind Kind { get; init; }
    public required int ProcessId { get; init; }

    /// <summary>Process creation time as a FILETIME, which keeps a reused PID from merging instances (I12).</summary>
    public required long ProcessStartFileTime { get; init; }

    /// <summary>A monotonic reading from the producing process; it is never compared across machines (section 8.1).</summary>
    public required long MonotonicTicks { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
    public long? CallId { get; init; }
    public int? LocalPort { get; init; }
    public int? RemotePort { get; init; }

    /// <summary>The named resource the operation used, such as a pipe path. Null for socket scenarios.</summary>
    public string? ResourceName { get; init; }

    /// <summary>Bytes the application asked the socket to move.</summary>
    public long? DeclaredBytes { get; init; }

    /// <summary>Bytes the socket reported as moved. Null stays unknown; it is never defaulted to zero (R3).</summary>
    public long? CompletedBytes { get; init; }

    public string? Status { get; init; }

    /// <summary>
    /// A duplication's two ends: on a <see cref="TruthEventKind.HandleDuplicated"/> record, and on the close of the handle
    /// a duplication made, which names that handle; null on any other record, where it is not written, so a record of any
    /// other kind reads as it always has and a log rebuilt from a published run is that run's, byte for byte (I14).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HandleDuplication? Duplication { get; init; }

    /// <summary>
    /// A cooperative message's identity: on a publication or a consumption a workload made through the SDK; null on any other
    /// record, where it is not written, so a record of any other kind reads as it always has.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CooperativeTruth? Message { get; init; }
}
