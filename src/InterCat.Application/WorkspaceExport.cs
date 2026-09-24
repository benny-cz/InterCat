using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How an export is written: self-describing JSON, or CSV that repeats its context in every row.</summary>
public enum ExportFormat
{
    Json,
    Csv,
}

/// <summary>
/// What one export names: the applied snapshot it was taken from and the scope its rows answer (plan §6.4). An export
/// that holds fewer rows than its scope has says so rather than passing a loaded page off as the whole result.
/// </summary>
public sealed record ExportContext(
    Guid? SessionId,
    long? Generation,
    DetailLevel Rung,
    string Breadcrumb,
    IReadOnlyList<ImpliedFilter> Filters,
    TimeRange? Interval,
    string Scope,
    bool Complete,
    IReadOnlyList<string> Caveats,
    DateTimeOffset ExportedUtc);

/// <summary>
/// Exports what a rung shows - its ranked rows, or the evidence records loaded at the evidence rung - as JSON that
/// describes itself, or CSV that repeats its context in every row. Evidence exports carry normalized metadata and the
/// raw locator only: no body or extended-data bytes ever leave through this path. CSV text cells that a spreadsheet
/// would evaluate as a formula are neutralized, because process and resource names come from the observed machine.
/// </summary>
public static class WorkspaceExport
{
    public const string Contract = "intercat-export-v1";

    private static readonly JsonSerializerOptions Json = CreateJson();

