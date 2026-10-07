using System.Runtime.CompilerServices;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Which records an interval's bytes are measured over: the records the interval table counts where it lists them
/// (§6.2's table equivalent, R15). Every record with a session time; one mechanism's lane; one process instance's own
/// records, or only those of one source direction, as a process lane or an L2 direction row lists them; or one admitted
/// paired channel's records, or only those made at one of its ends, as an L3 end lane lists them.
/// </summary>
public sealed record IntervalByteScope
{
    /// <summary>Every record with a session time, whatever its mechanism or owner.</summary>
    public static IntervalByteScope Whole { get; } = new();

    /// <summary>Only the records of this mechanism, as its mechanism lane counts them.</summary>
    public Mechanism? Mechanism { get; init; }

    /// <summary>Only the records canonically owned by this process instance and admitted under the policy.</summary>
    public ProcessInstanceId? Owner { get; init; }

    /// <summary>With <see cref="Owner"/>, only its records of this source direction (`EN-Direction`).</summary>
    public Direction? Direction { get; init; }

    /// <summary>Only the records of this admitted paired TCP channel, at either end.</summary>
    public string? ChannelKey { get; init; }

    /// <summary>With <see cref="ChannelKey"/>, only the records made at this end: 0 the first, 1 the second.</summary>
    public int? End { get; init; }

    /// <summary>What the scope says in words, for a heading or a caption: a mechanism and a direction as the window names them.</summary>
    public string Description =>
        Owner is { } owner
            ? $"records owned by process instance {owner}"
                + (Direction is { } direction ? $" with source direction {ObservationText.DirectionOf(direction)}" : string.Empty)
            : ChannelKey is { } key
            ? (End is { } end ? $"records made at end {end} of channel {key}" : $"records of channel {key}")
            : Mechanism is { } mechanism
            ? $"{EvidenceRowText.MechanismName(mechanism)} records"
            : "every record";

    /// <summary>Why this scope names no set of records a table lists, or null when it does.</summary>
    public string? Validate() =>
        (Mechanism is not null ? 1 : 0) + (Owner is not null ? 1 : 0) + (ChannelKey is not null ? 1 : 0) > 1
            ? "An interval's bytes are measured over one mechanism, one process instance or one channel, not a combination."
        : Mechanism is { } mechanism && !Enum.IsDefined(mechanism) ? "A mechanism scope names a mechanism §23 defines."
        : Owner is { Value: var id } && id == Guid.Empty ? "A process scope needs a non-empty instance ID."
        : Direction is { } direction && (Owner is null || !Enum.IsDefined(direction))
            ? "A source direction scopes one process instance's records and names a direction §23 defines."
        : ChannelKey is { } key && string.IsNullOrWhiteSpace(key) ? "A channel scope needs a channel key."
        : End is { } end && (ChannelKey is null || end is not (0 or 1)) ? "A channel end is 0 or 1 of a named channel."
        : null;
}

/// <summary>
/// One scope's transport bytes in each column of one interval, from one leased generation. The columns are the timeline's
/// own (§10.3's <c>b(i) = t0 + floor(span·i/W)</c>), and a record is placed by its session time as the timeline places it,
/// so each column is the interval, and holds the records, of the table row it answers.
/// </summary>
public sealed record SessionIntervalByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IntervalByteScope Scope,
    IReadOnlyList<TransportBytes> Columns)
{
    /// <summary>A column's interval, as the timeline's bucket of the same index states it.</summary>
    public TimeRange IntervalOf(int column) => TimelineColumns.IntervalOf(Interval, Columns.Count, column);

    /// <summary>The bytes of the column whose interval is exactly <paramref name="interval"/>, or null when none is.</summary>
    public TransportBytes? For(TimeRange interval)
    {
        if (Columns.Count == 0 || interval.StartTicks < Interval.StartTicks || interval.EndTicks > Interval.EndTicks)
        {
            return null;
        }

        // The column holding the interval's first tick is the only one it can be.
        int column = (int)(((((Int128)(interval.StartTicks - Interval.StartTicks) + 1) * Columns.Count) - 1) / Interval.SpanTicks);
        return IntervalOf(column) == interval ? Columns[column] : null;
    }
}

