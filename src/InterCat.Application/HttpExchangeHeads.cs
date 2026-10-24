using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// An HTTP exchange's two heads as the HTTP/1.1 head decoder reads them (M8, `contracts/decoders-v1.md`,
/// `contracts/http-exchanges-v1.md` §5c): its request head's decoding and its response head's, each null where the exchange
/// recorded no buffer of that head. A head is decoded only whole, from bytes kept with consent to inspect them, and the
/// decoding says why where it read none.
/// </summary>
public sealed record HttpExchangeHeads(DecodedContent? Request, DecodedContent? Response)
{
    /// <summary>What the request asked for: its method and target, "GET /index.html"; null where its request line was not decoded.</summary>
    public string? Asked => Request?.Fields is [{ Name: "method" } method, { Name: "target" } target, ..]
        ? method.Value + " " + target.Value
        : null;

    /// <summary>The host its first Host field named, in any case; null where it named none, or was not decoded.</summary>
    public string? Host => Request?.Fields.FirstOrDefault(line =>
        string.Equals(line.Name, "Host", StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// What the response answered: its status and, where its status line gave one, its reason, "200 OK"; null where its
    /// status line was not decoded. A reason is the field that follows the status on its line, never a field line's.
    /// </summary>
    public string? Answered => Response?.Fields is [{ Name: "version" }, { Name: "status" } status, ..] fields
        ? fields.Count > 2 && fields[2] is { Name: "reason" } reason && reason.Bytes.First == status.Bytes.Last + 2
            ? status.Value + " " + reason.Value
            : status.Value
        : null;

    /// <summary>
    /// What the request head declares of the request's body, beside what was recorded of it (§5e); null where the head
    /// was not decoded.
    /// </summary>
    public HttpBodyDeclaration? RequestBody(HttpPartRecord body) => HttpBodies.Declared(Request, body, response: false, null, null);

    /// <summary>
    /// What the response head declares of the response's body, beside what was recorded of it (§5e): a response to HEAD,
    /// or of status 1xx, 204 or 304, holds none. Null where the head was not decoded.
    /// </summary>
    public HttpBodyDeclaration? ResponseBody(HttpPartRecord body) => HttpBodies.Declared(Response, body, response: true,
        Request?.Fields is [{ Name: "method" } method, ..] ? method.Value : null,
        Response?.Fields is [{ Name: "version" }, { Name: "status" } status, ..] ? status.Value : null);
}

/// <summary>
/// What a message's head declares of its body's length, set beside what the capture recorded of the body
/// (`contracts/http-exchanges-v1.md` §5e), in a sentence; <paramref name="Contradicts"/> where what was recorded is not
/// what the head declared - fewer or more bytes than its Content-Length, or a body where it declares none - which a
/// body's buffers alone cannot show.
/// </summary>
public sealed record HttpBodyDeclaration(string Statement, bool Contradicts);

/// <summary>
/// A message body's length as RFC 9112 §6.3 has a recipient read it from the head, compared with the bytes WinINet's
/// buffers of the body measured. WinINet keeps a chunked response's body without its chunk framing (FX-HTTP-003), so a
/// chunked body's length is its buffers' alone; whether it keeps a coded body coded was not measured, so such a body is
/// not compared. Nothing of the body's content is read.
/// </summary>
internal static class HttpBodies
{
    public static HttpBodyDeclaration? Declared(DecodedContent? head, HttpPartRecord body, bool response, string? method, string? status)
    {
        if (head is not { Read: not null })
        {
            return null;
        }

        if (!head.ReachedEnd)
        {
            return new("Its head was not decoded to its end, so what it declares of its body is not known.", false);
        }

        string held = CountText.Of(body.Bytes, "byte");
        if (response && (method == "HEAD" || status is ['1', _, _] or "204" or "304"))
        {
            string kind = method == "HEAD" ? "a response to HEAD" : $"a {status} response";
            return body.Bytes > 0
                ? new($"As {kind}, it holds no body, yet its recorded buffers hold {held}.", true)
                : new($"As {kind}, it holds no body, and " + (body.Recorded ? "its recorded buffers hold none." : "none was recorded."), false);
        }

        string[] transfer = Values(head, "Transfer-Encoding");
        if (transfer.Length > 0)
        {
            string codings = string.Join(", ", transfer);
            return new(response && transfer is [{ } only] && only.Equals("chunked", StringComparison.OrdinalIgnoreCase)
                ? "Its head declares the chunked transfer coding, which WinINet removes before it keeps a body (FX-HTTP-003), "
                    + "so the body's end is its last buffer's, not a length its head declared."
                : $"Its head declares the {codings} transfer coding, which WinINet's capture was not measured to keep framed or "
                    + "unframed, so its body's length is not compared.", false);
        }

        string[] lengths = Values(head, "Content-Length");
        if (lengths.Length == 0)
        {
            return response
                ? new("Its head declares no length, so its body ran until its connection closed; its end is its last buffer's.", false)
                : body.Bytes > 0
                    ? new($"Its head declares no body, yet its recorded buffers hold {held}.", true)
                    : new("Its head declares no body, and none was recorded.", false);
        }

        if (lengths.Distinct(StringComparer.Ordinal).Count() != 1
            || !long.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out long declared))
        {
            return new($"Its Content-Length, {string.Join(", ", lengths)}, is not one length, so its body is not compared.", false);
        }

        string length = CountText.Of(declared, "byte");
        string[] coded = [.. Values(head, "Content-Encoding").Where(coding => !coding.Equals("identity", StringComparison.OrdinalIgnoreCase))];
        if (coded.Length > 0)
        {
            return new($"Its head declares {length} in the {string.Join(", ", coded)} content coding, and whether WinINet's "
                + "capture keeps such a body coded was not measured, so its length is not compared.", false);
        }

        if (body.Bytes == declared)
        {
            return new(declared == 0
                ? "Its head declares an empty body, and " + (body.Recorded ? "its recorded buffers hold no byte." : "none was recorded.")
                : $"Its head declares {length}, and its recorded buffers hold them all.", false);
        }

        if (!body.Recorded)
        {
            // A response to HEAD declares the length its GET would have had, and holds none of it.
            return response && method is null
                ? new($"Its head declares {length}, and no buffer of its body was recorded; its request's method was not "
                    + "decoded, and a response to HEAD holds none.", false)
                : new($"Its head declares {length}, and no buffer of its body was recorded.", true);
        }

        return body.Bytes < declared
            ? new($"Its head declares {length}, and its recorded buffers hold {body.Bytes.ToString("N0", CultureInfo.CurrentCulture)} "
                + "of them" + (body.Whole ? ", though their places and ends make the body whole." : "."), true)
            : new($"Its head declares {length}, and its recorded buffers hold {held}, more than it declared.", true);
    }

    /// <summary>
    /// The values of every field line <paramref name="name"/> names, in any case, each list element apart (RFC 9110 §5.3);
    /// a start line's fields, named in lower case for what they are, are none of them.
    /// </summary>
    private static string[] Values(DecodedContent head, string name) =>
    [
        .. head.Fields
            .Where(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(field => field.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
    ];
}

/// <summary>Reads exchanges' heads where a person asks what they say (`contracts/http-exchanges-v1.md` §5c, §5d).</summary>
public static class HttpExchangeHeadsQuery
{
    /// <summary>
    /// The heads of <paramref name="count"/> of <paramref name="group"/>'s exchanges from <paramref name="first"/>, in
    /// reading order, each read whole from the kept content of <paramref name="manifest"/>'s generation - every part in one
    /// pass over the session - and decoded by the HTTP/1.1 head decoder. Each head's buffers are found by the rows
    /// <paramref name="index"/> names, in the segments it names, so an index a later generation extended reads as one
    /// derived afresh.
    /// </summary>
    public static IReadOnlyList<HttpExchangeHeads> Read(
        SessionStore store,
        SessionManifestV1 manifest,
        HttpExchangeIndex index,
        HttpExchangeGroup group,
        int first,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentOutOfRangeException.ThrowIfNegative(first);

        // A buffer of each head of each exchange: its part, every buffer of the head, is found from it.
        var segments = new Dictionary<int, SegmentReaderV1>();
        var rows = new List<ObservationRowV1>();
        var records = new List<RawRecordId>();
        var asked = new List<(int Request, int Response)>();
        for (int position = first; position < Math.Min((long)first + count, group.Exchanges); position++)
        {
            (int Segment, int Row)[] buffers = [.. index.RecordsOf(group, position)];
            asked.Add((Ask(buffers, HttpPart.RequestHead), Ask(buffers, HttpPart.ResponseHead)));
        }

        IReadOnlyList<SessionContentPartDetail> parts = SessionContentPartQuery.ReadMany(store, manifest.SessionId, rows,
            revealBytes: true, cancellationToken);
        return [.. asked.Select(heads => new HttpExchangeHeads(Decode(heads.Request), Decode(heads.Response)))];

        int Ask((int Segment, int Row)[] buffers, HttpPart part)
        {
            foreach ((int at, int row) in buffers)
            {
                SegmentReaderV1 segment = Segment(at);
                ObservationRowV1 buffer = segment.Row(row);
                if (buffer.EventId == 2001 + (int)part)
                {
                    rows.Add(buffer);
                    records.Add(new(segment.CaptureId, buffer.RawStreamId, buffer.RawSourceEpoch, buffer.RawRecordOrdinal));
                    return rows.Count - 1;
                }
            }

            return -1;
        }

        SegmentReaderV1 Segment(int at)
        {
            if (!segments.TryGetValue(at, out SegmentReaderV1? segment))
            {
                string name = index.SegmentNames[at]
                    ?? throw new InvalidDataException("The exchanges were read from a segment no generation names.");
                segment = SessionSegments.Open(store, manifest, name);
                segments[at] = segment;
            }

            return segment;
        }

        DecodedContent? Decode(int at) => at < 0 ? null : ContentDecoders.DecodePart(parts[at], rows[at], records[at]);
    }
}
