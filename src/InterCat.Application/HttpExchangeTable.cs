using System.Globalization;
using System.Text;
using InterCat.Analysis;

namespace InterCat.Application;

/// <summary>
/// One listed HTTP exchange as a line of the table `icat exchanges --format csv` writes
/// (`contracts/http-exchanges-v1.md` §5f): its session and process, its key and number, when it began and how long it
/// took, what was recorded of its four parts and, where its heads were decoded when asked, what they say.
/// </summary>
public sealed record HttpExchangeLine
{
    public required Guid SessionId { get; init; }

    public required long Generation { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>The executable of the instance the exchange binds to; null where none is admitted or none was witnessed.</summary>
    public required string? Process { get; init; }

    /// <summary>The key the window's ladder names the process's exchanges by; null where no instance is admitted.</summary>
    public required string? ProcessKey { get; init; }

    /// <summary>Why no instance holds the process's exchanges under the evidence policy; null where one does.</summary>
    public required string? Unattributed { get; init; }

    /// <summary>The key the window's ladder names the exchange by; null where no instance is admitted.</summary>
    public required string? Key { get; init; }

    public required long Number { get; init; }

    /// <summary>Its first buffer's time, as seconds after the capture epoch; null where the clock is not described.</summary>
    public required string? FirstSeconds { get; init; }

    public required long? DurationNanoseconds { get; init; }

    public required bool RecordedWhole { get; init; }

    public required HttpPartRecord RequestHead { get; init; }

    public required HttpPartRecord RequestBody { get; init; }

    public required HttpPartRecord ResponseHead { get; init; }

    public required HttpPartRecord ResponseBody { get; init; }

    /// <summary>Its heads as the HTTP/1.1 head decoder read them when asked; null where they were not asked for.</summary>
    public HttpExchangeHeads? Heads { get; init; }
}

/// <summary>
/// The listed HTTP exchanges as a table (`contracts/http-exchanges-v1.md` §5f): one line per exchange in the order
/// listed, its numbers invariant, its text cells quoted where they hold a comma, a quote or a line end and neutralized
/// where a spreadsheet would evaluate them, since names and decoded values come from the observed machine. With decoded
/// heads it adds what they say, each value the decoding's own, and why a head was not decoded in that head's own cell.
/// </summary>
public static class HttpExchangeTable
{
    /// <summary>What a head's cell says where no buffer of the head was recorded.</summary>
    public const string NoHead = "No buffer of this head was recorded, so there is nothing to decode.";

    private static readonly string[] Parts = ["request_head", "request_body", "response_head", "response_body"];

    /// <summary>The columns every table has, in order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "session_id", "generation", "pid", "process", "process_key", "unattributed", "exchange_key", "exchange",
        "first_seconds", "duration_ns", "recorded_whole",
        .. Parts.SelectMany(part => new[] { part + "_buffers", part + "_bytes", part + "_whole" }),
    ];

    /// <summary>The columns a table of decoded heads adds after <see cref="Columns"/>, in order.</summary>
    public static IReadOnlyList<string> HeadColumns { get; } =
    [
        "decoder", "decoder_version", "method", "target", "request_version", "host", "status", "reason", "response_version",
        "request_head_decoded_to_end", "response_head_decoded_to_end", "request_head_not_decoded", "response_head_not_decoded",
        "request_body_declared", "request_body_contradicts", "response_body_declared", "response_body_contradicts",
    ];

    /// <summary>
    /// <paramref name="lines"/> as CSV, with <see cref="HeadColumns"/> where <paramref name="decoded"/>: a line whose
    /// heads were not read leaves them empty.
    /// </summary>
    public static string Csv(IReadOnlyList<HttpExchangeLine> lines, bool decoded)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var csv = new StringBuilder();
        WorkspaceExport.Line(csv, decoded ? [.. Columns, .. HeadColumns] : Columns);
        ContentDecoderInfo decoder = ContentDecoders.HttpHeadDecoder;
        foreach (HttpExchangeLine line in lines)
        {
            List<string> cells =
            [
                line.SessionId.ToString("N"),
                Number(line.Generation),
                Number(line.ProcessId),
                Text(line.Process),
                Text(line.ProcessKey),
                Text(line.Unattributed),
                Text(line.Key),
                Number(line.Number),
                line.FirstSeconds ?? string.Empty,
                line.DurationNanoseconds is { } duration ? Number(duration) : string.Empty,
                Flag(line.RecordedWhole),
            ];
            foreach (HttpPartRecord part in new[] { line.RequestHead, line.RequestBody, line.ResponseHead, line.ResponseBody })
            {
                cells.AddRange([Number(part.Buffers), Number(part.Bytes), Flag(part.Whole)]);
            }

            if (decoded)
            {
                cells.AddRange(line.Heads is { } heads ? Heads(heads, line, decoder) : HeadColumns.Select(_ => string.Empty));
            }

            WorkspaceExport.Line(csv, cells);
        }

        return csv.ToString();
    }

    private static IEnumerable<string> Heads(HttpExchangeHeads heads, HttpExchangeLine line, ContentDecoderInfo decoder)
    {
        HttpBodyDeclaration? request = heads.RequestBody(line.RequestBody);
        HttpBodyDeclaration? response = heads.ResponseBody(line.ResponseBody);
        return
        [
            Text(decoder.Id),
            Number(decoder.Version),
            Text(heads.RequestLine?.Method),
            Text(heads.RequestLine?.Target),
            Text(heads.RequestLine?.Version),
            Text(heads.Host),
            Text(heads.StatusLine?.Status),
            Text(heads.StatusLine?.Reason),
            Text(heads.StatusLine?.Version),
            heads.Request is { } asked ? Flag(asked.ReachedEnd) : string.Empty,
            heads.Response is { } answered ? Flag(answered.ReachedEnd) : string.Empty,
            Text(heads.Request is null ? NoHead : heads.Request.NotDecoded),
            Text(heads.Response is null ? NoHead : heads.Response.NotDecoded),
            Text(request?.Statement),
            request is null ? string.Empty : Flag(request.Contradicts),
            Text(response?.Statement),
            response is null ? string.Empty : Flag(response.Contradicts),
        ];
    }

    private static string Text(string? value) => value is null ? string.Empty : WorkspaceExport.Text(value);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "true" : "false";
}
