using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// An HTTP exchange's two heads as the HTTP/1.1 head decoder reads them (M8, `contracts/decoders-v1.md`,
/// `contracts/http-exchanges-v1.md` §5b): its request head's decoding and its response head's, each null where the exchange
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
}

/// <summary>Reads exchanges' heads for a listing that decodes them (`contracts/http-exchanges-v1.md` §5b).</summary>
public static class HttpExchangeHeadsQuery
{
    /// <summary>
    /// The heads of <paramref name="group"/>'s first <paramref name="count"/> exchanges in reading order, each read whole
    /// from the current generation's kept content - every part in one pass over the session - and decoded by the HTTP/1.1
    /// head decoder. <paramref name="segments"/> are those <paramref name="index"/> was derived from, whose rows name each
    /// head's buffers.
    /// </summary>
    public static IReadOnlyList<HttpExchangeHeads> Read(
        SessionStore store,
        Guid sessionId,
        IReadOnlyList<SegmentReaderV1> segments,
        HttpExchangeIndex index,
        HttpExchangeGroup group,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(group);

        // A buffer of each head of each exchange: its part, every buffer of the head, is found from it.
        var rows = new List<ObservationRowV1>();
        var records = new List<RawRecordId>();
        var asked = new List<(int Request, int Response)>();
        for (int position = 0; position < Math.Min(count, group.Exchanges); position++)
        {
            (int Segment, int Row)[] buffers = [.. index.RecordsOf(group, position)];
            asked.Add((Ask(buffers, HttpPart.RequestHead), Ask(buffers, HttpPart.ResponseHead)));
        }

        IReadOnlyList<SessionContentPartDetail> parts = SessionContentPartQuery.ReadMany(store, sessionId, rows, revealBytes: true,
            cancellationToken);
        return [.. asked.Select(heads => new HttpExchangeHeads(Decode(heads.Request), Decode(heads.Response)))];

        int Ask((int Segment, int Row)[] buffers, HttpPart part)
        {
            foreach ((int segment, int row) in buffers)
            {
                ObservationRowV1 buffer = segments[segment].Row(row);
                if (buffer.EventId == 2001 + (int)part)
                {
                    rows.Add(buffer);
                    records.Add(new(segments[segment].CaptureId, buffer.RawStreamId, buffer.RawSourceEpoch, buffer.RawRecordOrdinal));
                    return rows.Count - 1;
                }
            }

            return -1;
        }

        DecodedContent? Decode(int at) => at < 0 ? null : ContentDecoders.DecodePart(parts[at], rows[at], records[at]);
    }
}
