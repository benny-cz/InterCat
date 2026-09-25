using System.Runtime.CompilerServices;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The whole retained extent at a coarse fixed resolution, for the minimap (plan §6.2): observed rows per column, and
/// the capture's own coverage per column. Capture coverage does not depend on what was observed, so a column emptied by
/// a capture gap still shows the gap.
/// </summary>
public sealed record SessionMinimap(TimeRange Extent, IReadOnlyList<int> Counts, IReadOnlyList<CoverageState> Capture)
{
    /// <summary>Plan §6.2 and §23: the minimap draws the retained extent in at most this many columns.</summary>
    public const int MaximumColumns = 2_000;

    public TimeRange IntervalOf(int column) => TimelineColumns.IntervalOf(Extent, Counts.Count, column);
}

/// <summary>
/// The timeline over one interval at the resolution a viewport needs, from one leased generation. Its buckets follow the
/// overview's rules exactly, so the whole extent at the overview's column count is the overview's own timeline.
/// </summary>
public sealed record SessionTimelineDetail(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyList<TimelineBucket> Buckets)
{
    public IReadOnlyList<MechanismTimelineLane> MechanismLanes { get; init; } = [];
}

/// <summary>One group's exact owner-accounted L1 timeline row, keyed independently of its visual position.</summary>
public sealed record ProcessTimelineLane(ProcessInstanceId ProcessId, IReadOnlyList<TimelineBucket> Buckets);

/// <summary>
/// The records a focused rung's timeline draws in colour: exactly the rows its evidence scope reads (§3.2) - one admitted
/// paired channel, the rows canonically owned by a set of process instances, or both at once. A rung's timeline therefore
/// counts what E lists for it, never a guessed superset.
/// </summary>
public sealed class TimelineFocus
{
    public TimelineFocus(string? channelKey, IReadOnlyCollection<ProcessInstanceId> ownerProcesses)
    {
        ArgumentNullException.ThrowIfNull(ownerProcesses);
        if (channelKey is not null && string.IsNullOrWhiteSpace(channelKey))
        {
            throw new ArgumentException("A channel key cannot be empty.", nameof(channelKey));
        }

        if (ownerProcesses.Any(owner => owner.Value == Guid.Empty))
        {
            throw new ArgumentException("An owner process needs a non-empty instance ID.", nameof(ownerProcesses));
        }

        if (channelKey is null && ownerProcesses.Count == 0)
        {
            throw new ArgumentException("A timeline focus names a channel, owner processes or both; the whole session is no focus.",
                nameof(ownerProcesses));
        }

        ChannelKey = channelKey;
        OwnerProcesses = Array.AsReadOnly([.. ownerProcesses.Distinct().OrderBy(owner => owner.Value.ToString("N"), StringComparer.Ordinal)]);
        Key = $"channel:{channelKey?.Length ?? 0}:{channelKey}|owners:"
            + string.Join(",", OwnerProcesses.Select(owner => owner.Value.ToString("N")));
    }

    public string? ChannelKey { get; }

    /// <summary>The owner instances, distinct and in a stable order.</summary>
    public IReadOnlyList<ProcessInstanceId> OwnerProcesses { get; }

    /// <summary>What the focus reads, as one comparable string: two focuses with the same key count the same rows.</summary>
    public string Key { get; }

    /// <summary>The focus of an evidence scope; null for the whole session or for a scope that cannot be read.</summary>
    public static TimelineFocus? Of(EvidenceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Problem is null && !scope.IsWholeSession ? new(scope.ChannelKey, scope.OwnerProcesses) : null;
    }
}

/// <summary>
/// A viewport's timeline and the part of it one focus reads, counted in one pass into the same columns, so each focus
/// bucket lies inside the whole-timeline bucket of the same interval and never exceeds it.
/// </summary>
public sealed record SessionFocusedTimeline(SessionTimelineDetail Whole, IReadOnlyList<TimelineBucket> Focus)
{
    /// <summary>When a group fits the lane budget, these owner rows partition Focus on the same columns.</summary>
    public IReadOnlyList<ProcessTimelineLane> ProcessLanes { get; init; } = [];

