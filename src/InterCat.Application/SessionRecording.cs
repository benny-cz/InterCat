using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A session's recording (`contracts/metrics-v1.md` §7): the interval its capture recorded - from its epoch, session time
/// 0, to the stop reading its clock calibration records (`contracts/clock-calibration-v1.md`), widened to hold any record
/// with a session time before or after them. It is the whole interval a whole session's rates divide by. A record with no
/// session time widens nothing, since its reading places it at no time of the session. A session whose capture recorded
/// no stop - an import, one captured before revision 255, or one that ended before its last publication - has none, and
/// its whole-session rates stay unavailable rather than divided by the span between its first and last record, which is
/// no interval.
/// </summary>
public static class SessionRecording
{
    /// <summary>
    /// The recording in presentation ticks of session time, given the extent of the session's timed records; null when the
    /// capture recorded no stop.
    /// </summary>
    public static TimeRange? Interval(IOwnedDirectory root, SessionManifestV1 manifest, SourceClockDescriptor clock, TimeRange? records)
    {
        if (Stop(root, manifest, clock) is not { } stop)
        {
            return null;
        }

        // The stop reading rounded up to a whole tick, so the interval holds it; widened to any record beyond either end.
        return new TimeRange(Math.Min(0, records?.StartTicks ?? 0), Math.Max(((stop.Nanoseconds - 1) / 100) + 1, records?.EndTicks ?? 0));
    }

    /// <summary>
    /// The recording in native ticks of the session's clock, as a metric request names its interval: from the epoch reading
    /// to the stop reading, widened to hold every record with a session time, each widened end the first native reading at
    /// or after it as a brushed interval's are. Null when the capture recorded no stop.
    /// </summary>
    public static TimeRange? NativeInterval(
        SessionStore store,
        SessionManifestV1 manifest,
        SourceClockDescriptor clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Stop(store.Root, manifest, clock) is not { } stop)
        {
            return null;
        }

        // A finished session's persisted overview holds its timed records' extent; any other's segments count it from
        // their tiles.
        TimeRange? records = SessionDerivationCache.For(manifest).PersistedOverview(store.Root) is { } persisted
            ? persisted.Extent
            : Extent(store, manifest, cancellationToken);

