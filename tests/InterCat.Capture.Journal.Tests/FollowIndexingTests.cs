using System.Text;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// derivation-checkpoint-v1 §2: a follow given an indexer stages, into each generation it mirrors, the indexes of the
/// generation that one extends, so a viewer opening the session while it is followed reads only the newest chunk; the
/// compaction that ends a follow keeps none, and the checkpoint published after it describes everything.
/// </summary>
public sealed class FollowIndexingTests
{
    [Fact(DisplayName = "§20.1: a follow stages into each generation it mirrors the indexes of the one before, and its compaction keeps none")]
    public async Task AFollowIndexesEachGenerationItExtends()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        SessionStore evidence = await FollowContentTests.RecordContentEvidence(evidenceDirectory.Path, [["one"], ["two"], ["three"]]);
        SessionManifestV1 source = evidence.Current!;
        int chunks = source.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal);
        Assert.True(chunks >= 3, $"The recording published {chunks} chunks; three bursts publish three.");

        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
        var indexer = new GenerationIndexer();
        LiveSessionFollower follower = LiveSessionFollower.Open(evidence, derived, indexer: indexer);
        for (int mirrored = 1; mirrored <= chunks; mirrored++)
        {
            FollowStep step = follower.CatchUp(maximumChunks: 1);
            Assert.Equal(1, step.MirroredChunks);
            SessionManifestV1 current = derived.Current!;
            StoreDependency[] indexes = [.. current.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index)];
            if (step.Compactions > 0)
            {
                // The last chunk ends the follow with a compaction, which replaces the segments any index described.
                Assert.True(step.Finished);
                Assert.Empty(indexes);
                continue;
            }

            if (mirrored == 1)
            {
                Assert.Empty(indexes);
                continue;
            }

            StoreDependency index = Assert.Single(indexes);
            Assert.Equal($"index-{current.Generation:D10}.idx", index.Name);
            using FileStream stream = derived.Root.OpenOwnedFile(index.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal($"of generation {current.Generation - 1}", reader.ReadToEnd());
        }

        // Asked once for each chunk, with the generation the chunk's own extends current then.
        Assert.Equal(chunks, indexer.Asked.Count);
        Assert.Equal((1L, (long?)null), indexer.Asked[0]);
        Assert.All(indexer.Asked.Skip(1), asked => Assert.Equal(asked.Generation - 1, asked.Current));
    }

    [Fact(DisplayName = "§20.1: a follow compacts its small publications as it goes, each compaction keeping no index and the chunk after it indexing it")]
    public async Task AFollowCompactsAsItGoes()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        using var derivedDirectory = new TemporaryDirectory();
        _ = await EvidenceRecordings.RecordEvidence(evidenceDirectory.Path, ordinals: [.. Enumerable.Range(1, 12)], bursts: [2, 4, 6, 8, 10]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        SessionManifestV1 source = evidence.Current!;
        int chunks = LiveSessionFollower.Progress(source).Chunks;
        Assert.True(chunks >= 6, $"The recording published {chunks} chunks; six bursts publish six.");

        // A long capture's follow coalesces its small publications every so many, as §20.1's live compaction does; here
        // every third, so some chunks' generations stand between compactions.
        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(derivedDirectory.Path), source.SessionId, source.SourceIdentity);
        var indexer = new GenerationIndexer();
        LiveSessionFollower follower = LiveSessionFollower.Open(
            evidence, derived, compaction: new CompactionOptions { SmallUnitsBeforeCompaction = 3 }, indexer: indexer);
        var compactions = new List<long>();
        FollowStep step = null!;
        for (int mirrored = 1; mirrored <= chunks; mirrored++)
        {
            long before = derived.Current?.Generation ?? 0;
            step = follower.CatchUp(maximumChunks: 1);
            Assert.Equal((1, mirrored), (step.MirroredChunks, step.DerivedChunks));
            SessionManifestV1 current = derived.Current!;
            StoreDependency[] indexes = [.. current.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index)];
            if (step.Compactions > 0)
            {
                // The chunk's own generation, then each compaction's, which replaces the segments any index described.
                Assert.Equal(before + 1 + step.Compactions, current.Generation);
                Assert.Empty(indexes);
                if (!step.Finished)
                {
                    compactions.Add(current.Generation);
                }

                continue;
            }

            Assert.Equal(before + 1, current.Generation);
            if (mirrored > 1)
            {
                Assert.Equal($"index-{current.Generation:D10}.idx", Assert.Single(indexes).Name);
            }
        }

        // Compactions came as the follow went, not only at its end, and the chunk after each indexed it.
        Assert.True(step.Finished);
        Assert.True(compactions.Count >= 2, $"The follow compacted {compactions.Count} times before its end; {chunks} chunks do twice.");
        Assert.All(compactions, compaction => Assert.Contains((compaction + 1, (long?)compaction), indexer.Asked));
        Assert.Equal(chunks, indexer.Asked.Count);
        Assert.All(indexer.Asked.Skip(1), asked => Assert.Equal(asked.Generation - 1, asked.Current));

        // Every record is derived once, in order, and the session replays from its journal as it was published.
        Assert.Equal(12L, step.DerivedRecords);
        Assert.Equal(12, JournalRederivation.Verify(derived).ObservationRows);
    }

    /// <summary>Stages one index of the store's current generation, saying which generation it describes.</summary>
    private sealed class GenerationIndexer : ILiveIndexer
    {
        public List<(long Generation, long? Current)> Asked { get; } = [];

        public IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken)
        {
            Asked.Add((generation, store.Current?.Generation));
            if (store.Current is not { } current)
            {
                return [];
            }

            StoreStagingFile file = store.Stage($"index-{generation:D10}.idx", StoreDependencyKind.Index);
            file.Content.Write(Encoding.UTF8.GetBytes($"of generation {current.Generation}"));
            _ = file.Complete();
            return [file];
        }
    }
}
