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
    [Fact(DisplayName = "R22: owner filters use the instance binding, not a reused PID or a nearby lifetime")]
    public void OwnerFilterScopesTotalsGroupsAndEvidence()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());
        ProcessInstanceId owner = ProcessInstanceIndex.Derive(TestSessions.Segments(session.Store), TestClock)
            .Instances.Single(instance => instance.ProcessId == 100).Id;

        MetricRequest request = Request(Metric.Observations) with { Owner = owner };
        MetricResult count = SessionMetrics.Evaluate(session.Store, request, new() { EvidenceLimit = 10 });
        Assert.Equal(4, count.Value);
        Assert.Equal(5, count.ExcludedByProcessFilter);
        Assert.Equal([1UL, 2UL, 5UL, 6UL], count.Evidence.Select(item => item.ObservationId.RawRecordId.RecordOrdinal));
        Assert.Equal(ProcessInstanceIndex.BindingRule, count.BindingRule);

        MetricResult projected = SessionMetrics.Evaluate(session.Store, request with
        {
            Interval = new TimeRange(0, 70),
            Layer = ObservationLayer.Transport,
        });
        Assert.Equal(2, projected.Value);
        Assert.Equal(3, projected.ExcludedOutsideInterval);
        Assert.Equal(2, projected.ExcludedByProcessFilter);
        Assert.Equal(2, projected.ExcludedByProjection);
        Assert.Equal(9, projected.Value + projected.ExcludedOutsideInterval
            + projected.ExcludedByProcessFilter + projected.ExcludedByProjection);

        MetricResult grouped = SessionMetrics.Evaluate(session.Store, request with { Grouping = LaneGrouping.Mechanism });
        Assert.True(grouped.GroupsPartitionTotal);
        Assert.Equal(4, grouped.Value);
        Assert.Equal(5, grouped.ExcludedByProcessFilter);
        Assert.Equal(4, grouped.Groups.Sum(group => group.Value));

        MetricResult bytes = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Owner = owner },
            new() { EvidenceLimit = 10 });
        Assert.Equal(100, bytes.Value);
        Assert.Equal(5, bytes.ExcludedByProcessFilter);
        Assert.Single(bytes.Evidence);
        Assert.Equal(2UL, bytes.Evidence[0].ObservationId.RawRecordId.RecordOrdinal);
    }

    [Fact(DisplayName = "R22: without a relation a participant is its own records, with every undecided record disclosed")]
    public void ParticipantWithoutRelationsDisclosesWhatItCannotDecide()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());
        ProcessInstanceId owner = ProcessInstanceIndex.Derive(TestSessions.Segments(session.Store), TestClock)
            .Instances.Single(instance => instance.ProcessId == 100).Id;

        // No record carries an endpoint pair, so no other end is found: the participant's records are the ones it
        // made, and the four transfer records it did not make are disclosed as possibly its own - never added.
        MetricResult participant = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Participant = owner });
        Assert.Equal(4, participant.Value);
        Assert.Equal(4, participant.UnresolvedCounterparts[ProcessBindingReason.PeerEndpointIncomplete]);
        Assert.Equal(TransportRelationIndex.RelationRule, participant.RelationRule);

        // An owner selects the records it made, and a sent total measured at the receiving end is made by its peers.
        ArgumentException crossSide = Assert.Throws<ArgumentException>(() => SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with { Owner = owner }));
        Assert.Contains("sender or receiver focus", crossSide.Message, StringComparison.Ordinal);

        MetricResult absent = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Owner = ProcessInstanceId.New() });
        Assert.Equal(MetricUnavailableReason.ProcessInstanceNotFound, absent.Unavailable);
        Assert.NotNull((Request(Metric.Observations) with { Owner = owner, Participant = owner }).Check());
    }

    [Fact(DisplayName = "R22: an owner filter distinguishes reused PID instances and honors candidate policy")]
    public void OwnerFilterHonorsReuseAndEvidencePolicy()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 100, 1),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 2),
            Lifecycle(30, ObservationKind.Exit, 100, 3),
            Lifecycle(40, ObservationKind.Create, 100, 4),
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 20, 100, 5),
            Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 30, 100, 6),
        ]);
        ProcessInstanceId[] instances =
        [
            .. ProcessInstanceIndex.Derive(TestSessions.Segments(session.Store), TestClock).Instances
                .Where(instance => instance.ProcessId == 100)
                .OrderBy(instance => instance.CreatedNativeTicks)
                .Select(instance => instance.Id),
        ];
        Assert.Equal(2, instances.Length);

        MetricResult first = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Owner = instances[0] });
        MetricResult laterDefault = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Owner = instances[1] });
        MetricResult laterCandidates = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Owner = instances[1], EvidencePolicy = EvidencePolicy.IncludeCandidates });
        Assert.Equal(3, first.Value);
        Assert.Equal(1, laterDefault.Value);
        Assert.Equal(3, laterCandidates.Value);
        Assert.Equal(3, first.ExcludedByProcessFilter);
        Assert.Equal(5, laterDefault.ExcludedByProcessFilter);
        Assert.Equal(3, laterCandidates.ExcludedByProcessFilter);

        MetricResult firstBytes = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Owner = instances[0] });
        MetricResult laterBytes = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
            {
                Owner = instances[1],
                EvidencePolicy = EvidencePolicy.IncludeCandidates,
            });
        Assert.Equal(10, firstBytes.Value);
        Assert.Equal(50, laterBytes.Value);
    }

    [Fact(DisplayName = "R22: what an evidence policy leaves unattributed is said as what it is, a reused PID's candidates or all but lifecycle records")]
    public void WhatAPolicyLeavesUnattributedIsSaidAsWhatItIs()
    {
        // PID 100's first instance sent once, and its later one twice: the later sends are a reused PID's candidates.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 100, 1),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 2),
            Lifecycle(30, ObservationKind.Exit, 100, 3),
            Lifecycle(40, ObservationKind.Create, 100, 4),
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 20, 100, 5),
            Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 30, 100, 6),
        ]);

        MetricResult byDefault = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Contains(byDefault.Caveats, caveat => caveat.StartsWith(
            "2 contributions lie in the lifetime of a later instance of a PID this capture reused.", StringComparison.Ordinal));

        // Direct evidence only leaves the first instance's send out too, which is no candidate: the caveat says what all
        // three are, never that they lie in a reused PID's later instance.
        MetricResult direct = SessionMetrics.Evaluate(session.Store,
            Request(Metric.Observations) with { Grouping = LaneGrouping.InstanceOnly, EvidencePolicy = EvidencePolicy.DirectOnly });
        Assert.Contains(direct.Caveats, caveat => caveat.StartsWith(
            "3 contributions bind to an instance by where their readings fall in its lifetime, not by a lifecycle record of it, "
            + "so they stay unattributed under direct evidence only.", StringComparison.Ordinal));
        Assert.DoesNotContain(direct.Caveats, caveat => caveat.Contains("PID this capture reused.", StringComparison.Ordinal));
    }

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

    [Fact(DisplayName = "R22: a sent total measured at the receiving end is attributed to a sender only through a relation")]
    public void ACrossSideTotalNeedsTheOtherEnd()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusySession());

        // The receive records name their receivers; with no endpoint pair, nothing finds the process that sent to them,
        // so every contribution is unattributed with that reason rather than given to the receiver.
        MetricResult result = SessionMetrics.Evaluate(
            session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with { Grouping = LaneGrouping.InstanceOnly });

        Assert.True(result.GroupsPartitionTotal);
        Assert.Empty(result.Groups);
        MetricGroup unattributed = Assert.Single(result.Unattributed);
        Assert.Equal((ProcessBindingReason.PeerEndpointIncomplete, 160L), (unattributed.Reason!.Value, unattributed.Value!.Value));
        Assert.Equal(160, result.Value);

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

    [Fact(DisplayName = "R2: grouped by terminal session, a session holds its processes' records, one naming none is unattributed by that reason, and the rows partition the total")]
    public void GroupsByTerminalSession()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = SessionsOfProcesses();
        using var session = new TemporarySession();
        Publish(session.Store, rows, fields: fields);

        foreach ((MetricRequest request, long first, long second, long unrecorded) in new[]
        {
            (Request(Metric.Observations), 5L, 2L, 2L),
            (Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide), 150L, 0L, 9L),
        })
        {
            MetricResult ungrouped = SessionMetrics.Evaluate(session.Store, request);
            MetricResult grouped = SessionMetrics.Evaluate(session.Store, request with { Grouping = LaneGrouping.UserSession });

            // Processes 100 and 200 ran in terminal session 1 and 300 in session 0; 400's records named none.
            MetricGroup one = grouped.Groups.Single(group => group.TerminalSession == 1);
            Assert.Equal((MetricGroupKind.UserSession, first, 1), (one.Kind, one.Value!.Value, one.Rank!.Value));
            Assert.Equal(second, grouped.Groups.SingleOrDefault(group => group.TerminalSession == 0)?.Value ?? 0);
            Assert.All(grouped.Groups, group => Assert.Null(group.Process));
            MetricGroup missing = grouped.Unattributed.Single(group => group.Reason == ProcessBindingReason.SessionUnknown);
            Assert.Equal(unrecorded, missing.Value);
            Assert.Contains(grouped.Unattributed, group => group.Reason == ProcessBindingReason.NoOwner);
            Assert.DoesNotContain(grouped.Unattributed, group => group.Reason == ProcessBindingReason.ExecutableUnknown);
            Assert.Contains(grouped.Caveats, caveat => caveat.Contains("never placed in a guessed session", StringComparison.Ordinal));

            Assert.True(grouped.GroupsPartitionTotal);
            Assert.Equal(ungrouped.Value, grouped.Groups.Sum(group => group.Value ?? 0) + grouped.Unattributed.Sum(group => group.Value ?? 0));
        }

        // Peers are counted per session: session 1's processes reached session 0's and each other, and session 0's
        // reached one process of session 1.
        MetricResult peers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Grouping = LaneGrouping.UserSession });
        Assert.Equal(
            [(1u, 3L), (0u, 1L)],
            peers.Groups.Select(group => (group.TerminalSession!.Value, group.Value!.Value)));
        Assert.All(peers.Groups, group => Assert.Equal(MetricGroupKind.UserSession, group.Kind));

        // A session whose lifecycle records name no terminal session cannot be grouped by one at all.
        using var unnamed = new TemporarySession();
        Publish(unnamed.Store, BusySession());
        MetricResult refused = SessionMetrics.Evaluate(unnamed.Store, Request(Metric.Observations) with { Grouping = LaneGrouping.UserSession });
        Assert.Equal(MetricUnavailableReason.GroupingNotDerived, refused.Unavailable);
        Assert.Contains("terminal session", refused.UnavailableExplanation!, StringComparison.Ordinal);
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
    /// <summary>
    /// Four processes: 100 and 200 in terminal session 1, 300 in session 0 and 400, whose records name no session. 100
    /// sends 100 B to 300 and 200 sends 50 B to 100, each received at the other end; 300 sends nothing, 400 sends 9 B to
    /// a host no record holds, and one send of 5 B names no owner.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) SessionsOfProcesses()
    {
        ObservationRowV1 first = Lifecycle(10, ObservationKind.Create, 100, 1);
        ObservationRowV1 second = Lifecycle(11, ObservationKind.Create, 200, 2);
        ObservationRowV1 service = Lifecycle(12, ObservationKind.Create, 300, 3);
        ObservationRowV1 unnamed = Lifecycle(13, ObservationKind.Create, 400, 4);
        return (
        [
            first, second, service, unnamed,
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 5).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 300, 6).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 50, 200, 7).Between("127.0.0.1:50001", "127.0.0.1:9090"),
            Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 50, 100, 8).Between("127.0.0.1:9090", "127.0.0.1:50001"),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 9, 400, 9).Between("127.0.0.1:50002", "10.0.0.5:443"),
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 5, null, 10),
        ],
        [
            Field(first, SourceField.ProcessSessionId, 1),
            Field(second, SourceField.ProcessSessionId, 1),
            Field(service, SourceField.ProcessSessionId, 0),
        ]);
    }

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
