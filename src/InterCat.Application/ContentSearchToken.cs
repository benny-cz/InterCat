using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A content search's continuation written as text a person can give back, so a search the command line ended early reads
/// on as the window's does (`contracts/content-v1.md` §4). It holds no byte of what was kept (P16): the end of each HTTP
/// part the search had open is named by where the session keeps its last bytes - a segment's row and a range of its kept
/// bytes - and read again from there. It names the session by its id, and the segments read and the search asked by
/// digests alone, so it says nothing of what was looked for to anyone without the session; it is refused where any of
/// them is not as it was.
/// </summary>
public static class ContentSearchToken
{
    /// <summary>Why a token is refused that no search wrote, or that was not given whole.</summary>
    public const string Malformed = "This is not a token a content search printed for reading on; give it whole, as the "
        + "search printed it.";

    /// <summary>Why a token is refused that reads on a search of another session.</summary>
    public const string AnotherSessions = "This token reads on a search of another session; search this session from its start.";

    /// <summary>Why a token is refused for a search asked otherwise than the one it reads on.</summary>
    public const string AnotherSearch = "This token reads on another search; ask it as it was asked - its text, case, process "
        + "and evidence policy alike - or search again from its start.";

    /// <summary>Why a token is refused where the session no longer keeps bytes at the end of a part it names.</summary>
    public const string BytesGone = "The session no longer keeps every byte this search had read at the end of an HTTP part "
        + "it had open, so it cannot read on from where it stopped; search again from its start.";

    private const byte Version = 1;
    private const int DigestLength = 8;

    /// <summary>The continuation as a token: unpadded base64url, holding no byte of what was kept.</summary>
    public static string Write(ContentSearchContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        using var written = new MemoryStream();
        using (var writer = new BinaryWriter(written, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(continuation.SessionId.ToByteArray());
            writer.Write7BitEncodedInt(continuation.Segments.Count);
            writer.Write(continuation.SegmentsDigest.AsSpan(0, DigestLength));
            writer.Write(SearchDigest(continuation.SegmentsDigest, continuation.Request));
            writer.Write7BitEncodedInt(continuation.Row);
            writer.Write(continuation.Counted);
            writer.Write7BitEncodedInt(continuation.Seams.Count);
            foreach (((long exchange, ushort eventId, int process, Mechanism mechanism), ContentSearch.Seam seam) in continuation.Seams
                .OrderBy(part => part.Key))
            {
                writer.Write7BitEncodedInt64(exchange);
                writer.Write7BitEncodedInt(eventId);
                writer.Write7BitEncodedInt(process);
                writer.Write7BitEncodedInt((int)mechanism);
                writer.Write7BitEncodedInt64(seam.Sequence);
                writer.Write7BitEncodedInt(seam.Free);
                writer.Write7BitEncodedInt(seam.Sources.Count);
                foreach (ContentSearch.SeamSource source in seam.Sources)
                {
                    writer.Write7BitEncodedInt(source.Segment);
                    writer.Write7BitEncodedInt(source.Row);
                    writer.Write7BitEncodedInt(source.From);
                    writer.Write7BitEncodedInt(source.Length);
                }
            }
        }

        return Base64Url.EncodeToString(written.ToArray());
    }

    /// <summary>
    /// The continuation <paramref name="token"/> names, reading on the search <paramref name="request"/> asks - which may
    /// set other budgets, limit and previews - in the session's current generation: the bytes at the end of each part it
    /// had open read again from where the session keeps them. It refuses, with an <see cref="ArgumentException"/>, a token
    /// no search wrote, one of another session and one of another search; and, with an
    /// <see cref="InvalidOperationException"/>, one whose segments or bytes the session no longer keeps as they were.
    /// </summary>
    public static ContentSearchContinuation Read(
        SessionStore store,
        Guid expectedSessionId,
        ContentSearchRequest request,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(token);
        Written given = Decode(token) ?? throw new ArgumentException(Malformed);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException(SessionStore.AnotherSession);
        }

        if (given.SessionId != manifest.SessionId)
        {
            throw new ArgumentException(AnotherSessions);
        }

        string[] names = [.. SessionSegments.Names(manifest)];
        byte[] segmentsDigest = given.Segments <= names.Length ? SegmentsDigest(manifest, names[..given.Segments]) : [];
        if (!segmentsDigest.AsSpan().StartsWith(given.SegmentsDigest))
        {
            throw new InvalidOperationException(ContentSearch.SegmentsChanged);
        }

        if (!SearchDigest(segmentsDigest, request).AsSpan().SequenceEqual(given.SearchDigest))
        {
            throw new ArgumentException(AnotherSearch);
        }

        var seams = new Dictionary<(long Exchange, ushort Event, int Process, Mechanism Mechanism), ContentSearch.Seam>();
        if (given.Parts.Length > 0)
        {
            SessionContentIndex content = manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
                ? SessionDerivationCache.For(manifest).Content(store.Root, cancellationToken)
                : SessionContentIndex.Empty;
            var segments = new Dictionary<int, SegmentReaderV1>();
            var chunks = new Dictionary<string, FileStream>(StringComparer.Ordinal);
            try
            {
                foreach (Part part in given.Parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seams[part.Key] = Seam(store, manifest, names, content, segments, chunks, part);
                }
            }
            finally
            {
                foreach (FileStream chunk in chunks.Values)
                {
                    chunk.Dispose();
                }
            }
        }

        return new(manifest.SessionId, request, names[..given.Segments], segmentsDigest, given.Row, given.Counted, seams);
    }

