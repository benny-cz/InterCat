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

    /// <summary>
    /// An HTTP exchange's messages as the client library that made it recorded them - WinINet's own capture (ADR-037) -
    /// carried by a TCP connection whose transfers stay the transport's records.
    /// </summary>
    Http = 21,

    /// <summary>
    /// A call made through COM's channel from one apartment to another, as its client and server ends record it, with its
    /// interface and method (ADR-078). It is between processes only where its record names another process; an activation
    /// is <see cref="ComActivation"/>'s, and never one of these.
    /// </summary>
    ComCall = 22,
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
/// <c>EN-DurationInterval</c> (section 23): the named interval a duration measures (§5). Intervals of different names are
/// never interchangeable: a client call spans the transport and the server's work, a server execution only the latter.
/// </summary>
public enum DurationInterval
{
    ClientCall = 1,
    ServerExecution = 2,
    IoCompletion = 3,
    AlpcSendToReceive = 4,
    Wait = 5,
    MappingLifetime = 6,
}

/// <summary><c>EN-Cohort</c> (section 23): which operations a duration describes (§19.2).</summary>
public enum OperationCohort
{
    /// <summary>The operations whose completion is in scope; the default.</summary>
    CompletedInRange = 1,

    /// <summary>The operations whose start is in scope, those still open at capture end among them, censored.</summary>
    StartedInRange = 2,
}

/// <summary>
/// <c>EN-DurationStatistic</c> (section 23): the one number a duration answer, and a ranking of durations, reads from the
/// distribution, each a duration some operation took (nearest rank).
/// </summary>
public enum DurationStatistic
{
    Median = 1,
    Percentile95 = 2,
    Maximum = 3,
}
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

    /// <summary>
    /// An ALPC message id. The kernel reuses one within seconds across processes, so it joins a send to a receive only
    /// inside one call's window and thread chain, and is never an identity by itself (ADR-034).
    /// </summary>
    AlpcMessageId = 15,

    /// <summary>
    /// The HTTP exchange - a request and its response - a record's buffer belongs to, as the client library numbers it
    /// within its process. One per exchange where measured (ADR-037); its reuse over a process's life is unmeasured, so
    /// it joins records only within one process and one exchange's time, never as an identity by itself.
    /// </summary>
    HttpExchangeId = 16,

    /// <summary>A buffer's place in its message part: 0 for the first, then 1, 2 and on (ADR-037).</summary>
    ContentBufferSequence = 17,

    /// <summary>Which end of its message part a buffer is: 1 the first, 2 the last, 3 both, 0 neither (ADR-037).</summary>
    ContentBufferFlags = 18,

    /// <summary>
    /// A COM call's trace number within the process that raised its records, which its stop repeats: it joins a call's
    /// start to its stop at one end, never one process's records to another's (ADR-077).
    /// </summary>
    ComCallTraceId = 19,

    /// <summary>
    /// The process a record names at its other end, as the source names it: never the record's owner, and a peer only
    /// where a relation rule checks it (ADR-079).
    /// </summary>
    PeerProcessId = 20,

    /// <summary>
    /// A COM method's number within its interface, which the RPC call carrying it names as its procedure (ADR-079): a
    /// grouping key, never a call identity.
    /// </summary>
    ComMethodNumber = 21,

    /// <summary>
    /// A synchronization object's number in its capture, as the synchronization handle gate numbers it from the first handle
    /// the capture saw made at its address to the close of the last (ADR-083): never its kernel address, which the kernel
    /// reuses once the object is gone (R22).
    /// </summary>
    SynchronizationObjectNumber = 22,

    /// <summary>
    /// An object's type number, as the object manager numbers its types on one build - Event's, Mutant's and Semaphore's
    /// measured for the build (ADR-082) - never a type's identity across builds.
    /// </summary>
    ObjectTypeNumber = 23,

    /// <summary>
    /// A handle's value within the process holding it: reused once the handle closes, so it names one handle only in its
    /// process and between its creation and its close (R22).
    /// </summary>
    HandleValue = 24,

    /// <summary>The handle a duplication made in its target process, by its value there, as <see cref="HandleValue"/> is named.</summary>
    PeerHandleValue = 25,

    /// <summary>
    /// A window message's number in its capture, as the window message gate numbers it from its start - a send's start or a
    /// post's queueing - to its end (ADR-087): never the queue message's kernel pointer, which the kernel reuses once the
    /// message is retrieved (R22).
    /// </summary>
    WindowMessageNumber = 26,

    /// <summary>
    /// A mapped view's number in its capture, as the section view gate numbers it from its mapping, or the start's rundown,
    /// to its unmapping (ADR-090): never its base, which its process maps another view at once it is unmapped (R22).
    /// </summary>
    SectionViewNumber = 27,

    /// <summary>
    /// A section's number in its capture, as the section view gate numbers it by the value its views carry, from the first
    /// followed view carrying it to the unmapping of the last (ADR-090): never that value, a kernel address reused once the
    /// section is gone (R22), and never the number its handles are followed by, as no record joins a view to a handle.
    /// </summary>
    SectionNumber = 28,

    /// <summary>A view's size in bytes as its mapping gives it: the extent mapped, never bytes moved through it (P3, ADR-089).</summary>
    ViewSize = 29,

    /// <summary>
    /// A view's extra information as the kernel gives it, whose high byte alone is measured: 0x47 for an image's view, which
    /// no other view carries (ADR-089's fourth addendum).
    /// </summary>
    ViewInformation = 30,

    /// <summary>
    /// A section object's number in its capture, as the section handle gate numbers it from the first handle the capture saw
    /// made at its address to the close of the last (ADR-090): never its kernel address, which the kernel reuses once the
    /// section is gone (R22), and never <see cref="SectionNumber"/>, as no record joins a section's handles to its views.
    /// </summary>
    SectionObjectNumber = 31,
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

