using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The ranked table's byte ranking reads exactly what `icat metric` answers for every process: transport-observed bytes on
/// each process's own send records under sender accounting, and on its own receive records under receiver accounting (R18).
/// </summary>
public sealed class SessionByteRankingTests
{
    [Fact(DisplayName = "R18: each process's ranked bytes are what the metric grouped by process answers, whole or in an interval")]
    public void RankedBytesAreTheMetricsBytes()
    {
        for (int seed = 0; seed < 25; seed++)
        {
            var random = new Random(seed);
            List<ObservationRowV1> rows = RandomTraffic(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long start = random.Next(0, (int)end);
            var presentation = new TimeRange(start, start + random.Next(1, (int)end + 1));

            foreach (TimeRange? scope in new TimeRange?[] { null, presentation })
            {
                SessionByteMeasures measured = SessionByteRanking.Measure(session.Store, scope);

                // Session nanoseconds are 100 native ticks here, so a presentation tick is a native one.
                foreach ((Metric metric, AccountingSide side) in new[]
                {
                    (Metric.BytesSent, AccountingSide.SendSide),
                    (Metric.BytesReceived, AccountingSide.ReceiveSide),
                })
                {
                    MetricResult grouped = SessionMetrics.Evaluate(session.Store, new MetricRequest
                    {
                        Basis = AnalysisBasis.SourceObservations,
                        Metric = metric,
                        ByteDomain = ByteDomain.TransportObserved,
                        AccountingSide = side,
                        Grouping = LaneGrouping.InstanceOnly,
                        Interval = scope,
                    });
                    RankingMetric ranking = metric == Metric.BytesSent ? RankingMetric.BytesSent : RankingMetric.BytesReceived;
                    string context = $"seed {seed}, {metric}, interval {scope}";
                    // A total none of whose contributions was measured is unavailable, and still lists its groups.
                    Dictionary<ProcessInstanceId, MetricGroup> expected = grouped.Groups
                        .Where(group => group.Process is not null)
                        .ToDictionary(group => group.Process!.Id);
                    foreach ((ProcessInstanceId instance, TransportBytes bytes) in measured.ByProcess)
                    {
                        RankedValue value = bytes.Of(ranking);
                        if (!value.Holds)
                        {
                            Assert.False(expected.ContainsKey(instance), context);
                            continue;
                        }

                        MetricGroup group = Assert.Contains(instance, (IReadOnlyDictionary<ProcessInstanceId, MetricGroup>)expected);
                        Assert.True(
                            (group.Value, group.KnownContributions, group.UnknownContributions)
                                == (value.Value, value.Measured, value.Unmeasured),
                            $"{context}: {instance} metric ({group.Value}, {group.KnownContributions}, {group.UnknownContributions}) "
                                + $"and ranking ({value.Value}, {value.Measured}, {value.Unmeasured})");
                    }

                    Assert.True(
                        expected.Keys.All(instance => measured.ByProcess.TryGetValue(instance, out TransportBytes? bytes) && bytes.Of(ranking).Holds),
                        context);
                }
            }
        }
    }

    [Fact(DisplayName = "R18: each process's bytes sent and received are what endpoint activity grouped by process answers")]
    public void EndpointBytesAreTheMetricsEndpointActivity()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            // Some records state neither side; only endpoint activity takes them.
            List<ObservationRowV1> rows = [.. RandomTraffic(random).Select(row => row.Kind is ObservationKind.Send or ObservationKind.Receive
                && random.Next(6) == 0 ? row with { AccountingSide = AccountingSide.EndpointActivity } : row)];
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(3, 40));
            long end = rows.Max(row => row.NativeTicks) + 1;
            long start = random.Next(0, (int)end);
            var presentation = new TimeRange(start, start + random.Next(1, (int)end + 1));

