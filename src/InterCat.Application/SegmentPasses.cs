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
/// segments are counted side by side (revision 169).
/// </remarks>
internal static class SegmentPasses
{
    public static void Run<TState>(
        IReadOnlyList<SegmentReaderV1> segments,
        Func<TState> start,
        Action<SegmentReaderV1, TState> pass,
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
                new ParallelOptions { CancellationToken = cancellationToken },
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
}
