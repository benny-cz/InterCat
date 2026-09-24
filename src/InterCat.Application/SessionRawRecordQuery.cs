using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>Metadata and an optional bounded byte preview copied from one original journal-v1 envelope.</summary>
public sealed record SessionRawRecordDetail(
    Guid SessionId,
    long Generation,
    ObservationId ObservationId,
    string? JournalName,
    bool Available,
    string? UnavailableReason,
    EventHeaderFieldsV1? Header,
    BufferContextFieldsV1? BufferContext,
    ClockId? ClockId,
    TimestampEncoding? TimestampEncoding,
    long? NativeTicks,
    byte? PointerSize,
    uint? SchemaReference,
    string? SchemaFingerprint,
    uint? AdmissionPolicyReference,
    string? AdmissionPolicyId,
    BodyClassificationV1? BodyClassification,
    BodyDispositionV1? BodyDisposition,
    int? OriginalBodyLength,
    int? RetainedBodyLength,
    IReadOnlyList<RawExtendedItemSummary> ExtendedItems,
    int? OmittedExtendedItemCount,
    byte[]? BodyPreview,
    bool BodyPreviewTruncated);

public sealed record RawExtendedItemSummary(ushort Type, ushort Flags, int OriginalLength, int RetainedLength);

/// <summary>
/// Resolves a normalized row back to its exact admitted journal envelope. <see cref="Read"/> is bound to the row's
/// segment position in one published generation; <see cref="ReadRetained"/> follows the row's stable raw identity
/// into whatever generation is current, and checks the envelope against the row rather than trusting a position.
/// </summary>
public static class SessionRawRecordQuery
{
    public const int DefaultPreviewBytes = 256;
    public const int MaximumPreviewBytes = 1024;

