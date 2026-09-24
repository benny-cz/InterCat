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

    private static ExportContext Context(bool complete, TimeRange? interval) => new(
        SessionId, 3, DetailLevel.Machine, "Machine", [], interval, "Whole session", complete,
        ["a caveat"], new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
}
