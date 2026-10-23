using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A comparison of two intervals shared as a redacted report (`contracts/comparison-v1.md` §8b, plan M12): its processes,
/// executables and other hosts random tokens, consistent within the report; what it compared, by which evidence policy,
/// byte domain and accounting, each interval's coverage and the sources it is missing, and that no alignment applies; and
/// no name, PID, address, port, key, session identity or generation.
/// </summary>
public sealed partial class RedactedShareExportTests
{
    private const long Tick = 10_000_000;
    private static readonly ProcessInstanceId Client = new(Guid.Parse("c11e7000-0000-4000-8000-5ec2e75ec0de"));
    private static readonly ProcessInstanceId Server = new(Guid.Parse("5e7e7000-0000-4000-8000-5ec2e75ec0de"));
    private static readonly ProcessInstanceId Unnamed = new(Guid.Parse("0a0a0000-0000-4000-8000-5ec2e75ec0de"));
    private static readonly ProcessInstanceId Namesake = new(Guid.Parse("0b0b0000-0000-4000-8000-5ec2e75ec0de"));
    private static readonly MechanismCoverage TcpCovered = new(Mechanism.Tcp, CoverageState.Covered, "2 records from its 1 admitted descriptor, and nothing was reported lost");
    private const string NotCollected = "TCP was not collected over the compared interval: no admitted descriptor records it.";

