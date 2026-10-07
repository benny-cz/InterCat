using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// An L1 rung: process instances grouped by executable, service container or session (<c>EN-Grouping</c>).
/// </summary>
/// <param name="Key">The group's identity, unique within a workspace.</param>
/// <param name="Name">A short label for rows and graph nodes.</param>
/// <param name="Kind">What the members have in common.</param>
/// <param name="Detail">The full identity behind a shortened name, such as an executable's image path; null when the name
/// says it all.</param>
public sealed record ProcessGroup(
    string Key,
    string Name,
    LaneGrouping Kind,
    string? Detail = null)
{
    /// <summary>
    /// The key of the group that holds every process no record names the executable of: together, rather than under a
    /// guessed name.
    /// </summary>
    public const string UnwitnessedExecutableKey = "executable:unknown";

    /// <summary>
    /// How long the group's completed RPC calls took over the ranked scope, its members' calls together, when an RPC call
    /// ranking read them (<see cref="SessionCallRanking"/>); null until one has.
    /// </summary>
    public CallTimes? CallTimes { get; init; }

    /// <summary>The group's distinct peers over the ranked scope, its members' together, when a peer ranking read them.</summary>
    public PeerCount? Peers { get; init; }
}

public sealed record ProcessNode(
    ProcessInstanceId Id,
    int ProcessId,
    string Name,
    string Role,
    string GroupKey,
    double X,
    double Y,
    CoverageState Coverage)
{
    /// <summary>
    /// The process as a caption names it: its name and its PID, each once. A process whose executable was not witnessed
    /// is already named by its PID, so it reads "PID 812" rather than "PID 812 · PID 812". A reused PID's holders are
    /// told apart by <see cref="PidLabel"/>: "client.exe · PID 100 #2".
    /// </summary>
    public string NameWithPid => string.Equals(Name, PidName(ProcessId), StringComparison.Ordinal)
        ? PidLabel
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Name} · {PidLabel}");

    /// <summary>
    /// Its PID as a person reads it: "PID 100", or, when more than one process held the PID in the capture, which of them
    /// this was - "PID 100 #2" for the second, as `icat processes` names it - since the two otherwise read alike.
    /// </summary>
    public string PidLabel => PidLabelOf(ProcessId, PidHolder, PidHolders);

    /// <summary>
    /// The part this process played in collecting the capture - InterCat's broker, the process that asked it to record, or
    /// `icat record` - when the capture's collectors name it by PID and creation time (`collector-binding-v1`); null for
    /// every other process, and for every process of a capture that names none.
    /// </summary>
    public CollectorRole? Collector { get; init; }

    /// <summary>
    /// The line beneath its name in a ranked row and the inspector: its PID, how it was seen to start and, for one of
    /// InterCat's own, which it is - "PID 4120 · running at start · InterCat's broker".
    /// </summary>
    public string Caption => Collector is { } role
        ? $"{PidLabel} · {Role} · {CollectorText.Label(role)}"
        : $"{PidLabel} · {Role}";

    /// <summary>The name an instance gets when no executable was witnessed for it.</summary>
    public static string PidName(int processId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PID {processId}");

    /// <summary>
    /// A PID as <see cref="PidLabel"/> reads it for the <paramref name="holder"/>th of <paramref name="holders"/>
    /// processes that held it: numbered only when more than one did.
    /// </summary>
    public static string PidLabelOf(int processId, int holder, int holders) => holders > 1
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PID {processId} #{holder}")
        : PidName(processId);

    /// <summary>
    /// What the process was observed doing: its own records by mechanism, most first, as the evidence policy admits them
    /// (`process-activity-v1`). Empty when it made none, and for a node not projected from a session.
    /// </summary>
    public IReadOnlyList<MechanismCount> Activity { get; init; } = [];

    /// <summary>
    /// Which of the processes that held its PID in the capture this one was, from 1. A record naming the PID while a
    /// later holder ran could be a late record of an earlier one, so it binds to the later holder only as a candidate
    /// (`process-binding-v4`, identity-v1). 1 for a node not projected from a session.
    /// </summary>
    public int PidHolder { get; init; } = 1;

    /// <summary>How many processes held its PID in the capture: two or more means the PID was reused.</summary>
    public int PidHolders { get; init; } = 1;

    /// <summary>
    /// The records bound to it over the whole session that the evidence policy leaves out of <see cref="Activity"/>: a
    /// later holder's, which bind only as candidates. Zero when the policy admits every record bound to it.
    /// </summary>
    public long WithheldRecords { get; init; }

    /// <summary>
    /// The records bound to it over the whole session only as candidates, whether or not the evidence policy counts them:
    /// a later holder's every record but its lifecycle records. <see cref="WithheldRecords"/> is these under a policy
    /// that admits no candidate, and zero under one that does.
    /// </summary>
    public long CandidateRecords { get; init; }

    /// <summary>
    /// The PID of the process that created this one, as its creation or rundown record named it (entities-v1 §3); null
    /// when no lifecycle record of it named one.
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>
    /// The parent instance, when the capture holds it: linked by the parent's start key, or by its PID and this
    /// process's creation reading. A parent named but not in the capture is <see cref="ParentProcessId"/> alone, never
    /// a guessed instance.
    /// </summary>
    public ProcessInstanceId? Parent { get; init; }

    /// <summary>How strongly <see cref="Parent"/> is established: Direct by start key, Correlated by PID and time.</summary>
    public RelationStrength? ParentBinding { get; init; }

    /// <summary>
    /// The terminal session the process ran in, as its lifecycle records name it; null when none named one. A grouping by
    /// session reads it (<see cref="WorkspaceGrouping"/>), never guessing one.
    /// </summary>
    public uint? TerminalSession { get; init; }

    /// <summary>The records <see cref="Activity"/> counts.</summary>
    public long Records => Activity.Sum(entry => entry.Records);

    /// <summary>
    /// The process's transport bytes on its own records over the ranked scope, when a byte ranking read them
    /// (<see cref="SessionByteRanking"/>); null until one has.
    /// </summary>
    public TransportBytes? Bytes { get; init; }

    /// <summary>
    /// The process's RPC calls over the ranked scope, made and served, when an RPC call ranking read them
    /// (<see cref="SessionCallRanking"/>); null until one has.
    /// </summary>
    public ProcessCalls? Calls { get; init; }

    /// <summary>How long the process's completed RPC calls took over the ranked scope, read with <see cref="Calls"/>.</summary>
    public CallTimes? CallTimes { get; init; }

    /// <summary>
    /// The process's distinct peers over the ranked scope, when a peer ranking read them (<see cref="SessionPeerRanking"/>);
    /// null until one has.
    /// </summary>
    public PeerCount? Peers { get; init; }
}

