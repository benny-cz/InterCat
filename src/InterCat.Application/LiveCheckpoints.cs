using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Indexes each generation a live follow publishes with a derivation checkpoint of the generation it extends
/// (derivation-checkpoint-v1 §2): its instances, relations and counts, which a reader of the new generation extends over
/// the segments that generation adds (§4's prefix). Opening a session while it is still being recorded then derives from
/// its newest chunk rather than from every record (§12.1 S1). The derivation is the one the window draws the generation
/// from, shared through the derivation cache, so in the window nothing is derived twice, and each generation's extends
/// the one before it.
/// </summary>
public sealed class LiveCheckpoints : ILiveIndexer
{
    private LiveCheckpoints()
    {
    }

    /// <summary>The one indexer: it keeps no state of its own.</summary>
    public static LiveCheckpoints Instance { get; } = new();

    public IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Current is null)
        {
            return [];
        }

        StoreStagingFile? staged = null;
        try
        {
            using EvidenceLease lease = store.AcquireLease();
            SessionManifestV1 manifest = lease.Manifest;
            if (manifest.Generation >= generation
                || SessionSegments.SourceClock(store.Root, manifest) is not { } clock
                || SessionSegments.Names(manifest).Count == 0)
            {
                return [];
            }

            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            SessionDerivation derivation = SessionDerivationCache.For(manifest);
            ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
            TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);
            ProcessActivityIndex activity = derivation.Activity(store.Root, segments, clock, fields, cancellationToken);
            staged = store.Stage(DerivationCheckpoint.FileNameFor(generation), StoreDependencyKind.Index);
            _ = DerivationCheckpoint.Write(staged.Content, manifest.SessionId, manifest.Generation, processes, relations, activity);
            _ = staged.Complete();
            return [staged];
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // A checkpoint saves a reader time and never changes an answer, so one that cannot be made is not staged, and
            // the generation is published without it.
            staged?.Dispose();
            return [];
        }
    }
}
