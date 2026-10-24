using System.Diagnostics;
using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What to look for in a session's kept content, and how far to look (`contracts/content-v1.md` §4).</summary>
public sealed record ContentSearchRequest
{
    /// <summary>
    /// The text to find: its UTF-8 bytes in a record whose bytes declare no text or UTF-8, its UTF-16 bytes in one that
    /// declares UTF-16.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>Whether a letter A to Z matches in either case; every other character matches only itself.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Only the records of this process ID - the one a record names as its owner, else the one that raised it.</summary>
    public int? ProcessId { get; init; }

    /// <summary>How many records holding the text are listed at most; finding one more says there are more.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The most kept bytes read before the search stops.</summary>
    public long MaximumBytes { get; init; } = ContentSearch.DefaultMaximumBytes;

    /// <summary>How long the search reads before it stops.</summary>
    public TimeSpan Budget { get; init; } = ContentSearch.DefaultBudget;

    /// <summary>Whether each record listed shows the bytes around its first match; off unless asked for.</summary>
    public bool Previews { get; init; }
}

/// <summary>
/// A record whose kept bytes hold the text: the evidence-page locator that names it in the generation read, its row, how
/// many times its bytes hold the text and where first, and - only when previews were asked for - the bytes around that.
/// </summary>
public sealed record ContentSearchHit(string Segment, int Row, ObservationRowV1 Observation, int Matches, int FirstAt, ContentPreview? Preview);

/// <summary>
/// The kept bytes around a match, as inert text: up to <see cref="ContentSearch.PreviewBytes"/> before and after, each
/// byte outside visible ASCII - and a backslash - written \xHH, and in a record declaring UTF-16 each code unit outside
/// it written \uHHHH.
/// </summary>
public sealed record ContentPreview(string Before, string Match, string After);

/// <summary>
/// What a content search found and what it read (`contracts/content-v1.md` §4): the records holding the text, in reading
/// order; whether there are more than were listed; how many records' kept bytes it read, and how many bytes; how many
/// records it did not read because their bytes were kept without consent to inspect them; and why it stopped before
/// reading every record it could, or why some could not be read, where either happened.
/// </summary>
public sealed record ContentSearchResult
{
    public required Guid SessionId { get; init; }

    public required long Generation { get; init; }

    public required IReadOnlyList<ContentSearchHit> Hits { get; init; }

    /// <summary>Whether a record beyond those listed holds the text: the search stopped at the first one past the limit.</summary>
    public required bool More { get; init; }

    public required int RecordsRead { get; init; }

    public required long BytesRead { get; init; }

    /// <summary>The records in scope kept without consent to inspect them, whose bytes are never searched.</summary>
    public required int WithoutConsent { get; init; }

    /// <summary>Why the search stopped before reading every record it could, as a sentence; null when it read them all.</summary>
    public string? Stopped { get; init; }

    /// <summary>Why some kept content could not be read at all, so was not searched; null when all of it could.</summary>
    public string? Unreadable { get; init; }

    /// <summary>Why the session keeps no content to search - none was kept, or a release gave it up; null when it keeps some.</summary>
    public string? NoneKept { get; init; }

    /// <summary>Whether a record not listed may still hold the text: some kept bytes were not read, for whatever reason.</summary>
    public bool Partial => WithoutConsent > 0 || Stopped is not null || Unreadable is not null;
}

/// <summary>
/// Searches a session's kept content for a text, only when a person asks (`contracts/content-v1.md` §4): only the bytes
/// kept with consent to inspect them, each record's on its own, within a budget of bytes and time and a number of records
/// listed, saying what it did not read. It is exact within a record's kept bytes; a match split across two records - two
/// buffers of one HTTP part - is not found, and a record whose message was cut is searched in the bytes kept. Nothing it
/// finds is kept, and it shows no byte unless previews are asked for.
/// </summary>
public static class ContentSearch
{
    /// <summary>The most kept bytes a search reads unless asked for fewer.</summary>
    public const long DefaultMaximumBytes = 256L * 1024 * 1024;