        // A presentation tick holds the readings that truncate to it, so tick 0 and any before it reach up to 99 ns below
        // their start: a record there may precede the epoch, and the start is taken at or after the lowest it can be.
        long start = records is { StartTicks: <= 0 } before
            ? Math.Min(clock.CaptureEpochNativeTicks, NativeAt(clock, checked((before.StartTicks * 100) - 99)))
            : clock.CaptureEpochNativeTicks;
        long end = records is { } after ? Math.Max(stop.Native, NativeAt(clock, checked(after.EndTicks * 100))) : stop.Native;
        return TimeRange.Around(start, end, clock.CaptureEpochNativeTicks);
    }

    /// <summary>
    /// The stop reading its calibration's last sample records, natively and in session time; null when the generation
    /// carries no calibration of this clock with a stop after its start, or the stop has no session time after the epoch.
    /// </summary>
    private static (long Native, long Nanoseconds)? Stop(IOwnedDirectory root, SessionManifestV1 manifest, SourceClockDescriptor clock)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(manifest);
        if (ClockCalibrationV1.Read(root, manifest) is not { Samples.Count: >= 2 } calibration
            || calibration.ClockId != clock.Id.Value
            || calibration.Samples[^1].NativeTicks <= calibration.Samples[0].NativeTicks)
        {
            return null;
        }

        long native = calibration.Samples[^1].NativeTicks;
        return SourceClockMath.ConvertToSession(clock, new NativeTimestamp(clock.Id, clock.Encoding, native)).SessionTime?.Nanoseconds
            is long nanoseconds and > 0
                ? (native, nanoseconds)
                : null;
    }

    /// <summary>
    /// When the capture began by the wall clock its calibration read (`contracts/clock-calibration-v1.md`): session time 0,
    /// in UTC, as its first sample's wall-clock reading less that sample's session time. Null when the generation carries
    /// no calibration of this clock - an import, or a capture before revision 255 - or its first sample has no session time.
    /// </summary>
    public static DateTimeOffset? Began(IOwnedDirectory root, SessionManifestV1 manifest, SourceClockDescriptor clock)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(manifest);
        if (ClockCalibrationV1.Read(root, manifest) is not { Samples.Count: > 0 } calibration || calibration.ClockId != clock.Id.Value)
        {
            return null;
        }

        ClockCalibrationSampleV1 first = calibration.Samples[0];
        return SourceClockMath.ConvertToSession(clock, new NativeTimestamp(clock.Id, clock.Encoding, first.NativeTicks)).SessionTime
            is { } since
                ? first.Utc.ToUniversalTime().AddTicks(-(since.Nanoseconds / 100))
                : null;
    }

    /// <summary>
    /// The wall clock the capture's machine read, placed against session time by its calibration: the first sample's
    /// reading at that sample's session time, and the rate between the first and the last sample where there are two.
    /// Null where <see cref="Began"/> is.
    /// </summary>
    public static SessionWallClock? WallClock(IOwnedDirectory root, SessionManifestV1 manifest, SourceClockDescriptor clock)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(manifest);
        if (ClockCalibrationV1.Read(root, manifest) is not { Samples.Count: > 0 } calibration || calibration.ClockId != clock.Id.Value
            || SourceClockMath.ConvertToSession(clock, new NativeTimestamp(clock.Id, clock.Encoding, calibration.Samples[0].NativeTicks))
                .SessionTime is not { } since)
        {
            return null;
        }

        return new(since.Nanoseconds / 100, calibration.Samples[0].Utc.ToUniversalTime(),
            ClockCalibrationFacts.Rate(calibration, clock.TicksPerSecond)?.PartsPerMillion,
            calibration.Samples.Max(sample => sample.AcquisitionUncertaintyNanoseconds));
    }

    /// <summary>The wall clock the current generation's capture recorded, read under a lease of its own; null where none.</summary>
    public static SessionWallClock? WallClock(SessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        return SessionSegments.SourceClock(store.Root, lease.Manifest) is { } clock
            ? WallClock(store.Root, lease.Manifest, clock)
            : null;
    }

    /// <summary>
    /// The current generation's timed records' extent in presentation ticks, as the window's overview counts it: a finished
    /// session's persisted overview holds it, and any other's segments count it from their tiles. Null where no record
    /// has a session time.
    /// </summary>
    /// <summary>
    /// The presentation tick from which a session keeps every record: the first wholly at or after its latest interval
    /// release's boundary (ADR-043), so no column drawn from it reaches into what was released; null when it states none.
    /// Its timeline begins there (ADR-045), and a moment before it lies in what the session released.
    /// </summary>
    public static long? RetainedFromTicks(SessionManifestV1 manifest) =>
        RetainedFromNanoseconds(manifest) is { } boundary ? (boundary + 99) / 100 : null;

    /// <summary>
    /// The session time from which a session keeps every record: its latest interval release's boundary (ADR-043); null
    /// when it states none.
    /// </summary>
    public static long? RetainedFromNanoseconds(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds;
    }

    /// <summary>
    /// The extent of the session's timed records from where it keeps every record (<see cref="RetainedFromTicks"/>), as its
    /// overview counts it; null when no record has a session time.
    /// </summary>
    public static TimeRange? RecordsExtent(SessionStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        return SessionDerivationCache.For(lease.Manifest).PersistedOverview(store.Root) is { } persisted
            ? persisted.Extent
            : Extent(store, lease.Manifest, cancellationToken);
    }

    private static TimeRange? Extent(SessionStore store, SessionManifestV1 manifest, CancellationToken cancellationToken)
    {
        long first = long.MaxValue;
        long last = long.MinValue;
        foreach (string name in SessionSegments.Names(manifest))
        {
            SegmentTimeTiles tiles = SegmentTimeTiles.Of(SessionSegments.Open(store, manifest, name), cancellationToken);
            if (tiles.First is { } earliest && tiles.Last is { } latest)
            {
                first = Math.Min(first, earliest);
                last = Math.Max(last, latest);
            }
        }

        if (first == long.MaxValue)
        {
            return null;
        }

        // Drawn from the boundary on, as the overview counts it, though the rows kept from before it are records too.
        if (RetainedFromTicks(manifest) is { } retained && first < retained && last >= retained)
        {
            first = retained;
        }

        return new TimeRange(first, last + 1);
    }

    private static long NativeAt(SourceClockDescriptor clock, long nanoseconds) =>
        SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(nanoseconds));
}

/// <summary>
/// The wall clock a capture's machine read, placed against session time by its clock calibration
/// (`contracts/clock-calibration-v1.md`): the first sample's reading at that sample's session time and, from a second, the
/// rate the wall clock ran at against the source clock between them, so an instant between the capture's start and its
/// stop falls on the line through both readings and one beyond them on its extension. A sample bounds only how far apart
/// its two readings were taken, never how right the wall clock was, so what this places is what that machine's clock
/// read, not true time.
/// </summary>
/// <param name="AnchorTicks">The first sample's session time, in presentation ticks.</param>
/// <param name="AnchorUtc">The wall clock's reading at the first sample, in UTC.</param>
/// <param name="PartsPerMillion">
/// How fast the wall clock ran against the source clock between the first and the last sample; null with one sample, when
/// the wall clock is read at the source clock's own rate, which nothing measured.
/// </param>
/// <param name="UncertaintyNanoseconds">The widest acquisition uncertainty of the samples it rests on.</param>
public sealed record SessionWallClock(long AnchorTicks, DateTimeOffset AnchorUtc, double? PartsPerMillion, long UncertaintyNanoseconds)
{
    /// <summary>What the wall clock read at an instant of session time, in presentation ticks, by the calibration's line.</summary>
    public DateTimeOffset At(long ticks)
    {
        long elapsed = ticks - AnchorTicks;
        long steered = PartsPerMillion is { } rate ? (long)Math.Round(elapsed * rate / 1e6) : 0;
        return AnchorUtc.AddTicks(elapsed + steered);
    }

    /// <summary>The instant of session time, in presentation ticks, at which the wall clock read <paramref name="utc"/>.</summary>
    public long TicksAt(DateTimeOffset utc)
    {
        long elapsed = (utc - AnchorUtc).Ticks;
        return AnchorTicks + (PartsPerMillion is { } rate ? (long)Math.Round(elapsed / (1 + (rate / 1e6))) : elapsed);
    }
}
