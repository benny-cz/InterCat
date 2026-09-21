namespace InterCat.Domain;

/// <summary>A half-open interval [StartTicks, EndTicks), as required by I3.</summary>
public readonly record struct TimeRange
{
    public TimeRange(long startTicks, long endTicks)
    {
        if (endTicks <= startTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(endTicks), "EndTicks must be greater than StartTicks.");
        }

        StartTicks = startTicks;
        EndTicks = endTicks;
    }

    public long StartTicks { get; }
    public long EndTicks { get; }
    public long SpanTicks => checked(EndTicks - StartTicks);

    public bool Contains(long ticks) => ticks >= StartTicks && ticks < EndTicks;

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