            foreach (TimeRange? scope in new TimeRange?[] { null, presentation })
            {
                SessionByteMeasures measured = SessionByteRanking.Measure(session.Store, scope);
                MetricResult grouped = SessionMetrics.Evaluate(session.Store, new MetricRequest
                {
                    Basis = AnalysisBasis.SourceObservations,
                    Metric = Metric.EndpointActivityBytes,
                    ByteDomain = ByteDomain.TransportObserved,
                    Grouping = LaneGrouping.InstanceOnly,
                    Interval = scope,
                });
                string context = $"seed {seed}, interval {scope}";
                Dictionary<ProcessInstanceId, MetricGroup> expected = grouped.Groups
                    .Where(group => group.Process is not null)
                    .ToDictionary(group => group.Process!.Id);
                foreach ((ProcessInstanceId instance, TransportBytes bytes) in measured.ByProcess)
                {
                    RankedValue value = bytes.Of(RankingMetric.EndpointBytes);
                    if (!value.Holds)
                    {
                        Assert.False(expected.ContainsKey(instance), context);
                        continue;
                    }

                    MetricGroup group = Assert.Contains(instance, (IReadOnlyDictionary<ProcessInstanceId, MetricGroup>)expected);
                    Assert.True(
                        (group.Value, group.KnownContributions, group.UnknownContributions) == (value.Value, value.Measured, value.Unmeasured),
                        $"{context}: {instance} metric ({group.Value}, {group.KnownContributions}, {group.UnknownContributions}) "
                            + $"and ranking ({value.Value}, {value.Measured}, {value.Unmeasured})");
                }

                Assert.True(expected.Keys.All(instance => measured.ByProcess.TryGetValue(instance, out TransportBytes? bytes)
                    && bytes.Of(RankingMetric.EndpointBytes).Holds), context);
            }
        }
    }

    [Fact(DisplayName = "§5.2: a group's bytes are its processes', and a process with nothing measured has no value, never zero")]
    public void ByteValuesAddUpAndStayUnmeasured()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1),
            Lifecycle(2, ObservationKind.Create, 200, 2),
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, owner: 100, ordinal: 10),
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 250, owner: 100, ordinal: 11),
            Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, null, owner: 200, ordinal: 12),
            Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 40, owner: null, ordinal: 13),
        ]);

        SessionByteMeasures measured = SessionByteRanking.Measure(session.Store, null);
        TransportBytes sender = measured.ByProcess.Single(entry => entry.Value.SentMeasured > 0).Value;
        Assert.Equal(new RankedValue(RankingMetric.BytesSent, 750, 2, 0), sender.Of(RankingMetric.BytesSent));
        Assert.Equal(new RankedValue(RankingMetric.BytesReceived, null, 0, 0), sender.Of(RankingMetric.BytesReceived));

        // The receiver's one receive declared its length and carried none: unmeasured, not a received zero.
        TransportBytes receiver = measured.ByProcess.Single(entry => entry.Value.ReceivedUnmeasured > 0).Value;
        RankedValue received = receiver.Of(RankingMetric.BytesReceived);
        Assert.Equal((null, 0L, 1L, true), (received.Value, received.Measured, received.Unmeasured, received.Holds));

        // A send whose owner no instance holds is counted apart, never given to a process.
        Assert.Equal((40L, 1L), (measured.Unattributed.SentBytes, measured.Unattributed.SentMeasured));
        Assert.Equal(new TransportBytes(750, 2, 0, 0, 0, 1), sender.Plus(receiver));

        // An interval no reading falls in holds nothing.
        Assert.Empty(SessionByteRanking.Measure(session.Store, new TimeRange(1_000, 2_000)).ByProcess);
    }

    [Fact(DisplayName = "§5.2: bytes rank measured rows first, largest first, then unmeasured ones, then rows with none")]
    public void BytesRankMeasuredThenUnmeasuredThenNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store, NamedTraffic());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        WorkspaceSnapshot measured = OverviewWorkspace.WithBytes(whole, SessionByteRanking.Measure(session.Store, null));
        NavigationState root = SyntheticWorkspace.Root(measured);

        // By records the listener, which only receives, is the busiest, and no row carries a ranked value.
        LadderView byRecords = LadderProjection.Project(measured, root);
        Assert.Equal(["listen.exe", "big.exe", "blind.exe", "zero.exe"], byRecords.Rows.Select(row => row.Label));
        Assert.All(byRecords.Rows, row => Assert.Null(row.Ranked));

        // By bytes sent: the measured sum first, then a measured zero, then the sends that recorded no size, then no send.
        LadderView bySent = LadderProjection.Project(measured, root, RankingMetric.BytesSent);
        (string, long?, long, long)[] sent =
        [
            ("big.exe", 1_750, 3, 0),
            ("zero.exe", 0, 1, 0),
            ("blind.exe", null, 0, 2),
            ("listen.exe", null, 0, 0),
        ];
        Assert.Equal(sent, bySent.Rows.Select(row => (row.Label, row.Ranked!.Value, row.Ranked.Measured, row.Ranked.Unmeasured)));
        Assert.Equal(byRecords.ObservationCount, bySent.ObservationCount);

        // By bytes received only the listener measured any; every other row made no receive and follows it.
        LadderView byReceived = LadderProjection.Project(measured, root, RankingMetric.BytesReceived);
        Assert.Equal(("listen.exe", 192L), (byReceived.Rows[0].Label, byReceived.Rows[0].Ranked!.Value!.Value));
        Assert.All(byReceived.Rows.Skip(1), row => Assert.False(row.Ranked!.Holds));

        // A group's value is its processes', which the group rung ranks the same way: here against their records.
        LadderRow big = bySent.Rows[0];
        NavigationState group = new DetailLadder(root) is var ladder
            && ladder.TryDescend(LadderProjection.DescentFor(big, ladder.Current, measured.Extent), out _)
                ? ladder.Current
                : throw new InvalidOperationException("The group rung refused a descent.");
        Assert.Equal([100, 101], LadderProjection.Project(measured, group).Rows.Select(row => Pid(measured, row)));
        LadderView members = LadderProjection.Project(measured, group, RankingMetric.BytesSent);
        Assert.Equal([(101, 1_000L), (100, 750L)], members.Rows.Select(row => (Pid(measured, row), row.Ranked!.Value!.Value)));

        // A rung only some of whose processes carry bytes ranks by records and shows no value.
        WorkspaceSnapshot partial = measured with
        {
            Processes = [.. measured.Processes.Select(node => node.ProcessId == 300 ? node with { Bytes = null } : node)],
        };
        LadderView unranked = LadderProjection.Project(partial, root, RankingMetric.BytesSent);
        Assert.Equal(byRecords.Rows.Select(row => row.Key), unranked.Rows.Select(row => row.Key));
        Assert.All(unranked.Rows, row => Assert.Null(row.Ranked));

        // Within an interval only its sends count: big.exe's first two.
        WorkspaceSnapshot early = OverviewWorkspace.WithBytes(whole, SessionByteRanking.Measure(session.Store, new TimeRange(10, 12)));
        LadderRow first = LadderProjection.Project(early, root, RankingMetric.BytesSent).Rows[0];
        Assert.Equal(("big.exe", 750L, 2L), (first.Label, first.Ranked!.Value!.Value, first.Ranked.Measured));
    }

    [Fact(DisplayName = "R18: an export ranked by bytes names its ranking, and each row carries the value it was ranked by")]
    public void AnExportRankedByBytesNamesItsRanking()
    {
        using var session = new TemporarySession();
        Publish(session.Store, NamedTraffic());
        var exported = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        SessionExportResult json = SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), exported);
        Assert.Equal(RankingMetric.BytesSent, json.Context.RankedBy);
        Assert.Contains(json.Context.Caveats, caveat => caveat.StartsWith("Rows are ranked by bytes sent", StringComparison.Ordinal));
        using (JsonDocument document = JsonDocument.Parse(json.Content))
        {
            Assert.Equal("bytes-sent", document.RootElement.GetProperty("rankedBy").GetString());
            JsonElement[] rows = [.. document.RootElement.GetProperty("rows").EnumerateArray()];
            Assert.Equal(["big.exe", "zero.exe", "blind.exe", "listen.exe"], rows.Select(row => row.GetProperty("label").GetString()));
            Assert.Equal(1_750, rows[0].GetProperty("ranked").GetProperty("value").GetInt64());
            Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("ranked").GetProperty("value").ValueKind);
            Assert.Equal(2, rows[2].GetProperty("ranked").GetProperty("unmeasured").GetInt64());
        }

        string[] lines = SessionExport.Build(session.Store,
                new([], null, false, ExportFormat.Csv, RankBy: RankingMetric.BytesSent), exported).Content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith("ranked_by,ranked_value,ranked_measured,ranked_unmeasured,ranked_failed", lines[0].TrimEnd('\r'), StringComparison.Ordinal);
        Assert.EndsWith(",bytes-sent,1750,3,0,", lines[1].TrimEnd('\r'), StringComparison.Ordinal);
        Assert.EndsWith(",bytes-sent,,0,2,", lines[3].TrimEnd('\r'), StringComparison.Ordinal);

        // By records nothing is ranked by value, and the export says so.
        SessionExportResult records = SessionExport.Build(session.Store, new([], null, false, ExportFormat.Json), exported);
        Assert.Equal(RankingMetric.Records, records.Context.RankedBy);
        using (JsonDocument document = JsonDocument.Parse(records.Content))
        {
            Assert.Equal("records", document.RootElement.GetProperty("rankedBy").GetString());
            Assert.All(document.RootElement.GetProperty("rows").EnumerateArray(),
                row => Assert.Equal(JsonValueKind.Null, row.GetProperty("ranked").ValueKind));
        }

        // Below the group rung the rows are channels, which rank by records whatever was asked.
        ProcessNode sender = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 100);
        SessionExportResult process = SessionExport.Build(session.Store,
            new([sender.GroupKey, sender.Id.ToString()], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), exported);
        Assert.Equal(RankingMetric.Records, process.Context.RankedBy);

        // An evidence export lists records in reading order; a ranking is refused rather than ignored.
        Assert.Throws<ArgumentException>(() => SessionExport.Build(session.Store,
            new([], null, true, ExportFormat.Json, RankBy: RankingMetric.BytesSent), exported));
    }

    /// <summary>
    /// Four executables: big.exe's two processes send 750 and 1,000 bytes, zero.exe sends an empty message, blind.exe
    /// sends twice without recording a size, and listen.exe only receives, six times, so it has the most records.
    /// </summary>
    private static ObservationRowV1[] NamedTraffic() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Tools\zero.exe" }),
        Timed(Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Tools\blind.exe" }),
        Timed(Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\listen.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10)),
        Timed(Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 250, 100, 11)),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 1_000, 101, 12)),
        Timed(Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 0, 200, 13)),
        Timed(Transfer(14, ObservationKind.Send, AccountingSide.SendSide, null, 300, 14)),
        Timed(Transfer(15, ObservationKind.Send, AccountingSide.SendSide, null, 300, 15)),
        .. Enumerable.Range(0, 6).Select(index =>
            Timed(Transfer(20 + index, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 400, (ulong)(20 + index)))),
    ];

    private static int Pid(WorkspaceSnapshot snapshot, LadderRow row) =>
        snapshot.Processes.Single(node => node.Id.ToString() == row.Key).ProcessId;

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    [Fact(DisplayName = "§5.2: at a process's rung its channels rank by its own bytes on each, which its records there measured")]
    public void AProcessesChannelsRankByItsOwnBytesOnEach()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoConnections());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        SessionByteMeasures measured = SessionByteRanking.Measure(session.Store, null);
        ProcessNode client = whole.Processes.Single(node => node.ProcessId == 100);
        ProcessNode server = whole.Processes.Single(node => node.ProcessId == 200);
        Channel busy = whole.Channels.Single(channel => channel.Name.Contains(":50000", StringComparison.Ordinal));
        Channel large = whole.Channels.Single(channel => channel.Name.Contains(":50001", StringComparison.Ordinal));

        // Each end holds its own records: the client's sends and the server's receives, on each connection.
        Assert.Equal(new TransportBytes(300, 3, 0, 0, 0, 0), measured.ByChannelEnd[new(busy.Key, client.Id)]);
        Assert.Equal(new TransportBytes(0, 0, 0, 300, 3, 0), measured.ByChannelEnd[new(busy.Key, server.Id)]);
        Assert.Equal(new TransportBytes(5_000, 1, 0, 0, 0, 0), measured.ByChannelEnd[new(large.Key, client.Id)]);
        Assert.Equal(new TransportBytes(0, 0, 0, 5_000, 1, 0), measured.ByChannelEnd[new(large.Key, server.Id)]);

        // By records the busy connection leads the client's rung; by the client's bytes sent the large one does.
        WorkspaceSnapshot counted = OverviewWorkspace.WithBytes(whole, measured);
        NavigationState rung = ProcessRung(counted, client);
        Assert.Equal([busy.Key, large.Key], LadderProjection.Project(counted, rung).Rows.Select(row => row.Key));
        LadderView sent = LadderProjection.Project(counted, rung, RankingMetric.BytesSent);
        Assert.Equal([(large.Key, 5_000L), (busy.Key, 300L)], sent.Rows.Select(row => (row.Key, row.Ranked!.Value!.Value)));

        // The client received nothing on either; the server's received bytes rank its rung the same way.
        Assert.All(LadderProjection.Project(counted, rung, RankingMetric.BytesReceived).Rows, row => Assert.False(row.Ranked!.Holds));
        LadderView received = LadderProjection.Project(counted, ProcessRung(counted, server), RankingMetric.BytesReceived);
        Assert.Equal([large.Key, busy.Key], received.Rows.Select(row => row.Key));

        // A call ranking leaves a process's channels by records: no TCP channel carries a call.
        Assert.All(LadderProjection.Project(counted, rung, RankingMetric.RpcCallsMade).Rows, row => Assert.Null(row.Ranked));

        // The export at the client's rung is ranked the same way and says what its rows' bytes are.
        SessionExportResult export = SessionExport.Build(session.Store,
            new([client.GroupKey, client.Id.ToString()], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent),
            DateTimeOffset.UnixEpoch);
        Assert.Equal(RankingMetric.BytesSent, export.Context.RankedBy);
        Assert.Contains(export.Context.Caveats, caveat => caveat.Contains("this process's own send records on each channel", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(export.Content);
        Assert.Equal([large.Key, busy.Key], document.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("key").GetString()));
    }

    private static NavigationState ProcessRung(WorkspaceSnapshot snapshot, ProcessNode process)
    {
        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        foreach (string key in new[] { process.GroupKey, process.Id.ToString() })
        {
            LadderRow row = LadderProjection.Project(snapshot, ladder.Current).Rows.Single(candidate => candidate.Key == key);
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, snapshot.Extent), out string? refusal), refusal);
        }

        return ladder.Current;
    }

    /// <summary>
    /// A client, PID 100, and a server, PID 200, on two connections: three 100-byte messages on the first and one of
    /// 5,000 bytes on the second, each sent by the client and received by the server.
    /// </summary>
    private static ObservationRowV1[] TwoConnections() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, 3).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 100, 100, (ulong)(10 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, (ulong)(11 + (2 * index)))
                .Between("127.0.0.1:8080", "127.0.0.1:50000")),
        }),
        Timed(Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 5_000, 100, 30).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 5_000, 200, 31).Between("127.0.0.1:8080", "127.0.0.1:50001")),
    ];

    /// <summary>Random sends and receives, some unmeasured and some of an owner no lifecycle names, from reused PIDs.</summary>
    private static List<ObservationRowV1> RandomTraffic(Random random)
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(0, ObservationKind.Create, 100, 1),
            Lifecycle(0, ObservationKind.Create, 200, 2),
        };
        ulong ordinal = 10;
        long ticks = 1;
        if (random.Next(2) == 0)
        {
            // PID 100 exits and is reused, so a later record of it binds as a candidate the default policy excludes.
            rows.Add(Lifecycle(50, ObservationKind.Exit, 100, ++ordinal));
            rows.Add(Lifecycle(60, ObservationKind.Create, 100, ++ordinal));
        }

        int count = random.Next(5, 80);
        for (int index = 0; index < count; index++)
        {
            ticks += random.Next(0, 5);
            bool send = random.Next(2) == 0;
            int? owner = random.Next(10) switch
            {
                0 => null,
                1 => 300,
                < 6 => 100,
                _ => 200,
            };
            long? bytes = random.Next(6) == 0 ? null : random.Next(0, 5_000);
            rows.Add(Transfer(
                ticks,
                send ? ObservationKind.Send : ObservationKind.Receive,
                send ? AccountingSide.SendSide : AccountingSide.ReceiveSide,
                bytes,
                owner,
                ++ordinal) with { SessionRelativeTicks = ticks * 100 });
        }

        return rows;
    }
}
