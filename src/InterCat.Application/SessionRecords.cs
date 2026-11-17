using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One record of a session found by its raw identity (`contracts/identity-v1.md`): the stream, the source epoch and the
/// ordinal its capture gave it, which stay its own in every generation, where an evidence page's segment and row do not. A
/// message's content record is named so (ADR-112), as `icat content` prints a record: "raw 1/1/11".
/// </summary>
public static class SessionRecords
{
    /// <summary>
    /// The record of the current generation whose raw identity is <paramref name="stream"/>, <paramref name="epoch"/> and
    /// <paramref name="ordinal"/> within its capture, with what the generation keeps of its content; null where it holds
    /// none. Each segment is read once, outside the store's cache, as a one-off pass reads, its ordinals first.
    /// </summary>
    public static SessionEvidenceRecord? Find(
        SessionStore store,
        Guid expectedSessionId,
        uint stream,
        uint epoch,
        ulong ordinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException(SessionStore.AnotherSession);
        }

        SessionContentIndex content = manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
            ? SessionDerivationCache.For(manifest).Content(store.Root, cancellationToken)
            : SessionContentIndex.Empty;
        foreach (string name in SessionSegments.Names(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
            SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (ordinals.UnsignedAt(row) != ordinal
                    || segment.UnsignedValue(SegmentColumnId.RawStreamId, row) != stream
                    || segment.UnsignedValue(SegmentColumnId.RawSourceEpoch, row) != epoch)
                {
                    continue;
                }

                ObservationRowV1 observation = segment.Row(row);
                return new(observation.ObservationIdIn(segment.CaptureId, segment.Derivation), name, row, observation)
                {
                    Content = content.Find(segment.CaptureId, observation),
                    ContentProblem = content.Problem,
                };
            }
        }

        return null;
    }
}
