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

    public override string ToString() => string.Create(
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
        (string name, JournalV1Contents contents, long length) = Read(store);
        using (contents)
        {
            JournalReleasePreview preview = Measure(name, contents, length, firstRetainedRecordIndex);
            if (!preview.ReleasesAnything)
            {
                throw new InvalidOperationException(
                    $"Releasing records before {firstRetainedRecordIndex} of '{name}' releases nothing: a batch "
                    + "is the unit of release and none of them ends before that. "
                    + (preview.HasAnyReleasableBoundary
                        ? $"The smallest boundary this journal allows is {preview.SmallestReleasingBoundary}."
                        : "This journal was written as a single batch, so it has no releasable boundary at all.")
                    + " Nothing was published.");
            }

            if (preview.WouldEmptyTheJournal)
            {
                throw new InvalidOperationException(
                    $"Releasing records before {firstRetainedRecordIndex} of '{name}' would leave no admitted "
                    + "evidence at all. ADR-010 keeps a journal by default; a session with no journal cannot "
                    + "re-derive anything, so this is refused rather than published. The largest boundary this "
                    + $"journal allows is {preview.LargestReleasingBoundary}.");
            }

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
        StoreDependency journal = manifest.Dependencies.SingleOrDefault(dependency =>
            dependency.Kind == StoreDependencyKind.Journal)
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
