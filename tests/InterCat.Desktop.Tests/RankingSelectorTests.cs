using System.Globalization;
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
        Assert.Contains("bytes unknown", workspace.LevelSummaryShort, StringComparison.Ordinal);
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
