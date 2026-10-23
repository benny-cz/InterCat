using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What a comparison of two intervals compares each row by (`contracts/comparison-v1.md` §3).</summary>
public enum ComparisonMetric
{
    /// <summary>The records of both ends of a relationship, or of a process's connections to another host.</summary>
    Records = 1,

    /// <summary>
    /// The transport-observed bytes sent across, each transfer counted once at its sender (`metrics-v1` §4): what both ends
    /// of a relationship sent, as the relationship table states it, or what a process sent to another host, whose own
    /// sends no record of this machine holds.
    /// </summary>
    BytesSent = 2,
}

/// <summary>What a comparison's rows are of (`contracts/comparison-v1.md` §2a): processes, or the executables they ran.</summary>
public enum ComparisonGrouping
{
    /// <summary>Each process's relationships and connections to other hosts, as the overview draws them: a row per process.</summary>
    Process = 1,

    /// <summary>
    /// Each executable's: its processes' relationships and connections to other hosts together, grouped by the path their
    /// image was witnessed at and never by a name alone (plan M12's entity grouping) - a grouping, never an identity (P6,
    /// ADR-059), so an executable whose process restarted between the intervals keeps one row, neither process taken for
    /// the other.
    /// </summary>
    Executable = 2,
}

/// <summary>
/// How a row's measure changed from the baseline interval to the compared one, each a rate over its whole interval
/// (`comparison-v1` §4). A change is stated only where both values are of the same measurement.
/// </summary>
public enum ComparisonChange
{
    /// <summary>Its rate over the compared interval is its rate over the baseline, exactly.</summary>
    Unchanged = 1,

    /// <summary>Its rate over the compared interval is higher than over the baseline.</summary>
    Increased = 2,

    /// <summary>Its rate over the compared interval is lower than over the baseline.</summary>
    Decreased = 3,

    /// <summary>Nothing in the baseline, which covered its mechanism; something in the compared interval.</summary>
    Appeared = 4,

    /// <summary>Something in the baseline; nothing in the compared interval, which covered its mechanism.</summary>
    Disappeared = 5,

    /// <summary>Its two values are not of the same measurement: they are stated side by side, with why (`comparison-v1` §5).</summary>
    NotComparable = 6,
}

/// <summary>
/// A row's measure over one interval of a comparison: its records there, the value compared - its records, or the bytes
/// sent across - and the coverage it is judged by, its mechanism's over the interval, with the fact that decided it.
/// </summary>
/// <param name="Records">The row's records in the interval.</param>
/// <param name="Value">
/// What is compared: the records, or the bytes the sends that measured a size carried; zero where nothing was sent, and null
/// where no send measured one though some recorded none.
/// </param>
/// <param name="Measured">The records, or the sends that measured a size.</param>
/// <param name="Unmeasured">The sends that recorded no size, which make <paramref name="Value"/> a lower bound; none for records.</param>
/// <param name="Coverage">Its mechanism's coverage over the interval: RPC calls' is the worse of RPC's and ALPC's.</param>
public sealed record ComparedMeasure(long Records, long? Value, long Measured, long Unmeasured, MechanismCoverage Coverage)
{
    /// <summary>Whether its value is exact: known, and no record behind it left out what it counts.</summary>
    public bool Exact => Value is not null && Unmeasured == 0;

    /// <summary>Whether it holds nothing at all: no record, or under a byte comparison no send, measured or not.</summary>
    public bool None => Value == 0 && Unmeasured == 0;

    /// <summary>Its value per second of <paramref name="interval"/>, over the whole interval; null where its value is unknown.</summary>
    public double? PerSecond(TimeRange interval) =>
        Value is { } value ? value * (double)WorkspaceTime.TicksPerSecond / interval.SpanTicks : null;
}

/// <summary>
/// One process's connections over one transport to one other host's address, as the census counts them (§6.3): the
/// connections the graph draws to its Other hosts node, by address. Who is at the address is not observed (P7).
/// </summary>
public sealed record OtherHostReach(HostLink Link, HostAddress Address)
{
    /// <summary>Its key: its host link's, then the address.</summary>
    public string Key => Link.Key + "@" + Address.Address;
}

/// <summary>
/// Two executables' relationship over one mechanism (`comparison-v1` §2a): every relationship the overview draws between a
/// process of one and a process of the other, together - the two may be one executable, whose processes relate to each
/// other - with who made its connections or calls as the overview saw them open, and the ports they arrived at.
/// </summary>
/// <param name="Key">Its key: its mechanism and the two executables' group keys, in ordinal order.</param>
/// <param name="Source">The group key of the executable whose key orders first.</param>
/// <param name="Target">The other executable's group key; the source's own where the processes are of one executable.</param>
/// <param name="Mechanism">The mechanism its relationships are of.</param>
public sealed record ExecutableRelationship(string Key, string Source, string Target, Mechanism Mechanism)
{
    /// <summary>How many connections or calls the source's processes made to the target's, as the overview saw them open.</summary>
    public int SourceConnected { get; init; }

    /// <summary>How many the target's processes made to the source's, counted alike.</summary>
    public int TargetConnected { get; init; }

    /// <summary>The ports its connections arrived at, at the end that accepted each, each once, the lowest first.</summary>
    public IReadOnlyList<ushort> AcceptedPorts { get; init; } = [];
}

/// <summary>
/// One executable's processes' connections over one transport to one other host's address, together (`comparison-v1`
/// §2a), as the census counts each process's. Who is at the address is not observed (P7).
/// </summary>
/// <param name="Key">Its key: its mechanism, the executable's group key and the address.</param>
/// <param name="Executable">The executable's group key.</param>
/// <param name="Mechanism">The transport the connections ran over.</param>
/// <param name="Address">The other host's address, with no port.</param>
public sealed record ExecutableReach(string Key, string Executable, Mechanism Mechanism, string Address)
{
    /// <summary>How many of the connections its processes opened by a connect, as the capture saw them open.</summary>
    public int Connected { get; init; }

    /// <summary>How many its processes accepted from the address.</summary>
    public int Accepted { get; init; }

    /// <summary>The ports at the address its processes' connections ran to, each once, the lowest first.</summary>
    public IReadOnlyList<ushort> Ports { get; init; } = [];

    /// <summary>Its processes' own ports the accepted connections arrived at, each once, the lowest first.</summary>
    public IReadOnlyList<ushort> AcceptedPorts { get; init; } = [];
}

/// <summary>
/// What a comparison by executable could not group (`comparison-v1` §2a): the relationships and connections to other
/// hosts with a record in either interval that a process of no witnessed executable is at an end of, and their records
/// in each. No path names its executable, so it is neither grouped with another process nor taken for another's.
/// </summary>
/// <param name="Rows">How many such relationships and connections to other hosts there were.</param>
/// <param name="BaselineRecords">Their records in the baseline.</param>
/// <param name="ComparedRecords">Their records in the compared interval.</param>
public sealed record UnmatchedRows(int Rows, long BaselineRecords, long ComparedRecords);

