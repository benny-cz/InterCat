using System.Globalization;
using System.Text.Json;
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
/// A process's rung exports every row it lists (§6.4, R18): its connections to other hosts, its RPC channels and its HTTP
/// exchanges with its channels, in the order it lists them, as <c>icat export</c> reads them, and never fewer than it
/// lists without saying why.
/// </summary>
public sealed class ProcessRungExportTests
{
    private static readonly DateTimeOffset Exported = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R18: a process's rung exports its RPC channels and HTTP exchanges with its channels, in the order it lists them, as icat export does")]
    public void AProcessesRungExportsItsCallsAndExchanges() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        ChannelRungCardTests.PublishClient(session);
        using WorkspaceViewModel workspace = HostEdgeRowsTests.Open(session);
        string[] path = await Visit(workspace, "client.exe");
        Assert.Equal(3, workspace.RungRows.Count);
        foreach (RankingMetric metric in (RankingMetric[])[RankingMetric.Records, RankingMetric.BytesSent])
        {
            workspace.RankBy = metric;
            await workspace.RankingReady;
            foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
            {
                SessionExportResult desktop = await workspace.ExportAsync(format, Exported);
                Assert.Equal(3, desktop.Rows);
                Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, format, RankBy: metric), Exported).Content,
                    desktop.Content);
            }

            using JsonDocument json = JsonDocument.Parse((await workspace.ExportAsync(ExportFormat.Json, Exported)).Content);
            JsonElement[] rows = [.. json.RootElement.GetProperty("rows").EnumerateArray()];
            Assert.Equal(workspace.RungRows.Select(row => row.Key), rows.Select(row => row.GetProperty("key").GetString()));
            JsonElement rpc = rows.Single(row => row.GetProperty("mechanism").GetString() == "Rpc");
            Assert.Equal(("RPC calls to svcctl (Service Control Manager)", "RPC client · 1 call · median 200 ns", "Channel"),
                (rpc.GetProperty("label").GetString(), rpc.GetProperty("detail").GetString(), rpc.GetProperty("descendsTo").GetString()));
            JsonElement http = rows.Single(row => row.GetProperty("mechanism").GetString() == "Http");
            Assert.Equal(("HTTP exchanges through WinINet", "HTTP client · 1 exchange · median 200 ns"),
                (http.GetProperty("label").GetString(), http.GetProperty("detail").GetString()));

            // Under bytes sent they carry no transport bytes: ranked, with no value, after the channel that sent.
            if (metric == RankingMetric.BytesSent)
            {
                Assert.Equal("bytes-sent", json.RootElement.GetProperty("rankedBy").GetString());
                Assert.Equal("Tcp", rows[0].GetProperty("mechanism").GetString());
                Assert.All(rows[1..], row => Assert.Equal((JsonValueKind.Null, 0),
                    (row.GetProperty("ranked").GetProperty("value").ValueKind, row.GetProperty("ranked").GetProperty("measured").GetInt32())));
            }
        }

        // A brushed interval reads them for the interval, as icat export does.
        var interval = new TimeRange(50, 61);
        workspace.SelectInterval(interval);
        await workspace.IntervalReady;
        await workspace.RpcReady;
        await workspace.HttpReady;
        Assert.Equal(SessionExport.Build(session.Store, new(path, interval, false, ExportFormat.Json, RankBy: RankingMetric.BytesSent),
            Exported).Content, (await workspace.ExportAsync(ExportFormat.Json, Exported)).Content);
    });

    [Fact(DisplayName = "R18: a process's rung exports its connections to other hosts once they are read, an export waiting for them, as icat export does")]
    public void AProcessesRungExportsItsConnections() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, HostEdgeRowsTests.Rows());
        using WorkspaceViewModel workspace = HostEdgeRowsTests.Open(session);

        // browser.exe's only row is its connection to 142.250.1.1:443: until it is read the rung lists nothing to export,
        // and an export started meanwhile waits for it.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "browser.exe");
        string group = workspace.SelectedRung.Key;
        Assert.True(workspace.Descend());
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        string[] path = [group, workspace.SelectedRung.Key];
        Assert.True(workspace.Descend());
        Assert.False(workspace.CanExport);
        Assert.Equal("This rung lists no row, so there is nothing to export.", workspace.ExportTip);
        SessionExportResult early = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(1, early.Rows);
        Assert.True(workspace.CanExport);
        Assert.Equal("Export the rows this rung shows, named by their scope (Ctrl+E).", workspace.ExportTip);
        Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Json), Exported).Content, early.Content);
        using (JsonDocument json = JsonDocument.Parse(early.Content))
        {
            JsonElement row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal(("TCP to 142.250.1.1:443", "TCP from 192.168.1.10:52000 · connected in the capture, still open at its end · "
                + "200 B sent, no receive recorded", 3, 200, "Evidence"),
                (row.GetProperty("label").GetString(), row.GetProperty("detail").GetString(), row.GetProperty("observations").GetInt32(),
                    row.GetProperty("knownBytes").GetInt32(), row.GetProperty("descendsTo").GetString()));
        }

        // Its bytes rank the export by bytes, which the rung's channels, of which it has none, never could.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        SessionExportResult ranked = await workspace.ExportAsync(ExportFormat.Csv, Exported);
        Assert.Equal(RankingMetric.BytesSent, ranked.Context.RankedBy);
        Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Csv, RankBy: RankingMetric.BytesSent),
            Exported).Content, ranked.Content);

        // server.exe's channel and the connection it accepted, in the order its rung lists them; the redacted report holds
        // both, never the address.
        workspace.ReturnTo(0);
        workspace.RankBy = RankingMetric.BytesReceived;
        await workspace.RankingReady;
        path = await Visit(workspace, "server.exe");
        SessionExportResult server = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Json, RankBy: RankingMetric.BytesReceived),
            Exported).Content, server.Content);
        using (JsonDocument json = JsonDocument.Parse(server.Content))
        {
            Assert.Equal(workspace.RungRows.Select(row => row.Key),
                json.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("key").GetString()));
        }

        SessionExportResult shared = await workspace.ExportAsync(ExportFormat.Json, Exported, redacted: true);
        Assert.Equal(2, shared.Rows);
        Assert.DoesNotContain("192.168.1.30", shared.Content, StringComparison.Ordinal);

        // svc.exe's 400 holds one datagram flow, to an IPv6 host's 53, and nothing else.
        workspace.ReturnTo(0);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svc.exe");
        string svc = workspace.SelectedRung.Key;
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.Contains("400", StringComparison.Ordinal));
        path = [svc, workspace.SelectedRung.Key];
        Assert.True(workspace.Descend());
        SessionExportResult flow = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Json, RankBy: RankingMetric.BytesReceived),
            Exported).Content, flow.Content);
        using (JsonDocument json = JsonDocument.Parse(flow.Content))
        {
            Assert.Equal("UDP to [2001:db8::1]:53",
                Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray()).GetProperty("label").GetString());
        }
    });

    [Fact(DisplayName = "R21: a process's rung whose connections, RPC channels or HTTP exchanges could not be read exports nothing rather than leave them out, and says why")]
    public void AProcessesRungWhoseRowsCannotBeReadSaysSo() => SingleThreadedContext.Run(async () =>
    {
        // server.exe's channel is in the overview, but the connection it accepted could not be read.
        using var session = new TemporarySession();
        Publish(session.Store, HostEdgeRowsTests.Rows());
        using WorkspaceViewModel workspace = Unreadable(session);
        await Visit(workspace, "server.exe");
        await Refused(workspace, "connections");

        using var kinds = new TemporarySession();
        PublishKinds(kinds);
        using WorkspaceViewModel unread = Unreadable(kinds);
        await Visit(unread, "caller.exe");
        await Refused(unread, "RPC channels");
        unread.ReturnTo(0);
        await Visit(unread, "fetch.exe");
        await Refused(unread, "HTTP exchanges");

        static WorkspaceViewModel Unreadable(TemporarySession session)
        {
            SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
            return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
                new SessionEvidenceSource(Path.Combine(session.Path, "gone"), overview.SessionId, overview.Generation));
        }

        static async Task Refused(WorkspaceViewModel workspace, string rows)
        {
            Assert.False(workspace.CanExport);
            Assert.StartsWith($"This process's {rows} could not be read (", workspace.ExportTip, StringComparison.Ordinal);
            Assert.EndsWith("), so an export would leave them out.", workspace.ExportTip, StringComparison.Ordinal);
            Assert.Equal(workspace.ExportTip, workspace.ShareReportTip);
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => workspace.ExportAsync(ExportFormat.Json, Exported));
            Assert.Equal(workspace.ExportTip, refused.Message);
        }
    });

    [Fact(DisplayName = "R18: an export started while a process's rung reads its rows waits for each kind of row it holds, in words that do not follow the reader's culture")]
    public void AnExportWaitsForEachKindOfRow() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        PublishKinds(session);
        using WorkspaceViewModel workspace = HostEdgeRowsTests.Open(session);
        string[] sync = [];
        foreach ((string program, string mechanism) in ((string, string)[])
            [("caller.exe", "Rpc"), ("fetch.exe", "Http"), ("sync.exe", "Tcp")])
        {
            workspace.ReturnTo(0);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == program);
            string group = workspace.SelectedRung.Key;
            Assert.True(workspace.Descend());
            workspace.SelectedRung = Assert.Single(workspace.RungRows);
            string[] path = sync = [group, workspace.SelectedRung.Key];
            Assert.True(workspace.Descend());
            SessionExportResult early = await workspace.ExportAsync(ExportFormat.Json, Exported);
            using JsonDocument json = JsonDocument.Parse(early.Content);
            Assert.Equal(mechanism, Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray()).GetProperty("mechanism").GetString());
            Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Json), Exported).Content, early.Content);
        }

        // An interval that holds none of sync.exe's records lists none of its connections, as icat export reads it.
        var before = new TimeRange(50, 61);
        workspace.SelectInterval(before);
        await workspace.IntervalReady;
        await workspace.ConnectionsReady;
        SessionExportResult none = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(0, none.Rows);
        Assert.Equal(SessionExport.Build(session.Store, new(sync, before, false, ExportFormat.Json), Exported).Content, none.Content);
        workspace.ClearSelection();
        await workspace.IntervalReady;
        await workspace.ConnectionsReady;

        // The rung says sync.exe's 2,500 bytes as the reader's culture writes them; the file, icat export's alike, never.
        CultureInfo reader = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Contains("2.500 B sent", Assert.Single(workspace.RungRows).Detail, StringComparison.Ordinal);
            SessionExportResult file = await workspace.ExportAsync(ExportFormat.Csv, Exported);
            Assert.Contains("2,500 B sent", file.Content, StringComparison.Ordinal);
            Assert.Equal(SessionExport.Build(session.Store, new(sync, null, false, ExportFormat.Csv), Exported).Content, file.Content);
        }
        finally
        {
            CultureInfo.CurrentCulture = reader;
        }
    });

    /// <summary>
    /// caller.exe making one call to the service control manager over RPC, fetch.exe one HTTP exchange, and sync.exe one
    /// connection to another host it sent 2,500 bytes over: each process holds one kind of row and no paired channel.
    /// </summary>
    private static void PublishKinds(TemporarySession session)
    {
        Guid activity = new(1, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
        ObservationRowV1[] http = [Http(80, 2001, 10, 181), Http(81, 2003, 11, 115), Http(82, 2004, 12, 7)];
        ObservationRowV1[] rows =
        [
            Timed(Lifecycle(5, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Tools\caller.exe" }),
            Timed(Lifecycle(6, ObservationKind.Create, 400, 2) with { ResourceName = @"C:\Tools\fetch.exe" }),
            Timed(Lifecycle(7, ObservationKind.Create, 500, 3) with { ResourceName = @"C:\Tools\sync.exe" }),
            Timed(RpcCall(70, ObservationKind.RequestStart, Direction.Outbound, 300, 8, activity, ServiceControlInterface)),
            Timed(RpcCall(72, ObservationKind.RequestEnd, Direction.Outbound, 300, 9, activity, status: 0)),
            .. http,
            Timed(Transfer(90, ObservationKind.Send, AccountingSide.SendSide, 2_500, 500, 20)
                .Between("192.168.1.10:52000", "142.250.1.1:443")),
        ];
        Publish(session.Store, rows, coverage: RpcLedger(alpc: false), fields:
        [
            .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
            .. http.SelectMany(row => new[]
            {
                Field(row, SourceField.HttpExchangeId, 1),
                Field(row, SourceField.ContentBufferSequence, 0),
                Field(row, SourceField.ContentBufferFlags, 3),
            }),
        ]);
    }

    /// <summary>A WinINet record of <paramref name="eventId"/>, raised in fetch.exe's PID 400, which binds it by that header (ADR-030).</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Timed(Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 400,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        });

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>Opens <paramref name="program"/>'s one process's rung once its rows are read; the path icat export takes to it.</summary>
    private static async Task<string[]> Visit(WorkspaceViewModel workspace, string program)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == program);
        string group = workspace.SelectedRung.Key;
        Assert.True(workspace.Descend());
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        string process = workspace.SelectedRung.Key;
        Assert.True(workspace.Descend());
        await workspace.ConnectionsReady;
        await workspace.RpcReady;
        await workspace.HttpReady;
        return [group, process];
    }
}