/// <summary>
/// Several mechanisms' transport bytes in each column of one interval, from one leased generation: for each mechanism,
/// exactly what <see cref="SessionIntervalByteQuery.Measure"/> answers over its lane's records, all measured in one pass
/// (R18). The timeline's mechanism lanes plot them when the ranking is by bytes (§6.2).
/// </summary>
public sealed record SessionMechanismByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyList<SessionIntervalByteMeasures> Lanes)
{
    /// <summary>
    /// Whether these are the bytes a persisted overview kept in its own columns (overview-index-v1 minor 2), answered
    /// without opening a segment, rather than read from the segments.
    /// </summary>
    public bool FromPersistedOverview { get; init; }

    /// <summary>The columns of <paramref name="mechanism"/>'s lane, or null when it was not measured.</summary>
    public SessionIntervalByteMeasures? Of(Mechanism mechanism)
    {
        for (int index = 0; index < Lanes.Count; index++)
        {
            if (Lanes[index].Scope.Mechanism == mechanism)
            {
                return Lanes[index];
            }
        }

        return null;
    }
}

/// <summary>
/// A group's lanes' transport bytes over one interval, from one leased generation: every record's bytes in the machine
/// row's columns, and each process instance's own in the lanes' columns, which past the lanes' cell budget are fewer and
/// wider over the same interval (§6.2). Each is exactly what <see cref="SessionIntervalByteQuery.Measure"/> answers for
/// its scope, all measured in one pass (R18). A group's process lanes plot them when the ranking is by bytes.
/// </summary>
public sealed record SessionOwnerByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    SessionIntervalByteMeasures Machine,
    IReadOnlyList<SessionIntervalByteMeasures> Lanes)
{
    /// <summary>The columns of <paramref name="owner"/>'s lane, or null when it was not measured.</summary>
    public SessionIntervalByteMeasures? Of(ProcessInstanceId owner)
    {
        for (int index = 0; index < Lanes.Count; index++)
        {
            if (Lanes[index].Scope.Owner == owner)
            {
                return Lanes[index];
            }
        }

        return null;
    }
}

/// <summary>
/// A process instance's direction rows' transport bytes over one interval, from one leased generation: every record's bytes
/// in the machine row's columns, and the instance's own records' by source direction, one row per direction of
/// <see cref="SessionTimelineQuery.LaneDirections"/>, in the same columns. Each is exactly what
/// <see cref="SessionIntervalByteQuery.Measure"/> answers for its scope, all measured in one pass (R18). A process's
/// direction rows plot them when the ranking is by bytes.
/// </summary>
public sealed record SessionDirectionByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    ProcessInstanceId Owner,
    SessionIntervalByteMeasures Machine,
    IReadOnlyList<SessionIntervalByteMeasures> Lanes)
{
    /// <summary>The columns of the <paramref name="direction"/> row, or null when it was not measured.</summary>
    public SessionIntervalByteMeasures? Of(Direction direction)
    {
        for (int index = 0; index < Lanes.Count; index++)
        {
            if (Lanes[index].Scope.Direction == direction)
            {
                return Lanes[index];
            }
        }

        return null;
    }
}

/// <summary>
/// Measures the transport bytes of one scope's records in each column of an interval: what `icat metric --metric
/// bytes-sent --byte-domain transport-observed --side send` and its received counterpart answer for each column's own
/// interval, in one pass (R18). A record declaring a size it did not record is unmeasured and counted apart, never summed
/// as zero (R3); a record with no usable session time has no column, as it has no place on the timeline.
/// </summary>
public static class SessionIntervalByteQuery
{
    /// <summary>
    /// Measures a process's direction rows in one pass: every record in each column of the interval, for the machine row,
    /// and <paramref name="owner"/>'s own records, under the evidence rung's rules and the policy, in each column by the
    /// direction their source states. For each, what <see cref="Measure"/> answers for its scope. An owner this generation
    /// does not hold is refused with the evidence rung's reason, never measured as nothing.
    /// </summary>
    public static SessionDirectionByteMeasures MeasureByDirection(
        SessionStore store,
        TimeRange interval,
        int columns,
        ProcessInstanceId owner,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, SessionTimelineQuery.MaximumColumns);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (owner.Value == Guid.Empty) throw new ArgumentException("A process's rows need a non-empty instance ID.", nameof(owner));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its intervals cannot be placed.");
        // Its rows are resolved over the whole generation, from the derivation or the checkpoint where one holds them, and
        // measured only in the segments the interval meets (P25).
        FocusRows rows = FocusRows.Resolve(store, manifest, new GenerationSegments(store, manifest, clock),
            new TimelineFocus(null, [owner]), policy, cancellationToken);
        SegmentReaderV1[] segments = SessionNativeInterval.Segments(store, manifest, interval, clock);
        var layout = new TimelineColumns(interval, columns, tallyMechanisms: false);
        int count = layout.Counts.Count;
        int directions = SessionTimelineQuery.LaneDirections.Count;

