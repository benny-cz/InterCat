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