    /// <summary>Why L1 rows were not made; the aggregate focus count remains available and exact.</summary>
    public string? ProcessLaneProblem { get; init; }
}

public static class SessionTimelineQuery
{
    /// <summary>A viewport never needs more columns than the minimap holds for the whole session.</summary>
    public const int MaximumColumns = SessionMinimap.MaximumColumns;

    /// <summary>Hard query-side caps before an L1 row model is allocated, beneath §6.2's 20,000-mark frame budget.</summary>
    public const int MaximumProcessLanes = 200;
    public const int MaximumProcessLaneCells = 20_000;

    /// <summary>
    /// Counts the observations inside a half-open interval of presentation ticks into equal columns. A row without a
    /// usable session time cannot be placed and is not counted, exactly as the overview's timeline omits it. The scan is
    /// cancellable and runs off the input path; the overview's buckets stand in until it answers (P25).
    /// </summary>
    public static SessionTimelineDetail Detail(
        SessionStore store,
        TimeRange interval,
        int columns,
        CancellationToken cancellationToken = default) =>
        Count(store, interval, columns, null, EvidencePolicy.IncludeCorrelated, cancellationToken).Whole;

    /// <summary>
    /// <see cref="Detail"/> together with the rows <paramref name="focus"/> reads, under the evidence rung's own rules and
    /// policy. A focus this generation cannot resolve - an instance it does not hold, a channel it does not admit
    /// uniquely - is refused with the evidence rung's reason rather than counted as nothing.
    /// </summary>
    public static SessionFocusedTimeline Focused(
        SessionStore store,
        TimeRange interval,
        int columns,
        TimelineFocus focus,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(focus);
        (SessionTimelineDetail whole, TimelineBucket[]? focused, ProcessTimelineLane[] lanes, string? problem) =
            Count(store, interval, columns, focus, policy, cancellationToken);
        return new(whole, Array.AsReadOnly(focused!))
        {
            ProcessLanes = Array.AsReadOnly(lanes),
            ProcessLaneProblem = problem,
        };
    }

    private static (SessionTimelineDetail Whole, TimelineBucket[]? Focus,
        ProcessTimelineLane[] ProcessLanes, string? ProcessLaneProblem) Count(
        SessionStore store,
        TimeRange interval,
        int columns,
        TimelineFocus? focus,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaximumColumns);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its timeline cannot be placed.");
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest)
            .Select(name => SessionSegments.Open(store.Root, manifest, name))];
        FocusRows? rows = focus is null ? null : FocusRows.Resolve(store, manifest, segments, clock, focus, policy, cancellationToken);
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        TimelineColumns? focused = rows is null ? null : new TimelineColumns(interval, columns, tallyMechanisms: true);
        bool groupFocus = focus is { ChannelKey: null, OwnerProcesses.Count: > 1 };
        long laneCells = focus is null ? 0 : (long)focus.OwnerProcesses.Count * Math.Min(columns, interval.SpanTicks);
        string? laneProblem = !groupFocus ? null
            : focus!.OwnerProcesses.Count > MaximumProcessLanes || laneCells > MaximumProcessLaneCells
                ? $"This group needs {focus.OwnerProcesses.Count:N0} process lanes and {laneCells:N0} cells; the "
                    + $"current bounds are {MaximumProcessLanes:N0} lanes and {MaximumProcessLaneCells:N0} cells. "
                    + "Its aggregate timeline remains exact. A bounded grouping or paging control is required "
                    + "to inspect these process rows; the query does not silently drop them."
                : null;
        Dictionary<ProcessInstanceId, TimelineColumns>? processColumns = groupFocus && laneProblem is null
            ? focus!.OwnerProcesses.ToDictionary(owner => owner, _ => new TimelineColumns(interval, columns, tallyMechanisms: true))
            : null;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            FocusRows.SegmentRows? inFocus = null;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (times.SignedAt(row) is not { } nanoseconds || counted.ColumnOf(nanoseconds / 100) is not { } column)
                {
                    continue;
                }

                var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
                counted.Add(column, mechanism);
                if (rows is not null)
                {
                    // Bindings are derived only for a segment that has a row inside the interval.
                    inFocus ??= rows.Of(segment);
                    if (inFocus.Includes(row))
                    {
                        focused!.Add(column, mechanism);
                        if (processColumns is not null && inFocus.OwnerId(row) is { } owner)
                        {
                            processColumns[owner].Add(column, mechanism);
                        }
                    }
                }
            }
        }

        CoverageLedgerV1? coverage = SessionSegments.CoverageLedger(store.Root, manifest);
        return (
            new SessionTimelineDetail(manifest.SessionId, manifest.Generation, interval,
                Array.AsReadOnly(counted.Buckets(coverage, clock)))
            {
                MechanismLanes = Array.AsReadOnly(counted.MechanismLanes(coverage, clock)),
            },
            focused?.Buckets(coverage, clock),
            processColumns is null ? [] : [.. focus!.OwnerProcesses.Select(owner =>
                new ProcessTimelineLane(owner, Array.AsReadOnly(processColumns[owner].Buckets(coverage, clock))))],
            laneProblem);
    }
}

