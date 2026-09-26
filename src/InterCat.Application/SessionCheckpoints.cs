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
                    "The generation already names a checkpoint of every segment it names.");
            }

            SessionDerivation derivation = SessionDerivationCache.For(manifest);
            ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
            TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);
            long next = store.NextGeneration;
            using StoreStagingFile staged = store.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index);
            long bytes = DerivationCheckpoint.Write(staged.Content, manifest.SessionId, manifest.Generation, processes, relations);
            _ = staged.Complete();
            StoreCommitResult result = store.CommitIndex([staged], manifest.Generation, committedUtc, next, cancellationToken);
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

    /// <summary>Whether the generation names a readable checkpoint covering exactly the segments it names.</summary>
    private static bool IsCurrent(
        IOwnedDirectory directory,
        SessionManifestV1 manifest,
        SourceClockDescriptor clock,
        SegmentReaderV1[] segments,
        SegmentReaderV1[] fields)
    {
        try
        {
            return DerivationCheckpoint.NamedBy(manifest) is { } named
                && DerivationCheckpoint.Read(
                        SessionSegments.ReadVerified(directory, named, DerivationCheckpoint.MaximumBytes),
                        manifest.SessionId,
                        clock)
                    .Covers(segments, fields);
        }
        catch (InvalidDataException)
        {
            // One that cannot be read is replaced by the one published now.
            return false;
        }
    }

    private static CheckpointPublication Unpublished(CheckpointOutcome outcome, long generation, long started, string reason) =>
        new(outcome, generation, null, 0, Stopwatch.GetElapsedTime(started), reason);

    private static CheckpointPublication Refused(SessionStore store, long started, Exception exception) =>
        new(CheckpointOutcome.Refused, store.Current?.Generation ?? 0, null, 0, Stopwatch.GetElapsedTime(started),
            exception.Message);
}