/// <summary>
/// One row of a comparison - a relationship between two of the session's processes, or one process's connections to another
/// host, or under a comparison by executable the same of executables - with its measure over each interval and how it
/// changed, or why its two values cannot be compared (<see cref="Reason"/>).
/// </summary>
public sealed record ComparedRelationship(
    ComparedMeasure Baseline,
    ComparedMeasure Compared,
    ComparisonChange Change,
    string? Reason)
{
    /// <summary>The relationship between two processes the row compares; null for one to another host.</summary>
    public CommunicationEdge? Relationship { get; init; }

    /// <summary>The process's connections to another host the row compares; null for a relationship between two processes.</summary>
    public OtherHostReach? Host { get; init; }

    /// <summary>The two executables' relationship the row compares, in a comparison by executable; null otherwise.</summary>
    public ExecutableRelationship? Executables { get; init; }

    /// <summary>An executable's processes' connections to another host the row compares, in a comparison by executable.</summary>
    public ExecutableReach? ExecutableHost { get; init; }

    /// <summary>Its key: the relationship's, or the connections' to the other host.</summary>
    public string Key => Relationship?.Key ?? Host?.Key ?? Executables?.Key ?? ExecutableHost!.Key;

    /// <summary>The mechanism its records are of.</summary>
    public Mechanism Mechanism =>
        Relationship?.Mechanism ?? Host?.Link.Mechanism ?? Executables?.Mechanism ?? ExecutableHost!.Mechanism;

    /// <summary>
    /// Whether the change rests on a lower bound: one interval's value left sends without a size out, and the bound alone
    /// already exceeds the other interval's exact rate, so the change is at least as large as stated.
    /// </summary>
    public bool FromLowerBound { get; init; }

    /// <summary>
    /// The difference of the two rates scaled by both intervals' lengths, compared value times the baseline's length less
    /// baseline value times the compared length: its sign is the change's, and it ranks rows exactly, every row of one
    /// comparison sharing the scale. Zero where the values cannot be compared.
    /// </summary>
    internal Int128 ScaledDifference { get; init; }

    /// <summary>
    /// The larger of its two rates of records on the same scale, which ranks rows whose values cannot be compared: the
    /// busier first, as every other list ranks relationships, whatever their values are.
    /// </summary>
    internal Int128 ScaledBusier { get; init; }
}

/// <summary>
/// Two intervals of one generation compared row by row (`comparison-v1`): each relationship the overview draws, and each
/// process's connections over one transport to one other host, that had a record in either interval, its records or the
/// bytes sent across over each interval as a rate over the whole interval, the most changed first. A change is stated only
/// where both intervals covered its mechanism, through the same sources; elsewhere both values are stated with the reason
/// they cannot be compared, so a capture that did not collect a mechanism in one interval never reads as activity that
/// disappeared (plan M12).
/// </summary>
public sealed record IntervalComparison(
    Guid SessionId,
    long Generation,
    ComparisonMetric Metric,
    EvidencePolicy Policy,
    TimeRange Baseline,
    TimeRange Compared,
    IReadOnlyList<MechanismCoverage> BaselineCoverage,
    IReadOnlyList<MechanismCoverage> ComparedCoverage,
    IReadOnlyList<ComparedRelationship> Relationships)
{
    /// <summary>The process instances the overview identified, by identity, which name each row's processes.</summary>
    public required IReadOnlyDictionary<ProcessInstanceId, ProcessNode> Processes { get; init; }

    /// <summary>How many of the overview's relationships and connections to other hosts had no record in either interval, and so no row.</summary>
    public int Quiet { get; init; }

    /// <summary>What its rows are of: processes, or the executables they ran.</summary>
    public ComparisonGrouping Grouping { get; init; } = ComparisonGrouping.Process;

    /// <summary>The executables the overview groups processes by, by key, which name a comparison by executable's rows.</summary>
    public IReadOnlyDictionary<string, ProcessGroup> Groups { get; init; } = new Dictionary<string, ProcessGroup>();

    /// <summary>In a comparison by executable, what no executable groups; nothing in a comparison by process.</summary>
    public UnmatchedRows Unmatched { get; init; } = new(0, 0, 0);

    /// <summary>
    /// The session the baseline was read from where it is another capture than the compared interval's, which
    /// <see cref="SessionId"/> names (§2b); null where both intervals are of one session.
    /// </summary>
    public Guid? BaselineSessionId { get; init; }

    /// <summary>The generation the baseline's session was read at, where it is another capture; null otherwise.</summary>
    public long? BaselineGeneration { get; init; }

    /// <summary>
    /// Of two captures, whether their sources name one host identity - evidence of one host, and never proof of it (plan
    /// §8.3) - or two; null where either names none, and where both intervals are of one session.
    /// </summary>
    public bool? SameHost { get; init; }
}

/// <summary>
/// One column of a difference timeline (`contracts/comparison-v1.md` §8d): the k-th equal share of the baseline beside the
/// k-th of the compared interval, with what the capture covered over each.
/// </summary>
public sealed record ComparisonColumn(
    TimeRange Baseline,
    TimeRange Compared,
    IReadOnlyList<MechanismCoverage> BaselineCoverage,
    IReadOnlyList<MechanismCoverage> ComparedCoverage);

/// <summary>
/// A comparison and its difference timeline (`contracts/comparison-v1.md` §8d, plan M12): each interval cut into as many
/// equal columns, and the comparison's rows compared column by column - the k-th of the baseline's with the k-th of the
/// compared interval's, by the same metric, each pair judged as two intervals are, by its own coverage and by whether the
/// capture collected the row's mechanism alike in both - all read at one generation, so a row's columns add up to its
/// values over the whole intervals.
/// </summary>
/// <param name="Whole">The comparison of the two whole intervals, its rows ranked as it ranks them.</param>
/// <param name="Columns">The columns, the first of each interval first.</param>
/// <param name="Cells">The ranked rows' columns, by the row's key, a cell a column, in order.</param>
public sealed record ComparisonTimeline(
    IntervalComparison Whole,
    IReadOnlyList<ComparisonColumn> Columns,
    IReadOnlyDictionary<string, IReadOnlyList<ComparedRelationship>> Cells)
{
    /// <summary>A row's columns, in order, each its measure over the two columns and how it changed, or why it cannot be said.</summary>
    public IReadOnlyList<ComparedRelationship> Of(ComparedRelationship row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Cells.TryGetValue(row.Key, out IReadOnlyList<ComparedRelationship>? cells) ? cells : [];
    }
}

/// <summary>Compares two intervals of a session's generation row by row (`comparison-v1`, plan M12).</summary>
public static class SessionComparison
{
    /// <summary>The most columns a difference timeline cuts each interval into: each is read as an interval of its own.</summary>
    public const int MostTimelineColumns = 100;

    /// <summary>Why a difference timeline is not cut finer than its intervals' ticks.</summary>
    public const string ColumnsPastTicks = "Each column is a whole number of ticks, so neither interval may be shorter than its "
        + "columns.";

