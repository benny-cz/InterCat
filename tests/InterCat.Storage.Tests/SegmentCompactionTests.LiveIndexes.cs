using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// derivation-checkpoint-v1 §2: a live follow stages, into each generation it publishes, the indexes of the generation that
/// one extends. Each generation names its own and carries none of another's, so a damaged one costs at most a rollback to
/// the generation before it; a compaction releases them with the segments they describe; and only a generation that adds a
/// journal chunk takes them.
/// </summary>
public sealed partial class SegmentCompactionTests
{
    [Fact(DisplayName = "§20.1: a live chunk's generation names the indexes staged of the one it extends, each its own, and a compaction keeps none")]
    public void ALiveChunkNamesTheIndexesOfTheGenerationItExtends()
    {
        using var session = new TemporarySession();
        var indexer = new RecordingIndexer();

        // The first chunk extends nothing, so nothing is staged of it.
        PublishChunk(session.Store, firstOrdinal: 1, records: 4, indexer: indexer);
        Assert.Equal([(1L, (long?)null)], indexer.Asked);
        Assert.Empty(Indexes(session.Store.Current!));

        for (int chunk = 1; chunk < 4; chunk++)
        {
            PublishChunk(session.Store, firstOrdinal: 1 + ((ulong)chunk * 4), records: 4, indexer: indexer);
            SessionManifestV1 published = session.Store.Current!;
            StoreDependency index = Assert.Single(Indexes(published));
            Assert.Equal(IndexName(published.Generation), index.Name);
            Assert.Equal($"of generation {published.Generation - 1}", Encoding.UTF8.GetString(Read(session.Store, index)));
        }

        Assert.Equal([(1L, (long?)null), (2L, 1L), (3L, 2L), (4L, 3L)], indexer.Asked);

        // A compaction replaces the segments every index described, so it names none; the next chunk indexes it.
        Assert.NotNull(SegmentCompaction.Compact(session.Store, Committed));
        Assert.Empty(Indexes(session.Store.Current!));
        PublishChunk(session.Store, firstOrdinal: 17, records: 4, indexer: indexer);
        Assert.Equal($"of generation 5", Encoding.UTF8.GetString(Read(session.Store, Assert.Single(Indexes(session.Store.Current!)))));

        // A chunk published without an indexer names none, and carries none of the generation before it.
        PublishChunk(session.Store, firstOrdinal: 21, records: 4);
        Assert.Empty(Indexes(session.Store.Current!));
    }

    [Fact(DisplayName = "§20.1: a superseded generation goes with the indexes only it named, and a lease on it keeps it whole until a later publication")]
    public void ASupersededGenerationsIndexesGoWithIt()
    {
        using var session = new TemporarySession();
        var indexer = new RecordingIndexer();
        for (int chunk = 0; chunk < 6; chunk++)
        {
            PublishChunk(session.Store, firstOrdinal: 1 + ((ulong)chunk * 4), records: 4, indexer: indexer);
        }

        // The current generation and the last-known-good keep theirs; every earlier generation went whole.
        Assert.Equal([IndexName(5), IndexName(6)], Files(session.Path, "index-*"));
        Assert.Equal(["manifest-0000000005.json", "manifest-0000000006.json"], Files(session.Path, "manifest-*"));

        // While a reader holds a lease, every superseded generation is kept, manifest and index alike, for a later
        // publication to remove once none is held.
        using (EvidenceLease lease = session.Store.AcquireLease())
        {
            Assert.Equal(6, lease.Manifest.Generation);
            PublishChunk(session.Store, firstOrdinal: 25, records: 4, indexer: indexer);
            PublishChunk(session.Store, firstOrdinal: 29, records: 4, indexer: indexer);
            Assert.Equal([IndexName(5), IndexName(6), IndexName(7), IndexName(8)], Files(session.Path, "index-*"));
            Assert.Equal(4, Files(session.Path, "manifest-*").Length);
        }

        PublishChunk(session.Store, firstOrdinal: 33, records: 4, indexer: indexer);
        Assert.Equal([IndexName(8), IndexName(9)], Files(session.Path, "index-*"));
        Assert.Equal(["manifest-0000000008.json", "manifest-0000000009.json"], Files(session.Path, "manifest-*"));

        // An index the current generation names is kept whatever generation published it.
        StoreDependency carried = PublishIndex(session.Store, "an index of generation 9"u8);
        PublishChunk(session.Store, firstOrdinal: 37, records: 4);
        Assert.Contains(carried.Name, Files(session.Path, "index-*"));

        // A journal release carries the indexes of the generation it releases from, so the generation that published one
        // goes without it while the release still names it.
        PublishChunk(session.Store, firstOrdinal: 41, records: 4, indexer: indexer);
        long published = session.Store.Current!.Generation;
        string oldest = session.Store.Current!.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .Select(dependency => dependency.Name).Order(StringComparer.Ordinal).First();
        _ = session.Store.ReleaseJournalChunks([oldest], 4, "the oldest chunk", Committed, Committed);
        Assert.Equal([IndexName(published)], Indexes(session.Store.Current!).Select(index => index.Name));
        PublishChunk(session.Store, firstOrdinal: 45, records: 4, indexer: indexer);
        Assert.DoesNotContain($"manifest-{published:D10}.json", Files(session.Path, "manifest-*"));
        Assert.Contains(IndexName(published), Files(session.Path, "index-*"));
    }

