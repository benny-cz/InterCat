using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The density columns a lane of operations is drawn in (§6.2), counted as §21.1 defines an interval's operations: each
/// starts in one interval and completes in one, runs in every interval from its first reading's to its last's, and
/// occupies the time it ran inside each, clipped to it (I20).
/// </summary>
public sealed class OperationColumnsTests
{
    private const long Second = 10_000_000;
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "I20: §21.1's operation from 0.5 s to 2.5 s over [0,1), [1,2) and [2,3) s starts 1/0/0, completes 0/0/1, runs 1/1/1 and occupies 0.5/1/0.5 s")]
    public void AnOperationOccupiesItsClippedTimeInEachInterval()
    {
        // Session nanoseconds are 100 native ticks each here, so a call's workspace ticks are its native ones.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. new[]
            {
                Lifecycle(1, ObservationKind.Create, 400, 1),
                RpcCall(Second / 2, ObservationKind.RequestStart, Direction.Outbound, 400, 2, Activity(1), ServiceControl),
                RpcCall(Second * 5 / 2, ObservationKind.RequestEnd, Direction.Outbound, 400, 3, Activity(1), status: 0),
            }.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 }),
        ]);
        TimeRange[] buckets = [.. Enumerable.Range(0, 3).Select(bucket => new TimeRange(bucket * Second, (bucket + 1) * Second))];

        // It starts in the first second and completes in the third, each counted by the record that puts it there.
        long[] Counts(Metric metric) =>
        [
            .. buckets.Select(bucket => SessionMetrics.Evaluate(session.Store, new MetricRequest
            {
                Basis = AnalysisBasis.LogicalOperations,
                Metric = metric,
                Interval = bucket,
            }).Value!.Value),
        ];
        Assert.Equal([1L, 0L, 0L], Counts(Metric.OperationsStarted));
        Assert.Equal([0L, 0L, 1L], Counts(Metric.OperationsCompleted));

        // The lane draws it from its start to its stop. Counted into the three seconds' columns, it runs in each of them,
        // never starting again in the second or the third, and occupies the half, the whole and the half it ran in.
        ProcessInstanceId caller = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 400).Id;
        string channel = RpcChannelKeys.Channel(caller, RpcCallSide.Client, ServiceControl);
        var interval = new TimeRange(0, 3 * Second);
        RpcCallSpanView call = Assert.Single(SessionRpcCalls.Spans(session.Store, channel, interval).Calls);
        Assert.Equal((Second / 2, 5 * Second / 2), (call.StartTicks!.Value, call.EndTicks!.Value));
        var columns = new OperationColumns(interval, 3);
        columns.Add(call.FirstTicks, call.EndTicks.Value, call.Failed, (call.StartTicks.Value, call.EndTicks.Value));
        Assert.Equal(buckets, Enumerable.Range(0, 3).Select(column => RpcCallDensity.ColumnIntervalOf(interval, 3, column)));
        Assert.Equal([1L, 1L, 1L], columns.Running());
        Assert.Equal([Second / 2, Second, Second / 2], columns.Occupied());
        Assert.Equal([0L, 0L, 0L], columns.Untimed());
        Assert.Equal([0L, 0L, 0L], columns.Flagged());
    }

    [Fact(DisplayName = "I20: an operation that stops where a column begins runs in it and occupies none of it, and one no reading timed occupies nothing")]
    public void AnEdgeOrAnUntimedOperationOccupiesNothing()
    {
        var columns = new OperationColumns(new TimeRange(0, 30), 3);

        // From 5 to 20: read in the column its stop opens, yet its time is [5, 20), all of it in the first two.
        columns.Add(5, 20, isFlagged: false, (5, 20));

        // A record whose peer is not in the evidence runs in its own column and is counted there apart, adding no time.
        columns.Add(25, 25, isFlagged: true, null);

        // Open at capture end: from its start to the interval's end, whatever lies beyond.
        columns.Add(12, long.MaxValue, isFlagged: false, (12, long.MaxValue));

        Assert.Equal([1L, 2L, 3L], columns.Running());
        Assert.Equal([5L, 18L, 10L], columns.Occupied());
        Assert.Equal([0L, 0L, 1L], columns.Untimed());
        Assert.Equal([0L, 0L, 1L], columns.Flagged());
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 3);
}
