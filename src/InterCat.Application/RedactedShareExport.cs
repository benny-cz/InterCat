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
        + "coverage and quality; randomly pseudonymized entity, owner, endpoint and activity relationships.";

    private static readonly JsonSerializerOptions Json = CreateJson();

    public static string Ranking(ExportFormat format, ExportContext context, IReadOnlyList<LadderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var tokens = new Tokens();
        Guid reportId = Guid.NewGuid();
        Ranked[] safe = [.. rows.Select(row => new Ranked(
            tokens.For("entity", row.Key), row.ObservationCount, row.KnownBytes,
            row.Mechanism, row.Coverage, row.Side, row.DescendsTo))];
        return format switch
        {
            ExportFormat.Json => JsonSerializer.Serialize(new
            {
                Contract, Kind = "ranking", ReportId = reportId, Redaction = PolicyDescription(),
                Context = SafeContext(context), Rows = safe,
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
    };

    private static SharedObservation Safe(SessionEvidenceRecord record, Tokens tokens)
    {
        ObservationRowV1 row = record.Observation;
        bool bound = record.Owner is { AdmittedUnderPolicy: true, Instance: not null };
        return new(
            tokens.For("record", record.ObservationId.RawRecordId.ToString()),
            bound ? tokens.For("process", record.Owner!.Instance!.Value.ToString()) : null,
            bound ? tokens.ForNullable("executable", record.Owner!.ImageName) : null,
            tokens.Endpoint(row.EndpointAddressFamily, row.SourceEndpointAddress, row.SourceEndpointPort),
            tokens.Endpoint(row.EndpointAddressFamily, row.DestinationEndpointAddress, row.DestinationEndpointPort),
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

    private sealed record Ranked(string EntityToken, long Observations, long? KnownBytes,
        Mechanism Mechanism, CoverageState Coverage, AccountingSide AccountingSide, DetailLevel DescendsTo);

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

        public string? Endpoint(byte? family, uint? address, ushort? port) =>
            address is null && port is null ? null : For("endpoint",
                string.Create(CultureInfo.InvariantCulture, $"{family}:{address}:{port}"));
    }

    private static string RankingCsv(Guid reportId, ExportContext context, IReadOnlyList<Ranked> rows)
    {
        var csv = new StringBuilder();
        Line(csv, [.. ContextHeader, "row_present", "entity_token", "observations", "known_bytes", "mechanism", "coverage",
            "accounting_side", "descends_to"]);
        if (rows.Count == 0)
            Line(csv, [.. ContextCells(reportId, context, "ranking"), "false", .. Enumerable.Repeat(string.Empty, 7)]);
        foreach (Ranked row in rows)
            Line(csv, [.. ContextCells(reportId, context, "ranking"), "true", row.EntityToken,
                Number(row.Observations), Number(row.KnownBytes), row.Mechanism.ToString(),
                row.Coverage.ToString(), row.AccountingSide.ToString(), row.DescendsTo.ToString()]);
        return csv.ToString();
    }

    private static string EvidenceCsv(Guid reportId, ExportContext context, IReadOnlyList<SharedObservation> records)
    {
        var csv = new StringBuilder();
        Line(csv, [.. ContextHeader, "row_present", "record_token", "owner_token", "executable_token", "source_endpoint_token",
            "destination_endpoint_token", "resource_token", "source_identifier_token", "activity_token",
            "related_activity_token", "session_relative_ticks", "mechanism", "layer", "kind", "direction",
            "bytes", "byte_domain", "byte_availability", "status_code", "status_availability",
            "attribution", "correlation", "measurement", "timing", "owner_strength", "owner_reason"]);
        if (records.Count == 0)
            Line(csv, [.. ContextCells(reportId, context, "evidence"), "false", .. Enumerable.Repeat(string.Empty, 25)]);
        foreach (SharedObservation row in records)
            Line(csv, [.. ContextCells(reportId, context, "evidence"), "true", row.RecordToken, Cell(row.OwnerToken),
                Cell(row.ExecutableToken), Cell(row.SourceEndpointToken), Cell(row.DestinationEndpointToken),
                Cell(row.ResourceToken), Cell(row.SourceIdentifierToken), Cell(row.ActivityToken),
                Cell(row.RelatedActivityToken), Number(row.SessionRelativeTicks), row.Mechanism.ToString(),
                row.Layer.ToString(), row.Kind.ToString(), row.Direction.ToString(), Number(row.Bytes),
                row.ByteDomain?.ToString() ?? string.Empty, row.ByteAvailability.ToString(), Number(row.StatusCode),
                row.StatusAvailability.ToString(), row.Attribution.ToString(), row.Correlation.ToString(),
                row.Measurement.ToString(), row.Timing.ToString(), row.OwnerStrength?.ToString() ?? string.Empty,
                row.OwnerReason?.ToString() ?? string.Empty]);
        return csv.ToString();
    }

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

    private static void Line(StringBuilder csv, IReadOnlyList<string> cells)
    {
        for (int index = 0; index < cells.Count; index++)
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