/// <summary>How many records of one mechanism a process, or a group of them, was observed making.</summary>
public sealed record MechanismCount(Mechanism Mechanism, long Records);

/// <summary>
/// A relationship between two processes, as strong as its weakest evidence, carrying the rule that derived it and what it
/// rests on (R4), so the edge it draws can always be explained.
/// </summary>
public sealed record CommunicationEdge(
    string Key,
    ProcessInstanceId SourceId,
    ProcessInstanceId TargetId,
    Mechanism Mechanism,
    long ObservationCount,
    long? KnownBytes,
    RelationStrength Strength)
{
    /// <summary>The rule, by identity and version, that derived the relationship.</summary>
    public required RelationRule Rule { get; init; }

    /// <summary>
    /// What the relationship rests on, by identity, in ordinal order, each key opening its records at both ends. A TCP
    /// relationship names each connection it holds by the key of its channel, which names the connection's first record. An
    /// RPC relationship, whose links the overview keeps only as counts, names itself: its key opens the calls its links join.
    /// </summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    /// <summary>
    /// Two relationships are one when each of their fields is, their evidence compared key by key: one generation read
    /// twice yields equal relationships (R7), though each read builds its own list.
    /// </summary>
    public bool Equals(CommunicationEdge? other) =>
        other is not null
        && Key == other.Key
        && SourceId == other.SourceId
        && TargetId == other.TargetId
        && Mechanism == other.Mechanism
        && ObservationCount == other.ObservationCount
        && KnownBytes == other.KnownBytes
        && Strength == other.Strength
        && Rule == other.Rule
        && Evidence.SequenceEqual(other.Evidence, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key, SourceId, TargetId, Mechanism, ObservationCount, KnownBytes, Strength, Rule);
}

