namespace InterCat.Domain;

/// <summary>
/// A uniform half-open bucketing of a time extent (I3). Every eligible point belongs to exactly one
/// bucket (I4), and a bucket's range reproduces the membership test exactly, so a cell count and the
/// detail rows behind it can never disagree (I5). The math is integer-only in the time domain (R10).
/// </summary>
public sealed class TimeBuckets
{
    private readonly long start;
    private readonly long width;

    private TimeBuckets(long start, long width, int count)
    {
        this.start = start;
        this.width = width;
        Count = count;
    }

    /// <summary>Number of buckets covering the extent.</summary>
    public int Count { get; }

    /// <summary>Bucket width in ticks. Uniform, so no bucket can silently absorb a remainder.</summary>
    public long WidthTicks => width;

    /// <summary>The extent these buckets cover, which may extend past the requested end by at most one width.</summary>
    public TimeRange Extent => new(start, checked(start + (width * Count)));

    /// <summary>
    /// Divides <paramref name="extent"/> into buckets of at least one tick. The bucket count is chosen so
    /// the covered extent contains the requested one; a partial trailing bucket is never dropped.
    /// </summary>
    public static TimeBuckets Create(TimeRange extent, int requestedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedCount);

        long span = extent.SpanTicks;
        long width = Math.Max(1, span / requestedCount);
        long covered = checked(width * requestedCount);
        int count = covered >= span
            ? requestedCount
            : checked(requestedCount + (int)Math.Min(int.MaxValue - requestedCount, DivideUp(span - covered, width)));
        return new(extent.StartTicks, width, count);
    }

    /// <summary>Creates buckets of an exact width, covering the whole extent.</summary>
    public static TimeBuckets CreateWithWidth(TimeRange extent, long widthTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthTicks);

        long count = DivideUp(extent.SpanTicks, widthTicks);
        if (count > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthTicks),
                "The requested width would produce more buckets than a session may hold.");
        }

        return new(extent.StartTicks, widthTicks, (int)count);
    }

    /// <summary>
    /// The bucket a tick belongs to, or null when the tick lies outside the covered extent. Membership is
    /// half-open, so a tick on a boundary belongs to the bucket that starts there and to no other (I4).
    /// </summary>
    public int? IndexOf(long ticks)
    {
        if (ticks < start)
        {
            return null;
        }

        long offset = checked(ticks - start);
        long index = offset / width;
        return index >= Count ? null : (int)index;
    }

    /// <summary>The half-open range of one bucket.</summary>
    public TimeRange RangeOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        long bucketStart = checked(start + (width * index));
        return new(bucketStart, checked(bucketStart + width));
    }

    private static long DivideUp(long value, long divisor) => value <= 0 ? 0 : ((value - 1) / divisor) + 1;
}
