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
/// An RPC channel's rung and a process's HTTP exchanges' rung export what they list (§6.4, R18): every call or exchange of
/// the scope, not only the pages listed so far, as <c>icat export</c> reaches and writes them, and say when there is none.
/// </summary>
public sealed class ChannelRungExportTests
{
    private static readonly DateTimeOffset Exported = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R18: an RPC channel's and HTTP exchanges' rung export every call and exchange of the scope, as icat export does")]
    public void AChannelsRungExportsItsCallsAndExchanges() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        ChannelRungCardTests.PublishClient(session);
        using WorkspaceViewModel workspace = HostEdgeRowsTests.Open(session);
        string[] process = await OpenClient(workspace);
        foreach ((string mechanism, string label, string detail) in ((string, string, string)[])
        [
            ("Rpc", "RPC call at +0.000007 s", "200 ns · procedure 7 · succeeded"),
            ("Http", "HTTP exchange at +0.000008 s", "200 ns · exchange 1 · request 181 B · response 115 B + 7 B"),
        ])
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Source.Mechanism.ToString() == mechanism);
            string[] path = [.. process, workspace.SelectedRung.Key];
            Assert.True(workspace.Descend());
            await workspace.RpcReady;
            await workspace.HttpReady;
            Assert.True(workspace.CanExport);
            foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
            {
                SessionExportResult desktop = await workspace.ExportAsync(format, Exported);
                Assert.Equal((1, true), (desktop.Rows, desktop.Context.Complete));
                Assert.Equal(SessionExport.Build(session.Store, new(path, null, false, format), Exported).Content, desktop.Content);
            }

            using (JsonDocument json = JsonDocument.Parse((await workspace.ExportAsync(ExportFormat.Json, Exported)).Content))
            {
                JsonElement row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
                Assert.Equal((workspace.RungRows[0].Key, label, detail, "Evidence"),
                    (row.GetProperty("key").GetString(), row.GetProperty("label").GetString(), row.GetProperty("detail").GetString(),
                        row.GetProperty("descendsTo").GetString()));
            }

            // Its records, as E lists them, are icat export's at the same rung too.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Assert.Equal(SessionExport.Build(session.Store, new(path, null, true, ExportFormat.Json), Exported).Content,
                (await workspace.ExportAsync(ExportFormat.Json, Exported)).Content);
            Assert.True(workspace.Ascend());
            Assert.True(workspace.Ascend());
            await workspace.RpcReady;
            await workspace.HttpReady;
        }
    });

    [Fact(DisplayName = "R21: a channel's rung that has listed no call or exchange yet, holds none in scope, or could not read them exports nothing and says which")]
    public void AChannelsRungWithNothingToExportSaysWhy() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        ChannelRungCardTests.PublishClient(session);
        var before = new TimeRange(50, 61);
        WorkspaceNavigationMemento? rpcRung = null;
        foreach ((Mechanism mechanism, string row) in ((Mechanism, string)[])[(Mechanism.Rpc, "call"), (Mechanism.Http, "exchange")])
        {
            using WorkspaceViewModel workspace = HostEdgeRowsTests.Open(session);
            string[] process = await OpenClient(workspace);

            // Before its rows are read the rung lists none yet.
            workspace.SelectedRung = workspace.RungRows.Single(candidate => candidate.Source.Mechanism == mechanism);
            string[] path = [.. process, workspace.SelectedRung.Key];
            Assert.True(workspace.Descend());
            Assert.False(workspace.CanExport);
            Assert.Equal($"No {row} is listed here yet, so there is nothing to export.", workspace.ExportTip);
            await workspace.RpcReady;
            await workspace.HttpReady;
            Assert.True(workspace.CanExport);
            rpcRung ??= workspace.CaptureNavigation();

            // An interval before its one call or exchange holds none, nor does icat export's file of the same rung.
            workspace.SelectInterval(before);
            await workspace.IntervalReady;
            await workspace.RpcReady;
            await workspace.HttpReady;
            Assert.False(workspace.CanExport);
            Assert.Equal($"No {row} of this channel is in this scope, so there is nothing to export.", workspace.ExportTip);
            Assert.Equal(workspace.ExportTip, workspace.ShareReportTip);
            SessionExportResult none = await workspace.ExportAsync(ExportFormat.Json, Exported);
            Assert.Equal(0, none.Rows);
            Assert.Equal(SessionExport.Build(session.Store, new(path, before, false, ExportFormat.Json), Exported).Content, none.Content);
        }

        // A rung whose calls could not be read says so, and an export refuses in the same words.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var unread = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(Path.Combine(session.Path, "gone"), overview.SessionId, overview.Generation));
        Assert.Null(unread.RestoreNavigation(rpcRung!));
        await unread.RpcReady;
        Assert.False(unread.CanExport);
        Assert.Equal("This channel's calls could not be read, so there is nothing to export.", unread.ExportTip);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unread.ExportAsync(ExportFormat.Json, Exported));
        Assert.Equal(unread.ExportTip, refused.Message);
    });

    [Fact(DisplayName = "R18: a channel's export stops at its limit and says so, the rest left to a brushed interval or icat export --limit")]
    public void AChannelsExportSaysWhatItsLimitLeftOut() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields,
            coverage: RpcLedger(alpc: true));
        using WorkspaceViewModel workspace = HostEdgeRowsTests.Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
        foreach (string key in (string[])[client.GroupKey, client.Id.ToString()])
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        workspace.SelectedRung = Assert.Single(workspace.RungRows, row => row.Source.Mechanism == Mechanism.Rpc);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        workspace.ListedExportLimit = 2;
        SessionExportResult limited = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal((2, false), (limited.Rows, limited.Context.Complete));
        Assert.Contains("The export holds this scope's first 2 calls, leaving the rest out; narrow it with a brushed interval, or "
            + "use icat export --limit for them.", limited.Context.Caveats);
        workspace.ListedExportLimit = 3;
        SessionExportResult whole = await workspace.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal((3, true), (whole.Rows, whole.Context.Complete));
    });

    /// <summary>Opens client.exe's process rung once its rows are read; the path icat export takes to it.</summary>
    private static async Task<string[]> OpenClient(WorkspaceViewModel workspace)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
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
