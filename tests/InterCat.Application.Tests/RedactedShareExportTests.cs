using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using GeneratedRegexAttribute = System.Text.RegularExpressions.GeneratedRegexAttribute;
using Regex = System.Text.RegularExpressions.Regex;
using RegexOptions = System.Text.RegularExpressions.RegexOptions;

namespace InterCat.Application.Tests;

public sealed partial class RedactedShareExportTests
{
    private const string Secret = "SENSITIVE-host-user-process-resource";
    private static readonly Guid SecretProvider = Guid.Parse("5ec2e75e-c0de-4f00-8bad-5ec2e75ec0de");
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

    [Theory(DisplayName = "§11.3: a byte-ranked report names its ranking and each row's ranked bytes, and nothing that identifies")]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void AByteRankedReportKeepsItsRankingAndValues(ExportFormat format)
    {
        ExportContext context = Context() with { RankedBy = RankingMetric.BytesReceived };
        LadderRow[] rows =
        [
            new(Secret, Secret, Secret, 3, 64, Mechanism.Tcp, CoverageState.Covered, DetailLevel.Group,
                AccountingSide.CanonicalOwner) { Ranked = new(RankingMetric.BytesReceived, 4_096, 2, 1) },
            new(Secret + "-2", Secret, Secret, 9, null, Mechanism.Udp, CoverageState.Covered, DetailLevel.Group,
                AccountingSide.CanonicalOwner) { Ranked = new(RankingMetric.BytesReceived, null, 0, 0) },
        ];
        string report = RedactedShareExport.Ranking(format, context, rows);

        AssertSafe(report);
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(report);
            Assert.Equal("bytes-received", json.RootElement.GetProperty("rankedBy").GetString());
            JsonElement first = json.RootElement.GetProperty("rows")[0];
            Assert.Equal((4_096L, 2L, 1L), (first.GetProperty("rankedValue").GetInt64(),
                first.GetProperty("rankedMeasured").GetInt64(), first.GetProperty("rankedUnmeasured").GetInt64()));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("rows")[1].GetProperty("rankedValue").ValueKind);
        }
        else
        {
            string[] lines = report.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.EndsWith("ranked_by,ranked_value,ranked_measured,ranked_unmeasured,ranked_failed,scope_coverage", lines[0].TrimEnd('\r'), StringComparison.Ordinal);
            Assert.EndsWith(",bytes-received,4096,2,1,,", lines[1].TrimEnd('\r'), StringComparison.Ordinal);
            Assert.EndsWith(",bytes-received,,0,0,,", lines[2].TrimEnd('\r'), StringComparison.Ordinal);
            Assert.All(lines, line => Assert.Equal(CsvCellCount(lines[0].TrimEnd('\r')), CsvCellCount(line.TrimEnd('\r'))));
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
        foreach (string secret in new[] { "10.1.2.3", "10.1.2.4", "client.exe",
            activity.ToString(), ownerId.ToString(), first.ProviderId.ToString(), "seg-0000000001-0000.icats" })
            Assert.DoesNotContain(secret, report, StringComparison.OrdinalIgnoreCase);

        // Report tokens and the report's own id are random hex, which contains a short number such as "8080" by chance
        // about once in a few hundred reports. A port or PID leaks only if it appears anywhere else.
        string outsideRandomValues = RandomValues().Replace(report, "#");
        foreach (string secret in new[] { "50000", "8080", "98765" })
            Assert.DoesNotContain(secret, outsideRandomValues, StringComparison.OrdinalIgnoreCase);
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
            // The stored row has nanoseconds; report context promises 100-ns presentation ticks.
            Assert.Equal(15, left.GetProperty("sessionRelativeTicks").GetInt64());
            Assert.False(json.RootElement.GetProperty("redaction").GetProperty("rawLocatorsIncluded").GetBoolean());
        }
        else
        {
            Assert.Contains("session_relative_ticks", report, StringComparison.Ordinal);
            Assert.Equal(3, report.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    [Fact]
    public void Ipv6EndpointsAreTokensOfTheirOwnAddressNotOnlyTheirPort()
    {
        ProcessInstanceId ownerId = new(Guid.Parse("87654321-4321-4321-8321-abcdefabcdef"));
        SessionEvidenceRecord[] records =
        [
            Record(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 98765, 7)
                .Between("[2a01:5ec0::7]:50000", "[fe80::1234:5678:9abc:def0]:443"), ownerId),
            Record(Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 64, 98765, 8)
                .Between("[2a01:5ec0::8]:50000", "[fe80::1234:5678:9abc:def0]:443"), ownerId),
        ];

        string report = RedactedShareExport.Evidence(ExportFormat.Json, Context() with { Rung = DetailLevel.Evidence }, records);

        AssertSafe(report);
        foreach (string secret in new[] { "2a01", "5ec0", "fe80", "9abc:def0" })
            Assert.DoesNotContain(secret, RandomValues().Replace(report, "#"), StringComparison.OrdinalIgnoreCase);

        // Two hosts that share a port are two endpoints; the peer they share is one.
        using JsonDocument json = JsonDocument.Parse(report);
        JsonElement first = json.RootElement.GetProperty("records")[0];
        JsonElement second = json.RootElement.GetProperty("records")[1];
        Assert.NotEqual(first.GetProperty("sourceEndpointToken").GetString(), second.GetProperty("sourceEndpointToken").GetString());
        Assert.Equal(first.GetProperty("destinationEndpointToken").GetString(),
            second.GetProperty("destinationEndpointToken").GetString());
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

    [Theory(DisplayName = "R21: a sharing report states its scope's coverage in the inspector's words, from fixed templates that name no provider")]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void ASharingReportStatesItsScopesCoverage(ExportFormat format)
    {
        IReadOnlyList<MechanismCoverage> coverage = EveryReason();
        ExportContext context = Context() with { Coverage = coverage };
        string said = CoverageText.Describe(coverage);
        LadderRow[] rows = [new(Secret, Secret, Secret, 3, 64, Mechanism.Tcp, CoverageState.Covered, DetailLevel.Group,
            AccountingSide.CanonicalOwner)];
        SessionEvidenceRecord[] records = [Record(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 98765, 7)
            .Between("10.1.2.3:50000", "10.1.2.4:8080"), new(Guid.NewGuid()))];
        ExportContext evidence = context with { Rung = DetailLevel.Evidence };

        foreach (string report in new[]
        {
            RedactedShareExport.Ranking(format, context, rows), RedactedShareExport.Ranking(format, context, []),
            RedactedShareExport.Evidence(format, evidence, records), RedactedShareExport.Evidence(format, evidence, []),
        })
        {
            // The ledgers name their provider with the secret and identify it; neither reaches a report.
            AssertSafe(report);
            Assert.DoesNotContain(SecretProvider.ToString(), report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretProvider.ToString("N"), report, StringComparison.OrdinalIgnoreCase);
            if (format == ExportFormat.Json)
            {
                using JsonDocument json = JsonDocument.Parse(report);
                JsonElement stated = json.RootElement.GetProperty("context");
                Assert.Equal(coverage.Select(entry => ((string?)entry.Mechanism.ToString(), (string?)entry.State.ToString(),
                        (string?)entry.Reason)),
                    stated.GetProperty("coverage").EnumerateArray().Select(entry => (entry.GetProperty("mechanism").GetString(),
                        entry.GetProperty("state").GetString(), entry.GetProperty("reason").GetString())));
                Assert.Equal(said, stated.GetProperty("coverageSummary").GetString());
            }
            else
            {
                // Every line says it after every column the report had before, so each of those keeps its place, and it
                // opens with a word, which a spreadsheet does not evaluate.
                string[] lines = Csv.Lines(report);
                Assert.Equal("scope_coverage", Csv.Cells(lines[0])[^1]);
                Assert.All(lines.Skip(1), line => Assert.Equal(said, Csv.Cells(line)[^1]));
                Assert.All(lines, line => Assert.Equal(CsvCellCount(lines[0]), CsvCellCount(line)));
                Assert.StartsWith("Coverage", said, StringComparison.Ordinal);
            }
        }

        // Nothing judged is no claim: no list entry, no words, and an empty cell.
        string unjudged = RedactedShareExport.Ranking(format, Context(), rows);
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(unjudged);
            Assert.Equal(0, json.RootElement.GetProperty("context").GetProperty("coverage").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("context").GetProperty("coverageSummary").ValueKind);
        }
        else
        {
            Assert.Equal(string.Empty, Csv.Cells(Csv.Lines(unjudged)[1])[^1]);
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

    /// <summary>
    /// Every fact a coverage reason states (`coverage-v2` §4), one mechanism apiece as a scope's coverage lists them, judged
    /// by the rule itself over ledgers whose one provider is named with the secret. The reasons are pinned so that a new
    /// or reworded one fails here and is weighed against the sharing policy before a report carries it.
    /// </summary>
    private static IReadOnlyList<MechanismCoverage> EveryReason()
    {
        CoverageLedgerV1 live = Ledger(Epoch(CoverageAcquisition.LiveCapture, Mechanism.Tcp, admitted: 2));
        IReadOnlyList<MechanismCoverage> coverage =
        [
            SessionCoverage.Of(null, Mechanism.ProcessLifecycle),
            SessionCoverage.Of(live, Mechanism.ThreadLifecycle, new TimeRange(100, 200)),
            SessionCoverage.Of(live, Mechanism.Udp),
            SessionCoverage.Of(Ledger(Epoch(CoverageAcquisition.EtlImport, Mechanism.UnixDomainSocket, admitted: 0)),
                Mechanism.UnixDomainSocket),
            SessionCoverage.Of(Ledger(Epoch(CoverageAcquisition.LiveCapture, Mechanism.NamedPipe, admitted: 0)),
                Mechanism.NamedPipe),
            SessionCoverage.Of(live, Mechanism.Tcp),
            SessionCoverage.Of(Ledger(Epoch(CoverageAcquisition.LiveCapture, Mechanism.Rpc, admitted: 2, undecodable: 6,
                unassigned: 5, lost: [1, 2, 3, 4])), Mechanism.Rpc),
            SessionCoverage.Of(Ledger(Epoch(CoverageAcquisition.EtlImport, Mechanism.Alpc, admitted: 3, lost: [7])),
                Mechanism.Alpc),
            CoverageText.Over(live, null, new TimeRange(0, 10)).Single(entry => entry.Mechanism == Mechanism.Http),
        ];
        Assert.Equal(
        [
            "this generation publishes no coverage ledger",
            "outside the readings the capture's sources delivered",
            "no admitted descriptor records it",
            "its 1 admitted descriptor delivered nothing, and a file cannot show whether its session recorded them",
            "its 1 admitted descriptor delivered nothing while the session recorded them, and nothing was reported lost",
            "2 records from its 1 admitted descriptor, and nothing was reported lost",
            "the session reported 1 lost event, which may be any mechanism's; the consumer lost 2 buffers of unknown size; "
                + "InterCat's full queue dropped 3 records; 4 admitted records could not be stored; 5 undecodable records "
                + "had no admitted mechanism; it may affect this one; 6 of its records could not be decoded",
            "the file reported 7 lost events, which may be any mechanism's",
            "no reading of the capture's clock falls in this interval",
        ], coverage.Select(entry => entry.Reason));
        return coverage;
    }

    private static CoverageLedgerV1 Ledger(CoverageEpochV1 epoch) =>
        new() { Contract = CoverageLedgerV1.ContractName, Epochs = [epoch] };

    /// <summary>
    /// One epoch collecting one descriptor of the secret provider: its admitted and undecodable records, records of a
    /// descriptor it did not collect that could not be decoded, and what each loss layer it measures lost.
    /// </summary>
    private static CoverageEpochV1 Epoch(CoverageAcquisition acquisition, Mechanism mechanism, long admitted,
        long undecodable = 0, long unassigned = 0, long[]? lost = null)
    {
        List<CoverageDeliveryV1> deliveries = [];
        if (admitted + undecodable > 0)
        {
            deliveries.Add(new()
            {
                ProviderId = SecretProvider, EventId = 10, Version = 0, Delivered = admitted + undecodable,
                Admitted = admitted, Omitted = 0,
                Undecodable = undecodable > 0 ? new Dictionary<UndecodableReason, long> { [UndecodableReason.BodyShorterThanSchema] = undecodable } : null,
            });
        }

        if (unassigned > 0)
        {
            deliveries.Add(new()
            {
                ProviderId = SecretProvider, EventId = 11, Version = 0, Delivered = unassigned, Admitted = 0, Omitted = 0,
                Undecodable = new Dictionary<UndecodableReason, long> { [UndecodableReason.UnknownDescriptorVersion] = unassigned },
            });
        }

        LossLayer[] layers = acquisition == CoverageAcquisition.EtlImport
            ? [LossLayer.SourceSession]
            : [LossLayer.SourceSession, LossLayer.ConsumerBuffers, LossLayer.CallbackQueue, LossLayer.Storage];
        return new()
        {
            Epoch = 1,
            Acquisition = acquisition,
            FirstDeliveredNativeTicks = deliveries.Count > 0 ? 0 : null,
            LastDeliveredNativeTicks = deliveries.Count > 0 ? 20 : null,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = SecretProvider, ProviderName = Secret, EventId = 10, Version = 0, Mechanism = mechanism },
            ],
            Deliveries = deliveries,
            Losses = [.. layers.Select((layer, index) => new CoverageLossV1 { Layer = layer, Lost = lost?[index] ?? 0 })],
        };
    }

    private static SessionEvidenceRecord Record(ObservationRowV1 row, ProcessInstanceId id) => new(
        row.ObservationIdIn(Capture, NormalizerContractVersion.V1),
        "seg-0000000001-0000.icats", 3, row,
        new(id, 98765, "client.exe", RelationStrength.Direct, ProcessBindingReason.Bound, true));

    private static ExportContext Context() => new(Session, 3, DetailLevel.Machine,
        "Machine / " + Secret, [new ImpliedFilter(Secret, Secret, Secret)], new TimeRange(100, 2_000),
        Secret, false, [Secret], Exported);

    /// <summary>
    /// A report token (kind, a hyphen, 24 random hex digits) or a random GUID such as the report id, which JSON writes with
    /// dashes and CSV as 32 bare hex digits. Missing the CSV form let the id's own digits read as a leaked port.
    /// </summary>
    [GeneratedRegex("[a-z]+-[0-9a-f]{24}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32}",
        RegexOptions.IgnoreCase)]
    private static partial Regex RandomValues();

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
