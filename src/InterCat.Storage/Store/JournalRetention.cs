using System.Globalization;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>What releasing a journal prefix would give up, computed before anything is written.</summary>
public sealed record JournalReleasePreview(
    string JournalName,
    long TotalRecords,
    long TotalBytes,
    long ReleasedRecords,
    long RetainedRecords,
    long ReleasedBatches,
    long RetainedBatches,
    RawRecordId? FirstRetained,
    long SmallestReleasingBoundary,
    long LargestReleasingBoundary)
{
    /// <summary>Whether there is anything to release. Releasing nothing is not an error, it is a no-op.</summary>
    public bool ReleasesAnything => ReleasedRecords > 0;

    /// <summary>
    /// Whether the release would empty the journal. ADR-010 keeps a journal by default, and a release that
    /// leaves no admitted evidence at all is refused rather than quietly turning a session into derived data
    /// nothing can be re-derived from.
    /// </summary>
    public bool WouldEmptyTheJournal => RetainedRecords == 0;

    /// <summary>
    /// Whether any boundary at all would release something. A journal written as one batch has none, which is
    /// a fact about how it was written rather than a refusal.
    /// </summary>
    public bool HasAnyReleasableBoundary => SmallestReleasingBoundary > 0;

    /// <summary>
    /// How many files the admitted evidence spans: 1, or the chunks of a live recording (ADR-022). For chunks,
    /// <see cref="JournalName"/> is the newest, which the committed boundary names, and every count covers them all.
    /// </summary>
    public int JournalChunks { get; init; } = 1;

    /// <summary>The chunks a release at this boundary gives up, oldest first. A single journal has none.</summary>
    public IReadOnlyList<string> ReleasedChunks { get; init; } = [];

    /// <summary>What a release gives up whole: a batch of one journal, or a chunk of a recording.</summary>
    public string ReleaseUnit => JournalChunks > 1 ? "chunk" : "batch";

    public override string ToString() => JournalChunks > 1
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{JournalChunks} journal chunks: release {ReleasedRecords} of {TotalRecords} records in "
            + $"{ReleasedChunks.Count} chunks, retain {RetainedRecords} in {JournalChunks - ReleasedChunks.Count}")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{JournalName}: release {ReleasedRecords} of {TotalRecords} records in {ReleasedBatches} batches, "
            + $"retain {RetainedRecords} in {RetainedBatches}");
}

/// <summary>
/// Releases a prefix of a session's admitted journal, which ADR-010 makes an explicit action rather than a
/// default. A journal is append-only and its published file is immutable, so a release does not truncate it:
/// it publishes a new journal holding the retained suffix, names the released extent in the generation's
/// retention record, and lets the old file go once no lease holds it.
/// </summary>
/// <remarks>
/// The unit of release is a whole batch. A journal's frames are checksummed batches, and releasing part of
/// one would mean rewriting a frame whose digest covers records that are no longer in it — so the boundary a
/// caller asks for is rounded down to a batch boundary and the preview says exactly what that costs.
///
/// A live recording's journal is a sequence of chunks (ADR-022), and there the unit is a whole chunk (ADR-024).
/// The oldest chunks stop being named and nothing is rewritten. Rewriting part of a chunk would publish it under
/// a new generation's name, which sorts after every chunk and would put its records out of order.
///
/// This gives up the ability to re-derive those records after a normalizer revision. That is the whole point
/// of it being explicit: the storage saving is visible and the loss is not, so the loss is the thing that
/// gets published.
/// </remarks>
public static class JournalRetention
{
    /// <summary>
    /// Computes what releasing every batch entirely before <paramref name="firstRetainedRecordIndex"/> would
    /// give up. Nothing is written and nothing is published.
    /// </summary>
    public static JournalReleasePreview Preview(
        SessionStore store,
        long firstRetainedRecordIndex,
        CancellationToken cancellationToken = default)
    {
        if (ChunksOf(store) is { } chunks)
        {
            return MeasureChunks(store, chunks, firstRetainedRecordIndex, cancellationToken);
        }

        (string name, JournalV1Contents contents, long length) = Read(store);
        using (contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Measure(name, contents, length, firstRetainedRecordIndex);
        }
    }