    /// <summary>The SHA-256 of the segments a search read: each one's name and the digest its generation records, in order.</summary>
    internal static byte[] SegmentsDigest(SessionManifestV1 manifest, IReadOnlyList<string> names)
    {
        Dictionary<string, string> digests = manifest.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Segment)
            .ToDictionary(dependency => dependency.Name, dependency => dependency.Digest, StringComparer.Ordinal);
        var canonical = new StringBuilder();
        foreach (string name in names)
        {
            canonical.Append(name).Append('|').Append(digests[name]).Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    /// <summary>
    /// The first bytes of the SHA-256 of what a search looks for - its text, case and process, and the evidence policy a
    /// process instance's records are bound under - with the digest of the segments it read, which only the session's
    /// holder knows, so the token says nothing of the text to anyone else.
    /// </summary>
    private static byte[] SearchDigest(byte[] segmentsDigest, ContentSearchRequest request)
    {
        string policy = request.ProcessInstance is null ? string.Empty : ((int)request.Policy).ToString(CultureInfo.InvariantCulture);
        string canonical = string.Create(CultureInfo.InvariantCulture,
            $"{Convert.ToHexString(segmentsDigest)}|{request.IgnoreCase}|{request.ProcessId}|{request.ProcessInstance}|{policy}|{request.Text}");
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..DigestLength];
    }