    [Theory(DisplayName = "I22: a shared comparison's rows name their ends by tokens, and no process, PID, address, port or key")]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void ASharedComparisonNamesItsEndsByTokens(ExportFormat format)
    {
        IntervalComparison comparison = SecretComparison();
        string report = RedactedShareExport.Comparison(format, comparison, Exported);

        AssertSafe(report);
        Assert.DoesNotContain(Secret, report, StringComparison.OrdinalIgnoreCase);
        foreach (string secret in new[] { "10.98.76.54", "fe80::5ec:1", Client.ToString(), Server.ToString(), Unnamed.ToString(),
            Namesake.ToString(), SecretProvider.ToString(), "PID ", "tcp:", "hosts:" })
            Assert.DoesNotContain(secret, report, StringComparison.OrdinalIgnoreCase);

        // Tokens and the report's id are random hex, which may hold a short number by chance; a PID or port leaks only if it
        // appears anywhere else.
        string outsideRandomValues = RandomValues().Replace(report, "#");
        foreach (string secret in new[] { "98765", "98766", "51515", "62626", "49321", "3389" })
            Assert.DoesNotContain(secret, outsideRandomValues, StringComparison.Ordinal);
        Assert.NotEqual(report, RedactedShareExport.Comparison(format, comparison, Exported));

        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(report);
            JsonElement root = json.RootElement;
            Assert.Equal((RedactedShareExport.Contract, "comparison", RedactedShareExport.Policy),
                (root.GetProperty("contract").GetString(), root.GetProperty("kind").GetString(),
                    root.GetProperty("redaction").GetProperty("policy").GetString()));
            JsonElement stated = root.GetProperty("comparison");
            foreach (string withheld in new[] { "sessionId", "generation", "sessionPath" })
                Assert.False(stated.TryGetProperty(withheld, out _), withheld);
            Assert.Equal(("Records", JsonValueKind.Null, JsonValueKind.Null), (stated.GetProperty("comparedBy").GetString(),
                stated.GetProperty("byteDomain").ValueKind, stated.GetProperty("accounting").ValueKind));

            JsonElement[] rows = [.. root.GetProperty("rows").EnumerateArray()];
            Assert.Equal([1, 2, 3, 4], rows.Select(row => row.GetProperty("rank").GetInt32()));
            foreach (JsonElement row in rows)
            {
                foreach (string withheld in new[] { "key", "source", "target", "address", "processId", "name", "acceptedPorts", "hostPorts" })
                    Assert.False(row.TryGetProperty(withheld, out _), withheld);
            }

            // Two processes of one executable are two process tokens and one executable token, and an executable of the same
            // name at another path is another; a process whose executable was not witnessed has none; each token is its
            // kind's, and one process, or one host, is one token wherever it appears.
            (JsonElement between, JsonElement reached, JsonElement accepted, JsonElement namesake) = (rows[0], rows[1], rows[2], rows[3]);
            Assert.Equal(("Processes", "Tcp", "SourceToTarget"), (between.GetProperty("kind").GetString(),
                between.GetProperty("mechanism").GetString(), between.GetProperty("connected").GetString()));
            Assert.StartsWith("process-", between.GetProperty("sourceToken").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("process-", between.GetProperty("targetToken").GetString(), StringComparison.Ordinal);
            Assert.NotEqual(between.GetProperty("sourceToken").GetString(), between.GetProperty("targetToken").GetString());
            Assert.StartsWith("executable-", between.GetProperty("sourceExecutableToken").GetString(), StringComparison.Ordinal);
            Assert.Equal(between.GetProperty("sourceExecutableToken").GetString(),
                between.GetProperty("targetExecutableToken").GetString());
            Assert.Equal(JsonValueKind.Null, between.GetProperty("hostToken").ValueKind);
            Assert.Equal(("Increased", false, 1.0), (between.GetProperty("change").GetString(),
                between.GetProperty("fromLowerBound").GetBoolean(), between.GetProperty("differencePerSecond").GetDouble()));
            Assert.Equal((10L, 1.0, 20L, 2.0), (between.GetProperty("baseline").GetProperty("records").GetInt64(),
                between.GetProperty("baseline").GetProperty("perSecond").GetDouble(),
                between.GetProperty("compared").GetProperty("records").GetInt64(),
                between.GetProperty("compared").GetProperty("perSecond").GetDouble()));
            Assert.Equal(JsonValueKind.Null, between.GetProperty("reason").ValueKind);

            Assert.Equal(("OtherHost", "SourceToTarget", "NotComparable", NotCollected), (reached.GetProperty("kind").GetString(),
                reached.GetProperty("connected").GetString(), reached.GetProperty("change").GetString(),
                reached.GetProperty("reason").GetString()));
            Assert.StartsWith("host-", reached.GetProperty("hostToken").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, reached.GetProperty("sourceExecutableToken").ValueKind);
            Assert.Equal(JsonValueKind.Null, reached.GetProperty("targetToken").ValueKind);
            Assert.Equal(JsonValueKind.Null, reached.GetProperty("differencePerSecond").ValueKind);
            Assert.Equal(("Tcp", "NotCollected"), (reached.GetProperty("compared").GetProperty("coverage").GetProperty("mechanism").GetString(),
                reached.GetProperty("compared").GetProperty("coverage").GetProperty("state").GetString()));

            Assert.Equal(("TargetToSource", "Appeared"), (accepted.GetProperty("connected").GetString(),
                accepted.GetProperty("change").GetString()));
            Assert.Equal(between.GetProperty("sourceToken").GetString(), accepted.GetProperty("sourceToken").GetString());
            Assert.Equal(between.GetProperty("sourceExecutableToken").GetString(), accepted.GetProperty("sourceExecutableToken").GetString());
            Assert.NotEqual(reached.GetProperty("hostToken").GetString(), accepted.GetProperty("hostToken").GetString());
            Assert.Equal(reached.GetProperty("hostToken").GetString(), namesake.GetProperty("hostToken").GetString());
            Assert.StartsWith("executable-", namesake.GetProperty("sourceExecutableToken").GetString(), StringComparison.Ordinal);
            Assert.NotEqual(between.GetProperty("sourceExecutableToken").GetString(), namesake.GetProperty("sourceExecutableToken").GetString());
            Assert.DoesNotContain(namesake.GetProperty("sourceToken").GetString(), rows.Take(3).Select(row => row.GetProperty("sourceToken").GetString()));
        }
        else
        {
            string[] lines = Csv.Lines(report);
            Assert.Equal(5, lines.Length);
            string[] header = Csv.Cells(lines[0]);
            Assert.Equal(header.Length, header.Distinct().Count());
            Assert.All(lines, line => Assert.Equal(header.Length, Csv.Cells(line).Length));
            string[][] rows = [.. lines.Skip(1).Select(Csv.Cells)];
            string Of(string[] row, string column) => row[Array.IndexOf(header, column)];
            Assert.Equal(["comparison", "comparison", "comparison", "comparison"], rows.Select(row => Of(row, "kind")));
            Assert.Equal(["Processes", "OtherHost", "OtherHost", "OtherHost"], rows.Select(row => Of(row, "row_kind")));
            Assert.Equal(["1", "2", "3", "4"], rows.Select(row => Of(row, "rank")));
            Assert.Equal(Of(rows[0], "source_token"), Of(rows[2], "source_token"));
            Assert.Equal(Of(rows[1], "host_token"), Of(rows[3], "host_token"));
            Assert.Equal(Of(rows[0], "source_executable_token"), Of(rows[0], "target_executable_token"));
            Assert.Equal(string.Empty, Of(rows[1], "source_executable_token"));
            Assert.Equal((NotCollected, "NotCollected", string.Empty), (Of(rows[1], "reason"), Of(rows[1], "compared_coverage_state"),
                Of(rows[1], "difference_per_second")));
            Assert.Equal(("Increased", "1", "10", "1"), (Of(rows[0], "change"), Of(rows[0], "difference_per_second"),
                Of(rows[0], "baseline_records"), Of(rows[0], "baseline_per_second")));

            // A spreadsheet evaluates a cell that opens with =, + or @, and one with - unless a number follows: none does.
            Assert.All(rows.SelectMany(row => row), cell => Assert.False(
                cell.Length > 0 && (cell[0] is '=' or '+' or '@' || cell[0] == '-' && (cell.Length == 1 || !char.IsAsciiDigit(cell[1]))),
                cell));
        }
    }

