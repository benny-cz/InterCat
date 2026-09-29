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
        return new TimeRange(start, end);
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

        return first == long.MaxValue ? null : new TimeRange(first, last + 1);
    }

    private static long NativeAt(SourceClockDescriptor clock, long nanoseconds) =>
        SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(nanoseconds));
}
