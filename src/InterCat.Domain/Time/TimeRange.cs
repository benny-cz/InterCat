namespace InterCat.Domain;

/// <summary>
/// A half-open interval [StartTicks, EndTicks), as required by I3. Its span is a tick count: an end more than
/// <see cref="long.MaxValue"/> ticks after its start - over 29,000 years of 100-nanosecond ticks - is no interval this
/// type holds, so measuring one never overflows.
/// </summary>
public readonly record struct TimeRange
{
    /// <summary>
    /// How far before the instant it centres on <see cref="Around"/>'s widest span starts, which ends about as far after:
    /// 2^62 ticks, some 14,600 years of 10 MHz readings or 48 of a 3 GHz cycle counter's.
    /// </summary>
    private const long HalfWidestSpan = 1L << 62;

    public TimeRange(long startTicks, long endTicks)
    {
        if (endTicks <= startTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(endTicks), "EndTicks must be greater than StartTicks.");
        }

        if ((Int128)endTicks - startTicks > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endTicks), "EndTicks must be at most long.MaxValue ticks after StartTicks, so the span is a tick count.");
        }

        StartTicks = startTicks;
        EndTicks = endTicks;
    }

    public long StartTicks { get; }
    public long EndTicks { get; }
    public long SpanTicks => checked(EndTicks - StartTicks);

    public bool Contains(long ticks) => ticks >= StartTicks && ticks < EndTicks;

    /// <summary>
    /// Whether [<paramref name="startTicks"/>, <paramref name="endTicks"/>) is an interval this type holds - its end after
    /// its start, by at most <see cref="long.MaxValue"/> ticks - and the interval when it is. Bounds read from outside are
    /// asked this, so a reversed or too-wide request is refused in words rather than thrown.
    /// </summary>
    public static bool TryCreate(long startTicks, long endTicks, out TimeRange range)
    {
        bool holds = endTicks > startTicks && (Int128)endTicks - startTicks <= long.MaxValue;
        range = holds ? new TimeRange(startTicks, endTicks) : default;
        return holds;
    }

    /// <summary>
    /// [<paramref name="startTicks"/>, <paramref name="endTicks"/>) when this type holds it; when it is wider, the widest
    /// part of it that it holds, centred on <paramref name="focusTicks"/> as far as the interval reaches; null when the end
    /// is not after the start. A bound converted past a clock's range is held this way: a capture's readings lie near its
    /// epoch, so the part centred there keeps every reading some 2^62 ticks either side of it.
    /// </summary>
    public static TimeRange? Around(long startTicks, long endTicks, long focusTicks)
    {
        if (endTicks <= startTicks)
        {
            return null;
        }

        if (TryCreate(startTicks, endTicks, out TimeRange whole))
        {
            return whole;
        }

        Int128 start = Int128.Clamp((Int128)focusTicks - HalfWidestSpan, startTicks, (Int128)endTicks - long.MaxValue);
        return new TimeRange((long)start, (long)(start + long.MaxValue));
    }

    public TimeRange ClampInside(TimeRange extent)
    {
        if (SpanTicks >= extent.SpanTicks)
        {
            return extent;
        }

        if (StartTicks < extent.StartTicks)
        {
            return new(extent.StartTicks, checked(extent.StartTicks + SpanTicks));
        }

        if (EndTicks > extent.EndTicks)
        {
            return new(checked(extent.EndTicks - SpanTicks), extent.EndTicks);
        }

        return this;
    }
}