    private static string[] Files(string directory, string pattern) =>
        [.. Directory.EnumerateFiles(directory, pattern).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact(DisplayName = "§20.1: a generation takes the indexes of the one it extends once, completed and indexes only, and only when it adds a journal chunk")]
    public void AGenerationRefusesIndexesItCannotTake()
    {
        using var session = new TemporarySession();
        PublishChunk(session.Store, firstOrdinal: 1, records: 4);
        PublishChunk(session.Store, firstOrdinal: 5, records: 4);
        SessionManifestV1 current = session.Store.Current!;

        using (DerivedGenerationBuilder twice = DerivedGenerationBuilder.Begin(session.Store, Identity, TestClock, Committed))
        {
            twice.StageIndexesOf(new RecordingIndexer());
            Assert.Throws<InvalidOperationException>(() => twice.StageIndexesOf(new RecordingIndexer()));
        }

        foreach (Func<SessionStore, long, StoreStagingFile> wrong in new Func<SessionStore, long, StoreStagingFile>[]
        {
            // Not an index.
            (store, generation) => Completed(store.Stage($"plan-{generation:D10}.bin", StoreDependencyKind.DerivationPlan)),

            // An index not yet completed.
            (store, generation) => store.Stage(IndexName(generation), StoreDependencyKind.Index),
        })
        {
            using DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(session.Store, Identity, TestClock, Committed);
            Assert.Throws<InvalidOperationException>(() => builder.StageIndexesOf(new RecordingIndexer(wrong)));
        }

        // What a compaction or a re-derivation publishes replaces the segments such indexes describe.
        using (DerivedGenerationBuilder compaction = DerivedGenerationBuilder.BeginCompaction(session.Store, Identity, current.Generation))
        {
            Assert.Throws<InvalidOperationException>(() => compaction.StageIndexesOf(new RecordingIndexer()));
        }

        using (DerivedGenerationBuilder rederivation = DerivedGenerationBuilder.BeginFromJournal(
            session.Store, Identity, TestClock, current.Boundary, current.Generation))
        {
            Assert.Throws<InvalidOperationException>(() => rederivation.StageIndexesOf(new RecordingIndexer()));
        }

        Assert.Equal(current.Generation, session.Store.Current!.Generation);
    }

    private static StoreDependency[] Indexes(SessionManifestV1 manifest) =>
        [.. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index)];

    private static byte[] Read(SessionStore store, StoreDependency dependency)
    {
        using FileStream stream = store.Root.OpenOwnedFile(
            dependency.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static StoreStagingFile Completed(StoreStagingFile file)
    {
        file.Content.Write("staged"u8);
        _ = file.Complete();
        return file;
    }

    /// <summary>
    /// An indexer that stages one index of the store's current generation, saying which generation it describes, and
    /// records each generation it was asked for with the one current then; or what <paramref name="stage"/> stages.
    /// </summary>
    private sealed class RecordingIndexer(Func<SessionStore, long, StoreStagingFile>? stage = null) : ILiveIndexer
    {
        public List<(long Generation, long? Current)> Asked { get; } = [];

        public IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken)
        {
            Asked.Add((generation, store.Current?.Generation));
            if (stage is not null)
            {
                return [stage(store, generation)];
            }

            if (store.Current is not { } current)
            {
                return [];
            }

            StoreStagingFile file = store.Stage(IndexName(generation), StoreDependencyKind.Index);
            file.Content.Write(Encoding.UTF8.GetBytes($"of generation {current.Generation}"));
            _ = file.Complete();
            return [file];
        }
    }
}
