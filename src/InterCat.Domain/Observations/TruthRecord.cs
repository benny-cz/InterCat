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
}

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
}
