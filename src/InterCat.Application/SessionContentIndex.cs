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

/// <summary>
/// The content a generation keeps, by record (`contracts/content-v1.md`, ADR-036): every chunk it names, read and checked
/// once. Only the viewer's and the command line's record readers use it; nothing that reads metadata does.
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

    /// <summary>
    /// Reads every content chunk <paramref name="manifest"/> names. A chunk holds content of its capture's records, which
    /// are the generation's segments' records; a chunk of another capture, or a record kept twice, is a refusal.
    /// </summary>
    internal static SessionContentIndex Read(IOwnedDirectory directory, SessionManifestV1 manifest, CancellationToken cancellationToken)
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
