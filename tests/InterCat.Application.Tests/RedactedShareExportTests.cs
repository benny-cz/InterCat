using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class RedactedShareExportTests
{
    private const string Secret = "SENSITIVE-host-user-process-resource";
    private static readonly DateTimeOffset Exported = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void RankingIsAnAllowlistedReportNotARelabeledOriginal(ExportFormat format)
    {
        ExportContext context = Context();
        LadderRow[] rows = [new(Secret, Secret, Secret, 3, 64, Mechanism.Tcp,
            CoverageState.PartialGap, DetailLevel.Group, AccountingSide.CanonicalOwner)];
        string report = RedactedShareExport.Ranking(format, context, rows);

        AssertSafe(report);
        Assert.Contains(RedactedShareExport.Contract, report, StringComparison.Ordinal);
        Assert.Contains(RedactedShareExport.Policy, report, StringComparison.Ordinal);
        Assert.Contains("omitted", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warning", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entity-", report, StringComparison.Ordinal);
        Assert.NotEqual(report, RedactedShareExport.Ranking(format, context, rows));
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(report);
            Assert.Equal(3, json.RootElement.GetProperty("rows")[0].GetProperty("observations").GetInt64());
            Assert.False(json.RootElement.GetProperty("redaction").GetProperty("originalSourcesIncluded").GetBoolean());
        }
    }

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void EvidencePreservesRelationshipsWithoutLeakingSourceValues(ExportFormat format)
    {
        Guid activity = Guid.Parse("12345678-1234-4123-8123-123456789abc");
        ProcessInstanceId ownerId = new(Guid.Parse("87654321-4321-4321-8321-abcdefabcdef"));
        ObservationRowV1 first = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 98765, 7)
            .Between("10.1.2.3:50000", "10.1.2.4:8080") with
            {
                SessionRelativeTicks = 1_500, ResourceName = Secret, ActivityId = activity,
                SourceIdentifier = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            };
        ObservationRowV1 second = Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 98765, 8)
            .Between("10.1.2.4:8080", "10.1.2.3:50000") with
            { SessionRelativeTicks = 1_600, RelatedActivityId = activity, ResourceName = Secret };
        SessionEvidenceRecord[] records =
        [
            Record(first, ownerId),
            Record(second, ownerId),
        ];

        string report = RedactedShareExport.Evidence(format, Context() with { Rung = DetailLevel.Evidence }, records);
        AssertSafe(report);
        foreach (string secret in new[] { "10.1.2.3", "10.1.2.4", "50000", "8080", "98765", "client.exe",
            activity.ToString(), ownerId.ToString(), first.ProviderId.ToString(), "seg-0000000001-0000.icats" })
            Assert.DoesNotContain(secret, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endpoint-", report, StringComparison.Ordinal);
        Assert.Contains("record-", report, StringComparison.Ordinal);
        Assert.NotEqual(report, RedactedShareExport.Evidence(format, Context(), records));

        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(report);
            JsonElement left = json.RootElement.GetProperty("records")[0];
            JsonElement right = json.RootElement.GetProperty("records")[1];
            Assert.Equal(left.GetProperty("sourceEndpointToken").GetString(),
                right.GetProperty("destinationEndpointToken").GetString());
            Assert.Equal(left.GetProperty("destinationEndpointToken").GetString(),
                right.GetProperty("sourceEndpointToken").GetString());
            Assert.Equal(left.GetProperty("ownerToken").GetString(), right.GetProperty("ownerToken").GetString());
            Assert.Equal(left.GetProperty("activityToken").GetString(),
                right.GetProperty("relatedActivityToken").GetString());
            Assert.Equal(1_500, left.GetProperty("sessionRelativeTicks").GetInt64());
            Assert.False(json.RootElement.GetProperty("redaction").GetProperty("rawLocatorsIncluded").GetBoolean());
        }
        else
        {
            Assert.Contains("session_relative_ticks", report, StringComparison.Ordinal);
            Assert.Equal(3, report.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void EmptyReportStillDisclosesItsPolicyAndZeroRows(ExportFormat format)
    {
        string report = RedactedShareExport.Evidence(format, Context(), []);
        AssertSafe(report);
        Assert.Contains(RedactedShareExport.Policy, report, StringComparison.Ordinal);
        Assert.Contains("warning", report, StringComparison.OrdinalIgnoreCase);
        if (format == ExportFormat.Csv)
        {
            string[] lines = report.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Contains(",false,", lines[1], StringComparison.Ordinal);
            Assert.Equal(CsvCellCount(lines[0]), CsvCellCount(lines[1]));
        }
    }

    [Fact]
    public async Task ExportPublicationPreservesExistingFileOnRefusalAndCancellation()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("intercat-share-export-");
        string destination = Path.Combine(directory.FullName, "report.json");
        try
        {
            await File.WriteAllTextAsync(destination, "previous complete report");
            await Assert.ThrowsAsync<IOException>(() => ExportFileWriter.WriteAsync(destination, "replacement",
                overwrite: false));
            Assert.Equal("previous complete report", await File.ReadAllTextAsync(destination));

            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportFileWriter.WriteAsync(destination,
                "replacement", overwrite: true, stopped.Token));
            Assert.Equal("previous complete report", await File.ReadAllTextAsync(destination));
            Assert.Single(directory.GetFiles());

            await ExportFileWriter.WriteAsync(destination, "new complete report", overwrite: true);
            Assert.Equal("new complete report", await File.ReadAllTextAsync(destination));
            Assert.Single(directory.GetFiles());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static SessionEvidenceRecord Record(ObservationRowV1 row, ProcessInstanceId id) => new(
        row.ObservationIdIn(Capture, NormalizerContractVersion.V1),
        "seg-0000000001-0000.icats", 3, row,
        new(id, 98765, "client.exe", RelationStrength.Direct, ProcessBindingReason.Bound, true));

    private static ExportContext Context() => new(Session, 3, DetailLevel.Machine,
        "Machine / " + Secret, [new ImpliedFilter(Secret, Secret, Secret)], new TimeRange(100, 2_000),
        Secret, false, [Secret], Exported);

    private static void AssertSafe(string report)
    {
        Assert.DoesNotContain(Secret, report, StringComparison.Ordinal);
        Assert.DoesNotContain(Session.ToString(), report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Session.ToString("N"), report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Capture.ToString(), report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawRecord", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journalRecord", report, StringComparison.OrdinalIgnoreCase);
    }

    private static int CsvCellCount(string line)
    {
        int cells = 1;
        bool quoted = false;
        foreach (char character in line)
        {
            if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted) cells++;
        }
        return cells;
    }
}