    /// <summary>
    /// <see cref="Compare(SessionStore, TimeRange, TimeRange, ComparisonMetric, EvidencePolicy, ComparisonGrouping, CancellationToken)"/>
    /// with its difference timeline (`comparison-v1` §8d): each interval cut into <paramref name="columns"/> equal columns,
    /// and the first <paramref name="listed"/> rows of the comparison's ranking compared column by column, all read at the
    /// comparison's one generation.
    /// </summary>
    public static ComparisonTimeline Timeline(
        SessionStore store,
        TimeRange baseline,
        TimeRange compared,
        int columns,
        ComparisonMetric metric = ComparisonMetric.Records,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        ComparisonGrouping grouping = ComparisonGrouping.Process,
        int listed = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        Columned(baseline, compared, columns, listed);
        TimeRange[] asked = [baseline, compared, .. Cut(baseline, columns), .. Cut(compared, columns)];
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (ReadSession(store, asked, metric, policy, betweenReads: null, cancellationToken) is { } read)
            {
                IntervalComparison whole = Of(read, 0, 1, metric, policy, grouping);
                return TimelineOf(whole, listed, [.. Enumerable.Range(0, columns).Select(column => new ComparisonColumn(
                    read.Asked[2 + column], read.Asked[2 + columns + column],
                    read.Intervals[2 + column].Coverage, read.Intervals[2 + columns + column].Coverage))],
                    column => Rows(read, 2 + column, 2 + columns + column, metric, grouping, everyRow: true).Rows);
            }
        }

