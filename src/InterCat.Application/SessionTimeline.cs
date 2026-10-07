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

    /// <summary>
    /// The whole columns, end to end: since revision 158 multiples of one width from the 1-2-5 ladder (§6.2) that cover
    /// the extent, the first level of §12.1's overview pyramid, so a column is the same interval in every generation of
    /// the session that keeps its width. By default the extent itself, divided equally.
    /// </summary>
    public TimeRange ColumnSpan { get; init; } = Extent;

    /// <summary>One column's interval, clipped to the extent: the first and last columns can begin or end outside it.</summary>
    public TimeRange IntervalOf(int column)
    {
        TimeRange whole = TimelineColumns.IntervalOf(ColumnSpan, Counts.Count, column);
        return new(Math.Max(whole.StartTicks, Extent.StartTicks), Math.Min(whole.EndTicks, Extent.EndTicks));
    }

    /// <summary>
    /// The minimap's columns for an extent: the narrowest width of the 1-2-5 ladder that covers it in at most
    /// <see cref="MaximumColumns"/> whole columns, aligned to multiples of that width.
    /// </summary>
    internal static (TimeRange Span, int Count) ColumnsFor(TimeRange extent)
    {
        long width = 1;
        for (long decade = 1; ; decade = checked(decade * 10))
        {
            width = decade;
            if (extent.SpanTicks <= (MaximumColumns - 2) * width) break;
            width = decade * 2;
            if (extent.SpanTicks <= (MaximumColumns - 2) * width) break;
            width = decade * 5;
            if (extent.SpanTicks <= (MaximumColumns - 2) * width) break;
        }

        long first = SegmentTimeTiles.FloorDivide(extent.StartTicks, width);
        long last = SegmentTimeTiles.FloorDivide(extent.EndTicks - 1, width);
        return (new TimeRange(first * width, (last + 1) * width), checked((int)(last - first + 1)));
    }
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

    /// <summary>The digest of the one generation's manifest the buckets were counted from (I16).</summary>
    public string? ManifestDigest { get; init; }
}

/// <summary>One group's exact owner-accounted L1 timeline row, keyed independently of its visual position.</summary>
public sealed record ProcessTimelineLane(ProcessInstanceId ProcessId, IReadOnlyList<TimelineBucket> Buckets);

/// <summary>
/// One L2 owner's source-reported direction, including unknown and not-applicable rather than silently folding
/// either into inbound or outbound. These rows partition the owner's admitted timed records.
/// </summary>
public sealed record DirectionTimelineLane(Direction Direction, IReadOnlyList<TimelineBucket> Buckets);

/// <summary>
/// One end of an L3 paired channel: the records made at that end's own endpoint (§3.2), whoever holds it, so a process
/// connected to itself still has two ends. The two ends partition the channel's records bucket by bucket. Outbound and
/// Inbound hold the end's records by source direction (`EN-Direction`) on the same columns; a record with any other
/// direction, such as a disconnect, is in <see cref="Buckets"/> only, and is never drawn as either. An end can only
/// hold the channel's mechanism, so every bucket's coverage is that mechanism's, even where the end observed nothing.
/// </summary>
/// <param name="End">0 for the relation's first end, the one whose endpoint sorts first; 1 for the other.</param>
/// <param name="Holder">The process instance that holds this end.</param>
/// <param name="Endpoint">The end's own endpoint, as its source names it.</param>
public sealed record ChannelEndTimelineLane(
    int End,
    ProcessInstanceId Holder,
    string Endpoint,
    IReadOnlyList<TimelineBucket> Buckets,
    IReadOnlyList<TimelineBucket> Outbound,
    IReadOnlyList<TimelineBucket> Inbound);

/// <summary>
/// The records a focused rung's timeline draws in colour: exactly the rows its evidence scope reads (§3.2) - one admitted
/// paired channel, the rows canonically owned by a set of process instances, or both at once. A rung's timeline therefore
/// counts what E lists for it, never a guessed superset.
/// </summary>
public sealed class TimelineFocus
{
    public TimelineFocus(string? channelKey, IReadOnlyCollection<ProcessInstanceId> ownerProcesses, string? operationKey = null)
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

        if (operationKey is not null && (channelKey is not null || ownerProcesses.Count > 0
            || (!RpcChannelKeys.IsRpc(operationKey) && !HttpExchangeKeys.IsHttp(operationKey))))
        {
            throw new ArgumentException("An operation focus names an RPC channel or call, or HTTP exchanges, alone.", nameof(operationKey));
        }

        if (channelKey is null && ownerProcesses.Count == 0 && operationKey is null)
        {
            throw new ArgumentException(
                "A timeline focus names a channel, owner processes or both, or an operation's records; the whole session is no focus.",
                nameof(ownerProcesses));
        }

        ChannelKey = channelKey;
        OperationKey = operationKey;
        OwnerProcesses = Array.AsReadOnly([.. ownerProcesses.Distinct().OrderBy(owner => owner.Value.ToString("N"), StringComparer.Ordinal)]);
        Key = $"channel:{channelKey?.Length ?? 0}:{channelKey}|owners:"
            + string.Join(",", OwnerProcesses.Select(owner => owner.Value.ToString("N")))
            + (operationKey is null ? string.Empty : $"|operation:{operationKey.Length}:{operationKey}");
    }

    public string? ChannelKey { get; }

    /// <summary>
    /// The RPC channel or call, or the HTTP exchanges, whose own records the focus counts; null for a channel or owner
    /// focus.
    /// </summary>
    public string? OperationKey { get; }

    /// <summary>The owner instances, distinct and in a stable order.</summary>
    public IReadOnlyList<ProcessInstanceId> OwnerProcesses { get; }

    /// <summary>What the focus reads, as one comparable string: two focuses with the same key count the same rows.</summary>
    public string Key { get; }

    /// <summary>
    /// The focus of an evidence scope: its channel - paired, or a one-sided connection - its owner processes, or its RPC
    /// or HTTP key's own records; null for the whole session and for a scope that cannot be read.
    /// </summary>
    public static TimelineFocus? Of(EvidenceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Problem is not null || scope.IsWholeSession ? null
            : scope.OperationKey is { } operation ? new(null, [], operation)
            : new(scope.ChannelKey, scope.OwnerProcesses);
    }
}