    /// <summary>
    /// Resolve a generation-bound segment coordinate supplied by a headless evidence page. The second lease in
    /// <see cref="Read"/> rechecks the generation before touching raw evidence, so a publication between these
    /// two reads is a refusal rather than a shifted row.
    /// </summary>
    public static SessionRawRecordDetail ReadAt(
        SessionStore store,
        Guid expectedSessionId,
        long expectedGeneration,
        string segmentName,
        int segmentRow,
        bool revealBodyBytes = false,
        int maximumPreviewBytes = DefaultPreviewBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentName);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentRow);
        SessionEvidenceRecord selected;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            if (manifest.SessionId != expectedSessionId || manifest.Generation != expectedGeneration)
                throw new InvalidOperationException("This raw-record locator names another session or generation. "
                    + "Restart from icat evidence; no row was silently shifted.");
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, segmentName);
            if (segmentRow >= segment.RowCount)
                throw new ArgumentException("The row coordinate is outside its published segment.", nameof(segmentRow));
            ObservationRowV1 row = segment.Row(segmentRow);
            selected = new(row.ObservationIdIn(segment.CaptureId, segment.Derivation),
                segmentName, segmentRow, row);
        }

        return Read(store, expectedSessionId, expectedGeneration, selected,
            revealBodyBytes, maximumPreviewBytes, cancellationToken);
    }

    public static SessionRawRecordDetail Read(
        SessionStore store,
        Guid expectedSessionId,
        long expectedGeneration,
        SessionEvidenceRecord selected,
        bool revealBodyBytes = false,
        int maximumPreviewBytes = DefaultPreviewBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(selected);
        if (maximumPreviewBytes is < 1 or > MaximumPreviewBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPreviewBytes));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId || manifest.Generation != expectedGeneration)
            throw new InvalidOperationException("The session has published another generation. Reopen source rows "
                + "from the current workspace before viewing an original record.");

        SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, selected.SegmentName);
        if (selected.SegmentRow < 0 || selected.SegmentRow >= segment.RowCount)
            throw new InvalidDataException("The selected row is outside its published segment.");
        ObservationRowV1 published = segment.Row(selected.SegmentRow);
        if (published.ObservationIdIn(segment.CaptureId, segment.Derivation) != selected.ObservationId
            || published != selected.Observation)
            throw new InvalidDataException("The selected normalized fact does not match this generation's "
                + "published segment row.");

        return Resolve(store, manifest, selected, revealBodyBytes, maximumPreviewBytes, cancellationToken);
    }

    /// <summary>
    /// Resolves a row read from any generation of the same session, including one a live capture has since
    /// superseded. A raw identity is stable across generations and a journal is immutable evidence, so the current
    /// generation's retained journals are searched by that identity; the envelope found must carry the selected
    /// row's descriptor, native reading and schema, or it is refused. A record whose journal chunk retention has
    /// released is reported unavailable, never reconstructed.
    /// </summary>
    public static SessionRawRecordDetail ReadRetained(
        SessionStore store,
        Guid expectedSessionId,
        SessionEvidenceRecord selected,
        bool revealBodyBytes = false,
        int maximumPreviewBytes = DefaultPreviewBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(selected);
        if (maximumPreviewBytes is < 1 or > MaximumPreviewBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPreviewBytes));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId)
            throw new InvalidOperationException("This directory now holds another session. Reopen it from the "
                + "workspace before viewing an original record.");
        return Resolve(store, manifest, selected, revealBodyBytes, maximumPreviewBytes, cancellationToken);
    }

    /// <summary>
    /// Finds the envelope in the retained journals by its raw identity. The first search skips batches whose declared
    /// ordinals cannot hold it, so a lookup normally reads one batch rather than the whole capture. A record is
    /// reported unavailable only after a second, exhaustive search agrees, because the skip relies on a stored order
    /// the journal contract does not itself promise.
    /// </summary>
    private static SessionRawRecordDetail Resolve(
        SessionStore store,
        SessionManifestV1 manifest,
        SessionEvidenceRecord selected,
        bool revealBodyBytes,
        int maximumPreviewBytes,
        CancellationToken cancellationToken)
    {
        ObservationRowV1 expected = selected.Observation;
        StoreDependency[] journals = [.. manifest.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)];
        if (journals.Length == 0)
            return Unavailable("The original journal is not retained in this generation. The normalized "
                + "source row remains available, but its body and extended data cannot be recovered.");

        SessionRawRecordDetail? found = null;
        foreach (bool exhaustive in new[] { false, true })
        {
            foreach (StoreDependency journal in journals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = store.Root.OpenOwnedFile(journal.Name, FileMode.Open, FileAccess.Read,
                    FileShare.Read, exhaustive ? FileOptions.SequentialScan : FileOptions.RandomAccess);
                if (JournalV1Reader.FindRecord(stream, selected.ObservationId.RawRecordId,
                        (clock, schemas, envelope) => found = Describe(journal.Name, schemas, envelope),
                        exhaustive, cancellationToken))
                    return found!;
            }
        }

        return Unavailable("The original record is outside the retained journal boundary. "
            + "Its normalized fact survives, but the original body and extended data are unavailable.");

        SessionRawRecordDetail Describe(string journalName, JournalV1SchemaTable schemas, RecordEnvelopeV1 envelope)
        {
            if (envelope.Header.ProviderId != expected.ProviderId
                || envelope.Header.EventId != expected.EventId
                || envelope.Header.Version != expected.DescriptorVersion
                || envelope.NativeTicks != expected.NativeTicks)
                throw new InvalidDataException("The original journal record disagrees with the selected "
                    + "normalized row's source descriptor or native reading.");

            string? fingerprint = envelope.SchemaReference is { } schemaReference
                ? schemas.Find(schemaReference)?.Fingerprint
                    ?? throw new InvalidDataException("The journal record names a missing schema reference.")
                : null;
            if (fingerprint is not null && fingerprint != expected.SchemaFingerprint)
                throw new InvalidDataException("The original journal schema disagrees with the selected "
                    + "normalized row's fingerprint.");
            string policyId = schemas.FindPolicy(envelope.AdmissionPolicyReference)?.PolicyId
                ?? throw new InvalidDataException("The journal record names a missing admission policy.");

            BodyV1 body = envelope.Body;
            int kept = body.RetainedLength;
            int previewLength = revealBodyBytes ? Math.Min(kept, maximumPreviewBytes) : 0;
            byte[]? preview = revealBodyBytes
                ? body.Bytes.ReadOnlySpan[..previewLength].ToArray() : null;
            RawExtendedItemSummary[] extended = [.. envelope.ExtendedItems.Select(item =>
                new RawExtendedItemSummary(item.Type, item.Flags, item.OriginalLength, item.Bytes.Length))];
            return new(manifest.SessionId, manifest.Generation, selected.ObservationId, journalName,
                true, null, envelope.Header, envelope.BufferContext, envelope.ClockId,
                envelope.TimestampEncoding, envelope.NativeTicks, envelope.PointerSize,
                envelope.SchemaReference, fingerprint, envelope.AdmissionPolicyReference, policyId,
                body.Classification,
                body.Disposition, body.OriginalLength, kept, Array.AsReadOnly(extended),
                envelope.OmittedExtendedItemCount,
                preview, revealBodyBytes && previewLength < kept);
        }

        SessionRawRecordDetail Unavailable(string reason) => new(
            manifest.SessionId, manifest.Generation, selected.ObservationId, null, false, reason,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            [], null, null, false);
    }
}
