using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class WorkspaceExportTests
{
    private static readonly Guid SessionId = Guid.Parse("d6e7f8a9-b0c1-4d2e-8f3a-4b5c6d7e8f90");

    [Fact]
    public void ARankingExportNamesItsSnapshotAndQuotesAndNeutralizesTextCells()
    {
        ExportContext context = Context(complete: true, interval: new TimeRange(-5, 20));
        LadderRow[] rows =
        [
            new("executable:=CMD", "=cmd|' /C calc'!A0", "PID 100, \"quoted\"", 42, null, Mechanism.Tcp,
                CoverageState.Covered, DetailLevel.Group, AccountingSide.CanonicalOwner),
            new("executable:b", "b.exe", "PID 200", 7, 1_024, Mechanism.Udp,
                CoverageState.PartialGap, DetailLevel.Group, AccountingSide.CanonicalOwner),
        ];

        string csv = WorkspaceExport.RankingCsv(context, rows);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("session_id,generation,rung,interval_start_ticks,interval_end_ticks,complete,key,label", lines[0],
            StringComparison.Ordinal);
        // A cell a spreadsheet would evaluate is neutralized; a number, even a negative one, is left a number.
        Assert.Contains(",'=cmd|' /C calc'!A0,", lines[1], StringComparison.Ordinal);
        Assert.Contains(",\"PID 100, \"\"quoted\"\"\",", lines[1], StringComparison.Ordinal);
        Assert.StartsWith($"{SessionId:N},3,Machine,-5,20,true,", lines[1], StringComparison.Ordinal);
        Assert.Contains(",42,,Tcp,Covered,", lines[1], StringComparison.Ordinal);
        Assert.Contains(",7,1024,Udp,PartialGap,", lines[2], StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(WorkspaceExport.RankingJson(context, rows));
        JsonElement root = json.RootElement;
        Assert.Equal(WorkspaceExport.Contract, root.GetProperty("contract").GetString());
        Assert.Equal("ranking", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("context").GetProperty("complete").GetBoolean());
        Assert.Equal(-5, root.GetProperty("context").GetProperty("interval").GetProperty("startTicks").GetInt64());
        Assert.Equal("Tcp", root.GetProperty("rows")[0].GetProperty("mechanism").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[0].GetProperty("knownBytes").ValueKind);
        Assert.Equal("intercat-machine-d6e7f8a9-g3.json", WorkspaceExport.SuggestedName(context, "json"));
    }

    [Fact(DisplayName = "§6.4: an export's suggested name carries its rung's focus, made safe for a file name")]
    public void ASuggestedNameCarriesTheRungsFocus()
    {
        ExportContext machine = Context(complete: true, interval: null);
        ExportContext process = machine with
        {
            Rung = DetailLevel.ProcessInstance,
            Filters = [new("group", "worker.exe", "opened"), new("process", "worker.exe · PID 87372", "opened")],
        };
        ExportContext channel = machine with
        {
            Rung = DetailLevel.Channel,
            Filters = [.. process.Filters, new("channel", "127.0.0.1:19423 ↔ 127.0.0.1:19469", "opened")],
        };

        Assert.Equal("intercat-process-worker.exe-PID-87372-d6e7f8a9-g3.csv", WorkspaceExport.SuggestedName(process, "csv"));
        Assert.Equal("intercat-channel-127.0.0.1-19423-127.0.0.1-19469-d6e7f8a9-g3.json", WorkspaceExport.SuggestedName(channel, "json"));
        Assert.Equal("dílna.exe-C-Tools", WorkspaceExport.FileNamePart(@"  dílna.exe <C:\Tools>  "));
        Assert.Equal(48, WorkspaceExport.FileNamePart(new string('a', 60)).Length);

        // A focus with nothing a file name can hold is left out rather than leaving a stray hyphen.
        Assert.Equal("intercat-process-d6e7f8a9-g3.json",
            WorkspaceExport.SuggestedName(process with { Filters = [new("process", "<>:?", "opened")] }, "json"));
    }

    [Fact]
    public void AnEvidenceExportCarriesMetadataAndLocatorsButNoBodyAndSaysWhenItIsPartial()
    {
        ObservationRowV1 row = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 7)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_500 };
        var record = new SessionEvidenceRecord(row.ObservationIdIn(Capture, NormalizerContractVersion.V1),
            "seg-0000000001-0000.icats", 3, row,
            new(new ProcessInstanceId(Guid.NewGuid()), 100, "client.exe", RelationStrength.Direct,
                ProcessBindingReason.Bound, true));
        ExportContext partial = Context(complete: false, interval: null) with { Rung = DetailLevel.Evidence };

        using JsonDocument json = JsonDocument.Parse(WorkspaceExport.EvidenceJson(partial, [record]));
        JsonElement exported = json.RootElement.GetProperty("records")[0];
        Assert.False(json.RootElement.GetProperty("context").GetProperty("complete").GetBoolean());
        Assert.Contains("no payload", json.RootElement.GetProperty("context").GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("TCP send", exported.GetProperty("what").GetString());
        Assert.Equal("client.exe", exported.GetProperty("owner").GetProperty("executable").GetString());
        Assert.Equal("Microsoft-Windows-Kernel-Network", exported.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal(7UL, exported.GetProperty("observationId").GetProperty("ordinal").GetUInt64());
        Assert.False(exported.TryGetProperty("body", out _));

        string[] lines = WorkspaceExport.EvidenceCsv(partial, [record]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("body", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(",false,1500,10,TCP send,Tcp,Send,64,TransportObserved,Present,", lines[1], StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:50000 → 127.0.0.1:8080", lines[1], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: an export's CSV states its scope's coverage on every line, and a scope with no row keeps its context in one line")]
    public void AnExportsCsvStatesItsScopesCoverage()
    {
        MechanismCoverage[] coverage =
        [
            new(Mechanism.Tcp, CoverageState.Covered, "2 records from its 1 admitted descriptor, and nothing was reported lost"),
            new(Mechanism.Udp, CoverageState.PartialGap, "the session reported 3 lost events, which may be any mechanism's"),
            new(Mechanism.Rpc, CoverageState.NotCollected, "no admitted descriptor records it"),
        ];
        ExportContext ranked = Context(complete: true, interval: new TimeRange(25, 35)) with { Coverage = coverage };
        ExportContext evidence = ranked with { Rung = DetailLevel.Evidence };
        string said = CoverageText.Describe(coverage);
        LadderRow[] rows =
        [
            new("executable:a", "a.exe", "PID 100", 0, null, Mechanism.Tcp, CoverageState.Covered, DetailLevel.Group,
                AccountingSide.CanonicalOwner),
        ];
        ObservationRowV1 row = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 7)
            .Between("127.0.0.1:50000", "127.0.0.1:8080");
        SessionEvidenceRecord[] records =
            [new(row.ObservationIdIn(Capture, NormalizerContractVersion.V1), "seg-0000000001-0000.icats", 3, row)];

        // Every line of rows ends by saying it holds one, then what the capture covered over the scope, after every
        // column the export had before, so each of those keeps its place.
        foreach (string csv in new[] { WorkspaceExport.RankingCsv(ranked, rows), WorkspaceExport.EvidenceCsv(evidence, records) })
        {
            string[] lines = Csv.Lines(csv);
            Assert.Equal(2, lines.Length);
            string[] header = Csv.Cells(lines[0]);
            Assert.Equal(["session_id", "generation", "rung", "interval_start_ticks", "interval_end_ticks", "complete"], header[..6]);
            Assert.Equal(["row_present", "scope_coverage"], header[^2..]);
            Assert.Equal(header.Length, Csv.Cells(lines[1]).Length);
            Assert.Equal(["true", said], Csv.Cells(lines[1])[^2..]);
        }

        Assert.Equal(["ranked_failed", "row_present"], Csv.Cells(Csv.Lines(WorkspaceExport.RankingCsv(ranked, rows))[0])[^3..^1]);
        Assert.Equal(["segment_row", "row_present"], Csv.Cells(Csv.Lines(WorkspaceExport.EvidenceCsv(evidence, records))[0])[^3..^1]);

        // A scope with no row still names itself, says it is complete, and says what the capture covered there, on one
        // line that holds no row; a ranked one names its ranking, as a row would.
        foreach ((string csv, int rankedBy) in new[]
        {
            (WorkspaceExport.RankingCsv(ranked, []), 15),
            (WorkspaceExport.EvidenceCsv(evidence, []), -1),
        })
        {
            string[] lines = Csv.Lines(csv);
            Assert.Equal(2, lines.Length);
            string[] cells = Csv.Cells(lines[1]);
            Assert.Equal(Csv.Cells(lines[0]).Length, cells.Length);
            Assert.Equal([SessionId.ToString("N"), "3", "25", "35", "true"], [cells[0], cells[1], cells[3], cells[4], cells[5]]);
            Assert.Equal(["false", said], cells[^2..]);
            Assert.Equal(cells.Length - 8 - (rankedBy < 0 ? 0 : 1), cells[6..^2].Count(string.IsNullOrEmpty));
            if (rankedBy >= 0)
            {
                Assert.Equal(("ranked_by", "records"), (Csv.Cells(lines[0])[rankedBy], cells[rankedBy]));
            }
        }

        // Coverage nothing judged is an empty cell, never a claim.
        Assert.Equal(["true", string.Empty],
            Csv.Cells(Csv.Lines(WorkspaceExport.RankingCsv(ranked with { Coverage = [] }, rows))[1])[^2..]);
    }

    private static ExportContext Context(bool complete, TimeRange? interval) => new(
        SessionId, 3, DetailLevel.Machine, "Machine", [], interval, "Whole session", complete,
        ["a caveat"], new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
}
