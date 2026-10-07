using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// An interval of session time as the native readings it holds, so a reader can find its rows by reading, or pass over a
/// segment whose readings it does not meet, without converting a row (§10.2, I3).
/// </summary>
internal static class SessionNativeInterval
{
    /// <summary>
    /// The native readings whose session time an interval holds, [first, end): those whose nanoseconds over 100,
    /// truncated toward zero as every interval reads a row's time, lie in it. That is from 100 times its start - less 99
    /// when the start is not positive - to 100 times its end, less 99 when the end is not positive, and session time never
    /// decreases with the reading (I3), so they are one run. A bound past the clock's range is the range's end.
    /// </summary>
    public static (long First, long End) Readings(TimeRange range, SourceClockDescriptor clock)
    {
        Int128 first = (Int128)range.StartTicks * 100 - (range.StartTicks > 0 ? 0 : 99);
        Int128 end = (Int128)range.EndTicks * 100 - (range.EndTicks > 0 ? 0 : 99);
        return (Native(first), Native(end));

        long Native(Int128 nanoseconds)
        {
            if (nanoseconds < long.MinValue) return long.MinValue;
            if (nanoseconds > long.MaxValue) return long.MaxValue;
            try
            {
                return SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp((long)nanoseconds));
            }
            catch (OverflowException)
            {
                return nanoseconds < 0 ? long.MinValue : long.MaxValue;
            }
        }
    }

    /// <summary>
    /// The segments of a generation that can hold a row whose session time the interval holds, opened in the
    /// generation's order. Any other holds none (I3), and is passed over by the readings its header declares, or its open
    /// reader's, without being opened: a query of an interval costs what the interval holds, not what the session does
    /// (P25, §10.2).
    /// </summary>
    public static SegmentReaderV1[] Segments(
        SessionStore store,
        SessionManifestV1 manifest,
        TimeRange interval,
        SourceClockDescriptor clock) =>
        Segments(store, manifest, Readings(interval, clock));

    /// <summary>The segments of a generation holding a native reading in [first, end), opened in the generation's order.</summary>
    public static SegmentReaderV1[] Segments(SessionStore store, SessionManifestV1 manifest, (long First, long End) readings) =>
        [.. SessionSegments.Names(manifest)
            .Where(name => Meets(SessionSegments.NativeRange(store, manifest, name), readings))
            .Select(name => SessionSegments.Open(store, manifest, name))];

    /// <summary>The open segments holding a native reading in [first, end), in their order.</summary>
    public static SegmentReaderV1[] Of(IEnumerable<SegmentReaderV1> segments, (long First, long End) readings) =>
        [.. segments.Where(segment => Meets((segment.MinNativeTicks, segment.MaxNativeTicks), readings))];

    /// <summary>Whether a segment whose rows' readings run from its first to its last, both held, has one in [first, end).</summary>
    public static bool Meets((long Min, long Max) span, (long First, long End) readings) =>
        span.Min < readings.End && span.Max >= readings.First;
}
