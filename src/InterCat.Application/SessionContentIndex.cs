using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>One record's kept content as a generation holds it: the fragment, the chunk it is in and what it was kept under.</summary>
public sealed record SessionContentEntry(string ChunkName, ContentChunkHeaderV1 Header, ContentChunkEntryV1 Entry)
{
    /// <summary>The fragment's facts: what the bytes are and how many were kept of how many (content-v1 §1).</summary>
    public ContentFragmentV1 Fragment => Entry.Fragment;

    /// <summary>Whether a person may see the bytes: the capture kept them with inspection consent.</summary>
    public bool Inspectable => Header.Inspection == ContentInspectionV1.HexAndText;
}

/// <summary>What one content policy kept its fragments under: the policy, its per-record limit and whether a person may see them.</summary>
public sealed record SessionContentPolicy(string PolicyId, int RecordLimit, ContentInspectionV1 Inspection);

/// <summary>
/// What a generation keeps as content, in sum (content-v1 §4): how many records' messages, whole, cut or omitted, and
/// how many bytes, under which policies. It names no byte of any message.
/// </summary>
public sealed record SessionContentSummary
{
    /// <summary>How many content chunks the generation names.</summary>
    public required int Chunks { get; init; }

    /// <summary>How many records' content the readable chunks hold.</summary>
    public required int Records { get; init; }

    /// <summary>Records whose message was kept whole.</summary>
    public required int Whole { get; init; }

    /// <summary>Records whose message was cut to the policy's record limit.</summary>
    public required int Cut { get; init; }

    /// <summary>Records whose message was not kept because the capture had reached its content limit.</summary>
    public required int Omitted { get; init; }

    /// <summary>The bytes of messages kept, in all.</summary>
    public required long KeptBytes { get; init; }

    /// <summary>The policies the chunks were kept under, in policy order.</summary>
    public required IReadOnlyList<SessionContentPolicy> Policies { get; init; }

    /// <summary>Why a chunk could not be read, so these sums leave its records out; null when every one was read.</summary>
    public required string? Problem { get; init; }
}

/// <summary>
/// The content a generation keeps, by record (`contracts/content-v1.md`, ADR-036): every chunk it names, read and checked
/// once. Only the record readers and a session's summary use it; nothing that reads metadata does. It keeps no message
/// byte: one is read afresh from its chunk only when asked for.
/// </summary>
/// <remarks>
/// A chunk that cannot be read is not read in part. The index then holds no fragment of it and says why, so a record's
/// content is stated as unknown rather than as none (R21).
/// </remarks>
public sealed class SessionContentIndex
{
    private readonly Dictionary<(Guid Capture, uint Stream, uint Epoch, ulong Ordinal), SessionContentEntry> byRecord;

    private SessionContentIndex(
        Dictionary<(Guid, uint, uint, ulong), SessionContentEntry> byRecord,
        int chunks,
        string? problem)
    {
        this.byRecord = byRecord;
        Chunks = chunks;
        Problem = problem;
    }

    /// <summary>A generation that keeps no content.</summary>
    public static SessionContentIndex Empty { get; } = new([], 0, null);

    /// <summary>How many content chunks the generation names.</summary>
    public int Chunks { get; }

    /// <summary>How many records' content the readable chunks hold.</summary>
    public int Fragments => byRecord.Count;

    /// <summary>Why a chunk the generation names could not be read; null when every one was.</summary>
    public string? Problem { get; }

    /// <summary>The content kept of the record <paramref name="row"/> derives from in <paramref name="captureId"/>, or null.</summary>
    public SessionContentEntry? Find(CaptureId captureId, ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return byRecord.GetValueOrDefault((captureId.Value, row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal));
    }

    /// <summary>The content kept of the record <paramref name="record"/> names, or null.</summary>
    public SessionContentEntry? Find(RawRecordId record) =>
        byRecord.GetValueOrDefault((record.CaptureId.Value, record.StreamId, record.SourceEpoch, record.RecordOrdinal));

