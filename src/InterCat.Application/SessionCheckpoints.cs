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

    /// <summary>
    /// The generation already names a checkpoint, an overview and an operation index, each covering every segment it names.
    /// </summary>
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
                    "The generation already names a checkpoint, an overview and an index of its calls, each of every "
                    + "segment it names.");
            }

            // The derivation checkpoint, the persisted overview and the operation index are published together: with
            // them, a reopen opens no segment before its first view (overview-index-v1), nor before its first call
            // ranking, RPC or HTTP listing, or brush (operation-index-v1).
            SessionDerivation derivation = SessionDerivationCache.For(manifest);
            ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
            TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);
            ProcessActivityIndex activity = derivation.Activity(store.Root, segments, clock, fields, cancellationToken);
            OverviewCounts counts = SessionOverviewProjector.Count(
                segments, SessionOverviewProjector.RetainedFromTicks(manifest), cancellationToken);

            // What the first view under a byte ranking reads is kept with the counts, so it reads no segment either: each
            // overview column's bytes per mechanism, for the lanes (overview-index-v1 minor 2), and each process's and TCP
            // channel end's over the whole session, for the ranked table and the graph (minor 3). One pass sums both.
            (OverviewLaneBytes? laneBytes, OverviewProcessBytes? processBytes) =
                SumBytes(segments, counts.Main, processes, relations, cancellationToken);
            counts = counts with { LaneBytes = laneBytes, ProcessBytes = processBytes };

            // A capture that collected ALPC keeps its RPC links with the overview, so its first view follows no call
            // (overview-index-v1 §3); one that did not keeps none.
            RpcPeerIndex peers = derivation.RpcPeers(store.Root, segments, clock, fields, cancellationToken);
            HttpExchangeIndex exchanges = derivation.HttpExchanges(store.Root, segments, clock, fields, cancellationToken);
            if (RpcPeerEdges.Collected(SessionSegments.CoverageLedger(store.Root, manifest)))
            {
                counts = counts with { RpcLinks = RpcPeerEdges.Totals(peers) };
            }

            long next = store.NextGeneration;
            using StoreStagingFile checkpoint = store.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index);
            long bytes = DerivationCheckpoint.Write(
                checkpoint.Content, manifest.SessionId, manifest.Generation, processes, relations, activity);
            _ = checkpoint.Complete();
            using StoreStagingFile overview = store.Stage(SessionOverviewIndex.FileNameFor(next), StoreDependencyKind.Index);
            bytes += SessionOverviewIndex.Write(
                overview.Content, manifest.SessionId, manifest.Generation, SessionOverviewIndex.ObservationSegments(manifest), counts);
            _ = overview.Complete();
            using StoreStagingFile operations = store.Stage(OperationIndex.FileNameFor(next), StoreDependencyKind.Index);
            bytes += OperationIndex.Write(
                operations.Content,
                manifest.SessionId,
                manifest.Generation,
                SessionOverviewIndex.ObservationSegments(manifest),
                SessionOverviewIndex.FieldSegments(manifest),
                peers.Calls,
                peers,
                exchanges);
            _ = operations.Complete();

            // Everything read is written, so the lease goes before the commit: a writer removes superseded manifests
            // only while no lease anywhere holds the session (store-v1 §9), and this one would keep them. The commit
            // re-measures every dependency and refuses a generation that is no longer current, so nothing relies on it.
            lease.Dispose();
            StoreCommitResult result = store.CommitIndex(
                [checkpoint, overview, operations], manifest.Generation, committedUtc, next, cancellationToken);
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
    /// Whether the generation names a readable checkpoint holding every derivation, a readable persisted overview and a
    /// readable operation index keeping its exchanges, each covering exactly the segments it names. A checkpoint written
    /// before revision 166 holds no activity, which a reopen would count from every segment, so it is replaced, and so is
    /// a generation published before revision 440, which names no operation index, or 441, whose index keeps no exchanges.
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
                    manifest)
                && OperationIndex.NamedBy(manifest) is { } operations
                && OperationIndex.Read(
                    SessionSegments.ReadVerified(directory, operations, OperationIndex.MaximumBytes),
                    manifest.SessionId,
                    clock,
                    saved.Processes) is { KeepsExchanges: true } kept
                && kept.Covers(SessionOverviewIndex.ObservationSegments(manifest), SessionOverviewIndex.FieldSegments(manifest));
        }
        catch (InvalidDataException)
        {
            // What cannot be read is replaced by what is published now.
            return false;
        }
    }

    /// <summary>
    /// The overview's lane bytes, when it has columns, and every process's and TCP channel end's bytes before any evidence
    /// policy, when an overview keeps them, summed in one pass over the segments; each null when it is not kept.
    /// </summary>
    private static (OverviewLaneBytes? Lanes, OverviewProcessBytes? Processes) SumBytes(
        IReadOnlyList<SegmentReaderV1> segments,
        TimelineColumns? main,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        CancellationToken cancellationToken)
    {
        SessionIntervalByteQuery.LanePlan? lanes = main is null ? null : new(main);
        SessionByteRanking.KeptPlan? kept = SessionByteRanking.KeptPlan.For(processes, relations);
        if (lanes is null && kept is null)
        {
            return (null, null);
        }

        var total = new ByteTallies(lanes?.Start(), kept?.Start());
        SegmentPasses.Run(
            segments,
            () => new ByteTallies(lanes?.Start(), kept?.Start()),
            (segment, tallies) =>
            {
                lanes?.Measure(segment, tallies.Lanes!, cancellationToken);
                kept?.Measure(segment, tallies.Kept!, cancellationToken);
            },
            total.Add,
            cancellationToken);
        return (lanes?.Cells(total.Lanes!), kept?.Finish(total.Kept!));
    }

    /// <summary>One worker's lane and kept bytes, merged once it has no segment left.</summary>
    private sealed class ByteTallies(TransportByteTally? lanes, SessionByteRanking.KeptTally? kept)
    {
        public TransportByteTally? Lanes { get; } = lanes;

        public SessionByteRanking.KeptTally? Kept { get; } = kept;

        public void Add(ByteTallies other)
        {
            Lanes?.Add(other.Lanes!);
            Kept?.Add(other.Kept!);
        }
    }

    private static CheckpointPublication Unpublished(CheckpointOutcome outcome, long generation, long started, string reason) =>
        new(outcome, generation, null, 0, Stopwatch.GetElapsedTime(started), reason);

    private static CheckpointPublication Refused(SessionStore store, long started, Exception exception) =>
        new(CheckpointOutcome.Refused, store.Current?.Generation ?? 0, null, 0, Stopwatch.GetElapsedTime(started),
            exception.Message);
}