/// <summary>
/// A <see cref="TimelineFocus"/> resolved against one leased generation, testing rows by the evidence rung's rules
/// (<see cref="SessionEvidenceQuery"/>): a channel keeps the rows of its one admitted paired incarnation, and owner
/// processes keep the rows canonically bound to one of them and admitted under the policy.
/// </summary>
internal sealed class FocusRows
{
    private readonly ProcessInstanceIndex? processes;
    private readonly TransportRelationIndex? relations;
    private readonly HashSet<int> owners;
    private readonly int? channel;
    private readonly EvidencePolicy policy;

    private FocusRows(ProcessInstanceIndex? processes, TransportRelationIndex? relations, HashSet<int> owners, int? channel,
        EvidencePolicy policy)
    {
        this.processes = processes;
        this.relations = relations;
        this.owners = owners;
        this.channel = channel;
        this.policy = policy;
    }

    public static FocusRows Resolve(
        SessionStore store,
        SessionManifestV1 manifest,
        SegmentReaderV1[] segments,
        SourceClockDescriptor clock,
        TimelineFocus focus,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest)
            .Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        ProcessInstanceIndex? processes = null;
        HashSet<int> owners = [];
        if (focus.OwnerProcesses.Count > 0)
        {
            processes = derivation.Processes(segments, clock, fields, cancellationToken);
            var indexes = new Dictionary<ProcessInstanceId, int>(processes.Instances.Count);
            for (int index = 0; index < processes.Instances.Count; index++)
            {
                indexes.TryAdd(processes.Instances[index].Id, index);
            }

            foreach (ProcessInstanceId owner in focus.OwnerProcesses)
            {
                if (!indexes.TryGetValue(owner, out int index))
                {
                    throw new InvalidOperationException(focus.OwnerProcesses.Count == 1
                        ? "The focused process instance is not in this generation."
                        : "A process instance of the focused group is not in this generation.");
                }

                owners.Add(index);
            }
        }

        TransportRelationIndex? relations = null;
        int? channel = null;
        if (focus.ChannelKey is { } key)
        {
            relations = derivation.Relations(segments, clock, fields, cancellationToken);
            TransportRelation[] matching = [.. relations.Relations.Where(relation =>
                relation.Mechanism == Mechanism.Tcp && relation.StableKey == key
                && SessionOverviewProjector.Admitted(relation.Strength, policy))];
            if (matching.Length != 1)
            {
                throw new InvalidOperationException(
                    "The focused paired TCP channel is not uniquely admitted in this generation and evidence policy.");
            }

            channel = matching[0].Channel;
        }