/// <summary>
/// A viewport's timeline and the part of it one focus reads, counted in one pass into the same columns, so each focus
/// bucket lies inside the whole-timeline bucket of the same interval and never exceeds it.
/// </summary>
public sealed record SessionFocusedTimeline(SessionTimelineDetail Whole, IReadOnlyList<TimelineBucket> Focus)
{
    /// <summary>
    /// Focus split by mechanism on the same columns, one lane per mechanism it holds, so a view drawn in mechanism lanes
    /// can mark each lane's share of the focus.
    /// </summary>
    public IReadOnlyList<MechanismTimelineLane> FocusLanes { get; init; } = [];

    /// <summary>
    /// When a group fits the lane bound, these owner rows partition Focus. They share Focus's columns while the lanes and
    /// columns fit the cell budget, and past it are counted in fewer, wider columns of the same interval (§6.2): exact
    /// counts at a coarser resolution, drawn on the same rate scale, rather than no lanes at all.
    /// </summary>
    public IReadOnlyList<ProcessTimelineLane> ProcessLanes { get; init; } = [];

    /// <summary>At a single-owner process focus, exact rows split by each observed source direction.</summary>
    public IReadOnlyList<DirectionTimelineLane> DirectionLanes { get; init; } = [];

    /// <summary>
    /// At a single-owner process focus, the process's own records on the same columns, each quiet interval judged by the
    /// capture's coverage, as its lane in its group's timeline and its direction rows are (R21): the rows the focus
    /// counts, stated as the process's lane.
    /// </summary>
    public IReadOnlyList<TimelineBucket> OwnerLane { get; init; } = [];

    /// <summary>At a channel focus, its two ends, first end first; together they partition Focus on the same columns.</summary>
    public IReadOnlyList<ChannelEndTimelineLane> ChannelEndLanes { get; init; } = [];

    /// <summary>Why L1 rows were not made; the aggregate focus count remains available and exact.</summary>
    public string? ProcessLaneProblem { get; init; }
}

