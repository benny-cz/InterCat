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
}

/// <summary>Compares two intervals of a session's generation row by row (`comparison-v1`, plan M12).</summary>
public static class SessionComparison
{
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
        CancellationToken cancellationToken)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(store, policy, cancellationToken: cancellationToken);
        betweenReads?.Invoke();
        SessionIntervalCounts before = SessionIntervalQuery.Count(store, baseline, policy, cancellationToken);
        SessionIntervalCounts after = SessionIntervalQuery.Count(store, compared, policy, cancellationToken);
        SessionByteMeasures? beforeBytes = null, afterBytes = null;
        if (metric == ComparisonMetric.BytesSent)
        {
            beforeBytes = SessionByteRanking.Measure(store, baseline, policy, cancellationToken);
            afterBytes = SessionByteRanking.Measure(store, compared, policy, cancellationToken);
        }

        // Each process's connections to other hosts, by holder, transport and address, as the census counts them.
        Dictionary<string, OtherHostReach> reaches = new(StringComparer.OrdinalIgnoreCase);
        foreach (HostLink link in overview.HostLinks)
        {
            foreach (HostAddress address in link.Addresses)
            {
                reaches.TryAdd(ReachKey(link.Process, link.Mechanism, address.Address), new(link, address));
            }
        }

        HeldConnections? beforeHosts = null, afterHosts = null;
        if (reaches.Count > 0)
        {
            bool Reaches(TransportConnection connection) => reaches.ContainsKey(
                ReachKey(connection.Holder.Id, connection.Mechanism, HostScopeKeys.EndpointAddress(connection.RemoteEndpoint)));
            beforeHosts = new(SessionConnections.HeldIn(store, Reaches, baseline, policy, cancellationToken));
            afterHosts = new(SessionConnections.HeldIn(store, Reaches, compared, policy, cancellationToken));
        }

        // The ledger and clock that judge whether both intervals collected alike, from the same generation as the counts.
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        (Guid Session, long Generation)[] reads =
        [
            (overview.SessionId, overview.Generation), (before.SessionId, before.Generation), (after.SessionId, after.Generation),
            .. new[] { beforeBytes, afterBytes }.OfType<SessionByteMeasures>().Select(bytes => (bytes.SessionId, bytes.Generation)),
            .. new[] { beforeHosts, afterHosts }.OfType<HeldConnections>().Select(hosts => (hosts.Read.Session, hosts.Read.Generation)),
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
        TimeRange[] native = [.. new[] { baseline, compared }
            .Select(interval => clock is { } readings ? RankingScope.NativeInterval(readings, interval) : null)
            .OfType<TimeRange>()];
        var judge = new Judge(metric, baseline.SpanTicks, compared.SpanTicks, mechanism => Judged(mechanism)
            .Select(judged => SessionCoverage.CollectedOtherwise(ledger, judged, native))
            .FirstOrDefault(reason => reason is not null));

        // Each relationship's measure over both intervals, and each process's connections to another host's, measured at
        // this machine's end: their records, and what was sent across or there.
        bool bytes = metric == ComparisonMetric.BytesSent;
        (CommunicationEdge Edge, Reach First, Reach Second)[] edges = [.. overview.Edges.Select(edge => (edge,
            Sum(before.EdgeRecords.GetValueOrDefault(edge.Key), beforeBytes is null ? default : SentAcross(edge, beforeBytes)),
            Sum(after.EdgeRecords.GetValueOrDefault(edge.Key), afterBytes is null ? default : SentAcross(edge, afterBytes))))];
        Dictionary<string, Reach> then = Reached(beforeHosts), now = Reached(afterHosts);
        (OtherHostReach Reach, Reach First, Reach Second)[] hosts = [.. reaches.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Value, then.GetValueOrDefault(entry.Key), now.GetValueOrDefault(entry.Key)))];
        ComparedRelationship RowOf(Reach first, Reach second, Mechanism mechanism) => judge.Row(
            Measure(first, bytes, CoverageOf(mechanism, before.MechanismCoverage)),
            Measure(second, bytes, CoverageOf(mechanism, after.MechanismCoverage)),
            mechanism);

        var rows = new List<ComparedRelationship>();
        int quiet = 0;
        UnmatchedRows unmatched = new(0, 0, 0);
        if (grouping == ComparisonGrouping.Process)
        {
            foreach ((CommunicationEdge edge, Reach first, Reach second) in edges)
            {
                if (first.Records == 0 && second.Records == 0) quiet++;
                else rows.Add(RowOf(first, second, edge.Mechanism) with { Relationship = edge });
            }

            foreach ((OtherHostReach reach, Reach first, Reach second) in hosts)
            {
                if (first.Records == 0 && second.Records == 0) quiet++;
                else rows.Add(RowOf(first, second, reach.Link.Mechanism) with { Host = reach });
            }
        }
        else
        {
            ByExecutable grouped = ByExecutable.Of(overview, edges, hosts);
            unmatched = grouped.Unmatched;
            foreach (ExecutableTally tally in grouped.Tallies)
            {
                if (tally.First.Records == 0 && tally.Second.Records == 0) quiet++;
                else rows.Add(RowOf(tally.First, tally.Second, tally.Mechanism) with
                {
                    Executables = tally.Relationship,
                    ExecutableHost = tally.Reach,
                });
            }
        }

        // The comparable rows first, the most changed first; then those that cannot be compared, the busiest first; each
        // tie by its key, so a comparison read twice lists its rows alike (R7).
        ComparedRelationship[] ranked = [.. rows
            .OrderBy(row => row.Change == ComparisonChange.NotComparable)
            .ThenByDescending(row => row.Change == ComparisonChange.NotComparable ? row.ScaledBusier : Int128.Abs(row.ScaledDifference))
            .ThenBy(row => row.Key, StringComparer.Ordinal)];
        return new(overview.SessionId, overview.Generation, metric, policy, baseline, compared, before.MechanismCoverage,
            after.MechanismCoverage, Array.AsReadOnly(ranked))
        {
            Processes = overview.Nodes.ToDictionary(node => node.Id),
            Quiet = quiet,
            Grouping = grouping,
            Groups = overview.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal),
            Unmatched = unmatched,
        };
    }

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
    }

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