        return new(processes, relations, owners, channel, policy);
    }

    /// <summary>The row test for one segment, with the bindings it needs derived once.</summary>
    public SegmentRows Of(SegmentReaderV1 segment) => new(
        this,
        channel is null ? null : relations!.ChannelsOf(segment),
        owners.Count == 0 ? null : processes!.OwnersOf(segment));

    internal sealed class SegmentRows(FocusRows scope, ChannelBinding[]? channels, ProcessBinding[]? bindings)
    {
        public bool Includes(int row)
        {
            if (channels is not null && channels[row].Channel != scope.channel)
            {
                return false;
            }

            return bindings is null
                || (scope.owners.Contains(bindings[row].Instance) && bindings[row].IsAdmittedUnder(scope.policy));
        }

        /// <summary>The canonical admitted owner of an included row, for an L1 group partition.</summary>
        public ProcessInstanceId? OwnerId(int row) => bindings is not null
            && scope.owners.Contains(bindings[row].Instance)
            && bindings[row].IsAdmittedUnder(scope.policy)
                ? scope.processes!.Instances[bindings[row].Instance].Id
                : null;
    }
}

/// <summary>
/// Observations counted into equal half-open columns of one interval. Column <c>i</c> holds
/// <c>[start + i·span/n, start + (i+1)·span/n)</c> in presentation ticks, so the columns partition the interval exactly
/// and a column never straddles two others' records.
/// </summary>
internal sealed class TimelineColumns
{
    private readonly TimeRange interval;
    private readonly int[] counts;
    private readonly Dictionary<Mechanism, int>[]? mechanisms;