    /// <summary>
    /// Releases the prefix and publishes the retention generation. The retained journal is written whole
    /// before the generation names it, so an interruption leaves a staging file and the previous generation
    /// current, exactly as any other publication does.
    /// </summary>
    public static RetentionOutcome Release(
        SessionStore store,
        long firstRetainedRecordIndex,
        string reason,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (ChunksOf(store) is { } chunks)
        {
            JournalReleasePreview measured = MeasureChunks(store, chunks, firstRetainedRecordIndex, cancellationToken);
            RequireReleasable(measured, firstRetainedRecordIndex);
            cancellationToken.ThrowIfCancellationRequested();
            return store.ReleaseJournalChunks(
                measured.ReleasedChunks,
                measured.ReleasedRecords,
                reason,
                committedUtc,
                nowUtc);
        }

        (string name, JournalV1Contents contents, long length) = Read(store);
        using (contents)
        {
            JournalReleasePreview preview = Measure(name, contents, length, firstRetainedRecordIndex);
            RequireReleasable(preview, firstRetainedRecordIndex);
            cancellationToken.ThrowIfCancellationRequested();
            long generation = store.NextGeneration;
            string retainedName = SegmentFormatV1.JournalFileName(generation);
            using StoreStagingFile staged = store.Stage(retainedName, StoreDependencyKind.Journal);
            long retained = Write(staged, contents, preview);
            StoreDependency dependency = staged.Complete();
            var record = new RetentionRecord(
                RetentionExtentKind.JournalPrefix,
                nowUtc ?? DateTimeOffset.UtcNow,
                reason,
                [name],
                length - dependency.LengthBytes,
                preview.ReleasedRecords,
                SourceDigestOf(store, name));

            return store.ReleaseJournalPrefix(
                staged,
                new(dependency.Name, dependency.LengthBytes, retained, dependency.Digest),
                record,
                committedUtc,
                nowUtc);
        }
    }

    /// <summary>Refuses a release that gives up nothing, or everything, naming the boundaries that would work.</summary>
    private static void RequireReleasable(JournalReleasePreview preview, long firstRetainedRecordIndex)
    {
        string evidence = preview.JournalChunks > 1
            ? $"the {preview.JournalChunks} journal chunks"
            : $"'{preview.JournalName}'";
        if (!preview.ReleasesAnything)
        {
            throw new InvalidOperationException(
                $"Releasing records before {firstRetainedRecordIndex} of {evidence} releases nothing: a "
                + $"{preview.ReleaseUnit} is the unit of release and none of them ends before that. "
                + (preview.HasAnyReleasableBoundary
                    ? $"The smallest boundary this journal allows is {preview.SmallestReleasingBoundary}."
                    : preview.JournalChunks > 1
                        ? "No chunk can be released and still leave admitted records behind."
                        : "This journal was written as a single batch, so it has no releasable boundary at all.")
                + " Nothing was published.");
        }

        if (preview.WouldEmptyTheJournal)
        {
            throw new InvalidOperationException(
                $"Releasing records before {firstRetainedRecordIndex} of {evidence} would leave no admitted "
                + "evidence at all. ADR-010 keeps a journal by default; a session with no journal cannot "
                + "re-derive anything, so this is refused rather than published. "
                + (preview.HasAnyReleasableBoundary
                    ? $"The largest boundary this journal allows is {preview.LargestReleasingBoundary}."
                    : "No boundary of this journal leaves admitted records behind."));
        }
    }

