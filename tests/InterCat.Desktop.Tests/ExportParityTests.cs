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

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
