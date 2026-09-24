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
/// Resolves a normalized row back to its exact admitted journal envelope. The request is bound to the row's
/// segment position and published generation; it never follows a stale selection into a newer snapshot.
/// </summary>
public static class SessionRawRecordQuery
{
    public const int DefaultPreviewBytes = 256;
    public const int MaximumPreviewBytes = 1024;

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

        StoreDependency[] journals = [.. manifest.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)];
        if (journals.Length == 0)
            return Unavailable("The original journal is not retained in this generation. The normalized "
                + "source row remains available, but its body and extended data cannot be recovered.");

        SessionRawRecordDetail? found = null;
        foreach (StoreDependency journal in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream stream = store.Root.OpenOwnedFile(journal.Name, FileMode.Open, FileAccess.Read,
                FileShare.Read, FileOptions.SequentialScan);
            JournalV1Reader.ReplayBatches(stream, (capture, clock, schemas, batch) =>
            {
                foreach (RecordEnvelopeV1 envelope in batch.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (envelope.Id != selected.ObservationId.RawRecordId) continue;
                    if (found is not null)
                        throw new InvalidDataException("The selected raw-record identity occurs more than once "
                            + "in the retained journals.");
                    if (envelope.Header.ProviderId != published.ProviderId
                        || envelope.Header.EventId != published.EventId
                        || envelope.Header.Version != published.DescriptorVersion
                        || envelope.NativeTicks != published.NativeTicks)
                        throw new InvalidDataException("The original journal record disagrees with the selected "
                            + "normalized row's source descriptor or native reading.");

                    string? fingerprint = envelope.SchemaReference is { } schemaReference
                        ? schemas.Find(schemaReference)?.Fingerprint
                            ?? throw new InvalidDataException("The journal record names a missing schema reference.")
                        : null;
                    if (fingerprint is not null && fingerprint != published.SchemaFingerprint)
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
                    found = new(manifest.SessionId, manifest.Generation, selected.ObservationId, journal.Name,
                        true, null, envelope.Header, envelope.BufferContext, envelope.ClockId,
                        envelope.TimestampEncoding, envelope.NativeTicks, envelope.PointerSize,
                        envelope.SchemaReference, fingerprint, envelope.AdmissionPolicyReference, policyId,
                        body.Classification,
                        body.Disposition, body.OriginalLength, kept, Array.AsReadOnly(extended),
                        envelope.OmittedExtendedItemCount,
                        preview, revealBodyBytes && previewLength < kept);
                }
            }, cancellationToken);
        }

        return found ?? Unavailable("The original record is outside the retained journal boundary. "
            + "Its normalized fact survives, but the original body and extended data are unavailable.");

        SessionRawRecordDetail Unavailable(string reason) => new(
            manifest.SessionId, manifest.Generation, selected.ObservationId, null, false, reason,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            [], null, null, false);
    }
}
