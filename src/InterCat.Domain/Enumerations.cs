namespace InterCat.Domain;

public enum Mechanism
{
    ProcessLifecycle = 1,
    ThreadLifecycle = 2,
    Tcp = 3,
    Udp = 4,
    UnixDomainSocket = 5,
    NamedPipe = 6,
    AnonymousPipe = 7,
    Rpc = 8,
    Alpc = 9,
    SharedSection = 10,
    ComActivation = 11,
    Synchronization = 12,
    WindowMessage = 13,
    Clipboard = 14,
    Mailslot = 15,
    Dde = 16,
    RemoteFileOrSmb = 17,
    Quic = 18,
    ApplicationSdk = 19,
    Instrumented = 20,
    UnknownMechanism = 99,
}

public enum ObservationLayer { Transport = 1, Application = 2, Resource = 3, Lifecycle = 4, Collector = 5 }

public enum ObservationKind
{
    Send = 1, Receive = 2, RequestStart = 3, RequestEnd = 4, Open = 5, Close = 6,
    Bind = 7, Connect = 8, Accept = 9, Disconnect = 10, Map = 11, Unmap = 12,
    Wait = 13, Signal = 14, Create = 15, Exit = 16, Inventory = 17, Error = 18,
    Discovery = 19, UnknownKind = 99,
}

public enum Direction { UnknownDirection = 0, Outbound = 1, Inbound = 2, Bidirectional = 3, DirectionNotApplicable = 4 }
public enum AnalysisBasis { SourceObservations = 1, LogicalOperations = 2, ResourceTopology = 3 }
/// <summary>
/// <c>EN-Metric</c> (section 23). <see cref="EndpointActivityBytes"/> was added by plan revision 28 and ADR-012:
/// endpoint activity counts both directions at every endpoint, so it is a metric of its own rather than an
/// accounting side of a metric whose name states one direction.
/// </summary>
public enum Metric
{
    Observations = 1,
    OperationsStarted = 2,
    OperationsCompleted = 3,
    BytesSent = 4,
    BytesReceived = 5,
    RequestedIoBytes = 6,
    ApplicationPayloadBytes = 7,
    CapturedContentBytes = 8,
    Rate = 9,
    Duration = 10,
    ActiveChannels = 11,
    ActivePeers = 12,
    MappingCapacity = 13,
    Errors = 14,
    EndpointActivityBytes = 15,
}
public enum ByteDomain { TransportObserved = 1, RequestedIo = 2, CompletedIo = 3, ApplicationPayload = 4, CapturedContent = 5, Capacity = 6 }
public enum AccountingSide { SendSide = 1, ReceiveSide = 2, EndpointActivity = 3, CanonicalOwner = 4 }
/// <summary>
/// <c>EN-SourceField</c> (section 23): a source correlation or object field of §7.3 that <c>observation-v1</c> has no
/// column for. The catalog names which admitted field each code is; a code is a meaning, never a field name, so two
/// providers' fields of one meaning share it and a derivation reads meanings rather than provider layouts.
/// </summary>
public enum SourceField : ushort
{
    /// <summary>A process's non-reusable start sequence number: the provider start key (I12).</summary>
    ProcessStartSequence = 1,

    /// <summary>A process's creation time as the source reported it, a FILETIME.</summary>
    ProcessCreateTime = 2,

    /// <summary>The PID of the process that created this one.</summary>
    ParentProcessId = 3,

    /// <summary>The start sequence number of the process that created this one.</summary>
    ParentStartSequence = 4,

    /// <summary>A process's exit time as the source reported it, a FILETIME.</summary>
    ProcessExitTime = 5,

    /// <summary>The terminal session a process runs in.</summary>
    ProcessSessionId = 6,

    /// <summary>A transport connection identifier. Reusable and sometimes zero, so never an identity alone (R22).</summary>
    ConnectionId = 7,

    /// <summary>An I/O request packet address that pairs an operation with its completion.</summary>
    IoRequestPacket = 8,

    /// <summary>A file object address: one open instance, inside its observed lifetime only (R22).</summary>
    FileObject = 9,