public static class SessionTimelineQuery
{
    /// <summary>
    /// L2's source-direction rows, in drawing order: the two data directions first, then the rows that name none. Every
    /// `EN-Direction` code has a row, so the rows partition the owner's records and never fold unknown into a direction.
    /// </summary>
    public static IReadOnlyList<Direction> LaneDirections { get; } = Array.AsReadOnly(
        [Direction.Outbound, Direction.Inbound, Direction.Bidirectional,
            Direction.UnknownDirection, Direction.DirectionNotApplicable]);

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
        return Count(store, interval, columns, focus, policy, cancellationToken);
    }

    /// <summary>The whole timeline and, with a focus, its count and lanes; without one, Focus is empty.</summary>
    private static SessionFocusedTimeline Count(
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
        // A zoom reads only the segments its interval meets, by the readings their headers declare: the first zoom of a
        // reopened session opened every segment and built every segment's tiles, wherever it was (P25, S4). A focus resolves
        // its rows over the whole session, from the derivation or the checkpoint where one holds them, and is counted only
        // in the segments its interval meets.
        FocusRows? rows = focus is null ? null
            : FocusRows.Resolve(store, manifest, new GenerationSegments(store, manifest, clock), focus, policy, cancellationToken);
        SegmentReaderV1[] met = SessionNativeInterval.Segments(store, manifest, interval, clock);
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        bool groupFocus = focus is { ChannelKey: null, OwnerProcesses.Count: > 1 };
        string? laneProblem = !groupFocus ? null
            : focus!.OwnerProcesses.Count > MaximumProcessLanes
                ? $"This group needs {focus.OwnerProcesses.Count:N0} process lanes; the current bound is "
                    + $"{MaximumProcessLanes:N0} lanes. Its aggregate timeline remains exact. A bounded grouping or paging "
                    + "control is required to inspect these process rows; the query does not silently drop them."
                : null;
        ProcessInstanceId[] laneOwners = groupFocus && laneProblem is null ? [.. focus!.OwnerProcesses] : [];

        // The lanes share one cell budget: past it they are counted in fewer, wider columns of the same interval (§6.2),
        // exact at that resolution, rather than refused.
        int laneColumns = LaneColumnsFor(laneOwners.Length, columns, interval);

        // At most five direction codes and 2,000 columns: no extra segment pass or unbounded owner-by-peer matrix.
        bool directionLanes = focus is { ChannelKey: null, OwnerProcesses.Count: 1 };

        // A channel has exactly two ends, each with its total and two directional bands: six series at most.
        TransportRelation? endRelation = focus is { OwnerProcesses.Count: 0 } ? rows?.Relation : null;

        // The whole timeline comes from each segment's tiles (S4), focused or not: only a tile a column boundary crosses
        // has its rows read.
        foreach (SegmentReaderV1 segment in met)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentTimeTiles.Of(segment, cancellationToken).CountInto(segment, counted, cancellationToken);
        }

        // A focus reads its rows. Each segment is counted by one worker into columns of its own, and the workers' columns
        // are summed (SegmentPasses): at 10M records a group's lanes answer within §12's budget only side by side.
        FocusTally? total = null;
        if (rows is not null)
        {
            int[]? laneOf = laneOwners.Length == 0 ? null : rows.LanesOf(laneOwners);
            total = new FocusTally(interval, columns, laneOwners.Length, laneColumns, directionLanes, endRelation);
            SegmentPasses.Run(
                met,
                () => new FocusTally(interval, columns, laneOwners.Length, laneColumns, directionLanes, endRelation),
                (segment, tally) => CountFocus(segment, rows, laneOf, tally, cancellationToken),
                total.Add,
                cancellationToken);
        }

        CoverageLedgerV1? coverage = SessionSegments.CoverageLedger(store.Root, manifest);

        // Every timeline, lane and direction row here shares the columns, and so the capture's coverage over each: it is
        // judged once. Whatever rows a timeline counts could be any record the capture collects, so its quiet interval is
        // judged by the capture - observed-empty where it covered, a gap where it lost records - as a lane's is (R21).
        CoverageState[] capture = counted.CaptureCoverage(coverage, clock);
        var whole = new SessionTimelineDetail(manifest.SessionId, manifest.Generation, interval,
            Array.AsReadOnly(counted.Buckets(coverage, clock, capture)))
        {
            MechanismLanes = Array.AsReadOnly(counted.MechanismLanes(coverage, clock)),
            ManifestDigest = manifest.Digest,
        };
        CoverageState[]? laneCapture = total?.Lanes is not { Length: > 0 } counting ? null
            : counting[0].Counts.Count == counted.Counts.Count ? capture : counting[0].CaptureCoverage(coverage, clock);
        return new(whole, total is null ? [] : Array.AsReadOnly(total.Focused.Buckets(coverage, clock, capture)))
        {
            FocusLanes = total is null ? [] : Array.AsReadOnly(total.Focused.MechanismLanes(coverage, clock)),
            ProcessLanes = total?.Lanes is not { } lanes ? [] : Array.AsReadOnly([.. laneOwners.Select((owner, lane) =>
                new ProcessTimelineLane(owner, Array.AsReadOnly(lanes[lane].Buckets(laneCapture!))))]),
            DirectionLanes = total?.Directions is not { } directions ? [] : Array.AsReadOnly([.. LaneDirections.Select(
                (direction, slot) => new DirectionTimelineLane(direction, Array.AsReadOnly(directions[slot].Buckets(capture))))]),
            ChannelEndLanes = total?.Ends is not { } ends ? []
                : Array.AsReadOnly([.. ends.Select(end => end.Lane(coverage, clock))]),
            OwnerLane = total?.Directions is null ? [] : Array.AsReadOnly(total.Focused.Buckets(capture)),
            ProcessLaneProblem = laneProblem,
        };
    }

    /// <summary>
    /// The columns a group's lanes are counted in: the view's own while every lane's cells fit
    /// <see cref="MaximumProcessLaneCells"/>, else as many as fit, at least one.
    /// </summary>
    internal static int LaneColumnsFor(int lanes, int columns, TimeRange interval)
    {
        int drawn = (int)Math.Min(columns, interval.SpanTicks);
        return lanes == 0 || (long)lanes * drawn <= MaximumProcessLaneCells
            ? drawn
            : Math.Max(1, MaximumProcessLaneCells / lanes);
    }

    /// <summary>
    /// Counts one segment's focused records into a worker's columns: each into the focus, and into its owner's lane, its
    /// source direction's row or its channel end, as the focus has them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CountFocus(
        SegmentReaderV1 segment,
        FocusRows rows,
        int[]? laneOf,
        FocusTally tally,
        CancellationToken cancellationToken)
    {
        // A segment whose timed readings all lie outside the interval has nothing to count, and its tiles say so without
        // a row, or a binding, being read.
        TimeRange interval = tally.Focused.Interval;
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        if (tiles.First is not { } first || tiles.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return;
        }

        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice directions = tally.Directions is not null || tally.Ends is not null
            ? segment.Slice(SegmentColumnId.Direction)
            : default;
        using FocusRows.SegmentRows inFocus = rows.Of(segment);
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is not { } nanoseconds || tally.Focused.ColumnOf(nanoseconds / 100) is not { } column
                || !inFocus.Includes(row))
            {
                continue;
            }

            var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
            tally.Focused.Add(column, mechanism);
            if (laneOf is not null && inFocus.OwnerPosition(row) is { } position && laneOf[position] is >= 0 and int lane)
            {
                // Lanes past the cell budget have columns of their own over the same interval.
                TimelineColumns laneColumns = tally.Lanes![lane];
                laneColumns.Add(tally.LanesShareColumns ? column : laneColumns.ColumnOf(nanoseconds / 100)!.Value, mechanism);
            }

            if (tally.Directions is not null)
            {
                tally.Directions[DirectionSlot(DirectionAt(directions, row))].Add(column, mechanism);
            }

            if (tally.Ends is not null)
            {
                int end = inFocus.EndOf(row);
                if (end is not (0 or 1))
                {
                    throw new InvalidDataException("A record of the focused channel names no end of it.");
                }

                tally.Ends[end].Add(column, mechanism, DirectionAt(directions, row));
            }
        }
    }

    /// <summary>A direction's row among <see cref="LaneDirections"/>, which name every direction code.</summary>
    internal static int DirectionSlot(Direction direction)
    {
        for (int slot = 0; slot < LaneDirections.Count; slot++)
        {
            if (LaneDirections[slot] == direction)
            {
                return slot;
            }
        }

        throw new InvalidDataException($"The focused record has an unknown direction code: {(int)direction}.");
    }

    /// <summary>One worker's focused counts: the focus, and its process, direction or channel-end lanes.</summary>
    private sealed class FocusTally
    {
        public FocusTally(TimeRange interval, int columns, int lanes, int laneColumns, bool directions, TransportRelation? ends)
        {
            Focused = new(interval, columns, tallyMechanisms: true);
            Lanes = lanes == 0 ? null
                : [.. Enumerable.Range(0, lanes).Select(_ => new TimelineColumns(interval, laneColumns, tallyMechanisms: true))];
            LanesShareColumns = Lanes is null || Lanes[0].Counts.Count == Focused.Counts.Count;
            Directions = directions
                ? [.. LaneDirections.Select(_ => new TimelineColumns(interval, columns, tallyMechanisms: true))]
                : null;
            Ends = ends is null ? null :
            [
                new(0, ends.First.Id, ends.FirstEndpoint, ends.Mechanism, interval, columns),
                new(1, ends.Second.Id, ends.SecondEndpoint, ends.Mechanism, interval, columns),
            ];
        }

        public TimelineColumns Focused { get; }

        public TimelineColumns[]? Lanes { get; }

        /// <summary>Whether the lanes are counted in the focus's own columns, so a row's column is the focus's.</summary>
        public bool LanesShareColumns { get; }

        public TimelineColumns[]? Directions { get; }

        public ChannelEndColumns[]? Ends { get; }

        /// <summary>Sums another worker's counts into these.</summary>
        public void Add(FocusTally other)
        {
            Focused.MergeFrom(other.Focused);
            for (int lane = 0; lane < (Lanes?.Length ?? 0); lane++)
            {
                Lanes![lane].MergeFrom(other.Lanes![lane]);
            }

            for (int slot = 0; slot < (Directions?.Length ?? 0); slot++)
            {
                Directions![slot].MergeFrom(other.Directions![slot]);
            }

            for (int end = 0; end < (Ends?.Length ?? 0); end++)
            {
                Ends![end].MergeFrom(other.Ends![end]);
            }
        }
    }

    /// <summary>A focused record's `EN-Direction`; a code outside the enumeration is corrupt evidence, never a direction.</summary>
    internal static Direction DirectionAt(SegmentColumnSlice directions, int row)
    {
        var direction = (Direction)directions.UnsignedAt(row)!.Value;
        return Enum.IsDefined(direction)
            ? direction
            : throw new InvalidDataException($"The focused record has an unknown direction code: {(int)direction}.");
    }

    /// <summary>
    /// One channel end's columns: every record made there, and its outbound and inbound records apart. The channel's
    /// mechanism is known, so even an empty column is judged on that mechanism's coverage.
    /// </summary>
    private sealed class ChannelEndColumns(
        int end, ProcessInstanceId holder, string endpoint, Mechanism mechanism, TimeRange interval, int columns)
    {
        private readonly TimelineColumns total = new(interval, columns, tallyMechanisms: true);
        private readonly TimelineColumns outbound = new(interval, columns, tallyMechanisms: true);
        private readonly TimelineColumns inbound = new(interval, columns, tallyMechanisms: true);

        public void Add(int column, Mechanism mechanism, Direction direction)
        {
            total.Add(column, mechanism);
            if (direction == Direction.Outbound) outbound.Add(column, mechanism);
            else if (direction == Direction.Inbound) inbound.Add(column, mechanism);
        }

        /// <summary>Sums another worker's counts of the same end into these.</summary>
        public void MergeFrom(ChannelEndColumns other)
        {
            total.MergeFrom(other.total);
            outbound.MergeFrom(other.outbound);
            inbound.MergeFrom(other.inbound);
        }

        public ChannelEndTimelineLane Lane(CoverageLedgerV1? coverage, SourceClockDescriptor clock) => new(
            end, holder, endpoint,
            Array.AsReadOnly(total.Buckets(coverage, clock, mechanism)),
            Array.AsReadOnly(outbound.Buckets(coverage, clock, mechanism)),
            Array.AsReadOnly(inbound.Buckets(coverage, clock, mechanism)));
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

    /// <summary>Whether each instance position is a focused owner; null when the focus names no owner.</summary>
    private readonly bool[]? members;

    /// <summary>An operation focus's records by segment name and row; null for any other focus.</summary>
    private readonly HashSet<(string Segment, int Row)>? operationRecords;

    private FocusRows(ProcessInstanceIndex? processes, TransportRelationIndex? relations, HashSet<int> owners,
        TransportRelation? relation, int? channel, HashSet<(string Segment, int Row)>? operationRecords, EvidencePolicy policy)
    {
        this.processes = processes;
        this.relations = relations;
        this.owners = owners;
        Relation = relation;
        this.channel = channel;
        this.operationRecords = operationRecords;
        this.policy = policy;

        // Membership by position: a row's test is an index, never a hash, since it is asked of every row (R11).
        if (processes is not null && owners.Count > 0)
        {
            members = new bool[processes.Instances.Count];
            foreach (int owner in owners)
            {
                members[owner] = true;
            }
        }
    }

    /// <summary>For each instance position, the lane of <paramref name="lanes"/> its records are drawn in, or -1.</summary>
    public int[] LanesOf(IReadOnlyList<ProcessInstanceId> lanes)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        var laneOf = new Dictionary<ProcessInstanceId, int>(lanes.Count);
        for (int lane = 0; lane < lanes.Count; lane++)
        {
            laneOf[lanes[lane]] = lane;
        }

        int[] positions = new int[processes?.Instances.Count ?? 0];
        for (int position = 0; position < positions.Length; position++)
        {
            positions[position] = laneOf.TryGetValue(processes!.Instances[position].Id, out int lane) ? lane : -1;
        }

        return positions;
    }

    /// <summary>The one admitted paired incarnation a channel focus reads; null for an owner-only focus.</summary>
    public TransportRelation? Relation { get; }

    /// <summary>
    /// The rows a focus names, resolved over the whole generation: its instances and channels as the derivation or the
    /// checkpoint holds them, opening no segment when one does (P25), and an operation's records from every segment.
    /// </summary>
    public static FocusRows Resolve(
        SessionStore store,
        SessionManifestV1 manifest,
        GenerationSegments generation,
        TimelineFocus focus,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        ProcessInstanceIndex? processes = null;
        HashSet<int> owners = [];
        if (focus.OwnerProcesses.Count > 0)
        {
            processes = generation.Processes(cancellationToken);
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
        TransportRelation? relation = null;
        int? channel = null;
        if (focus.ChannelKey is { } key && TransportConnection.IsKey(key))
        {
            // A one-sided connection is one process's channel: its rows are the ones naming its channel, all at one end.
            relations = generation.Relations(cancellationToken);
            TransportConnection[] held = [.. relations.OneSided.Where(candidate =>
                candidate.StableKey == key && SessionOverviewProjector.Admitted(candidate.Strength, policy))];
            if (held.Length != 1)
            {
                throw new InvalidOperationException("The focused connection is not in this generation under the evidence policy.");
            }

            channel = held[0].Channel;
        }
        else if (focus.ChannelKey is { } pairedKey)
        {
            relations = generation.Relations(cancellationToken);
            TransportRelation[] matching = [.. relations.Relations.Where(candidate =>
                candidate.Mechanism == Mechanism.Tcp && candidate.StableKey == pairedKey
                && SessionOverviewProjector.Admitted(candidate.Strength, policy))];
            if (matching.Length != 1)
            {
                throw new InvalidOperationException(
                    "The focused paired TCP channel is not uniquely admitted in this generation and evidence policy.");
            }

            relation = matching[0];
            channel = relation.Channel;
        }

        // An RPC channel's or call's records, or HTTP exchanges', are the ones the generation's calls or exchanges group.
        HashSet<(string Segment, int Row)>? operationRecords = focus.OperationKey is not { } operation ? null
            : (HttpExchangeKeys.IsHttp(operation)
                ? SessionHttpExchanges.RecordsOf(store, manifest, generation.All, operation, policy, cancellationToken)
                : SessionRpcCalls.RecordsOf(store, manifest, generation.All, operation, policy, cancellationToken))
                ?? throw new InvalidOperationException("The focused operation is not in this generation under the evidence policy.");

        return new(processes, relations, owners, relation, channel, operationRecords, policy);
    }

    /// <summary>
    /// The row test for one segment: the derivation's bindings of its rows, kept with its reader, and for a channel the
    /// ends of its rows, rented for the segment's pass (R11). The caller disposes it when that pass ends.
    /// </summary>
    public SegmentRows Of(SegmentReaderV1 segment)
    {
        // The bindings are the derivation's, kept with the reader (SegmentBindings); only a channel's ends are rented for
        // the segment's pass.
        RentedRows<sbyte>? ends = null;
        if (Relation is not null)
        {
            ends = RentedRows<sbyte>.For(segment);
            TransportRelationIndex.EndsOf(segment, ends.Value.Span);
        }

        // An operation's records in this segment, by row; a segment holding none of them includes no row.
        bool[]? operationRows = null;
        if (operationRecords is not null)
        {
            operationRows = new bool[segment.RowCount];
            string name = segment.Published?.Name ?? string.Empty;
            foreach ((string recordSegment, int row) in operationRecords)
            {
                if (string.Equals(recordSegment, name, StringComparison.Ordinal) && row < operationRows.Length)
                {
                    operationRows[row] = true;
                }
            }
        }

        return new(
            this,
            channel is null ? null : SegmentBindings.ChannelsOf(segment, relations!),
            owners.Count > 0 ? SegmentBindings.OwnersOf(segment, processes!) : null,
            ends)
        {
            OperationRows = operationRows,
        };
    }

    internal sealed class SegmentRows : IDisposable
    {
        private readonly FocusRows scope;
        private readonly RentedRows<sbyte>? endRows;
        private readonly PackedChannels channels;
        private readonly bool hasChannels;
        private readonly PackedOwners bindings;
        private readonly bool hasOwners;
        private readonly sbyte[]? ends;
        private bool disposed;

        public SegmentRows(
            FocusRows scope,
            PackedChannels? channels,
            PackedOwners? bindings,
            RentedRows<sbyte>? endRows)
        {
            this.scope = scope;
            this.endRows = endRows;
            (this.channels, hasChannels) = channels is { } known ? (known, true) : (default, false);
            (this.bindings, hasOwners) = bindings is { } owned ? (owned, true) : (default, false);
            ends = endRows?.Buffer;
        }

        /// <summary>Gives the rented buffer back, once.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            endRows?.Dispose();
        }

        /// <summary>Which end of the focused channel an included row was made at: 0 its first end, 1 its second.</summary>
        public int EndOf(int row) => ends is null
            ? throw new InvalidOperationException("Only a channel focus has ends.")
            : ends[row];

        /// <summary>For an operation focus, whether each row of the segment is one of its records; null otherwise.</summary>
        public bool[]? OperationRows { get; init; }

        public bool Includes(int row)
        {
            if (OperationRows is { } operation && !operation[row])
            {
                return false;
            }

            if (hasChannels && channels[row].Channel != scope.channel)
            {
                return false;
            }

            return !hasOwners || OwnerPosition(row) is not null;
        }

        /// <summary>The position of an included row's canonical admitted owner, for an L1 group partition.</summary>
        public int? OwnerPosition(int row)
        {
            if (!hasOwners)
            {
                return null;
            }

            ProcessBinding binding = bindings[row];
            return binding.IsAdmittedUnder(scope.policy) && scope.members is { } members && members[binding.Instance]
                ? binding.Instance
                : null;
        }

        /// <summary>The canonical admitted owner of an included row, for an L1 group partition.</summary>
        public ProcessInstanceId? OwnerId(int row) => OwnerPosition(row) is { } position
            ? scope.processes!.Instances[position].Id
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
    /// <summary>Every defined mechanism, in code order: a column's tally holds one count per slot of this list.</summary>
    private static readonly Mechanism[] Slots = [.. Enum.GetValues<Mechanism>().Order()];

    /// <summary>A mechanism code's slot, or -1 for a code §23 does not define.</summary>
    private static readonly int[] SlotOfCode = SlotsByCode();

    private readonly TimeRange interval;
    private readonly int[] counts;

    /// <summary>
    /// One count per column and mechanism slot, flat: the tally a row adds to is an index, never a dictionary, so counting
    /// a row allocates and hashes nothing (R11).
    /// </summary>
    private readonly int[]? mechanisms;

    public TimelineColumns(TimeRange interval, int columns, bool tallyMechanisms)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        this.interval = interval;
        counts = new int[(int)Math.Min(columns, interval.SpanTicks)];
        if (tallyMechanisms)
        {
            mechanisms = new int[counts.Length * Slots.Length];
        }
    }

    public IReadOnlyList<int> Counts => counts;

    /// <summary>The interval the columns divide.</summary>
    public TimeRange Interval => interval;

    /// <summary>Whether each column's count is also kept per mechanism.</summary>
    internal bool TalliesMechanisms => mechanisms is not null;

    /// <summary>
    /// Every nonzero count of one mechanism in one column, by column and then mechanism code: what a persisted overview
    /// holds (`contracts/overview-index-v1.md`). Columns counted without their mechanisms have none.
    /// </summary>
    internal IEnumerable<(int Column, Mechanism Mechanism, int Count)> Tallies()
    {
        if (mechanisms is null)
        {
            yield break;
        }

        for (int column = 0; column < counts.Length; column++)
        {
            for (int slot = 0; slot < Slots.Length; slot++)
            {
                if (mechanisms[(column * Slots.Length) + slot] is > 0 and int count)
                {
                    yield return (column, Slots[slot], count);
                }
            }
        }
    }

    /// <summary>How many records of one mechanism a column counts; columns counted without their mechanisms count none.</summary>
    internal int CountOf(int column, Mechanism mechanism) =>
        mechanisms is null ? 0 : mechanisms[(column * Slots.Length) + SlotOf(mechanism)];

    /// <summary>One more than the largest mechanism code §23 defines: a table indexed by code has this many slots.</summary>
    internal static int MechanismCodes => SlotOfCode.Length;

    /// <summary>A defined mechanism's code; an undefined one is refused, as counting a record of it would be.</summary>
    internal static int CodeOf(Mechanism mechanism)
    {
        _ = SlotOf(mechanism);
        return (int)mechanism;
    }

    /// <summary>The column holding a presentation tick, or null outside the interval.</summary>
    /// <remarks>
    /// <para>
    /// The column is the one whose interval (<see cref="IntervalOf(TimeRange, int, int)"/>, §10.3's
    /// <c>b(i) = t0 + floor(span * i / W)</c>) holds the tick: the largest <c>i</c> with <c>b(i) &lt;= t</c>, which is
    /// <c>floor(((t - t0 + 1) * W - 1) / span)</c>. Until revision 158 it was <c>floor((t - t0) * W / span)</c>, which
    /// agrees only where <c>W</c> divides the span: a record exactly at any other boundary was counted in the column
    /// before the one whose stated interval holds it.
    /// </para>
    /// <para>
    /// Called once per row. It is compiled optimized at once rather than waiting to be promoted from the first tier,
    /// which a projection called every couple of seconds would spend its first seconds waiting for.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int? ColumnOf(long tick) => interval.Contains(tick)
        ? (int)(((((Int128)(tick - interval.StartTicks) + 1) * counts.Length) - 1) / interval.SpanTicks)
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
            ref int tally = ref mechanisms[(column * Slots.Length) + SlotOf(mechanism)];
            if (tally == int.MaxValue)
            {
                throw new InvalidOperationException("One mechanism exceeds the timeline bucket's count bound.");
            }

            tally++;
        }
    }

    /// <summary>
    /// Adds every count of <paramref name="other"/>, columns of the same interval counted elsewhere, as a worker's share of
    /// a count is summed into the whole. The bounds are the same as a row's.
    /// </summary>
    public void MergeFrom(TimelineColumns other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.interval != interval || other.counts.Length != counts.Length
            || (other.mechanisms is null) != (mechanisms is null))
        {
            throw new ArgumentException("Only columns of the same interval, width and tallies merge.", nameof(other));
        }

        for (int column = 0; column < counts.Length; column++)
        {
            if (counts[column] > int.MaxValue - other.counts[column])
            {
                throw new InvalidOperationException(
                    "One timeline bucket has more than 2,147,483,647 observed rows. The viewer count cannot "
                    + "represent it without compaction, so it is refused rather than wrapped to a false value.");
            }

            counts[column] += other.counts[column];
        }

        if (mechanisms is not null)
        {
            for (int slot = 0; slot < mechanisms.Length; slot++)
            {
                if (mechanisms[slot] > int.MaxValue - other.mechanisms![slot])
                {
                    throw new InvalidOperationException("One mechanism exceeds the timeline bucket's count bound.");
                }

                mechanisms[slot] += other.mechanisms[slot];
            }
        }
    }

    /// <summary>How many mechanism slots a per-mechanism table needs: one per defined mechanism.</summary>
    internal static int SlotCount => Slots.Length;

    /// <summary>The mechanism of a slot of <see cref="SlotOfMechanism"/>.</summary>
    internal static Mechanism MechanismAt(int slot) => Slots[slot];

    /// <summary>A defined mechanism's slot, dense from 0; an undefined one is refused.</summary>
    internal static int SlotOfMechanism(Mechanism mechanism) => SlotOf(mechanism);

    /// <summary>Adds <paramref name="count"/> records of one mechanism to a column at once, as a tile holds them.</summary>
    public void Add(int column, Mechanism mechanism, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (counts[column] > int.MaxValue - count)
        {
            throw new InvalidOperationException(
                "One timeline bucket has more than 2,147,483,647 observed rows. The viewer count cannot "
                + "represent it without compaction, so it is refused rather than wrapped to a false value.");
        }

        counts[column] += count;
        if (mechanisms is not null)
        {
            ref int tally = ref mechanisms[(column * Slots.Length) + SlotOf(mechanism)];
            if (tally > int.MaxValue - count)
            {
                throw new InvalidOperationException("One mechanism exceeds the timeline bucket's count bound.");
            }

            tally += count;
        }
    }

    /// <summary>
    /// The buckets, each with its dominant mechanism and the coverage of the mechanisms observed in it; a bucket with
    /// nothing observed takes the capture's own coverage over its interval (<paramref name="capture"/>, from
    /// <see cref="CaptureCoverage"/> over the same columns). Whatever these rows are - the machine's, or a focus's - they
    /// could hold any mechanism the capture collects, so a quiet interval the capture covered reads as observed-empty, one
    /// where it lost records as a gap, and one past the readings it delivered as unknown (R21), as a lane's does.
    /// </summary>
    public TimelineBucket[] Buckets(CoverageLedgerV1? coverage, SourceClockDescriptor clock, IReadOnlyList<CoverageState> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Count != counts.Length)
        {
            throw new ArgumentException("The capture's coverage was judged over other columns.", nameof(capture));
        }

        TimelineBucket[] buckets = Buckets(coverage, clock);
        for (int index = 0; index < buckets.Length; index++)
        {
            if (buckets[index].ObservationCount == 0)
            {
                buckets[index] = buckets[index] with { Coverage = capture[index] };
            }
        }

        return buckets;
    }

    /// <summary>
    /// The buckets, each with its dominant mechanism and the coverage of the mechanisms observed in it. A bucket with
    /// nothing observed has no mechanism of its own to judge and stays unknown here; a timeline that knows the capture's
    /// coverage over its columns judges it by that instead.
    /// </summary>
    public TimelineBucket[] Buckets(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        int[] tallies = mechanisms ?? throw new InvalidOperationException("These columns were counted without their mechanisms.");
        var buckets = new TimelineBucket[counts.Length];
        for (int index = 0; index < counts.Length; index++)
        {
            TimeRange range = IntervalOf(interval, counts.Length, index);
            buckets[index] = new TimelineBucket(
                range,
                counts[index],
                null,
                Dominant(tallies, index, Mechanism.UnknownMechanism),
                coverage is null ? CoverageState.UnknownCoverage
                    : BucketCoverage(coverage, clock, range, counts[index] == 0 ? [] : Observed(tallies, index, null)));
        }

        return buckets;
    }

    /// <summary>
    /// The buckets of a lane whose records are all of one known <paramref name="mechanism"/>, such as one channel's ends.
    /// Its coverage is judged on that mechanism even in an empty bucket, as a mechanism lane's is, so a quiet interval the
    /// capture covered reads as observed-empty rather than unknown, while a gap in that mechanism is still a gap.
    /// </summary>
    public TimelineBucket[] Buckets(CoverageLedgerV1? coverage, SourceClockDescriptor clock, Mechanism mechanism)
    {
        int[] tallies = mechanisms ?? throw new InvalidOperationException("These columns were counted without their mechanisms.");
        var buckets = new TimelineBucket[counts.Length];
        for (int index = 0; index < counts.Length; index++)
        {
            TimeRange range = IntervalOf(interval, counts.Length, index);
            buckets[index] = new TimelineBucket(range, counts[index], null, Dominant(tallies, index, mechanism),
                coverage is null ? CoverageState.UnknownCoverage
                    : BucketCoverage(coverage, clock, range, Observed(tallies, index, mechanism)));
        }

        return buckets;
    }

    /// <summary>
    /// The buckets of one process's lane, each judged by what the capture collected over its interval
    /// (<paramref name="capture"/>, from <see cref="CaptureCoverage"/> over the same columns) rather than by what the
    /// process did in it. A process can produce any mechanism the capture collects, so a quiet interval the capture
    /// covered reads as observed-empty, as a mechanism lane's does, and a gap in any collected mechanism is still a gap.
    /// Judged only by its own records, every quiet interval of every process was unknown (revision 165).
    /// </summary>
    public TimelineBucket[] Buckets(IReadOnlyList<CoverageState> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Count != counts.Length)
        {
            throw new ArgumentException("The capture's coverage was judged over other columns.", nameof(capture));
        }

        int[] tallies = mechanisms ?? throw new InvalidOperationException("These columns were counted without their mechanisms.");
        var buckets = new TimelineBucket[counts.Length];
        for (int index = 0; index < counts.Length; index++)
        {
            buckets[index] = new TimelineBucket(IntervalOf(interval, counts.Length, index), counts[index], null,
                Dominant(tallies, index, Mechanism.UnknownMechanism), capture[index]);
        }

        return buckets;
    }

    /// <summary>
    /// The same counted rows split into mechanism lanes. No extra segment read occurs. Even an empty column in a
    /// mechanism lane asks the coverage ledger about that mechanism, not whichever mechanism dominated the whole column.
    /// </summary>
    public MechanismTimelineLane[] MechanismLanes(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        int[] tallies = mechanisms ?? throw new InvalidOperationException("These columns were counted without mechanisms.");
        var lanes = new List<MechanismTimelineLane>();
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            bool observed = false;
            for (int index = 0; index < counts.Length && !observed; index++)
            {
                observed = tallies[(index * Slots.Length) + slot] > 0;
            }

            if (!observed) continue;
            Mechanism mechanism = Slots[slot];
            var buckets = new TimelineBucket[counts.Length];
            for (int index = 0; index < counts.Length; index++)
            {
                TimeRange range = IntervalOf(interval, counts.Length, index);
                buckets[index] = new TimelineBucket(range, tallies[(index * Slots.Length) + slot], null, mechanism,
                    BucketCoverage(coverage, clock, range, [mechanism]));
            }

            lanes.Add(new MechanismTimelineLane(mechanism, Array.AsReadOnly(buckets)));
        }

        return [.. lanes];
    }

    /// <summary>
    /// The mechanism with the most rows in a column; among equals the lowest code, so the choice never depends on the
    /// order rows arrived in. A column with none has <paramref name="fallback"/>.
    /// </summary>
    private static Mechanism Dominant(int[] tallies, int column, Mechanism fallback)
    {
        int best = -1;
        int most = 0;
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            int count = tallies[(column * Slots.Length) + slot];
            if (count > most)
            {
                most = count;
                best = slot;
            }
        }

        return best < 0 ? fallback : Slots[best];
    }

    /// <summary>The mechanisms a column holds rows of, in code order, with <paramref name="also"/> when one is given.</summary>
    private static Mechanism[] Observed(int[] tallies, int column, Mechanism? also)
    {
        var observed = new List<Mechanism>(2);
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            if (tallies[(column * Slots.Length) + slot] > 0 || Slots[slot] == also)
            {
                observed.Add(Slots[slot]);
            }
        }

        return [.. observed];
    }

    private static int SlotOf(Mechanism mechanism)
    {
        int code = (int)mechanism;
        int slot = (uint)code < (uint)SlotOfCode.Length ? SlotOfCode[code] : -1;
        return slot >= 0
            ? slot
            : throw new InvalidDataException(
                $"A record carries mechanism code {code}, which §23 does not define. It is refused rather than counted.");
    }

    private static int[] SlotsByCode()
    {
        int[] slots = new int[(int)Slots.Max() + 1];
        Array.Fill(slots, -1);
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            slots[(int)Slots[slot]] = slot;
        }

        return slots;
    }

    /// <summary>
    /// The capture's own coverage over each column, independent of what was observed in it, over the part of each
    /// column inside <paramref name="within"/> when one is given: a column that begins before the extent is not judged
    /// on readings from before it.
    /// </summary>
    public CoverageState[] CaptureCoverage(CoverageLedgerV1? coverage, SourceClockDescriptor clock, TimeRange? within = null)
    {
        if (coverage is null)
        {
            return [.. Enumerable.Repeat(CoverageState.UnknownCoverage, counts.Length)];
        }

        // Adjacent columns share a boundary, so each boundary is mapped to its native reading once; a column whose
        // readings are further apart than a tick count holds is judged on the part centred on the epoch, as a bucket is.
        long?[] boundaries = [.. Enumerable.Range(0, counts.Length + 1).Select(index => FirstNativeAt(clock, Within(
            index == counts.Length ? interval.EndTicks : IntervalOf(interval, counts.Length, index).StartTicks)))];
        return [.. SessionCoverage.CaptureStates(coverage, [.. Enumerable.Range(0, counts.Length).Select(index =>
            boundaries[index] is { } first && boundaries[index + 1] is { } last
                ? TimeRange.Around(first, last, clock.CaptureEpochNativeTicks)
                : null)])];

        long Within(long tick) => within is { } bounds ? Math.Clamp(tick, bounds.StartTicks, bounds.EndTicks) : tick;
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
    /// neighboring bins; one whose readings are further apart than a tick count holds is held centred on the epoch.
    /// </summary>
    private static TimeRange? NativeInterval(SourceClockDescriptor clock, TimeRange presentationInterval) =>
        FirstNativeAt(clock, presentationInterval.StartTicks) is { } first
            && FirstNativeAt(clock, presentationInterval.EndTicks) is { } lastExclusive
                ? TimeRange.Around(first, lastExclusive, clock.CaptureEpochNativeTicks)
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