    /// <summary>What the generation keeps, in sum, from its fragments' facts alone.</summary>
    public SessionContentSummary Summarize()
    {
        int whole = 0, cut = 0, omitted = 0;
        long kept = 0;
        var policies = new HashSet<SessionContentPolicy>();
        foreach (SessionContentEntry entry in byRecord.Values)
        {
            _ = policies.Add(new(entry.Header.PolicyId, entry.Header.RecordLimit, entry.Header.Inspection));
            kept += entry.Fragment.Kept;
            switch (entry.Fragment.Disposition)
            {
                case ContentDispositionV1.Whole:
                    whole++;
                    break;
                case ContentDispositionV1.TruncatedByRecordLimit:
                    cut++;
                    break;
                default:
                    omitted++;
                    break;
            }
        }

        return new()
        {
            Chunks = Chunks,
            Records = byRecord.Count,
            Whole = whole,
            Cut = cut,
            Omitted = omitted,
            KeptBytes = kept,
            Policies =
            [
                .. policies.OrderBy(policy => policy.PolicyId, StringComparer.Ordinal)
                    .ThenBy(policy => policy.RecordLimit)
                    .ThenBy(policy => policy.Inspection),
            ],
            Problem = Problem,
        };
    }

    /// <summary>
    /// Reads and checks every content chunk <paramref name="manifest"/> names, keeping each fragment's facts and none of its
    /// bytes. A chunk holds content of its capture's records, which are the generation's segments' records; a chunk of
    /// another capture, or a record kept twice, is a refusal.
    /// </summary>
    public static SessionContentIndex Read(IOwnedDirectory directory, SessionManifestV1 manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] chunks = [.. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Content)];
        if (chunks.Length == 0)
        {
            return Empty;
        }

        IReadOnlyList<string> segments = SessionSegments.Names(manifest);
        if (segments.Count == 0)
        {
            return new([], chunks.Length, "the generation keeps content but holds no records to keep it of");
        }

        CaptureId capture = SessionSegments.Open(directory, manifest, segments[0]).CaptureId;
        var byRecord = new Dictionary<(Guid, uint, uint, ulong), SessionContentEntry>();
        string? problem = null;
        foreach (StoreDependency chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ContentChunkContentsV1 contents;
                using (FileStream stream = directory.OpenOwnedFile(
                    chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
                {
                    if (stream.Length != chunk.LengthBytes)
                    {
                        throw new InvalidDataException($"it is not the length its generation recorded.");
                    }

                    contents = ContentChunkV1.Read(stream, capture, cancellationToken);
                }

                // A record's content is in the chunk its journal chunk's generation published, and in no other.
                var entries = new Dictionary<(Guid, uint, uint, ulong), SessionContentEntry>(contents.Entries.Count);
                foreach (ContentChunkEntryV1 entry in contents.Entries)
                {
                    ContentFragmentV1 fragment = entry.Fragment;
                    (Guid, uint, uint, ulong) key = (capture.Value, fragment.StreamId, fragment.SourceEpoch, fragment.RecordOrdinal);
                    if (byRecord.ContainsKey(key))
                    {
                        throw new InvalidDataException(
                            $"record {fragment.StreamId}/{fragment.SourceEpoch}/{fragment.RecordOrdinal}'s content is kept in another chunk too.");
                    }

                    entries[key] = new(chunk.Name, contents.Header, entry);
                }

                foreach (KeyValuePair<(Guid, uint, uint, ulong), SessionContentEntry> entry in entries)
                {
                    byRecord[entry.Key] = entry.Value;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // The chunk is not read in part; the others still are, and the problem is stated for every record.
                problem ??= $"'{chunk.Name}' could not be read: {exception.Message}";
            }
        }

        return new(byRecord, chunks.Length, problem);
    }

    /// <summary>
    /// The first <paramref name="maximum"/> bytes of an entry's fragment, read afresh from its chunk and checked again, or
    /// null when the capture kept them without inspection consent: they are then never read for a person (ADR-036).
    /// </summary>
    public static byte[]? ReadBytes(IOwnedDirectory directory, SessionContentEntry entry, int maximum)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.Inspectable)
        {
            return null;
        }

        using FileStream stream = directory.OpenOwnedFile(
            entry.ChunkName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        return ContentChunkV1.ReadBytes(stream, entry.Entry, maximum);
    }
}
