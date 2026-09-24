using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionExportTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const string OtherClient = "127.0.0.1:50001";
    private const string OtherServer = "127.0.0.1:9090";
    private static readonly DateTimeOffset Exported = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R18: a headless export descends by row key and names its rung, interval and scope")]
    public void AHeadlessExportDescendsByRowKey()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessNode client = overview.Nodes.Single(node => node.ProcessId == 100);

        SessionExportResult machine = SessionExport.Build(session.Store, new([], null, false, ExportFormat.Json), Exported);
        Assert.Equal(DetailLevel.Machine, machine.Context.Rung);
        Assert.Equal("Whole session", machine.Context.Scope);
        Assert.Equal(overview.Groups.Count, machine.Rows);
        using (JsonDocument json = JsonDocument.Parse(machine.Content))
        {
            Assert.Equal(WorkspaceExport.Contract, json.RootElement.GetProperty("contract").GetString());
            Assert.Equal("ranking", json.RootElement.GetProperty("kind").GetString());
        }

        SessionExportResult process = SessionExport.Build(session.Store,
            new([client.GroupKey, client.Id.ToString()], null, false, ExportFormat.Csv), Exported);
        Assert.Equal(DetailLevel.ProcessInstance, process.Context.Rung);

        // icat overview --json prints instance IDs dashed; either spelling names the same row.
        Assert.Equal(process.Content, SessionExport.Build(session.Store,
            new([client.GroupKey, client.Id.Value.ToString("D")], null, false, ExportFormat.Csv), Exported).Content);
        Assert.Equal(2, process.Context.Filters.Count);
        Assert.Contains(client.Name, process.Context.Breadcrumb, StringComparison.Ordinal);
        Assert.Equal(process.Rows + 1, process.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // Ranked within an interval, the counts answer only that interval and the scope says so.
        SessionExportResult within = SessionExport.Build(session.Store,
            new([client.GroupKey], new TimeRange(10, 13), false, ExportFormat.Json), Exported);
        Assert.StartsWith("Ranked within", within.Context.Scope, StringComparison.Ordinal);
        Assert.Equal(new TimeRange(10, 13), within.Context.Interval);

        ArgumentException missing = Assert.Throws<ArgumentException>(() =>
            SessionExport.Build(session.Store, new(["executable:nowhere"], null, false, ExportFormat.Json), Exported));
        Assert.Contains("is not a row of the Machine rung", missing.Message, StringComparison.Ordinal);
        Assert.Contains(client.GroupKey, missing.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: an evidence export reads its whole scope in one pass, and one that stops at its limit says so")]
    public void AnEvidenceExportPagesItsWholeScope()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, rowsPerSegment: 37);

        // One pass under one lease reads exactly the pages' records, in the same order, across every segment.
        var paged = new List<SessionEvidenceRecord>();
        string? cursor = null;
        do
        {
            SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 7, cursor: cursor);
            paged.AddRange(page.Records);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        SessionEvidencePage scope = SessionEvidenceQuery.ReadScope(session.Store, SessionExport.MaximumEvidenceLimit);
        Assert.Null(scope.NextCursor);
        Assert.Equal(paged.Select(record => (record.SegmentName, record.SegmentRow)),
            scope.Records.Select(record => (record.SegmentName, record.SegmentRow)));
        Assert.NotNull(SessionEvidenceQuery.ReadScope(session.Store, rows.Length - 1).NextCursor);

        SessionExportResult all = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Json), Exported);
        Assert.Equal(DetailLevel.Evidence, all.Context.Rung);
        Assert.Equal(rows.Length, all.Rows);
        Assert.True(all.Context.Complete);
        Assert.Contains("Every record of this scope is included.", all.Context.Caveats);
        using (JsonDocument json = JsonDocument.Parse(all.Content))
        {
            Assert.Equal(rows.Length, json.RootElement.GetProperty("records").GetArrayLength());
            Assert.True(json.RootElement.GetProperty("context").GetProperty("complete").GetBoolean());
        }

        SessionExportResult exact = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Json, rows.Length), Exported);
        Assert.True(exact.Context.Complete);

        SessionExportResult partial = SessionExport.Build(session.Store, new([], null, true, ExportFormat.Csv, 5), Exported);
        Assert.Equal(5, partial.Rows);
        Assert.False(partial.Context.Complete);
        Assert.Contains(partial.Context.Caveats, caveat => caveat.Contains("--limit", StringComparison.Ordinal));
    }

    /// <summary>Two paired connections between two process pairs, 400 records, some beyond one page.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        .. Enumerable.Range(0, 100).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(1 + (4 * index)))
                .Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, (ulong)(2 + (4 * index)))
                .Between(ServerEnd, ClientEnd)),
            Timed(Transfer(510 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 8, 300, (ulong)(3 + (4 * index)))
                .Between(OtherClient, OtherServer)),
            Timed(Transfer(511 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 400, (ulong)(4 + (4 * index)))
                .Between(OtherServer, OtherClient)),
        }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