/// <summary>
/// <c>EN-FilterDimension</c> (section 23): what one filter term constrains. The codes order a canonical filter's AND
/// terms (§10.5). A peer is a member of the process-instance term, because it is relative to that term's focus, and
/// time is the specification's time scope; neither is an independent term.
/// </summary>
public enum FilterDimension
{
    Host = 1,
    ProcessInstance = 2,
    Executable = 3,
    Mechanism = 4,
    Layer = 5,
    Endpoint = 6,
    Direction = 7,
    Operation = 8,
    Status = 9,
    Quality = 10,
    Source = 11,
    ByteValue = 12,
}

/// <summary>
/// <c>EN-BetweenDirection</c> (section 23): which data a <c>between(A,B)</c> filter keeps (§19.1). A record with no data
/// direction, such as a connect, connects the two sets only under <see cref="Either"/>.
/// </summary>
public enum BetweenDirection
{
    /// <summary>Every record connecting a process of one set with a process of the other, in either direction.</summary>
    Either = 1,

    /// <summary>Data that left a process of the first set and reached a process of the second.</summary>
    FirstToSecond = 2,

    /// <summary>Data that left a process of the second set and reached a process of the first.</summary>
    SecondToFirst = 3,
}

public enum AdmissionMode { MetadataOnly = 1, ScopedContent = 2, OriginalEvidence = 3 }

/// <summary>
/// <c>EN-OmissionReason</c> (section 23): why a delivered record was not admitted. A policy omission is not a loss: each
/// reason is counted separately and never folded into loss or into another reason (section 20.6).
/// </summary>
public enum OmissionReason
{
    /// <summary>The record came from a provider this capture did not request.</summary>
    UnrequestedProvider = 1,

    /// <summary>The descriptor is not in the profile's allowlist.</summary>
    DescriptorNotAdmitted = 2,

    /// <summary>The descriptor is explicitly denied, for example a payload-producing debug event (P28).</summary>
    DescriptorDenied = 3,
}

/// <summary>
/// <c>EN-GateOmission</c> (`contracts/coverage-v3.md` §1): why a gated source's gate did not admit a record of an admitted
/// descriptor (ADR-074). A source whose records of one descriptor are not all of one resource or one count - Winsock AFD,
/// every socket's calls of every family, several records a call (ADR-073) - admits through a gate that reads them as they
/// arrive. A gated omission is not a loss, and is counted beside the descriptor's other outcomes, never folded into them.
/// </summary>
public enum GateOmission
{
    /// <summary>A record of a resource the gate follows that counts nothing of it: one of a call's other records.</summary>
    NotCounted = 1,

    /// <summary>A record of no resource the gate followed: another kind's, or one made before the capture began.</summary>
    NoFollowedResource = 2,

    /// <summary>
    /// The record that began a resource the gate did not follow, holding as many as it may; that resource's later records
    /// are of none it followed.
    /// </summary>
    BeyondBound = 3,

    /// <summary>
    /// A record raised before one the session delivered ahead of it. Its gate numbers by the order a session was measured
    /// delivering in, the order raised (ADR-086's third addendum), which this record is not, so it is omitted rather than
    /// numbered with a resource it may not be of (ADR-087's addendum).
    /// </summary>
    OutOfOrder = 4,
}

/// <summary>
/// <c>EN-UndecodableReason</c> (section 23): why a record of an admitted descriptor could not be read. A guessed layout
/// is never used instead (section 18.3); the record is the decode layer's loss, attributed to its descriptor.
/// </summary>
public enum UndecodableReason
{
    /// <summary>The body is shorter than the admitted slots require.</summary>
    BodyShorterThanSchema = 1,