    /// <summary>How long a search reads unless asked for less.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    /// <summary>How many kept bytes a preview shows before and after a match.</summary>
    public const int PreviewBytes = 16;

    /// <summary>The sentence every search's answer carries about what it can and cannot find.</summary>
    public const string Exactness = "Each record's kept bytes are searched on their own, so a match split across two records "
        + "- two buffers of one HTTP part - is not found, and a record whose message was cut is searched in the bytes kept.";

    public static ContentSearchResult Find(
        SessionStore store,
        Guid expectedSessionId,
        ContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Text);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumBytes, 1);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException(SessionStore.AnotherSession);
        }

        SessionContentIndex content = manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
            ? SessionDerivationCache.For(manifest).Content(store.Root, cancellationToken)
            : SessionContentIndex.Empty;
        string? unreadable = content.Problem is { } unread ? $"Some kept content could not be read ({unread}), so it was not searched." : null;
        string? noneKept = content.Fragments > 0 || unreadable is not null ? null
            : SessionContentQuery.ReleasedContent(manifest) ?? "The session keeps no content, so there was nothing to search.";
        byte[] utf8 = Fold(Encoding.UTF8.GetBytes(request.Text), request.IgnoreCase, utf16: false);
        byte[] utf16 = Fold(Encoding.Unicode.GetBytes(request.Text), request.IgnoreCase, utf16: true);
        var hits = new List<ContentSearchHit>();
        var chunks = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        Stopwatch elapsed = Stopwatch.StartNew();
        int read = 0, withoutConsent = 0;
        long bytesRead = 0;
        bool more = false;
        string? stopped = null;
        try
        {
            foreach (string name in content.Fragments == 0 ? [] : SessionSegments.Names(manifest))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SegmentReaderV1 segment = SessionSegments.Open(store, manifest, name);
                SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
                SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                for (int index = 0; index < segment.RowCount && stopped is null && !more; index++)
                {
                    if (content.Find(new RawRecordId(segment.CaptureId, (uint)streams.UnsignedAt(index)!.Value,
                        (uint)epochs.UnsignedAt(index)!.Value, ordinals.UnsignedAt(index)!.Value)) is not { } entry)
                    {
                        continue;
                    }

                    ObservationRowV1 row = segment.Row(index);
                    if (request.ProcessId is { } only && (row.OwnerProcessId ?? row.HeaderProcessId) != only)
                    {
                        continue;
                    }

                    if (!entry.Inspectable)
                    {
                        withoutConsent++;
                        continue;
                    }

                    int kept = entry.Fragment.Kept;
                    if (kept == 0)
                    {
                        continue;
                    }

                    // The budgets are checked before a record is read, so what was read is all stated and none is half read.
                    if (bytesRead + kept > request.MaximumBytes || elapsed.Elapsed >= request.Budget)
                    {
                        stopped = "The search stopped at its budget of "
                            + (bytesRead + kept > request.MaximumBytes
                                ? CountText.Of(request.MaximumBytes, "byte") + " read"
                                : string.Create(CultureInfo.CurrentCulture, $"{request.Budget.TotalSeconds:N0} seconds"))
                            + ", after " + CountText.Of(read, "record") + "; the rest were not searched, so a record not "
                            + "listed may still hold the text.";
                        break;
                    }

                    if (!chunks.TryGetValue(entry.ChunkName, out FileStream? chunk))
                    {
                        chunk = store.Root.OpenOwnedFile(entry.ChunkName, FileMode.Open, FileAccess.Read, FileShare.Read,
                            FileOptions.RandomAccess);
                        chunks[entry.ChunkName] = chunk;
                    }

                    byte[] bytes = ContentChunkV1.ReadBytes(chunk, entry.Entry, kept);
                    read++;
                    bytesRead += kept;
                    bool wide = entry.Fragment.Encoding == ContentEncodingV1.Utf16LittleEndian;
                    byte[] pattern = wide ? utf16 : utf8;
                    (int matches, int first) = Count(Fold(bytes, request.IgnoreCase, wide), pattern, aligned: wide);
                    if (matches == 0)
                    {
                        continue;
                    }

                    if (hits.Count == request.Limit)
                    {
                        more = true;
                        break;
                    }

                    hits.Add(new(name, index, row, matches, first, request.Previews ? Preview(bytes, first, pattern.Length, wide) : null));
                }

                if (stopped is not null || more)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (FileStream chunk in chunks.Values)
            {
                chunk.Dispose();
            }
        }

        return new()
        {
            SessionId = manifest.SessionId,
            Generation = manifest.Generation,
            Hits = hits,
            More = more,
            RecordsRead = read,
            BytesRead = bytesRead,
            WithoutConsent = withoutConsent,
            Stopped = stopped,
            Unreadable = unreadable,
            NoneKept = noneKept,
        };
    }

    /// <summary>
    /// How many times <paramref name="pattern"/> occurs in <paramref name="bytes"/>, none overlapping another, and where
    /// first; in UTF-16 only at a code unit's start.
    /// </summary>
    private static (int Matches, int First) Count(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> pattern, bool aligned)
    {
        if (pattern.IsEmpty)
        {
            return (0, -1);
        }

        int matches = 0, first = -1, at = 0;
        while (at <= bytes.Length - pattern.Length)
        {
            int found = bytes[at..].IndexOf(pattern);
            if (found < 0)
            {
                break;
            }

            int position = at + found;
            if (aligned && position % 2 != 0)
            {
                at = position + 1;
                continue;
            }

            matches++;
            if (first < 0) first = position;
            at = position + pattern.Length;
        }

        return (matches, first);
    }

    /// <summary>The bytes with every letter A to Z made a to z, where case is ignored: in UTF-16, a code unit's whole value.</summary>
    private static byte[] Fold(byte[] bytes, bool ignoreCase, bool utf16)
    {
        if (!ignoreCase)
        {
            return bytes;
        }

        byte[] folded = (byte[])bytes.Clone();
        for (int index = 0; index < folded.Length; index += utf16 ? 2 : 1)
        {
            if (folded[index] is >= (byte)'A' and <= (byte)'Z' && (!utf16 || (index + 1 < folded.Length && folded[index + 1] == 0)))
            {
                folded[index] += 32;
            }
        }

        return folded;
    }

    /// <summary>The kept bytes around the match at <paramref name="first"/>, as inert text.</summary>
    private static ContentPreview Preview(byte[] bytes, int first, int length, bool utf16)
    {
        int from = Math.Max(0, first - PreviewBytes);
        int to = Math.Min(bytes.Length, first + length + PreviewBytes);
        return new(Inert(bytes.AsSpan(from, first - from), utf16), Inert(bytes.AsSpan(first, length), utf16),
            Inert(bytes.AsSpan(first + length, to - first - length), utf16));
    }

    /// <summary>
    /// Bytes as inert text: visible ASCII and the space as they are, a backslash and every other byte as \xHH; in UTF-16,
    /// each code unit in visible ASCII as its character, every other as \uHHHH, and an odd last byte as \xHH.
    /// </summary>
    internal static string Inert(ReadOnlySpan<byte> bytes, bool utf16)
    {
        var text = new StringBuilder(bytes.Length);
        int index = 0;
        while (index < bytes.Length)
        {
            if (utf16 && index + 1 < bytes.Length)
            {
                int unit = bytes[index] | (bytes[index + 1] << 8);
                text.Append(unit is >= 0x20 and <= 0x7E and not 0x5C ? ((char)unit).ToString()
                    : string.Create(CultureInfo.InvariantCulture, $"\\u{unit:X4}"));
                index += 2;
                continue;
            }

            byte value = bytes[index];
            text.Append(value is >= 0x20 and <= 0x7E and not 0x5C ? ((char)value).ToString()
                : string.Create(CultureInfo.InvariantCulture, $"\\x{value:X2}"));
            index++;
        }

        return text.ToString();
    }
}
