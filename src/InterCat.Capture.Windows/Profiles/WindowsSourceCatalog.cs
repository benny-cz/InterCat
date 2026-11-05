using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>A documented transform applied when building an observation, never in place of the raw value (R1).</summary>
public enum SlotTransform
{
    None = 1,

    /// <summary>A 16-bit port delivered in network byte order.</summary>
    NetworkOrderPort = 2,

    /// <summary>A 32-bit IPv4 address delivered in network byte order.</summary>
    NetworkOrderIpv4Address = 3,

    /// <summary>
    /// A 128-bit IPv6 address delivered as its 16 bytes in network order, which a row keeps as the number those bytes
    /// are when read big-endian (`segment-v1` §5).
    /// </summary>
    NetworkOrderIpv6Address = 4,
}

/// <summary>A field the adapter intends to admit, with the semantics it would carry (R2).</summary>
public sealed record AdmittedFieldIntent(
    string FieldName,
    FieldRole Role,
    MeasurementUnit? Unit = null,
    ByteDomain? ByteDomain = null,
    SlotTransform Transform = SlotTransform.None,
    string? Notes = null,
    SourceField? SourceField = null)
{
    /// <summary>For a content field: what its bytes are, as the source's validated content contract says (§11.2).</summary>
    public ContentEvidenceClassification? ContentClassification { get; init; }

    /// <summary>For a content field: how its bytes are encoded, as the source declares it; never guessed.</summary>
    public ContentFieldEncoding? ContentEncoding { get; init; }

    /// <summary>
    /// What a derivation rule decides its source's records by this field, completing "by which": a schema that does not
    /// admit it refuses the source, rather than admit records the rule would decide by a value never observed (R3); null
    /// for a field no rule decides by, whose absence a record states as unknown.
    /// </summary>
    public string? Decides { get; init; }
}

/// <summary>One event descriptor the adapter intends to admit under a metadata-only policy (§18.2).</summary>
/// <remarks>
/// The layer is part of the source contract, not a normalizer guess: whether a descriptor is transport,
/// application, resource or lifecycle evidence decides which metrics may be summed together (§5.1, I11), so it
/// is declared here beside the mechanism the descriptor was validated for.
/// </remarks>
public sealed record AdmittedEventIntent(
    int EventId,
    int Version,
    string Name,
    Mechanism Mechanism,
    ObservationLayer Layer,
    ObservationKind Kind,
    Direction Direction,
    IReadOnlyList<AdmittedFieldIntent> Fields)
{
    /// <summary>
    /// A classic kernel event's opcode, which alone tells its class's events apart; null for a manifest event, whose id
    /// does (ADR-035's addendum). A classic intent's event id is 0, as its header has it.
    /// </summary>
    public int? Opcode { get; init; }
}

/// <summary>
/// A candidate Windows source with everything §4.3 requires before a capture may request it.
/// Nothing here is a coverage claim: a definition states an intent that a probe then confirms or refuses.
/// </summary>
public sealed record WindowsSourceDefinition
{
    public required string SourceId { get; init; }
    public required string DisplayName { get; init; }
    public required SourceKind Kind { get; init; }
    public required string ProviderName { get; init; }
    public required IReadOnlyList<Mechanism> Mechanisms { get; init; }
    public required PrivilegeRequirement RequiredPrivilege { get; init; }
    public required string Level { get; init; }
    public required ulong MatchAnyKeyword { get; init; }
    public ulong MatchAllKeyword { get; init; }
    public required IReadOnlyList<string> RequestedKeywords { get; init; }
    public bool SupportsCaptureSideProcessFilter { get; init; }
    public ValidatedContentSourceContract? ContentContract { get; init; }
    public required string FilteringNotes { get; init; }
    public required string StartupBehaviour { get; init; }

    /// <summary>True when the source can deliver a rundown of pre-existing state on request (§18.5).</summary>
    public bool SupportsCaptureState { get; init; }

    public required SourceContractStatus ContractStatus { get; init; }
    public OverheadClass Overhead { get; init; } = OverheadClass.Unmeasured;
    public string? OverheadEvidence { get; init; }
    public IReadOnlyList<int> DeniedEventIds { get; init; } = [];
    public IReadOnlyList<AdmittedEventIntent> AdmittedEvents { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Mechanisms this source could serve but the current enablement deliberately omits.</summary>
    public IReadOnlyList<Mechanism> MechanismsOmittedByProfile { get; init; } = [];

    /// <summary>
    /// A kernel flag group's classic event class, whose registration TDH reads for its layout (ADR-035); null for a
    /// manifest provider.
    /// </summary>
    public Guid? ClassicEventClass { get; init; }

    /// <summary>The kernel flags that enable a kernel flag group in a private system logger; 0 for a manifest provider.</summary>
    public ulong KernelFlags { get; init; }

    /// <summary>
    /// The kernel group masks, as PERF_* values, that enable a kernel group with no flag of its own in a private system
    /// logger, as the object manager's handles (ADR-082); empty for every other source.
    /// </summary>
    public IReadOnlyList<uint> KernelGroupMasks { get; init; } = [];

    /// <summary>
    /// The manifest of a provider InterCat defines itself - the content fixture - generated from the type that raises its
    /// events, so its layout is read from that type rather than from a registration the machine may not have; null for
    /// every Windows provider, whose manifest the machine's registration supplies.
    /// </summary>
    public string? EmbeddedManifest { get; init; }
}

/// <summary>
/// The M0 candidate source catalog. Every entry is a lead to be validated against a truth workload;
/// none of them promotes a mechanism tier by itself (§14.2, P27).
/// </summary>
public static class WindowsSourceCatalog
{
    public const string KernelNetworkSourceId = "etw/manifest/Microsoft-Windows-Kernel-Network";
    public const string KernelProcessSourceId = "etw/manifest/Microsoft-Windows-Kernel-Process";
    public const string RpcSourceId = "etw/manifest/Microsoft-Windows-RPC";
    public const string TcpipSourceId = "etw/manifest/Microsoft-Windows-TCPIP";
    public const string KernelFileSourceId = "etw/manifest/Microsoft-Windows-Kernel-File";
    public const string KernelMemorySourceId = "etw/manifest/Microsoft-Windows-Kernel-Memory";
    public const string KernelAlpcSourceId = "etw/kernel-flag/ALPC";
    public const string WinsockAfdSourceId = "etw/manifest/Microsoft-Windows-Winsock-AFD";
    public const string ComPerfSourceId = "etw/manifest/Microsoft-Windows-COM-Perf";

    /// <summary>The object manager's handle group: named synchronization objects' lead, enabled by a group mask (ADR-082).</summary>
    public const string KernelObjectHandlesSourceId = "etw/kernel-group/ObjectHandles";

    /// <summary>The classic event class of the kernel's ALPC events, measured on this workstation (ADR-035's addendum).</summary>
    public static readonly Guid AlpcEventClass = Guid.Parse("45d8cccd-539f-4b72-a8b7-5c683142609a");

    /// <summary>The classic event class of the kernel's object manager events, ObTrace, as this build registers it (§4.2).</summary>
    public static readonly Guid ObjectManagerEventClass = Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7");

    /// <summary><c>EVENT_TRACE_FLAG_ALPC</c>.</summary>
    public const ulong AlpcKernelFlag = 0x0010_0000;

    /// <summary>
    /// <c>EVENT_TRACE_FLAG_PROCESS</c>: the least kernel flag, which starts a private system logger for a kernel group with
    /// no flag of its own (ADR-082).
    /// </summary>
    public const ulong ProcessKernelFlag = 0x0000_0001;

