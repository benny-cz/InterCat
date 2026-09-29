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

    /// <summary>What the scope says in words, for a heading or a caption.</summary>
    public string Description =>
        Owner is { } owner
            ? $"records owned by process instance {owner}" + (Direction is { } direction ? $" with source direction {direction}" : string.Empty)
            : ChannelKey is { } key
            ? (End is { } end ? $"records made at end {end} of channel {key}" : $"records of channel {key}")
            : Mechanism is { } mechanism
            ? $"{mechanism} records"
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
/// Measures the transport bytes of one scope's records in each column of an interval: what `icat metric --metric
/// bytes-sent --byte-domain transport-observed --side send` and its received counterpart answer for each column's own
/// interval, in one pass (R18). A record declaring a size it did not record is unmeasured and counted apart, never summed
/// as zero (R3); a record with no usable session time has no column, as it has no place on the timeline.
/// </summary>
public static class SessionIntervalByteQuery
{
    /// <summary>
    /// Measures each of <paramref name="mechanisms"/>' records in each column of an interval, in one pass: for each, what
    /// <see cref="Measure"/> answers for that mechanism's lane. A record of a mechanism not named is measured in no lane.
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
        _ = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its intervals cannot be placed.");
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        var layout = new TimelineColumns(interval, columns, tallyMechanisms: false);
        int count = layout.Counts.Count;
        int lanes = mechanisms.Count;

        // A mechanism code's lane, or -1 for a code no lane measures; a row's code indexes it without hashing (R11).
        int[] laneOfCode = [.. Enumerable.Repeat(-1, Math.Max(TimelineColumns.MechanismCodes, mechanisms.Max(code => (int)code) + 1))];
        for (int lane = 0; lane < lanes; lane++)
        {
            laneOfCode[(int)mechanisms[lane]] = lane;
        }

        // One slot per column and lane, column by column, then one for every row outside them.
        int slots = (count * lanes) + 1;
        var total = new TransportByteTally(slots);
        SegmentPasses.Run(
            segments,
            () => new TransportByteTally(slots),
            (segment, tally) => MeasureLanes(segment, layout, laneOfCode, lanes, tally, cancellationToken),
            total.Add,
            cancellationToken);
        return new(manifest.SessionId, manifest.Generation, interval, Array.AsReadOnly([.. mechanisms.Select((mechanism, lane) =>
            new SessionIntervalByteMeasures(manifest.SessionId, manifest.Generation, interval,
                new IntervalByteScope { Mechanism = mechanism },
                Array.AsReadOnly([.. Enumerable.Range(0, count).Select(column => total.Of((column * lanes) + lane))])))]));
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
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];

        // A process or channel scope reads its rows by the evidence rung's own rules, as the lanes that list them count
        // them; one this generation cannot resolve is refused with the rung's reason rather than measured as nothing.
        FocusRows? rows = scope.Owner is { } owner
            ? FocusRows.Resolve(store, manifest, segments, clock, new TimelineFocus(null, [owner]), policy, cancellationToken)
            : scope.ChannelKey is { } key
            ? FocusRows.Resolve(store, manifest, segments, clock, new TimelineFocus(key, []), policy, cancellationToken)
            : null;
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
