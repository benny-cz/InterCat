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
}

/// <summary>A field the adapter intends to admit, with the semantics it would carry (R2).</summary>
public sealed record AdmittedFieldIntent(
    string FieldName,
    FieldRole Role,
    MeasurementUnit? Unit = null,
    ByteDomain? ByteDomain = null,
    SlotTransform Transform = SlotTransform.None,
    string? Notes = null);

/// <summary>One event descriptor the adapter intends to admit under a metadata-only policy (§18.2).</summary>
public sealed record AdmittedEventIntent(
    int EventId,
    int Version,
    string Name,
    Mechanism Mechanism,
    ObservationKind Kind,
    Direction Direction,
    IReadOnlyList<AdmittedFieldIntent> Fields);

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

    private static readonly IReadOnlyList<AdmittedFieldIntent> TcpTransferFields =
    [
        new("PID", FieldRole.ProcessAttribution, Notes: "Payload owner. The event header PID may belong to an unrelated context (section 4.1)."),
        new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.TransportObserved),
        new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the owning process's own endpoint, measured on FX-TCP-001 for send and receive alike."),
        new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the owning process's own endpoint."),
        new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address, Notes: "Network-order address of the peer endpoint."),
        new("dport", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderPort, Notes: "Network-order port of the peer endpoint."),
        new("connid", FieldRole.CorrelationKey, Notes: "Reported as zero on several builds; never an identity by itself (R22)."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileCreateFields =
    [
        new("Irp", FieldRole.CorrelationKey, Notes: "Pairs this operation with its OperationEnd completion."),
        new("FileObject", FieldRole.ResourceHandle, Notes: "Instance handle of one open pipe or file. Reusable, so it is an identity only inside its observed lifetime (R22)."),
        new("IssuingThreadId", FieldRole.ThreadAttribution),
        new("FileName", FieldRole.ResourceName, Notes: "Bounded copy of the object path. A pipe name is metadata, not payload (section 18.2)."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileTransferFields =
    [
        new("Irp", FieldRole.CorrelationKey, Notes: "Pairs this operation with its OperationEnd completion."),
        new("FileObject", FieldRole.ResourceHandle),
        new("FileKey", FieldRole.ResourceHandle, Notes: "Name key of the object, stable across handles to one name."),
        new("IssuingThreadId", FieldRole.ThreadAttribution),
        new("IOSize", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.RequestedIo, Notes: "Requested bytes. It is never relabelled as transferred bytes (P3)."),
        new("ByteOffset", FieldRole.Unclassified),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileLifetimeFields =
    [
        new("Irp", FieldRole.CorrelationKey),
        new("FileObject", FieldRole.ResourceHandle),
        new("FileKey", FieldRole.ResourceHandle),
        new("IssuingThreadId", FieldRole.ThreadAttribution),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileNameFields =
    [
        new("FileKey", FieldRole.ResourceHandle),
        new("FileName", FieldRole.ResourceName),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> FileOperationEndFields =
    [
        new("Irp", FieldRole.CorrelationKey, Notes: "The only link back to the operation this completion belongs to."),
        new("ExtraInformation", FieldRole.ByteCount, MeasurementUnit.Bytes, Domain.ByteDomain.CompletedIo, Notes: "IO status information: completed bytes for a read or write completion."),
        new("Status", FieldRole.Status),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> RpcCallStartFields =
    [
        new("InterfaceUuid", FieldRole.CorrelationKey, Notes: "The RPC interface the call targets. It identifies an interface, never a process (R22)."),
        new("ProcNum", FieldRole.CorrelationKey, Notes: "Operation number within the interface."),
        new("Protocol", FieldRole.Unclassified, Notes: "Transport sequence identifier, which says whether the call stayed local."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> RpcCallStopFields =
    [
        new("Status", FieldRole.Status, Notes: "Call status. The descriptor carries no size, so no byte value exists to report (R3)."),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> ProcessStartFields =
    [
        new("ProcessID", FieldRole.ProcessAttribution),
        new("ProcessSequenceNumber", FieldRole.CorrelationKey, Notes: "Non-reusable instance discriminator for PID reuse (I12)."),
        new("CreateTime", FieldRole.Timing, Notes: "FILETIME of process creation; part of the start key."),
        new("ParentProcessID", FieldRole.ProcessAttribution),
        new("ParentProcessSequenceNumber", FieldRole.CorrelationKey),
        new("SessionID", FieldRole.Unclassified),
    ];

    private static readonly IReadOnlyList<AdmittedFieldIntent> ProcessStopFields =
    [
        new("ProcessID", FieldRole.ProcessAttribution),
        new("ProcessSequenceNumber", FieldRole.CorrelationKey),
        new("CreateTime", FieldRole.Timing),
        new("ExitTime", FieldRole.Timing),
        new("ExitCode", FieldRole.Status),
    ];

    public static IReadOnlyList<WindowsSourceDefinition> All { get; } = Build();

    public static WindowsSourceDefinition? Find(string sourceId)
    {
        foreach (WindowsSourceDefinition definition in All)
        {
            if (string.Equals(definition.SourceId, sourceId, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    private static IReadOnlyList<WindowsSourceDefinition> Build()
    {
        var kernelNetwork = new WindowsSourceDefinition
        {
            SourceId = KernelNetworkSourceId,
            DisplayName = "Kernel network (TCP and UDP transfer events)",
            Kind = SourceKind.ManifestProvider,
            ProviderName = "Microsoft-Windows-Kernel-Network",
            Mechanisms = [Mechanism.Tcp],
            MechanismsOmittedByProfile = [Mechanism.Udp],
            RequiredPrivilege = PrivilegeRequirement.Administrator,
            Level = "win:Informational",
            MatchAnyKeyword = 0x10,
            RequestedKeywords = ["KERNEL_NETWORK_KEYWORD_IPV4"],
            SupportsCaptureSideProcessFilter = false,
            FilteringNotes =
                "Scope is limited by keyword and event id. The provider exposes no capture-side process filter, "
                + "so a process-focused view still pays whole-machine capture cost (section 9.4).",
            StartupBehaviour =
                "No rundown of established connections. A flow already open when capture starts is observed only "
                + "from its next transfer, so its lifetime stays uncertain (section 18.5).",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Documented,
            Overhead = OverheadClass.Low,
            OverheadEvidence = "bench/results/capture-impact-20260921T200502Z/impact.json",
            AdmittedEvents =
            [
                new(10, 0, "TCPv4 data sent", Mechanism.Tcp, ObservationKind.Send, Direction.Outbound, TcpTransferFields),
                new(11, 0, "TCPv4 data received", Mechanism.Tcp, ObservationKind.Receive, Direction.Inbound, TcpTransferFields),
                new(12, 0, "TCPv4 connection attempted", Mechanism.Tcp, ObservationKind.Connect, Direction.Outbound, TcpTransferFields),
                new(13, 0, "TCPv4 disconnect issued", Mechanism.Tcp, ObservationKind.Disconnect, Direction.DirectionNotApplicable, TcpTransferFields),
                new(14, 0, "TCPv4 data retransmitted", Mechanism.Tcp, ObservationKind.Send, Direction.Outbound, TcpTransferFields),
                new(15, 0, "TCPv4 connection accepted", Mechanism.Tcp, ObservationKind.Accept, Direction.Inbound, TcpTransferFields),
            ],
            Notes =
            [
                "Retransmissions stay separate observations and are never folded into delivered payload (P5).",
                "Measured on FX-TCP-001: addresses and ports arrive in network byte order, and the saddr and sport "
                + "pair names the owning process's own endpoint on both send and receive descriptors.",
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
            Overhead = OverheadClass.Low,
            OverheadEvidence = "bench/results/capture-impact-20260921T200502Z/impact.json",
            AdmittedEvents =
            [
                new(1, 4, "Process start", Mechanism.ProcessLifecycle, ObservationKind.Create, Direction.DirectionNotApplicable, ProcessStartFields),
                new(2, 2, "Process stop", Mechanism.ProcessLifecycle, ObservationKind.Exit, Direction.DirectionNotApplicable, ProcessStopFields),
                new(15, 2, "Process rundown", Mechanism.ProcessLifecycle, ObservationKind.Inventory, Direction.DirectionNotApplicable, ProcessStartFields),
            ],
            Notes =
            [
                "ProcessSequenceNumber with CreateTime forms the start key that keeps reused PIDs from merging (I12, R22).",
                "ThreadLifecycle needs WINEVENT_KEYWORD_THREAD, which this plan omits to keep the M0 volume bounded.",
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
            DeniedEventIds = [10, 11],
            AdmittedEvents =
            [
                new(5, 1, "RPC client call start", Mechanism.Rpc, ObservationKind.RequestStart, Direction.Outbound, RpcCallStartFields),
                new(6, 1, "RPC server call start", Mechanism.Rpc, ObservationKind.RequestStart, Direction.Inbound, RpcCallStartFields),
                new(7, 1, "RPC client call stop", Mechanism.Rpc, ObservationKind.RequestEnd, Direction.Outbound, RpcCallStopFields),
                new(8, 1, "RPC server call stop", Mechanism.Rpc, ObservationKind.RequestEnd, Direction.Inbound, RpcCallStopFields),
            ],
            Notes =
            [
                "Start events carry InterfaceUuid, ProcNum and Protocol before variable-length endpoint strings, "
                + "so those three are admitted and the endpoint strings are not.",
                "Stop events carry a status and no size. RPC therefore yields operations, never a byte volume, "
                + "and an RPC annotation never adds transport bytes (I11, P4).",
                "A call is paired with its completion through the event activity id, not by time proximity (P8).",
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
            FilteringNotes = "Not enabled by the M0 plan: its volume and field semantics are unmeasured.",
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
                + "process-scoped capture loses the peer side. The M0 plan therefore captures whole-machine "
                + "file activity and discloses the cost (section 9.4).",
            StartupBehaviour =
                "No rundown. A pipe opened before capture starts has no observed Create, so its name cannot be "
                + "resolved from this source and its operations stay bound to an unnamed object (section 18.5).",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            AdmittedEvents =
            [
                new(10, 0, "File name create", Mechanism.NamedPipe, ObservationKind.Discovery, Direction.DirectionNotApplicable, FileNameFields),
                new(12, 1, "File create", Mechanism.NamedPipe, ObservationKind.Open, Direction.DirectionNotApplicable, FileCreateFields),
                new(14, 1, "File close", Mechanism.NamedPipe, ObservationKind.Close, Direction.DirectionNotApplicable, FileLifetimeFields),
                new(15, 1, "File read", Mechanism.NamedPipe, ObservationKind.Receive, Direction.Inbound, FileTransferFields),
                new(16, 1, "File write", Mechanism.NamedPipe, ObservationKind.Send, Direction.Outbound, FileTransferFields),
                new(24, 0, "Operation end", Mechanism.NamedPipe, ObservationKind.RequestEnd, Direction.DirectionNotApplicable, FileOperationEndFields),
            ],
            Notes =
            [
                "Registration is not evidence of pipe traffic or peers; only a measured fixture assigns a tier (IC-005).",
                "IOSize is requested bytes and OperationEnd carries completed bytes. The two are separate byte domains "
                + "and are never summed or substituted (P3, I6).",
                "Equal pipe names do not identify one instance: a FileObject binds an operation to one open instance "
                + "only inside its observed lifetime (R22, I12).",
                "This source observes the whole machine's file activity, which is the dominant overhead of the M0 plan.",
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
            FilteringNotes = "Not enabled by the M0 plan.",
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
                "A kernel flag group, not a manifest provider: it needs a system logger session, which the M0 "
                + "owned-session experiment does not create (section 18.2).",
            StartupBehaviour = "Unmeasured.",
            SupportsCaptureState = false,
            ContractStatus = SourceContractStatus.Experimental,
            Notes =
            [
                "There is no registered ALPC manifest provider to inventory, so its absence from the registry is expected.",
                "Documented send and receive payloads expose a message identifier, not a size or content contract (section 4.1).",
            ],
        };

        return [kernelNetwork, kernelProcess, rpc, tcpip, kernelFile, kernelMemory, alpc];
    }
}
