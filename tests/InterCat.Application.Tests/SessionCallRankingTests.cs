using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The ranked table's RPC call ranking counts what `icat metric --basis logical-operations` counts for every process,
/// split by the side each call was made on: a client's calls made and a server's calls served, completed and counted by
/// their stop, with the failed ones beside them and the stops paired with no start stated, never counted (R18, §8a).
/// </summary>
public sealed class SessionCallRankingTests
{
    private const int Client = 100;
    private const int Server = 200;
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "R18: each process's calls made and served add up to what the operations metric counts for it, whole or in an interval")]
    public void CallsAreTheOperationsMetricsCalls()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var random = new Random(seed);
            (List<ObservationRowV1> rows, List<(int Pid, Guid? Activity, long? Start, long? Stop, long? Status)> planned) = RandomCalls(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var presentation = new TimeRange(from, from + random.Next(1, (int)end + 1));

            foreach (TimeRange? scope in new TimeRange?[] { null, presentation })
            {
                SessionCallMeasures measured = SessionCallRanking.Measure(session.Store, scope);
                string context = $"seed {seed}, interval {scope}";
                bool Inside(long? ticks) => ticks is { } at && (scope is not { } range || range.Contains(at));

                // An independent oracle from the plan: completed is a start and a stop sharing an activity id, counted by
                // the stop; any other stop in scope is unpaired.
                foreach (int pid in new[] { Client, Server })
                {
                    var side = planned.Where(call => call.Pid == pid && Inside(call.Stop)).ToList();
                    long completed = side.Count(call => call.Activity is not null && call.Start is not null);
                    long failed = side.Count(call => call.Activity is not null && call.Start is not null && call.Status is not (null or 0));
                    long unpaired = side.Count - completed;
                    ProcessCalls calls = measured.ByProcess.Values.SingleOrDefault(value => (pid == Client ? value.Made + value.MadeUnpaired : value.Served + value.ServedUnpaired) > 0)
                        ?? ProcessCalls.None;
                    (long, long, long) actual = pid == Client
                        ? (calls.Made, calls.MadeFailed, calls.MadeUnpaired)
                        : (calls.Served, calls.ServedFailed, calls.ServedUnpaired);
                    Assert.True((completed, failed, unpaired) == actual, $"{context}, PID {pid}: plan {(completed, failed, unpaired)}, ranking {actual}");
                }

                // And the metric grouped by process: each process's completed calls, both sides together.
                MetricResult grouped = SessionMetrics.Evaluate(session.Store, new MetricRequest
                {
                    Basis = AnalysisBasis.LogicalOperations,
                    Metric = Metric.OperationsCompleted,
                    Grouping = LaneGrouping.InstanceOnly,
                    Interval = scope,
                });
                foreach (MetricGroup group in grouped.Groups.Where(group => group.Process is not null))
                {
                    ProcessCalls calls = measured.ByProcess.GetValueOrDefault(group.Process!.Id) ?? ProcessCalls.None;
                    Assert.True(group.Value == calls.Made + calls.Served, $"{context}: {group.Process.Id} metric {group.Value}, ranking {calls.Made + calls.Served}");
                }
            }
        }
    }

    [Fact(DisplayName = "R18: each process's RPC errors are what the errors metric grouped by process answers, unknown status apart")]
    public void ErrorsAreTheErrorsMetrics()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var random = new Random(seed);
            (List<ObservationRowV1> rows, _) = RandomCalls(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var presentation = new TimeRange(from, from + random.Next(1, (int)end + 1));

            foreach (TimeRange? scope in new TimeRange?[] { null, presentation })
            {
                SessionCallMeasures measured = SessionCallRanking.Measure(session.Store, scope);
                MetricResult grouped = SessionMetrics.Evaluate(session.Store, new MetricRequest
                {
                    Basis = AnalysisBasis.LogicalOperations,
                    Metric = Metric.Errors,
                    Grouping = LaneGrouping.InstanceOnly,
                    Interval = scope,
                });
                string context = $"seed {seed}, interval {scope}";
                foreach (MetricGroup group in grouped.Groups.Where(group => group.Process is not null))
                {
                    RankedValue value = (measured.ByProcess.GetValueOrDefault(group.Process!.Id) ?? ProcessCalls.None).Of(RankingMetric.RpcErrors);
                    Assert.True(
                        (group.Value, group.KnownContributions, group.UnknownContributions) == (value.Value, value.Measured, value.Unmeasured),
                        $"{context}: {group.Process.Id} metric ({group.Value}, {group.KnownContributions}, {group.UnknownContributions}) "
                            + $"and ranking ({value.Value}, {value.Measured}, {value.Unmeasured})");
                }
            }
        }
    }

    [Fact(DisplayName = "R18: each process's RPC call and serve times are what the duration metric grouped by process answers, whole or in an interval")]
    public void TimesAreTheDurationMetrics()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var random = new Random(seed);
            (List<ObservationRowV1> rows, _) = RandomCalls(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long from = random.Next(0, (int)end);
            var presentation = new TimeRange(from, from + random.Next(1, (int)end + 1));

            foreach (TimeRange? scope in new TimeRange?[] { null, presentation })
            {
                SessionCallMeasures measured = SessionCallRanking.Measure(session.Store, scope);
                string context = $"seed {seed}, interval {scope}";
                foreach ((DurationInterval interval, RankingMetric metric) in new[]
                {
                    (DurationInterval.ClientCall, RankingMetric.RpcCallTime),
                    (DurationInterval.ServerExecution, RankingMetric.RpcServeTime),
                })
                {
                    MetricResult grouped = SessionMetrics.Evaluate(session.Store, Duration(interval, LaneGrouping.InstanceOnly, scope));
                    foreach (MetricGroup group in grouped.Groups.Where(group => group.Process is not null))
                    {
                        ProcessInstanceId id = group.Process!.Id;
                        RankedValue value = (measured.TimesByProcess.GetValueOrDefault(id) ?? CallTimes.None)
                            .Of(metric, measured.ByProcess.GetValueOrDefault(id) ?? ProcessCalls.None);
                        Assert.True(
                            (group.Value, group.KnownContributions, group.UnknownContributions) == (value.Value, value.Measured, value.Unmeasured),
                            $"{context}, {interval}: {id} metric ({group.Value}, {group.KnownContributions}, {group.UnknownContributions}) "
                                + $"and ranking ({value.Value}, {value.Measured}, {value.Unmeasured})");
                    }
                }
            }
        }
    }

    [Fact(DisplayName = "R18: a group's RPC call time is its members' calls together, as the duration metric grouped by executable or by session answers")]
    public void AGroupsTimeIsItsMembersCallsTogether()
    {
        // Two instances of caller.exe: one's calls took 1, 1 and 9 ticks, the other's 5 and 7. Their medians are 1 and 5,
        // and together the five calls' median is 5 - neither medians' sum nor their mean.
        ObservationRowV1 first = Lifecycle(1, ObservationKind.Create, Client, 1) with { ResourceName = @"C:\Tools\caller.exe" };
        ObservationRowV1 second = Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\caller.exe" };
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            first, second,
            .. Call(Client, 10, 11, 1), .. Call(Client, 20, 21, 2), .. Call(Client, 30, 39, 3),
            .. Call(101, 40, 45, 4), .. Call(101, 50, 57, 5),
        ], fields: [Field(first, SourceField.ProcessSessionId, 1), Field(second, SourceField.ProcessSessionId, 2)]);
        SessionCallMeasures measured = SessionCallRanking.Measure(session.Store, null);
        MetricResult grouped = SessionMetrics.Evaluate(session.Store, Duration(DurationInterval.ClientCall, LaneGrouping.Executable, null));
        MetricGroup caller = Assert.Single(grouped.Groups, group => group.Executable is not null);
        CallTimes together = measured.TimesByGroup[@"executable:C:\TOOLS\CALLER.EXE"];
        Assert.Equal((caller.Value, caller.KnownContributions), (together.MadeMedian, together.MadeTimed));
        Assert.Equal((5L, 500L), (together.MadeTimed, together.MadeMedian!.Value));

        // The instances ran in terminal sessions 1 and 2: each session's time is its own instance's calls, under the key
        // the window's session groups have, as the metric grouped by session answers.
        MetricResult bySession = SessionMetrics.Evaluate(session.Store, Duration(DurationInterval.ClientCall, LaneGrouping.UserSession, null));
        Assert.Equal([1u, 2u], bySession.Groups.Select(group => group.TerminalSession!.Value).Order());
        foreach (MetricGroup group in bySession.Groups)
        {
            CallTimes own = measured.TimesByGroup[WorkspaceGrouping.SessionKey(group.TerminalSession)];
            Assert.Equal((group.Value, group.KnownContributions), (own.MadeMedian, own.MadeTimed));
        }

        Assert.Equal((3L, 100L), (measured.TimesByGroup["session:1"].MadeTimed, measured.TimesByGroup["session:1"].MadeMedian!.Value));

        // The ladder's group row reads that median, not one made from its members'.
        WorkspaceSnapshot counted = OverviewWorkspace.WithCalls(
            OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store)), measured);
        LadderRow row = Assert.Single(LadderProjection.Project(counted, SyntheticWorkspace.Root(counted), RankingMetric.RpcCallTime).Rows);
        Assert.Equal((500L, 5L, 0L), (row.Ranked!.Value, row.Ranked.Measured, row.Ranked.Unmeasured));

        static ObservationRowV1[] Call(int pid, long start, long stop, int number) =>
        [
            RpcCall(start, ObservationKind.RequestStart, Direction.Outbound, pid, (ulong)(100 + (2 * number)), Activity(number), ServiceControl),
            RpcCall(stop, ObservationKind.RequestEnd, Direction.Outbound, pid, (ulong)(101 + (2 * number)), Activity(number), status: 0),
        ];
    }

    [Fact(DisplayName = "§19.5: call times over a group and the machine leave out the instances a view sets aside, which keep their own")]
    public void TimesLeaveOutWhatIsSetAside()
    {
        // The caller.exe instance in terminal session 2 made the calls of 5 and 7 ticks; set aside, its group's and the
        // machine's median are the other instance's three calls', 1, 1 and 9 ticks, and its session's group has none.
        ObservationRowV1 first = Lifecycle(1, ObservationKind.Create, Client, 1) with { ResourceName = @"C:\Tools\caller.exe" };
        ObservationRowV1 second = Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\caller.exe" };
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            first, second,
            .. Call(Client, 10, 11, 1), .. Call(Client, 20, 21, 2), .. Call(Client, 30, 39, 3),
            .. Call(101, 40, 45, 4), .. Call(101, 50, 57, 5),
        ], fields: [Field(first, SourceField.ProcessSessionId, 1), Field(second, SourceField.ProcessSessionId, 2)]);
        ProcessNode other = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store)).Processes
            .Single(process => process.ProcessId == 101);
        SessionCallMeasures all = SessionCallRanking.Measure(session.Store, null);
        SessionCallMeasures aside = SessionCallRanking.Measure(session.Store, null, setAside: new HashSet<ProcessInstanceId> { other.Id });

        Assert.Equal(all.ByProcess, aside.ByProcess);
        Assert.Equal(all.TimesByProcess[other.Id], aside.TimesByProcess[other.Id]);
        Assert.Equal((5L, 500L), (all.TimesAttributed.MadeTimed, all.TimesAttributed.MadeMedian!.Value));
        Assert.Equal((3L, 100L), (aside.TimesAttributed.MadeTimed, aside.TimesAttributed.MadeMedian!.Value));
        CallTimes group = aside.TimesByGroup[@"executable:C:\TOOLS\CALLER.EXE"];
        Assert.Equal((3L, 100L), (group.MadeTimed, group.MadeMedian!.Value));
        Assert.True(all.TimesByGroup.ContainsKey("session:2"));
        Assert.False(aside.TimesByGroup.ContainsKey("session:2"));
        Assert.Equal(all.TimesByGroup["session:1"], aside.TimesByGroup["session:1"]);

        static ObservationRowV1[] Call(int pid, long start, long stop, int number) =>
        [
            RpcCall(start, ObservationKind.RequestStart, Direction.Outbound, pid, (ulong)(100 + (2 * number)), Activity(number), ServiceControl),
            RpcCall(stop, ObservationKind.RequestEnd, Direction.Outbound, pid, (ulong)(101 + (2 * number)), Activity(number), status: 0),
        ];
    }

    [Fact(DisplayName = "§8a: calls made and served rank their own sides, and a process with only unpaired stops follows those with calls")]
    public void CallsRankByTheirSide()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Named());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        SessionCallMeasures measured = SessionCallRanking.Measure(session.Store, null);
        Assert.Null(measured.Unavailable);
        WorkspaceSnapshot counted = OverviewWorkspace.WithCalls(whole, measured);
        NavigationState root = SyntheticWorkspace.Root(counted);

        // The caller made three calls, one of which failed; the orphan's only stop began before the capture.
        LadderView made = LadderProjection.Project(counted, root, RankingMetric.RpcCallsMade);
        (string, long?, long, long, long?)[] expectedMade =
        [
            ("caller.exe", 3, 3, 0, 1),
            ("orphan.exe", null, 0, 1, 0),
            ("idle.exe", null, 0, 0, 0),
            ("service.exe", null, 0, 0, 0),
        ];
        Assert.Equal(expectedMade.Take(2), made.Rows.Take(2).Select(row =>
            (row.Label, row.Ranked!.Value, row.Ranked.Measured, row.Ranked.Unmeasured, row.Ranked.Failed)));
        Assert.All(made.Rows.Skip(2), row => Assert.False(row.Ranked!.Holds));

        // The service served two calls; nobody else served any.
        LadderView served = LadderProjection.Project(counted, root, RankingMetric.RpcCallsServed);
        Assert.Equal(("service.exe", 2L, 0L), (served.Rows[0].Label, served.Rows[0].Ranked!.Value!.Value, served.Rows[0].Ranked!.Failed!.Value));
        Assert.All(served.Rows.Skip(1), row => Assert.False(row.Ranked!.Holds));

        // Within [10, 25) only the first call's stop and the first served call's stop are in scope.
        SessionCallMeasures early = SessionCallRanking.Measure(session.Store, new TimeRange(10, 25));
        ProcessNode caller = whole.Processes.Single(node => node.ProcessId == Client);
        Assert.Equal(new ProcessCalls(1, 0, 0, 0, 0, 0), early.ByProcess[caller.Id]);
    }

    [Fact(DisplayName = "R21: when the capture did not collect RPC, no call ranks the rows, and the export says why")]
    public void UncollectedRpcRanksNothing()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Named();
        Publish(session.Store, rows, coverage: TcpOnlyLedger(rows.Max(row => row.NativeTicks)));

        SessionCallMeasures measured = SessionCallRanking.Measure(session.Store, null);
        Assert.Equal(CoverageState.NotCollected, measured.Coverage.State);
        Assert.StartsWith("this capture did not collect RPC over this scope", measured.Unavailable, StringComparison.Ordinal);
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        LadderView view = LadderProjection.Project(OverviewWorkspace.WithCalls(whole, measured), SyntheticWorkspace.Root(whole),
            RankingMetric.RpcCallsMade);
        Assert.All(view.Rows, row => Assert.Null(row.Ranked));

        SessionExportResult export = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.RpcCallsMade), DateTimeOffset.UnixEpoch);
        Assert.Equal(RankingMetric.Records, export.Context.RankedBy);
        Assert.Contains(export.Context.Caveats, caveat =>
            caveat.StartsWith("RPC calls were asked to rank the rows, but this capture did not collect RPC", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R18: an export ranked by calls served names its ranking, and each row its calls, failures and unpaired stops")]
    public void AnExportRankedByCallsNamesItsRanking()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Named());

        SessionExportResult json = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.RpcCallsMade), DateTimeOffset.UnixEpoch);
        Assert.Equal(RankingMetric.RpcCallsMade, json.Context.RankedBy);
        Assert.Contains(json.Context.Caveats, caveat => caveat.StartsWith("Rows are ranked by RPC calls made", StringComparison.Ordinal));
        using (JsonDocument document = JsonDocument.Parse(json.Content))
        {
            Assert.Equal("rpc-calls-made", document.RootElement.GetProperty("rankedBy").GetString());
            JsonElement first = document.RootElement.GetProperty("rows")[0];
            Assert.Equal("caller.exe", first.GetProperty("label").GetString());
            JsonElement ranked = first.GetProperty("ranked");
            Assert.Equal((3L, 3L, 0L, 1L), (ranked.GetProperty("value").GetInt64(), ranked.GetProperty("measured").GetInt64(),
                ranked.GetProperty("unmeasured").GetInt64(), ranked.GetProperty("failed").GetInt64()));
        }

        string[] lines = Csv.Lines(SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Csv, RankBy: RankingMetric.RpcCallsServed), DateTimeOffset.UnixEpoch).Content);
        int rankedBy = Array.IndexOf(Csv.Cells(lines[0]), "ranked_by");
        Assert.Equal(["rpc-calls-served", "2", "2", "0", "0"], Csv.Cells(lines[1])[rankedBy..(rankedBy + 5)]);
    }

    /// <summary>
    /// Four executables: caller.exe makes three calls to service.exe, which serves two of them (the third's server side
    /// was not captured); one of the three fails. orphan.exe has one stop whose start came before the capture, and
    /// idle.exe only exists.
    /// </summary>
    private static ObservationRowV1[] Named() =>
    [
        Lifecycle(1, ObservationKind.Create, Client, 1) with { ResourceName = @"C:\Tools\caller.exe" },
        Lifecycle(2, ObservationKind.Create, Server, 2) with { ResourceName = @"C:\Tools\service.exe" },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\orphan.exe" },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\idle.exe" },
        RpcCall(10, ObservationKind.RequestStart, Direction.Outbound, Client, 10, Activity(1), ServiceControl),
        RpcCall(11, ObservationKind.RequestStart, Direction.Inbound, Server, 11, Activity(2), ServiceControl),
        RpcCall(18, ObservationKind.RequestEnd, Direction.Inbound, Server, 12, Activity(2), status: 0),
        RpcCall(20, ObservationKind.RequestEnd, Direction.Outbound, Client, 13, Activity(1), status: 0),
        RpcCall(30, ObservationKind.RequestStart, Direction.Outbound, Client, 14, Activity(3), ServiceControl),
        RpcCall(31, ObservationKind.RequestStart, Direction.Inbound, Server, 15, Activity(4), ServiceControl),
        RpcCall(38, ObservationKind.RequestEnd, Direction.Inbound, Server, 16, Activity(4), status: 0),
        RpcCall(40, ObservationKind.RequestEnd, Direction.Outbound, Client, 17, Activity(3), status: 1_722),
        RpcCall(50, ObservationKind.RequestStart, Direction.Outbound, Client, 18, Activity(5), ServiceControl),
        RpcCall(60, ObservationKind.RequestEnd, Direction.Outbound, Client, 19, Activity(5), status: 0),
        RpcCall(70, ObservationKind.RequestEnd, Direction.Outbound, 300, 20, Activity(6), status: 0),
    ];

    /// <summary>An imported trace that collected TCP and process lifecycle over its readings, and no RPC.</summary>
    private static CoverageLedgerV1 TcpOnlyLedger(long last) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [new CoverageEpochV1
        {
            Epoch = 1,
            Acquisition = CoverageAcquisition.EtlImport,
            FirstDeliveredNativeTicks = 0,
            LastDeliveredNativeTicks = last,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 1, Version = 4, Mechanism = Mechanism.ProcessLifecycle },
            ],
            Deliveries = [new CoverageDeliveryV1
            {
                ProviderId = ProcessProvider,
                EventId = 1,
                Version = 4,
                Delivered = 4,
                Admitted = 4,
                Omitted = 0,
            }],
            Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }],
        }],
    };

    /// <summary>
    /// Random calls on a client and a server, as the operations metric's oracle plans them: a distinct activity id or
    /// none, some with no start or no stop, and a status that is success, a failure or absent.
    /// </summary>
    private static (List<ObservationRowV1> Rows, List<(int Pid, Guid? Activity, long? Start, long? Stop, long? Status)> Calls) RandomCalls(Random random)
    {
        var rows = new List<(ObservationRowV1 Row, long Ticks)>
        {
            (Lifecycle(0, ObservationKind.Create, Client, 1), 0),
            (Lifecycle(0, ObservationKind.Create, Server, 2), 0),
        };
        var calls = new List<(int, Guid?, long?, long?, long?)>();
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

            calls.Add((pid, activity, hasStart ? startTicks : null, hasStop ? stopTicks : null, hasStop ? status : null));
        }

        return ([.. rows.OrderBy(entry => entry.Ticks).ThenBy(entry => entry.Row.RawRecordOrdinal).Select(entry => entry.Row)], calls);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);

    /// <summary>`icat metric --basis logical-operations --metric duration` for one named interval, by the median.</summary>
    private static MetricRequest Duration(DurationInterval interval, LaneGrouping grouping, TimeRange? scope) => new()
    {
        Basis = AnalysisBasis.LogicalOperations,
        Metric = Metric.Duration,
        DurationInterval = interval,
        Cohort = OperationCohort.CompletedInRange,
        Statistic = DurationStatistic.Median,
        Grouping = grouping,
        Interval = scope,
    };
}
