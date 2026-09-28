using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The logical-operations basis (`contracts/metrics-v1.md` §8a): operations started, completed and failed are counted
/// from the calls `rpc-call-operation-v1` derives, each by the record that puts it in scope, and every call in scope a
/// count does not take is stated by what its records establish.
/// </summary>
public sealed class OperationMetricsTests
{
    private const int Client = 400;
    private const int Server = 500;
    private const long Second = 10_000_000;
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "P4: started, completed and failed calls are counted from the calls, never from the records beneath them")]
    public void OperationsAreCountedFromTheCalls()
    {
        // The client's first call carried a transfer beneath it, seen at both ends. That is transport evidence of the one
        // exchange, not another communication: it adds no operation, and no count adds it to one (§5.1).
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. EveryState(),
            Transfer(120, ObservationKind.Send, AccountingSide.SendSide, 64, Client, ordinal: 50),
            Transfer(130, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, Server, ordinal: 51),
        ]);
        Assert.Equal(16, SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Observations,
        }).Value);
        MetricRejection both = Assert.IsType<MetricRejection>(Request(Metric.Observations).Check());
        Assert.Contains("count one exchange twice", both.Reason, StringComparison.Ordinal);

        // Six calls have a start, whatever became of them: four completed, one is open at capture end and one carried no
        // activity id. A stop without its start is not a start.
        MetricResult started = Evaluate(session.Store, Metric.OperationsStarted);
        Assert.True(started.IsAvailable);
        Assert.Equal(6, started.Value);
        Assert.Equal(MeasurementUnit.Count, started.Unit);
        Assert.Equal(ObservationKind.RequestStart, started.Operations!.CountedRecord);
        Assert.Equal(
            new Dictionary<RpcCallState, long>
            {
                [RpcCallState.Completed] = 4,
                [RpcCallState.OpenAtCaptureEnd] = 1,
                [RpcCallState.NoActivityId] = 1,
            },
            started.Operations.InScope);
        Assert.Equal(8, started.Operations.Calls);
        Assert.Equal((RpcCallIndex.OperationRule, ProcessInstanceIndex.BindingRule), (started.OperationRule, started.BindingRule));
        Assert.Null(started.RelationRule);
        Assert.Contains(started.Caveats, caveat => caveat.Contains("1 open at capture end (censored, not failed)", StringComparison.Ordinal));

        // Six stops are in scope and four of them close a call whose start is in the evidence. The other two are stated:
        // one whose start came before the capture, and one that carried no activity id.
        MetricResult completed = Evaluate(session.Store, Metric.OperationsCompleted);
        Assert.Equal(4, completed.Value);
        Assert.Equal(ObservationKind.RequestEnd, completed.Operations!.CountedRecord);
        Assert.Equal(
            new Dictionary<RpcCallState, long>
            {
                [RpcCallState.Completed] = 4,
                [RpcCallState.StartNotObserved] = 1,
                [RpcCallState.NoActivityId] = 1,
            },
            completed.Operations.InScope);
        Assert.Contains(completed.Caveats, caveat => caveat.Contains(
            "2 stops in scope are paired with no start and are not counted: 1 whose start is not in the evidence and 1 with "
            + "no activity id", StringComparison.Ordinal));

        // One completed call failed. One completed call's stop carried no status, so it is unknown, never a success. The
        // stop without its start reports a failure too, and is stated rather than counted: its call is not complete here.
        MetricResult errors = Evaluate(session.Store, Metric.Errors);
        Assert.Equal(1, errors.Value);
        Assert.Equal((3L, 1L), (errors.KnownContributions, errors.UnknownContributions));
        Assert.Equal(0.75, errors.MeasurementAvailability);
        Assert.Equal(1, errors.Operations!.UnpairedFailures);
        Assert.Contains(errors.Caveats, caveat => caveat.Contains("1 of them report a failure status", StringComparison.Ordinal));
        Assert.Contains(errors.Caveats, caveat => caveat.Contains("1 of 4 completed calls in scope carried no status", StringComparison.Ordinal));

        // Every operation is an RPC call, so the answer states RPC's coverage rather than an all-mechanism one.
        Assert.Contains(errors.Caveats, caveat => caveat.StartsWith("RPC's capture coverage is unknown", StringComparison.Ordinal));
        Assert.Contains(errors.Caveats, caveat => caveat.Contains("never by time", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§21.1: a call from 0.5 s to 2.5 s starts in the first second and completes in the third")]
    public void ACallStartsAndCompletesInTheIntervalsItsRecordsAreIn()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, Client, 1),
            RpcCall(Second / 2, ObservationKind.RequestStart, Direction.Outbound, Client, 2, Activity(1), ServiceControl),
            RpcCall(Second * 5 / 2, ObservationKind.RequestEnd, Direction.Outbound, Client, 3, Activity(1), status: 1_722),
        ]);

        long[] Counts(Metric metric) =>
        [
            .. Enumerable.Range(0, 3).Select(bucket =>
                Evaluate(session.Store, metric, interval: new TimeRange(bucket * Second, (bucket + 1) * Second)).Value!.Value),
        ];

        Assert.Equal([1L, 0L, 0L], Counts(Metric.OperationsStarted));
        Assert.Equal([0L, 0L, 1L], Counts(Metric.OperationsCompleted));
        Assert.Equal([0L, 0L, 1L], Counts(Metric.Errors));

        // A call counted in one interval is outside another; it is never counted twice or lost between them.
        MetricResult first = Evaluate(session.Store, Metric.OperationsCompleted, interval: new TimeRange(0, Second));
        Assert.Equal((0L, 1L), (first.Value!.Value, first.ExcludedOutsideInterval));
        Assert.Contains(first.Caveats, caveat => caveat.StartsWith("No call is in scope", StringComparison.Ordinal));

        // A rate divides the whole interval, not the span between the call's records.
        MetricResult rate = Evaluate(
            session.Store, Metric.Rate, rateNumerator: Metric.OperationsStarted, interval: new TimeRange(0, 4 * Second));
        Assert.Equal((1L, 4 * Second), (rate.Rate!.Numerator, rate.Rate.IntervalTicks));
        Assert.Equal(0.25m, rate.Rate.PerSecond);
        Assert.Equal(MeasurementUnit.CountPerSecond, rate.Unit);
        Assert.Null(rate.Value);

        MetricResult noInterval = Evaluate(session.Store, Metric.Rate, rateNumerator: Metric.OperationsStarted);
        Assert.Equal(MetricUnavailableReason.NoInterval, noInterval.Unavailable);
    }

    [Fact(DisplayName = "R22: owner(P) keeps the calls P made or served, and a grouping by process partitions the count")]
    public void OwnerAndProcessGroupingReadTheCallsBindings()
    {
        using var session = new TemporarySession();
        Publish(session.Store, EveryState());
        ProcessInstanceId client = InstanceOf(session.Store, Client);
        ProcessInstanceId server = InstanceOf(session.Store, Server);

        MetricResult clientStarts = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with { Owner = client });
        Assert.Equal((4L, 2L), (clientStarts.Value!.Value, clientStarts.ExcludedByProcessFilter));
        Assert.Contains(clientStarts.Caveats, caveat => caveat.Contains($"owner instance {client}", StringComparison.Ordinal));

        MetricResult serverCompletions = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsCompleted) with { Owner = server });
        Assert.Equal((2L, 4L), (serverCompletions.Value!.Value, serverCompletions.ExcludedByProcessFilter));

        MetricResult absent = SessionMetrics.Evaluate(
            session.Store, Request(Metric.OperationsStarted) with { Owner = new ProcessInstanceId(Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100")) });
        Assert.Equal(MetricUnavailableReason.ProcessInstanceNotFound, absent.Unavailable);

        // Grouped by process, each group is the ungrouped count over its own calls, so the groups add up to the total.
        MetricResult byProcess = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.True(byProcess.GroupsPartitionTotal);
        Assert.Equal(6, byProcess.Value);
        Assert.Equal(
            [(1, Client, 4L), (2, Server, 2L)],
            byProcess.Groups.Select(group => (group.Rank!.Value, group.Process!.ProcessId, group.Value!.Value)));
        Assert.Equal(4L, byProcess.Groups[0].Bindings[RelationStrength.Correlated]);
        Assert.Empty(byProcess.Unattributed);
        Assert.Equal(byProcess.Value, byProcess.Groups.Sum(group => group.Value));

        // A process whose completed calls all succeeded failed none: an observed zero, ranked. One whose completed calls
        // said nothing of how they ended is unmeasured, and sorts after every ranked group rather than at zero.
        using var mixed = new TemporarySession();
        Publish(mixed.Store,
        [
            .. EveryState(),
            Lifecycle(3, ObservationKind.Create, 600, 90),
            RpcCall(800, ObservationKind.RequestStart, Direction.Outbound, 600, 91, Activity(20), ServiceControl),
            RpcCall(900, ObservationKind.RequestEnd, Direction.Outbound, 600, 92, Activity(20), status: null),
        ]);
        MetricResult errors = SessionMetrics.Evaluate(mixed.Store, Request(Metric.Errors) with { Grouping = LaneGrouping.InstanceOnly, RequestedRows = 5 });
        Assert.Equal(
            [(1, Client, (long?)1L), (2, Server, 0L)],
            errors.Groups.Where(group => group.Rank is not null).Select(group => (group.Rank!.Value, group.Process!.ProcessId, group.Value)));
        MetricGroup unmeasured = Assert.Single(errors.Groups, group => group.Rank is null);
        Assert.Equal((600, (long?)null, 0L, 1L), (unmeasured.Process!.ProcessId, unmeasured.Value, unmeasured.KnownContributions, unmeasured.UnknownContributions));
        Assert.Null(errors.Remainder);

        MetricResult top = SessionMetrics.Evaluate(mixed.Store, Request(Metric.OperationsCompleted) with { Grouping = LaneGrouping.InstanceOnly, RequestedRows = 1 });
        // The client and the server completed two calls each; the tie breaks on the instance's identity, never on order.
        MetricGroup kept = Assert.Single(top.Groups);
        Assert.Equal(2L, kept.Value);
        Assert.Contains(kept.Process!.ProcessId, new[] { Client, Server });
        Assert.Equal((2, (long?)3L), (top.Remainder!.GroupsMerged, top.Remainder.Value));
        Assert.Equal(top.Value, kept.Value + top.Remainder.Value);

        MetricResult byMechanism = SessionMetrics.Evaluate(mixed.Store, Request(Metric.OperationsCompleted) with { Grouping = LaneGrouping.Mechanism });
        MetricGroup rpc = Assert.Single(byMechanism.Groups);
        Assert.Equal((Mechanism.Rpc, (long?)5L), (rpc.Mechanism!.Value, rpc.Value));
    }

    [Fact(DisplayName = "R3: an error count over calls none of which says how it ended is unmeasured, never zero")]
    public void ErrorsOverCallsWithNoStatusAreUnmeasured()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, Client, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, Client, 2, Activity(1), ServiceControl),
            RpcCall(200, ObservationKind.RequestEnd, Direction.Outbound, Client, 3, Activity(1), status: null),
        ]);

        Assert.Equal(1, Evaluate(session.Store, Metric.OperationsCompleted).Value);
        MetricResult errors = Evaluate(session.Store, Metric.Errors);
        Assert.Equal(MetricUnavailableReason.NothingMeasured, errors.Unavailable);
        Assert.Null(errors.Value);
        Assert.Equal((0L, 1L), (errors.KnownContributions, errors.UnknownContributions));
        Assert.Contains("never as zero", errors.UnavailableExplanation!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I16: an operation answer names the operation rule and the binding rule in its identity")]
    public void AnOperationAnswerNamesItsRules()
    {
        using var session = new TemporarySession();
        Publish(session.Store, EveryState());

        QueryIdentity started = SessionMetrics.Identify(session.Store, Request(Metric.OperationsStarted))!;
        Assert.Contains(
            $"\"versions\":{{\"normalizerContract\":1,\"entityRevision\":\"{ProcessInstanceIndex.BindingRule}\","
            + $"\"correlationRevision\":\"{RpcCallIndex.OperationRule}\",\"metricsContract\":\"metrics-v1\"}}",
            started.CanonicalSpecification,
            StringComparison.Ordinal);

        // With no process filter or grouping the evidence policy decides nothing, so it is not written; with one, it is.
        Assert.DoesNotContain("evidencePolicy", started.CanonicalSpecification, StringComparison.Ordinal);
        ProcessInstanceId client = InstanceOf(session.Store, Client);
        QueryIdentity owned = SessionMetrics.Identify(session.Store, Request(Metric.OperationsStarted) with { Owner = client })!;
        Assert.Contains("\"evidencePolicy\":\"IncludeCorrelated\"", owned.CanonicalSpecification, StringComparison.Ordinal);

        // Started and completed counts are two questions, and the source basis never answers either.
        Assert.NotEqual(started.Hash, SessionMetrics.Identify(session.Store, Request(Metric.OperationsCompleted))!.Hash);
        Assert.Equal(started, Evaluate(session.Store, Metric.OperationsStarted).Identity);
    }

    [Fact(DisplayName = "§9: an operation count's evidence is the record that put each counted call in scope, in segment order")]
    public void EvidenceListsTheCountedRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, EveryState(), rowsPerSegment: 4);

        MetricResult completed = SessionMetrics.Evaluate(
            session.Store, Request(Metric.OperationsCompleted), new() { EvidenceLimit = 3 });
        Assert.Equal(4, completed.Value);
        Assert.Equal([180L, 200L, 260L], completed.Evidence.Select(item => item.Observation.NativeTicks));
        Assert.All(completed.Evidence, item => Assert.Equal(ObservationKind.RequestEnd, item.Observation.Kind));
        Assert.Equal(2, completed.Evidence.Select(item => item.Segment).Distinct().Count());

        MetricResult failed = SessionMetrics.Evaluate(session.Store, Request(Metric.Errors), new() { EvidenceLimit = 10 });
        MetricEvidence stop = Assert.Single(failed.Evidence);
        Assert.Equal((400L, 1_722L), (stop.Observation.NativeTicks, stop.Observation.StatusCode!.Value));
    }

    [Fact(DisplayName = "P4: what no derived operation can answer is unavailable with its reason, never a zero")]
    public void WhatNoOperationAnswersIsUnavailable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, EveryState());
        ProcessInstanceId client = InstanceOf(session.Store, Client);
        ProcessInstanceId server = InstanceOf(session.Store, Server);

        MetricResult bytes = SessionMetrics.Evaluate(session.Store, Request(Metric.BytesSent) with
        {
            ByteDomain = ByteDomain.TransportObserved,
            AccountingSide = AccountingSide.SendSide,
        });
        Assert.Equal(MetricUnavailableReason.NothingMeasured, bytes.Unavailable);
        Assert.Contains("carry no length", bytes.UnavailableExplanation!, StringComparison.Ordinal);
        Assert.NotNull(bytes.Identity);

        foreach ((MetricRequest request, string says) in new (MetricRequest, string)[]
        {
            (DurationRequest(DurationInterval.IoCompletion), "No IoCompletion operation is derived"),
            (Request(Metric.ActiveChannels), "connection incarnations"),
            (Request(Metric.ActivePeers) with { Owner = client }, "this capture collected no ALPC"),
            (Request(Metric.Errors) with { AccountingSide = AccountingSide.SendSide }, "no call is accounted to a side"),
            (Request(Metric.OperationsStarted) with { Participant = client }, "participant(P) needs the process at a call's other end"),
            (Request(Metric.OperationsStarted) with { Sender = client }, "sender(P) selects data that flowed one way"),
            (Request(Metric.OperationsStarted) with { Between = new([client], [server], BetweenDirection.FirstToSecond) }, "A directional between(A,B)"),
            (Request(Metric.OperationsStarted) with { Owner = client, Peer = server }, "peer(P,Q) needs"),
            (Request(Metric.OperationsStarted) with { Between = new([client], [server], BetweenDirection.Either) }, "between(A,B) needs"),
            (Request(Metric.OperationsStarted) with { Owner = client, Grouping = LaneGrouping.Peer }, "Grouping by peer needs"),
            (Request(Metric.OperationsStarted) with { Mechanism = Mechanism.Tcp }, "No correlator derives Tcp operations"),
            (Request(Metric.OperationsStarted) with { Layer = ObservationLayer.Transport }, "No Transport-layer operation"),
        })
        {
            MetricResult result = SessionMetrics.Evaluate(session.Store, request);
            Assert.Equal(MetricUnavailableReason.NoLogicalOperations, result.Unavailable);
            Assert.Contains(says, result.UnavailableExplanation!, StringComparison.Ordinal);
            Assert.Null(result.Value);
        }

        // The projection RPC calls are in keeps every one of them.
        MetricResult projected = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with
        {
            Mechanism = Mechanism.Rpc,
            Layer = ObservationLayer.Application,
        });
        Assert.Equal(6, projected.Value);
        Assert.DoesNotContain(projected.Caveats, caveat => caveat.Contains("No other mechanism", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§19.2: a duration measures one named interval over one cohort, and a censored call gets no duration")]
    public void ADurationMeasuresOneIntervalOverOneCohort()
    {
        using var session = new TemporarySession();
        Publish(session.Store, EveryState());

        // Client calls completed in scope: two measured, 10 µs each (100 ticks of 100 ns). The stop whose start came
        // before the capture and the stop with no activity id are in the cohort and have no duration; the server's calls
        // measure another named interval and are left out.
        MetricResult client = Duration(session.Store, DurationInterval.ClientCall);
        Assert.True(client.IsAvailable, client.UnavailableExplanation);
        Assert.Equal((10_000L, MeasurementUnit.Nanoseconds), (client.Value!.Value, client.Unit!.Value));
        Assert.Equal(new MetricDistribution(2, 10_000, 10_000, 10_000, 10_000, 20_000, 20_000), client.Distribution);
        Assert.Equal((2L, 2L, 2L), (client.KnownContributions, client.UnknownContributions, client.ExcludedByProjection));
        Assert.Equal(ObservationKind.RequestEnd, client.Operations!.CountedRecord);
        Assert.Contains(client.Caveats, caveat => caveat.Contains("left-censored", StringComparison.Ordinal));
        Assert.Contains(client.Caveats, caveat => caveat.Contains("measure server execution, another named interval", StringComparison.Ordinal));

        // The calls started in scope: the call still open at capture end is right-censored, never counted as short.
        MetricResult started = Duration(session.Store, DurationInterval.ClientCall, OperationCohort.StartedInRange);
        Assert.Equal(ObservationKind.RequestStart, started.Operations!.CountedRecord);
        Assert.Equal((2L, 2L), (started.KnownContributions, started.UnknownContributions));
        Assert.Equal(1, started.Operations.InScope[RpcCallState.OpenAtCaptureEnd]);
        Assert.Contains(started.Caveats, caveat => caveat.Contains("right-censored", StringComparison.Ordinal));

        // Served calls: 3 µs and 1 µs. The median, the 95th percentile and the maximum are each a duration a call took.
        MetricResult median = Duration(session.Store, DurationInterval.ServerExecution);
        Assert.Equal(1_000, median.Value);
        Assert.Equal(3_000, Duration(session.Store, DurationInterval.ServerExecution, statistic: DurationStatistic.Percentile95).Value);
        Assert.Equal(3_000, Duration(session.Store, DurationInterval.ServerExecution, statistic: DurationStatistic.Maximum).Value);
        Assert.Equal((4_000L, 4_000L), (median.Distribution!.SummedNanoseconds, median.Distribution.BusyNanoseconds));

        // Scope comes from the counted record: the cohort completed in [0, 19 µs) holds only the served call ending at 18 µs.
        // The interval is read before the side, so four stops fall outside it and the client's stop at 5 µs is the other
        // named interval.
        MetricResult early = SessionMetrics.Evaluate(session.Store, DurationRequest(DurationInterval.ServerExecution) with
        {
            Interval = new TimeRange(0, 190),
        });
        Assert.Equal((3_000L, 4L, 1L), (early.Value!.Value, early.ExcludedOutsideInterval, early.ExcludedByProjection));
    }

    [Fact(DisplayName = "§19.2: calls that ran at once sum to more than the time they kept their side busy")]
    public void OverlappingCallsSumToMoreThanTheirBusyTime()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, Server, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Inbound, Server, 2, Activity(1), ServiceControl),
            RpcCall(150, ObservationKind.RequestStart, Direction.Inbound, Server, 3, Activity(2), ServiceControl),
            RpcCall(200, ObservationKind.RequestEnd, Direction.Inbound, Server, 4, Activity(1), status: 0),
            RpcCall(250, ObservationKind.RequestEnd, Direction.Inbound, Server, 5, Activity(2), status: 0),
        ]);

        MetricDistribution distribution = Duration(session.Store, DurationInterval.ServerExecution).Distribution!;
        Assert.Equal((20_000L, 15_000L), (distribution.SummedNanoseconds, distribution.BusyNanoseconds));
        Assert.Equal(MetricUnavailableReason.NothingMeasured, Duration(session.Store, DurationInterval.ClientCall).Unavailable);
    }

    [Fact(DisplayName = "§19.2: durations grouped by process rank by their statistic, and a remainder is its calls' own distribution")]
    public void GroupedDurationsRankByTheirStatistic()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. EveryState(),
            Lifecycle(3, ObservationKind.Create, 600, 90),
            RpcCall(800, ObservationKind.RequestStart, Direction.Outbound, 600, 91, Activity(20), ServiceControl),
            RpcCall(1_300, ObservationKind.RequestEnd, Direction.Outbound, 600, 92, Activity(20), status: 0),
            RpcCall(900, ObservationKind.RequestStart, Direction.Outbound, 600, 93, Activity(21), ServiceControl),
            RpcCall(910, ObservationKind.RequestEnd, Direction.Outbound, 600, 94, Activity(21), status: 0),
        ]);

        // PID 600's calls took 50 µs and 1 µs, the client's 10 µs twice: by the maximum PID 600 ranks first, by the median
        // the client does, because a median is a duration a call took, never an average.
        MetricResult byMaximum = SessionMetrics.Evaluate(session.Store, DurationRequest(DurationInterval.ClientCall) with
        {
            Statistic = DurationStatistic.Maximum,
            Grouping = LaneGrouping.InstanceOnly,
        });
        Assert.Equal([(600, 50_000L), (Client, 10_000L)], byMaximum.Groups.Select(group => (group.Process!.ProcessId, group.Value!.Value)));
        Assert.False(byMaximum.GroupsPartitionTotal);
        Assert.Contains(byMaximum.Caveats, caveat => caveat.Contains("does not add", StringComparison.Ordinal));

        MetricResult byMedian = SessionMetrics.Evaluate(session.Store, DurationRequest(DurationInterval.ClientCall) with
        {
            Grouping = LaneGrouping.InstanceOnly,
            RequestedRows = 1,
        });
        MetricGroup first = Assert.Single(byMedian.Groups);
        Assert.Equal((Client, 10_000L), (first.Process!.ProcessId, first.Value!.Value));

        // The remainder is the distribution of the calls it merges: PID 600's two, so its median is 1 µs.
        Assert.Equal((1, (long?)1_000L, 2L), (byMedian.Remainder!.GroupsMerged, byMedian.Remainder.Value, byMedian.Remainder.Distribution!.Count));
    }

    [Fact(DisplayName = "§5: a duration names its interval, and one no operation measures is unavailable, never another renamed")]
    public void ADurationNamesItsInterval()
    {
        MetricRejection unnamed = Assert.IsType<MetricRejection>(Request(Metric.Duration).Check());
        Assert.Contains("There is no default", unnamed.Reason, StringComparison.Ordinal);
        MetricRejection notADuration = Assert.IsType<MetricRejection>((Request(Metric.OperationsStarted) with
        {
            Cohort = OperationCohort.StartedInRange,
        }).Check());
        Assert.Contains("is not a duration", notADuration.Reason, StringComparison.Ordinal);
        MetricRejection lifetime = Assert.IsType<MetricRejection>(DurationRequest(DurationInterval.MappingLifetime).Check());
        Assert.Contains("resource-topology basis", lifetime.Reason, StringComparison.Ordinal);
        MetricRejection onTopology = Assert.IsType<MetricRejection>((DurationRequest(DurationInterval.ClientCall) with
        {
            Basis = AnalysisBasis.ResourceTopology,
        }).Check());
        Assert.Contains("an operation's", onTopology.Reason, StringComparison.Ordinal);

        using var session = new TemporarySession();
        Publish(session.Store, EveryState());
        MetricResult waits = SessionMetrics.Evaluate(session.Store, DurationRequest(DurationInterval.Wait));
        Assert.Equal(MetricUnavailableReason.NoLogicalOperations, waits.Unavailable);
        Assert.Contains("No Wait operation is derived", waits.UnavailableExplanation!, StringComparison.Ordinal);

        // The defaults are written out, so leaving them implicit and naming them are one question (§10.5).
        QueryIdentity implicitly = SessionMetrics.Identify(session.Store, DurationRequest(DurationInterval.ClientCall))!;
        QueryIdentity explicitly = SessionMetrics.Identify(session.Store, DurationRequest(DurationInterval.ClientCall) with
        {
            Cohort = OperationCohort.CompletedInRange,
            Statistic = DurationStatistic.Median,
        })!;
        Assert.Equal(implicitly, explicitly);
        Assert.Contains(
            "\"metric\":\"Duration\",\"durationInterval\":\"ClientCall\",\"cohort\":\"CompletedInRange\",\"statistic\":\"Median\"",
            implicitly.CanonicalSpecification,
            StringComparison.Ordinal);
        Assert.NotEqual(implicitly.Hash, SessionMetrics.Identify(session.Store, DurationRequest(DurationInterval.ServerExecution))!.Hash);
    }

    [Fact(DisplayName = "P8: over random calls, each count equals the calls whose counted record is in scope, derived independently")]
    public void RandomCallsAreCountedAsAnOracleCountsThem()
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var random = new Random(seed);
            (List<ObservationRowV1> rows, List<PlannedCall> planned) = RandomCalls(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var interval = new TimeRange(from, from + random.Next(1, (int)end + 1));
            int owner = random.Next(2) == 0 ? Client : Server;
            ProcessInstanceId instance = InstanceOf(session.Store, owner);

            foreach (TimeRange? scope in new TimeRange?[] { null, interval })
            {
                foreach (int? pid in new int?[] { null, owner })
                {
                    IEnumerable<PlannedCall> kept = planned.Where(call => pid is null || call.Pid == pid);
                    bool Inside(long? ticks) => ticks is { } at && (scope is not { } range || range.Contains(at));
                    long started = kept.Count(call => Inside(call.Start));
                    long completed = kept.Count(call => call.Activity is not null && call.Start is not null && Inside(call.Stop));
                    long failed = kept.Count(call => call.Activity is not null && call.Start is not null && Inside(call.Stop)
                        && call.Status is not (null or 0));

                    MetricRequest Scoped(Metric metric) => Request(metric) with
                    {
                        Interval = scope,
                        Owner = pid is null ? null : instance,
                    };

                    string context = $"seed {seed}, interval {scope}, owner {pid}";
                    Assert.True(started == SessionMetrics.Evaluate(session.Store, Scoped(Metric.OperationsStarted)).Value, context);
                    Assert.True(completed == SessionMetrics.Evaluate(session.Store, Scoped(Metric.OperationsCompleted)).Value, context);
                    MetricResult errors = SessionMetrics.Evaluate(session.Store, Scoped(Metric.Errors));
                    Assert.True(failed == (errors.Value ?? 0), context);

                    MetricResult grouped = SessionMetrics.Evaluate(session.Store, Scoped(Metric.OperationsCompleted) with
                    {
                        Grouping = LaneGrouping.InstanceOnly,
                        RequestedRows = 1,
                    });
                    Assert.True(
                        completed == grouped.Groups.Concat(grouped.Unattributed).Sum(group => group.Value ?? 0) + (grouped.Remainder?.Value ?? 0),
                        context);

                    // Each side's durations over the calls completed in scope: the median is the duration by nearest rank.
                    foreach ((DurationInterval named, int side) in new[] { (DurationInterval.ClientCall, Client), (DurationInterval.ServerExecution, Server) })
                    {
                        long[] durations =
                        [
                            .. kept
                                .Where(call => call.Pid == side && call.Activity is not null && call.Start is not null && Inside(call.Stop))
                                .Select(call => (call.Stop!.Value - call.Start!.Value) * 100)
                                .Order(),
                        ];
                        long? median = durations.Length == 0
                            ? null
                            : durations[(int)Math.Max(0, (((durations.Length * 50L) + 99) / 100) - 1)];
                        MetricResult duration = SessionMetrics.Evaluate(session.Store, Scoped(Metric.Duration) with { DurationInterval = named });
                        Assert.True(median == duration.Value, $"{context}, {named}: {median} and {duration.Value}");
                        Assert.True(durations.Length == (duration.Distribution?.Count ?? 0), context);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Calls in every state, on a client and a server. The client: a call that succeeded, one that failed, one still open
    /// at capture end, a stop whose start came before the capture, and a start and a stop with no activity id. The
    /// server: two served calls, one of whose stops carried no status.
    /// </summary>
    private static List<ObservationRowV1> EveryState() =>
    [
        Lifecycle(1, ObservationKind.Create, Client, 1),
        Lifecycle(2, ObservationKind.Create, Server, 2),
        RpcCall(50, ObservationKind.RequestEnd, Direction.Outbound, Client, 3, Activity(4), status: 5),
        RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, Client, 4, Activity(1), ServiceControl),
        RpcCall(150, ObservationKind.RequestStart, Direction.Inbound, Server, 5, Activity(5), ServiceControl),
        RpcCall(180, ObservationKind.RequestEnd, Direction.Inbound, Server, 6, Activity(5), status: 0),
        RpcCall(200, ObservationKind.RequestEnd, Direction.Outbound, Client, 7, Activity(1), status: 0),
        RpcCall(250, ObservationKind.RequestStart, Direction.Inbound, Server, 8, Activity(6), ServiceControl),
        RpcCall(260, ObservationKind.RequestEnd, Direction.Inbound, Server, 9, Activity(6), status: null),
        RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, Client, 10, Activity(2), ServiceControl),
        RpcCall(400, ObservationKind.RequestEnd, Direction.Outbound, Client, 11, Activity(2), status: 1_722),
        RpcCall(500, ObservationKind.RequestStart, Direction.Outbound, Client, 12, Activity(3), ServiceControl),
        RpcCall(600, ObservationKind.RequestStart, Direction.Outbound, Client, 13, activity: null, ServiceControl),
        RpcCall(700, ObservationKind.RequestEnd, Direction.Outbound, Client, 14, activity: null, status: 0),
    ];

    /// <summary>One call as a random capture plans it, before any record of it is written.</summary>
    private sealed record PlannedCall(int Pid, Guid? Activity, long? Start, long? Stop, long? Status);

    /// <summary>
    /// Random calls on a client and a server: each with a distinct activity id or none, some with no start or no stop,
    /// and a status that is success, a failure or absent. Records interleave in time, so a pairing by proximity would
    /// fail where the activity id does not.
    /// </summary>
    private static (List<ObservationRowV1> Rows, List<PlannedCall> Calls) RandomCalls(Random random)
    {
        var rows = new List<(ObservationRowV1 Row, long Ticks)>
        {
            (Lifecycle(0, ObservationKind.Create, Client, 1), 0),
            (Lifecycle(0, ObservationKind.Create, Server, 2), 0),
        };
        var calls = new List<PlannedCall>();
        ulong ordinal = 10;
        int count = random.Next(1, 40);
        for (int index = 0; index < count; index++)
        {
            int pid = random.Next(2) == 0 ? Client : Server;
            Direction direction = pid == Client ? Direction.Outbound : Direction.Inbound;
            Guid? activity = random.Next(8) == 0 ? null : Activity(100 + index);
            long startTicks = random.Next(1, 1_000);
            long stopTicks = startTicks + random.Next(0, 300);
            bool hasStart = random.Next(6) != 0;
            bool hasStop = !hasStart || random.Next(6) != 0;
            long? status = random.Next(5) switch
            {
                0 => null,
                1 => 1_722,
                _ => 0,
            };
            ulong startOrdinal = ++ordinal;
            ulong stopOrdinal = ++ordinal;
            if (hasStart)
            {
                rows.Add((RpcCall(startTicks, ObservationKind.RequestStart, direction, pid, startOrdinal, activity, ServiceControl), startTicks));
            }

            if (hasStop)
            {
                rows.Add((RpcCall(stopTicks, ObservationKind.RequestEnd, direction, pid, stopOrdinal, activity, status: status), stopTicks));
            }

            calls.Add(new(pid, activity, hasStart ? startTicks : null, hasStop ? stopTicks : null, hasStop ? status : null));
        }

        return ([.. rows.OrderBy(entry => entry.Ticks).ThenBy(entry => entry.Row.RawRecordOrdinal).Select(entry => entry.Row)], calls);
    }

    private static ProcessInstanceId InstanceOf(SessionStore store, int pid)
    {
        MetricResult grouped = SessionMetrics.Evaluate(store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Observations,
            Grouping = LaneGrouping.InstanceOnly,
        });
        return grouped.Groups.Single(group => group.Process?.ProcessId == pid).Process!.Id;
    }

    private static MetricRequest Request(Metric metric) => new()
    {
        Basis = AnalysisBasis.LogicalOperations,
        Metric = metric,
    };

    private static MetricRequest DurationRequest(DurationInterval interval) => Request(Metric.Duration) with
    {
        DurationInterval = interval,
    };

    private static MetricResult Duration(
        SessionStore store,
        DurationInterval interval,
        OperationCohort? cohort = null,
        DurationStatistic? statistic = null) =>
        SessionMetrics.Evaluate(store, DurationRequest(interval) with { Cohort = cohort, Statistic = statistic });

    private static MetricResult Evaluate(
        SessionStore store,
        Metric metric,
        Metric? rateNumerator = null,
        TimeRange? interval = null) =>
        SessionMetrics.Evaluate(store, Request(metric) with { RateNumerator = rateNumerator, Interval = interval });

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
}
