using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A metadata-only sharing report, not a reopenable session. Every output field is allowlisted: no original
/// source, journal coordinate, session identity, observed name, address, PID or raw content is serialized.
/// </summary>
public static class RedactedShareExport
{
    public const string Contract = "intercat-share-report-v1";
    public const string Policy = "share-report-redaction-v1";

    public const string Warning = "Pseudonymized for sharing, not anonymous. Counts, relative times, sizes and "
        + "patterns can still identify a workload. Review the result before sharing it.";
    public const string Omitted = "Session and capture IDs; process, executable and user names/IDs; endpoint addresses "
        + "and ports; resource names; raw-record locators; provider/schema IDs; original files; body and extended bytes; "
        + "free-text filters, breadcrumbs and caveats.";
    public const string Retained = "Rung, relative time, observed counts and sizes, mechanism, direction, status, "
        + "quality, and what the capture covered over the scope and for each row; how many of InterCat's own processes a "
        + "ranking set aside, and their records; a comparison's two intervals, each row's counts and rates in each, its "
        + "change, or why none is stated, and the sources each interval is missing; randomly pseudonymized entity, owner, "
        + "endpoint, process, executable, host and activity relationships.";

    private static readonly JsonSerializerOptions Json = CreateJson();

    public static string Ranking(ExportFormat format, ExportContext context, IReadOnlyList<LadderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var tokens = new Tokens();
        Guid reportId = Guid.NewGuid();
        Ranked[] safe = [.. rows.Select(row => new Ranked(
            tokens.For("entity", row.Key), row.ObservationCount, row.KnownBytes,
            row.Mechanism, row.Coverage, row.Side, row.DescendsTo,
            row.Ranked?.Value, row.Ranked?.Measured, row.Ranked?.Unmeasured, row.Ranked?.Failed))];
        return format switch
        {
            ExportFormat.Json => JsonSerializer.Serialize(new
            {
                Contract, Kind = "ranking", ReportId = reportId, Redaction = PolicyDescription(),
                Context = SafeContext(context), RankedBy = WorkspaceExport.RankingName(context.RankedBy),
                GroupedBy = WorkspaceExport.GroupingName(context.GroupedBy),
                SetAside = new { Processes = context.SetAsideProcesses, Records = context.SetAsideRecords }, Rows = safe,
            }, Json),
            ExportFormat.Csv => RankingCsv(reportId, context, safe),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public static string Evidence(ExportFormat format, ExportContext context,
        IReadOnlyList<SessionEvidenceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(records);
        var tokens = new Tokens();
        Guid reportId = Guid.NewGuid();
        SharedObservation[] safe = [.. records.Select(record => Safe(record, tokens))];
        return format switch
        {
            ExportFormat.Json => JsonSerializer.Serialize(new
            {
                Contract, Kind = "evidence", ReportId = reportId, Redaction = PolicyDescription(),
                Context = SafeContext(context), Records = safe,
            }, Json),
            ExportFormat.Csv => EvidenceCsv(reportId, context, safe),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    /// <summary>
    /// What a shared comparison says of the clock its two intervals are read on: one session's own, so no alignment of
    /// captures applies and no alignment revision is named (plan M12).
    /// </summary>
    public const string NoAlignment = "None: both intervals are of one session and read on its own clock, so no alignment "
        + "between captures applies.";

    /// <summary>
    /// A comparison of two intervals of one session as a report to share (`contracts/comparison-v1.md` §8b, plan M12): what
    /// it compared by, under which evidence policy, byte domain and accounting; each interval's relative time, what the
    /// capture covered over it and the sources it is missing there; that no alignment applies; and its rows, the most
    /// changed first, at most <paramref name="limit"/>, each one's ends random tokens, with its measure over each interval
    /// and its change, or why none is stated. No name, PID, address, port, key, session identity or generation is written.
    /// </summary>
    public static string Comparison(ExportFormat format, IntervalComparison comparison, DateTimeOffset exportedUtc,
        int limit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var tokens = new Tokens();
        Guid reportId = Guid.NewGuid();
        SharedComparisonRow[] rows = [.. comparison.Relationships.Take(limit)
            .Select((row, index) => Safe(row, index + 1, comparison, tokens))];
        bool bytes = comparison.Metric == ComparisonMetric.BytesSent;
        var scope = new SharedComparison(
            exportedUtc, "100-nanosecond session-relative presentation ticks", comparison.Metric, comparison.Policy,
            comparison.Grouping, bytes ? ByteDomain.TransportObserved : null, bytes ? AccountingSide.SendSide : null, null,
            NoAlignment, ComparisonText.RatesNote, Shared(comparison.Baseline, comparison.BaselineCoverage),
            Shared(comparison.Compared, comparison.ComparedCoverage), comparison.Relationships.Count, rows.Length,
            rows.Length == comparison.Relationships.Count, comparison.Quiet,
            comparison.Grouping == ComparisonGrouping.Executable ? comparison.Unmatched : null);
        return format switch
        {
            ExportFormat.Json => JsonSerializer.Serialize(new
            {
                Contract, Kind = "comparison", ReportId = reportId, Redaction = PolicyDescription(), Comparison = scope,
                Rows = rows,
            }, Json),
            ExportFormat.Csv => ComparisonCsv(reportId, scope, rows),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public static string SuggestedName(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return $"intercat-share-report.{extension}";
    }

    private static object PolicyDescription() => new
    {
        Policy,
        TokenScope = "Random tokens are consistent within this report only; no source-to-token mapping is included.",
        Omitted,
        Retained,
        Warning,
        OriginalSourcesIncluded = false,
        RawLocatorsIncluded = false,
        ContentBytesIncluded = false,
    };

    private static object SafeContext(ExportContext context) => new
    {
        Rung = NavigationState.Name(context.Rung),
        IntervalStartTicks = context.Interval?.StartTicks,
        IntervalEndTicks = context.Interval?.EndTicks,
        IntervalUnit = "100-nanosecond session-relative presentation ticks",
        context.Complete,
        context.ExportedUtc,
        FilterCount = context.Filters.Count,
        ScopeKind = context.Rung == DetailLevel.Evidence ? "source observations" : "ranked rows",
        CompletenessMeaning = "Complete means all rows of the selected export scope, not complete capture coverage.",
        Coverage = context.Coverage.Select(entry => new SharedCoverage(entry.Mechanism, entry.State, entry.Reason)).ToArray(),
        CoverageSummary = Summary(context),
    };

    /// <summary>
    /// What the capture covered over the report's scope, in the inspector's words (R21), or null when nothing was judged.
    /// Every part of it is a mechanism's name, a state's words or a reason's fixed template, whose only variable parts are
    /// counts: no provider, host or record value enters it.
    /// </summary>
    private static string? Summary(ExportContext context) =>
        CoverageText.Describe(context.Coverage) is { Length: > 0 } said ? said : null;

    private static SharedObservation Safe(SessionEvidenceRecord record, Tokens tokens)
    {
        ObservationRowV1 row = record.Observation;
        bool bound = record.Owner is { AdmittedUnderPolicy: true, Instance: not null };
        return new(
            tokens.For("record", record.ObservationId.RawRecordId.ToString()),
            bound ? tokens.For("process", record.Owner!.Instance!.Value.ToString()) : null,
            bound ? tokens.ForNullable("executable", record.Owner!.ImageName) : null,
            tokens.Endpoint(row.EndpointAddressFamily, row.SourceEndpointAddress, row.SourceEndpointAddressV6,
                row.SourceEndpointPort),
            tokens.Endpoint(row.EndpointAddressFamily, row.DestinationEndpointAddress, row.DestinationEndpointAddressV6,
                row.DestinationEndpointPort),
            tokens.ForNullable("resource", row.ResourceName),
            row.SourceIdentifier is { } sourceId ? tokens.For("identifier", sourceId.ToString("N")) : null,
            row.ActivityId is { } activity ? tokens.For("activity", activity.ToString("N")) : null,
            row.RelatedActivityId is { } related ? tokens.For("activity", related.ToString("N")) : null,
            row.SessionRelativeTicks / 100,
            row.Mechanism, row.Layer, row.Kind, row.Direction,
            row.ByteValue, row.ByteDomain, row.ByteAvailability, row.StatusCode, row.StatusAvailability,
            row.AttributionQuality, row.CorrelationQuality, row.MeasurementQuality, row.TimingQuality,
            record.Owner?.Strength, record.Owner?.Reason);
    }

    /// <summary>
    /// One ranked row: counts, sizes and enumerations only. Under a byte ranking it carries the ranked byte sum and how many
    /// records measured it or recorded no size (admitted in revision 189); under an RPC call ranking, the calls completed,
    /// the stops paired with no start and the failed calls (revision 190). The ranking's name is one of five fixed words.
    /// </summary>
    private sealed record Ranked(string EntityToken, long Observations, long? KnownBytes,
        Mechanism Mechanism, CoverageState Coverage, AccountingSide AccountingSide, DetailLevel DescendsTo,
        long? RankedValue, long? RankedMeasured, long? RankedUnmeasured, long? RankedFailed);

    /// <summary>
    /// One mechanism's coverage over the report's scope (admitted in revision 372): two enumerations and the fact behind
    /// them, one of `coverage-v2` §4's fixed templates whose only variable parts are invariant counts, so a ledger's provider
    /// names and identifiers never reach it.
    /// </summary>
    private sealed record SharedCoverage(Mechanism Mechanism, CoverageState State, string Reason);

    /// <summary>
    /// One interval of a shared comparison: its relative ticks; what the capture covered over it, each mechanism's state
    /// and the fact behind it, and the same in the inspector's words; and the sources it is missing - each mechanism it
    /// did not collect in full, or cannot show it did.
    /// </summary>
    private static SharedInterval Shared(TimeRange interval, IReadOnlyList<MechanismCoverage> coverage)
    {
        SharedCoverage[] each = [.. coverage.Select(entry => new SharedCoverage(entry.Mechanism, entry.State, entry.Reason))];
        return new(interval.StartTicks, interval.EndTicks, each,
            CoverageText.Describe(coverage) is { Length: > 0 } said ? said : null,
            [.. each.Where(entry => entry.State is CoverageState.PartialGap or CoverageState.NotCollected
                or CoverageState.UnknownCoverage)]);
    }

    /// <summary>
    /// One row of a comparison with its ends as tokens: a process by its instance, an executable by the path its group is
    /// keyed by - none where no executable was witnessed - and another host by its address. A row of executables names no
    /// process.
    /// </summary>
    private static SharedComparisonRow Safe(ComparedRelationship row, int rank, IntervalComparison comparison, Tokens tokens)
    {
        string Process(ProcessInstanceId id) => tokens.For("process", id.ToString());
        string? Executable(string? group) => group is null || group == ProcessGroup.UnwitnessedExecutableKey
            ? null
            : tokens.For("executable", group);
        string? Of(ProcessInstanceId id) => Executable(comparison.Processes[id].GroupKey);
        string Host(string address) => tokens.For("host", address);
        (string Kind, string? Source, string? SourceExecutable, string? Target, string? TargetExecutable, string? Host) ends = row switch
        {
            { Relationship: { } edge } =>
                ("Processes", Process(edge.SourceId), Of(edge.SourceId), Process(edge.TargetId), Of(edge.TargetId), null),
            { Host: { } reach } =>
                ("OtherHost", Process(reach.Link.Process), Of(reach.Link.Process), null, null, Host(reach.Address.Address)),
            { Executables: { } pair } => ("Executables", null, Executable(pair.Source), null, Executable(pair.Target), null),
            _ => ("ExecutableToOtherHost", null, Executable(row.ExecutableHost!.Executable), null, null,
                Host(row.ExecutableHost.Address)),
        };
        SharedMeasure Measured(ComparedMeasure measure, TimeRange interval) => new(measure.Records, measure.Value,
            measure.Measured, measure.Unmeasured, ComparisonText.Rounded(measure.PerSecond(interval)),
            new(measure.Coverage.Mechanism, measure.Coverage.State, measure.Coverage.Reason));
        return new(
            rank, ends.Kind, row.Mechanism, ends.Source, ends.SourceExecutable, ends.Target, ends.TargetExecutable, ends.Host,
            ComparisonText.Connected(row),
            Measured(row.Baseline, comparison.Baseline), Measured(row.Compared, comparison.Compared),
            row.Change, row.FromLowerBound, ComparisonText.DifferencePerSecond(row, comparison), row.Reason);
    }

    /// <summary>
    /// What a shared comparison compared (admitted in revision 754): enumerations, fixed sentences, each interval's
    /// relative ticks and coverage, and counts of its rows - nothing that names the session, its generation, a process or a
    /// host. No alignment revision is named: both intervals are of one clock.
    /// </summary>
    private sealed record SharedComparison(
        DateTimeOffset ExportedUtc,
        string IntervalUnit,
        ComparisonMetric ComparedBy,
        EvidencePolicy EvidencePolicy,
        ComparisonGrouping GroupedBy,
        ByteDomain? ByteDomain,
        AccountingSide? Accounting,
        string? AlignmentRevision,
        string Alignment,
        string RatesMeaning,
        SharedInterval Baseline,
        SharedInterval Compared,
        int RowsCompared,
        int RowsListed,
        bool Complete,
        int Quiet,
        UnmatchedRows? Unmatched);

    private sealed record SharedInterval(long StartTicks, long EndTicks, SharedCoverage[] Coverage, string? CoverageSummary,
        SharedCoverage[] MissingSources);

    /// <summary>
    /// One row of a shared comparison (admitted in revision 754): its rank, kind and mechanism; its ends as random tokens -
    /// two processes and their executables, or a process, its executable and another host; which end connected or called;
    /// its measure over each interval; and its change, or why none is stated, one of `comparison-v1` §5's fixed sentences,
    /// whose only variable parts are mechanism names, a coverage reason of `coverage-v2` §4, epoch numbers and counts.
    /// </summary>
    private sealed record SharedComparisonRow(
        int Rank,
        string Kind,
        Mechanism Mechanism,
        string? SourceToken,
        string? SourceExecutableToken,
        string? TargetToken,
        string? TargetExecutableToken,
        string? HostToken,
        string Connected,
        SharedMeasure Baseline,
        SharedMeasure Compared,
        ComparisonChange Change,
        bool FromLowerBound,
        double? DifferencePerSecond,
        string? Reason);

    /// <summary>A row's measure over one interval: counts, its rate rounded to three places, and its mechanism's coverage there.</summary>
    private sealed record SharedMeasure(long Records, long? Value, long Measured, long Unmeasured, double? PerSecond,
        SharedCoverage Coverage);

    private sealed record SharedObservation(
        string RecordToken, string? OwnerToken, string? ExecutableToken,
        string? SourceEndpointToken, string? DestinationEndpointToken,
        string? ResourceToken, string? SourceIdentifierToken,
        string? ActivityToken, string? RelatedActivityToken,
        long? SessionRelativeTicks,
        Mechanism Mechanism, ObservationLayer Layer, ObservationKind Kind, Direction Direction,
        long? Bytes, ByteDomain? ByteDomain, FieldAvailability ByteAvailability,
        long? StatusCode, FieldAvailability StatusAvailability,
        QualityLevel Attribution, QualityLevel Correlation, QualityLevel Measurement, QualityLevel Timing,
        RelationStrength? OwnerStrength, ProcessBindingReason? OwnerReason);

    private sealed class Tokens
    {
        private readonly Dictionary<(string Kind, string Value), string> known = [];
        private readonly HashSet<string> issued = new(StringComparer.Ordinal);

        public string For(string kind, string value)
        {
            if (known.TryGetValue((kind, value), out string? token)) return token;
            do
            {
                token = kind + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            } while (!issued.Add(token));
            known[(kind, value)] = token;
            return token;
        }

        public string? ForNullable(string kind, string? value) =>
            string.IsNullOrEmpty(value) ? null : For(kind, value);

        /// <summary>One token per endpoint the row names, whichever family's column holds its address.</summary>
        public string? Endpoint(byte? family, uint? address, UInt128? address6, ushort? port) =>
            address is null && address6 is null && port is null ? null : For("endpoint",
                string.Create(CultureInfo.InvariantCulture, $"{family}:{address}:{address6}:{port}"));
    }

    private static string RankingCsv(Guid reportId, ExportContext context, IReadOnlyList<Ranked> rows)
    {
        var csv = new StringBuilder();
        Line(csv, [.. ContextHeader, "row_present", "entity_token", "observations", "known_bytes", "mechanism", "coverage",
            "accounting_side", "descends_to", "ranked_by", "ranked_value", "ranked_measured", "ranked_unmeasured",
            "ranked_failed", "scope_coverage", .. WorkspaceExport.SetAsideHeader]);
        string rankedBy = WorkspaceExport.RankingName(context.RankedBy);
        string coverage = Summary(context) ?? string.Empty;
        string[] setAside = WorkspaceExport.SetAsideCells(context);
        if (rows.Count == 0)
            Line(csv, [.. ContextCells(reportId, context, "ranking"), "false", .. Enumerable.Repeat(string.Empty, 7),
                rankedBy, string.Empty, string.Empty, string.Empty, string.Empty, coverage, .. setAside]);
        foreach (Ranked row in rows)
            Line(csv, [.. ContextCells(reportId, context, "ranking"), "true", row.EntityToken,
                Number(row.Observations), Number(row.KnownBytes), row.Mechanism.ToString(),
                row.Coverage.ToString(), row.AccountingSide.ToString(), row.DescendsTo.ToString(), rankedBy,
                Number(row.RankedValue), Number(row.RankedMeasured), Number(row.RankedUnmeasured), Number(row.RankedFailed),
                coverage, .. setAside]);
        return csv.ToString();
    }

    private static string EvidenceCsv(Guid reportId, ExportContext context, IReadOnlyList<SharedObservation> records)
    {
        var csv = new StringBuilder();
        Line(csv, [.. ContextHeader, "row_present", "record_token", "owner_token", "executable_token", "source_endpoint_token",
            "destination_endpoint_token", "resource_token", "source_identifier_token", "activity_token",
            "related_activity_token", "session_relative_ticks", "mechanism", "layer", "kind", "direction",
            "bytes", "byte_domain", "byte_availability", "status_code", "status_availability",
            "attribution", "correlation", "measurement", "timing", "owner_strength", "owner_reason", "scope_coverage"]);
        string coverage = Summary(context) ?? string.Empty;
        if (records.Count == 0)
            Line(csv, [.. ContextCells(reportId, context, "evidence"), "false", .. Enumerable.Repeat(string.Empty, 25),
                coverage]);
        foreach (SharedObservation row in records)
            Line(csv, [.. ContextCells(reportId, context, "evidence"), "true", row.RecordToken, Cell(row.OwnerToken),
                Cell(row.ExecutableToken), Cell(row.SourceEndpointToken), Cell(row.DestinationEndpointToken),
                Cell(row.ResourceToken), Cell(row.SourceIdentifierToken), Cell(row.ActivityToken),
                Cell(row.RelatedActivityToken), Number(row.SessionRelativeTicks), row.Mechanism.ToString(),
                row.Layer.ToString(), row.Kind.ToString(), row.Direction.ToString(), Number(row.Bytes),
                row.ByteDomain?.ToString() ?? string.Empty, row.ByteAvailability.ToString(), Number(row.StatusCode),
                row.StatusAvailability.ToString(), row.Attribution.ToString(), row.Correlation.ToString(),
                row.Measurement.ToString(), row.Timing.ToString(), row.OwnerStrength?.ToString() ?? string.Empty,
                row.OwnerReason?.ToString() ?? string.Empty, coverage]);
        return csv.ToString();
    }

    /// <summary>
    /// A shared comparison as CSV: on every line the policy and its disclosures, what was compared and each interval,
    /// then a row's cells, then each interval's missing sources and coverage in words, and what its rates mean; a
    /// comparison with no row writes one line without one, so all of that is still said.
    /// </summary>
    private static string ComparisonCsv(Guid reportId, SharedComparison scope, IReadOnlyList<SharedComparisonRow> rows)
    {
        var csv = new StringBuilder();
        Line(csv, ComparisonHeader);
        string[] context =
        [
            Contract, reportId.ToString("N"), Policy, "comparison",
            scope.ExportedUtc.ToString("O", CultureInfo.InvariantCulture), "false", "false", "false",
            "Random tokens are consistent within this report only; no source-to-token mapping is included.",
            Omitted, Retained, Warning, scope.IntervalUnit, scope.ComparedBy.ToString(), scope.EvidencePolicy.ToString(),
            scope.GroupedBy.ToString(), scope.ByteDomain?.ToString() ?? string.Empty, scope.Accounting?.ToString() ?? string.Empty,
            Cell(scope.AlignmentRevision), scope.Alignment, Number(scope.Baseline.StartTicks), Number(scope.Baseline.EndTicks),
            Number(scope.Compared.StartTicks), Number(scope.Compared.EndTicks), Number(scope.RowsCompared),
            Number(scope.RowsListed), scope.Complete ? "true" : "false", Number(scope.Quiet), Number(scope.Unmatched?.Rows),
            Number(scope.Unmatched?.BaselineRecords), Number(scope.Unmatched?.ComparedRecords),
        ];
        string[] said =
        [
            Missing(scope.Baseline), Missing(scope.Compared), Cell(scope.Baseline.CoverageSummary),
            Cell(scope.Compared.CoverageSummary), scope.RatesMeaning,
        ];
        if (rows.Count == 0)
            Line(csv, [.. context, "false", .. Enumerable.Repeat(string.Empty, 25), .. said]);
        foreach (SharedComparisonRow row in rows)
            Line(csv, [.. context, "true", Number(row.Rank), row.Kind, row.Mechanism.ToString(), Cell(row.SourceToken),
                Cell(row.SourceExecutableToken), Cell(row.TargetToken), Cell(row.TargetExecutableToken), Cell(row.HostToken),
                row.Connected, .. Measure(row.Baseline), .. Measure(row.Compared), row.Change.ToString(),
                row.FromLowerBound ? "true" : "false", Number(row.DifferencePerSecond), Cell(row.Reason), .. said]);
        return csv.ToString();

        static string[] Measure(SharedMeasure measure) =>
        [
            Number(measure.Records), Number(measure.Value), Number(measure.Measured), Number(measure.Unmeasured),
            Number(measure.PerSecond), measure.Coverage.State.ToString(),
        ];

        // Each source an interval is missing, as its mechanism and state: "Udp:NotCollected;Http:UnknownCoverage".
        static string Missing(SharedInterval interval) =>
            string.Join(';', interval.MissingSources.Select(entry => $"{entry.Mechanism}:{entry.State}"));
    }

    private static readonly string[] ComparisonHeader =
        ["contract", "report_id", "policy", "kind", "exported_utc", "original_sources_included", "raw_locators_included",
            "content_bytes_included", "token_scope", "omitted", "retained", "warning", "interval_unit", "compared_by",
            "evidence_policy", "grouped_by", "byte_domain", "accounting", "alignment_revision", "alignment",
            "baseline_start_ticks", "baseline_end_ticks", "compared_start_ticks", "compared_end_ticks", "rows_compared",
            "rows_listed", "complete", "quiet", "unmatched_rows", "unmatched_baseline_records", "unmatched_compared_records",
            "row_present", "rank", "row_kind", "mechanism", "source_token", "source_executable_token",
            "target_token", "target_executable_token", "host_token", "connected", "baseline_records", "baseline_value",
            "baseline_measured", "baseline_unmeasured", "baseline_per_second", "baseline_coverage_state", "compared_records",
            "compared_value", "compared_measured", "compared_unmeasured", "compared_per_second", "compared_coverage_state",
            "change", "from_lower_bound", "difference_per_second", "reason", "baseline_missing_sources",
            "compared_missing_sources", "baseline_coverage", "compared_coverage", "rates_meaning"];

    private static readonly string[] ContextHeader =
        ["contract", "report_id", "policy", "kind", "rung", "interval_start_ticks", "interval_end_ticks",
            "complete", "filter_count", "exported_utc", "original_sources_included", "raw_locators_included",
            "content_bytes_included", "token_scope", "omitted", "retained", "warning"];

    private static string[] ContextCells(Guid reportId, ExportContext context, string kind) =>
    [
        Contract, reportId.ToString("N"), Policy, kind, NavigationState.Name(context.Rung),
        Number(context.Interval?.StartTicks), Number(context.Interval?.EndTicks),
        context.Complete ? "true" : "false", Number(context.Filters.Count),
        context.ExportedUtc.ToString("O", CultureInfo.InvariantCulture), "false", "false", "false",
        "Random tokens are consistent within this report only; no source-to-token mapping is included.",
        Omitted, Retained, Warning,
    ];

    private static string Cell(string? value) => value ?? string.Empty;
    private static string Number<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number<T>(T value) where T : struct, IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);

    private static void Line(StringBuilder csv, string[] cells)
    {
        for (int index = 0; index < cells.Length; index++)
        {
            if (index > 0) csv.Append(',');
            string cell = cells[index];
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0)
                csv.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else
                csv.Append(cell);
        }
        csv.Append("\r\n");
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
