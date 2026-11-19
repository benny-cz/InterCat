using System.Runtime.ExceptionServices;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One pass per segment across the machine's processors. Each worker counts into state of its own, and each worker's
/// state is merged once it has no segment left, so a count is the same sum a serial pass makes, in any order. A failure
/// surfaces as the exception a serial pass would have thrown, not wrapped, and cancellation as cancellation.
/// </summary>
/// <remarks>
/// A segment's rows are counted by one worker, so a pass needs no lock of its own. §12's reference machine has 16
/// threads, and at 10M records a count that reads every row of a brushed interval meets its budget only when the
/// segments are counted side by side (revision 169). Each worker holds the columns its segment's pass reads, so the
/// segments read side by side are held to <see cref="SideBySideBytes"/> of columns: a compacted session's segments of a
/// quarter of a million rows, 48 MB of columns each, were read one a processor at once, 24 on a workstation, and a busy
/// hour's checkpoint ran out of memory summing their bytes (revision 883).
/// </remarks>
internal static class SegmentPasses
{
    /// <summary>
    /// How many bytes of columns the segments a pass reads side by side may hold, half the reader cache's budget: a
    /// worker waits for room for its segment's, unless no segment is being read.
    /// </summary>
    internal const long SideBySideBytes = SessionStore.DefaultSegmentReaderCacheBytes / 2;

    /// <summary>
    /// How many workers a pass runs on where a test asks for a number, to find the same answer at one as at many (§13.2's
    /// reproducibility, I14); the machine's processors otherwise. It flows only with the test that set it.
    /// </summary>
    internal static readonly AsyncLocal<int?> Workers = new();

    /// <summary>
    /// One pass per segment, the segments read side by side holding no more than <paramref name="sideBySideBytes"/> of
    /// columns between them, but for a segment read alone.
    /// </summary>
    public static void Run<TState>(
        IReadOnlyList<SegmentReaderV1> segments,
        Func<TState> start,
        Action<SegmentReaderV1, TState> pass,
        Action<TState> merge,
        CancellationToken cancellationToken,
        long sideBySideBytes = SideBySideBytes)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var room = new Room(sideBySideBytes);
        Run<SegmentReaderV1, TState>(
            segments,
            start,
            (segment, state) =>
            {
                long bytes = ColumnBytes(segment);
                room.Enter(bytes, cancellationToken);
                try
                {
                    pass(segment, state);
                }
                finally
                {
                    room.Leave(bytes);
                }
            },
            merge,
            cancellationToken);
    }

    /// <summary>
    /// One pass per segment named some other way - by its section of a persisted index, say - with the same workers,
    /// merging and failures.
    /// </summary>
    public static void Run<TSegment, TState>(
        IReadOnlyList<TSegment> segments,
        Func<TState> start,
        Action<TSegment, TState> pass,
        Action<TState> merge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(merge);
        cancellationToken.ThrowIfCancellationRequested();
        var gate = new Lock();
        try
        {
            _ = Parallel.For(
                0,
                segments.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Workers.Value ?? Environment.ProcessorCount,
                },
                start,
                (index, _, state) =>
                {
                    pass(segments[index], state);
                    return state;
                },
                state =>
                {
                    lock (gate)
                    {
                        merge(state);
                    }
                });
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>
    /// One pass per item on at most <paramref name="workers"/> workers, each taking the next item in
    /// <paramref name="items"/>' order and passing it into state of its own, merged once the worker has no item left
    /// (revision 1090): a group's byte lanes measuring its sections behind their read-ahead. A failure surfaces as the one
    /// a serial pass in that order would have met first - every item before it passed, none after it begun once it is
    /// met - and cancellation as cancellation.
    /// </summary>
    public static void RunInOrder<TItem, TState>(
        IReadOnlyList<TItem> items,
        int workers,
        Func<TState> start,
        Action<TItem, TState> pass,
        Action<TState> merge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(merge);
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0)
        {
            return;
        }

        var gate = new Lock();
        int next = -1;
        int failedAt = int.MaxValue;
        ExceptionDispatchInfo? failure = null;
        int count = Math.Clamp(workers, 1, items.Count);
        _ = Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = count }, _ =>
        {
            TState state = start();
            try
            {
                for (int at = Interlocked.Increment(ref next); at < items.Count && at < Volatile.Read(ref failedAt); at = Interlocked.Increment(ref next))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pass(items[at], state);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        lock (gate)
                        {
                            if (at < failedAt)
                            {
                                (failure, failedAt) = (ExceptionDispatchInfo.Capture(exception), at);
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    merge(state);
                }
            }
        });

        failure?.Throw();
    }

    /// <summary>
    /// How many workers measure a group's <paramref name="sections"/> sections behind their read-ahead
    /// (<see cref="RunInOrder"/>, revision 1090): one for every 32 sections, and at most a quarter of the processors a pass
    /// runs on and six. Each holds a tally of every lane's columns' bytes, which merging costs, so a session of few sections
    /// is measured by one, and measured at T3, six measured a group's first byte lanes in 90 ms at the median where one
    /// took 124 - while a group's count, lighter a section, was slower on more than one, and walks the sections alone.
    /// </summary>
    internal static int GroupMeasureWorkers(int sections) => Math.Clamp(sections / 32, 1, QuarterOfProcessors);

    /// <summary>
    /// A quarter of the processors a pass runs on, from one to six: the workers a pass beside other passes of one question
    /// runs on - a group's byte lanes' measure, and the machine row's sums before it (revision 1091), where on every
    /// processor the passes slowed each other and the rest of the machine.
    /// </summary>
    internal static int QuarterOfProcessors => Math.Clamp((Workers.Value ?? Environment.ProcessorCount) / 4, 1, 6);

    /// <summary>
    /// The most a pass over <paramref name="segment"/> reads of it: every column's values and null bitmap, which a column
    /// read holds whole.
    /// </summary>
    internal static long ColumnBytes(SegmentReaderV1 segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        IReadOnlyList<SegmentColumnDescriptor> columns = segment.Columns;
        long bytes = 0;
        for (int index = 0; index < columns.Count; index++)
        {
            bytes += columns[index].ValueLength + (columns[index].Nullable ? columns[index].NullBitmapLength : 0L);
        }

        return bytes;
    }

    /// <summary>
    /// The columns the segments being read side by side hold: a worker enters with its segment's once they fit beside
    /// those, or once no segment is being read, and leaves when its pass is done.
    /// </summary>
    private sealed class Room(long budget)
    {
        private readonly object gate = new();
        private long held;

        public void Enter(long bytes, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                while (held > 0 && held + bytes > budget)
                {
                    _ = Monitor.Wait(gate, TimeSpan.FromMilliseconds(50));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                held += bytes;
            }
        }

        public void Leave(long bytes)
        {
            lock (gate)
            {
                held -= bytes;
                Monitor.PulseAll(gate);
            }
        }
    }
}
