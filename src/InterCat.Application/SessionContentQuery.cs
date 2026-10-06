using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One record's kept content as a person inspects it (§3.7, ADR-036): the fragment's facts and, only when asked for and
/// only when the capture kept them with consent to inspect them, its bytes, read afresh from the chunk and checked.
/// </summary>
public sealed record SessionContentDetail
{
    /// <summary>Whether the generation read still keeps the record's content.</summary>
    public required bool Available { get; init; }

    /// <summary>Why it does not, as a sentence; null when it does.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The generation the content was read from.</summary>
    public required long Generation { get; init; }

    /// <summary>The record's normalized row, whose source and owner the facts name.</summary>
    public required ObservationRowV1 Observation { get; init; }

    /// <summary>The fragment, the chunk that holds it and what it was kept under; null when unavailable.</summary>
    public SessionContentEntry? Entry { get; init; }

    /// <summary>
    /// Every kept byte of the fragment, checked against its checksum; null unless they were asked for and may be shown.
    /// A fragment is at most its chunk's record limit, so this is bounded before a byte is read.
    /// </summary>
    public byte[]? Bytes { get; init; }
}

/// <summary>
/// Reads one record's kept content (`contracts/content-v1.md` §4). A record is found by its stable raw identity, so it
/// stays reachable while a live capture publishes newer generations, and every read holds a lease on the generation it
/// reads, so a retention release cannot remove the chunk beneath it.
/// </summary>
public static class SessionContentQuery
{
    /// <summary>
    /// The content the current generation keeps of <paramref name="selected"/>, whichever generation its page was read
    /// from; <paramref name="revealBytes"/> asks for its bytes too.
    /// </summary>
    public static SessionContentDetail Read(
        SessionStore store,
        Guid expectedSessionId,
        SessionEvidenceRecord selected,
        bool revealBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(selected);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException("This directory now holds another session. Reopen it from the "
                + "workspace before inspecting a record's content.");
        }

        return Resolve(store, manifest, selected.ObservationId.RawRecordId, selected.Observation, revealBytes,
            cancellationToken);
    }

    /// <summary>
    /// The content kept of the record an evidence page's exact locator names. The locator binds a generation, so one
    /// that names another session or generation is refused rather than followed to a shifted row.
    /// </summary>
    public static SessionContentDetail ReadAt(
        SessionStore store,
        Guid expectedSessionId,
        long expectedGeneration,
        string segmentName,
        int segmentRow,
        bool revealBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentName);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentRow);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId || manifest.Generation != expectedGeneration)
        {
            throw new InvalidOperationException("This record locator names another session or generation. Restart "
                + "from icat evidence; no row was silently shifted.");
        }

        SegmentReaderV1 segment = SessionSegments.Open(store, manifest, segmentName);
        if (segmentRow >= segment.RowCount)
        {
            throw new ArgumentException("The row coordinate is outside its published segment.", nameof(segmentRow));
        }

        ObservationRowV1 row = segment.Row(segmentRow);
        return Resolve(store, manifest, row.ObservationIdIn(segment.CaptureId, segment.Derivation).RawRecordId, row,
            revealBytes, cancellationToken);
    }

    /// <summary>
    /// What the generation says of a content release, when one was published - by this generation, or by an earlier one
    /// whose record it carries: when and why every record's content went (content-v1 §2). Null when none was.
    /// </summary>
    public static string? ReleasedContent(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.LatestRelease(RetentionExtentKind.Content)?.Record is { } release
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture,
                $"Its content was released on {release.ReleasedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC, with every record's content in the session: \"{release.Reason}\". Every record's metadata is kept, and none of its bytes.")
            : null;
    }

    private static SessionContentDetail Resolve(
        SessionStore store,
        SessionManifestV1 manifest,
        RawRecordId record,
        ObservationRowV1 observation,
        bool revealBytes,
        CancellationToken cancellationToken)
    {
        SessionContentIndex index = manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
            ? SessionDerivationCache.For(manifest).Content(store.Root, cancellationToken)
            : SessionContentIndex.Empty;
        if (index.Find(record) is not { } entry)
        {
            return new()
            {
                Available = false,
                Generation = manifest.Generation,
                Observation = observation,
                UnavailableReason = index.Problem is { } problem
                    ? $"This session's kept content could not all be read ({problem}), so this record's may be in what "
                        + "could not."
                    : ReleasedContent(manifest) is { } released
                        ? released
                        : "The session keeps no content of this record now. Kept content goes with its journal chunk, or "
                            + "on its own by a content release, so a retention may have released it.",
            };
        }

        byte[]? bytes = null;
        if (revealBytes && entry.Inspectable)
        {
            using FileStream stream = store.Root.OpenOwnedFile(
                entry.ChunkName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            bytes = ContentChunkV1.ReadBytes(stream, entry.Entry, entry.Fragment.Kept);
        }

        return new()
        {
            Available = true, Generation = manifest.Generation, Observation = observation, Entry = entry, Bytes = bytes,
        };
    }
}