/// <summary>
/// An L3 rung: one channel or endpoint of a relationship. A relationship can carry more than one, which
/// is why descending from an edge is a rung of its own rather than a detail of the edge.
/// </summary>
public sealed record Channel(
    string Key,
    string EdgeKey,
    string Name,
    Mechanism Mechanism,
    Direction Direction,
    long ObservationCount,
    long? KnownBytes,
    CoverageState Coverage)
{
    /// <summary>
    /// The transport bytes each end's own records measured over the ranked scope, by the process holding that end, when a
    /// byte ranking read them (<see cref="SessionByteRanking"/>); null until one has.
    /// </summary>
    public IReadOnlyDictionary<ProcessInstanceId, TransportBytes>? EndBytes { get; init; }

    /// <summary>
    /// The process holding the first endpoint of <see cref="Name"/>, when known. A paired channel names its ends in its
    /// relation's order, which is neither its relationship's source nor its target, so a view that puts one process's own
    /// end first needs to know whose each is.
    /// </summary>
    public ProcessInstanceId? FirstHolder { get; init; }

    /// <summary>The process holding the second endpoint of <see cref="Name"/>, when known; the first's for a process connected to itself.</summary>
    public ProcessInstanceId? SecondHolder { get; init; }

    /// <summary>
    /// The rule that paired its two ends (R4): `transport-endpoint-relation` for a TCP connection a session's relations
    /// paired; null for a channel no rule derived, as the tour's.
    /// </summary>
    public RelationRule? Rule { get; init; }

    /// <summary>
    /// How strongly the pairing holds: correlated, or a candidate when either end's records bind to their process only
    /// as candidates, as a reused PID's later holder's do (`contracts/relations-v1.md` §5).
    /// </summary>
    public RelationStrength Strength { get; init; } = RelationStrength.Correlated;

    /// <summary>
    /// Whether the capture saw the connection open at both ends - a connect at one, an accept at the other - and close
    /// at both. A connection the capture did not see open was open before the capture began, and one it did not see close
    /// was still open when the capture ended. Both true for a channel no rule derived.
    /// </summary>
    public bool OpenWitnessed { get; init; } = true;

    /// <inheritdoc cref="OpenWitnessed"/>
    public bool CloseWitnessed { get; init; } = true;
}

/// <summary>
/// An L4 rung: one logical operation on a channel. Requested and completed sizes stay separate, and an
/// operation whose completion was never seen keeps its state rather than borrowing a size (P3, P8).
/// </summary>
public sealed record ChannelOperation(
    string Key,
    string ChannelKey,
    ObservationKind Kind,
    Direction Direction,
    TimeRange Interval,
    long? RequestedBytes,
    long? CompletedBytes,
    OperationState State);

/// <summary>
/// An L5 rung: one source observation. It names the provider and descriptor it came from, because the
/// question the product exists to answer is "show me the actual records" (section 3.2).
/// </summary>
public sealed record EvidenceMark(
    string Key,
    string OperationKey,
    long NativeTicks,
    string Provider,
    int EventId,
    string Summary);

public sealed record TimelineBucket(
    TimeRange Interval,
    int ObservationCount,
    long? KnownBytes,
    Mechanism DominantMechanism,
    CoverageState Coverage);

/// <summary>One mechanism's observations on the same column boundaries as the whole timeline.</summary>
public sealed record MechanismTimelineLane(Mechanism Mechanism, IReadOnlyList<TimelineBucket> Buckets);