    /// <summary><c>PERF_OB_HANDLE</c>: the object manager's handle group, a handle's creation, close and duplication (ADR-082).</summary>
    public const uint ObjectHandlesGroupMask = 0x8000_0040;

    private static readonly IReadOnlyList<AdmittedFieldIntent> AlpcMessageFields =
    [
        new("MessageID", FieldRole.CorrelationKey,
            Notes: "Reused within seconds across processes: a join key inside one call's window and thread chain only (ADR-034).",
            SourceField: SourceField.AlpcMessageId),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> TcpTransferFields =
    [
        new("PID", FieldRole.ProcessAttribution, Notes: "Payload owner. The event header PID may belong to an unrelated context (section 4.1)."),
        new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.TransportObserved),
        new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the owning process's own endpoint, measured on FX-TCP-001 for send and receive alike."),
        new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the owning process's own endpoint."),
        new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the peer endpoint."),
        new("dport", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the peer endpoint."),
        new("connid", FieldRole.CorrelationKey, Notes: "Reported as zero on several builds; never an identity by itself (R22).", SourceField: SourceField.ConnectionId),
    ];

    /// <summary>
    /// A connection event's fields: a transfer's, without its size. The provider's own messages state a byte count for a
    /// send, a receive and a retransmission and none for a connection attempted, established or closed, whose size field
    /// reads zero on every one observed. Admitted as bytes it counted each such event as a measured zero-byte transfer;
    /// it now measures nothing, as its message says (revision 203).
    /// </summary>
    private static readonly IReadOnlyList<AdmittedFieldIntent> TcpConnectionFields =
        [.. TcpTransferFields.Where(field => field.Role != FieldRole.ByteCount)];

    private static readonly IReadOnlyList<AdmittedFieldIntent> UdpSendFields =
    [
        new("PID", FieldRole.ProcessAttribution, Notes: "Payload owner: the sending process. The event header PID may belong to an unrelated context (section 4.1)."),
        new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.TransportObserved, Notes: "The datagram's payload bytes, equal to the truth log's on FX-UDP-001."),
        new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the sending process's own endpoint, measured on FX-UDP-001."),
        new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the sending process's own endpoint."),
        new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address the datagram was sent to."),
        new("dport", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port the datagram was sent to."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> UdpReceiveFields =
    [
        new("PID", FieldRole.ProcessAttribution, Notes: "Payload owner: the receiving process. On FX-UDP-001 the event header PID differed from it on every receive (section 4.1)."),
        new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.TransportObserved, Notes: "The datagram's payload bytes, equal to the truth log's on FX-UDP-001."),
        new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the datagram's sender. Unlike a TCP receive, a UDP receive names the origin first: measured on FX-UDP-001."),
        new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the datagram's sender."),
        new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the receiving process's own endpoint."),
        new("dport", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the receiving process's own endpoint."),
    ];

    // The IPv6 descriptors carry the IPv4 ones' fields in the same order, with each address 16 bytes in network order.
    private static readonly IReadOnlyList<AdmittedFieldIntent> TcpTransferFields6 = Ipv6(TcpTransferFields);

    private static readonly IReadOnlyList<AdmittedFieldIntent> TcpConnectionFields6 = Ipv6(TcpConnectionFields);

    private static readonly IReadOnlyList<AdmittedFieldIntent> UdpSendFields6 = Ipv6(UdpSendFields);

    private static readonly IReadOnlyList<AdmittedFieldIntent> UdpReceiveFields6 = Ipv6(UdpReceiveFields);

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileCreateFields =
    [
        new("Irp", FieldRole.CorrelationKey, Notes: "Pairs this operation with its OperationEnd completion.", SourceField: SourceField.IoRequestPacket),
        new("FileObject", FieldRole.ResourceHandle, Notes: "Instance handle of one open pipe or file. Reusable, so it is an identity only inside its observed lifetime (R22).", SourceField: SourceField.FileObject),
        new("IssuingThreadId", FieldRole.ThreadAttribution, SourceField: SourceField.IssuingThreadId),
        new("FileName", FieldRole.ResourceName, Notes: "Bounded copy of the object path. A pipe name is metadata, not payload (section 18.2)."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileTransferFields =
    [
        new("Irp", FieldRole.CorrelationKey, Notes: "Pairs this operation with its OperationEnd completion.", SourceField: SourceField.IoRequestPacket),
        new("FileObject", FieldRole.ResourceHandle, SourceField: SourceField.FileObject),
        new("FileKey", FieldRole.ResourceHandle, Notes: "Name key of the object, stable across handles to one name.", SourceField: SourceField.FileKey),
        new("IssuingThreadId", FieldRole.ThreadAttribution, SourceField: SourceField.IssuingThreadId),
        new("IOSize", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.RequestedIo, Notes: "Requested bytes. It is never relabelled as transferred bytes (P3)."),
        new("ByteOffset", FieldRole.Unclassified, SourceField: SourceField.FileByteOffset),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileLifetimeFields =
    [
        new("Irp", FieldRole.CorrelationKey, SourceField: SourceField.IoRequestPacket),
        new("FileObject", FieldRole.ResourceHandle, SourceField: SourceField.FileObject),
        new("FileKey", FieldRole.ResourceHandle, SourceField: SourceField.FileKey),
        new("IssuingThreadId", FieldRole.ThreadAttribution, SourceField: SourceField.IssuingThreadId),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileNameFields =
    [
        new("FileKey", FieldRole.ResourceHandle, SourceField: SourceField.FileKey),
        new("FileName", FieldRole.ResourceName),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileOperationEndFields =
    [
        new("Irp", FieldRole.CorrelationKey, Notes: "The only link back to the operation this completion belongs to.", SourceField: SourceField.IoRequestPacket),
        new("ExtraInformation", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.CompletedIo, Notes: "IO status information: completed bytes for a read or write completion."),
        new("Status", FieldRole.Status),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> RpcCallStartFields =
    [
        new("InterfaceUuid", FieldRole.CorrelationKey, Notes: "The RPC interface the call targets. It identifies an interface, never a process (R22)."),
        new("ProcNum", FieldRole.CorrelationKey, Notes: "Operation number within the interface.", SourceField: SourceField.RpcProcedureNumber),
        new("Protocol", FieldRole.Unclassified, Notes: "Transport sequence identifier, which says whether the call stayed local.", SourceField: SourceField.RpcProtocolSequence),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> RpcCallStopFields =
    [
        new("Status", FieldRole.Status, Notes: "Call status. The descriptor carries no size, so no byte value exists to report (R3)."),
    ];

    // Every Winsock AFD call record's fields the AF_UNIX gate reads and writes (ADR-073, ADR-074). The gate writes into
    // Process and Endpoint its own values, so no record keeps the kernel addresses the source raised.
    private static readonly IReadOnlyList<AdmittedFieldIntent> AfdCallFields =
    [
        new("EnterExit", FieldRole.Unclassified, Notes: "The call's phase: 0 on its entry, 1 on its exit. The gate counts each call at one."),
        new("Location", FieldRole.Unclassified, Notes: "Where in the provider the record was raised; the gate counts a send at one of its two exits by it."),
        new("Process", FieldRole.ProcessAttribution, Notes: "Written by the gate: the process the socket's create named, never the source's kernel address."),
        new("Endpoint", FieldRole.CorrelationKey, Notes: "Written by the gate: the socket's number in the capture, never its reused kernel address (R22).", SourceField: SourceField.ConnectionId),
        new("Status", FieldRole.Status, Notes: "The call's status at its phase."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> AfdCreateFields =
    [
        .. AfdCallFields,
        new("AddressFamily", FieldRole.Unclassified, Notes: "The socket's family: the gate follows a socket created AF_UNIX, 1."),
        new("SocketType", FieldRole.Unclassified, Notes: "The socket's type, 1 for a stream."),
        new("ProcessId", FieldRole.Unclassified, Notes: "The process the socket was made for, which owns it whichever thread raises a record of it."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> AfdTransferFields =
    [
        .. AfdCallFields,
        // The bytes a socket's transport carried, as a TCP or UDP record's are: a stream socket's call completes what its
        // AF_UNIX transport took or gave, and Kernel-Network records none of it, so nothing is counted twice (ADR-076).
        new("BufferLength", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.TransportObserved,
            Notes: "At the exit the gate admits, the bytes the socket's transport carried for the call: a send's, and a receive's taken - its entry's is asked for - equal to the truth's on FX-UNIX-001."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> ProcessStartFields =
    [
        new("ProcessID", FieldRole.ProcessAttribution),
        new("ProcessSequenceNumber", FieldRole.CorrelationKey, Notes: "Non-reusable instance discriminator for PID reuse (I12).", SourceField: SourceField.ProcessStartSequence),
        new("CreateTime", FieldRole.Timing, Notes: "FILETIME of process creation; part of the start key.", SourceField: SourceField.ProcessCreateTime),
        new("ParentProcessID", FieldRole.ProcessAttribution, SourceField: SourceField.ParentProcessId),
        new("ParentProcessSequenceNumber", FieldRole.CorrelationKey, SourceField: SourceField.ParentStartSequence),
        new("SessionID", FieldRole.Unclassified, SourceField: SourceField.ProcessSessionId),
        new("ImageName", FieldRole.ResourceName, Notes: "The image path. On the start and rundown descriptors it follows a variable-length SID and is reached through that SID's own sub-authority count."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> ProcessStopFields =
    [
        new("ProcessID", FieldRole.ProcessAttribution),
        new("ProcessSequenceNumber", FieldRole.CorrelationKey, SourceField: SourceField.ProcessStartSequence),
        new("CreateTime", FieldRole.Timing, SourceField: SourceField.ProcessCreateTime),
        new("ExitTime", FieldRole.Timing, SourceField: SourceField.ProcessExitTime),
        new("ExitCode", FieldRole.Status),
        new("ImageName", FieldRole.ResourceName, Notes: "The image file name as an 8-bit string, the last field of the stop descriptor."),
    ];

    /// <summary>WinINet's own capture of an HTTP exchange's buffers, for the processes a capture names (ADR-037).</summary>
    public const string WinInetCaptureSourceId = ContentSources.WinInetCapture;

    /// <summary>The capture events' own keywords - send, receive, personal data present, packet - and not the operational channel's.</summary>
    public const ulong WinInetCaptureKeywords = 0x0000_0603_0000_0000;

    /// <summary>
    /// Every capture event's layout (ADR-037): its exchange, its buffer's place and ends, its length, its bytes. Declared
    /// before <see cref="All"/>, whose initializer reads it: static fields initialize in the order they are written.
    /// </summary>
    private static readonly IReadOnlyList<AdmittedFieldIntent> WinInetCaptureFields =
    [
        new("SessionId", FieldRole.CorrelationKey, SourceField: SourceField.HttpExchangeId,
            Notes: "WinINet's number for the exchange - a request and its response - within the client process (ADR-037)."),
        new("SequenceNumber", FieldRole.Unclassified, SourceField: SourceField.ContentBufferSequence,
            Notes: "The buffer's place in its part - head or body - from 0."),
        new("Flags", FieldRole.Unclassified, SourceField: SourceField.ContentBufferFlags,
            Notes: "1 marks its part's first buffer and 2 its last; a body ends with an empty last buffer."),
        new("PayloadByteLength", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.ApplicationPayload,
            Notes: "The buffer's length: every byte of it, however many a capture keeps."),
        new("Payload", FieldRole.Content,
            Notes: "The buffer's bytes: part of an HTTP message as the client library held it, above any encryption.")
        {
            ContentClassification = ContentEvidenceClassification.ApplicationPayload,
            ContentEncoding = ContentFieldEncoding.Binary,
        },
    ];

    // A COM call's start at each end and its stop (ADR-077, ADR-079): the client's start names the process at the other end
    // as its target, the server's as its source. Declared before All, whose initializer reads them.
    private static readonly IReadOnlyList<AdmittedFieldIntent> ComClientCallStartFields = ComCallStartFields("TargetProcessId");

    private static readonly IReadOnlyList<AdmittedFieldIntent> ComServerCallStartFields = ComCallStartFields("SourceProcessId");

    private static readonly IReadOnlyList<AdmittedFieldIntent> ComCallStopFields =
    [
        new("CallTraceId", FieldRole.CorrelationKey, SourceField: SourceField.ComCallTraceId,
            Notes: "The number its start carried, which joins the two in the process that raised them.")
        {
            Decides = TraceNumberDecides,
        },
        new("HRESULT", FieldRole.Status,
            Notes: "The method's answer, which the RPC call carrying it does not report: a call answered E_NOINTERFACE "
                + "completes as an RPC call with status 0 (ADR-079)."),
    ];

    public static IReadOnlyList<WindowsSourceDefinition> All { get; } = Build();

    /// <summary>
    /// A COM call start's fields, as COM-Perf lays out a client's and a server's start: its trace number, the process at its
    /// other end, its method and its interface. Its thread fields name nothing the other end recorded and its causality is
    /// no call's identity (ADR-077), and every field after the interface is a pointer, an IPID, an OID or an address, so
    /// none of them is admitted.
    /// </summary>
    private static IReadOnlyList<AdmittedFieldIntent> ComCallStartFields(string otherEnd) =>
    [
        new("CallTraceId", FieldRole.CorrelationKey, SourceField: SourceField.ComCallTraceId,
            Notes: "The call's number within the process that raised it, which its stop repeats: it joins the two there, "
                + "never one process's records to another's.")
        {
            Decides = TraceNumberDecides,
        },
        new(otherEnd, FieldRole.CorrelationKey, SourceField: SourceField.PeerProcessId,
            Notes: "The process at the call's other end, never the record's own: a peer only where ADR-079's link checks it.")
        {
            Decides = "com-call-link-v1 checks the process at a call's other end against the process of the call it reached",
        },
        new("TargetMethod", FieldRole.CorrelationKey, SourceField: SourceField.ComMethodNumber,
            Notes: "The method's number within its interface, which the RPC call carrying it names as its procedure (ADR-079).")
        {
            Decides = "com-call-link-v1 checks a call's method against the RPC call carrying it and the call at its other end",
        },
        new("TargetInterface", FieldRole.CorrelationKey,
            Notes: "The interface the call targets. It identifies an interface, never a process (R22).")
        {
            Decides = "com-call-link-v1 checks a call's interface against the RPC call carrying it and the call at its other end",
        },
    ];

    /// <summary>What the link rule decides by a COM call record's trace number, at its start and at its stop.</summary>
    private const string TraceNumberDecides = "com-call-link-v1 joins a call's start to its stop in the process that raised them";

    /// <summary>The content fixture's source: InterCat's own provider, raised only by its content-fixture workload (ADR-036).</summary>
    public const string ContentFixtureSourceId = "etw/eventsource/InterCat-Fixture-Content";

    private static readonly IReadOnlyList<AdmittedFieldIntent> ContentFixtureFields =
    [
        new("processId", FieldRole.ProcessAttribution,
            Notes: "The workload's own process, which names itself in each message it raises: the record's owner (§2a)."),
        new("conversation", FieldRole.CorrelationKey, Notes: "The fixture's conversation number. It names no process (R22)."),
        new("messageSize", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.ApplicationPayload,
            Notes: "The message's length as the workload raised it: every byte of it, however many the capture keeps."),
        new("message", FieldRole.Content,
            Notes: "The workload's generated message, each record one whole message from its first byte.")
        {
            ContentClassification = ContentEvidenceClassification.ApplicationPayload,
            ContentEncoding = ContentFieldEncoding.Binary,
        },
    ];

    /// <summary>
    /// Sources that are not Windows capabilities but InterCat's own instruments: the content fixture (ADR-036). A profile
    /// can request one, and <see cref="Find"/> resolves it, but the machine's capability report never lists one, because it
    /// states what Windows can deliver on this machine.
    /// </summary>
    public static IReadOnlyList<WindowsSourceDefinition> Fixtures { get; } =
    [
        new WindowsSourceDefinition
        {
            SourceId = ContentFixtureSourceId,
            DisplayName = "InterCat content fixture",
            Kind = SourceKind.ManifestProvider,
            ProviderName = ContentFixtureEventSource.ProviderName,
            Mechanisms = [Mechanism.ApplicationSdk],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = false,
            ContentContract = new ValidatedContentSourceContract(
                [ContentFixtureEventSource.MessageSentId, ContentFixtureEventSource.MessageReceivedId],
                ["message"],
                [ContentEvidenceClassification.ApplicationPayload],
                EnforcesProcessScopeBeforePersistence: false,
                EnforcesChannelScopeBeforePersistence: false,
                "contracts/content-v1.md; the content-fixture workload's truth log",
                OverheadClass.Unmeasured,
                string.Empty),
            FilteringNotes =
                "InterCat's own provider, raised only by its content-fixture workload: the content it carries is the "
                + "workload's generated messages, never another application's, so no process or channel scope applies.",
            StartupBehaviour = "No rundown: a message raised before the capture enabled the provider is not seen.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Overhead = OverheadClass.Unmeasured,
            EmbeddedManifest = System.Diagnostics.Tracing.EventSource.GenerateManifest(
                typeof(ContentFixtureEventSource), string.Empty),
            AdmittedEvents =
            [
                new(ContentFixtureEventSource.MessageSentId, ContentFixtureEventSource.EventVersion, "Fixture message sent",
                    Mechanism.ApplicationSdk, ObservationLayer.Application, ObservationKind.Send, Direction.Outbound,
                    ContentFixtureFields),
                new(ContentFixtureEventSource.MessageReceivedId, ContentFixtureEventSource.EventVersion,
                    "Fixture message received", Mechanism.ApplicationSdk, ObservationLayer.Application, ObservationKind.Receive,
                    Direction.Inbound, ContentFixtureFields),
            ],
            Notes =
            [
                "The layout is the one the raising type declares - the raising process, a conversation number, the "
                + "message's length, then its bytes - read from that type, so the workload and this plan cannot disagree.",
                "A record's owner is the process its payload names, as a kernel record's is; no application provider's "
                + "event header is read as an owner (ADR-030).",
                "The message is kept only under the scoped content fixture policy; under any other it is not admitted.",
            ],
        },
    ];

    /// <summary>
    /// The kernel's classic event classes a private system logger delivers without being asked (ADR-035): its own trace
    /// records, process and thread starts, stops and rundown, and the system's configuration. They are Windows' fixed
    /// classes rather than names looked up on the reading machine, so a display may name them where no plan requested
    /// them, as it names a source's provider.
    /// </summary>
    private static readonly Dictionary<Guid, string> UnaskedClassicClasses = new()
    {
        [Guid.Parse("68fdd900-4a3e-11d1-84f4-0000f80464e3")] = "Kernel trace records (EventTrace class)",
        [Guid.Parse("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c")] = "Kernel process events (Process class)",
        [Guid.Parse("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c")] = "Kernel thread events (Thread class)",
        [Guid.Parse("01853a65-418f-4f36-aefc-dc0f1d2fd235")] = "Kernel system configuration (SystemConfig class)",
    };

    /// <summary>A classic kernel class's name: a kernel flag group's provider, or a class a system logger delivers unasked.</summary>
    public static string? ClassicEventClassName(Guid classGuid) =>
        All.FirstOrDefault(definition => definition.ClassicEventClass == classGuid)?.ProviderName
        ?? UnaskedClassicClasses.GetValueOrDefault(classGuid);

    public static WindowsSourceDefinition? Find(string sourceId)
    {
        foreach (WindowsSourceDefinition definition in All.Concat(Fixtures))
        {
            if (string.Equals(definition.SourceId, sourceId, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>
    /// An IPv4 descriptor's fields as its IPv6 counterpart names them: the same names and meanings, each address read as
    /// the 16 bytes an IPv6 template declares. A template that disagrees is refused field by field when it is compiled.
    /// FX-TCP-002 and FX-UDP-002 measured over IPv6 loopback what FX-TCP-001 and FX-UDP-001 measured over IPv4, so each
    /// note names the fixture of its own family.
    /// </summary>
    private static IReadOnlyList<AdmittedFieldIntent> Ipv6(IReadOnlyList<AdmittedFieldIntent> ipv4) =>
    [
        .. ipv4.Select(field => field with
        {
            Transform = field.Transform == SlotTransform.NetworkOrderIpv4Address
                ? SlotTransform.NetworkOrderIpv6Address
                : field.Transform,
            Notes = field.Notes?
                .Replace("FX-TCP-001", "FX-TCP-002", StringComparison.Ordinal)
                .Replace("FX-UDP-001", "FX-UDP-002", StringComparison.Ordinal),
        }),
    ];

    private static IReadOnlyList<WindowsSourceDefinition> Build()
    {
        var kernelNetwork = new WindowsSourceDefinition
        {
            SourceId = KernelNetworkSourceId,
            DisplayName = "Kernel network (TCP and UDP transfer events over IPv4 and IPv6)",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-Kernel-Network",
            Mechanisms = [Mechanism.Tcp, Mechanism.Udp],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0x30,
            RequestedKeywords = ["KERNEL_NETWORK_KEYWORD_IPV4", "KERNEL_NETWORK_KEYWORD_IPV6"],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes =
                "Scope is limited by keyword and event id. The provider exposes no capture-side process filter, "
                + "so a process-focused view still pays whole-machine capture cost (section 9.4).",
            StartupBehaviour =
                "No rundown of established connections. A flow already open when capture starts is observed only "
                + "from its next transfer, so its lifetime stays uncertain (section 18.5).",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Documented,
            Overhead = OverheadClass.Moderate,
            OverheadEvidence = "bench/results/capture-impact-20260927T115356Z/impact.json",
            AdmittedEvents =
            [
                new(10, 0, "TCPv4 data sent", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, TcpTransferFields),
                new(11, 0, "TCPv4 data received", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, TcpTransferFields),
                new(12, 0, "TCPv4 connection attempted", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Connect, Direction.Outbound, TcpConnectionFields),
                new(13, 0, "TCPv4 disconnect issued", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Disconnect, Direction.DirectionNotApplicable, TcpConnectionFields),
                new(14, 0, "TCPv4 data retransmitted", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, TcpTransferFields),
                new(15, 0, "TCPv4 connection accepted", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Accept, Direction.Inbound, TcpConnectionFields),
                new(42, 0, "UDPv4 datagram sent", Mechanism.Udp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, UdpSendFields),
                new(43, 0, "UDPv4 datagram received", Mechanism.Udp, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, UdpReceiveFields),
                new(26, 0, "TCPv6 data sent", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, TcpTransferFields6),
                new(27, 0, "TCPv6 data received", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, TcpTransferFields6),
                new(28, 0, "TCPv6 connection attempted", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Connect, Direction.Outbound, TcpConnectionFields6),
                new(29, 0, "TCPv6 disconnect issued", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Disconnect, Direction.DirectionNotApplicable, TcpConnectionFields6),
                new(30, 0, "TCPv6 data retransmitted", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, TcpTransferFields6),
                new(31, 0, "TCPv6 connection accepted", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Accept, Direction.Inbound, TcpConnectionFields6),
                new(58, 0, "UDPv6 datagram sent", Mechanism.Udp, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, UdpSendFields6),
                new(59, 0, "UDPv6 datagram received", Mechanism.Udp, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, UdpReceiveFields6),
            ],
            Notes =
            [
                "Retransmissions stay separate observations and are never folded into delivered payload (P5).",
                "Measured on FX-TCP-001: addresses and ports arrive in network byte order, and the saddr and sport "
                + "pair names the owning process's own endpoint on both send and receive descriptors.",
                "Measured on FX-UDP-001: a UDP send names the sender's own endpoint first, and a UDP receive names the "
                + "datagram's sender first, so the receiver's own endpoint is its destination. Records keep both as delivered.",
                "Measured on FX-TCP-002 and FX-UDP-002 over IPv6 loopback: the TCPv6 and UDPv6 descriptors name their "
                + "endpoints as their IPv4 counterparts do, and deliver each address as its 16 bytes in network order. "
                + "The manifest TraceEvent rebuilds omits those fields' length, which TDH reports and the adapter restores.",
                "Capture impact with both keywords, seven pairs on 2026-09-27: a median of 0.00 CPU pp (observed -1.45) "
                + "and no throughput regression, where the IPv4 keyword alone measured 1.61 pp on 2026-09-23. The "
                + "difference is within this machine's noise, so the class stays Moderate, the higher of the two runs.",
                "Loopback traffic is observed on both endpoints; the canonical owner rule of section 5.3 decides the total.",
            ],
        };

        var kernelProcess = new WindowsSourceDefinition
        {
            SourceId = KernelProcessSourceId,
            DisplayName = "Kernel process lifecycle",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-Kernel-Process",
            Mechanisms = [Mechanism.ProcessLifecycle],
            MechanismsOmittedByProfile = [Mechanism.ThreadLifecycle],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0x10,
            RequestedKeywords = ["WINEVENT_KEYWORD_PROCESS"],
            SupportsCaptureSideProcessFilter = true,
            FilteringNotes =
                "Process-id filters are accepted by the provider, but lifecycle context for peers must stay "
                + "collected or attribution degrades (section 9.4). The Explore plan therefore requests no filter.",
            StartupBehaviour =
                "Processes started before the session are delivered only when the capture requests provider "
                + "capture state, which yields ProcessRundown as witnessed presence, not a creation time (section 18.5).",
            SupportsCaptureState = true,
            ContractStatus = SourceContractStatus.Documented,
            Overhead = OverheadClass.Moderate,
            OverheadEvidence = "bench/results/capture-impact-20260927T115356Z/impact.json",
            AdmittedEvents =
            [
                new(1, 4, "Process start", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Create, Direction.DirectionNotApplicable, ProcessStartFields),
                new(2, 2, "Process stop", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Exit, Direction.DirectionNotApplicable, ProcessStopFields),
                new(15, 2, "Process rundown", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Inventory, Direction.DirectionNotApplicable, ProcessStartFields),
            ],
            Notes =
            [
                "ProcessSequenceNumber with CreateTime forms the start key that keeps reused PIDs from merging (I12, R22).",
                "ThreadLifecycle needs WINEVENT_KEYWORD_THREAD, which the capture profiles omit to keep a capture's volume bounded.",
            ],
        };

        var rpc = new WindowsSourceDefinition
        {
            SourceId = RpcSourceId,
            DisplayName = "RPC client and server calls",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-RPC",
            Mechanisms = [Mechanism.Rpc],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = true,
            FilteringNotes =
                "The provider declares no keywords, so a keyword mask cannot scope it. Scope must come from an "
                + "event-id filter; events 10 and 11 carry binary fragments and stay denied by default (P28).",
            StartupBehaviour = "No rundown of calls in flight. A call open at capture start is censored, not failed (I20).",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Documented,
            Overhead = OverheadClass.Low,
            OverheadEvidence = "bench/results/capture-impact-20260927T121747Z/impact.json",
            DeniedEventIds = [10, 11],
            AdmittedEvents =
            [
                new(5, 1, "RPC client call start", Mechanism.Rpc, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Outbound, RpcCallStartFields),
                new(6, 1, "RPC server call start", Mechanism.Rpc, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Inbound, RpcCallStartFields),
                new(7, 1, "RPC client call stop", Mechanism.Rpc, ObservationLayer.Application, ObservationKind.RequestEnd, Direction.Outbound, RpcCallStopFields),
                new(8, 1, "RPC server call stop", Mechanism.Rpc, ObservationLayer.Application, ObservationKind.RequestEnd, Direction.Inbound, RpcCallStopFields),
            ],
            Notes =
            [
                "Start events carry InterfaceUuid, ProcNum and Protocol before variable-length endpoint strings, "
                + "so those three are admitted and the endpoint strings are not.",
                "Stop events carry a status and no size. RPC therefore yields operations, never a byte volume, "
                + "and an RPC annotation never adds transport bytes (I11, P4).",
                "A call is paired with its completion through the event activity id, not by time proximity (P8).",
                "Measured on FX-RPC-001: every record is raised in the process it describes - a client call in the "
                + "caller, a served call in the host - so a record binds to its header's process (ADR-030).",
                "Measured on FX-RPC-002: a call whose client bound ncacn_ip_tcp names Protocol 1, where a "
                + "local call names 3, and its bytes are its TCP connection's alone; the endpoint mapper's records of the "
                + "calls it served over TCP carried no activity id, so they pair with nothing (ADR-004).",
                "Capture impact alone, seven pairs on 2026-09-27: a median of 0.52 CPU pp (pairs from -4.2 to +4.7) "
                + "with no throughput regression, loss-free, so the class is Low. It was recorded once the records "
                + "bind to a process, pair into calls and reach the ladder (revisions 177 to 179).",
            ],
        };

        var tcpip = new WindowsSourceDefinition
        {
            SourceId = TcpipSourceId,
            DisplayName = "TCP/IP stack tracing",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-TCPIP",
            Mechanisms = [Mechanism.Tcp],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes = "No capture profile requests it: its volume and field semantics are unmeasured.",
            StartupBehaviour = "Unmeasured.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Notes = ["Inventoried as a second TCP lead so Kernel-Network findings can be cross-checked later (section 13.4)."],
        };

        var kernelFile = new WindowsSourceDefinition
        {
            SourceId = KernelFileSourceId,
            DisplayName = "Kernel file operations (named pipe path)",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-Kernel-File",
            Mechanisms = [Mechanism.NamedPipe],
            MechanismsOmittedByProfile = [Mechanism.AnonymousPipe],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",

            // FILENAME, FILEIO, OP_END, CREATE, READ and WRITE. FILEIO is required for the Close that ends
            // a resource lifetime, and it is the reason this source is whole-machine expensive.
            MatchAnyKeyword = 0x3F0,
            RequestedKeywords =
            [
                "KERNEL_FILE_KEYWORD_FILENAME",
                "KERNEL_FILE_KEYWORD_FILEIO",
                "KERNEL_FILE_KEYWORD_OP_END",
                "KERNEL_FILE_KEYWORD_CREATE",
                "KERNEL_FILE_KEYWORD_READ",
                "KERNEL_FILE_KEYWORD_WRITE",
            ],
            SupportsCaptureSideProcessFilter = true,
            FilteringNotes =
                "The provider accepts a process-id filter, but a pipe peer lives in another process, so a "
                + "process-scoped capture loses the peer side. A capture that requests it therefore records the whole "
                + "machine's file activity and discloses the cost (section 9.4).",
            StartupBehaviour =
                "No rundown. A pipe opened before capture starts has no observed Create, so its name cannot be "
                + "resolved from this source and its operations stay bound to an unnamed object (section 18.5).",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            AdmittedEvents =
            [
                new(10, 0, "File name create", Mechanism.NamedPipe, ObservationLayer.Resource, ObservationKind.Discovery, Direction.DirectionNotApplicable, FileNameFields),
                new(12, 1, "File create", Mechanism.NamedPipe, ObservationLayer.Resource, ObservationKind.Open, Direction.DirectionNotApplicable, FileCreateFields),
                new(14, 1, "File close", Mechanism.NamedPipe, ObservationLayer.Resource, ObservationKind.Close, Direction.DirectionNotApplicable, FileLifetimeFields),
                new(15, 1, "File read", Mechanism.NamedPipe, ObservationLayer.Resource, ObservationKind.Receive, Direction.Inbound, FileTransferFields),
                new(16, 1, "File write", Mechanism.NamedPipe, ObservationLayer.Resource, ObservationKind.Send, Direction.Outbound, FileTransferFields),
                new(24, 0, "Operation end", Mechanism.NamedPipe, ObservationLayer.Resource, ObservationKind.RequestEnd, Direction.DirectionNotApplicable, FileOperationEndFields),
            ],
            Notes =
            [
                "Registration is not evidence of pipe traffic or peers; only a measured fixture assigns a tier.",
                "IOSize is requested bytes and OperationEnd carries completed bytes. The two are separate byte domains "
                + "and are never summed or substituted (P3, I6).",
                "Equal pipe names do not identify one instance: a FileObject binds an operation to one open instance "
                + "only inside its observed lifetime (R22, I12).",
                "This source observes the whole machine's file activity, the dominant overhead of a capture that requests it.",
            ],
        };

        var kernelMemory = new WindowsSourceDefinition
        {
            SourceId = KernelMemorySourceId,
            DisplayName = "Kernel memory (shared section lead)",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-Kernel-Memory",
            Mechanisms = [Mechanism.SharedSection],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes = "No capture profile requests it.",
            StartupBehaviour = "Unmeasured.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Notes =
            [
                "Mapping size and committed pages are capacity, not transfer volume (section 5, P3).",
                "Ordinary loads and stores through a mapped pointer produce no event stream (section 4.1).",
            ],
        };

        var alpc = new WindowsSourceDefinition
        {
            SourceId = KernelAlpcSourceId,
            DisplayName = "ALPC kernel flag group",
            Kind = SourceKind.KernelFlagGroup,
            ProviderName = "Kernel ALPC (EVENT_TRACE_FLAG_ALPC)",
            Mechanisms = [Mechanism.Alpc],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes =
                "A kernel flag group, not a manifest provider: only a private system logger receives it, and a capture "
                + "whose profile needs it makes its one session one, the kernel flags first (ADR-035).",
            StartupBehaviour = "No rundown: a message in flight at capture start is seen only from its next event.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Overhead = OverheadClass.Moderate,
            OverheadEvidence = "bench/results/alpc-impact-20260927T171439Z/series-1.json",
            ClassicEventClass = AlpcEventClass,
            KernelFlags = AlpcKernelFlag,
            AdmittedEvents =
            [
                new(0, 2, "ALPC message send", Mechanism.Alpc, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, AlpcMessageFields) { Opcode = 33 },
                new(0, 2, "ALPC message receive", Mechanism.Alpc, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, AlpcMessageFields) { Opcode = 34 },
            ],
            Notes =
            [
                "There is no registered ALPC manifest provider to inventory, so its absence from the registry is expected.",
                "Documented send and receive payloads expose a message identifier, not a size or content contract (section 4.1).",
                "A classic event: its header names ALPC's class with event id 0 and version 2, and only its opcode - 33 send, "
                + "34 receive - tells the two apart; TDH reads their one 32-bit message id from the class's registration.",
                "Collection alone measured 1.86 CPU pp at about 1,160 events a second, Moderate, so it is never in Explore "
                + "(ADR-034's addendum); the profile that admits it states its own class once measured through the product.",
                "Its private system logger also delivers, unasked, the kernel's process and thread starts, stops and rundown "
                + "and the system's configuration; they are counted as unrequested classes, each under its own class, and "
                + "never admitted.",
            ],
        };

        var winInetCapture = new WindowsSourceDefinition
        {
            SourceId = WinInetCaptureSourceId,
            DisplayName = "WinINet HTTP exchanges",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-WinINet-Capture",
            Mechanisms = [Mechanism.Http],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = WinInetCaptureKeywords,
            RequestedKeywords =
                ["WININET_KEYWORD_SEND", "WININET_KEYWORD_RECEIVE", "WININET_KEYWORD_PII_PRESENT", "WININET_KEYWORD_PACKET"],
            SupportsCaptureSideProcessFilter = true,
            FilteringNotes =
                "Enabled only for the processes a capture names, by the session's process filter, which keeps every other "
                + "process's records out of the session (ADR-037). Unscoped it would record every WinINet client's requests "
                + "on the machine, their cookies and authorization headers among them.",
            StartupBehaviour = "No rundown: an exchange made before the capture enabled the provider is not seen.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Overhead = OverheadClass.Low,
            OverheadEvidence = "bench/results/wininet-capture-impact-20260928T105549Z/impact.json",
            ContentContract = new ValidatedContentSourceContract(
                [2001, 2002, 2003, 2004],
                ["Payload"],
                [ContentEvidenceClassification.ApplicationPayload],
                EnforcesProcessScopeBeforePersistence: true,
                EnforcesChannelScopeBeforePersistence: false,
                "ADR-037; bench/results/wininet-capture-feasibility-20260928T102426Z; over TLS, bench/results/wininet-capture-feasibility-tls-20260928T120408Z",
                OverheadClass.Low,
                "bench/results/wininet-capture-impact-20260928T105549Z/impact.json")
            {
                // Measured over TLS (FX-HTTP-002): every part matched the server's decrypted bytes, and nothing in a record
                // says whether its exchange was encrypted.
                ContentStatement = "WinINet holds each message above any encryption, so an HTTPS exchange is kept as its "
                    + "plaintext, headers, cookies and authorization included.",
            },
            AdmittedEvents =
            [
                new(2001, 0, "HTTP request head sent", Mechanism.Http, ObservationLayer.Application, ObservationKind.Send,
                    Direction.Outbound, WinInetCaptureFields),
                new(2002, 0, "HTTP request body sent", Mechanism.Http, ObservationLayer.Application, ObservationKind.Send,
                    Direction.Outbound, WinInetCaptureFields),
                new(2003, 0, "HTTP response head received", Mechanism.Http, ObservationLayer.Application, ObservationKind.Receive,
                    Direction.Inbound, WinInetCaptureFields),
                new(2004, 0, "HTTP response body received", Mechanism.Http, ObservationLayer.Application, ObservationKind.Receive,
                    Direction.Inbound, WinInetCaptureFields),
            ],
            Notes =
            [
                "Measured on FX-HTTP-001 (ADR-037): every head and body of every exchange, concatenated from its buffers, was "
                + "the wire's; the session id names the exchange, the sequence number a part's buffers from 0, and the flags "
                + "its first and last.",
                "Every record is raised in the client process that made the exchange, so a record binds to its header's "
                + "process (ADR-030, process-binding-v4).",
                "Measured on FX-HTTP-005 (ADR-037): a client working WinINet asynchronously raises the same records as one "
                + "working it synchronously, and in either a record is raised on whichever of the process's threads finished "
                + "the step it records - the calling thread where the call finished at once, one of WinINet's own where it "
                + "waited, every head among them - so its header's thread is not the thread that made the request.",
                "Measured on FX-HTTP-006 (ADR-037): an exchange WinINet makes over HTTP/2 is recorded as one over HTTP/1.1, its "
                + "bodies as its client sent and read them and its heads in HTTP/1.1's form, which no wire carried, so the "
                + "version a head names is WinINet's and no record says which protocol carried the exchange.",
                "Measured on FX-HTTP-007 (ADR-037): a request body its client writes in pieces with InternetWriteFile is kept "
                + "a buffer to a write - one past 64,511 bytes cut there, as a body handed over whole is - and ended by an empty "
                + "buffer flagged last.",
                "Measured on FX-HTTP-004 and FX-HTTP-008 (ADR-037): a response body in gzip, deflate or br is kept as its "
                + "client read it - coded, or decoded where the client had WinINet decode it - and one in the zlib-format "
                + "deflate WinINet cannot decode is kept as one buffer of no byte.",
                "Measured on FX-HTTP-009 (ADR-037): a redirect WinINet follows is kept as two exchanges - the request redirected "
                + "and its redirect, and the request WinINet made to the location under a number of its own, begun once the "
                + "redirect's response ended - and no record ties the two.",
                "Measured on FX-HTTP-010 (ADR-037): a request through a proxy is kept as WinINet sent it there - over HTTP its "
                + "target the whole address; over TLS after a CONNECT, which is kept as an exchange of its own, its answer of no "
                + "body, the request through the tunnel kept as over TLS directly.",
                "Measured on FX-HTTP-011 (ADR-037): a challenge for Basic credentials and the request WinINet then sends with them "
                + "are kept as an exchange each; once answered, WinINet sends the credentials with every later request unasked, and "
                + "a request's Authorization field is kept as sent.",
                "Measured on FX-HTTP-012 (ADR-037): a proxy's Basic challenge is met as a server's - over HTTP on a request sent "
                + "it whole, over TLS on its CONNECT - each challenge and its answer kept as an exchange each, and a request through "
                + "a tunnel carries no credentials.",
                "Only WinINet's clients raise these records: .NET's HTTP client, WinHTTP and browsers' own stacks do not.",
                "Its message bytes are kept only under a scoped content policy; under any other, the buffer's length is.",
                "Capture impact, seven pairs of FX-HTTP-001 on 2026-09-28 with 4,096 exchanges each: a median of 0.71 CPU "
                + "pp and 2.5% of the workload's time for 179,277 records and 640 MB copied, loss-free, so the class is Low.",
            ],
        };

        var winsockAfd = new WindowsSourceDefinition
        {
            SourceId = WinsockAfdSourceId,
            DisplayName = "Winsock AFD socket calls (Unix-domain socket lead)",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-Winsock-AFD",
            Mechanisms = [Mechanism.UnixDomainSocket],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",

            // AFD_KW_SOURCE_WINSOCK: the socket calls, which every event the gate counts carries.
            MatchAnyKeyword = 0x4,
            RequestedKeywords = ["AFD_KW_SOURCE_WINSOCK"],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes =
                "The opt-in Unix sockets profile alone requests it, for the ten calls its gate counts, each enabled by its "
                + "event id. Measured on FX-UNIX-001: a session's process filter does not hold it - a decoy outside the filter "
                + "had its every record delivered - so a capture requesting it collects every socket call of every process, "
                + "TCP's and UDP's too.",
            StartupBehaviour = "No rundown: a socket made before a capture starts has no create in it, so its records belong to no socket (R22).",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Overhead = OverheadClass.Moderate,
            OverheadEvidence = "bench/results/afd-impact-unix-20261104T063455Z/impact.json",

            // The calls the AF_UNIX gate counts: no buffer address and no socket address is admitted, the path being
            // cut to 14 characters (ADR-072, ADR-073).
            AdmittedEvents =
            [
                new(1000, 0, "Socket created", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Open, Direction.DirectionNotApplicable, AfdCreateFields),
                new(1001, 0, "Socket closed", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Close, Direction.DirectionNotApplicable, AfdCallFields),
                new(1003, 0, "Socket send", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound, AfdTransferFields),
                new(1004, 0, "Socket receive", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, AfdTransferFields),
                new(1017, 0, "Socket connected", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Connect, Direction.Outbound, AfdCallFields),
                new(1020, 0, "Socket connected (ConnectEx)", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Connect, Direction.Outbound, AfdCallFields),
                new(1023, 0, "Connection accepted", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Accept, Direction.Inbound, AfdCallFields),
                new(1027, 0, "Connection accepted (AcceptEx)", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Accept, Direction.Inbound, AfdCallFields),
                new(1029, 0, "Socket bound", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Bind, Direction.DirectionNotApplicable, AfdCallFields),
                new(1030, 0, "Socket bound to an address", Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Bind, Direction.DirectionNotApplicable, AfdCallFields),
            ],
            Notes =
            [
                "Collection alone, nine pairs each over AF_UNIX and TCP traffic at about 490,000 records a second machine-wide, "
                + "loss-free: a median of 1.6 CPU pp over AF_UNIX's, where an earlier nine measured none, and 0.5 over TCP's, "
                + "the workload 2.8 and 4.1% slower; the class is the higher, Moderate, so only an opt-in profile admits "
                + "it, as ALPC's. It was measured collecting both of AFD's source keywords and every event, more than the "
                + "profile requests.",
                "Measured on FX-UNIX-001: every AF_UNIX socket of the workload's two processes - created with its family, "
                + "bound, listening, connected, accepted and closed - and every send and receive with its length, exactly "
                + "the truth's at both ends; Kernel-Network held none of it.",
                "A record names its socket by its endpoint's kernel address, which the kernel frees and reuses within "
                + "seconds, so a socket is its create to the next create at that address, owned by the process its create "
                + "names, never identified by the address alone (R22).",
                "A completion is raised on the thread that finished it, often the peer process's, so a record's header "
                + "names no owner; its socket's create does.",
                "A bind's or connect's address holds 16 bytes, an AF_UNIX path's first 14 characters, so a socket's name is "
                + "not observed whole and no peer is resolved by it.",
                "Measured through the product on FX-UNIX-001, under the Unix sockets profile: every truth send and receive a row "
                + "of its owner, bound to its socket with the bytes it moved, 1,920 of 1,920, and every connection a socket created "
                + "and closed. No peer is named, so its tier is experimental evidence (ADR-075).",
                "Registration is not evidence of traffic; only a measured fixture assigns a tier.",
            ],
        };

        // COM calls' lead (ADR-077, ADR-078, ADR-079): inventoried, admitting its four synchronous call events, requested by
        // the opt-in COM calls profile beside RPC, whose calls rpc-call-peer-v2 serves through them; its measured notes are
        // what icat capabilities states of it.
        var comPerf = new WindowsSourceDefinition
        {
            SourceId = ComPerfSourceId,
            DisplayName = "COM call starts and stops (COM call lead)",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-COM-Perf",
            Mechanisms = [Mechanism.ComCall],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = true,
            FilteringNotes =
                "Only the opt-in COM calls profile requests it. Measured on FX-COM-001: a session's process filter holds it - its two "
                + "processes' every call arrived, and nothing of a decoy pair making as many calls outside the filter or of any "
                + "other process - as it is raised in the process that makes or serves a call.",
            StartupBehaviour = "No rundown: a call made before a capture starts is not seen, nor an object exported before it.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Overhead = OverheadClass.Low,
            OverheadEvidence = "bench/results/com-perf-impact-20261104T160252Z/impact.json",
            AdmittedEvents =
            [
                new(39, 1, "COM client call start", Mechanism.ComCall, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Outbound, ComClientCallStartFields),
                new(40, 0, "COM client call stop", Mechanism.ComCall, ObservationLayer.Application, ObservationKind.RequestEnd, Direction.Outbound, ComCallStopFields),
                new(43, 2, "COM server call start", Mechanism.ComCall, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Inbound, ComServerCallStartFields),
                new(44, 0, "COM server call stop", Mechanism.ComCall, ObservationLayer.Application, ObservationKind.RequestEnd, Direction.Inbound, ComCallStopFields),
            ],
            Notes =
            [
                "Collection alone, two series of nine pairs over 16,384 unpaced calls at about 65,000 records a second, "
                + "loss-free: a median of 0.6 and 0.3 CPU pp, Low; but the workload ran 18.1% and 17.3% slower, past the 5% "
                + "throughput target, as a call's four records are written in the threads that make and serve it. Only an "
                + "opt-in profile stating it may admit it, never Explore.",
                "Measured on FX-COM-001: every out-of-process COM call at both ends - its client's start naming the server's "
                + "process and the server's naming the client's, with its interface and method, each start joined to its stop "
                + "by its call trace number, with its HRESULT.",
                "A causality names a chain of calls, not one, and the threads its records name are none the other end recorded. "
                + "A call's two ends are linked by the client's activity id, which the server's records carry: one server call "
                + "for each client call, naming the client as the client names the server, inside the client's call (ADR-079).",
                "Measured on FX-COM-001 beside RPC: each call is carried by one RPC call at each end, on its interface with its "
                + "method as the procedure - at the client the RPC call of its activity id, at the server the one around it on "
                + "its thread - so the two are one call, never two (ADR-079). Its HRESULT is the method's answer, which the RPC "
                + "call's status is not: a call answered E_NOINTERFACE stopped with RPC status 0.",
                "A call across an apartment within one process is recorded as an out-of-process one is, both ends naming the "
                + "process itself (FX-COM-002): a call is between processes only where the process it names is another.",
                "Its activation events carry no field, so none names what was activated or becomes a call count.",
                "Its four synchronous call events are admitted: a call's trace number, interface and method, the process at "
                + "its other end and its HRESULT - never a thread, a pointer, an IPID, an OID or an address. Its "
                + "asynchronous calls, which no fixture made, are not.",
                "Measured through the product on FX-COM-001, under the COM calls profile: every truth call its client's COM call "
                + "with the HRESULT it logged, linked to the server's call of the same truth call, and carried by one RPC call "
                + "served through it, 1,920 of 1,920; and on FX-COM-003, a client following 8 to 32 servers started one after "
                + "another, every call linked to the server instance that served it, 1,792 of 1,792. A COM call carries no byte and "
                + "names no object, so its tier is experimental evidence (ADR-080).",
                "Registration is not evidence of traffic; only a measured fixture assigns a tier, and only the opt-in COM calls "
                + "profile requests this source, beside RPC, never Explore.",
            ],
        };

        // Named synchronization objects' lead (ADR-082): inventoried and requested by no profile, admitting no event until its
        // records are bound and joined; a capture that asks for it sets its group mask on its private system logger. Its
        // measured notes are what icat capabilities states of it.
        var objectHandles = new WindowsSourceDefinition
        {
            SourceId = KernelObjectHandlesSourceId,
            DisplayName = "Object manager handle group (named synchronization object lead)",
            Kind = SourceKind.KernelFlagGroup,
            ProviderName = "Kernel object manager handles (PERF_OB_HANDLE)",
            Mechanisms = [Mechanism.Synchronization],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0,
            RequestedKeywords = [],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes =
                "No capture profile requests it. A kernel group of the object manager, enabled by the group mask PERF_OB_HANDLE "
                + "and by no kernel flag: only a private system logger receives it. A capture that asks for it sets the mask on its "
                + "own logger by the session's handle, once the kernel's process flag has started the logger where no other flag "
                + "does; TraceEvent sets group masks only on the NT Kernel Logger, a session a capture never uses (ADR-035, "
                + "ADR-082). It has no process filter: it records every process's handles.",
            StartupBehaviour =
                "At the session's stop a rundown names every handle still open, with its process and its object's name, and every "
                + "object type's name; a handle opened before the capture began is seen only at its close or in that rundown.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Overhead = OverheadClass.Low,
            OverheadEvidence = "bench/results/sync-objects-impact-handles-20261105T035540Z/impact.json",
            ClassicEventClass = ObjectManagerEventClass,
            KernelGroupMasks = [ObjectHandlesGroupMask],
            Notes =
            [
                "There is no registered object manager manifest provider to inventory, so its absence from the registry is expected.",
                "Collection alone, two series of nine pairs over a handle-heavy workload, loss-free: a median of 0.4 and 0.0 CPU "
                + "pp, Low; but the workload ran 5.3 and 8.3% lower in throughput, past the 5% target, as a handle's creation and "
                + "close each raise a record in the thread that makes the call. Only an opt-in profile stating it may admit it, "
                + "never Explore.",
                "Measured on FX-SYNC-001: each process's handle to a named object two processes share, its creation and its "
                + "close, both processes' at the object's one address, typed by the type rundown. A creation names nothing; the "
                + "close names the object, so a handle is named at its close.",
                "A named object is one object by its address within its lifetime, never by its name or its address alone: the "
                + "object group, PERF_OB_OBJECT, records each object's creation and deletion by address, but its references come "
                + "with it, about 2.7 million records a second and a fifth of a handle-heavy workload's throughput, Moderate.",
                "No record names a signal or a wait on an object. The dispatcher's ready thread names neither the object nor the "
                + "thread that readied it, and threads of two processes ready each other with no object between them, so a ready "
                + "is never a signal by itself.",
            ],
        };

        return [kernelNetwork, kernelProcess, rpc, tcpip, kernelFile, kernelMemory, alpc, winInetCapture, winsockAfd, comPerf, objectHandles];
    }
}
