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
/// the one before it. Beside it go the tiles of the chunk the generation it extends added (tile-index-v1 §6), which every
/// later generation carries, so its overview and timeline count every chunk but the newest from tiles (S4).
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

        EvidenceLease acquired;
        try
        {
            acquired = store.AcquireLease();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return [];
        }

        using EvidenceLease lease = acquired;
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.Generation >= generation
            || SessionSegments.SourceClock(store.Root, manifest) is not { } clock
            || SessionSegments.Names(manifest).Count == 0)
        {
            return [];
        }

        // Each index saves a reader time and never changes an answer, so one that cannot be made is not staged, and the
        // generation is published without it: the checkpoint and each chunk's tiles alike.
        var staged = new List<StoreStagingFile>();
        StoreStagingFile? checkpoint = null;
        ProcessInstanceIndex? bound = null;
        TransportRelationIndex? related = null;
        try
        {
            // Opened only as the derivation reads them: one already made, as a live follow's is, opens none.
            var segments = new SegmentsOnDemand(store, manifest, SessionSegments.Names(manifest));
            var fields = new SegmentsOnDemand(store, manifest, SessionSegments.FieldNames(manifest));
            SessionDerivation derivation = SessionDerivationCache.For(manifest);
            ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
            bound = processes;
            TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);
            related = relations;
            ProcessActivityIndex activity = derivation.Activity(store.Root, segments, clock, fields, cancellationToken);
            checkpoint = store.Stage(DerivationCheckpoint.FileNameFor(generation), StoreDependencyKind.Index);
            _ = DerivationCheckpoint.Write(checkpoint.Content, manifest.SessionId, manifest.Generation, processes, relations, activity);
            _ = checkpoint.Complete();
            staged.Add(checkpoint);
            checkpoint = null;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            checkpoint?.Dispose();
        }
        catch
        {
            checkpoint?.Dispose();
            throw;
        }

        try
        {
            // The chunks' tiles bind their owners as this generation's instances do, and key their channels by the spans
            // of the ends its relations hold, which a later generation checks before counting an owner or a channel from
            // them.
            staged.AddRange(ChunkTiles.Stage(store, manifest, generation, bound, related, cancellationToken));
        }
        catch
        {
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }

            throw;
        }

        return staged;
    }
}