/// <summary>
/// Workspace viewport ticks are 100-nanosecond presentation ticks. A real-session projection converts source-native
/// and session-relative readings into this scale before they reach the graph or timeline; no UI assumes the source
/// clock itself has this frequency.
/// </summary>
public static class WorkspaceTime
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>
    /// A session-relative range in the unit its span needs, so a brushed millisecond never reads "0.00 s – 0.00 s":
    /// seconds for spans of 10 ms and more (to the millisecond below 10 s), milliseconds below 10 ms, and microseconds
    /// below 10 µs (§6.2 escalates axis units the same way).
    /// </summary>
    public static string FormatRange(TimeRange range, IFormatProvider? culture = null)
    {
        (decimal divisor, string unit, string format) = Unit(range.EndTicks - range.StartTicks);
        IFormatProvider provider = culture ?? System.Globalization.CultureInfo.CurrentCulture;
        return string.Create(provider,
            $"{(range.StartTicks / divisor).ToString(format, provider)} – {(range.EndTicks / divisor).ToString(format, provider)} {unit}");
    }

    /// <summary>
    /// The same range with the half-open boundary made visible. Use this where inclusion at an interval edge matters,
    /// such as a hover or inspector; an en-dash range is easier to scan on an axis but does not say whether its end is
    /// included (§6.2, R13).
    /// </summary>
    public static string FormatHalfOpenRange(TimeRange range, IFormatProvider? culture = null)
    {
        (decimal divisor, string unit, string format) = Unit(range.EndTicks - range.StartTicks);
        IFormatProvider provider = culture ?? System.Globalization.CultureInfo.CurrentCulture;
        string separator = BoundSeparator(provider);
        return string.Create(provider,
            $"[{(range.StartTicks / divisor).ToString(format, provider)}{separator} {(range.EndTicks / divisor).ToString(format, provider)}) {unit}");
    }

    /// <summary>
    /// What parts a half-open range's bounds: the culture's list separator, so a comma-decimal culture writes "[0,5; 2,0)"
    /// and the separator never reads as a decimal comma. It is never the decimal separator itself.
    /// </summary>
    public static string BoundSeparator(IFormatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        System.Globalization.NumberFormatInfo number = System.Globalization.NumberFormatInfo.GetInstance(provider);
        string separator = provider is System.Globalization.CultureInfo info ? info.TextInfo.ListSeparator : ",";
        return string.IsNullOrWhiteSpace(separator) || separator == number.NumberDecimalSeparator
            ? number.NumberDecimalSeparator == "," ? ";" : ","
            : separator;
    }

    /// <summary>What a session's instants are counted in, where the wall clock its capture began at is not known.</summary>
    public const string SessionTimeWords = "session time";

    /// <summary>
    /// The time base a session's instants are counted in, as the timeline states it under its axis (§6.2): session time,
    /// and, where the capture recorded its wall clock, the moment it counts from in <paramref name="zone"/> with its offset
    /// from UTC, since a wall-clock time is never stated without one - "session time since 10/06/2026 14:03:12 UTC+02:00".
    /// </summary>
    public static string TimeBase(DateTimeOffset? began, TimeZoneInfo zone, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (began is not { } start)
        {
            return SessionTimeWords;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(start, zone);
        IFormatProvider provider = culture ?? System.Globalization.CultureInfo.CurrentCulture;
        return string.Create(provider,
            $"{SessionTimeWords} since {local.DateTime:d} {local.DateTime:T} {SessionClock.OffsetText(local.Offset)}");
    }

    /// <summary>One instant, in the unit a visible span of <paramref name="span"/> ticks needs, as an axis labels its edges.</summary>
    public static string FormatInstant(long ticks, long span, IFormatProvider? culture = null)
    {
        (decimal divisor, string unit, string format) = Unit(span);
        IFormatProvider provider = culture ?? System.Globalization.CultureInfo.CurrentCulture;
        return string.Create(provider, $"{(ticks / divisor).ToString(format, provider)} {unit}");
    }

    /// <summary>A duration of this many workspace ticks, in the same span-driven unit as <see cref="FormatRange"/>.</summary>
    public static string FormatDuration(long ticks, IFormatProvider? culture = null)
    {
        (decimal divisor, string unit, string format) = Unit(ticks);
        IFormatProvider provider = culture ?? System.Globalization.CultureInfo.CurrentCulture;
        return string.Create(provider, $"{(ticks / divisor).ToString(format, provider)} {unit}");
    }

    private static (decimal Divisor, string Unit, string Format) Unit(long span) => span switch
    {
        >= 10 * TicksPerSecond => (TicksPerSecond, "s", "N1"),
        >= TicksPerSecond / 100 => (TicksPerSecond, "s", "N3"),
        >= TicksPerSecond / 100_000 => (TicksPerSecond / 1_000m, "ms", "N3"),
        _ => (TicksPerSecond / 1_000_000m, "µs", "N1"),
    };
}

