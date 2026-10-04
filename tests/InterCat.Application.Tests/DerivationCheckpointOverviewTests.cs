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

        // A minor-0 overview, as revisions 163 to 228 wrote it, ends with its minimap and keeps no links: this build's ends
        // with a flag for links, another for lane bytes and another for process bytes after it.
        byte[] minorZero = Written(counts)[..^3];
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

    [Fact(DisplayName = "I14: a finished session keeps each overview column's bytes per mechanism, and a reopen's lanes under a byte ranking open no segment")]
    public void AFinishedSessionKeepsItsLaneBytes()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        // A send that declared a size it did not record, and one of nothing: each is counted, and neither summed as a size.
        Publish(session.Store,
        [
            Transfer(900, ObservationKind.Send, AccountingSide.SendSide, null, 100, 9_001)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 450 },
            Transfer(901, ObservationKind.Send, AccountingSide.SendSide, 0, 100, 9_002)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 460 },
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var extent = new TimeRange(overview.Timeline[0].Interval.StartTicks, overview.Timeline[^1].Interval.EndTicks);
        int columns = overview.Timeline.Count;
        Mechanism[] lanes = [.. overview.MechanismLanes.Select(lane => lane.Mechanism)];
        SessionMechanismByteMeasures read = SessionIntervalByteQuery.MeasureByMechanism(session.Store, extent, columns, lanes);
        Assert.Contains(read.Lanes.SelectMany(lane => lane.Columns), bytes => bytes.SentMeasured > 0 && bytes.ReceivedMeasured > 0);
        Assert.Contains(read.Lanes.SelectMany(lane => lane.Columns), bytes => bytes.SentUnmeasured > 0);
        Assert.False(read.FromPersistedOverview);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);

        // The overview keeps, cell by cell, what a read of every segment measures.
        (OverviewCounts kept, _) = SessionOverviewIndex.Read(
            SessionSegments.ReadVerified(session.Store.Root, SessionOverviewIndex.NamedBy(session.Store.Current!)!, SessionOverviewIndex.MaximumBytes),
            session.Store.Current!.SessionId);
        OverviewLaneBytes cells = Assert.IsType<OverviewLaneBytes>(kept.LaneBytes);
        for (int lane = 0; lane < lanes.Length; lane++)
        {
            for (int column = 0; column < columns; column++)
            {
                Assert.Equal(read.Lanes[lane].Columns[column], cells.Of(column, lanes[lane]));
            }
        }

        // A fresh viewer answers the overview's lanes from it and opens no segment; a zoomed view's columns are read.
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionMechanismByteMeasures answered = SessionIntervalByteQuery.MeasureByMechanism(reopened, extent, columns, lanes);
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.True(answered.FromPersistedOverview);
        Assert.Equal(read.Lanes.SelectMany(lane => lane.Columns), answered.Lanes.SelectMany(lane => lane.Columns));
        Assert.Equal(lanes, answered.Lanes.Select(lane => lane.Scope.Mechanism!.Value));
        var half = new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 2));
        SessionMechanismByteMeasures zoomed = SessionIntervalByteQuery.MeasureByMechanism(reopened, half, 8, lanes);
        Assert.True(reopened.SegmentReaderCache.Entries > 0);
        Assert.False(zoomed.FromPersistedOverview);
        Assert.Equal(SessionIntervalByteQuery.MeasureByMechanism(session.Store, half, 8, lanes).Lanes.SelectMany(lane => lane.Columns),
            zoomed.Lanes.SelectMany(lane => lane.Columns));

        // The whole session in other columns than the overview's is read too: the kept cells answer only their own.
        SessionMechanismByteMeasures coarser = SessionIntervalByteQuery.MeasureByMechanism(reopened, extent, columns / 2, lanes);
        Assert.False(coarser.FromPersistedOverview);
        Assert.Equal(SessionIntervalByteQuery.MeasureByMechanism(session.Store, extent, columns / 2, lanes).Lanes.SelectMany(lane => lane.Columns),
            coarser.Lanes.SelectMany(lane => lane.Columns));
    }

    [Fact(DisplayName = "I4: an overview's lane bytes read back as written, an earlier overview keeps none, and damaged ones are refused")]
    public void PersistedLaneBytesReadBackOrAreRefused()
    {
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in IncrementalOverviewTests.LiveChunks())
        {
            Publish(session.Store, chunk);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        OverviewCounts counts = SessionOverviewProjector.Count(segments, CancellationToken.None);
        OverviewLaneBytes lanes = SessionIntervalByteQuery.OverviewLanes(segments, counts.Main!, CancellationToken.None);
        Assert.True(lanes.Cells.Count >= 2);
        StoreDependency[] covered = SessionOverviewIndex.ObservationSegments(manifest);
        byte[] Written(OverviewCounts written)
        {
            using var stream = new MemoryStream();
            _ = SessionOverviewIndex.Write(stream, manifest.SessionId, manifest.Generation, covered, written);
            return stream.ToArray();
        }

        byte[] bytes = Written(counts with { LaneBytes = lanes });
        Assert.Equal(lanes.Cells, SessionOverviewIndex.Read(bytes, manifest.SessionId).Counts.LaneBytes!.Cells);

        // A minor-1 overview, as revisions 229 to 288 wrote it, ends with its links and keeps no lane bytes; this build's
        // keeps no process bytes after them, and says so in its last byte.
        byte[] minorOne = Written(counts)[..^2];
        minorOne[10] = 1;
        Assert.Null(SessionOverviewIndex.Read(minorOne, manifest.SessionId).Counts.LaneBytes);

        // A cell where the columns count no record of its mechanism, with a count below zero, or summing what no
        // contribution measured is refused; so are two cells out of order, which only damage can put them in.
        (int column, Mechanism mechanism, TransportBytes first) = lanes.Cells[0];
        Mechanism absent = Enum.GetValues<Mechanism>().First(candidate => counts.Main!.CountOf(column, candidate) == 0);
        const int Cell = 4 + 2 + (9 * 8);
        int cellsEnd = bytes.Length - 1;
        byte[] swapped = [.. bytes];
        bytes.AsSpan(cellsEnd - (2 * Cell), Cell).CopyTo(swapped.AsSpan(cellsEnd - Cell));
        bytes.AsSpan(cellsEnd - Cell, Cell).CopyTo(swapped.AsSpan(cellsEnd - (2 * Cell)));
        foreach (byte[] damaged in new[]
        {
            Written(counts with { LaneBytes = new OverviewLaneBytes([(column, absent, first)]) }),
            Written(counts with { LaneBytes = new OverviewLaneBytes([(column, mechanism, first with { SentUnmeasured = -1 })]) }),
            Written(counts with { LaneBytes = new OverviewLaneBytes([(column, mechanism, new TransportBytes(64, 0, 1, 0, 0, 0))]) }),
            swapped,
        })
        {
            Assert.Contains("a lane's bytes", Assert.Throws<InvalidDataException>(
                () => SessionOverviewIndex.Read(damaged, manifest.SessionId)).Message, StringComparison.Ordinal);
        }

        // Cut short anywhere it is refused, and a changed byte is refused or reads as some overview: nothing else escapes.
        for (int length = 0; length < bytes.Length; length++)
        {
            _ = Assert.Throws<InvalidDataException>(() => SessionOverviewIndex.Read(bytes.AsMemory(0, length), manifest.SessionId));
        }

        for (int index = bytes.Length - (lanes.Cells.Count * Cell) - 5; index < bytes.Length; index++)
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

        // Counts with no extent keep no cell: the writer refuses to write one there.
        _ = Assert.Throws<ArgumentException>(() => Written(new OverviewCounts(1, 1, null, null, null) { LaneBytes = lanes }));
    }

    [Fact(DisplayName = "I14: a finished session keeps each process's and channel end's bytes, and a reopen's whole-session byte ranking opens no segment and measures as a read does under every policy")]
    public void AFinishedSessionKeepsItsProcessBytes()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        Publish(session.Store, KeptBytesSession());
        EvidencePolicy[] policies = Enum.GetValues<EvidencePolicy>();
        Dictionary<EvidencePolicy, SessionByteMeasures> read = policies.ToDictionary(
            policy => policy, policy => SessionByteRanking.Measure(session.Store, null, policy));
        Assert.All(read.Values, measures => Assert.False(measures.FromPersistedOverview));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var extent = new TimeRange(overview.Timeline[0].Interval.StartTicks, overview.Timeline[^1].Interval.EndTicks);
        var half = new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 2));
        SessionByteMeasures halfRead = SessionByteRanking.Measure(session.Store, half);

        // The session holds what each policy tells apart: a candidate's bytes and channel, attributed only when candidates
        // are asked for; sends that recorded no size; and a send no policy attributes to a process.
        Assert.NotEqual(Sorted(read[EvidencePolicy.IncludeCorrelated].ByProcess), Sorted(read[EvidencePolicy.IncludeCandidates].ByProcess));
        Assert.NotEqual(Sorted(read[EvidencePolicy.IncludeCorrelated].ByChannelEnd), Sorted(read[EvidencePolicy.IncludeCandidates].ByChannelEnd));
        Assert.Contains(read[EvidencePolicy.IncludeCorrelated].ByProcess.Values, bytes => bytes.SentUnmeasured > 0);
        Assert.NotEqual(TransportBytes.None, read[EvidencePolicy.AllIncludingConflicting].Unattributed);
        SegmentReaderV1[] segments = [.. SessionSegments.Names(session.Store.Current!)
            .Select(name => SessionSegments.Open(session.Store, session.Store.Current!, name))];
        Assert.Contains(TransportRelationIndex.Derive(segments, ProcessInstanceIndex.Derive(segments,
            SessionSegments.SourceClock(session.Store.Root, session.Store.Current!)!.Value,
            [.. SessionSegments.FieldNames(session.Store.Current!).Select(name => SessionSegments.Open(session.Store, session.Store.Current!, name))]))
            .Relations, relation => relation.Mechanism == Mechanism.Udp);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);

        // The overview keeps them before any policy: a candidate's at its strength, and one end of a process's channel to
        // itself, where its records are counted.
        (OverviewCounts counts, _) = SessionOverviewIndex.Read(
            SessionSegments.ReadVerified(session.Store.Root, SessionOverviewIndex.NamedBy(session.Store.Current!)!, SessionOverviewIndex.MaximumBytes),
            session.Store.Current!.SessionId);
        OverviewProcessBytes kept = Assert.IsType<OverviewProcessBytes>(counts.ProcessBytes);
        Assert.Contains(kept.Processes, entry => entry.Strength == RelationStrength.Candidate);
        Assert.Contains(kept.Ends, entry => entry.Strength == RelationStrength.Candidate);
        Assert.Equal(read[EvidencePolicy.AllIncludingConflicting].ByChannelEnd.Count, kept.Ends.Count);

        // A fresh viewer answers the whole session under every policy from the overview, and opens no segment.
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        foreach (EvidencePolicy policy in policies)
        {
            SessionByteMeasures answered = SessionByteRanking.Measure(reopened, null, policy);
            Assert.True(answered.FromPersistedOverview, policy.ToString());
            Assert.Null(answered.Interval);
            Assert.Equal(Sorted(read[policy].ByProcess), Sorted(answered.ByProcess));
            Assert.Equal(Sorted(read[policy].ByChannelEnd), Sorted(answered.ByChannelEnd));
            Assert.Equal(read[policy].Unattributed, answered.Unattributed);
        }

        Assert.Equal(0, reopened.SegmentReaderCache.Entries);

        // An interval is read from the segments, as before.
        SessionByteMeasures scoped = SessionByteRanking.Measure(reopened, half);
        Assert.False(scoped.FromPersistedOverview);
        Assert.True(reopened.SegmentReaderCache.Entries > 0);
        Assert.Equal(Sorted(halfRead.ByProcess), Sorted(scoped.ByProcess));
        Assert.Equal(Sorted(halfRead.ByChannelEnd), Sorted(scoped.ByChannelEnd));
    }

    [Fact(DisplayName = "I14: process bytes that name an instance or a channel end the checkpoint does not hold are not used, and the segments are read")]
    public void ProcessBytesTheCheckpointDoesNotHoldAreRead()
    {
        var stranger = new ProcessInstanceId(Guid.Parse("0b5e5e5e-0000-4000-8000-000000000001"));
        var some = new TransportBytes(9, 1, 0, 0, 0, 0);
        foreach (OverviewProcessBytes foreign in new[]
        {
            new OverviewProcessBytes(TransportBytes.None, [(stranger, RelationStrength.Correlated, some)], []),
            new OverviewProcessBytes(TransportBytes.None, [], [("transport:none", stranger, RelationStrength.Correlated, some)]),
        })
        {
            SessionDerivationCache.Clear();
            using var session = new TemporarySession();
            Publish(session.Store, KeptBytesSession());
            SessionByteMeasures read = SessionByteRanking.Measure(session.Store, null);
            SessionManifestV1 manifest = session.Store.Current!;
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
            using var stream = new MemoryStream();
            _ = SessionOverviewIndex.Write(stream, manifest.SessionId, manifest.Generation, SessionOverviewIndex.ObservationSegments(manifest),
                SessionOverviewProjector.Count(segments, CancellationToken.None) with { ProcessBytes = foreign });
            PublishIndexes(session.Store, withCheckpoint: true, stream.ToArray());

            SessionDerivationCache.Clear();
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            SessionByteMeasures answered = SessionByteRanking.Measure(reopened, null);
            Assert.False(answered.FromPersistedOverview);
            Assert.Equal(Sorted(read.ByProcess), Sorted(answered.ByProcess));
            Assert.Equal(Sorted(read.ByChannelEnd), Sorted(answered.ByChannelEnd));
            Assert.Equal(read.Unattributed, answered.Unattributed);
        }
    }

    [Fact(DisplayName = "I4: an overview's process bytes read back as written, an earlier overview keeps none, and damaged ones are refused")]
    public void PersistedProcessBytesReadBackOrAreRefused()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        Publish(session.Store, KeptBytesSession());
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        SessionManifestV1 manifest = session.Store.Current!;
        (OverviewCounts counts, IReadOnlyList<StoreDependency> covered) = SessionOverviewIndex.Read(
            SessionSegments.ReadVerified(session.Store.Root, SessionOverviewIndex.NamedBy(manifest)!, SessionOverviewIndex.MaximumBytes),
            manifest.SessionId);
        OverviewProcessBytes kept = Assert.IsType<OverviewProcessBytes>(counts.ProcessBytes);
        Assert.True(kept.Processes.Count >= 2);
        Assert.True(kept.Ends.Count >= 2);
        byte[] Written(OverviewCounts written)
        {
            using var stream = new MemoryStream();
            _ = SessionOverviewIndex.Write(stream, manifest.SessionId, 1, [.. covered], written);
            return stream.ToArray();
        }

        byte[] bytes = Written(counts);
        OverviewProcessBytes again = SessionOverviewIndex.Read(bytes, manifest.SessionId).Counts.ProcessBytes!;
        Assert.Equal(kept.Unbound, again.Unbound);
        Assert.Equal(kept.Processes, again.Processes);
        Assert.Equal(kept.Ends, again.Ends);
        Assert.Equal(kept.EncodedLength, bytes.Length - Written(counts with { ProcessBytes = null }).Length + 1);

        // A minor-2 overview, as revision 289 wrote it, ends with its lane bytes and keeps no process bytes.
        byte[] minorTwo = Written(counts with { ProcessBytes = null })[..^1];
        minorTwo[10] = 2;
        OverviewCounts earlier = SessionOverviewIndex.Read(minorTwo, manifest.SessionId).Counts;
        Assert.Null(earlier.ProcessBytes);
        Assert.Equal(counts.LaneBytes!.Cells, earlier.LaneBytes!.Cells);

        // An entry naming no instance, channel or holder, at a strength no policy admits, below zero, summing what no
        // contribution measured, or holding none, is refused; so is one held twice, and the bytes of records bound to no
        // instance contradicting themselves.
        (ProcessInstanceId instance, RelationStrength strength, TransportBytes measured) = kept.Processes[0];
        (string channel, ProcessInstanceId holder, RelationStrength endStrength, TransportBytes endBytes) = kept.Ends[0];
        var unmeasuredSum = new TransportBytes(64, 0, 1, 0, 0, 0);
        foreach ((OverviewProcessBytes Damaged, string Says) in new (OverviewProcessBytes, string)[]
        {
            (new(kept.Unbound, [(default, strength, measured)], []), "a process's bytes"),
            (new(kept.Unbound, [(instance, RelationStrength.Unresolved, measured)], []), "a process's bytes"),
            (new(kept.Unbound, [(instance, strength, measured with { SentBytes = -1 })], []), "a process's bytes"),
            (new(kept.Unbound, [(instance, strength, unmeasuredSum)], []), "a process's bytes"),
            (new(kept.Unbound, [(instance, strength, TransportBytes.None)], []), "a process's bytes"),
            (new(kept.Unbound, [(instance, strength, measured), (instance, strength, measured)], []), "a process's bytes"),
            (new(kept.Unbound, [], [("", holder, endStrength, endBytes)]), "a channel end's bytes"),
            (new(kept.Unbound, [], [(channel, default, endStrength, endBytes)]), "a channel end's bytes"),
            (new(kept.Unbound, [], [(channel, holder, RelationStrength.Unresolved, endBytes)]), "a channel end's bytes"),
            (new(kept.Unbound, [], [(channel, holder, endStrength, unmeasuredSum)]), "a channel end's bytes"),
            (new(kept.Unbound, [], [(channel, holder, endStrength, endBytes), (channel, holder, endStrength, endBytes)]), "a channel end's bytes"),
            (new(unmeasuredSum, [], []), "bound to no instance"),
        })
        {
            Assert.Contains(Says, Assert.Throws<InvalidDataException>(
                () => SessionOverviewIndex.Read(Written(counts with { ProcessBytes = Damaged }), manifest.SessionId)).Message,
                StringComparison.Ordinal);
        }

        // Two entries out of order, which only damage can put them in, are refused.
        const int Entry = 16 + 1 + (9 * 8);
        long endsLength = kept.Ends.Sum(end => 1L + end.Channel.Length + 16 + 1 + (9 * 8));
        int processesStart = bytes.Length - (int)endsLength - 4 - (kept.Processes.Count * Entry);
        byte[] swapped = [.. bytes];
        bytes.AsSpan(processesStart, Entry).CopyTo(swapped.AsSpan(processesStart + Entry));
        bytes.AsSpan(processesStart + Entry, Entry).CopyTo(swapped.AsSpan(processesStart));
        Assert.Contains("a process's bytes", Assert.Throws<InvalidDataException>(
            () => SessionOverviewIndex.Read(swapped, manifest.SessionId)).Message, StringComparison.Ordinal);

        // Cut short anywhere it is refused, and a changed byte is refused or reads as some overview: nothing else escapes.
        for (int length = 0; length < bytes.Length; length++)
        {
            _ = Assert.Throws<InvalidDataException>(() => SessionOverviewIndex.Read(bytes.AsMemory(0, length), manifest.SessionId));
        }

        for (int index = processesStart - 80; index < bytes.Length; index++)
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
    }

    /// <summary>
    /// A session whose bytes each evidence policy attributes differently: a client sending to a server whose PID was used
    /// before, so the server's records are a candidate's and so is their channel; two processes in a conversation, which
    /// no lifecycle record names, and a datagram flow between them, which no channel end the table ranks holds; a process
    /// connected to itself; sends that recorded no size, or a size of nothing; and a send made before its process was
    /// created, which binds to no instance.
    /// </summary>
    private static ObservationRowV1[] KeptBytesSession() =>
    [
        Lifecycle(10, ObservationKind.Create, 200, 1),
        Lifecycle(20, ObservationKind.Exit, 200, 2),
        Lifecycle(30, ObservationKind.Create, 200, 3),
        Timed(Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(42, ObservationKind.Send, AccountingSide.SendSide, null, 100, 6).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(43, ObservationKind.Send, AccountingSide.SendSide, 0, 200, 7).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 8).Between("127.0.0.1:51000", "127.0.0.1:9000")),
        Timed(Transfer(51, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 400, 9).Between("127.0.0.1:9000", "127.0.0.1:51000")),
        Timed(Transfer(52, ObservationKind.Send, AccountingSide.SendSide, 30, 400, 10).Between("127.0.0.1:9000", "127.0.0.1:51000")),
        Timed(Transfer(53, ObservationKind.Receive, AccountingSide.ReceiveSide, 30, 300, 11).Between("127.0.0.1:51000", "127.0.0.1:9000")),
        Timed(Transfer(54, ObservationKind.Send, AccountingSide.SendSide, 32, 300, 16).Between("127.0.0.1:5353", "127.0.0.1:5354")
            with { Mechanism = Mechanism.Udp }),
        Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 400, 17).Between("127.0.0.1:5353", "127.0.0.1:5354")
            with { Mechanism = Mechanism.Udp }),
        Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 5, 500, 12).Between("127.0.0.1:52000", "127.0.0.1:52001")),
        Timed(Transfer(61, ObservationKind.Receive, AccountingSide.ReceiveSide, 5, 500, 13).Between("127.0.0.1:52001", "127.0.0.1:52000")),
        Timed(Transfer(70, ObservationKind.Send, AccountingSide.SendSide, 9, 600, 14).Between("127.0.0.1:53000", "10.0.0.9:443")),
        Lifecycle(80, ObservationKind.Create, 600, 15),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>A measure's entries by key, in one order, so two measures compare entry for entry.</summary>
    private static (string Key, TransportBytes Bytes)[] Sorted<TKey>(IReadOnlyDictionary<TKey, TransportBytes> measured)
        where TKey : notnull =>
        [.. measured.Select(entry => (entry.Key.ToString()!, entry.Value)).OrderBy(entry => entry.Item1, StringComparer.Ordinal)];

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
        JsonSerializer.Serialize(overview with { Generation = 0, ManifestDigest = null, GraphIdentity = string.Empty, Caveats = [] });
}