    /// <summary>
    /// A ranked rung's export context: its breadcrumb and filters, and the interval its counts answer, which is a
    /// brushed interval only once its counts have been applied. The Desktop and <c>icat export</c> both build it here,
    /// so the two name one snapshot the same way (R18).
    /// </summary>
    public static ExportContext RankingContext(
        Guid? sessionId,
        long? generation,
        DetailLadder ladder,
        TimeRange? appliedInterval,
        string disclosure,
        DateTimeOffset exportedUtc)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        ArgumentException.ThrowIfNullOrWhiteSpace(disclosure);
        return new(
            sessionId,
            generation,
            ladder.Current.Level,
            LadderProjection.Breadcrumb(ladder),
            [.. ladder.Current.Filters],
            appliedInterval,
            appliedInterval is { } range
                ? "Ranked within " + WorkspaceTime.FormatRange(range, CultureInfo.InvariantCulture)
                : "Whole session",
            true,
            [disclosure],
            exportedUtc);
    }

    /// <summary>
    /// An evidence export's context: the records' own scope, complete only when every record of it is included. An
    /// incomplete export says what was left out and how to get the rest, in the words of whoever produced it.
    /// </summary>
    public static ExportContext EvidenceContext(
        Guid? sessionId,
        long? generation,
        DetailLadder ladder,
        EvidenceScope scope,
        bool complete,
        string disclosure,
        string incompleteAdvice,
        DateTimeOffset exportedUtc)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(disclosure);
        ArgumentException.ThrowIfNullOrWhiteSpace(incompleteAdvice);
        return new(
            sessionId,
            generation,
            ladder.Current.Level,
            LadderProjection.Breadcrumb(ladder),
            [.. ladder.Current.Filters],
            scope.Interval,
            scope.Description,
            complete,
            [disclosure, complete ? "Every record of this scope is included." : incompleteAdvice],
            exportedUtc);
    }

    /// <summary>Ranked rows in the chosen format.</summary>
    public static string Ranking(ExportFormat format, ExportContext context, IReadOnlyList<LadderRow> rows) =>
        format == ExportFormat.Csv ? RankingCsv(context, rows) : RankingJson(context, rows);

    /// <summary>Evidence records in the chosen format.</summary>
    public static string Evidence(ExportFormat format, ExportContext context, IReadOnlyList<SessionEvidenceRecord> records) =>
        format == ExportFormat.Csv ? EvidenceCsv(context, records) : EvidenceJson(context, records);

    public static string RankingJson(ExportContext context, IReadOnlyList<LadderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        return JsonSerializer.Serialize(new
        {
            Contract,
            Kind = "ranking",
            Context = Describe(context),
            Rows = rows.Select(row => new
            {
                row.Key,
                row.Label,
                row.Detail,
                Observations = row.ObservationCount,
                row.KnownBytes,
                row.Mechanism,
                row.Coverage,
                AccountingSide = row.Side,
                row.DescendsTo,
            }),
        }, Json);
    }

    public static string EvidenceJson(ExportContext context, IReadOnlyList<SessionEvidenceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(records);
        return JsonSerializer.Serialize(new
        {
            Contract,
            Kind = "evidence",
            Context = Describe(context),
            Records = records.Select(record => Evidence(record)),
        }, Json);
    }

    public static string RankingCsv(ExportContext context, IReadOnlyList<LadderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var csv = new StringBuilder();
        Line(csv, [.. ContextHeader, "key", "label", "detail", "observations", "known_bytes", "mechanism", "coverage",
            "accounting_side", "descends_to"]);
        foreach (LadderRow row in rows)
        {
            Line(csv, [.. ContextCells(context), Text(row.Key), Text(row.Label), Text(row.Detail),
                Number(row.ObservationCount), Number(row.KnownBytes), row.Mechanism.ToString(), row.Coverage.ToString(),
                row.Side.ToString(), row.DescendsTo.ToString()]);
        }

        return csv.ToString();
    }

    public static string EvidenceCsv(ExportContext context, IReadOnlyList<SessionEvidenceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(records);
        var csv = new StringBuilder();
        Line(csv, [.. ContextHeader, "session_relative_ns", "native_ticks", "what", "mechanism", "kind", "bytes",
            "byte_domain", "byte_availability", "endpoints", "owner_pid", "owner_instance", "owner_executable",
            "owner_strength", "provider", "event_id", "version", "attribution", "correlation", "measurement", "timing",
            "raw_stream", "raw_epoch", "raw_ordinal", "fact_key", "segment", "segment_row"]);
        foreach (SessionEvidenceRecord record in records)
        {
            ObservationRowV1 row = record.Observation;
            Line(csv, [.. ContextCells(context), Number(row.SessionRelativeTicks), Number(row.NativeTicks),
                Text(EvidenceRowText.Title(row)), row.Mechanism.ToString(), row.Kind.ToString(), Number(row.ByteValue),
                row.ByteDomain?.ToString() ?? string.Empty, row.ByteAvailability.ToString(),
                Text(EvidenceRowText.Endpoints(row) ?? string.Empty), Number(row.OwnerProcessId),
                record.Owner?.Instance?.ToString() ?? string.Empty, Text(record.Owner?.ImageName ?? string.Empty),
                record.Owner?.Strength.ToString() ?? string.Empty, Text(EvidenceRowText.ProviderName(row.ProviderId)),
                Number(row.EventId), Number(row.DescriptorVersion), row.AttributionQuality.ToString(),
                row.CorrelationQuality.ToString(), row.MeasurementQuality.ToString(), row.TimingQuality.ToString(),
                Number(row.RawStreamId), Number(row.RawSourceEpoch), Number((decimal)row.RawRecordOrdinal),
                row.FactKey.ToString(), Text(record.SegmentName), Number(record.SegmentRow)]);
        }

        return csv.ToString();
    }

    /// <summary>A file name that names the snapshot: rung, session prefix and generation, never a path the user did not choose.</summary>
    public static string SuggestedName(ExportContext context, string extension)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        string session = context.SessionId is { } id ? id.ToString("N")[..8] : "workspace";
        string generation = context.Generation is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"-g{number}")
            : string.Empty;
        return $"intercat-{NavigationState.Name(context.Rung).ToLowerInvariant()}-{session}{generation}.{extension}";
    }

    private static object Describe(ExportContext context) => new
    {
        context.SessionId,
        context.Generation,
        Rung = NavigationState.Name(context.Rung),
        context.Breadcrumb,
        Filters = context.Filters.Select(filter => new { filter.Field, filter.Value, filter.Key, filter.Level }),
        Interval = context.Interval is { } range
            ? new
            {
                StartTicks = range.StartTicks,
                EndTicks = range.EndTicks,
                Unit = "100-nanosecond session-relative presentation ticks",
                Text = WorkspaceTime.FormatRange(range, CultureInfo.InvariantCulture),
            }
            : null,
        context.Scope,
        context.Complete,
        context.Caveats,
        context.ExportedUtc,
        Content = "Normalized metadata and raw locators only; no payload or extended-data bytes.",
    };

    private static object Evidence(SessionEvidenceRecord record)
    {
        ObservationRowV1 row = record.Observation;
        return new
        {
            ObservationId = new
            {
                Capture = record.ObservationId.RawRecordId.CaptureId.Value,
                Stream = row.RawStreamId,
                Epoch = row.RawSourceEpoch,
                Ordinal = row.RawRecordOrdinal,
                Normalizer = record.ObservationId.NormalizerContractVersion.Value,
                FactKey = row.FactKey.ToString(),
            },
            SessionRelativeNanoseconds = row.SessionRelativeTicks,
            row.NativeTicks,
            What = EvidenceRowText.Title(row),
            row.Mechanism,
            row.Kind,
            row.Direction,
            Bytes = row.ByteValue,
            row.ByteDomain,
            row.ByteAvailability,
            Endpoints = EvidenceRowText.Endpoints(row),
            Owner = new
            {
                ProcessId = row.OwnerProcessId,
                Instance = record.Owner?.Instance?.ToString(),
                Executable = record.Owner?.ImageName,
                Strength = record.Owner?.Strength,
                Reason = record.Owner?.Reason,
            },
            Provider = new
            {
                Id = row.ProviderId,
                Name = EvidenceRowText.ProviderName(row.ProviderId),
                row.EventId,
                Version = row.DescriptorVersion,
                row.SchemaFingerprint,
            },
            Quality = new
            {
                Attribution = row.AttributionQuality,
                Correlation = row.CorrelationQuality,
                Measurement = row.MeasurementQuality,
                Timing = row.TimingQuality,
            },
            Location = new { Segment = record.SegmentName, Row = record.SegmentRow },
        };
    }

    private static readonly string[] ContextHeader =
        ["session_id", "generation", "rung", "interval_start_ticks", "interval_end_ticks", "complete"];

    private static string[] ContextCells(ExportContext context) =>
    [
        context.SessionId?.ToString("N") ?? string.Empty,
        Number(context.Generation),
        NavigationState.Name(context.Rung),
        Number(context.Interval?.StartTicks),
        Number(context.Interval?.EndTicks),
        context.Complete ? "true" : "false",
    ];

    private static string Number<T>(T? value)
        where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number<T>(T value)
        where T : struct, IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>
    /// A text cell a spreadsheet will not evaluate: a leading <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage
    /// return is prefixed with an apostrophe, the documented neutralization for formula injection.
    /// </summary>
    private static string Text(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    private static void Line(StringBuilder csv, IReadOnlyList<string> cells)
    {
        for (int index = 0; index < cells.Count; index++)
        {
            if (index > 0) csv.Append(',');
            string cell = cells[index];
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            {
                csv.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                csv.Append(cell);
            }
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
