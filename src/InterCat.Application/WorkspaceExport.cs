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
    DateTimeOffset ExportedUtc)
{
    /// <summary>What a ranked export's rows are ordered by; records unless a byte ranking was applied (§5.2).</summary>
    public RankingMetric RankedBy { get; init; } = RankingMetric.Records;
}

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
    /// so the two name one snapshot the same way (R18). A byte ranking is named, with what it measures, beside the
    /// disclosure.
    /// </summary>
    public static ExportContext RankingContext(
        Guid? sessionId,
        long? generation,
        DetailLadder ladder,
        TimeRange? appliedInterval,
        string disclosure,
        DateTimeOffset exportedUtc,
        RankingMetric rankedBy = RankingMetric.Records)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        ArgumentException.ThrowIfNullOrWhiteSpace(disclosure);
        if (!Enum.IsDefined(rankedBy)) throw new ArgumentOutOfRangeException(nameof(rankedBy));
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
            rankedBy == RankingMetric.Records ? [disclosure] : [disclosure, RankingCaveat(rankedBy, ladder.Current.Level)],
            exportedUtc)
        {
            RankedBy = rankedBy,
        };
    }

    /// <summary>
    /// What a byte or call ranking measures, as an export states it beside its rows. At a process's rung its rows are its
    /// channels, ranked by its own end's bytes on each.
    /// </summary>
    public static string RankingCaveat(RankingMetric rankedBy, DetailLevel rung = DetailLevel.Machine) => (rankedBy, rung) switch
    {
        (RankingMetric.BytesSent, DetailLevel.ProcessInstance) => "Rows are ranked by bytes sent: transport-observed bytes on "
            + "this process's own send records on each channel, sender-accounted. A channel none of whose sends recorded a size "
            + "is unmeasured and ranks after every measured channel, then channels with no send.",
        (RankingMetric.EndpointBytes, DetailLevel.ProcessInstance) => "Rows are ranked by bytes sent and received: "
            + "transport-observed bytes on every one of this process's own records on each channel, both directions. A channel "
            + "none of whose records recorded a size is unmeasured and ranks after every measured channel.",
        (RankingMetric.BytesReceived, DetailLevel.ProcessInstance) => "Rows are ranked by bytes received: transport-observed "
            + "bytes on this process's own receive records on each channel, receiver-accounted. A channel none of whose "
            + "receives recorded a size is unmeasured and ranks after every measured channel, then channels with no receive.",
        _ => MachineCaveat(rankedBy),
    };

    private static string MachineCaveat(RankingMetric rankedBy) => rankedBy switch
    {
        RankingMetric.BytesSent => "Rows are ranked by bytes sent: transport-observed bytes on each process's own send "
            + "records, sender-accounted. A row none of whose sends recorded a size is unmeasured and ranks after every "
            + "measured row, then rows with no send.",
        RankingMetric.BytesReceived => "Rows are ranked by bytes received: transport-observed bytes on each process's "
            + "own receive records, receiver-accounted. A row none of whose receives recorded a size is unmeasured and "
            + "ranks after every measured row, then rows with no receive.",
        RankingMetric.EndpointBytes => "Rows are ranked by bytes sent and received: transport-observed bytes on every one of "
            + "each process's own records, both directions - endpoint activity, which counts a local transfer at both of its "
            + "ends by design (metrics-v1 §5.1), so the rows' sum is not a transfer total. A row none of whose records "
            + "recorded a size is unmeasured and ranks after every measured row.",
        RankingMetric.RpcErrors => "Rows are ranked by RPC errors: the completed calls each process made or served whose "
            + "stop reported a status other than 0 (metrics-v1 §8a). A call whose stop carried no status is unmeasured, never "
            + "a success; a row with only such calls ranks after every row that knows its outcomes.",
        RankingMetric.RpcCallsMade => "Rows are ranked by RPC calls made: the client calls each process completed, counted by "
            + "their stop (metrics-v1 §8a). A failed call is one whose stop reported a status other than 0; a stop paired with "
            + "no start is stated, never counted, and a row with only such stops ranks after every row that completed a call.",
        RankingMetric.RpcCallsServed => "Rows are ranked by RPC calls served: the server calls each process completed, counted "
            + "by their stop (metrics-v1 §8a). A failed call is one whose stop reported a status other than 0; a stop paired "
            + "with no start is stated, never counted, and a row with only such stops ranks after every row that completed a call.",
        RankingMetric.RpcCallTime => "Rows are ranked by RPC call time, slowest first: the median time the client calls each "
            + "process completed took, from a call's start to its stop in the calling process (metrics-v1 §8a, ClientCall), in "
            + "session nanoseconds. " + TimeCaveat,
        RankingMetric.RpcServeTime => "Rows are ranked by RPC serve time, slowest first: the median time each process took to "
            + "serve the server calls it completed, from a served call's start to its stop (metrics-v1 §8a, ServerExecution), in "
            + "session nanoseconds. " + TimeCaveat,
        _ => throw new ArgumentOutOfRangeException(nameof(rankedBy), rankedBy, "Records need no caveat."),
    };

    private const string TimeCaveat = "A median does not add: a group's is its members' calls taken together, and the rows "
        + "do not partition a total. A stop paired with no start is never timed, and a row with only such stops ranks after "
        + "every row that timed a call.";

    /// <summary>A ranking's name as the command line and an export spell it.</summary>
    public static string RankingName(RankingMetric ranking) => ranking switch
    {
        RankingMetric.Records => "records",
        RankingMetric.BytesSent => "bytes-sent",
        RankingMetric.BytesReceived => "bytes-received",
        RankingMetric.RpcCallsMade => "rpc-calls-made",
        RankingMetric.RpcCallsServed => "rpc-calls-served",
        RankingMetric.EndpointBytes => "bytes-sent-and-received",
        RankingMetric.RpcErrors => "rpc-errors",
        RankingMetric.RpcCallTime => "rpc-call-time-median",
        RankingMetric.RpcServeTime => "rpc-serve-time-median",
        _ => throw new ArgumentOutOfRangeException(nameof(ranking)),
    };

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
            RankedBy = RankingName(context.RankedBy),
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
                Ranked = row.Ranked is { } ranked
                    ? new { Value = ranked.Value, Measured = ranked.Measured, Unmeasured = ranked.Unmeasured, Failed = ranked.Failed }
                    : null,
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
            "accounting_side", "descends_to", "ranked_by", "ranked_value", "ranked_measured", "ranked_unmeasured",
            "ranked_failed"]);
        foreach (LadderRow row in rows)
        {
            Line(csv, [.. ContextCells(context), Text(row.Key), Text(row.Label), Text(row.Detail),
                Number(row.ObservationCount), Number(row.KnownBytes), row.Mechanism.ToString(), row.Coverage.ToString(),
                row.Side.ToString(), row.DescendsTo.ToString(), RankingName(context.RankedBy),
                Number(row.Ranked?.Value), Number(row.Ranked?.Measured), Number(row.Ranked?.Unmeasured),
                Number(row.Ranked?.Failed)]);
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
                Text(EvidenceRowText.Endpoints(row) ?? string.Empty), Number(EvidenceRowText.OwnerProcessId(row)),
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
                ProcessId = EvidenceRowText.OwnerProcessId(row),
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
