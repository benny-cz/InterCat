using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// One lane's operations counted into an interval's equal density columns as they are read (§6.2, §21.1): in each column,
/// the operations running in it - its overlap count - how many of those are flagged, how many no reading timed, and the
/// time they occupied inside it. An operation runs in every column from the one its first reading falls in to the one its
/// last does; its measured time is clipped to each column it crosses, so a long operation adds time to every column it
/// crosses while it starts in one of them only. Each count is kept as differences until it is read, so an operation costs
/// the same whatever its length.
/// </summary>
internal sealed class OperationColumns
{
    private readonly TimeRange interval;
    private readonly long[] running;
    private readonly long[] flagged;
    private readonly long[] untimed;
    private readonly long[] crossed;
    private readonly long[] occupied;

    /// <summary>Cuts <paramref name="interval"/> into <paramref name="columns"/> columns, each at least one tick wide.</summary>
    public OperationColumns(TimeRange interval, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, interval.SpanTicks);
        this.interval = interval;
        Columns = columns;
        running = new long[columns + 1];
        flagged = new long[columns + 1];
        untimed = new long[columns + 1];
        crossed = new long[columns + 1];
        occupied = new long[columns];
    }

    public int Columns { get; }

    /// <summary>
    /// Counts one operation whose readings run from <paramref name="first"/> to <paramref name="last"/>, which the caller
    /// has found to meet the interval. <paramref name="measured"/> is the time it ran, from its start to its stop - to
    /// <see cref="long.MaxValue"/> while it was still running at capture end - or null where no reading timed it, as a
    /// record whose peer is not in the evidence did not.
    /// </summary>
    public void Add(long first, long last, bool isFlagged, (long Start, long Stop)? measured)
    {
        int from = RpcCallDensity.ColumnOf(interval, Columns, first);
        int to = RpcCallDensity.ColumnOf(interval, Columns, Math.Max(first, last)) + 1;
        running[from]++;
        running[to]--;
        if (isFlagged)
        {
            flagged[from]++;
            flagged[to]--;
        }

        if (measured is not { } time)
        {
            untimed[from]++;
            untimed[to]--;
            return;
        }

        // The time is half-open, [start, stop): an operation that stops where a column begins is read in that column but
        // occupies none of it, and the columns together hold exactly its time within the interval.
        long start = Math.Max(time.Start, interval.StartTicks);
        long stop = Math.Min(time.Stop, interval.EndTicks);
        if (stop <= start)
        {
            return;
        }

        int head = RpcCallDensity.ColumnOf(interval, Columns, start);
        int tail = RpcCallDensity.ColumnOf(interval, Columns, stop - 1);
        if (head == tail)
        {
            occupied[head] = checked(occupied[head] + (stop - start));
            return;
        }

        occupied[head] = checked(occupied[head] + (RpcCallDensity.ColumnIntervalOf(interval, Columns, head).EndTicks - start));
        occupied[tail] = checked(occupied[tail] + (stop - RpcCallDensity.ColumnIntervalOf(interval, Columns, tail).StartTicks));
        crossed[head + 1]++;
        crossed[tail]--;
    }

    /// <summary>The operations running in each column: §21.1's overlap count, never a count of records.</summary>
    public long[] Running() => Summed(running);

    /// <summary>Of the operations running in each column, those flagged: failed, or not recorded whole.</summary>
    public long[] Flagged() => Summed(flagged);

    /// <summary>Of the operations running in each column, those no reading timed, whose time no column holds.</summary>
    public long[] Untimed() => Summed(untimed);

    /// <summary>
    /// The time the operations occupied in each column, in ticks: each one's measured time clipped to the column, so
    /// operations that ran at once occupy more than the column's width between them.
    /// </summary>
    public long[] Occupied()
    {
        long[] whole = Summed(crossed);
        long[] result = new long[Columns];
        for (int column = 0; column < Columns; column++)
        {
            long width = RpcCallDensity.ColumnIntervalOf(interval, Columns, column).SpanTicks;
            result[column] = checked(occupied[column] + (whole[column] * width));
        }

        return result;
    }

    private long[] Summed(long[] differences)
    {
        long[] result = new long[Columns];
        long sum = 0;
        for (int column = 0; column < Columns; column++)
        {
            sum += differences[column];
            result[column] = sum;
        }

        return result;
    }
}