/// <summary>
/// Everything one workspace shows, at every rung of the section 3.2 ladder. The levels are separate
/// collections rather than a nested tree so a projection can be written as a pure query at any rung.
/// </summary>
public sealed record WorkspaceSnapshot(
    string Title,
    TimeRange Extent,
    IReadOnlyList<ProcessGroup> Groups,
    IReadOnlyList<ProcessNode> Processes,
    IReadOnlyList<CommunicationEdge> Edges,
    IReadOnlyList<Channel> Channels,
    IReadOnlyList<ChannelOperation> Operations,
    IReadOnlyList<EvidenceMark> Evidence,
    IReadOnlyList<TimelineBucket> Timeline,
    string? ChannelProjectionProblem = null,
    SessionMinimap? Minimap = null,
    SessionRedaction? Redaction = null)
{
    /// <summary>Whole-session L0 mechanism lanes from the same leased overview, not guessed from dominant hues.</summary>
    public IReadOnlyList<MechanismTimelineLane> MechanismLanes { get; init; } = [];

    /// <summary>The clock the snapshot's readings are on; null for a snapshot that was not projected from a session.</summary>
    public SourceClockDescriptor? Clock { get; init; }

    /// <summary>
    /// Whether the generation publishes a coverage ledger. Without one, as while a capture records, every interval's
    /// coverage is unknown because nothing has judged it yet, which is a different statement from a gap.
    /// </summary>
    public bool CoverageLedgerPublished { get; init; } = true;

    /// <summary>
    /// Each mechanism's coverage over what the snapshot counts - the whole session, or the interval it was counted within
    /// (<see cref="OverviewWorkspace.WithinInterval"/>) - and the fact that decided it (`coverage-v2` §4); empty for a
    /// snapshot that was not projected from a session.
    /// </summary>
    public IReadOnlyList<MechanismCoverage> MechanismCoverage { get; init; } = [];

    /// <summary>
    /// Observed rows in scope that no process instance holds, which the ranked table's groups and processes therefore
    /// cannot count and the timeline does; null for a snapshot not projected from a session.
    /// </summary>
    public long? RowsNoProcessHolds { get; init; }

    /// <summary>
    /// The interval the session's capture recorded, which its whole-session rates divide by (<see cref="SessionRecording"/>);
    /// null when the capture recorded no stop.
    /// </summary>
    public TimeRange? Recording { get; init; }

    /// <summary>Whether the session is InterCat's generated demo (<see cref="DemoInvestigation"/>), which every view says.</summary>
    public bool Demo { get; init; }

    /// <summary>
    /// InterCat's own processes this snapshot sets aside (§19.5's view filter, <see cref="WorkspaceCollectors"/>): absent
    /// from its groups, processes, relationships and channels, though the timeline and the evidence still count their
    /// records. None when it sets none aside.
    /// </summary>
    public IReadOnlyList<ProcessNode> SetAside { get; init; } = [];

    /// <summary>
    /// The processes the capture names as its collectors (`collector-identities-v1`), which instance each is and those no
    /// instance is (`collector-binding-v1`), so a view can say of a process holding such a PID that it is not taken for
    /// one; <see cref="CollectorMatch.NotRecorded"/> for a capture that names none, and a snapshot not projected from one.
    /// </summary>
    public CollectorMatch Collectors { get; init; } = CollectorMatch.NotRecorded;

    /// <summary>
    /// When the session's capture began by the wall clock it recorded: session time 0, which the timeline's axis counts
    /// from, in UTC; null when the capture recorded no wall clock, as an import does.
    /// </summary>
    public DateTimeOffset? Began { get; init; }

    /// <summary>
    /// The wall clock the session's capture's machine read, placed against session time, which a view may read its
    /// instants in (§6.2's time base); null when the capture recorded none.
    /// </summary>
    public SessionWallClock? WallClock { get; init; }
}