    /// <summary>
    /// The end of a part as the search held it, its last bytes read again from where they are kept, and the latest buffer's
    /// facts from its own record.
    /// </summary>
    private static ContentSearch.Seam Seam(SessionStore store, SessionManifestV1 manifest, string[] names, SessionContentIndex content,
        Dictionary<int, SegmentReaderV1> segments, Dictionary<string, FileStream> chunks, Part part)
    {
        var tail = new List<byte>();
        SessionContentEntry? latest = null;
        foreach (ContentSearch.SeamSource source in part.Sources)
        {
            if (!segments.TryGetValue(source.Segment, out SegmentReaderV1? segment))
            {
                segment = SessionSegments.Open(store, manifest, names[source.Segment]);
                segments[source.Segment] = segment;
            }

            latest = source.Row < segment.RowCount
                && content.Find(new RawRecordId(segment.CaptureId,
                    (uint)segment.Slice(SegmentColumnId.RawStreamId).UnsignedAt(source.Row)!.Value,
                    (uint)segment.Slice(SegmentColumnId.RawSourceEpoch).UnsignedAt(source.Row)!.Value,
                    segment.Slice(SegmentColumnId.RawRecordOrdinal).UnsignedAt(source.Row)!.Value)) is { Inspectable: true } entry
                && source.From <= entry.Fragment.Kept - source.Length
                    ? entry
                    : throw new InvalidOperationException(BytesGone);
            if (!chunks.TryGetValue(latest.ChunkName, out FileStream? chunk))
            {
                chunk = store.Root.OpenOwnedFile(latest.ChunkName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileOptions.RandomAccess);
                chunks[latest.ChunkName] = chunk;
            }

            tail.AddRange(ContentChunkV1.ReadBytes(chunk, latest.Entry, latest.Fragment.Kept).AsSpan(source.From, source.Length));
        }

        return new(part.Sequence, latest!.Fragment.Disposition == ContentDispositionV1.Whole,
            latest.Fragment.Encoding == ContentEncodingV1.Utf16LittleEndian, latest.Fragment.Kept, part.Free, [.. tail], part.Sources);
    }

    /// <summary>What a token holds, read and checked in itself; null where it is not a token a search wrote.</summary>
    private static Written? Decode(string token)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(Base64Url.DecodeFromChars(token)));
            if (reader.ReadByte() != Version)
            {
                return null;
            }

            var sessionId = new Guid(reader.ReadBytes(16));
            int segments = reader.Read7BitEncodedInt();
            byte[] segmentsDigest = reader.ReadBytes(DigestLength);
            byte[] searchDigest = reader.ReadBytes(DigestLength);
            int row = reader.Read7BitEncodedInt();
            byte counted = reader.ReadByte();
            int count = reader.Read7BitEncodedInt();
            if (segments < 1 || row < 0 || counted > 1 || count < 0)
            {
                return null;
            }

            var parts = new List<Part>();
            var keys = new HashSet<(long, ushort, int, Mechanism)>();
            for (int index = 0; index < count; index++)
            {
                long exchange = reader.Read7BitEncodedInt64();
                int eventId = reader.Read7BitEncodedInt();
                int process = reader.Read7BitEncodedInt();
                var mechanism = (Mechanism)reader.Read7BitEncodedInt();
                long sequence = reader.Read7BitEncodedInt64();
                int free = reader.Read7BitEncodedInt();
                int sources = reader.Read7BitEncodedInt();
                var named = new List<ContentSearch.SeamSource>();
                for (int source = 0; source < sources; source++)
                {
                    named.Add(new(reader.Read7BitEncodedInt(), reader.Read7BitEncodedInt(), reader.Read7BitEncodedInt(),
                        reader.Read7BitEncodedInt()));
                }

                // Each byte named lies in a segment the search read, a range of a row's kept bytes, and a part's bytes
                // after its last match are some of its last bytes.
                if (eventId is < 0 or > ushort.MaxValue || sources < 1 || !keys.Add((exchange, (ushort)eventId, process, mechanism))
                    || named.Any(source => source.Segment < 0 || source.Segment >= segments || source.Row < 0 || source.From < 0
                        || source.Length < 1)
                    || free < 0 || free > named.Sum(source => (long)source.Length))
                {
                    return null;
                }

                parts.Add(new((exchange, (ushort)eventId, process, mechanism), sequence, free, [.. named]));
            }

            return reader.BaseStream.Position == reader.BaseStream.Length
                ? new(sessionId, segments, segmentsDigest, searchDigest, row, counted == 1, [.. parts])
                : null;
        }
        catch (Exception unread) when (unread is FormatException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record Written(Guid SessionId, int Segments, byte[] SegmentsDigest, byte[] SearchDigest, int Row, bool Counted,
        Part[] Parts);

    private sealed record Part((long Exchange, ushort Event, int Process, Mechanism Mechanism) Key, long Sequence, int Free,
        ContentSearch.SeamSource[] Sources);
}