    [Theory(DisplayName = "R21: a shared comparison records each interval's coverage and missing sources, its byte domain, accounting and alignment")]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void ASharedComparisonRecordsWhatItCompared(ExportFormat format)
    {
        // The baseline's coverage holds every reason the rule gives, judged over ledgers whose one provider is named with the
        // secret; what is not covered in full, or not known to be, is a source the interval is missing.
        IReadOnlyList<MechanismCoverage> every = EveryReason();
        MechanismCoverage[] missing = [.. every.Where(entry => entry.State is CoverageState.PartialGap
            or CoverageState.NotCollected or CoverageState.UnknownCoverage)];
        Assert.NotEmpty(missing);
        Assert.NotEqual(every.Count, missing.Length);
        IntervalComparison comparison = SecretComparison() with
        {
            Metric = ComparisonMetric.BytesSent,
            BaselineCoverage = every,
            Relationships =
            [
                .. SecretComparison().Relationships.Take(1).Select(row => row with
                {
                    Baseline = new(10, 100, 2, 0, TcpCovered),
                    Compared = new(20, 400, 1, 1, TcpCovered),
                    FromLowerBound = true,
                }),
                .. SecretComparison().Relationships.Skip(1),
            ],
        };

        // As many rows as asked, saying how many were compared and that the report is not all of them.
        string report = RedactedShareExport.Comparison(format, comparison, Exported, limit: 2);
        AssertSafe(report);
        Assert.DoesNotContain(SecretProvider.ToString(), report, StringComparison.OrdinalIgnoreCase);
        string said = CoverageText.Describe(every);
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(report);
            JsonElement stated = json.RootElement.GetProperty("comparison");
            Assert.Equal(("BytesSent", "IncludeCorrelated", "TransportObserved", "SendSide"), (stated.GetProperty("comparedBy").GetString(),
                stated.GetProperty("evidencePolicy").GetString(), stated.GetProperty("byteDomain").GetString(),
                stated.GetProperty("accounting").GetString()));
            Assert.Equal((JsonValueKind.Null, RedactedShareExport.NoAlignment), (stated.GetProperty("alignmentRevision").ValueKind,
                stated.GetProperty("alignment").GetString()));
            Assert.Equal(ComparisonText.RatesNote, stated.GetProperty("ratesMeaning").GetString());
            Assert.Equal((4, 2, false, 7), (stated.GetProperty("rowsCompared").GetInt32(), stated.GetProperty("rowsListed").GetInt32(),
                stated.GetProperty("complete").GetBoolean(), stated.GetProperty("quiet").GetInt32()));
            Assert.Equal(Exported, stated.GetProperty("exportedUtc").GetDateTimeOffset());
            JsonElement baseline = stated.GetProperty("baseline");
            Assert.Equal((0L, 10 * Tick), (baseline.GetProperty("startTicks").GetInt64(), baseline.GetProperty("endTicks").GetInt64()));
            Assert.Equal(every.Select(entry => ((string?)entry.Mechanism.ToString(), (string?)entry.State.ToString(), (string?)entry.Reason)),
                baseline.GetProperty("coverage").EnumerateArray().Select(Coverage));
            Assert.Equal(missing.Select(entry => ((string?)entry.Mechanism.ToString(), (string?)entry.State.ToString(), (string?)entry.Reason)),
                baseline.GetProperty("missingSources").EnumerateArray().Select(Coverage));
            Assert.Equal(said, baseline.GetProperty("coverageSummary").GetString());
            Assert.Equal(0, stated.GetProperty("compared").GetProperty("missingSources").GetArrayLength());

            JsonElement[] rows = [.. json.RootElement.GetProperty("rows").EnumerateArray()];
            Assert.Equal(2, rows.Length);
            Assert.Equal(("Increased", true, 1L), (rows[0].GetProperty("change").GetString(), rows[0].GetProperty("fromLowerBound").GetBoolean(),
                rows[0].GetProperty("compared").GetProperty("unmeasured").GetInt64()));
        }
        else
        {
            string[] lines = Csv.Lines(report);
            string[] header = Csv.Cells(lines[0]);
            Assert.Equal(3, lines.Length);
            Assert.All(lines, line => Assert.Equal(header.Length, Csv.Cells(line).Length));
            string[] row = Csv.Cells(lines[1]);
            string Of(string column) => row[Array.IndexOf(header, column)];
            Assert.Equal(("BytesSent", "TransportObserved", "SendSide", string.Empty, RedactedShareExport.NoAlignment),
                (Of("compared_by"), Of("byte_domain"), Of("accounting"), Of("alignment_revision"), Of("alignment")));
            Assert.Equal(("4", "2", "false", "true"), (Of("rows_compared"), Of("rows_listed"), Of("complete"), Of("from_lower_bound")));
            Assert.Equal(string.Join(';', missing.Select(entry => $"{entry.Mechanism}:{entry.State}")), Of("baseline_missing_sources"));
            Assert.Equal((said, string.Empty), (Of("baseline_coverage"), Of("compared_missing_sources")));
            Assert.Equal("rates_meaning", header[^1]);
        }