        throw new InvalidOperationException(Unsettled);
    }

    /// <summary>
    /// <see cref="CompareCaptures"/> with its difference timeline (`comparison-v1` §8d): each capture's interval cut into
    /// <paramref name="columns"/> equal columns of its own clock, and the first <paramref name="listed"/> rows of the
    /// comparison's ranking compared column by column, each capture read at one generation of its own.
    /// </summary>
    public static ComparisonTimeline CaptureTimeline(
        SessionStore baselineStore,
        TimeRange baseline,
        SessionStore comparedStore,
        TimeRange compared,
        int columns,
        ComparisonMetric metric = ComparisonMetric.Records,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        int listed = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselineStore);
        ArgumentNullException.ThrowIfNull(comparedStore);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        Columned(baseline, compared, columns, listed);
        SessionRead first = Settled(baselineStore, [baseline, .. Cut(baseline, columns)], metric, policy, cancellationToken);
        SessionRead second = Settled(comparedStore, [compared, .. Cut(compared, columns)], metric, policy, cancellationToken);
        if (first.Overview.SessionId == second.Overview.SessionId)
        {
            throw new ArgumentException(OneSession, nameof(baselineStore));
        }

        IntervalComparison whole = OfCaptures(first, second, 0, 0, metric, policy);
        return TimelineOf(whole, listed, [.. Enumerable.Range(1, columns).Select(column => new ComparisonColumn(
            first.Asked[column], second.Asked[column], first.Intervals[column].Coverage, second.Intervals[column].Coverage))],
            column => CaptureRows(first, second, 1 + column, 1 + column, metric, everyRow: true).Rows);
    }

    /// <summary>Refuses a timeline of no column, of more than it reads, of columns shorter than a tick, or of no row.</summary>
    private static void Columned(TimeRange baseline, TimeRange compared, int columns, int listed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MostTimelineColumns);
        ArgumentOutOfRangeException.ThrowIfLessThan(listed, 1);
        if (baseline.SpanTicks < columns || compared.SpanTicks < columns)
        {
            throw new ArgumentException(ColumnsPastTicks, nameof(columns));
        }
    }

    /// <summary>An interval cut into <paramref name="columns"/> equal columns, end to end, as the timeline cuts its own.</summary>
    private static TimeRange[] Cut(TimeRange interval, int columns) =>
        [.. Enumerable.Range(0, columns).Select(column => TimelineColumns.IntervalOf(interval, columns, column))];

    /// <summary>
    /// The difference timeline of a comparison's first <paramref name="listed"/> rows: each column's cells, by the row's key,
    /// from the column's every row, quiet ones too.
    /// </summary>
    private static ComparisonTimeline TimelineOf(IntervalComparison whole, int listed, IReadOnlyList<ComparisonColumn> columns,
        Func<int, List<ComparedRelationship>> rowsOf)
    {
        Dictionary<string, ComparedRelationship[]> cells = whole.Relationships.Take(listed)
            .ToDictionary(row => row.Key, _ => new ComparedRelationship[columns.Count], StringComparer.Ordinal);
        for (int column = 0; column < columns.Count; column++)
        {
            foreach (ComparedRelationship cell in rowsOf(column))
            {
                if (cells.TryGetValue(cell.Key, out ComparedRelationship[]? row)) row[column] = cell;
            }
        }

        return new(whole, columns, cells.ToDictionary(entry => entry.Key,
            entry => (IReadOnlyList<ComparedRelationship>)Array.AsReadOnly(entry.Value), StringComparer.Ordinal));
    }

    /// <summary>How many times a comparison reads a session that publishes between its reads before it says so.</summary>
    internal const int Attempts = 3;

    /// <summary>Why a comparison of a session publishing faster than it reads gives no answer.</summary>
    internal const string Unsettled = "The session published a newer generation each time it was compared, so no two of its "
        + "reads were of one generation; compare it again once it publishes less often, or once its recording ends.";

    /// <summary>
    /// Compares <paramref name="compared"/> with <paramref name="baseline"/>, each a half-open interval of 100-nanosecond
    /// presentation ticks, under <paramref name="policy"/>, by <paramref name="metric"/>, a row per process or, by
    /// <paramref name="grouping"/>, per executable. Every count is of one generation: a live session that publishes between
    /// reads is read again, and one that keeps doing so is refused in words.
    /// </summary>
    public static IntervalComparison Compare(
        SessionStore store,
        TimeRange baseline,
        TimeRange compared,
        ComparisonMetric metric = ComparisonMetric.Records,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        ComparisonGrouping grouping = ComparisonGrouping.Process,
        CancellationToken cancellationToken = default) =>
        Compare(store, baseline, compared, metric, policy, grouping, betweenReads: null, cancellationToken);

    /// <summary>
    /// <see cref="Compare(SessionStore, TimeRange, TimeRange, ComparisonMetric, EvidencePolicy, ComparisonGrouping, CancellationToken)"/>,
    /// with <paramref name="betweenReads"/> run after each attempt's first read, which a test publishes through.
    /// </summary>
    internal static IntervalComparison Compare(
        SessionStore store,
        TimeRange baseline,
        TimeRange compared,
        ComparisonMetric metric,
        EvidencePolicy policy,
        ComparisonGrouping grouping,
        Action? betweenReads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        ArgumentOutOfRangeException.ThrowIfLessThan(baseline.SpanTicks, 1, nameof(baseline));
        ArgumentOutOfRangeException.ThrowIfLessThan(compared.SpanTicks, 1, nameof(compared));

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (Read(store, baseline, compared, metric, policy, grouping, betweenReads, cancellationToken) is { } comparison)
            {
                return comparison;
            }
        }

        throw new InvalidOperationException(Unsettled);
    }

    /// <summary>One attempt: every read the comparison needs, compared once they are all of one generation; null otherwise.</summary>
    private static IntervalComparison? Read(
        SessionStore store,
        TimeRange baseline,
        TimeRange compared,
        ComparisonMetric metric,
        EvidencePolicy policy,
        ComparisonGrouping grouping,
        Action? betweenReads,
        CancellationToken cancellationToken) =>
        ReadSession(store, [baseline, compared], metric, policy, betweenReads, cancellationToken) is { } read
            ? Of(read, 0, 1, metric, policy, grouping)
            : null;

    /// <summary>The comparison of a session read's interval <paramref name="before"/> with its interval <paramref name="after"/>.</summary>
    private static IntervalComparison Of(
        SessionRead read, int before, int after, ComparisonMetric metric, EvidencePolicy policy, ComparisonGrouping grouping)
    {
        SessionOverviewBundle overview = read.Overview;
        (List<ComparedRelationship> rows, int quiet, UnmatchedRows unmatched) = Rows(read, before, after, metric, grouping, everyRow: false);
        return new(overview.SessionId, overview.Generation, metric, policy, read.Asked[before], read.Asked[after],
            read.Intervals[before].Coverage, read.Intervals[after].Coverage, Array.AsReadOnly(Ranked(rows)))
        {
            Processes = overview.Nodes.ToDictionary(node => node.Id),
            Quiet = quiet,
            Grouping = grouping,
            Groups = overview.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal),
            Unmatched = unmatched,
        };
    }

    /// <summary>
    /// The rows of a session read's interval <paramref name="before"/> compared with its interval <paramref name="after"/>,
    /// judged by both intervals' coverage and by whether the capture collected each mechanism alike in both: each row with
    /// a record in either, or with <paramref name="everyRow"/> each the overview draws, a quiet one too; how many were
    /// quiet; and by executable, what no executable groups.
    /// </summary>
    private static (List<ComparedRelationship> Rows, int Quiet, UnmatchedRows Unmatched) Rows(
        SessionRead read, int before, int after, ComparisonMetric metric, ComparisonGrouping grouping, bool everyRow)
    {
        SessionOverviewBundle overview = read.Overview;
        IntervalRead then = read.Intervals[before], now = read.Intervals[after];
        IReadOnlyList<TimeRange> native = NativeOf(read, before, after);
        var judge = new Judge(metric, read.Asked[before].SpanTicks, read.Asked[after].SpanTicks, mechanism => Judged(mechanism)
            .Select(judged => SessionCoverage.CollectedOtherwise(read.Ledger, judged, native))
            .FirstOrDefault(reason => reason is not null));

        // Each relationship's measure over both intervals, and each process's connections to another host's, measured at
        // this machine's end: their records, and what was sent across or there.
        bool bytes = metric == ComparisonMetric.BytesSent;
        (CommunicationEdge Edge, Reach First, Reach Second)[] edges =
            [.. overview.Edges.Select(edge => (edge, then.Edges[edge.Key], now.Edges[edge.Key]))];
        (OtherHostReach Reach, Reach First, Reach Second)[] hosts = [.. read.Reaches
            .Select(entry => (entry.Value, then.Hosts.GetValueOrDefault(entry.Key), now.Hosts.GetValueOrDefault(entry.Key)))];
        ComparedRelationship RowOf(Reach first, Reach second, Mechanism mechanism) => judge.Row(
            Measure(first, bytes, CoverageOf(mechanism, then.Coverage)),
            Measure(second, bytes, CoverageOf(mechanism, now.Coverage)),
            mechanism);

        var rows = new List<ComparedRelationship>();
        int quiet = 0;
        if (grouping == ComparisonGrouping.Executable)
        {
            ByExecutable grouped = ByExecutable.Of(overview, edges, hosts);
            return (rows, Executables(grouped, RowOf, rows, everyRow), grouped.Unmatched);
        }

        foreach ((CommunicationEdge edge, Reach first, Reach second) in edges)
        {
            if (first.Records == 0 && second.Records == 0) quiet++;
            if (everyRow || first.Records != 0 || second.Records != 0) rows.Add(RowOf(first, second, edge.Mechanism) with { Relationship = edge });
        }

        foreach ((OtherHostReach reach, Reach first, Reach second) in hosts)
        {
            if (first.Records == 0 && second.Records == 0) quiet++;
            if (everyRow || first.Records != 0 || second.Records != 0) rows.Add(RowOf(first, second, reach.Link.Mechanism) with { Host = reach });
        }

        return (rows, quiet, new(0, 0, 0));
    }

    /// <summary>The native intervals of a read's intervals at <paramref name="indexes"/>, those its clock could place.</summary>
    private static IReadOnlyList<TimeRange> NativeOf(SessionRead read, params int[] indexes) =>
        [.. indexes.Select(index => read.Native[index]).OfType<TimeRange>()];

    /// <summary>Why two captures cannot be compared when both name one session.</summary>
    public const string OneSession = "Both captures are one session; compare two of its intervals as one session instead.";

    /// <summary>
    /// Compares <paramref name="compared"/>, an interval of the capture <paramref name="comparedStore"/> holds, with
    /// <paramref name="baseline"/>, an interval of another capture, <paramref name="baselineStore"/> (`contracts/comparison-v1.md`
    /// §2b): each interval in its own session's presentation ticks, by <paramref name="metric"/>, under
    /// <paramref name="policy"/>, row by row by executable - the processes each capture ran from one path, paired across the
    /// captures by that path, a grouping and never an identity (P6, ADR-059). A change is stated only where both captures
    /// covered a row's mechanism and collected it through the same descriptors. Each capture is read at one generation of
    /// its own, read again where it publishes between reads.
    /// </summary>
    public static IntervalComparison CompareCaptures(
        SessionStore baselineStore,
        TimeRange baseline,
        SessionStore comparedStore,
        TimeRange compared,
        ComparisonMetric metric = ComparisonMetric.Records,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselineStore);
        ArgumentNullException.ThrowIfNull(comparedStore);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        ArgumentOutOfRangeException.ThrowIfLessThan(baseline.SpanTicks, 1, nameof(baseline));
        ArgumentOutOfRangeException.ThrowIfLessThan(compared.SpanTicks, 1, nameof(compared));

        SessionRead first = Settled(baselineStore, [baseline], metric, policy, cancellationToken);
        SessionRead second = Settled(comparedStore, [compared], metric, policy, cancellationToken);
        if (first.Overview.SessionId == second.Overview.SessionId)
        {
            throw new ArgumentException(OneSession, nameof(baselineStore));
        }

        return OfCaptures(first, second, 0, 0, metric, policy);
    }

    /// <summary>
    /// Two captures' comparison: the baseline capture's read interval <paramref name="before"/> with the compared capture's
    /// read interval <paramref name="after"/>, by executable.
    /// </summary>
    private static IntervalComparison OfCaptures(
        SessionRead first, SessionRead second, int before, int after, ComparisonMetric metric, EvidencePolicy policy)
    {
        (List<ComparedRelationship> rows, int quiet, UnmatchedRows unmatched) = CaptureRows(first, second, before, after, metric, everyRow: false);

        // An executable is named over both captures' groups, with the fewest folders that tell two of one name apart.
        var paths = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ProcessGroup group in first.Overview.Groups.Concat(second.Overview.Groups))
        {
            paths.TryAdd(group.Key, group.Detail);
        }

        Dictionary<string, string> labels = ExecutableNames.Label([.. paths.Select(path => (path.Key, path.Value))]);
        return new(second.Overview.SessionId, second.Overview.Generation, metric, policy, first.Asked[before], second.Asked[after],
            first.Intervals[before].Coverage, second.Intervals[after].Coverage, Array.AsReadOnly(Ranked(rows)))
        {
            Processes = first.Overview.Nodes.Concat(second.Overview.Nodes).DistinctBy(node => node.Id).ToDictionary(node => node.Id),
            Quiet = quiet,
            Grouping = ComparisonGrouping.Executable,
            Groups = paths.ToDictionary(path => path.Key,
                path => new ProcessGroup(path.Key, labels[path.Key], LaneGrouping.Executable, path.Value), StringComparer.Ordinal),
            Unmatched = unmatched,
            BaselineSessionId = first.Overview.SessionId,
            BaselineGeneration = first.Overview.Generation,
            SameHost = (first.Clock, second.Clock) is ({ } one, { } other) ? one.HostId == other.HostId : null,
        };
    }

    /// <summary>
    /// Two captures' rows by executable: the baseline capture's read interval <paramref name="before"/> with the compared
    /// capture's read interval <paramref name="after"/>, each judged by its own capture's coverage, and the two captures by
    /// whether they collected each mechanism through the same descriptors; each with a record in either, or with
    /// <paramref name="everyRow"/> each the captures' executables share or hold alone.
    /// </summary>
    private static (List<ComparedRelationship> Rows, int Quiet, UnmatchedRows Unmatched) CaptureRows(
        SessionRead first, SessionRead second, int before, int after, ComparisonMetric metric, bool everyRow)
    {
        IReadOnlyList<TimeRange> thenNative = NativeOf(first, before), nowNative = NativeOf(second, after);
        var judge = new Judge(metric, first.Asked[before].SpanTicks, second.Asked[after].SpanTicks, mechanism => Judged(mechanism)
            .Select(judged => SessionCoverage.CollectedOtherwise(first.Ledger, thenNative, second.Ledger, nowNative, judged))
            .FirstOrDefault(reason => reason is not null));
        IntervalRead then = first.Intervals[before], now = second.Intervals[after];
        bool bytes = metric == ComparisonMetric.BytesSent;
        ComparedRelationship RowOf(Reach earlier, Reach later, Mechanism mechanism) => judge.Row(
            Measure(earlier, bytes, CoverageOf(mechanism, then.Coverage)),
            Measure(later, bytes, CoverageOf(mechanism, now.Coverage)),
            mechanism);

        // Each capture's rows by executable, the baseline's in its interval and the compared capture's in its own, paired
        // by their keys: the paths both captures group processes by.
        ByExecutable grouped = ByExecutable.Merge(
            ByExecutable.Of(first.Overview,
                [.. first.Overview.Edges.Select(edge => (edge, then.Edges[edge.Key], default(Reach)))],
                [.. first.Reaches.Select(entry => (entry.Value, then.Hosts.GetValueOrDefault(entry.Key), default(Reach)))]),
            ByExecutable.Of(second.Overview,
                [.. second.Overview.Edges.Select(edge => (edge, default(Reach), now.Edges[edge.Key]))],
                [.. second.Reaches.Select(entry => (entry.Value, default(Reach), now.Hosts.GetValueOrDefault(entry.Key)))]));
        var rows = new List<ComparedRelationship>();
        return (rows, Executables(grouped, RowOf, rows, everyRow), grouped.Unmatched);
    }

    /// <summary>One capture's intervals read at one generation, read again where it publishes between reads.</summary>
    private static SessionRead Settled(
        SessionStore store, IReadOnlyList<TimeRange> intervals, ComparisonMetric metric, EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (ReadSession(store, intervals, metric, policy, betweenReads: null, cancellationToken) is { } read)
            {
                return read;
            }
        }

        throw new InvalidOperationException(Unsettled);
    }

    /// <summary>
    /// Every read a comparison makes of one session over its intervals, all of one generation: its overview, and for each
    /// interval its counts, what was sent across and its connections to other hosts, with the ledger and clock that judge
    /// whether they were collected alike; null where a read was of another generation than the overview.
    /// </summary>
    private static SessionRead? ReadSession(
        SessionStore store,
        IReadOnlyList<TimeRange> intervals,
        ComparisonMetric metric,
        EvidencePolicy policy,
        Action? betweenReads,
        CancellationToken cancellationToken)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(store, policy, cancellationToken: cancellationToken);
        betweenReads?.Invoke();
        SessionIntervalCounts[] counts = [.. intervals.Select(interval => SessionIntervalQuery.Count(store, interval, policy, cancellationToken))];
        SessionByteMeasures?[] sent = [.. intervals.Select(interval => metric == ComparisonMetric.BytesSent
            ? SessionByteRanking.Measure(store, interval, policy, cancellationToken)
            : null)];

        // Each process's connections to other hosts, by holder, transport and address, as the census counts them.
        Dictionary<string, OtherHostReach> reaches = new(StringComparer.OrdinalIgnoreCase);
        foreach (HostLink link in overview.HostLinks)
        {
            foreach (HostAddress address in link.Addresses)
            {
                reaches.TryAdd(ReachKey(link.Process, link.Mechanism, address.Address), new(link, address));
            }
        }

        bool Reaches(TransportConnection connection) => reaches.ContainsKey(
            ReachKey(connection.Holder.Id, connection.Mechanism, HostScopeKeys.EndpointAddress(connection.RemoteEndpoint)));
        HeldConnections?[] held = [.. intervals.Select(interval => reaches.Count > 0
            ? new HeldConnections(SessionConnections.HeldIn(store, Reaches, interval, policy, cancellationToken))
            : null)];

        // The ledger and clock that judge whether the intervals collected alike, from the same generation as the counts.
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        (Guid Session, long Generation)[] reads =
        [
            (overview.SessionId, overview.Generation),
            .. counts.Select(count => (count.SessionId, count.Generation)),
            .. sent.OfType<SessionByteMeasures>().Select(bytes => (bytes.SessionId, bytes.Generation)),
            .. held.OfType<HeldConnections>().Select(hosts => (hosts.Read.Session, hosts.Read.Generation)),
            (manifest.SessionId, manifest.Generation),
        ];
        if (reads.Any(read => read.Session != overview.SessionId))
        {
            throw new InvalidOperationException("This directory began holding another session while it was compared.");
        }

        if (reads.Any(read => read.Generation != overview.Generation))
        {
            return null;
        }

        CoverageLedgerV1? ledger = SessionSegments.CoverageLedger(store.Root, manifest);
        SourceClockDescriptor? clock = SessionSegments.SourceClock(store.Root, manifest);
        IntervalRead[] measured = [.. intervals.Select((interval, index) => new IntervalRead(
            counts[index].MechanismCoverage,
            overview.Edges.ToDictionary(edge => edge.Key, edge => Sum(counts[index].EdgeRecords.GetValueOrDefault(edge.Key),
                sent[index] is { } bytes ? SentAcross(edge, bytes) : default), StringComparer.Ordinal),
            Reached(held[index])))];
        TimeRange?[] native = [.. intervals
            .Select(interval => clock is { } readings ? RankingScope.NativeInterval(readings, interval) : null)];
        return new(overview, intervals, measured, [.. reaches.OrderBy(entry => entry.Key, StringComparer.Ordinal)], ledger, clock,
            native);
    }

    /// <summary>
    /// The rows of a comparison by executable, in <paramref name="rows"/>: each tally with a record in either interval, or
    /// with <paramref name="everyRow"/> each, judged by <paramref name="rowOf"/>. Returns how many had none, which are quiet.
    /// </summary>
    private static int Executables(ByExecutable grouped, Func<Reach, Reach, Mechanism, ComparedRelationship> rowOf,
        List<ComparedRelationship> rows, bool everyRow)
    {
        int quiet = 0;
        foreach (ExecutableTally tally in grouped.Tallies)
        {
            if (tally.First.Records == 0 && tally.Second.Records == 0) quiet++;
            if (everyRow || tally.First.Records != 0 || tally.Second.Records != 0) rows.Add(rowOf(tally.First, tally.Second, tally.Mechanism) with
            {
                Executables = tally.Relationship,
                ExecutableHost = tally.Reach,
            });
        }

        return quiet;
    }

    /// <summary>
    /// The comparable rows first, the most changed first; then those that cannot be compared, the busiest first; each tie
    /// by its key, so a comparison read twice lists its rows alike (R7).
    /// </summary>
    private static ComparedRelationship[] Ranked(IEnumerable<ComparedRelationship> rows) => [.. rows
        .OrderBy(row => row.Change == ComparisonChange.NotComparable)
        .ThenByDescending(row => row.Change == ComparisonChange.NotComparable ? row.ScaledBusier : Int128.Abs(row.ScaledDifference))
        .ThenBy(row => row.Key, StringComparer.Ordinal)];

    /// <summary>A row's records over one interval with what was sent across, or none sent where bytes are not read.</summary>
    private static Reach Sum(long records, (long Sent, long Measured, long Unmeasured) bytes) =>
        new(records, bytes.Sent, bytes.Measured, bytes.Unmeasured);

    /// <summary>A row's measure over one interval: its records, and under a byte comparison what was sent across.</summary>
    private static ComparedMeasure Measure(Reach reach, bool bytes, MechanismCoverage coverage) =>
        Measure(reach.Records, bytes ? (reach.Sent, reach.Measured, reach.Unmeasured) : null, coverage);

    /// <summary>
    /// A row's measure over one interval: its records, and under a byte comparison what was sent across, each transfer
    /// counted once at its sender.
    /// </summary>
    private static ComparedMeasure Measure(
        long records, (long Sent, long Measured, long Unmeasured)? bytes, MechanismCoverage coverage) =>
        bytes is not { } sent
            ? new(records, records, records, 0, coverage)
            : new(records, sent.Measured > 0 ? sent.Sent : sent.Unmeasured > 0 ? null : 0, sent.Measured, sent.Unmeasured, coverage);

    /// <summary>
    /// What both ends of a relationship sent across its channels, each transfer counted once at its sender: the bytes the
    /// sends that measured a size carried, and how many measured one or recorded none. A relationship whose evidence names
    /// no channel, as RPC calls' does, sent nothing a byte measure holds.
    /// </summary>
    internal static (long Sent, long Measured, long Unmeasured) SentAcross(CommunicationEdge edge, SessionByteMeasures bytes)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(bytes);
        long sent = 0, measured = 0, unmeasured = 0;
        foreach (string channel in edge.Evidence)
        {
            foreach (ProcessInstanceId end in new[] { edge.SourceId, edge.TargetId }.Distinct())
            {
                if (bytes.ByChannelEnd.GetValueOrDefault(new ChannelEnd(channel, end)) is { } ofEnd)
                {
                    sent = SegmentMeasurement.AddBytes(sent, ofEnd.SentBytes);
                    measured += ofEnd.SentMeasured;
                    unmeasured += ofEnd.SentUnmeasured;
                }
            }
        }

        return (sent, measured, unmeasured);
    }

    /// <summary>
    /// What each process's connections to each other host held over one interval, by the key of the census's reach: their
    /// records, and the bytes the process's sends there carried, how many measured a size and how many recorded none.
    /// </summary>
    private static Dictionary<string, Reach> Reached(HeldConnections? hosts)
    {
        var reached = new Dictionary<string, Reach>(StringComparer.OrdinalIgnoreCase);
        foreach ((TransportConnection connection, ConnectionSummary summary) in hosts?.Read.Held ?? [])
        {
            string key = ReachKey(connection.Holder.Id, connection.Mechanism, HostScopeKeys.EndpointAddress(connection.RemoteEndpoint));
            Reach sum = reached.GetValueOrDefault(key);
            reached[key] = new(sum.Records + summary.Records, SegmentMeasurement.AddBytes(sum.Sent, summary.SentBytes),
                sum.Measured + summary.Sends - summary.UnmeasuredSends, sum.Unmeasured + summary.UnmeasuredSends);
        }

        return reached;
    }

    private static string ReachKey(ProcessInstanceId process, Mechanism mechanism, string address) =>
        string.Create(CultureInfo.InvariantCulture, $"{process}|{(int)mechanism}|{address}");

    /// <summary>
    /// The mechanisms whose coverage a row's records are judged by: its own, and for RPC calls also ALPC, whose messages
    /// link each call to the call that served it (ADR-034).
    /// </summary>
    private static Mechanism[] Judged(Mechanism mechanism) =>
        mechanism == Mechanism.Rpc ? [Mechanism.Rpc, Mechanism.Alpc] : [mechanism];

    /// <summary>The worst coverage of the mechanisms a row is judged by, the first of them where two are as bad.</summary>
    private static MechanismCoverage CoverageOf(Mechanism mechanism, IReadOnlyList<MechanismCoverage> over)
    {
        MechanismCoverage[] judged = [.. Judged(mechanism)
            .Select(each => over.FirstOrDefault(entry => entry.Mechanism == each))
            .OfType<MechanismCoverage>()];
        return judged.Length == 0
            ? new(mechanism, CoverageState.UnknownCoverage, "its coverage over this interval was not judged")
            : judged.MaxBy(entry => entry.State)!;
    }

    /// <summary>
    /// One executable row being summed: its relationship or its reach, and its processes' records over each interval with
    /// what they sent across or there.
    /// </summary>
    private sealed class ExecutableTally(Mechanism mechanism)
    {
        public Mechanism Mechanism { get; } = mechanism;
        public Reach First { get; set; }
        public Reach Second { get; set; }
        public ExecutableRelationship? Relationship { get; set; }
        public ExecutableReach? Reach { get; set; }

        /// <summary>Adds another tally of the same row, as two captures' tallies of one key are added.</summary>
        public void Merge(ExecutableTally other)
        {
            Add(other.First, other.Second);
            if (Relationship is { } mine && other.Relationship is { } theirs)
            {
                Relationship = mine with
                {
                    SourceConnected = mine.SourceConnected + theirs.SourceConnected,
                    TargetConnected = mine.TargetConnected + theirs.TargetConnected,
                    AcceptedPorts = [.. mine.AcceptedPorts.Union(theirs.AcceptedPorts).Order()],
                };
            }

            if (Reach is { } reach && other.Reach is { } theirReach)
            {
                Reach = reach with
                {
                    Connected = reach.Connected + theirReach.Connected,
                    Accepted = reach.Accepted + theirReach.Accepted,
                    Ports = [.. reach.Ports.Union(theirReach.Ports).Order()],
                    AcceptedPorts = [.. reach.AcceptedPorts.Union(theirReach.AcceptedPorts).Order()],
                };
            }
        }

        public void Add(Reach first, Reach second)
        {
            First = new(First.Records + first.Records, SegmentMeasurement.AddBytes(First.Sent, first.Sent),
                First.Measured + first.Measured, First.Unmeasured + first.Unmeasured);
            Second = new(Second.Records + second.Records, SegmentMeasurement.AddBytes(Second.Sent, second.Sent),
                Second.Measured + second.Measured, Second.Unmeasured + second.Unmeasured);
        }
    }

    /// <summary>
    /// The relationships and connections to other hosts of a session's processes summed by executable (`comparison-v1`
    /// §2a): each pair of executables' relationships over one mechanism, each executable's connections over one transport to
    /// one address, by key, and what a process of no witnessed executable is at an end of, counted apart.
    /// </summary>
    private sealed record ByExecutable(IReadOnlyList<ExecutableTally> Tallies, UnmatchedRows Unmatched)
    {
        public static ByExecutable Of(
            SessionOverviewBundle overview,
            IReadOnlyList<(CommunicationEdge Edge, Reach First, Reach Second)> edges,
            IReadOnlyList<(OtherHostReach Reach, Reach First, Reach Second)> hosts)
        {
            Dictionary<ProcessInstanceId, string> groups = overview.Nodes
                .Where(node => node.GroupKey != ProcessGroup.UnwitnessedExecutableKey)
                .ToDictionary(node => node.Id, node => node.GroupKey);
            var tallies = new SortedDictionary<string, ExecutableTally>(StringComparer.Ordinal);
            var accepted = new Dictionary<string, SortedSet<ushort>>(StringComparer.Ordinal);
            var ports = new Dictionary<string, SortedSet<ushort>>(StringComparer.Ordinal);
            int unmatched = 0;
            long unmatchedFirst = 0, unmatchedSecond = 0;
            void Unmatched(Reach first, Reach second)
            {
                if (first.Records == 0 && second.Records == 0) return;
                unmatched++;
                unmatchedFirst += first.Records;
                unmatchedSecond += second.Records;
            }

            foreach ((CommunicationEdge edge, Reach first, Reach second) in edges)
            {
                if (!groups.TryGetValue(edge.SourceId, out string? from) || !groups.TryGetValue(edge.TargetId, out string? to))
                {
                    Unmatched(first, second);
                    continue;
                }

                // The pair is keyed in ordinal order, whichever of its processes the overview drew first; who connected to
                // whom follows the processes into it.
                bool ordered = string.CompareOrdinal(from, to) <= 0;
                (string source, string target) = ordered ? (from, to) : (to, from);
                string key = string.Create(CultureInfo.InvariantCulture,
                    $"executables:{MechanismText.Name(edge.Mechanism).ToLowerInvariant()}:{source}|{target}");
                if (!tallies.TryGetValue(key, out ExecutableTally? tally))
                {
                    tally = new(edge.Mechanism) { Relationship = new(key, source, target, edge.Mechanism) };
                    tallies[key] = tally;
                    accepted[key] = [];
                }

                tally.Add(first, second);
                tally.Relationship = tally.Relationship! with
                {
                    SourceConnected = tally.Relationship.SourceConnected + (ordered ? edge.SourceConnected : edge.TargetConnected),
                    TargetConnected = tally.Relationship.TargetConnected + (ordered ? edge.TargetConnected : edge.SourceConnected),
                };
                accepted[key].UnionWith(edge.AcceptedPorts);
            }

            foreach ((OtherHostReach reach, Reach first, Reach second) in hosts)
            {
                if (!groups.TryGetValue(reach.Link.Process, out string? executable))
                {
                    Unmatched(first, second);
                    continue;
                }

                Mechanism mechanism = reach.Link.Mechanism;
                string key = string.Create(CultureInfo.InvariantCulture,
                    $"executable-hosts:{MechanismText.Name(mechanism).ToLowerInvariant()}:{executable}@{reach.Address.Address}");
                if (!tallies.TryGetValue(key, out ExecutableTally? tally))
                {
                    tally = new(mechanism) { Reach = new(key, executable, mechanism, reach.Address.Address) };
                    tallies[key] = tally;
                    accepted[key] = [];
                    ports[key] = [];
                }

                tally.Add(first, second);
                tally.Reach = tally.Reach! with
                {
                    Connected = tally.Reach.Connected + reach.Address.Connected,
                    Accepted = tally.Reach.Accepted + reach.Address.Accepted,
                };
                accepted[key].UnionWith(reach.Address.AcceptedPorts);
                ports[key].UnionWith(reach.Address.Ports);
            }

            foreach ((string key, ExecutableTally tally) in tallies)
            {
                if (tally.Relationship is { } pair) tally.Relationship = pair with { AcceptedPorts = [.. accepted[key]] };
                if (tally.Reach is { } reach) tally.Reach = reach with { AcceptedPorts = [.. accepted[key]], Ports = [.. ports[key]] };
            }

            return new([.. tallies.Values], new(unmatched, unmatchedFirst, unmatchedSecond));
        }

        /// <summary>
        /// Two captures' rows by executable, paired by key - the baseline's tallies holding its interval's measures, the
        /// compared capture's its own - and what each could not group, added.
        /// </summary>
        public static ByExecutable Merge(ByExecutable baseline, ByExecutable compared)
        {
            var tallies = new SortedDictionary<string, ExecutableTally>(StringComparer.Ordinal);
            foreach (ExecutableTally tally in baseline.Tallies.Concat(compared.Tallies))
            {
                string key = tally.Relationship?.Key ?? tally.Reach!.Key;
                if (tallies.TryGetValue(key, out ExecutableTally? paired)) paired.Merge(tally);
                else tallies[key] = tally;
            }

            return new([.. tallies.Values], new(baseline.Unmatched.Rows + compared.Unmatched.Rows,
                baseline.Unmatched.BaselineRecords + compared.Unmatched.BaselineRecords,
                baseline.Unmatched.ComparedRecords + compared.Unmatched.ComparedRecords));
        }
    }

    /// <summary>
    /// Every read of one session a comparison makes, of one generation: its overview, the intervals asked and each one's
    /// measures in that order, its processes' connections to other hosts by the census's keys, in key order, and the ledger,
    /// clock and native intervals - each asked interval's, null where the clock places none - that judge whether its
    /// intervals were collected alike.
    /// </summary>
    private sealed record SessionRead(
        SessionOverviewBundle Overview,
        IReadOnlyList<TimeRange> Asked,
        IReadOnlyList<IntervalRead> Intervals,
        IReadOnlyList<KeyValuePair<string, OtherHostReach>> Reaches,
        CoverageLedgerV1? Ledger,
        SourceClockDescriptor? Clock,
        IReadOnlyList<TimeRange?> Native);

    /// <summary>
    /// One interval of a session: what the capture covered over it, each relationship's records there with what was sent
    /// across it, by the relationship's key, and each process's connections to another host's, by the census's key.
    /// </summary>
    private sealed record IntervalRead(
        IReadOnlyList<MechanismCoverage> Coverage,
        IReadOnlyDictionary<string, Reach> Edges,
        IReadOnlyDictionary<string, Reach> Hosts);

    /// <summary>One interval's read of the connections to other hosts, with the session and generation it was read from.</summary>
    private sealed record HeldConnections(
        (Guid Session, long Generation, IReadOnlyList<(TransportConnection Connection, ConnectionSummary Summary)> Held) Read);

    /// <summary>What one process's connections to one other host held over one interval, at this machine's end.</summary>
    private readonly record struct Reach(long Records, long Sent, long Measured, long Unmeasured);

    /// <summary>
    /// How a row changed, rates compared exactly: the compared value times the baseline's length against the baseline
    /// value times the compared length. A value short of exact decides a change only where its bound alone does.
    /// </summary>
    private sealed class Judge(
        ComparisonMetric metric,
        Int128 baselineSpan,
        Int128 comparedSpan,
        Func<Mechanism, string?> collectedOtherwise)
    {
        public ComparedRelationship Row(ComparedMeasure then, ComparedMeasure now, Mechanism mechanism)
        {
            Int128 scaledNow = (Int128)(now.Value ?? 0) * baselineSpan;
            Int128 scaledThen = (Int128)(then.Value ?? 0) * comparedSpan;
            var row = new ComparedRelationship(then, now, ComparisonChange.NotComparable, null)
            {
                ScaledBusier = Int128.Max(now.Records * baselineSpan, then.Records * comparedSpan),
            };

            string? reason = NotComparable(mechanism, then, now);
            if (reason is not null)
            {
                return row with { Reason = reason };
            }

            ComparisonChange change = (then.None, now.None) switch
            {
                (true, true) => ComparisonChange.Unchanged,
                (true, false) => ComparisonChange.Appeared,
                (false, true) => ComparisonChange.Disappeared,
                _ when then.Exact && now.Exact => scaledNow.CompareTo(scaledThen) switch
                {
                    > 0 => ComparisonChange.Increased,
                    < 0 => ComparisonChange.Decreased,
                    _ => ComparisonChange.Unchanged,
                },

                // A lower bound decides only a change it already exceeds: more than the baseline's exact rate, or a baseline
                // whose bound is more than the compared interval's exact rate. An unknown value, scaled as none, decides none.
                _ when then.Exact && scaledNow > scaledThen => ComparisonChange.Increased,
                _ when now.Exact && scaledThen > scaledNow => ComparisonChange.Decreased,
                _ => ComparisonChange.NotComparable,
            };
            return change == ComparisonChange.NotComparable
                ? row with { Reason = Unsized(then, now) }
                : row with
                {
                    Change = change,
                    ScaledDifference = scaledNow - scaledThen,
                    FromLowerBound = !(then.Exact && now.Exact) && change is ComparisonChange.Increased or ComparisonChange.Decreased,
                };
        }

        /// <summary>
        /// Why a row's two values are not of one measurement, before either is read: bytes asked of RPC calls, which record
        /// no size; an interval that did not cover its mechanism; or one whose sources collected it otherwise. Null when
        /// they are.
        /// </summary>
        private string? NotComparable(Mechanism mechanism, ComparedMeasure then, ComparedMeasure now)
        {
            if (metric == ComparisonMetric.BytesSent && mechanism == Mechanism.Rpc)
            {
                return "RPC calls record no size, so no bytes are measured of them in either interval.";
            }

            if (then.Coverage.State != CoverageState.Covered)
            {
                return SessionCoverage.Sentence(then.Coverage, "the baseline");
            }

            if (now.Coverage.State != CoverageState.Covered)
            {
                return SessionCoverage.Sentence(now.Coverage, "the compared interval");
            }

            return collectedOtherwise(mechanism) is { } otherwise
                ? string.Concat(char.ToUpperInvariant(otherwise[0]).ToString(), otherwise[1..], ".")
                : null;
        }

        /// <summary>Why bytes with sends that recorded no size cannot decide a change: which interval's are a lower bound.</summary>
        private static string Unsized(ComparedMeasure then, ComparedMeasure now)
        {
            static string Sends(long count) => string.Create(CultureInfo.InvariantCulture, $"{count:N0}");
            return (then.Unmeasured > 0, now.Unmeasured > 0) switch
            {
                (true, true) => $"{Sends(then.Unmeasured)} {CountText.Agree(then.Unmeasured, "send", "sends")} in the baseline and "
                    + $"{Sends(now.Unmeasured)} in the compared interval recorded no size, so what was sent across in each is a "
                    + "lower bound, and the two bounds cannot decide whether it rose or fell.",
                (true, false) => $"{Sends(then.Unmeasured)} {CountText.Agree(then.Unmeasured, "send", "sends")} in the baseline "
                    + "recorded no size, so what was sent across there is a lower bound, which cannot decide whether it rose or fell.",
                _ => $"{Sends(now.Unmeasured)} {CountText.Agree(now.Unmeasured, "send", "sends")} in the compared interval "
                    + "recorded no size, so what was sent across there is a lower bound, which cannot decide whether it rose or fell.",
            };
        }
    }
}
