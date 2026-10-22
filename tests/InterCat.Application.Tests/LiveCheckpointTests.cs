using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §12.1 S1 for a session still being recorded (derivation-checkpoint-v1 §2): each generation a live follow publishes names a
/// checkpoint of the generation it extends, so a viewer that opens the session extends it over the newest chunk rather than
/// deriving from every record - wherever a derivation kept in memory would extend, and to the same answer.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class LiveCheckpointTests
{
    [Fact(DisplayName = "S1: each live publication names a checkpoint of the generation it extends, and a viewer opening the session extends it wherever a derivation in memory extends, projecting as one of every record")]
    public void AViewerExtendsTheCheckpointOfTheGenerationBefore()
    {
        // The window's way: a derivation kept in memory, extended generation by generation, through a compaction too.
        SessionDerivationCache.Clear();
        using var twin = new TemporarySession();
        var expected = new List<((bool, bool, bool) Extended, string Overview)>();
        foreach (Step step in Steps())
        {
            step(twin.Store, null);
            _ = SessionOverviewProjector.Project(twin.Store);
            SessionDerivation memory = SessionDerivationCache.For(twin.Store.Current!);
            expected.Add(((memory.ProcessesExtended, memory.RelationsExtended, memory.ActivityExtended),
                Comparable(SessionOverviewProjector.Project(twin.Store))));
        }

        // The session a live follow publishes, opened afresh after each publication, with nothing in memory.
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        int index = 0;
        foreach (Step step in Steps())
        {
            SessionManifestV1? before = session.Store.Current;
            step(session.Store, LiveCheckpoints.Instance);
            SessionManifestV1 published = session.Store.Current!;
            StoreDependency? named = DerivationCheckpoint.NamedBy(published);
            bool compaction = published.Retention is not null;
            if (before is null || compaction)
            {
                Assert.Null(named);
            }
            else
            {
                // Named for the generation that publishes it, and of every segment the generation before it named.
                Assert.Equal(DerivationCheckpoint.FileNameFor(published.Generation), named!.Name);
                DerivationCheckpoint checkpoint = DerivationCheckpoint.Read(
                    SessionSegments.ReadVerified(session.Store.Root, named, DerivationCheckpoint.MaximumBytes),
                    published.SessionId,
                    SessionSegments.SourceClock(session.Store.Root, published)!.Value);
                Assert.Equal(before.Generation, checkpoint.DerivedGeneration);
                Assert.True(checkpoint.Covers(SessionOverviewIndex.ObservationSegments(before), SessionOverviewIndex.FieldSegments(before)));
                Assert.False(checkpoint.Covers(SessionOverviewIndex.ObservationSegments(published), SessionOverviewIndex.FieldSegments(published)));
            }

            SessionDerivationCache.Clear();
            SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            string overview = Comparable(SessionOverviewProjector.Project(viewer));
            SessionDerivation slot = SessionDerivationCache.For(viewer.Current!);
            Assert.Null(slot.CheckpointProblem);
            Assert.Equal(named is null ? (false, false, false) : expected[index].Extended,
                (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint, slot.ActivityFromCheckpoint));
            Assert.Equal(expected[index].Overview, overview);
            viewer.ReleaseSegmentReaders();
            index++;
        }

        // Every live generation after the first extended, as a derivation in memory does, but where a late record or the
        // compaction made it derive in full.
        Assert.Contains(expected, step => step.Extended == (true, true, true));
        Assert.Contains(expected.Skip(1), step => step.Extended != (true, true, true));
    }

    [Fact(DisplayName = "S1: nothing is staged for a session with no generation, and a live checkpoint that is not one is set aside, the answer the same")]
    public void ALiveCheckpointThatIsNotOneIsSetAside()
    {
        SessionDerivationCache.Clear();
        using var empty = new TemporarySession();
        Assert.Empty(LiveCheckpoints.Instance.Stage(empty.Store, 1, CancellationToken.None));

        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        ObservationRowV1[][] chunks = IncrementalOverviewTests.LiveChunks();
        for (int index = 0; index < 3; index++)
        {
            Publish(session.Store, chunks[index], indexer: index < 2 ? LiveCheckpoints.Instance : new NotACheckpoint());
            Publish(twin.Store, chunks[index]);
        }

        SessionDerivationCache.Clear();
        string expected = Comparable(SessionOverviewProjector.Project(twin.Store));
        SessionDerivationCache.Clear();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        SessionDerivation slot = SessionDerivationCache.For(session.Store.Current!);
        Assert.Equal((false, false), (slot.ProcessesFromCheckpoint, slot.RelationsFromCheckpoint));
        Assert.Contains("not readable", slot.CheckpointProblem, StringComparison.Ordinal);
        Assert.Equal(expected, Comparable(overview));
        Assert.Contains(overview.Caveats, caveat => caveat.Contains(slot.CheckpointProblem!, StringComparison.Ordinal));
    }

    /// <summary>A step of a live recording into a store, with the indexer its follow publishes through.</summary>
    private delegate void Step(SessionStore store, ILiveIndexer? indexer);

    /// <summary>The live chunks a capture publishes, one generation each, with a compaction of the small ones among them.</summary>
    private static IEnumerable<Step> Steps()
    {
        ObservationRowV1[][] chunks = IncrementalOverviewTests.LiveChunks();
        for (int index = 0; index < chunks.Length; index++)
        {
            ObservationRowV1[] chunk = chunks[index];
            yield return (store, indexer) => Publish(store, chunk, indexer: indexer);
            if (index == 5)
            {
                yield return (store, _) => Assert.NotNull(SegmentCompaction.Compact(store, Committed));
            }
        }
    }

    private static string Comparable(SessionOverviewBundle overview) => JsonSerializer.Serialize(overview with
    {
        Generation = 0, ManifestDigest = null, GraphIdentity = string.Empty, Caveats = [], Size = null,
    });

    /// <summary>An indexer that stages, as the checkpoint of the generation before, bytes that are not one.</summary>
    private sealed class NotACheckpoint : ILiveIndexer
    {
        public IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken)
        {
            StoreStagingFile staged = store.Stage(DerivationCheckpoint.FileNameFor(generation), StoreDependencyKind.Index);
            staged.Content.Write("ICATDCKP not a checkpoint"u8);
            _ = staged.Complete();
            return [staged];
        }
    }
}
