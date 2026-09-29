using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.1's metric selector: the machine and group rungs rank by records or by each process's transport bytes, read off the
/// UI thread for the scope the rows count. Until the bytes arrive the rows keep their records ranking and say why, and an
/// unmeasured row ranks after every measured one rather than as a zero (§5.2).
/// </summary>
public sealed class RankingSelectorTests
{
    private static readonly DateTimeOffset Exported = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§6.1: choosing bytes sent re-ranks the machine and group rungs once read, and says what it measured")]
    public void ChoosingBytesReRanksOnceRead() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel workspace = Open(session);
        Assert.True(workspace.ShowsRankingChoice);
        Assert.False(workspace.ShowsRankingNote);
        Assert.Equal(["listen.exe", "big.exe", "blind.exe", "zero.exe"], workspace.RungRows.Select(row => row.Label));

        // The bytes are read off the UI thread; meanwhile the rows keep ranking by records, and the note says so.
        workspace.SelectedRanking = workspace.RankingOptions.Single(option => option.Metric == RankingMetric.BytesSent);
        Assert.Equal(RankingMetric.BytesSent, workspace.RankBy);
        Assert.Equal(RankingMetric.Records, workspace.AppliedRanking);
        Assert.Equal("Reading bytes sent…", workspace.RankingNote);
        Assert.EndsWith("The rows rank by records until the bytes sent are read.", workspace.RankingNoteDetail, StringComparison.Ordinal);
        Assert.Equal("listen.exe", workspace.RungRows[0].Label);
        Assert.DoesNotContain("bytes unknown", workspace.LevelSummaryShort, StringComparison.Ordinal);
        await workspace.RankingReady;

        // Measured rows first, a measured zero among them; then the sends that recorded no size; then no send at all.
        // The total above them no longer names the paired channels' bytes, which would contradict the note.
        Assert.Equal(RankingMetric.BytesSent, workspace.AppliedRanking);
        Assert.Equal("17 observations · own records", workspace.LevelSummaryShort);
        Assert.Equal(["big.exe", "zero.exe", "blind.exe", "listen.exe"], workspace.RungRows.Select(row => row.Label));
        Assert.Equal([WorkspaceRowBuilder.DescribeSize(1_750), "0 B", "unmeasured", "no sends"],
            workspace.RungRows.Select(row => row.Figure));
        RungRow big = workspace.RungRows[0];
        Assert.Contains("· 5 records", big.DetailLine, StringComparison.Ordinal);
        Assert.Contains($"{WorkspaceRowBuilder.DescribeSize(1_750)} sent on 3 measured sends", big.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes unknown", big.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("bytes sent unmeasured, 2 sends with no size", workspace.RungRows[2].AccessibleName, StringComparison.Ordinal);
        Assert.Equal($"{WorkspaceRowBuilder.DescribeSize(1_750)} on 4 sends · 2 unmeasured", workspace.RankingNote);
        Assert.Equal(
            "Bytes sent are the transport-observed bytes of each process's own send records, sender-accounted. The rows shown "
                + $"hold {WorkspaceRowBuilder.DescribeSize(1_750)} on 4 measured sends; 2 more recorded no size. A row with none "
                + "measured ranks after every measured row, never as zero.",
            workspace.RankingNoteDetail);

        // The group rung ranks its processes the same way: here against their records, which rank 100 first.
        workspace.SelectedRung = big;
        Assert.True(workspace.Descend());
        Assert.True(workspace.ShowsRankingChoice);
        Assert.Equal([101, 100], workspace.RungRows.Select(row => Pid(workspace, row)));
        Assert.Equal([WorkspaceRowBuilder.DescribeSize(1_000), WorkspaceRowBuilder.DescribeSize(750)],
            workspace.RungRows.Select(row => row.Figure));

        // A process's rung ranks its channels by its own bytes on each; this one has no paired channel, and says so.
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        Assert.True(workspace.ShowsRankingChoice);
        Assert.Equal("No sends in scope", workspace.RankingNote);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());

        // One read answers both directions, so bytes received ranks at once; records bring the first order back.
        workspace.RankBy = RankingMetric.BytesReceived;
        Assert.Equal(RankingMetric.BytesReceived, workspace.AppliedRanking);
        Assert.Equal(("listen.exe", WorkspaceRowBuilder.DescribeSize(192)), (workspace.RungRows[0].Label, workspace.RungRows[0].Figure));
        Assert.All(workspace.RungRows.Skip(1), row => Assert.Equal("no receives", row.Figure));
        workspace.RankBy = RankingMetric.Records;
        Assert.False(workspace.ShowsRankingNote);
        Assert.Equal(["listen.exe", "big.exe", "blind.exe", "zero.exe"], workspace.RungRows.Select(row => row.Label));
        Assert.All(workspace.RungRows, row => Assert.Equal(row.Observations, row.Figure));