    /// <summary>
    /// Measures a live recording's chunks. Every chunk is read to count its records, and a boundary releases each
    /// chunk that ends at or before it. Records count in stored order across the chunks the generation names, which
    /// is where `--release-journal-before-record` counts from.
    /// </summary>
    private static JournalReleasePreview MeasureChunks(
        SessionStore store,
        StoreDependency[] chunks,
        long firstRetainedRecordIndex,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstRetainedRecordIndex);
        var measured = new (long Records, long Batches, RawRecordId? First)[chunks.Length];
        for (int index = 0; index < chunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            measured[index] = CountRecords(store, chunks[index]);
        }

        long total = measured.Sum(chunk => chunk.Records);
        long end = 0;
        long released = 0;
        long releasedBatches = 0;
        long retainedBatches = 0;
        long smallest = 0;
        long largest = 0;
        RawRecordId? firstRetained = null;
        var releasedChunks = new List<string>();
        for (int index = 0; index < chunks.Length; index++)
        {
            end += measured[index].Records;

            // A boundary at a chunk's end is one the recording allows when it releases records and keeps some.
            if (end > 0 && end < total)
            {
                smallest = smallest == 0 ? end : smallest;
                largest = end;
            }

            // A chunk is released only when all of it is before the boundary. The chunks sort in the order they were
            // recorded, so the released ones are always the oldest.
            if (end <= firstRetainedRecordIndex && releasedChunks.Count == index)
            {
                released += measured[index].Records;
                releasedBatches += measured[index].Batches;
                releasedChunks.Add(chunks[index].Name);
                continue;
            }

            retainedBatches += measured[index].Batches;
            firstRetained ??= measured[index].First;
        }