        // The machine row's columns, and one slot per direction and column, direction by direction, each with one more
        // for every row outside them.
        var total = new LaneByteTally(count + 1, (directions * count) + 1);
        SegmentPasses.Run(
            segments,
            () => new LaneByteTally(count + 1, (directions * count) + 1),
            (segment, tally) => MeasureDirections(segment, layout, rows, tally, cancellationToken),
            total.Add,
            cancellationToken);
        return new(manifest.SessionId, manifest.Generation, interval, owner,
            new SessionIntervalByteMeasures(manifest.SessionId, manifest.Generation, interval, IntervalByteScope.Whole,
                Array.AsReadOnly([.. Enumerable.Range(0, count).Select(total.Machine.Of)])),
            Array.AsReadOnly([.. SessionTimelineQuery.LaneDirections.Select((direction, slot) =>
                new SessionIntervalByteMeasures(manifest.SessionId, manifest.Generation, interval,
                    new IntervalByteScope { Owner = owner, Direction = direction },
                    Array.AsReadOnly([.. Enumerable.Range(0, count).Select(column => total.Lanes.Of((slot * count) + column))])))]));
    }

    /// <summary>
    /// Places each of one segment's rows in the machine row's column, and the owner's in its direction's row of the same
    /// column, as a process's direction rows count them, and measures both.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MeasureDirections(
        SegmentReaderV1 segment,
        TimelineColumns layout,
        FocusRows rows,
        LaneByteTally tally,
        CancellationToken cancellationToken)
    {
        TimeRange interval = layout.Interval;
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        if (tiles.First is not { } first || tiles.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return;
        }

        int count = layout.Counts.Count;
        int laneOutside = SessionTimelineQuery.LaneDirections.Count * count;
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice directions = segment.Slice(SegmentColumnId.Direction);
        using FocusRows.SegmentRows inFocus = rows.Of(segment);
        using RentedRows<int> machineGroups = RentedRows<int>.For(segment);
        using RentedRows<int> laneGroups = RentedRows<int>.For(segment);
        Span<int> columnOf = machineGroups.Span;
        Span<int> slotOf = laneGroups.Span;
        for (int row = 0; row < columnOf.Length; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is not { } nanoseconds || layout.ColumnOf(nanoseconds / 100) is not { } column)
            {
                columnOf[row] = count;
                slotOf[row] = laneOutside;
                continue;
            }

            columnOf[row] = column;
            slotOf[row] = inFocus.Includes(row)
                ? (SessionTimelineQuery.DirectionSlot(SessionTimelineQuery.DirectionAt(directions, row)) * count) + column
                : laneOutside;
        }

        var spec = new DomainMeasurementSpec { Domain = ByteDomain.TransportObserved };
        tally.Machine.Add(SegmentMeasurement.MeasureDomainByGroup(segment, spec, columnOf, count + 1));
        tally.Lanes.Add(SegmentMeasurement.MeasureDomainByGroup(segment, spec, slotOf, laneOutside + 1));
    }

    /// <summary>
    /// Measures a group's lanes in one pass: every record in <paramref name="columns"/> columns of the interval, for the
    /// machine row, and each of <paramref name="owners"/>' own records, under the evidence rung's rules and the policy, in
    /// <paramref name="laneColumns"/> columns of the same interval. For each, what <see cref="Measure"/> answers for its
    /// scope. An owner this generation does not hold is refused with the evidence rung's reason, never measured as nothing.
    /// </summary>
    public static SessionOwnerByteMeasures MeasureByOwner(
        SessionStore store,
        TimeRange interval,
        int columns,
        int laneColumns,
        IReadOnlyList<ProcessInstanceId> owners,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, SessionTimelineQuery.MaximumColumns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(laneColumns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(laneColumns, SessionTimelineQuery.MaximumColumns);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (owners.Count == 0 || owners.Count > SessionTimelineQuery.MaximumProcessLanes
            || owners.Any(owner => owner.Value == Guid.Empty) || owners.Distinct().Count() != owners.Count)
        {
            throw new ArgumentException(
                $"Name one to {SessionTimelineQuery.MaximumProcessLanes:N0} process instances, each once.", nameof(owners));
        }

        if ((long)owners.Count * Math.Min(laneColumns, interval.SpanTicks) > SessionTimelineQuery.MaximumProcessLaneCells)
        {
            throw new ArgumentException(
                $"The lanes need more than {SessionTimelineQuery.MaximumProcessLaneCells:N0} cells; count them in fewer columns.",
                nameof(laneColumns));
        }

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its intervals cannot be placed.");
        FocusRows rows = FocusRows.Resolve(store, manifest, new GenerationSegments(store, manifest, clock),
            new TimelineFocus(null, owners), policy, cancellationToken);
        SegmentReaderV1[] segments = SessionNativeInterval.Segments(store, manifest, interval, clock);
        int[] laneOf = rows.LanesOf(owners);
        var machine = new TimelineColumns(interval, columns, tallyMechanisms: false);
        var lanes = new TimelineColumns(interval, laneColumns, tallyMechanisms: false);
        int machineCount = machine.Counts.Count;
        int laneCount = lanes.Counts.Count;

        // The machine row's columns and one slot per lane and column, lane by lane, each with one more for every row
        // outside them.
        var total = new LaneByteTally(machineCount + 1, (owners.Count * laneCount) + 1);
        SegmentPasses.Run(
            segments,
            () => new LaneByteTally(machineCount + 1, (owners.Count * laneCount) + 1),
            (segment, tally) => MeasureOwners(segment, machine, lanes, rows, laneOf, owners.Count, tally, cancellationToken),
            total.Add,
            cancellationToken);
        return new(manifest.SessionId, manifest.Generation, interval,
            new SessionIntervalByteMeasures(manifest.SessionId, manifest.Generation, interval, IntervalByteScope.Whole,
                Array.AsReadOnly([.. Enumerable.Range(0, machineCount).Select(total.Machine.Of)])),
            Array.AsReadOnly([.. owners.Select((owner, lane) =>
                new SessionIntervalByteMeasures(manifest.SessionId, manifest.Generation, interval,
                    new IntervalByteScope { Owner = owner },
                    Array.AsReadOnly([.. Enumerable.Range(0, laneCount).Select(column => total.Lanes.Of((lane * laneCount) + column))])))]));
    }

    /// <summary>One worker's bytes: the machine row's columns, and every lane's.</summary>
    private sealed class LaneByteTally(int machineSlots, int laneSlots)
    {
        public TransportByteTally Machine { get; } = new(machineSlots);

        public TransportByteTally Lanes { get; } = new(laneSlots);

        public void Add(LaneByteTally other)
        {
            Machine.Add(other.Machine);
            Lanes.Add(other.Lanes);
        }
    }

    /// <summary>
    /// Places each of one segment's rows in the machine row's column, and a lane's row in its lane's column, as a group's
    /// lanes count them, and measures both.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MeasureOwners(
        SegmentReaderV1 segment,
        TimelineColumns machine,
        TimelineColumns lanes,
        FocusRows rows,
        int[] laneOf,
        int laneTotal,
        LaneByteTally tally,
        CancellationToken cancellationToken)
    {
        TimeRange interval = machine.Interval;
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        if (tiles.First is not { } first || tiles.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return;
        }

        int machineOutside = machine.Counts.Count;
        int laneCount = lanes.Counts.Count;
        int laneOutside = laneTotal * laneCount;
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        using FocusRows.SegmentRows inFocus = rows.Of(segment);
        using RentedRows<int> machineGroups = RentedRows<int>.For(segment);
        using RentedRows<int> laneGroups = RentedRows<int>.For(segment);
        Span<int> columnOf = machineGroups.Span;
        Span<int> slotOf = laneGroups.Span;
        for (int row = 0; row < columnOf.Length; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is not { } nanoseconds || machine.ColumnOf(nanoseconds / 100) is not { } column)
            {
                columnOf[row] = machineOutside;
                slotOf[row] = laneOutside;
                continue;
            }

            columnOf[row] = column;
            slotOf[row] = inFocus.OwnerPosition(row) is { } position && laneOf[position] is >= 0 and int lane
                && lanes.ColumnOf(nanoseconds / 100) is { } laneColumn
                ? (lane * laneCount) + laneColumn
                : laneOutside;
        }

        var spec = new DomainMeasurementSpec { Domain = ByteDomain.TransportObserved };
        tally.Machine.Add(SegmentMeasurement.MeasureDomainByGroup(segment, spec, columnOf, machineOutside + 1));
        tally.Lanes.Add(SegmentMeasurement.MeasureDomainByGroup(segment, spec, slotOf, laneOutside + 1));
    }

    /// <summary>
    /// Measures each of <paramref name="mechanisms"/>' records in each column of an interval, in one pass: for each, what
    /// <see cref="Measure"/> answers for that mechanism's lane. A record of a mechanism not named is measured in no lane.
    /// The overview's own columns are answered from the bytes a persisted overview keeps there (overview-index-v1 minor 2),
    /// so a finished session's first view under a byte ranking opens no segment either; any other columns are read.
    /// </summary>
    public static SessionMechanismByteMeasures MeasureByMechanism(
        SessionStore store,
        TimeRange interval,
        int columns,
        IReadOnlyList<Mechanism> mechanisms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mechanisms);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, SessionTimelineQuery.MaximumColumns);
        if (mechanisms.Count == 0 || mechanisms.Any(mechanism => !Enum.IsDefined(mechanism))
            || mechanisms.Distinct().Count() != mechanisms.Count)
        {
            throw new ArgumentException("Name at least one mechanism §23 defines, each once.", nameof(mechanisms));
        }

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its intervals cannot be placed.");
        Func<int, int, TransportBytes> bytesOf;
        int count;
        OverviewCounts? overview = SessionDerivationCache.For(manifest).PersistedOverview(store.Root);
        bool persisted = overview is { Extent: { } extent, Main: { } main, LaneBytes: not null }
            && extent == interval && main.Counts.Count == columns;
        if (persisted)
        {
            OverviewLaneBytes kept = overview!.LaneBytes!;
            count = columns;
            bytesOf = (column, lane) => kept.Of(column, mechanisms[lane]);
        }
        else
        {
            SegmentReaderV1[] segments = SessionNativeInterval.Segments(store, manifest, interval, clock);
            var layout = new TimelineColumns(interval, columns, tallyMechanisms: false);
            TransportByteTally total = TallyLanes(segments, layout, mechanisms, cancellationToken);
            count = layout.Counts.Count;
            bytesOf = (column, lane) => total.Of((column * mechanisms.Count) + lane);
        }

        return new(manifest.SessionId, manifest.Generation, interval, Array.AsReadOnly([.. mechanisms.Select((mechanism, lane) =>
            new SessionIntervalByteMeasures(manifest.SessionId, manifest.Generation, interval,
                new IntervalByteScope { Mechanism = mechanism },
                Array.AsReadOnly([.. Enumerable.Range(0, count).Select(column => bytesOf(column, lane))])))]))
        {
            FromPersistedOverview = persisted,
        };
    }

    /// <summary>
    /// Each overview column's bytes per mechanism, for a persisted overview to keep (overview-index-v1 minor 2): every
    /// mechanism the columns count records of, measured in one pass as <see cref="MeasureByMechanism"/> measures them.
    /// </summary>
    internal static OverviewLaneBytes OverviewLanes(
        IReadOnlyList<SegmentReaderV1> segments, TimelineColumns main, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var plan = new LanePlan(main);
        if (plan.Mechanisms.Count == 0)
        {
            return new([]);
        }

        TransportByteTally total = TallyLanes(segments, plan.Layout, plan.Mechanisms, cancellationToken);
        return plan.Cells(total);
    }

    /// <summary>
    /// The overview's lanes as a pass sums their bytes (overview-index-v1 minor 2): its columns, and every mechanism they
    /// count records of, each a lane. A publication sums them in the same pass as each process's bytes.
    /// </summary>
    internal sealed class LanePlan
    {
        private readonly int[] laneOfCode;

        public LanePlan(TimelineColumns main)
        {
            ArgumentNullException.ThrowIfNull(main);
            Mechanisms = [.. main.Tallies().Select(tally => tally.Mechanism).Distinct().Order()];
            Layout = new TimelineColumns(main.Interval, main.Counts.Count, tallyMechanisms: false);
            laneOfCode = [.. Enumerable.Repeat(-1, Math.Max(TimelineColumns.MechanismCodes,
                Mechanisms.Count == 0 ? 0 : Mechanisms.Max(code => (int)code) + 1))];
            for (int lane = 0; lane < Mechanisms.Count; lane++)
            {
                laneOfCode[(int)Mechanisms[lane]] = lane;
            }
        }

        public IReadOnlyList<Mechanism> Mechanisms { get; }

        public TimelineColumns Layout { get; }

        /// <summary>An empty tally for one worker: a slot per column and lane, then one for every row outside them.</summary>
        public TransportByteTally Start() => new((Layout.Counts.Count * Mechanisms.Count) + 1);

        public void Measure(SegmentReaderV1 segment, TransportByteTally tally, CancellationToken cancellationToken)
        {
            if (Mechanisms.Count > 0)
            {
                MeasureLanes(segment, Layout, laneOfCode, Mechanisms.Count, tally, cancellationToken);
            }
        }

        /// <summary>Every column's bytes in every lane, which the overview keeps where a contribution is.</summary>
        public OverviewLaneBytes Cells(TransportByteTally total) =>
            new(Enumerable.Range(0, Layout.Counts.Count).SelectMany(column => Mechanisms.Select((mechanism, lane) =>
                (column, mechanism, total.Of((column * Mechanisms.Count) + lane)))));
    }

    /// <summary>Every row's bytes in its column's slot for its mechanism's lane, column by column, then one slot outside.</summary>
    private static TransportByteTally TallyLanes(
        IReadOnlyList<SegmentReaderV1> segments,
        TimelineColumns layout,
        IReadOnlyList<Mechanism> mechanisms,
        CancellationToken cancellationToken)
    {
        int lanes = mechanisms.Count;

        // A mechanism code's lane, or -1 for a code no lane measures; a row's code indexes it without hashing (R11).
        int[] laneOfCode = [.. Enumerable.Repeat(-1, Math.Max(TimelineColumns.MechanismCodes, mechanisms.Max(code => (int)code) + 1))];
        for (int lane = 0; lane < lanes; lane++)
        {
            laneOfCode[(int)mechanisms[lane]] = lane;
        }

        // One slot per column and lane, column by column, then one for every row outside them.
        int slots = (layout.Counts.Count * lanes) + 1;
        var total = new TransportByteTally(slots);
        SegmentPasses.Run(
            segments,
            () => new TransportByteTally(slots),
            (segment, tally) => MeasureLanes(segment, layout, laneOfCode, lanes, tally, cancellationToken),
            total.Add,
            cancellationToken);
        return total;
    }

    /// <summary>Places each of one segment's rows in its column's slot for its mechanism's lane, or outside, and measures them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MeasureLanes(
        SegmentReaderV1 segment,
        TimelineColumns layout,
        int[] laneOfCode,
        int lanes,
        TransportByteTally tally,
        CancellationToken cancellationToken)
    {
        TimeRange interval = layout.Interval;
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        if (tiles.First is not { } first || tiles.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return;
        }

        int outside = layout.Counts.Count * lanes;
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice codes = segment.Slice(SegmentColumnId.Mechanism);
        using RentedRows<int> groups = RentedRows<int>.For(segment);
        Span<int> slotOf = groups.Span;
        for (int row = 0; row < slotOf.Length; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            slotOf[row] = times.SignedAt(row) is { } nanoseconds && layout.ColumnOf(nanoseconds / 100) is { } column
                && codes.UnsignedAt(row) is { } code && code < (ulong)laneOfCode.Length && laneOfCode[(int)code] is >= 0 and int lane
                ? (column * lanes) + lane
                : outside;
        }

        var spec = new DomainMeasurementSpec { Domain = ByteDomain.TransportObserved };
        tally.Add(SegmentMeasurement.MeasureDomainByGroup(segment, spec, slotOf, outside + 1));
    }

    public static SessionIntervalByteMeasures Measure(
        SessionStore store,
        TimeRange interval,
        int columns,
        IntervalByteScope scope,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, SessionTimelineQuery.MaximumColumns);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (scope.Validate() is { } problem) throw new ArgumentException(problem, nameof(scope));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its intervals cannot be placed.");

        // A process or channel scope reads its rows by the evidence rung's own rules, as the lanes that list them count
        // them; one this generation cannot resolve is refused with the rung's reason rather than measured as nothing. Only
        // the segments the interval meets are read (P25).
        var generation = new GenerationSegments(store, manifest, clock);
        FocusRows? rows = scope.Owner is { } owner
            ? FocusRows.Resolve(store, manifest, generation, new TimelineFocus(null, [owner]), policy, cancellationToken)
            : scope.ChannelKey is { } key
            ? FocusRows.Resolve(store, manifest, generation, new TimelineFocus(key, []), policy, cancellationToken)
            : null;
        SegmentReaderV1[] segments = SessionNativeInterval.Segments(store, manifest, interval, clock);
        var layout = new TimelineColumns(interval, columns, tallyMechanisms: false);
        int count = layout.Counts.Count;

        // One slot per column, then one for every row outside them or outside the scope.
        var total = new TransportByteTally(count + 1);
        SegmentPasses.Run(
            segments,
            () => new TransportByteTally(count + 1),
            (segment, tally) => MeasureSegment(segment, layout, rows, scope, tally, cancellationToken),
            total.Add,
            cancellationToken);
        return new(manifest.SessionId, manifest.Generation, interval, scope,
            Array.AsReadOnly([.. Enumerable.Range(0, count).Select(total.Of)]));
    }

    /// <summary>Places each of one segment's rows in its column, or outside, and measures its bytes by column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MeasureSegment(
        SegmentReaderV1 segment,
        TimelineColumns layout,
        FocusRows? rows,
        IntervalByteScope scope,
        TransportByteTally tally,
        CancellationToken cancellationToken)
    {
        // A segment whose timed readings all lie outside the interval has nothing to measure, and its tiles say so without
        // a row being read.
        TimeRange interval = layout.Interval;
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        if (tiles.First is not { } first || tiles.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return;
        }

        int outside = layout.Counts.Count;
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice directions = scope.Direction is not null ? segment.Slice(SegmentColumnId.Direction) : default;
        using FocusRows.SegmentRows? inScope = rows?.Of(segment);
        using RentedRows<int> groups = RentedRows<int>.For(segment);
        Span<int> columnOf = groups.Span;
        for (int row = 0; row < columnOf.Length; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            columnOf[row] = times.SignedAt(row) is { } nanoseconds && layout.ColumnOf(nanoseconds / 100) is { } column
                && (inScope is null
                    || (inScope.Includes(row)
                        && (scope.Direction is not { } direction || SessionTimelineQuery.DirectionAt(directions, row) == direction)
                        && (scope.End is not { } end || inScope.EndOf(row) == end)))
                ? column
                : outside;
        }

        var spec = new DomainMeasurementSpec { Domain = ByteDomain.TransportObserved, Mechanism = scope.Mechanism };
        tally.Add(SegmentMeasurement.MeasureDomainByGroup(segment, spec, columnOf, outside + 1));
    }
}