    /// <summary>The descriptor arrived at a version the saved schema does not cover.</summary>
    UnknownDescriptorVersion = 2,

    /// <summary>Reading the body raised an error inside the bounded admission read.</summary>
    AdmissionReadFailed = 3,

    /// <summary>The record arrived with a pointer width the compiled offsets were not built for.</summary>
    PointerWidthMismatch = 4,
}

/// <summary>
/// <c>EN-LossLayer</c> (section 23): where evidence was lost before it could be attributed to a descriptor (section 13.3).
/// A loss states its layer because each layer bounds a different thing; an undecodable record is the decode layer's
/// loss and is always attributed to its descriptor instead.
/// </summary>
public enum LossLayer
{
    /// <summary>Events the recording session reported lost. For an imported file, the file's own lost-event count.</summary>
    SourceSession = 1,

    /// <summary>Buffers the session's consumer reported lost. Each held an unknown number of records.</summary>
    ConsumerBuffers = 2,

    /// <summary>Records InterCat's own bounded callback queue dropped because it was full (R8).</summary>
    CallbackQueue = 3,

    /// <summary>Admitted records the writer could not persist.</summary>
    Storage = 4,
}

/// <summary>Stable names of the capture intents described by section 9.4.</summary>
public enum CaptureProfileKind
{
    Explore = 1,
    FocusedTransport = 2,
    Timing = 3,
    Content = 4,
    FlightRecorder = 5,

    /// <summary>
    /// Explore's lifecycle and RPC calls, with the kernel's ALPC messages in a private system logger, to link an RPC call to
    /// the process that served it (ADR-034, ADR-035). Opt-in: collecting ALPC measured Moderate.
    /// </summary>
    RpcPeers = 6,

    /// <summary>
    /// Lifecycle and InterCat's own content fixture, whose generated messages are kept as content: the controlled path
    /// that carries content end to end (ADR-036). It keeps no other application's bytes.
    /// </summary>
    ContentFixture = 7,

    /// <summary>
    /// Lifecycle and each AF_UNIX socket's calls, from Winsock AFD through its gate (ADR-073, ADR-074). Opt-in: AFD's
    /// process filter does not hold, so it collects every socket call of the machine, which measured Moderate.
    /// </summary>
    UnixSockets = 8,

    /// <summary>
    /// Lifecycle, RPC calls and the COM calls they carry, from Microsoft-Windows-COM-Perf beside Microsoft-Windows-RPC, so a
    /// COM call's RPC call has the process at its other end (ADR-079). Opt-in: COM-Perf's collection, Low in processor time,
    /// slowed a COM-heavy workload 17 to 18%.
    /// </summary>
    ComCalls = 9,

    /// <summary>
    /// Lifecycle and named synchronization objects' handles - an event's, a mutant's or a semaphore's creation, duplication
    /// and close in the process that holds it, and those open at the stop - from the kernel's object manager handle group,
    /// each object numbered in the capture (ADR-083). Opt-in: collecting the group, Low in processor time, lowered a
    /// handle-heavy workload's throughput 5.3 and 8.3%.
    /// </summary>
    SyncObjects = 10,

    /// <summary>
    /// Lifecycle and window messages - every message one window sends or posts another, its ends each bound to the process
    /// that raised it and numbered by its message in the capture - from Win32k's message pump (ADR-087). Opt-in: collecting
    /// the message pump, Low in the processor time it adds, lowered a message-bound workload's throughput 8.3 and 5.2%.
    /// </summary>
    WindowMessages = 11,

    /// <summary>
    /// Lifecycle and sections - every view a process maps or unmaps, and each section handle made, duplicated and closed, each
    /// view and section numbered in the capture - from the kernel's file view flag and the object manager's handle group
    /// (ADR-090). Opt-in: collecting either, Low in the processor time it adds, lowered a workload heavy in it past the
    /// throughput target.
    /// </summary>
    Sections = 12,
}

public enum CaptureLifecycle { Idle = 1, Probing = 2, Starting = 3, Recording = 4, Stopping = 5, Finalizing = 6, Closed = 7 }
/// <summary>
/// What an icat invocation exits with, one meaning each in every command (§20.4). <see cref="InternalError"/> is an error
/// nothing in InterCat mapped: a defect in InterCat rather than in what was asked (§20.6 Internal), 70 as the broker's is.
/// </summary>
public enum InterCatExitCode
{
    Success = 0,
    PartialResultSuccess = 1,
    InvalidInvocation = 2,
    PermissionOrCapabilityFailure = 3,
    CorruptedInput = 4,
    Cancelled = 5,
    InternalError = 70,
}

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