        // A comparison with no row still says what it compared and over what, and that it lists none.
        string empty = RedactedShareExport.Comparison(format, comparison with { Relationships = [] }, Exported);
        AssertSafe(empty);
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(empty);
            Assert.Equal(0, json.RootElement.GetProperty("rows").GetArrayLength());
            Assert.True(json.RootElement.GetProperty("comparison").GetProperty("complete").GetBoolean());
        }
        else
        {
            string[] lines = Csv.Lines(empty);
            string[] header = Csv.Cells(lines[0]);
            Assert.Equal(2, lines.Length);
            string[] row = Csv.Cells(lines[1]);
            Assert.Equal(header.Length, row.Length);
            Assert.Equal(("false", said), (row[Array.IndexOf(header, "row_present")], row[Array.IndexOf(header, "baseline_coverage")]));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => RedactedShareExport.Comparison(format, comparison, Exported, limit: 0));
    }

    [Theory(DisplayName = "I22: a shared comparison by executable names each executable by a token, never its path or a process")]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.Csv)]
    public void ASharedComparisonByExecutableNamesNoPath(ExportFormat format)
    {
        // Two executables whose paths hold the secret related, and the first reached another host; processes of no
        // witnessed executable were in no row.
        string first = "executable:C:\\" + Secret.ToUpperInvariant() + "\\APP.EXE";
        string second = "executable:D:\\" + Secret.ToUpperInvariant() + "\\APP.EXE";
        IntervalComparison comparison = SecretComparison() with
        {
            Grouping = ComparisonGrouping.Executable,
            Groups = new Dictionary<string, ProcessGroup>
            {
                [first] = new(first, "app.exe (" + Secret + ")", LaneGrouping.Executable, "C:\\" + Secret + "\\app.exe"),
                [second] = new(second, "app.exe (" + Secret + "2)", LaneGrouping.Executable, "D:\\" + Secret + "\\app.exe"),
            },
            Unmatched = new UnmatchedRows(3, 12, 9),
            Relationships =
            [
                new ComparedRelationship(new(10, 10, 10, 0, TcpCovered), new(20, 20, 20, 0, TcpCovered), ComparisonChange.Increased, null)
                {
                    Executables = new ExecutableRelationship("executables:tcp:" + first + "|" + second, first, second, Mechanism.Tcp)
                    {
                        SourceConnected = 2,
                        AcceptedPorts = [49321],
                    },
                },
                new ComparedRelationship(new(0, 0, 0, 0, TcpCovered), new(4, 4, 4, 0, TcpCovered), ComparisonChange.Appeared, null)
                {
                    ExecutableHost = new ExecutableReach("executable-hosts:tcp:" + first + "@10.98.76.54", first, Mechanism.Tcp,
                        "10.98.76.54") { Connected = 1, Ports = [49321] },
                },
            ],
        };
        string report = RedactedShareExport.Comparison(format, comparison, Exported);

        AssertSafe(report);
        Assert.DoesNotContain(Secret, report, StringComparison.OrdinalIgnoreCase);
        foreach (string secret in new[] { "app.exe", "APP.EXE", "10.98.76.54", "executables:", "executable-hosts:" })
            Assert.DoesNotContain(secret, report, StringComparison.Ordinal);
        Assert.DoesNotContain("49321", RandomValues().Replace(report, "#"), StringComparison.Ordinal);
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(report);
            JsonElement stated = json.RootElement.GetProperty("comparison");
            Assert.Equal("Executable", stated.GetProperty("groupedBy").GetString());
            Assert.Equal((3, 12L, 9L), (stated.GetProperty("unmatched").GetProperty("rows").GetInt32(),
                stated.GetProperty("unmatched").GetProperty("baselineRecords").GetInt64(),
                stated.GetProperty("unmatched").GetProperty("comparedRecords").GetInt64()));
            (JsonElement pair, JsonElement reach) = (json.RootElement.GetProperty("rows")[0], json.RootElement.GetProperty("rows")[1]);
            Assert.Equal(("Executables", "ExecutableToOtherHost"), (pair.GetProperty("kind").GetString(), reach.GetProperty("kind").GetString()));
            foreach (JsonElement row in new[] { pair, reach })
            {
                Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (row.GetProperty("sourceToken").ValueKind, row.GetProperty("targetToken").ValueKind));
            }

            Assert.Equal(pair.GetProperty("sourceExecutableToken").GetString(), reach.GetProperty("sourceExecutableToken").GetString());
            Assert.NotEqual(pair.GetProperty("sourceExecutableToken").GetString(), pair.GetProperty("targetExecutableToken").GetString());
            Assert.Equal("SourceToTarget", pair.GetProperty("connected").GetString());
            Assert.StartsWith("host-", reach.GetProperty("hostToken").GetString(), StringComparison.Ordinal);
        }
        else
        {
            string[] lines = Csv.Lines(report);
            string[] header = Csv.Cells(lines[0]);
            Assert.All(lines, line => Assert.Equal(header.Length, Csv.Cells(line).Length));
            string[] row = Csv.Cells(lines[1]);
            string Of(string column) => row[Array.IndexOf(header, column)];
            Assert.Equal(("Executable", "3", "12", "9", string.Empty), (Of("grouped_by"), Of("unmatched_rows"),
                Of("unmatched_baseline_records"), Of("unmatched_compared_records"), Of("source_token")));
        }

        // By process, nothing is counted apart, and the report says none: no unmatched rows, not zero of them.
        using JsonDocument byProcess = JsonDocument.Parse(RedactedShareExport.Comparison(ExportFormat.Json, SecretComparison(), Exported));
        Assert.Equal(("Process", JsonValueKind.Null), (byProcess.RootElement.GetProperty("comparison").GetProperty("groupedBy").GetString(),
            byProcess.RootElement.GetProperty("comparison").GetProperty("unmatched").ValueKind));
        Assert.Equal((1, JsonValueKind.Null, RedactedShareExport.NoAlignment), (byProcess.RootElement.GetProperty("comparison")
            .GetProperty("captures").GetInt32(), byProcess.RootElement.GetProperty("comparison").GetProperty("sameHost").ValueKind,
            byProcess.RootElement.GetProperty("comparison").GetProperty("alignment").GetString()));

        // Of two captures, each interval is on its own capture's clock; whether they name one host is said, never which.
        string across = RedactedShareExport.Comparison(format, comparison with
        {
            BaselineSessionId = Guid.Parse("0c0c0c0c-0000-4000-8000-5ec2e75ec0de"), BaselineGeneration = 7, SameHost = true,
        }, Exported);
        AssertSafe(across);
        Assert.DoesNotContain("0c0c0c0c", across, StringComparison.OrdinalIgnoreCase);
        if (format == ExportFormat.Json)
        {
            using JsonDocument json = JsonDocument.Parse(across);
            JsonElement stated = json.RootElement.GetProperty("comparison");
            Assert.Equal((2, true, RedactedShareExport.NoAlignmentAcross, JsonValueKind.Null), (stated.GetProperty("captures").GetInt32(),
                stated.GetProperty("sameHost").GetBoolean(), stated.GetProperty("alignment").GetString(),
                stated.GetProperty("alignmentRevision").ValueKind));
            Assert.False(stated.TryGetProperty("baselineGeneration", out _));
        }
        else
        {
            string[] lines = Csv.Lines(across);
            string[] header = Csv.Cells(lines[0]);
            string[] row = Csv.Cells(lines[1]);
            Assert.Equal(("2", "true", RedactedShareExport.NoAlignmentAcross), (row[Array.IndexOf(header, "captures")],
                row[Array.IndexOf(header, "same_host")], row[Array.IndexOf(header, "alignment")]));
        }
    }

    private static (string?, string?, string?) Coverage(JsonElement entry) => (entry.GetProperty("mechanism").GetString(),
        entry.GetProperty("state").GetString(), entry.GetProperty("reason").GetString());

    /// <summary>
    /// A comparison whose every name, path, key and address holds the secret or a number a leak would show: two processes
    /// of one executable, PIDs 98765 and 98766, whose relationship rose; a process of no witnessed executable, PID 51515,
    /// whose connections to 10.98.76.54:49321 are not comparable; connections from fe80::5ec:1 to the first's port 3389;
    /// and a process of an executable of the first's name at another path, PID 62626, whose connections to 10.98.76.54
    /// appeared.
    /// </summary>
    private static IntervalComparison SecretComparison()
    {
        string executable = "executable:C:\\" + Secret.ToUpperInvariant() + "\\APP.EXE";
        string elsewhere = "executable:D:\\" + Secret.ToUpperInvariant() + "\\APP.EXE";
        var edge = new CommunicationEdge("tcp:" + Secret, Client, Server, Mechanism.Tcp, 30, 3_000, RelationStrength.Direct)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = ["tcp:" + Secret],
            SourceConnected = 1,
            AcceptedPorts = [49321],
        };
        var reach = new HostLink(Unnamed, Mechanism.Tcp,
            [new HostAddress("10.98.76.54", 1, 4) { Connected = 1, Ports = [49321] }], RelationStrength.Direct);
        var accepted = new HostLink(Client, Mechanism.Tcp,
            [new HostAddress("fe80::5ec:1", 1, 2) { Accepted = 1, AcceptedPorts = [3389] }], RelationStrength.Direct);
        var again = new HostLink(Namesake, Mechanism.Tcp,
            [new HostAddress("10.98.76.54", 1, 1) { Connected = 1, Ports = [49321] }], RelationStrength.Direct);
        MechanismCoverage gone = new(Mechanism.Tcp, CoverageState.NotCollected, "no admitted descriptor records it");
        return new IntervalComparison(Session, 41, ComparisonMetric.Records, EvidencePolicy.IncludeCorrelated,
            new TimeRange(0, 10 * Tick), new TimeRange(10 * Tick, 20 * Tick), [TcpCovered], [TcpCovered],
            [
                new ComparedRelationship(new(10, 10, 10, 0, TcpCovered), new(20, 20, 20, 0, TcpCovered), ComparisonChange.Increased, null)
                {
                    Relationship = edge,
                },
                new ComparedRelationship(new(4, 4, 4, 0, TcpCovered), new(0, 0, 0, 0, gone), ComparisonChange.NotComparable, NotCollected)
                {
                    Host = new OtherHostReach(reach, reach.Addresses[0]),
                },
                new ComparedRelationship(new(0, 0, 0, 0, TcpCovered), new(2, 2, 2, 0, TcpCovered), ComparisonChange.Appeared, null)
                {
                    Host = new OtherHostReach(accepted, accepted.Addresses[0]),
                },
                new ComparedRelationship(new(0, 0, 0, 0, TcpCovered), new(1, 1, 1, 0, TcpCovered), ComparisonChange.Appeared, null)
                {
                    Host = new OtherHostReach(again, again.Addresses[0]),
                },
            ])
        {
            Processes = new Dictionary<ProcessInstanceId, ProcessNode>
            {
                [Client] = new(Client, 98765, Secret, "client", executable, 0.5, 0.5, CoverageState.Covered),
                [Server] = new(Server, 98766, Secret, "server", executable, 0.5, 0.5, CoverageState.Covered),
                [Unnamed] = new(Unnamed, 51515, ProcessNode.PidName(51515), "client", ProcessGroup.UnwitnessedExecutableKey, 0.5,
                    0.5, CoverageState.Covered),
                [Namesake] = new(Namesake, 62626, Secret, "client", elsewhere, 0.5, 0.5, CoverageState.Covered),
            },
            Quiet = 7,
        };
    }
}
