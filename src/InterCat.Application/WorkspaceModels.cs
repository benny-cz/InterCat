using InterCat.Domain;

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
    string? Detail = null);

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
    /// is already named by its PID, so it reads "PID 812" rather than "PID 812 · PID 812".
    /// </summary>
    public string NameWithPid => string.Equals(Name, PidName(ProcessId), StringComparison.Ordinal)
        ? Name
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Name} · PID {ProcessId}");

    /// <summary>The name an instance gets when no executable was witnessed for it.</summary>
    public static string PidName(int processId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PID {processId}");
}

public sealed record CommunicationEdge(
    string Key,
    ProcessInstanceId SourceId,
    ProcessInstanceId TargetId,
    Mechanism Mechanism,
    long ObservationCount,
    long? KnownBytes,
    RelationStrength Strength);

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
    CoverageState Coverage);

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

        // The bounds are parted by the culture's list separator: a comma-decimal culture writes "[0,5; 2,0)", so the
        // separator never reads as a decimal comma. It is never the decimal separator itself.
        System.Globalization.NumberFormatInfo number = System.Globalization.NumberFormatInfo.GetInstance(provider);
        string separator = provider is System.Globalization.CultureInfo info ? info.TextInfo.ListSeparator : ",";
        if (string.IsNullOrWhiteSpace(separator) || separator == number.NumberDecimalSeparator)
        {
            separator = number.NumberDecimalSeparator == "," ? ";" : ",";
        }

        return string.Create(provider,
            $"[{(range.StartTicks / divisor).ToString(format, provider)}{separator} {(range.EndTicks / divisor).ToString(format, provider)}) {unit}");
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
}
