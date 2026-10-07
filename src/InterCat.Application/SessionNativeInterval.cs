using InterCat.Domain;

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
}