    /// <summary>A file name key, stable across handles to one name.</summary>
    FileKey = 10,

    /// <summary>The thread that issued an operation.</summary>
    IssuingThreadId = 11,

    /// <summary>An RPC procedure number within its interface. A grouping key, never a call identity.</summary>
    RpcProcedureNumber = 12,

    /// <summary>An RPC protocol sequence code, which says whether a call stayed local.</summary>
    RpcProtocolSequence = 13,

    /// <summary>A file I/O byte offset.</summary>
    FileByteOffset = 14,
}

/// <summary><c>EN-TimeScope</c> (section 23): the interval a result is scoped to.</summary>
public enum TimeScope { AnalysisInterval = 1, RetainedCapture = 2, VisibleViewport = 3 }

public enum QualityDimension { Attribution = 1, Correlation = 2, Measurement = 3, Timing = 4 }
public enum QualityLevel { Proven = 1, Qualified = 2, Weak = 3, UnknownQuality = 4 }
public enum CoverageState { Covered = 1, ReducedFidelity = 2, PartialGap = 3, NotCollected = 4, UnknownCoverage = 5 }
public enum FieldAvailability { Present = 1, NotExposed = 2, ProfileDisabled = 3, Denied = 4, EventLost = 5, SchemaUnknown = 6, Redacted = 7, NotApplicable = 8 }
public enum CapabilityState { Available = 1, Experimental = 2, Unsupported = 3, PermissionDenied = 4, DisabledByProfile = 5, SchemaUnknown = 6, ProviderFailed = 7 }
public enum CapabilityTier { TrafficVisualization = 1, TopologyOnly = 2, ExperimentalEvidence = 3, Unsupported = 4 }
public enum RelationStrength { Direct = 1, Correlated = 2, Candidate = 3, Unresolved = 4, Conflicting = 5 }

/// <summary>
/// <c>EN-EvidencePolicy</c> (section 23): which relation strengths a result admits. The default admits direct and
/// correlated relations; a candidate is shown only when asked for, and definitive causal views admit 1 and 2 only.
/// </summary>
public enum EvidencePolicy { DirectOnly = 1, IncludeCorrelated = 2, IncludeCandidates = 3, AllIncludingConflicting = 4 }

/// <summary>
/// <c>EN-OperationState</c> (section 23). A heuristic timeout yields the last known state, never a
/// Windows timeout error (section 18.5).
/// </summary>
public enum OperationState
{
    Started = 1,
    Completed = 2,
    ExplicitlyFailed = 3,
    OrphanCompletion = 4,
    OpenAtBoundary = 5,
    Ambiguous = 6,
    EvictedUnresolved = 7,
}

/// <summary><c>EN-Grouping</c> (section 23): how lanes and graph clusters are grouped at a rung.</summary>
public enum LaneGrouping
{
    InstanceOnly = 1,
    Executable = 2,
    ServiceContainer = 3,
    UserSession = 4,
    Host = 5,
    Mechanism = 6,
    Endpoint = 7,
    Package = 8,

    /// <summary>The processes at the other end from one focused process, through proven relations (§19.1 peer).</summary>
    Peer = 9,
}

public enum AdmissionMode { MetadataOnly = 1, ScopedContent = 2, OriginalEvidence = 3 }

/// <summary>Stable names of the capture intents described by section 9.4.</summary>
public enum CaptureProfileKind
{
    Explore = 1,
    FocusedTransport = 2,
    Timing = 3,
    Content = 4,
    FlightRecorder = 5,
}

public enum CaptureLifecycle { Idle = 1, Probing = 2, Starting = 3, Recording = 4, Stopping = 5, Finalizing = 6, Closed = 7 }
public enum InterCatExitCode { Success = 0, PartialResultSuccess = 1, InvalidInvocation = 2, PermissionOrCapabilityFailure = 3, CorruptedInput = 4, Cancelled = 5 }

public enum MeasurementUnit
{
    Count = 1,
    Bytes = 2,
    Seconds = 3,
    BytesPerSecond = 4,
    Milliseconds = 5,
    Nanoseconds = 6,
    CountPerSecond = 7,
}
