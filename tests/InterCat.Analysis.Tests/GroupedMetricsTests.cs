using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Grouped answers: a total broken down by process instance or by mechanism. A group's value is the total's own
/// computation over the records that belong to it, and every record in scope belongs to exactly one group or one
/// stated reason, so the rows of a grouped answer add up to its total and never count anything twice (§3.2, §5.1).
/// </summary>
public sealed class GroupedMetricsTests
{
    [Fact(DisplayName = "I5: grouped rows, the remainder and the unattributed rows partition the total exactly")]
    public void GroupsPartitionTheTotal()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());

        foreach (MetricRequest request in new[]
        {
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide),
            Request(Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.ReceiveSide),
            Request(Metric.EndpointActivityBytes, ByteDomain.TransportObserved),
            Request(Metric.Observations),
        })
        {
            MetricResult ungrouped = SessionMetrics.Evaluate(session.Store, request);
            MetricResult grouped = SessionMetrics.Evaluate(session.Store, request with { Grouping = LaneGrouping.InstanceOnly, RequestedRows = 2 });

            Assert.True(grouped.GroupsPartitionTotal);
            long sum = grouped.Groups.Sum(group => group.Value ?? 0)
                + (grouped.Remainder?.Value ?? 0)
                + grouped.Unattributed.Sum(group => group.Value ?? 0);
            Assert.Equal(ungrouped.Value, sum);
            Assert.Equal(ungrouped.Value, grouped.Value);
            Assert.Equal(ungrouped.KnownContributions, grouped.KnownContributions);

            // What the total left out is the same whether or not it is grouped: grouping never changes the scope.
            Assert.Equal(
                (ungrouped.ExcludedOtherSide, ungrouped.ExcludedNoDeclaredSlot, ungrouped.ExcludedOutsideInterval),
                (grouped.ExcludedOtherSide, grouped.ExcludedNoDeclaredSlot, grouped.ExcludedOutsideInterval));
        }
    }

    [Fact(DisplayName = "R2: a grouped total is ranked by value, ties break on a stable identity, and the rest is one exact remainder")]
    public void GroupsAreRankedWithAnExactRemainder()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 50, 100, 1),
                Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 20, 200, 2),
                Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 20, 300, 3),
                Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 5, 400, 4),
                Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 3, 500, 5),
            ]);

        MetricResult top = SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
            {
                Grouping = LaneGrouping.InstanceOnly,
                RequestedRows = 2,
            });

        Assert.Equal([1, 2], top.Groups.Select(group => group.Rank!.Value));
        Assert.Equal(50, top.Groups[0].Value);
        Assert.Equal(100, top.Groups[0].Process!.ProcessId);

        // PIDs 200 and 300 tie at 20 bytes. The tie breaks on the instance identity, so it is the same order on every
        // refresh, and the one ranked third is in the remainder with the rest.
        ProcessInstanceIndex index = ProcessInstanceIndex.Derive(TestSessions.Segments(session.Store), TestClock);
        ProcessInstanceId[] tied =
        [
            .. index.Instances
                .Where(instance => instance.ProcessId is 200 or 300)
                .Select(instance => instance.Id)
                .OrderBy(id => id.ToString(), StringComparer.Ordinal),
        ];
        Assert.Equal(tied[0], top.Groups[1].Process!.Id);
        MetricGroup remainder = top.Remainder!;
        Assert.Equal((MetricGroupKind.Remainder, 3, 28L), (remainder.Kind, remainder.GroupsMerged, remainder.Value!.Value));
    }

    [Fact(DisplayName = "R3: a group with only unknown contributions is unmeasured, never ranked below a measured zero")]
    public void AnUnmeasuredGroupSortsApart()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 0, 100, 1),
                Transfer(20, ObservationKind.Send, AccountingSide.SendSide, null, 200, 2) with { ByteAvailability = FieldAvailability.EventLost },
                Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 9, 300, 3),
            ]);

        MetricResult result = SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Grouping = LaneGrouping.InstanceOnly });

        Assert.Equal([300, 100, 200], result.Groups.Select(group => group.Process!.ProcessId));
        Assert.Equal((1, 9L), (result.Groups[0].Rank!.Value, result.Groups[0].Value!.Value));

        // PID 100 measured an observed zero and is ranked; PID 200 measured nothing and is not.
        Assert.Equal((2, 0L), (result.Groups[1].Rank!.Value, result.Groups[1].Value!.Value));
        Assert.Null(result.Groups[2].Rank);
        Assert.Null(result.Groups[2].Value);
        Assert.Equal(1, result.Groups[2].UnknownContributions);
    }

    [Fact(DisplayName = "R22: a sent total measured at the receiving end is not attributed to a sender without an association")]
    public void ACrossSideTotalIsNotGroupedByProcess()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());

        MetricResult result = SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with { Grouping = LaneGrouping.InstanceOnly });

        Assert.Equal(MetricUnavailableReason.NoTransferAssociations, result.Unavailable);
        Assert.Contains("names its receiver, not its sender", result.UnavailableExplanation!, StringComparison.Ordinal);

        // The same total grouped by mechanism is well defined: a mechanism is a fact about each record.
        MetricResult byMechanism = SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with { Grouping = LaneGrouping.Mechanism });
        Assert.True(byMechanism.IsAvailable);
        Assert.Equal(Mechanism.Tcp, Assert.Single(byMechanism.Groups).Mechanism);
    }

    [Fact(DisplayName = "I11: grouped by mechanism, each mechanism's count is its own records")]
    public void GroupsByMechanism()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());

        MetricResult result = SessionMetrics.Evaluate(session.Store, Request(Metric.Observations) with { Grouping = LaneGrouping.Mechanism });

        Assert.Equal(
            [(Mechanism.Tcp, 6L), (Mechanism.ProcessLifecycle, 3L)],
            result.Groups.Select(group => (group.Mechanism!.Value, group.Value!.Value)));
        Assert.Empty(result.Unattributed);
        Assert.Null(result.BindingRule);
    }

    [Fact(DisplayName = "R2: a grouping this session cannot derive is unavailable with what it needs, and rows need a grouping")]
    public void AnUnderivedGroupingIsUnavailable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());

        MetricResult executable = SessionMetrics.Evaluate(session.Store, Request(Metric.Observations) with { Grouping = LaneGrouping.Executable });
        Assert.Equal(MetricUnavailableReason.GroupingNotDerived, executable.Unavailable);
        Assert.Contains("image name", executable.UnavailableExplanation!, StringComparison.Ordinal);

        MetricRejection rows = (Request(Metric.Observations) with { RequestedRows = 5 }).Check()!;
        Assert.Contains("ungrouped result is one row", rows.Reason, StringComparison.Ordinal);

        ArgumentException evidence = Assert.Throws<ArgumentException>(() => SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.Observations) with { Grouping = LaneGrouping.Mechanism },
            new() { EvidenceLimit = 3 }));
        Assert.Contains("records of one total", evidence.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: a grouped rate divides every group by the same whole interval")]
    public void AGroupedRateSharesItsDenominator()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());

        MetricResult rate = SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.Rate, rateNumerator: Metric.Observations) with
            {
                Grouping = LaneGrouping.InstanceOnly,
                Interval = new TimeRange(0, 10_000_000),
            });

        Assert.True(rate.IsAvailable);
        Assert.Equal(9, rate.Rate!.Numerator);
        Assert.All(rate.Groups, group => Assert.Equal(10_000_000, group.Rate!.IntervalTicks));
        Assert.Equal(
            rate.Rate.Numerator,
            rate.Groups.Sum(group => group.Rate!.Numerator) + rate.Unattributed.Sum(group => group.Rate!.Numerator));
        Assert.Contains(rate.Caveats, caveat => caveat.Contains("the same for every group", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two processes exchange data; one was created in the capture and exits, the other was already running. A record
    /// with no owner in its payload, and one naming a PID after its only instance exited, are unattributed.
    /// </summary>
    private static ObservationRowV1[] BusySession() =>
    [
        Lifecycle(10, ObservationKind.Create, 100, 1),
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 2),
        Transfer(30, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 3),
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 60, 200, 4),
        Transfer(50, ObservationKind.Receive, AccountingSide.ReceiveSide, 60, 100, 5),
        Lifecycle(60, ObservationKind.Exit, 100, 6, exitCode: 0),
        Transfer(70, ObservationKind.Send, AccountingSide.SendSide, 5, 100, 7),
        Transfer(80, ObservationKind.Send, AccountingSide.SendSide, 9, null, 8),
        Lifecycle(90, ObservationKind.Inventory, 300, 9),
    ];

    private static MetricRequest Request(
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null,
        Metric? rateNumerator = null) => new()
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = metric,
            ByteDomain = byteDomain,
            AccountingSide = accountingSide,
            RateNumerator = rateNumerator,
        };
}
