using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A finished session publishes its derivation checkpoint, and opening it again builds its processes and relationships
/// from the checkpoint instead of from every record (`contracts/derivation-checkpoint-v1.md`, §12.1 S1). What the
/// window shows must be exactly what a derivation from every segment shows, and a checkpoint that cannot be used costs
/// time and a caveat, never an answer.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class DerivationCheckpointOverviewTests
{
    [Fact(DisplayName = "I14: a session reopened from its checkpoint and overview opens no segment and projects as a derivation of every segment")]
    public void AReopenedSessionProjectsFromItsCheckpoint()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        long written = session.Store.Current!.Generation;
        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);
        string derived = Comparable(full);
        CheckpointPublication publication = SessionCheckpoints.Publish(session.Store, Committed);
        Assert.Equal((CheckpointOutcome.Published, written, written + 1), (publication.Outcome, publication.SourceGeneration, publication.PublishedGeneration));
        Assert.True(publication.Bytes > 0);
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(session.Store, Committed).Outcome);

        // A fresh viewer, with nothing derived in memory, takes both derivations from the checkpoint as they stand and
        // the timeline from the persisted overview: it opens no segment before its first view (S1).
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        SessionDerivation slot = SessionDerivationCache.For(reopened.Current!);
        Assert.Equal((true, true, true, null, null),
            (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint, slot.ActivityFromCheckpoint, slot.CheckpointProblem, slot.OverviewProblem));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Contains(overview.Nodes, node => node.Records > 0);
        Assert.Equal(full.Nodes.Select(node => (node.Id, node.Records)), overview.Nodes.Select(node => (node.Id, node.Records)));
        Assert.Equal(written + 1, overview.Generation);
        Assert.Equal(derived, Comparable(overview));
        Assert.Equal(full.Caveats, overview.Caveats);

        // A generation that adds segments names no checkpoint: it is derived, until its writer publishes one of its own.
        Publish(session.Store, [Transfer(900, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1_000)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 90_000 }]);
        SessionDerivationCache.Clear();
        SessionOverviewBundle added = SessionOverviewProjector.Project(session.Store);
        slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((false, false), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        SessionDerivationCache.Clear();
        Assert.Equal(Comparable(added), Comparable(SessionOverviewProjector.Project(session.Store)));
        slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((true, true), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
    }

    [Fact(DisplayName = "I14: a checkpoint that cannot be read is derived around, the overview says why, and publishing replaces it")]
    public void AnUnreadableCheckpointIsDerivedAround()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);
        string derived = Comparable(full);

        // Its generation records its digest, so only a checkpoint written wrong - by another format, say - reads this far.
        long next = session.Store.NextGeneration;
        using (StoreStagingFile staged = session.Store.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index))
        {
            staged.Content.Write("ICATDCKP not a checkpoint"u8);
            _ = staged.Complete();
            _ = session.Store.CommitIndex([staged], session.Store.Current!.Generation, Committed, next);
        }

        SessionDerivationCache.Clear();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        SessionDerivation slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((false, false), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
        Assert.Contains("not readable", slot.CheckpointProblem, StringComparison.Ordinal);
        Assert.Equal(derived, Comparable(overview));
        string caveat = Assert.Single(overview.Caveats.Except(full.Caveats));
        Assert.Equal(full.Caveats, overview.Caveats.Where(line => line != caveat));
        Assert.Contains(slot.CheckpointProblem!, caveat, StringComparison.Ordinal);
        Assert.Contains("same result", caveat, StringComparison.Ordinal);

        // Publishing replaces the unreadable checkpoint, and the next open uses the new one without a caveat.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        Assert.Equal(
            [DerivationCheckpoint.NamedBy(session.Store.Current!)!, SessionOverviewIndex.NamedBy(session.Store.Current!)!],
            session.Store.Current!.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index));
        SessionDerivationCache.Clear();
        SessionOverviewBundle replaced = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(full.Caveats, replaced.Caveats);
        Assert.Equal(derived, Comparable(replaced));
    }

    [Fact(DisplayName = "I14: nothing is published for a session with no generation or no records, and a stale writer is refused")]
    public void NothingToDeriveAndAStaleWriterPublishNothing()
    {
        SessionDerivationCache.Clear();
        using var empty = new TemporarySession();
        Assert.Equal(CheckpointOutcome.NothingToDerive, SessionCheckpoints.Publish(empty.Store, Committed).Outcome);

        using var session = new TemporarySession();
        Publish(session.Store, IncrementalOverviewTests.LiveChunks()[0]);

        // Another writer publishes after this one opened: the checkpoint would name a generation that is no longer current.
        SessionStore stale = SessionStore.Open(LocalOwnedDirectory.Open(session.Path), TestSessions.Session, "metric-tests");
        Publish(session.Store, IncrementalOverviewTests.LiveChunks()[1]);
        CheckpointPublication refused = SessionCheckpoints.Publish(stale, Committed);
        Assert.Equal(CheckpointOutcome.Refused, refused.Outcome);
        Assert.Null(refused.PublishedGeneration);
        Assert.NotNull(refused.Reason);
        Assert.Null(DerivationCheckpoint.NamedBy(session.Store.Current!));
    }

    [Fact(DisplayName = "I14: an overview that cannot be read is counted around from the segments, and the overview says why")]
    public void AnUnreadableOverviewIsCountedAround()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);
        PublishIndexes(session.Store, withCheckpoint: true, overview: "ICATOVRV not an overview"u8.ToArray());
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        SessionDerivation slot = SessionDerivationCache.For(reopened.Current!);

        // The processes and relationships still come from the checkpoint; only the timeline reads the segments.
        Assert.Equal((true, true, null), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint, slot.CheckpointProblem));
        Assert.Contains("not readable", slot.OverviewProblem, StringComparison.Ordinal);
        Assert.NotEqual(0, reopened.SegmentReaderCache.Entries);
        Assert.Equal(Comparable(full), Comparable(overview));
        string caveat = Assert.Single(overview.Caveats.Except(full.Caveats));
        Assert.Contains(slot.OverviewProblem!, caveat, StringComparison.Ordinal);
        Assert.Contains("same result", caveat, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I14: a checkpoint without an overview gives the derivations, and the timeline is counted from the segments")]
    public void ACheckpointWithoutAnOverviewCountsTheTimeline()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);

        // As revision 162 published: a checkpoint alone. Publishing again adds the overview.
        PublishIndexes(session.Store, withCheckpoint: true, overview: null);
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        SessionDerivation slot = SessionDerivationCache.For(reopened.Current!);
        Assert.Equal((true, true, null), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint, slot.OverviewProblem));
        Assert.Equal(Comparable(full), Comparable(overview));
        Assert.Equal(full.Caveats, overview.Caveats);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        Assert.NotNull(SessionOverviewIndex.NamedBy(session.Store.Current!));
    }

    [Fact(DisplayName = "I14: a checkpoint written before process counts gives its derivations, the counts come from the segments, and publishing replaces it")]
    public void AMinorZeroCheckpointIsCountedAroundAndReplaced()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionOverviewBundle full = SessionOverviewProjector.Project(session.Store);

        // As revisions 163 to 165 published: a checkpoint without the counts, beside a persisted overview.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        byte[] persisted = SessionSegments.ReadVerified(
            session.Store.Root, SessionOverviewIndex.NamedBy(session.Store.Current!)!, SessionOverviewIndex.MaximumBytes);
        PublishIndexes(session.Store, withCheckpoint: true, overview: persisted, minorZero: true);

        // The instances and relations come from the checkpoint, and the counts, which it does not hold, from every
        // segment: the same answer, without a caveat, since a checkpoint older than the counts is not damaged.
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        SessionDerivation slot = SessionDerivationCache.For(reopened.Current!);
        Assert.Equal((true, true, false, false, null),
            (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint, slot.ActivityFromCheckpoint, slot.ActivityExtended, slot.CheckpointProblem));
        Assert.NotEqual(0, reopened.SegmentReaderCache.Entries);
        Assert.Equal(Comparable(full), Comparable(overview));
        Assert.Equal(full.Caveats, overview.Caveats);

        // Publishing replaces it with one that holds the counts, and the next open reads no segment.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        SessionDerivationCache.Clear();
        reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        overview = SessionOverviewProjector.Project(reopened);
        slot = SessionDerivationCache.For(reopened.Current!);
        Assert.True(slot.ActivityFromCheckpoint);
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Equal(Comparable(full), Comparable(overview));
    }

    [Fact(DisplayName = "I15: publishing a checkpoint removes the manifests no pointer names, as every publication does")]
    public void PublishingACheckpointRemovesSupersededManifests()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[][] chunks = IncrementalOverviewTests.LiveChunks();
        for (int index = 0; index < 3; index++)
        {
            Publish(session.Store, chunks[index]);
        }

        Assert.Equal(["manifest-0000000002.json", "manifest-0000000003.json"], Manifests(session.Path));

        // Its lease ends before its commit, so the writer can remove what the new pointers no longer name (store-v1 §9).
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        Assert.Equal(["manifest-0000000003.json", "manifest-0000000004.json"], Manifests(session.Path));

        static string[] Manifests(string directory) =>
            [.. Directory.EnumerateFiles(directory, "manifest-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
    }

    [Fact(DisplayName = "I4: a persisted overview reads back as the counts it was written from, and is refused, never misread, when damaged")]
    public void APersistedOverviewReadsBackOrIsRefused()
    {
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        OverviewCounts counts = SessionOverviewProjector.Count(segments, CancellationToken.None);
        StoreDependency[] covered = SessionOverviewIndex.ObservationSegments(manifest);
        using var written = new MemoryStream();
        _ = SessionOverviewIndex.Write(written, manifest.SessionId, manifest.Generation, covered, counts);
        byte[] bytes = written.ToArray();

        (OverviewCounts read, IReadOnlyList<StoreDependency> readCovered) = SessionOverviewIndex.Read(bytes, manifest.SessionId);
        Assert.True(SessionOverviewIndex.Covers(readCovered, manifest));
        Assert.Equal((counts.Rows, counts.WithoutTime, counts.Extent), (read.Rows, read.WithoutTime, read.Extent));
        Assert.Equal(counts.Main!.Counts, read.Main!.Counts);
        Assert.Equal(counts.Main.Tallies(), read.Main.Tallies());
        Assert.Equal(counts.Minimap!.Interval, read.Minimap!.Interval);
        Assert.Equal(counts.Minimap.Counts, read.Minimap.Counts);

        for (int length = 0; length < bytes.Length; length++)
        {
            _ = Assert.Throws<InvalidDataException>(() => SessionOverviewIndex.Read(bytes.AsMemory(0, length), manifest.SessionId));
        }

        // A changed byte is refused or reads as some overview whose columns still add up; nothing else escapes.
        for (int index = 0; index < bytes.Length; index++)
        {
            byte[] changed = [.. bytes];
            changed[index] ^= 0xFF;
            try
            {
                _ = SessionOverviewIndex.Read(changed, manifest.SessionId);
            }
            catch (InvalidDataException)
            {
            }
        }

        Assert.Contains("belongs to session", Assert.Throws<InvalidDataException>(
            () => SessionOverviewIndex.Read(bytes, Guid.NewGuid())).Message, StringComparison.Ordinal);
        byte[] otherBounds = [.. bytes];
        otherBounds[36] = 65;
        Assert.Contains("columns this build does not draw", Assert.Throws<InvalidDataException>(
            () => SessionOverviewIndex.Read(otherBounds, manifest.SessionId)).Message, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidDataException>(() => SessionOverviewIndex.Read((byte[])[.. bytes, 0], manifest.SessionId));
    }

    [Fact(DisplayName = "I4: a persisted overview of a few records over a long extent reads back: its column widths bound nothing that follows")]
    public void ASparseSessionsOverviewReadsBack()
    {
        // Two records ten seconds apart: the minimap divides the extent into its full width of columns, and a column holds a
        // count only where a record fell, so the file ends a few bytes after it names how many columns there are. That
        // number is the width the extent divides into, not a count of fields to follow, and no bound on the bytes left
        // (a live content capture of 986 records found its 1,061 minimap columns refused against the 77 bytes left).
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(1_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1) with { SessionRelativeTicks = 1_000 },
            Transfer(100_001_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2) with { SessionRelativeTicks = 100_001_000 }]);
        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        OverviewCounts counts = SessionOverviewProjector.Count(segments, CancellationToken.None);
        Assert.True(counts.Minimap!.Counts.Count > 100);

        using var written = new MemoryStream();
        _ = SessionOverviewIndex.Write(written, manifest.SessionId, manifest.Generation, SessionOverviewIndex.ObservationSegments(manifest), counts);
        (OverviewCounts read, _) = SessionOverviewIndex.Read(written.ToArray(), manifest.SessionId);
        Assert.Equal(counts.Main!.Counts, read.Main!.Counts);
        Assert.Equal(counts.Minimap.Counts, read.Minimap!.Counts);
        Assert.Equal(2, read.Minimap.Counts.Sum());
    }

    [Fact(DisplayName = "I14: an RPC peers session keeps its links with its overview, and its first view draws them opening no segment")]
    public void AnRpcPeersSessionReopensWithItsLinks()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = RpcPeerEdgesTests.LinkedCalls();
        Publish(session.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        CommunicationEdge derived = Assert.Single(SessionOverviewProjector.Project(session.Store).Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);

        // The overview keeps the two links as one pair's total; a reopen draws the edge from it and opens no segment.
        (OverviewCounts kept, _) = SessionOverviewIndex.Read(
            SessionSegments.ReadVerified(session.Store.Root, SessionOverviewIndex.NamedBy(session.Store.Current!)!, SessionOverviewIndex.MaximumBytes),
            session.Store.Current!.SessionId);
        RpcPeerLinkTotal total = Assert.Single(kept.RpcLinks!);
        Assert.Equal((RelationStrength.Correlated, 8L), (total.Strength, total.Records));
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        CommunicationEdge drawn = Assert.Single(SessionOverviewProjector.Project(reopened).Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Equal(derived, drawn);

        // A capture that collected no ALPC keeps no links at all.
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields, coverage: RpcLedger(alpc: false));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(plain.Store, Committed).Outcome);
        (OverviewCounts none, _) = SessionOverviewIndex.Read(
            SessionSegments.ReadVerified(plain.Store.Root, SessionOverviewIndex.NamedBy(plain.Store.Current!)!, SessionOverviewIndex.MaximumBytes),
            plain.Store.Current!.SessionId);
        Assert.Null(none.RpcLinks);
    }

    [Fact(DisplayName = "I4: an overview's RPC links read back as written, a minor-0 overview keeps none, and damaged links are refused")]
    public void PersistedRpcLinksReadBackOrAreRefused()
    {
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        OverviewCounts counts = SessionOverviewProjector.Count(segments, CancellationToken.None);
        StoreDependency[] covered = SessionOverviewIndex.ObservationSegments(manifest);
        var first = new ProcessInstanceId(Guid.Parse("11111111-0000-4000-8000-000000000001"));
        var second = new ProcessInstanceId(Guid.Parse("22222222-0000-4000-8000-000000000002"));
        RpcPeerLinkTotal[] links =
        [
            new(first, second, RelationStrength.Correlated, 8),
            new(first, second, RelationStrength.Candidate, 3),
        ];
        byte[] Written(OverviewCounts written)
        {
            using var stream = new MemoryStream();
            _ = SessionOverviewIndex.Write(stream, manifest.SessionId, manifest.Generation, covered, written);
            return stream.ToArray();
        }

        byte[] bytes = Written(counts with { RpcLinks = links });
        Assert.Equal(links, SessionOverviewIndex.Read(bytes, manifest.SessionId).Counts.RpcLinks);

        // A minor-0 overview, as revisions 163 to 228 wrote it, ends with its minimap and keeps no links.
        byte[] minorZero = Written(counts)[..^1];
        minorZero[10] = 0;
        OverviewCounts earlier = SessionOverviewIndex.Read(minorZero, manifest.SessionId).Counts;
        Assert.Null(earlier.RpcLinks);
        Assert.Equal(counts.Main!.Tallies(), earlier.Main!.Tallies());

        // A link at a strength no link has, out of order, or joining an instance to itself is refused.
        foreach (RpcPeerLinkTotal[] damaged in new[]
        {
            new[] { new RpcPeerLinkTotal(first, second, RelationStrength.Direct, 8) },
            [links[1], links[0]],
            [new RpcPeerLinkTotal(second, first, RelationStrength.Correlated, 8)],
            [new RpcPeerLinkTotal(first, first, RelationStrength.Correlated, 8)],
        })
        {
            Assert.Contains("an RPC link", Assert.Throws<InvalidDataException>(
                () => SessionOverviewIndex.Read(Written(counts with { RpcLinks = damaged }), manifest.SessionId)).Message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Publishes, as the next generation, a checkpoint of the current one when asked and an overview holding
    /// <paramref name="overview"/> when given. The checkpoint is in the format revisions 162 to 165 wrote, without the
    /// process counts, when <paramref name="minorZero"/> asks for that.
    /// </summary>
    private static void PublishIndexes(SessionStore store, bool withCheckpoint, byte[]? overview, bool minorZero = false)
    {
        SessionManifestV1 manifest = store.Current!;
        long next = store.NextGeneration;
        var staged = new List<StoreStagingFile>();
        try
        {
            if (withCheckpoint)
            {
                SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
                SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
                ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, SessionSegments.SourceClock(store.Root, manifest)!.Value, fields);
                ProcessActivityIndex activity = ProcessActivityIndex.Derive(segments, processes);
                using var written = new MemoryStream();
                _ = DerivationCheckpoint.Write(written, manifest.SessionId, manifest.Generation, processes,
                    TransportRelationIndex.Derive(segments, processes), activity);
                StoreStagingFile checkpoint = store.Stage(DerivationCheckpoint.FileNameFor(next), StoreDependencyKind.Index);
                staged.Add(checkpoint);
                checkpoint.Content.Write(minorZero
                    ? EarlierCheckpoints.MinorZero(written.ToArray(), segments, processes, activity)
                    : written.ToArray());
                _ = checkpoint.Complete();
            }

            if (overview is not null)
            {
                StoreStagingFile file = store.Stage(SessionOverviewIndex.FileNameFor(next), StoreDependencyKind.Index);
                staged.Add(file);
                file.Content.Write(overview);
                _ = file.Complete();
            }

            _ = store.CommitIndex(staged, manifest.Generation, Committed, next);
        }
        finally
        {
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }
        }
    }

    /// <summary>
    /// What an overview shows, without the generation and identity that differ between two of the same records, and
    /// without its caveats, which each test compares itself.
    /// </summary>
    private static string Comparable(SessionOverviewBundle overview) =>
        JsonSerializer.Serialize(overview with { Generation = 0, GraphIdentity = string.Empty, Caveats = [] });
}
