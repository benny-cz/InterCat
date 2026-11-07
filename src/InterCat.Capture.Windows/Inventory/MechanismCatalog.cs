using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// One mechanism as InterCat's capability catalog explains it (§4.1): the sources this build's catalog holds for it, the
/// tiers fixtures measured on each build, and in words where it stands and why - for one no source records, what keeps it
/// out. It is what InterCat knows, read from no machine: a capability report says what one machine registers (P27).
/// </summary>
public sealed record MechanismCatalogEntry
{
    public required Mechanism Mechanism { get; init; }

    /// <summary>The mechanism as a row names it.</summary>
    public required string Name { get; init; }

    /// <summary>Where it stands and why: what its records can say and what they cannot, and the decision that set it.</summary>
    public required string Explanation { get; init; }

    /// <summary>The catalog's sources that record it, whether or not a profile requests them.</summary>
    public required IReadOnlyList<string> SourceIds { get; init; }

    /// <summary>The catalog's sources that could serve it though no profile requests them for it.</summary>
    public required IReadOnlyList<string> UnrequestedSourceIds { get; init; }

    /// <summary>Its latest fixture evidence on each build the fixtures measured it on, the latest first.</summary>
    public required IReadOnlyList<MeasuredCoverageEntry> Measured { get; init; }
}

/// <summary>
/// The capability catalog (§4.1): every mechanism EN-Mechanism names but the unknown one, each with the sources that record
/// it, the tiers fixtures measured, and why it stands where it does - an unsupported mechanism explained, never left out.
/// </summary>
public static class MechanismCatalog
{
    /// <summary>Every mechanism, in EN-Mechanism's order.</summary>
    public static IReadOnlyList<MechanismCatalogEntry> Entries { get; } =
    [
        .. Enum.GetValues<Mechanism>().Where(mechanism => mechanism != Mechanism.UnknownMechanism).Select(mechanism => new MechanismCatalogEntry
        {
            Mechanism = mechanism,
            Name = MechanismText.Name(mechanism),
            Explanation = Explain(mechanism),
            // What an instrumented application raises is recorded by a source of its own, not a capability of Windows (ADR-103).
            SourceIds =
            [
                .. WindowsSourceCatalog.All.Concat(WindowsSourceCatalog.Cooperative)
                    .Where(source => source.Mechanisms.Contains(mechanism))
                    .Select(source => source.SourceId),
            ],
            UnrequestedSourceIds =
            [
                .. WindowsSourceCatalog.All.Where(source => source.MechanismsOmittedByProfile.Contains(mechanism)).Select(source => source.SourceId),
            ],
            Measured =
            [
                .. MeasuredCoverage.Entries.Where(entry => entry.Mechanism == mechanism)
                    .OrderByDescending(entry => entry.Date, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Build, StringComparer.Ordinal),
            ],
        }),
    ];