        // A person's export of the byte ranking is icat export --rank-by bytes-sent, byte for byte (R18).
        workspace.RankBy = RankingMetric.BytesSent;
        Assert.Equal(RankingMetric.BytesSent, workspace.AppliedRanking);
        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            Assert.Equal((await workspace.ExportAsync(format, Exported)).Content, SessionExport.Build(session.Store,
                new([], null, false, format, RankBy: RankingMetric.BytesSent), Exported).Content);
        }
    });

    [Fact(DisplayName = "§6.4: a brushed interval re-ranks by the bytes inside it, read beside its counts, so the rows change once")]
    public void ABrushReRanksByItsOwnBytesInOneStep() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel workspace = Open(session);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;

        // Every time the rows change, they are ranked by bytes: never by records while the interval's bytes are read.
        var seen = new List<(RankingMetric Applied, string First)>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.RungRows))
                seen.Add((workspace.AppliedRanking, workspace.RungRows[0].Figure));
        };

        // Inside [12, 14) big.exe sent 1,000 bytes from PID 101 and zero.exe its empty message; nobody else sent.
        var interval = new TimeRange(12, 14);
        workspace.SelectInterval(interval);
        Assert.Equal(WorkspaceRowBuilder.DescribeSize(1_750), workspace.RungRows[0].Figure);
        await workspace.IntervalReady;
        Assert.Equal(RankingMetric.BytesSent, workspace.AppliedRanking);
        Assert.Equal(["big.exe", "zero.exe"], workspace.RungRows.Take(2).Select(row => row.Label));
        Assert.Equal([WorkspaceRowBuilder.DescribeSize(1_000), "0 B", "no sends", "no sends"],
            workspace.RungRows.Select(row => row.Figure));
        Assert.All(seen, change => Assert.Equal(RankingMetric.BytesSent, change.Applied));
        Assert.Equal(
            (await workspace.ExportAsync(ExportFormat.Json, Exported)).Content,
            SessionExport.Build(session.Store, new([], interval, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported).Content);

        // Clearing the brush returns to the whole session's bytes at once, since they were read before.
        workspace.ClearSelection();
        await workspace.IntervalReady;
        Assert.Equal(RankingMetric.BytesSent, workspace.AppliedRanking);
        Assert.Equal(WorkspaceRowBuilder.DescribeSize(1_750), workspace.RungRows[0].Figure);

        // Choosing bytes while a brush is applied reads that interval's bytes, not the whole session's; an interval
        // read before answers again at once.
        workspace.RankBy = RankingMetric.Records;
        workspace.SelectInterval(new TimeRange(10, 12));
        await workspace.IntervalReady;
        workspace.RankBy = RankingMetric.BytesSent;
        Assert.Equal(RankingMetric.Records, workspace.AppliedRanking);
        await workspace.RankingReady;
        Assert.Equal(WorkspaceRowBuilder.DescribeSize(750), workspace.RungRows[0].Figure);
        workspace.SelectInterval(interval);
        await workspace.IntervalReady;
        Assert.Equal(WorkspaceRowBuilder.DescribeSize(1_000), workspace.RungRows[0].Figure);
    });

    [Fact(DisplayName = "§6.4: a live publication keeps the byte ranking, shows the last one's bytes marked, then its own")]
    public void ALivePublicationKeepsTheRankingUntilItsOwnBytesArrive() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel first = Open(session);
        first.RankBy = RankingMetric.BytesSent;
        await first.RankingReady;
        Assert.Equal(RankingMetric.BytesSent, first.CaptureNavigation().RankBy);

        // The next generation holds a large send by zero.exe, which will lead once this generation's bytes are read.
        Publish(session.Store, [Timed(Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 5_000, 200, 30))]);
        using WorkspaceViewModel second = Open(session);
        Assert.Null(second.RestoreNavigation(first.CaptureNavigation()));
        second.AdoptScope(first.CarryScope());
        Assert.Equal(RankingMetric.BytesSent, second.RankBy);
        Assert.Equal(RankingMetric.BytesSent, second.AppliedRanking);
        Assert.Equal("big.exe", second.RungRows[0].Label);
        Assert.EndsWith(" · updating", second.RankingNote, StringComparison.Ordinal);
        Assert.EndsWith("These are the previous publication's measures, shown until this one's are read.", second.RankingNoteDetail,
            StringComparison.Ordinal);

        // An export names this generation, so it waits for this generation's own bytes.
        SessionExportResult exported = await second.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent), Exported).Content, exported.Content);
        await second.RankingReady;
        Assert.DoesNotContain("updating", second.RankingNote, StringComparison.Ordinal);
        Assert.DoesNotContain("previous publication", second.RankingNoteDetail, StringComparison.Ordinal);
        Assert.Equal(("zero.exe", WorkspaceRowBuilder.DescribeSize(5_000)), (second.RungRows[0].Label, second.RungRows[0].Figure));
    });

    [Fact(DisplayName = "§6.1: the tour offers no byte ranking, and a byte figure is written in the next unit it rounds to")]
    public void TheTourOffersNoByteRankingAndSizesRoundUp()
    {
        using var tour = new WorkspaceViewModel();
        Assert.False(tour.ShowsRankingChoice);
        Assert.Equal([RankingMetric.Records], tour.RankingOptions.Select(option => option.Metric));

        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal(["0 B", "999 B", "1.0 KB", "9.9 KB", "10 KB", "999 KB", "1.0 MB", "37 MB", "1.3 GB"],
                new long[] { 0, 999, 1_000, 9_940, 9_960, 999_499, 999_999, 37_000_000, 1_300_000_000 }
                    .Select(WorkspaceRowBuilder.DescribeSize));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "§6.1: the selector lists its metrics by basis, and the basis of the chosen one stays beside it")]
    public void TheSelectorStatesEachMetricsBasis() => SingleThreadedContext.Run(() =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        using WorkspaceViewModel workspace = Open(session);

        // What each record measured, then what each RPC call, paired from its records, came to; each group headed once.
        Assert.Equal(
            [
                RankingMetric.Records, RankingMetric.BytesSent, RankingMetric.BytesReceived, RankingMetric.EndpointBytes,
                RankingMetric.ActivePeers, RankingMetric.RpcCallsMade, RankingMetric.RpcCallsServed, RankingMetric.RpcErrors,
                RankingMetric.RpcCallTime, RankingMetric.RpcServeTime,
            ],
            workspace.RankingOptions.Select(option => option.Metric));
        Assert.Equal(
            [("SOURCE OBSERVATIONS", RankingMetric.Records), ("LOGICAL OPERATIONS · RPC CALLS", RankingMetric.RpcCallsMade)],
            workspace.RankingOptions.Where(option => option.OpensBasis).Select(option => (option.BasisHeading, option.Metric)));
        Assert.All(workspace.RankingOptions, option => Assert.EndsWith(
            RankingMetrics.BasisOf(option.Metric) == AnalysisBasis.LogicalOperations ? ", on logical operations" : ", on source observations",
            option.AccessibleName, StringComparison.Ordinal));

        Assert.Equal("observations", workspace.RankingBasis);
        workspace.RankBy = RankingMetric.RpcErrors;
        Assert.Equal("operations", workspace.RankingBasis);
        Assert.StartsWith("Basis: logical operations. Each RPC call is paired from its start and stop records", workspace.RankingBasisDetail,
            StringComparison.Ordinal);
        workspace.RankBy = RankingMetric.ActivePeers;
        Assert.Equal("observations", workspace.RankingBasis);
        return Task.CompletedTask;
    });

    [Fact(DisplayName = "§8a: RPC calls made and served rank the rail by each side's completed calls, with failures and unpaired stops said")]
    public void CallsMadeAndServedRankTheRail() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        using WorkspaceViewModel workspace = Open(session);

        workspace.SelectedRanking = workspace.RankingOptions.Single(option => option.Metric == RankingMetric.RpcCallsMade);
        Assert.Equal("Reading RPC calls made…", workspace.RankingNote);
        await workspace.RankingReady;
        Assert.Equal(RankingMetric.RpcCallsMade, workspace.AppliedRanking);
        Assert.Equal(["caller.exe", "orphan.exe"], workspace.RungRows.Take(2).Select(row => row.Label));
        Assert.Equal(["3 calls", "0 completed", "no calls", "no calls"], workspace.RungRows.Select(row => row.Figure));
        Assert.Contains("3 RPC calls made, 1 failed", workspace.RungRows[0].AccessibleName, StringComparison.Ordinal);
        Assert.Contains("0 RPC calls made, 1 stop paired with no start", workspace.RungRows[1].AccessibleName, StringComparison.Ordinal);
        Assert.Equal("3 calls · 1 failed · 1 unpaired", workspace.RankingNote);
        Assert.EndsWith(
            "RPC's capture coverage over this scope is UnknownCoverage: this generation publishes no coverage ledger.",
            workspace.RankingNoteDetail, StringComparison.Ordinal);

        // One read answers both sides: the service served two calls and ranks first at once.
        workspace.RankBy = RankingMetric.RpcCallsServed;
        Assert.Equal(("service.exe", "2 calls"), (workspace.RungRows[0].Label, workspace.RungRows[0].Figure));

        // The export is icat export --rank-by rpc-calls-served's, coverage caveat included (R18).
        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            SessionExportResult desktop = await workspace.ExportAsync(format, Exported);
            Assert.Equal(desktop.Content, SessionExport.Build(session.Store,
                new([], null, false, format, RankBy: RankingMetric.RpcCallsServed), Exported).Content);
            Assert.Contains(desktop.Context.Caveats, caveat => caveat.StartsWith("RPC's capture coverage", StringComparison.Ordinal));
        }
    });

    [Fact(DisplayName = "§5.2: bytes sent and received count both directions, and RPC errors rank known failures before unknown outcomes")]
    public void EndpointBytesAndErrorsRankTheRail() => SingleThreadedContext.Run(async () =>
    {
        using (var bytes = new TemporarySession())
        {
            Publish(bytes.Store, Traffic());
            using WorkspaceViewModel workspace = Open(bytes);
            workspace.RankBy = RankingMetric.EndpointBytes;
            await workspace.RankingReady;
            Assert.Equal(["big.exe", "listen.exe", "zero.exe", "blind.exe"], workspace.RungRows.Select(row => row.Label));
            Assert.Equal([WorkspaceRowBuilder.DescribeSize(1_750), WorkspaceRowBuilder.DescribeSize(192), "0 B", "unmeasured"],
                workspace.RungRows.Select(row => row.Figure));
            Assert.Equal($"{WorkspaceRowBuilder.DescribeSize(1_942)} on 10 records · both ends · 2 unmeasured", workspace.RankingNote);
            Assert.Contains("counts a local transfer at both of its ends", workspace.RankingNoteDetail, StringComparison.Ordinal);
        }

        using var calls = new TemporarySession();
        Publish(calls.Store, Calls());
        using WorkspaceViewModel rpc = Open(calls);
        rpc.RankBy = RankingMetric.RpcErrors;
        await rpc.RankingReady;
        Assert.Equal(["caller.exe", "service.exe"], rpc.RungRows.Take(2).Select(row => row.Label));
        Assert.Equal(["1 error", "0 errors", "no calls", "no calls"], rpc.RungRows.Select(row => row.Figure));
        Assert.Contains("1 RPC error of 3 calls with a status", rpc.RungRows[0].AccessibleName, StringComparison.Ordinal);
        Assert.Equal("1 failed of 5 calls", rpc.RankingNote);
    });

    [Fact(DisplayName = "§5.2: RPC call and serve time rank the rail by each side's median call, slowest first, untimed rows after")]
    public void CallAndServeTimeRankTheRail() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        using WorkspaceViewModel workspace = Open(session);
        string Took(long nanoseconds) => OperationText.Duration(nanoseconds, CultureInfo.CurrentCulture);

        // The caller's three calls each took 1 µs; the orphan's only stop began before the capture and was never timed.
        workspace.SelectedRanking = workspace.RankingOptions.Single(option => option.Metric == RankingMetric.RpcCallTime);
        Assert.Equal("Reading RPC call times…", workspace.RankingNote);
        await workspace.RankingReady;
        Assert.Equal(RankingMetric.RpcCallTime, workspace.AppliedRanking);
        Assert.Equal(["caller.exe", "orphan.exe"], workspace.RungRows.Take(2).Select(row => row.Label));
        Assert.Equal([Took(1_000), "untimed", "no calls", "no calls"], workspace.RungRows.Select(row => row.Figure));
        Assert.Contains($"median {Took(1_000)} over 3 timed calls made", workspace.RungRows[0].AccessibleName, StringComparison.Ordinal);
        Assert.Equal("1 process instance · 3 calls timed · unknown coverage", workspace.RungRows[0].DetailLine);
        Assert.Contains("no calls made timed, 1 stop paired with no start, not timed", workspace.RungRows[1].AccessibleName,
            StringComparison.Ordinal);
        Assert.Equal($"Median {Took(1_000)} over 3 calls · 1 unpaired", workspace.RankingNote);
        Assert.StartsWith("RPC call time is how long each process's completed client calls took", workspace.RankingNoteDetail,
            StringComparison.Ordinal);

        // One read answers both sides: the service took 700 ns to serve each of its two calls.
        workspace.RankBy = RankingMetric.RpcServeTime;
        Assert.Equal(("service.exe", Took(700)), (workspace.RungRows[0].Label, workspace.RungRows[0].Figure));
        Assert.Contains($"median {Took(700)} over 2 timed calls served", workspace.RungRows[0].AccessibleName, StringComparison.Ordinal);
        Assert.Equal($"Median {Took(700)} over 2 calls", workspace.RankingNote);

        // The export is icat export --rank-by rpc-serve-time-median's (R18).
        SessionExportResult desktop = await workspace.ExportAsync(ExportFormat.Csv, Exported);
        Assert.Equal(desktop.Content, SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Csv, RankBy: RankingMetric.RpcServeTime), Exported).Content);
        Assert.Contains(",rpc-serve-time-median,700,2,0,", desktop.Content, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.1: peers rank the rail by the processes at each row's other end, and a row that resolved none follows")]
    public void PeersRankTheRail() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Hub());
        using WorkspaceViewModel workspace = Open(session);

        workspace.SelectedRanking = workspace.RankingOptions.Single(option => option.Metric == RankingMetric.ActivePeers);
        Assert.Equal("Reading peers…", workspace.RankingNote);
        await workspace.RankingReady;
        Assert.Equal(RankingMetric.ActivePeers, workspace.AppliedRanking);

        // client.exe's two instances share one peer; lone.exe's send reached no process this capture can name.
        Assert.Equal(["hub.exe", "client.exe", "other.exe", "lone.exe"], workspace.RungRows.Select(row => row.Label));
        Assert.Equal(["3 peers", "1 peer", "1 peer", "unresolved"], workspace.RungRows.Select(row => row.Figure));
        Assert.Contains("3 peers on 6 records", workspace.RungRows[0].AccessibleName, StringComparison.Ordinal);
        Assert.Contains("no peer resolved, 1 record whose other end is unresolved", workspace.RungRows[3].AccessibleName,
            StringComparison.Ordinal);
        Assert.Equal("4 processes have a peer · 1 record unresolved", workspace.RankingNote);
        Assert.StartsWith("Peers are the distinct process instances at the other end", workspace.RankingNoteDetail,
            StringComparison.Ordinal);

        // The export is icat export --rank-by active-peers's (R18).
        SessionExportResult desktop = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(desktop.Content, SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.ActivePeers), Exported).Content);
        Assert.Contains("\"rankedBy\": \"active-peers\"", desktop.Content, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.7: a ranked row says how much of what it stands for the multi-selection holds: all, some or none")]
    public void RowsStateTheirShareOfTheSelection() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Hub());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        ProcessNode[] clients = [.. workspace.Snapshot.Processes.Where(node => node.ProcessId is 200 or 201).OrderBy(node => node.ProcessId)];
        RungRow group = workspace.RungRows.Single(row => row.Key == clients[0].GroupKey);
        RungRow hub = workspace.RungRows.Single(row => row.Label == "hub.exe");
        Assert.Equal(SelectionShare.None, workspace.ShareOf(group));

        // One of client.exe's two instances, chosen from the graph: its group's row holds some of the selection.
        string first = workspace.GraphDisplay.Nodes.Single(node => node.Members.SequenceEqual([clients[0].Id])).Key;
        workspace.ToggleGraphNodeInSelection(first);
        Assert.Equal((SelectionShare.Some, SelectionShare.None), (workspace.ShareOf(group), workspace.ShareOf(hub)));

        // Both instances: the whole group.
        workspace.ToggleGraphNodeInSelection(workspace.GraphDisplay.Nodes.Single(node => node.Members.SequenceEqual([clients[1].Id])).Key);
        Assert.Equal(SelectionShare.All, workspace.ShareOf(group));

        // A plain selection ends the set, and with it every row's share.
        workspace.SelectProcess(clients[0].Id);
        Assert.Equal(SelectionShare.None, workspace.ShareOf(group));
    });

    /// <summary>
    /// hub.exe talks to three clients over paired TCP: two instances of client.exe and one of other.exe; lone.exe's one
    /// send reached no process the capture can name.
    /// </summary>
    private static ObservationRowV1[] Hub()
    {
        static ObservationRowV1[] Exchange(int client, int port, long ticks, ulong ordinal) =>
        [
            Timed(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, client, ordinal)
                .Between($"127.0.0.1:{port}", "127.0.0.1:8080")),
            Timed(Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 100, ordinal + 1)
                .Between("127.0.0.1:8080", $"127.0.0.1:{port}")),
        ];

        return
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\hub.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(3, ObservationKind.Create, 201, 3) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Tools\other.exe" }),
            Timed(Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\lone.exe" }),
            .. Exchange(200, 50_000, 10, 10),
            .. Exchange(201, 50_001, 20, 20),
            .. Exchange(300, 50_002, 30, 30),
            Timed(Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 400, 40).Between("127.0.0.1:50003", "127.0.0.1:7070")),
        ];
    }

    [Fact(DisplayName = "R21: a capture that did not collect RPC ranks by records under a call ranking, and says why")]
    public void UncollectedRpcKeepsTheRecordsRanking() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Calls();
        Publish(session.Store, rows, coverage: TcpOnlyLedger(rows.Max(row => row.NativeTicks)));
        using WorkspaceViewModel workspace = Open(session);
        string[] byRecords = [.. workspace.RungRows.Select(row => row.Key)];

        workspace.RankBy = RankingMetric.RpcCallsMade;
        await workspace.RankingReady;
        Assert.Equal(RankingMetric.Records, workspace.AppliedRanking);
        Assert.Equal(byRecords, workspace.RungRows.Select(row => row.Key));
        Assert.Equal("RPC calls made unavailable: RPC was not collected", workspace.RankingNote);
        Assert.Contains("They cannot rank the rows: this capture did not collect RPC over this scope", workspace.RankingNoteDetail,
            StringComparison.Ordinal);

        // Nothing is read again on its own, and the export ranks by records with the reason, as icat export does.
        Assert.True(workspace.RankingReady.IsCompleted);
        SessionExportResult exported = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(RankingMetric.Records, exported.Context.RankedBy);
        Assert.Equal(SessionExport.Build(session.Store,
            new([], null, false, ExportFormat.Json, RankBy: RankingMetric.RpcCallsMade), Exported).Content, exported.Content);
    });

    [Fact(DisplayName = "§6.1: at a process's rung bytes rank its TCP channels by its own bytes, and its RPC channels say they carry no size")]
    public void AProcessesChannelsRankByItsBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        Assert.True(workspace.ShowsRankingChoice);
        Assert.Equal(RankingMetric.BytesSent, workspace.AppliedRanking);
        Assert.Equal(
            [WorkspaceRowBuilder.DescribeSize(5_000), WorkspaceRowBuilder.DescribeSize(300), "no size"],
            workspace.RungRows.Select(row => row.Figure));
        Assert.Equal("RPC carries no size", workspace.RungRows[2].RankedSpoken);
        Assert.Equal($"{WorkspaceRowBuilder.DescribeSize(5_300)} on 4 sends", workspace.RankingNote);
        Assert.StartsWith("Bytes sent are the transport-observed bytes of this process's own send records on each channel",
            workspace.RankingNoteDetail, StringComparison.Ordinal);

        // The export at this rung is icat export's (R18).
        string[] path = [client.GroupKey, client.Id.ToString()];
        Assert.Equal((await workspace.ExportAsync(ExportFormat.Csv, Exported)).Content,
            SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Csv, RankBy: RankingMetric.BytesSent), Exported).Content);

        // A call ranking ranks groups and processes; here the channels, which already list their calls, rank by records.
        workspace.RankBy = RankingMetric.RpcCallsMade;
        await workspace.RankingReady;
        Assert.Equal(RankingMetric.Records, workspace.AppliedRanking);
        Assert.Equal("Channels rank by records at a process's rung", workspace.RankingNote);
        Assert.Equal([6L, 4L, 2L], workspace.RungRows.Select(row => row.Source.ObservationCount));
        Assert.All(workspace.RungRows, row => Assert.Null(row.RankedFigure));
    });

    [Fact(DisplayName = "R21: a selection's bytes are read and stated for its scope, and a relationship's are what was sent across it")]
    public void ASelectionsBytesAreReadAndStated() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        ProcessNode server = workspace.Snapshot.Processes.Single(node => node.ProcessId == 200);

        // Before anything is selected nothing is read, and no total calls the session's bytes unknown.
        Assert.DoesNotContain("bytes unknown", workspace.LevelSummaryShort, StringComparison.Ordinal);
        Assert.Equal("bytes not read", Assert.Single(workspace.Relationships).KnownBytes);
        string edge = Assert.Single(workspace.GraphDisplay.Edges).Key;
        Assert.Contains("Bytes: not read yet · selecting reads them for this scope", workspace.DescribeGraphHover(edge)!.Lines);

        // Selecting the client reads its bytes; the inspector says so meanwhile, then states them.
        workspace.SelectProcess(client.Id);
        Assert.EndsWith("reading bytes…", workspace.EvidenceSummary, StringComparison.Ordinal);
        await workspace.SelectionBytesReady;
        Assert.EndsWith($"{WorkspaceRowBuilder.DescribeSize(5_300)} sent · nothing received", workspace.EvidenceSummary, StringComparison.Ordinal);
        workspace.SelectProcess(server.Id);
        Assert.EndsWith($"nothing sent · {WorkspaceRowBuilder.DescribeSize(5_300)} received", workspace.EvidenceSummary, StringComparison.Ordinal);

        // The relationship carries what was sent across it, each transfer counted once at its sender.
        Assert.Equal($"{WorkspaceRowBuilder.DescribeSize(5_300)} sent across", Assert.Single(workspace.Relationships).KnownBytes);
        Assert.Contains($"Bytes: {WorkspaceRowBuilder.DescribeSize(5_300)} sent across, each transfer counted once at its sender",
            workspace.DescribeGraphHover(edge)!.Lines);

        // A brush reads its own bytes: inside [10, 20) only the three 100-byte messages were sent.
        workspace.SelectProcess(client.Id);
        workspace.SelectInterval(new TimeRange(10, 20));
        await workspace.IntervalReady;
        await workspace.SelectionBytesReady;
        Assert.EndsWith($"{WorkspaceRowBuilder.DescribeSize(300)} sent · nothing received", workspace.EvidenceSummary, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "R21: a channel rung states the bytes sent across it, and the tables read what a real session's timeline does not sum")]
    public void AChannelRungAndTheTablesStateBytesTruthfully() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);

        // The relationship table names each end with its PID, so one executable's instances read as different rows. The
        // interval table's timeline sums no bytes: until the table is shown and reads them, each row says they are not
        // read, never that they are unknown or none.
        RelationshipRow relationship = Assert.Single(workspace.Relationships);
        Assert.Equal(["client.exe · PID 100", "server.exe · PID 200"],
            new[] { relationship.Source, relationship.Target }.Order(StringComparer.Ordinal));
        Assert.True(workspace.IntervalTableShowsBytes);
        Assert.All(workspace.Intervals, row => Assert.Equal("bytes not read", row.KnownBytes));
        Assert.EndsWith(" · bytes not read", workspace.IntervalTableScope, StringComparison.Ordinal);

        // Showing the tables reads the bytes: the relationship and each interval say so meanwhile, then state them.
        workspace.ShowTables = true;
        Assert.Equal("reading bytes…", Assert.Single(workspace.Relationships).KnownBytes);
        Assert.All(workspace.Intervals, row => Assert.Equal("reading bytes…", row.KnownBytes));
        Assert.EndsWith(" · reading each interval's bytes…", workspace.IntervalTableScope, StringComparison.Ordinal);
        await workspace.SelectionBytesReady;
        await workspace.IntervalBytesReady;
        Assert.Equal($"{WorkspaceRowBuilder.DescribeSize(5_300)} sent across", Assert.Single(workspace.Relationships).KnownBytes);
        IntervalRow large = workspace.Intervals.Single(row => row.Interval.StartTicks == 30);
        Assert.Equal($"{WorkspaceRowBuilder.DescribeSize(5_000)} sent · no receive recorded", large.KnownBytes);
        Assert.Contains($", {WorkspaceRowBuilder.DescribeSize(5_000)} sent · no receive recorded, ", large.AccessibleName,
            StringComparison.Ordinal);
        workspace.ShowTables = false;

        // Down to the channel of the one 5,000-byte message, whose rung states what was sent across it.
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Source is { Mechanism: Mechanism.Tcp, ObservationCount: 2 });
        Assert.True(workspace.Descend());
        await workspace.SelectionBytesReady;
        Assert.Equal(
            $"2 observed records at this channel's two ends · {WorkspaceRowBuilder.DescribeSize(5_000)} sent across · no operation rung; E shows the records",
            workspace.LevelSummary);

        // The interval table still states what each interval's records sent and received, read once, while it is hidden.
        Assert.EndsWith(" · bytes each interval's records sent and received", workspace.IntervalTableScope,
            StringComparison.Ordinal);

        // A chosen interval counts the channel's own bytes, and one it sent nothing in says so rather than a zero total.
        workspace.SelectInterval(new TimeRange(10, 20));
        await workspace.IntervalReady;
        await workspace.SelectionBytesReady;
        Assert.StartsWith("0 observed records at this channel's two ends · nothing sent across · ", workspace.LevelSummary,
            StringComparison.Ordinal);
    });

    [Fact(DisplayName = "R21: a real session's interval table reads each interval's bytes over exactly the records it lists")]
    public void TheIntervalTableReadsTheBytesOfWhatItLists() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        string BytesAt(long tick) => workspace.Intervals.Single(row => row.Interval.StartTicks == tick).KnownBytes;
        string Sent(long bytes) => $"{WorkspaceRowBuilder.DescribeSize(bytes)} sent · no receive recorded";
        string Received(long bytes) => $"no send recorded · {WorkspaceRowBuilder.DescribeSize(bytes)} received";
        const string None = "no transfer recorded";

        // Every record, one tick per interval here: the client's sends, the server's receives, and RPC calls with no size.
        workspace.ShowTables = true;
        await workspace.IntervalBytesReady;
        Assert.Equal([Sent(100), Received(100), Sent(5_000), Received(5_000), None],
            new long[] { 10, 11, 30, 31, 40 }.Select(BytesAt));

        // A bucket's hover card states what the table read for the same interval and records.
        TimelineBucket bucket = workspace.WholeSnapshot.Timeline.Single(candidate => candidate.Interval.StartTicks == 30);
        Assert.Contains("Bytes: " + Sent(5_000), workspace.DescribeTimelineHover(bucket, 1).Lines);
        Assert.Contains("Bytes: not summed by the timeline · the interval table (T) reads those of what it lists",
            workspace.DescribeTimelineHover(bucket, 1, lane: Mechanism.Tcp).Lines);

        // A mechanism's lane lists its own records: the RPC lane's carry no size, TCP's the transfers.
        workspace.SelectTimelineLane(Mechanism.Rpc);
        Assert.All(workspace.Intervals, row => Assert.Equal("reading bytes…", row.KnownBytes));
        await workspace.IntervalBytesReady;
        Assert.All(workspace.Intervals, row => Assert.Equal(None, row.KnownBytes));
        workspace.SelectTimelineLane(Mechanism.Tcp);
        await workspace.IntervalBytesReady;
        Assert.Equal([Sent(100), Received(100), None], new long[] { 10, 11, 40 }.Select(BytesAt));
        workspace.SelectTimelineLane(null);
        await workspace.IntervalBytesReady;

        // Down to the client's rung, where a direction row lists the client's records of one source direction only.
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, workspace.WholeSnapshot.Timeline.Count);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        await workspace.TimelineDetailReady;
        await workspace.IntervalBytesReady;
        Assert.EndsWith(" · bytes each interval's records sent and received, every record's and not only the focus's",
            workspace.IntervalTableScope, StringComparison.Ordinal);
        workspace.SelectDirectionLane(Direction.Outbound);
        await workspace.IntervalBytesReady;
        Assert.Equal([Sent(100), None, Sent(5_000), None], new long[] { 10, 11, 30, 31 }.Select(BytesAt));
        workspace.SelectDirectionLane(Direction.Inbound);
        await workspace.IntervalBytesReady;
        Assert.All(workspace.Intervals, row => Assert.Equal(None, row.KnownBytes));

        // Down to the channel of the 5,000-byte message: its first end is the server's port 8080, which received it, and
        // its second the client's, which sent it.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Source is { Mechanism: Mechanism.Tcp, ObservationCount: 2 });
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        workspace.SelectChannelEnd(0);
        await workspace.IntervalBytesReady;
        Assert.Equal([None, Received(5_000)], new long[] { 30, 31 }.Select(BytesAt));
        workspace.SelectChannelEnd(1);
        await workspace.IntervalBytesReady;
        Assert.Equal([Sent(5_000), None], new long[] { 30, 31 }.Select(BytesAt));
        Assert.Contains("Bytes: " + Sent(5_000), workspace.DescribeTimelineHover(
            bucket, 1, endLane: workspace.TimelineChannelEndLanes!.Single(end => end.End == 1)).Lines);

        // A hidden table reads nothing more: a new listing says its bytes are not read.
        workspace.ShowTables = false;
        workspace.SelectChannelEnd(0);
        Assert.All(workspace.Intervals, row => Assert.Equal("bytes not read", row.KnownBytes));
    });

    [Fact(DisplayName = "§3.2: at a process's rung a paired channel reads by its peer, its own port first, and keeps its name")]
    public void AProcesssChannelsReadByTheirPeer() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        foreach ((int pid, string peer, bool client) in new[] { (100, "server.exe · PID 200", true), (200, "client.exe · PID 100", false) })
        {
            // The client's two connections leave from ports 50000 and 50001 to the server's 8080.
            string[] ends = [.. Enumerable.Range(50_000, 2).Select(port =>
                client ? $":{port} ↔ :8080 on 127.0.0.1" : $":8080 ↔ :{port} on 127.0.0.1")];
            ProcessNode process = workspace.Snapshot.Processes.Single(node => node.ProcessId == pid);
            while (workspace.CanAscend)
            {
                workspace.Ascend();
            }

            foreach (string key in new[] { process.GroupKey, process.Id.ToString() })
            {
                workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
                Assert.True(workspace.Descend());
            }

            await workspace.RpcReady;
            RungRow[] paired = [.. workspace.RungRows.Where(row => row.Source.Mechanism == Mechanism.Tcp)];
            Assert.All(paired, row => Assert.Equal("↔ " + peer, row.Label));
            Assert.Equal(ends, paired.Select(row => row.Detail[(row.Detail.IndexOf(" · ", StringComparison.Ordinal) + 3)..])
                .Order(StringComparer.Ordinal));

            // The channel keeps its own name where it is identified: the tooltip, the spoken row and the crumb it opens.
            RungRow first = paired[0];
            Assert.Equal($"↔ {peer}\n{first.Source.Label}", first.Tip);
            Assert.StartsWith($"Channel with {peer}, TCP · ", first.AccessibleName, StringComparison.Ordinal);
            workspace.SelectedRung = first;
            Assert.True(workspace.Descend());
            Assert.Equal($"Channel: {first.Source.Label}", workspace.Crumbs[^1].Label);
        }
    });

    [Fact(DisplayName = "R15: below the machine rung the relationship table lists what the rung's graph draws and counts the rest")]
    public void TheRelationshipTableListsWhatTheRungsGraphDraws() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. ClientAndServer(),
            Timed(Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\agent.exe" }),
            Timed(Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\store.exe" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 64, 300, 50).Between("127.0.0.1:50010", "127.0.0.1:9090")),
            Timed(Transfer(51, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 400, 51).Between("127.0.0.1:9090", "127.0.0.1:50010")),
        ]);
        using WorkspaceViewModel workspace = Open(session);
        Assert.Equal(2, workspace.Relationships.Count);
        Assert.Equal("Whole machine · 2 relationships", workspace.RelationshipTableScope);

        // At the client's rung the graph draws the client and the server; agent.exe and store.exe are counted, not listed.
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        RelationshipRow listed = Assert.Single(workspace.Relationships);
        Assert.Equal(["client.exe · PID 100", "server.exe · PID 200"], new[] { listed.Source, listed.Target }.Order(StringComparer.Ordinal));
        Assert.Equal("client.exe · PID 100 and its peers · 1 relationship · 1 more elsewhere, listed at the machine rung",
            workspace.RelationshipTableScope);

        // Back at the machine rung, every relationship is listed again.
        while (workspace.CanAscend)
        {
            workspace.Ascend();
        }

        Assert.Equal(2, workspace.Relationships.Count);
        Assert.Equal("Whole machine · 2 relationships", workspace.RelationshipTableScope);
    });

    [Fact(DisplayName = "§6.3: under a byte ranking the graph's edges and nodes are sized by bytes, so the panes agree on magnitude")]
    public void TheGraphIsSizedByTheRankingsMetric() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplayEdge byRecords = Assert.Single(workspace.GraphDisplay.Edges);
        Assert.Null(byRecords.Magnitude);
        Assert.Equal(byRecords.ObservationCount, byRecords.Weight);
        Assert.Contains(workspace.DescribeGraphHover(byRecords.Key)!.Lines,
            line => line.StartsWith("Thickness: log scale against the busiest drawn edge", StringComparison.Ordinal));

        // Ranked by bytes sent, the relationship is as thick as the 5,300 bytes sent across it, and each end as large.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        GraphDisplayEdge byBytes = Assert.Single(workspace.GraphDisplay.Edges);
        Assert.Equal(5_300, byBytes.Weight);
        Assert.Equal(byRecords.ObservationCount, byBytes.ObservationCount);
        Assert.All(workspace.GraphDisplay.Nodes.Where(node => node.Kind == GraphNodeKind.Process && node.Relationships > 0),
            node => Assert.Equal(5_300, node.Weight));
        Assert.Contains($"Thickness: bytes sent across, log scale against the busiest drawn edge, {WorkspaceRowBuilder.DescribeSize(5_300)}",
            workspace.DescribeGraphHover(byBytes.Key)!.Lines);
        Assert.Same(workspace.GraphDisplay, workspace.GraphDisplay);

        // A call ranking does not size a graph of TCP relationships: back to records.
        workspace.RankBy = RankingMetric.RpcCallsMade;
        await workspace.RankingReady;
        Assert.Null(Assert.Single(workspace.GraphDisplay.Edges).Magnitude);
    });

    [Fact(DisplayName = "R3: under a byte ranking a relationship whose sends recorded no size is drawn unmeasured, never as zero")]
    public void AnUnmeasuredRelationshipIsDrawnUnmeasured() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, MeasuredBlindAndQuiet());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplayNode Node(int pid) => workspace.GraphDisplay.Nodes.Single(node => node.ProcessId == pid);
        GraphDisplayEdge EdgeOf(int pid) => workspace.GraphDisplay.Edges.Single(edge =>
            edge.SourceKey == Node(pid).Key || edge.TargetKey == Node(pid).Key);

        // By records every mark has a size, and the legend keys no unmeasured value.
        Assert.False(workspace.GraphDrawsUnmeasured);
        var raised = new List<string?>();
        workspace.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        // By bytes sent: the client sent 500 bytes to the server; blind.exe's send recorded no size, so what it sent is
        // unknown, drawn as an open cross-hatched band rather than a hairline; quiet.exe's connection sent nothing, zero.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        Assert.Contains(nameof(WorkspaceViewModel.GraphDrawsUnmeasured), raised);
        Assert.True(workspace.GraphDrawsUnmeasured);
        Assert.Equal((500L, false), (EdgeOf(100).Weight, EdgeOf(100).Unmeasured));
        Assert.Equal((0L, true), (EdgeOf(300).Weight, EdgeOf(300).Unmeasured));
        Assert.Equal((0L, false), (EdgeOf(400).Weight, EdgeOf(400).Unmeasured));
        Assert.Equal(GraphEncoding.UnmeasuredBand, GraphEncoding.EdgeThickness(EdgeOf(300), GraphEncoding.EdgeScale(workspace.GraphDisplay)));
        Assert.Equal(500, GraphEncoding.EdgeScale(workspace.GraphDisplay));

        // A node is unknown only when none of its relationships measured a size: the server's 500 bytes are a lower bound.
        Assert.Equal((500L, false), (Node(200).Weight, Node(200).Unmeasured));
        Assert.Equal((0L, true), (Node(300).Weight, Node(300).Unmeasured));
        Assert.Equal((0L, false), (Node(400).Weight, Node(400).Unmeasured));

        // Each mark's card says which it is, as the relationship table's rows do.
        IReadOnlyList<string> blind = workspace.DescribeGraphHover(EdgeOf(300).Key)!.Lines;
        Assert.Contains("Bytes: unknown · 1 send recorded no size", blind);
        Assert.Contains("Thickness: none · its sends recorded no size, so it is drawn as an open cross-hatched band, unknown rather than zero",
            blind);
        Assert.Contains("Bytes: nothing sent across", workspace.DescribeGraphHover(EdgeOf(400).Key)!.Lines);
        Assert.Contains("Size: unknown · its relationships' sends recorded no size, so it is drawn open and cross-hatched at the "
            + "smallest size, not as zero", workspace.DescribeGraphHover(Node(300).Key)!.Lines);
        workspace.ShowTables = true;
        await workspace.SelectionBytesReady;
        string RowBytes(int pid) => workspace.Relationships.Single(row => row.Source.Contains($"PID {pid}", StringComparison.Ordinal)
            || row.Target.Contains($"PID {pid}", StringComparison.Ordinal)).KnownBytes;
        Assert.Equal(["500 B sent across", "no size measured", "nothing sent across"], [RowBytes(100), RowBytes(300), RowBytes(400)]);

        // Back to records, nothing is unmeasured.
        workspace.RankBy = RankingMetric.Records;
        await workspace.RankingReady;
        Assert.False(workspace.GraphDrawsUnmeasured);
        Assert.All(workspace.GraphDisplay.Edges, edge => Assert.False(edge.Unmeasured));
    });

    [Fact(DisplayName = "§5.2: per second states each row's value over the whole ranked interval, in its total's order, as icat metric's rate does")]
    public void PerSecondStatesTheRateOverTheRankedInterval() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);

        // At the whole session there is no interval to divide by: the rows keep their totals, and the note says why.
        Assert.True(workspace.OffersPerSecond);
        workspace.PerSecond = true;
        Assert.True(workspace.ShowsRankingNote);
        Assert.Equal("Per second needs an interval: brush one or zoom", workspace.RankingNote);
        Assert.All(workspace.RungRows, row => Assert.Null(row.RankedFigure));

        // Brushed to [10, 20), one microsecond: records per second, with each row's records still on its second line.
        var interval = new TimeRange(10, 20);
        string over = OperationText.Duration(1_000, CultureInfo.CurrentCulture);
        workspace.SelectInterval(interval);
        await workspace.IntervalReady;
        Assert.Equal("Records · per second over " + over, workspace.RankingNote);
        RungRow first = workspace.RungRows[0];
        Assert.Equal(WorkspaceRowBuilder.DescribeRate(first.Source.ObservationCount / 1e-6) + "/s", first.Figure);
        Assert.Contains($"{first.Observations} records", first.DetailLine, StringComparison.Ordinal);

        // Bytes sent per second: the client's 300 bytes over the microsecond, what icat metric's rate of bytes sent answers,
        // and the rows in the order of their totals; the server, which sent nothing, says so rather than a zero rate.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        MetricResult rate = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Rate,
            RateNumerator = Metric.BytesSent,
            ByteDomain = ByteDomain.TransportObserved,
            AccountingSide = AccountingSide.SendSide,
            Grouping = LaneGrouping.InstanceOnly,
            Interval = interval,
        });
        decimal perSecond = rate.Groups.Single(group => group.Process?.Id == client.Id).Rate!.PerSecond!.Value;
        RungRow sender = workspace.RungRows[0];
        Assert.Equal(client.GroupKey, sender.Key);
        Assert.Equal(WorkspaceRowBuilder.DescribeByteRate((double)perSecond), sender.Figure);
        Assert.Equal(WorkspaceRowBuilder.DescribeByteRate(300 / 1e-6), sender.Figure);
        Assert.Contains(WorkspaceRowBuilder.DescribeByteRate(300 / 1e-6).Replace("/s", " per second", StringComparison.Ordinal)
            + ", " + WorkspaceRowBuilder.DescribeSize(300) + " sent", sender.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("no sends", workspace.RungRows.Single(row => row.Label.StartsWith("server", StringComparison.Ordinal)).Figure);
        Assert.EndsWith(" · per second over " + over, workspace.RankingNote, StringComparison.Ordinal);
        Assert.Contains("divided by it (metrics-v1 §7)", workspace.RankingNoteDetail, StringComparison.Ordinal);

        // The choice travels with the ranking to the next publication.
        Assert.True(workspace.CaptureNavigation().PerSecond);

        // A median has no rate: the choice steps aside, and the rows state their medians.
        workspace.RankBy = RankingMetric.RpcCallTime;
        await workspace.RankingReady;
        Assert.False(workspace.OffersPerSecond);
        Assert.DoesNotContain("per second", workspace.RankingNote, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "R3: a whole session states per second over its recording when its capture recorded a stop, as icat metric's rate does")]
    public void AWholeSessionStatesPerSecondOverItsRecording() => SingleThreadedContext.Run(async () =>
    {
        // The capture's calibration read its clock 5 ticks in and again at its stop, 2 s in: its recording is those 2 s.
        using var session = new TemporarySession();
        Publish(session.Store, ClientAndServer(), calibration: new ClockCalibrationV1
        {
            Contract = ClockCalibrationV1.ContractName,
            CaptureId = TestSessions.Capture.Value,
            ClockId = TestClock.Id.Value,
            WallClock = "test-wall-clock",
            Samples =
            [
                new() { NativeTicks = 5, Utc = Exported, AcquisitionUncertaintyNanoseconds = 200 },
                new() { NativeTicks = 20_000_000, Utc = Exported.AddSeconds(2), AcquisitionUncertaintyNanoseconds = 200 },
            ],
        });
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);

        // The inspector's time scope is the recording, which the rates divide by, not the span of the records in it.
        Assert.Equal("All · " + WorkspaceTime.FormatDuration(20_000_000, CultureInfo.CurrentCulture) + " recorded",
            workspace.IntervalLabel);

        // At the whole session the rows state records per second over those 2 s, each with its records still beneath.
        string over = OperationText.Duration(2_000_000_000, CultureInfo.CurrentCulture);
        workspace.PerSecond = true;
        Assert.Equal("Records · per second over the whole recording, " + over, workspace.RankingNote);
        Assert.Contains("from the capture's start to the stop its clock calibration records", workspace.RankingNoteDetail,
            StringComparison.Ordinal);
        RungRow first = workspace.RungRows[0];
        Assert.Equal(WorkspaceRowBuilder.DescribeRate(first.Source.ObservationCount / 2.0) + "/s", first.Figure);

        // Bytes sent per second: the client's 5,300 bytes over the 2 s, what icat metric's rate answers over the recording.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        TimeRange recording = SessionRecording.NativeInterval(session.Store, session.Store.Current!, TestClock)!.Value;
        Assert.Equal(new TimeRange(0, 20_000_000), recording);
        MetricResult rate = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Rate,
            RateNumerator = Metric.BytesSent,
            ByteDomain = ByteDomain.TransportObserved,
            AccountingSide = AccountingSide.SendSide,
            Grouping = LaneGrouping.InstanceOnly,
            Interval = recording,
        });
        decimal perSecond = rate.Groups.Single(group => group.Process?.Id == client.Id).Rate!.PerSecond!.Value;
        Assert.Equal(2_650m, perSecond);
        Assert.Equal(WorkspaceRowBuilder.DescribeByteRate((double)perSecond), workspace.RungRows[0].Figure);
        Assert.EndsWith(" · per second over the whole recording, " + over, workspace.RankingNote, StringComparison.Ordinal);
    });

    /// <summary>
    /// client.exe sends the server 500 bytes; blind.exe sends it once without recording a size; quiet.exe's connection to it
    /// carries no send at either end, only receives. Each relationship is its own graph edge.
    /// </summary>
    private static ObservationRowV1[] MeasuredBlindAndQuiet() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\blind.exe" }),
        Timed(Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\quiet.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 500, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, null, 300, 12).Between("127.0.0.1:51000", "127.0.0.1:8080")),
        Timed(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:51000")),
        Timed(Transfer(14, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 400, 14).Between("127.0.0.1:52000", "127.0.0.1:8080")),
        Timed(Transfer(15, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 15).Between("127.0.0.1:8080", "127.0.0.1:52000")),
    ];

    /// <summary>
    /// A client, PID 100, and a server, PID 200, on two connections - three 100-byte messages on the first, one of 5,000
    /// bytes on the second - and two RPC calls the client makes to the service control manager.
    /// </summary>
    private static ObservationRowV1[] ClientAndServer()
    {
        Guid serviceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
        return
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
            Timed(RpcCall(40, ObservationKind.RequestStart, Direction.Outbound, 100, 40, Activity(1), serviceControl)),
            Timed(RpcCall(41, ObservationKind.RequestEnd, Direction.Outbound, 100, 41, Activity(1), status: 0)),
            Timed(RpcCall(42, ObservationKind.RequestStart, Direction.Outbound, 100, 42, Activity(2), serviceControl)),
            Timed(RpcCall(43, ObservationKind.RequestEnd, Direction.Outbound, 100, 43, Activity(2), status: 0)),
        ];
    }

    /// <summary>
    /// caller.exe makes three calls to service.exe, which serves two of them; one of the three fails. orphan.exe has one
    /// stop whose start came before the capture, and idle.exe only exists.
    /// </summary>
    private static ObservationRowV1[] Calls()
    {
        Guid serviceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
        return
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\caller.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\service.exe" }),
            Timed(Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\orphan.exe" }),
            Timed(Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\idle.exe" }),
            Timed(RpcCall(10, ObservationKind.RequestStart, Direction.Outbound, 100, 10, Activity(1), serviceControl)),
            Timed(RpcCall(11, ObservationKind.RequestStart, Direction.Inbound, 200, 11, Activity(2), serviceControl)),
            Timed(RpcCall(18, ObservationKind.RequestEnd, Direction.Inbound, 200, 12, Activity(2), status: 0)),
            Timed(RpcCall(20, ObservationKind.RequestEnd, Direction.Outbound, 100, 13, Activity(1), status: 0)),
            Timed(RpcCall(30, ObservationKind.RequestStart, Direction.Outbound, 100, 14, Activity(3), serviceControl)),
            Timed(RpcCall(31, ObservationKind.RequestStart, Direction.Inbound, 200, 15, Activity(4), serviceControl)),
            Timed(RpcCall(38, ObservationKind.RequestEnd, Direction.Inbound, 200, 16, Activity(4), status: 0)),
            Timed(RpcCall(40, ObservationKind.RequestEnd, Direction.Outbound, 100, 17, Activity(3), status: 1_722)),
            Timed(RpcCall(50, ObservationKind.RequestStart, Direction.Outbound, 100, 18, Activity(5), serviceControl)),
            Timed(RpcCall(60, ObservationKind.RequestEnd, Direction.Outbound, 100, 19, Activity(5), status: 0)),
            Timed(RpcCall(70, ObservationKind.RequestEnd, Direction.Outbound, 300, 20, Activity(6), status: 0)),
        ];
    }

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

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static int Pid(WorkspaceViewModel workspace, RungRow row) =>
        workspace.Snapshot.Processes.Single(node => node.Id.ToString() == row.Key).ProcessId;

    /// <summary>
    /// Four executables: big.exe's two processes send 750 and 1,000 bytes, zero.exe sends an empty message, blind.exe
    /// sends twice without recording a size, and listen.exe only receives, six times, so it has the most records.
    /// </summary>
    private static ObservationRowV1[] Traffic() =>
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

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
