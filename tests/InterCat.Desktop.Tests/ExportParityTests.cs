using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using System.Text.Json;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>A person's export of one view and <c>icat export</c> of the same view are the same file (R18).</summary>
public sealed class ExportParityTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private static readonly DateTimeOffset Exported = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R18: the headless export is the Desktop's export, byte for byte, at a ranked rung and at evidence")]
    public async Task TheHeadlessExportIsTheDesktopExport()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, 150).SelectMany(index => new[]
            {
                Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                    (ulong)(100 + (2 * index))).Between(ClientEnd, ServerEnd)),
                Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                    (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd)),
            }),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        ProcessNode client = overview.Nodes.Single(node => node.ProcessId == 100);
        string[] path = [client.GroupKey, client.Id.ToString()];

        // A person selects the process's group, then the process.
        foreach (string key in path)
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            Assert.Equal((await workspace.ExportAsync(format, Exported)).Content,
                SessionExport.Build(session.Store, new(path, null, false, format), Exported).Content);
        }

        // At the evidence rung both export the whole scope: the same records, whatever the Desktop had paged in.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.CanLoadMoreEvidence);
        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            SessionExportResult desktop = await workspace.ExportAsync(format, Exported);
            Assert.Equal(150, desktop.Rows);
            Assert.Equal(desktop.Content, SessionExport.Build(session.Store, new(path, null, true, format), Exported).Content);

            SessionExportResult shared = await workspace.ExportAsync(format, Exported, redacted: true);
            SessionExportResult headlessShare = SessionExport.Build(session.Store,
                new(path, null, true, format, Redacted: true), Exported);
            Assert.Equal(desktop.Rows, shared.Rows);
            Assert.Equal(shared.Rows, headlessShare.Rows);
            Assert.Equal(shared.Context.Rung, headlessShare.Context.Rung);
            Assert.Contains(RedactedShareExport.Contract, shared.Content, StringComparison.Ordinal);
            Assert.Contains(RedactedShareExport.Contract, headlessShare.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientEnd, shared.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(ServerEnd, shared.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(overview.SessionId.ToString(), shared.Content, StringComparison.OrdinalIgnoreCase);
            if (format == ExportFormat.Json)
            {
                using JsonDocument desktopReport = JsonDocument.Parse(shared.Content);
                using JsonDocument cliReport = JsonDocument.Parse(headlessShare.Content);
                Assert.Equal(150, desktopReport.RootElement.GetProperty("records").GetArrayLength());
                Assert.Equal(150, cliReport.RootElement.GetProperty("records").GetArrayLength());
            }
        }
    }

    [Fact(DisplayName = "R18: grouped by terminal session, the window's export of the machine rung, a session's rung and its records is icat export --group-by session's")]
    public async Task AGroupedExportIsTheHeadlessExport()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(WorkspaceGrouping.Regroup(OverviewWorkspace.From(overview), LaneGrouping.UserSession),
            WorkspaceGrouping.Identity(overview.GraphIdentity, LaneGrouping.UserSession),
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        SessionExportRequest Headless(string[] path, bool evidence, ExportFormat format) =>
            new(path, null, evidence, format, RankBy: evidence ? RankingMetric.Records : RankingMetric.ActivePeers,
                Grouping: LaneGrouping.UserSession);

        // The machine rung's rows are the sessions, ranked by peers, and the export says how they were grouped.
        workspace.RankBy = RankingMetric.ActivePeers;
        await workspace.RankingReady;
        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            Assert.Equal((await workspace.ExportAsync(format, Exported)).Content,
                SessionExport.Build(session.Store, Headless([], false, format), Exported).Content);
        }

        using (JsonDocument machine = JsonDocument.Parse((await workspace.ExportAsync(ExportFormat.Json, Exported)).Content))
        {
            Assert.Equal("session", machine.RootElement.GetProperty("groupedBy").GetString());
            Assert.Equal(["session:1", "session:0", "session:2", "session:unknown"],
                machine.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("key").GetString()));
            Assert.Contains(WorkspaceExport.SessionGroupingCaveat,
                machine.RootElement.GetProperty("context").GetProperty("caveats").EnumerateArray().Select(caveat => caveat.GetString()));
        }

        // A session's rung is reached by its key, and its records are the session's processes' own.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == "session:1");
        Assert.True(workspace.Descend());
        await workspace.RankingReady;
        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            Assert.Equal((await workspace.ExportAsync(format, Exported)).Content,
                SessionExport.Build(session.Store, Headless(["session:1"], false, format), Exported).Content);
        }

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        foreach (ExportFormat format in (ExportFormat[])[ExportFormat.Json, ExportFormat.Csv])
        {
            SessionExportResult desktop = await workspace.ExportAsync(format, Exported);
            Assert.Equal(desktop.Content, SessionExport.Build(session.Store, Headless(["session:1"], true, format), Exported).Content);
            Assert.StartsWith("Records owned by the 2 instances of Terminal session 1", desktop.Context.Scope, StringComparison.Ordinal);
        }

        // By executable, as by default, a session's key names no row; and a session whose records name no terminal session
        // is never grouped by one.
        Assert.Contains("'session:1' is not a row of the Machine rung", Assert.Throws<ArgumentException>(() =>
            SessionExport.Build(session.Store, new(["session:1"], null, false, ExportFormat.Json), Exported)).Message,
            StringComparison.Ordinal);
        using var unnamed = new TemporarySession();
        Publish(unnamed.Store, rows);
        Assert.Contains("a session is never guessed", Assert.Throws<ArgumentException>(() =>
            SessionExport.Build(unnamed.Store, Headless([], false, ExportFormat.Json), Exported)).Message, StringComparison.Ordinal);
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
