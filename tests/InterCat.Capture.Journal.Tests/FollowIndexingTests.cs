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