        return new(
            chunks[^1].Name,
            total,
            chunks.Sum(chunk => chunk.LengthBytes),
            released,
            total - released,
            releasedBatches,
            retainedBatches,
            firstRetained,
            smallest,
            largest)
        {
            JournalChunks = chunks.Length,
            ReleasedChunks = releasedChunks,
        };
    }

    /// <summary>Counts one chunk's records and batches, and names its first record.</summary>
    private static (long Records, long Batches, RawRecordId? First) CountRecords(SessionStore store, StoreDependency chunk)
    {
        using FileStream stream = store.Root.OpenOwnedFile(
            chunk.Name,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        using JournalV1Contents contents = JournalV1Reader.Read(bytes);
        return (
            contents.Batches.Sum(batch => (long)batch.Records.Count),
            contents.Batches.Count,
            contents.Batches.Count > 0 ? contents.Batches[0].First : null);
    }

    /// <summary>A live recording's chunks, oldest first, or null when the generation names at most one journal.</summary>
    private static StoreDependency[]? ChunksOf(SessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        SessionManifestV1 manifest = store.Current
            ?? throw new InvalidOperationException("This session has published no generation to retain from.");
        StoreDependency[] journals =
        [
            .. manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
        ];
        return journals.Length > 1 ? journals : null;
    }

    private static JournalReleasePreview Measure(
        string name,
        JournalV1Contents contents,
        long length,
        long firstRetainedRecordIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstRetainedRecordIndex);
        long total = 0;
        long released = 0;
        long releasedBatches = 0;
        long retainedBatches = 0;
        long smallest = 0;
        long largest = 0;
        RawRecordId? firstRetained = null;
        int batchIndex = 0;
        int batchCount = contents.Batches.Count;
        foreach (JournalBatchV1 batch in contents.Batches)
        {
            total += batch.Records.Count;

            // The boundaries that do anything are the batch ends, and the last batch is never released,
            // because a release that empties the journal is refused. Naming them is what lets a caller pick a
            // boundary instead of guessing one.
            if (batchIndex == 0)
            {
                smallest = total;
            }

            if (batchIndex == batchCount - 2)
            {
                largest = total;
            }

            batchIndex++;

            // A batch is released only when the whole of it is before the boundary. A frame's checksum covers
            // its records, so releasing part of one would mean publishing a frame whose digest describes
            // records it no longer holds.
            if (total <= firstRetainedRecordIndex)
            {
                released += batch.Records.Count;
                releasedBatches++;
                continue;
            }

            retainedBatches++;
            firstRetained ??= batch.First;
        }

        return new(
            name,
            total,
            length,
            released,
            total - released,
            releasedBatches,
            retainedBatches,
            firstRetained,
            batchCount > 1 ? smallest : 0,
            batchCount > 1 ? largest : 0);
    }

    private static long Write(
        StoreStagingFile staged,
        JournalV1Contents contents,
        JournalReleasePreview preview)
    {
        // The retained journal is a complete journal-v1 file: the same capture, the same clock frame and the
        // same schema table, then the retained batches. A suffix that dropped the clock frame would be
        // refused on the next read, which is the correct behaviour for a file that names no clock.
        using var writer = JournalV1Writer.Create(
            staged.Content,
            contents.CaptureId,
            contents.SourceClock,
            contents.CreatedUtc);
        writer.WriteSchemas(contents.Schemas);
        long seen = 0;
        long written = 0;
        foreach (JournalBatchV1 batch in contents.Batches)
        {
            seen += batch.Records.Count;
            if (seen <= preview.ReleasedRecords)
            {
                continue;
            }

            foreach (RecordEnvelopeV1 record in batch.Records)
            {
                // The reader owns these envelopes and disposes them with the batch, so the writer is given a
                // copy of each. Handing it the original would have two owners returning one pooled buffer.
                writer.Append(Copy(record));
                written++;
            }

            writer.FlushBatch();
        }

        writer.Complete();
        return written;
    }

    /// <summary>
    /// An independent copy of one envelope, with its own buffer leases. §18.1 gives a buffer exactly one
    /// owner, so a record that is written while its reader still holds it is copied rather than shared.
    /// </summary>
    private static RecordEnvelopeV1 Copy(RecordEnvelopeV1 record)
    {
        var items = new List<ExtendedItemV1>(record.ExtendedItems.Count);
        EnvelopeBuffer? body = null;
        try
        {
            foreach (ExtendedItemV1 item in record.ExtendedItems)
            {
                items.Add(new()
                {
                    Type = item.Type,
                    Flags = item.Flags,
                    OriginalLength = item.OriginalLength,
                    Bytes = EnvelopeBuffer.CopyOf(item.Bytes.ReadOnlySpan),
                });
            }

            body = EnvelopeBuffer.CopyOf(record.Body.Bytes.ReadOnlySpan);
            return record with
            {
                ExtendedItems = items,
                Body = new()
                {
                    Classification = record.Body.Classification,
                    Disposition = record.Body.Disposition,
                    OriginalLength = record.Body.OriginalLength,
                    Bytes = body,
                },
            };
        }
        catch
        {
            body?.Dispose();
            foreach (ExtendedItemV1 item in items)
            {
                item.Dispose();
            }

            throw;
        }
    }

    private static (string Name, JournalV1Contents Contents, long Length) Read(SessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        SessionManifestV1 manifest = store.Current
            ?? throw new InvalidOperationException("This session has published no generation to retain from.");
        StoreDependency[] journals = [.. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)];
        StoreDependency journal = journals.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Generation {manifest.Generation} names no single admitted journal, so there is no prefix to "
                + "release.");

        using FileStream stream = store.Root.OpenOwnedFile(
            journal.Name,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return (journal.Name, JournalV1Reader.Read(bytes), bytes.LongLength);
    }

    /// <summary>
    /// The digest of the journal this release started from. It identifies what was given up: the released
    /// prefix's own bytes are gone, so the digest of the file that held them is what stays checkable.
    /// </summary>
    private static string SourceDigestOf(SessionStore store, string name) =>
        store.Current!.Dependencies
            .Single(dependency => dependency.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Digest;
}