    public TimelineColumns(TimeRange interval, int columns, bool tallyMechanisms)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        this.interval = interval;
        counts = new int[(int)Math.Min(columns, interval.SpanTicks)];
        if (tallyMechanisms)
        {
            mechanisms = new Dictionary<Mechanism, int>[counts.Length];
            for (int index = 0; index < counts.Length; index++) mechanisms[index] = [];
        }
    }

    public IReadOnlyList<int> Counts => counts;

    /// <summary>The column holding a presentation tick, or null outside the interval.</summary>
    /// <remarks>
    /// Called once per row. It is compiled optimized at once rather than waiting to be promoted from the first tier,
    /// which a projection called every couple of seconds would spend its first seconds waiting for.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int? ColumnOf(long tick) => interval.Contains(tick)
        ? (int)Math.Min(counts.Length - 1, (long)(((Int128)(tick - interval.StartTicks) * counts.Length) / interval.SpanTicks))
        : null;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Add(int column, Mechanism mechanism)
    {
        if (counts[column] == int.MaxValue)
        {
            throw new InvalidOperationException(
                "One timeline bucket has more than 2,147,483,647 observed rows. The viewer count cannot "
                + "represent it without compaction, so it is refused rather than wrapped to a false value.");
        }

        counts[column]++;
        if (mechanisms is not null)
        {
            Dictionary<Mechanism, int> tally = mechanisms[column];
            if (tally.GetValueOrDefault(mechanism) == int.MaxValue)
            {
                throw new InvalidOperationException("One mechanism exceeds the timeline bucket's count bound.");
            }

            tally[mechanism] = tally.GetValueOrDefault(mechanism) + 1;
        }
    }

    /// <summary>
    /// The buckets, each with its dominant mechanism and the coverage of the mechanisms observed in it. A bucket with
    /// nothing observed has no mechanism to judge and stays unknown: an empty interval is not proof of inactivity.
    /// </summary>
    public TimelineBucket[] Buckets(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        if (mechanisms is null)
        {
            throw new InvalidOperationException("These columns were counted without their mechanisms.");
        }

        return [.. Enumerable.Range(0, counts.Length).Select(index =>
        {
            TimeRange range = IntervalOf(interval, counts.Length, index);
            return new TimelineBucket(
                range,
                counts[index],
                null,
                mechanisms[index].OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key)
                    .Select(entry => entry.Key).DefaultIfEmpty(Mechanism.UnknownMechanism).First(),
                BucketCoverage(coverage, clock, range, counts[index] == 0 ? [] : mechanisms[index].Keys));
        })];
    }

    /// <summary>
    /// The same counted rows split into mechanism lanes. No extra segment read occurs. Even an empty column in a
    /// mechanism lane asks the coverage ledger about that mechanism, not whichever mechanism dominated the whole column.
    /// </summary>
    public MechanismTimelineLane[] MechanismLanes(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        if (mechanisms is null) throw new InvalidOperationException("These columns were counted without mechanisms.");
        return [.. mechanisms.SelectMany(tally => tally.Keys).Distinct().Order().Select(mechanism =>
            new MechanismTimelineLane(mechanism, Array.AsReadOnly([.. Enumerable.Range(0, counts.Length).Select(index =>
            {
                TimeRange range = IntervalOf(interval, counts.Length, index);
                return new TimelineBucket(range, mechanisms[index].GetValueOrDefault(mechanism), null, mechanism,
                    BucketCoverage(coverage, clock, range, [mechanism]));
            })])))];
    }

    /// <summary>The capture's own coverage over each column, independent of what was observed in it.</summary>
    public CoverageState[] CaptureCoverage(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        if (coverage is null)
        {
            return [.. Enumerable.Repeat(CoverageState.UnknownCoverage, counts.Length)];
        }

        // Adjacent columns share a boundary, so each boundary is mapped to its native reading once.
        long?[] boundaries = [.. Enumerable.Range(0, counts.Length + 1).Select(index => FirstNativeAt(clock,
            index == counts.Length ? interval.EndTicks : IntervalOf(interval, counts.Length, index).StartTicks))];
        return [.. SessionCoverage.CaptureStates(coverage, [.. Enumerable.Range(0, counts.Length).Select(index =>
            boundaries[index] is { } first && boundaries[index + 1] is { } last && last > first
                ? new TimeRange(first, last)
                : (TimeRange?)null)])];
    }

    public static TimeRange IntervalOf(TimeRange interval, int columns, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, columns);
        return new(
            interval.StartTicks + (long)(((Int128)column * interval.SpanTicks) / columns),
            interval.StartTicks + (long)(((Int128)(column + 1) * interval.SpanTicks) / columns));
    }

    internal static CoverageState BucketCoverage(
        CoverageLedgerV1? ledger,
        SourceClockDescriptor clock,
        TimeRange presentationInterval,
        IEnumerable<Mechanism> mechanisms)
    {
        if (ledger is null)
        {
            return CoverageState.UnknownCoverage;
        }

        Mechanism[] scoped = [.. mechanisms];
        if (scoped.Length == 0 || NativeInterval(clock, presentationInterval) is not { } nativeInterval)
        {
            return CoverageState.UnknownCoverage;
        }

        return SessionCoverage.Worst(SessionCoverage.ForMechanisms(ledger, scoped, nativeInterval)
            .Select(result => result.State));
    }

    /// <summary>
    /// The native readings a presentation interval covers, or null when it maps to none. The presentation tick is
    /// nanoseconds / 100 with C# truncation toward zero; these are the exact nanosecond boundaries of that mapping,
    /// including its asymmetric zero tick (I3, I8). An interval that cannot be mapped is unknown, never inferred from
    /// neighboring bins.
    /// </summary>
    private static TimeRange? NativeInterval(SourceClockDescriptor clock, TimeRange presentationInterval) =>
        FirstNativeAt(clock, presentationInterval.StartTicks) is { } first
            && FirstNativeAt(clock, presentationInterval.EndTicks) is { } lastExclusive
            && lastExclusive > first
                ? new TimeRange(first, lastExclusive)
                : null;

    /// <summary>The first native reading at or after a presentation tick's lower bound, or null when it cannot be mapped.</summary>
    private static long? FirstNativeAt(SourceClockDescriptor clock, long presentationTick)
    {
        try
        {
            return SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(PresentationLowerNanoseconds(presentationTick)));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long PresentationLowerNanoseconds(long tick) => tick switch
    {
        < 0 => checked(tick * 100 - 99),
        0 => -99,
        _ => checked(tick * 100),
    };
}
