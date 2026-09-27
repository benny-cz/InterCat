using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What publishing a derivation checkpoint came to.</summary>
public enum CheckpointOutcome
{
    /// <summary>A checkpoint of the current generation was published as the next generation.</summary>
    Published = 1,

    /// <summary>The generation already names a checkpoint covering every segment it names.</summary>
    AlreadyCurrent = 2,

    /// <summary>The generation names no source clock or no observation segment, so nothing derives from it.</summary>
    NothingToDerive = 3,

    /// <summary>Publishing was refused or failed; the reason says why. The session is as complete as before.</summary>
    Refused = 4,
}

/// <summary>What publishing a generation's derivation checkpoint did.</summary>
/// <param name="Outcome">Whether one was published, and if not, why not.</param>
/// <param name="SourceGeneration">The generation the checkpoint describes, or 0 when the session has none.</param>
/// <param name="PublishedGeneration">The generation that published it; null when none was published.</param>
/// <param name="Bytes">The checkpoint's length, when one was published.</param>
/// <param name="Elapsed">How long deriving, writing and publishing took.</param>
/// <param name="Reason">Why none was published.</param>
public sealed record CheckpointPublication(
    CheckpointOutcome Outcome,
    long SourceGeneration,
    long? PublishedGeneration,
    long Bytes,
    TimeSpan Elapsed,
    string? Reason);

/// <summary>
/// Publishes the derivation checkpoint of a session's current generation (`contracts/derivation-checkpoint-v1.md`), so
/// that opening the session builds its process instances and relations from the checkpoint rather than from every
/// record (§12.1 S1). A writer does this once it has finished writing: an import, a finished live follow, a compaction,
/// a re-derivation. A checkpoint only saves time, so failing to publish one fails nothing and says why.
/// </summary>
public static class SessionCheckpoints
{
    /// <summary>
    /// Publishes a checkpoint of the current generation as the next generation, unless the generation already names one
    /// covering every segment it names. <paramref name="store"/> must be a writer's store; a writer that published since
    /// it was opened is refused, as any stale writer is.
    /// </summary>
    public static CheckpointPublication Publish(
        SessionStore store,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        long started = Stopwatch.GetTimestamp();
        if (store.Current is null)
        {
            return new(CheckpointOutcome.NothingToDerive, 0, null, 0, TimeSpan.Zero,
                "The session has published no generation to derive a checkpoint of.");
        }

        try
        {
            using EvidenceLease lease = store.AcquireLease();
            SessionManifestV1 manifest = lease.Manifest;
            if (SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
            {
                return Unpublished(CheckpointOutcome.NothingToDerive, manifest.Generation, started,
                    "The generation names no source clock, so nothing is derived from it.");
            }

            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            if (segments.Length == 0)
            {
                return Unpublished(CheckpointOutcome.NothingToDerive, manifest.Generation, started,
                    "The generation names no observation segment, so there is nothing to derive.");
            }

            if (IsCurrent(store.Root, manifest, clock, segments, fields))
            {
                return Unpublished(CheckpointOutcome.AlreadyCurrent, manifest.Generation, started,
                    "The generation already names a checkpoint and an overview of every segment it names.");
            }

            // The derivation checkpoint and the persisted overview are published together: with both, a reopen opens no
            // segment before its first view (overview-v1).
            SessionDerivation derivation = SessionDerivationCache.For(manifest);
            ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
            TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);
            ProcessActivityIndex activity = derivation.Activity(store.Root, segments, clock, fields, cancellationToken);
            OverviewCounts counts = SessionOverviewProjector.Count(segments, cancellationToken);
            long next = store.NextGeneration;
            using StoreStagingFile checkpoint = store.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index);
            long bytes = DerivationCheckpoint.Write(
                checkpoint.Content, manifest.SessionId, manifest.Generation, processes, relations, activity);
            _ = checkpoint.Complete();
            using StoreStagingFile overview = store.Stage(SessionOverviewIndex.FileNameFor(next), StoreDependencyKind.Index);
            bytes += SessionOverviewIndex.Write(
                overview.Content, manifest.SessionId, manifest.Generation, SessionOverviewIndex.ObservationSegments(manifest), counts);
            _ = overview.Complete();

            // Everything read is written, so the lease goes before the commit: a writer removes superseded manifests
            // only while no lease anywhere holds the session (store-v1 §9), and this one would keep them. The commit
            // re-measures every dependency and refuses a generation that is no longer current, so nothing relies on it.
            lease.Dispose();
            StoreCommitResult result = store.CommitIndex([checkpoint, overview], manifest.Generation, committedUtc, next, cancellationToken);
            return new(CheckpointOutcome.Published, manifest.Generation, result.Manifest.Generation, bytes,
                Stopwatch.GetElapsedTime(started), null);
        }
        catch (InvalidOperationException exception)
        {
            return Refused(store, started, exception);
        }
        catch (InvalidDataException exception)
        {
            return Refused(store, started, exception);
        }
        catch (IOException exception)
        {
            return Refused(store, started, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refused(store, started, exception);
        }
    }

    /// <summary>
    /// The derivation checkpoint and the persisted overview a generation names, each null when it names none. A report
    /// states what a writer published; whether they are used is decided where the session is read.
    /// </summary>
    public static (StoreDependency? Checkpoint, StoreDependency? Overview) NamedBy(SessionManifestV1 manifest) =>
        (DerivationCheckpoint.NamedBy(manifest), SessionOverviewIndex.NamedBy(manifest));

    /// <summary>
    /// Whether the generation names a readable checkpoint holding every derivation and a readable persisted overview,
    /// each covering exactly the segments it names. A checkpoint written before revision 166 holds no activity, which a
    /// reopen would count from every segment, so it is replaced.
    /// </summary>
    private static bool IsCurrent(
        IOwnedDirectory directory,
        SessionManifestV1 manifest,
        SourceClockDescriptor clock,
        SegmentReaderV1[] segments,
        SegmentReaderV1[] fields)
    {
        try
        {
            return DerivationCheckpoint.NamedBy(manifest) is { } checkpoint
                && DerivationCheckpoint.Read(
                        SessionSegments.ReadVerified(directory, checkpoint, DerivationCheckpoint.MaximumBytes),
                        manifest.SessionId,
                        clock) is { Activity: not null } saved
                && saved.Covers(segments, fields)
                && SessionOverviewIndex.NamedBy(manifest) is { } overview
                && SessionOverviewIndex.Covers(
                    SessionOverviewIndex.Read(
                        SessionSegments.ReadVerified(directory, overview, SessionOverviewIndex.MaximumBytes),
                        manifest.SessionId).Segments,
                    manifest);
        }
        catch (InvalidDataException)
        {
            // What cannot be read is replaced by what is published now.
            return false;
        }
    }

    private static CheckpointPublication Unpublished(CheckpointOutcome outcome, long generation, long started, string reason) =>
        new(outcome, generation, null, 0, Stopwatch.GetElapsedTime(started), reason);

    private static CheckpointPublication Refused(SessionStore store, long started, Exception exception) =>
        new(CheckpointOutcome.Refused, store.Current?.Generation ?? 0, null, 0, Stopwatch.GetElapsedTime(started),
            exception.Message);
}