    /// <summary>
    /// The entries whose name, mechanism, explanation, sources, fixtures, builds or tiers - by name or in words - hold
    /// <paramref name="text"/>, ignoring case; every entry for blank text.
    /// </summary>
    public static IReadOnlyList<MechanismCatalogEntry> Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Entries;
        }

        string sought = text.Trim();
        bool Holds(string value) => value.Contains(sought, StringComparison.OrdinalIgnoreCase);
        return
        [
            .. Entries.Where(entry => Holds(entry.Name) || Holds(entry.Mechanism.ToString()) || Holds(entry.Explanation)
                || entry.SourceIds.Any(Holds) || entry.UnrequestedSourceIds.Any(Holds)
                || entry.Measured.Any(measured => Holds(measured.Fixture) || Holds(measured.Build) || Holds(measured.Tier.ToString())
                    || Holds(CapabilityText.Tier(measured.Tier)))),
        ];
    }

    // Where each mechanism stands and why, citing the decision that set it; a tier is the measured coverage's to state.
    private static string Explain(Mechanism mechanism) => mechanism switch
    {
        Mechanism.ProcessLifecycle =>
            "Each process's start and exit, and the processes running as a capture starts and stops, from "
            + "Microsoft-Windows-Kernel-Process, which every capture collects: the process instances every other record binds to.",
        Mechanism.ThreadLifecycle =>
            "Microsoft-Windows-Kernel-Process could record a thread's start and end, but no profile requests them, so no capture "
            + "holds a thread's lifetime.",
        Mechanism.Tcp =>
            "Microsoft-Windows-Kernel-Network records each connection's ends, its opens and its sends and receives with their "
            + "sizes, by the process each record names: traffic, as the tier its fixtures measured says.",
        Mechanism.Udp =>
            "Microsoft-Windows-Kernel-Network records each datagram's ends and size, with no connection lifetime; a receive names "
            + "the datagram's sender first, as measured (ADR-019).",
        Mechanism.UnixDomainSocket =>
            "Winsock AFD records each AF_UNIX socket call with its length, but holds no process filter, no socket's name and no "
            + "peer (ADR-072): an opt-in profile records a socket's calls and bytes, never its other end (ADR-075).",
        Mechanism.NamedPipe =>
            "On the builds measured, Microsoft-Windows-Kernel-File recorded no named pipe's I/O (ADR-003), and no inference - a "
            + "parent and child, an equal name, a handle - stands in for it, so a named pipe is a stated gap (P6, P7).",
        Mechanism.AnonymousPipe =>
            "Microsoft-Windows-Kernel-File could serve it, but no profile requests it, and a parent and its child alone never "
            + "prove a pipe they share.",
        Mechanism.Rpc =>
            "Microsoft-Windows-RPC records each call's interface, procedure, protocol and endpoint at the client and the server, "
            + "and its status at its stop, each record bound to the process that raised it (ADR-030).",
        Mechanism.Alpc =>
            "The kernel's ALPC events record a message's send and receive by its message ID, with no length and no port's name: "
            + "they link an RPC client's call to the call that served it, never a byte count.",
        Mechanism.SharedSection =>
            "A section two processes share is recorded by its handles and by its views, which no record joins (ADR-089), and is "
            + "topology at most: who held it and the extent a view mapped, never bytes moved through it (ADR-092).",
        Mechanism.ComActivation =>
            "Microsoft-Windows-COM-Perf declares activation starts and stops that carry no field: an activation names nothing "
            + "(ADR-077), so no source admits one, and an activation is never counted as a call.",
        Mechanism.Synchronization =>
            "The kernel's handle records join a named object's handles by its address, and no event records a signal or a wait "
            + "(ADR-082): a named event, mutex or semaphore and the processes holding it, topology at most (ADR-084).",
        Mechanism.WindowMessage =>
            "Win32k's message pump records a window message between processes at both ends, but not which message (ADR-086): "
            + "each message by its rank, experimental evidence at most (ADR-088).",
        Mechanism.Clipboard =>
            "Win32k declares a clipboard write by its process and a read naming the clipboard's owner, but no fixture has "
            + "measured them, so no source admits them.",
        Mechanism.Mailslot =>
            "No driverless source records one: neither the kernel's classic file I/O class nor Microsoft-Windows-Kernel-File "
            + "recorded a mailslot while both recorded the same processes' own files (ADR-085).",
        Mechanism.Dde =>
            "No event names DDE. Its conversations are window messages, whose records do not say which message they are "
            + "(ADR-086), so a DDE conversation is told from no other.",
        Mechanism.RemoteFileOrSmb =>
            "No adapter: a remote file or pipe is seen as the TCP connection beneath it, and no record names its SMB requests.",
        Mechanism.Quic =>
            "No adapter: a QUIC connection is seen as its UDP datagrams, and its streams would need evidence of their own.",
        Mechanism.ApplicationSdk =>
            "Records an application writes about itself through InterCat's SDK - its channels, messages and operations - are "
            + "cooperative instrumentation, outside driverless capture: no Windows source records them, and the sdk profile "
            + "admits what an instrumented application raises, each record owned by the process it names and its own word "
            + "(ADR-103).",
        Mechanism.Instrumented =>
            "Records from instrumentation added to a program are outside driverless capture: no source in the catalog records "
            + "them, and no profile hooks or instruments a process.",
        Mechanism.Http =>
            "WinINet's capture provider records an HTTP exchange exactly for the processes a capture names (ADR-037), keeping "
            + "content only from the client's own processes (ADR-049).",
        Mechanism.ComCall =>
            "Microsoft-Windows-COM-Perf records each out-of-process COM call at both ends (ADR-077): a COM call is the RPC call "
            + "carrying it, its ends linked by the client's activity (ADR-079), and experimental evidence at most (ADR-080).",
        _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "The catalog explains every mechanism EN-Mechanism names."),
    };
}
