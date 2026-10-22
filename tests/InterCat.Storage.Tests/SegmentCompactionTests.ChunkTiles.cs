using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// tile-index-v1 §6, store-v1 §5: a chunk's tiles describe the observation segments one generation published, so unlike
/// every other index they are carried by each later generation that names those segments, released only with them, and
/// dropped by a commit that finds them damaged rather than failing it.
/// </summary>
public sealed partial class SegmentCompactionTests
{
    [Fact(DisplayName = "S4: a chunk's tiles are named for the generation whose observation segments they describe and the one that published them")]
    public void AChunksTilesAreNamedForItsSegments()
    {
        Assert.Equal("chunk-tiles-0000000012-0000000013.bin", ChunkTileFiles.FileNameFor(12, 13));
        Assert.Equal(12, ChunkTileFiles.SegmentsGenerationOf(ChunkTileFiles.FileNameFor(12, 13)));
        foreach (string other in new[]
        {
            "tiles-0000000013.bin", "chunk-tiles-0000000012-0000000013.idx", "chunk-tiles-000000012-0000000013.bin",
            "chunk-tiles-0000000012_0000000013.bin", "chunk-tiles-0000000000-0000000013.bin",
            "chunk-tiles-0000000012-0000000000.bin", "chunk-tiles-00000000x2-0000000013.bin",
        })
        {
            Assert.Null(ChunkTileFiles.SegmentsGenerationOf(other));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkTileFiles.FileNameFor(0, 13));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkTileFiles.FileNameFor(12, 10_000_000_000));

        string tiles = ChunkTileFiles.FileNameFor(12, 13);
        Assert.True(ChunkTileFiles.Is(Dependency(tiles, StoreDependencyKind.Index)));
        Assert.False(ChunkTileFiles.Is(Dependency(tiles, StoreDependencyKind.Segment)));
        Assert.False(ChunkTileFiles.Is(Dependency("tiles-0000000013.bin", StoreDependencyKind.Index)));
        Assert.True(ChunkTileFiles.Describes(tiles, Dependency("seg-0000000012-0003.icats", StoreDependencyKind.Segment)));
        Assert.False(ChunkTileFiles.Describes(tiles, Dependency("seg-0000000013-0000.icats", StoreDependencyKind.Segment)));
        Assert.False(ChunkTileFiles.Describes(tiles, Dependency("fld-0000000012-0000.icats", StoreDependencyKind.Segment)));
        Assert.False(ChunkTileFiles.Describes(tiles, Dependency("seg-0000000012-0000.icats", StoreDependencyKind.Index)));
        Assert.False(ChunkTileFiles.Describes("tiles-0000000013.bin", Dependency("seg-0000000012-0000.icats", StoreDependencyKind.Segment)));
    }

    [Fact(DisplayName = "S4: a chunk's tiles are carried by every later generation naming its segments, though no other index is, and an index publication names none")]
    public void AChunksTilesAreCarriedWithItsSegments()
    {
        using var session = new TemporarySession();
        var indexer = new ChunkIndexer();
        for (int chunk = 0; chunk < 4; chunk++)
        {
            PublishChunk(session.Store, firstOrdinal: 1 + ((ulong)chunk * 4), records: 4, indexer: indexer);
        }

        // Each generation names the tiles of every chunk before it, the ones it carried and the one it staged, and only its
        // own other index.
        SessionManifestV1 fourth = session.Store.Current!;
        Assert.Equal(4, fourth.Generation);
        Assert.Equal([Tiles(1, 2), Tiles(2, 3), Tiles(3, 4)], ChunksTiles(fourth));
        Assert.Equal([IndexName(4)], OtherIndexes(fourth));
        Assert.Equal("of chunk 1", Encoding.UTF8.GetString(Read(session.Store, Named(fourth, Tiles(1, 2)))));

        // A chunk published without an indexer stages none, and still carries the chunks' tiles, though not the index.
        PublishChunk(session.Store, firstOrdinal: 17, records: 4);
        Assert.Equal([Tiles(1, 2), Tiles(2, 3), Tiles(3, 4)], ChunksTiles(session.Store.Current!));
        Assert.Empty(OtherIndexes(session.Store.Current!));

        // Tiles named for a generation whose segments no generation names describe nothing, and are not carried.
        PublishChunk(session.Store, firstOrdinal: 21, records: 4, indexer: new StrayIndexer());
        Assert.Contains(Tiles(99, 6), ChunksTiles(session.Store.Current!));
        PublishChunk(session.Store, firstOrdinal: 25, records: 4);
        Assert.Equal([Tiles(1, 2), Tiles(2, 3), Tiles(3, 4)], ChunksTiles(session.Store.Current!));

        // An index publication replaces every index with its own, chunks' tiles among them: a finished session's tile index
        // describes every segment.
        StoreDependency published = PublishIndex(session.Store, "an index of generation 7"u8);
        Assert.Equal([published], Indexes(session.Store.Current!));
        PublishChunk(session.Store, firstOrdinal: 29, records: 4, indexer: indexer);
        Assert.Equal([Tiles(7, 9)], ChunksTiles(session.Store.Current!));
    }

    [Fact(DisplayName = "S4: a release that gives up a chunk's segment gives up that chunk's tiles and every other index, and keeps the tiles of the chunks it keeps")]
    public void AChunksTilesGoOnlyWithItsSegments()
    {
        using var session = new TemporarySession();
        var indexer = new ChunkIndexer();
        for (int chunk = 0; chunk < 4; chunk++)
        {
            PublishChunk(session.Store, firstOrdinal: 1 + ((ulong)chunk * 4), records: 4, indexer: indexer);
        }

        string second = Assert.Single(SessionSegments.Names(session.Store.Current!), name => name.StartsWith("seg-0000000002-", StringComparison.Ordinal));
        _ = session.Store.ReleaseDependencies([second], "the second chunk's rows", Committed, Committed);
        SessionManifestV1 released = session.Store.Current!;
        Assert.Equal([Tiles(1, 2), Tiles(3, 4)], ChunksTiles(released));
        Assert.Empty(OtherIndexes(released));
        Assert.Contains(Tiles(2, 3), released.Retention!.ReleasedFiles);
        Assert.Contains(IndexName(4), released.Retention.ReleasedFiles);

        // A compaction replaces every segment, and so every chunk's tiles go with them.
        Assert.NotNull(SegmentCompaction.Compact(session.Store, Committed));
        Assert.Empty(Indexes(session.Store.Current!));
    }

    [Fact(DisplayName = "S4: a chunk's tiles shortened or rewritten decide no generation: the session is read at its newest, and the next commit drops them and carries the others")]
    public void ADamagedChunksTilesAreDropped()
    {
        using var session = new TemporarySession();
        var indexer = new ChunkIndexer();
        for (int chunk = 0; chunk < 4; chunk++)
        {
            PublishChunk(session.Store, firstOrdinal: 1 + ((ulong)chunk * 4), records: 4, indexer: indexer);
        }

        // One shortened, one rewritten with as many bytes, as a disk or a person might leave them.
        string shortened = Path.Combine(session.Path, Tiles(1, 2));
        File.WriteAllBytes(shortened, File.ReadAllBytes(shortened)[..^1]);
        string rewritten = Path.Combine(session.Path, Tiles(2, 3));
        byte[] bytes = File.ReadAllBytes(rewritten);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(rewritten, bytes);
        File.SetLastWriteTimeUtc(rewritten, DateTime.UtcNow.AddMinutes(1));

        // A viewer reads the newest generation still, and its later hashing finds nothing changed.
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(4, viewer.Current!.Generation);
        Assert.Empty(viewer.VerifyContents().Problems);

        PublishChunk(session.Store, firstOrdinal: 17, records: 4, indexer: indexer);
        Assert.Equal([Tiles(3, 4), Tiles(4, 5)], ChunksTiles(session.Store.Current!));

        // A later publication tiles those chunks again, under names of their own.
        PublishChunk(session.Store, firstOrdinal: 21, records: 4, indexer: new ChunkIndexer(every: true));
        Assert.Equal([Tiles(1, 6), Tiles(2, 6), Tiles(3, 4), Tiles(4, 5), Tiles(5, 6)], ChunksTiles(session.Store.Current!));

        // A release drops those it finds damaged too, and releases the ones of the chunk it gives up.
        string damaged = Path.Combine(session.Path, Tiles(3, 4));
        File.WriteAllBytes(damaged, File.ReadAllBytes(damaged)[..^2]);
        string first = Assert.Single(SessionSegments.Names(session.Store.Current!), name => name.StartsWith("seg-0000000001-", StringComparison.Ordinal));
        _ = session.Store.ReleaseDependencies([first], "the first chunk's rows", Committed, Committed);
        Assert.Equal([Tiles(2, 6), Tiles(4, 5), Tiles(5, 6)], ChunksTiles(session.Store.Current!));
    }

    private static string Tiles(long segments, long publishing) => ChunkTileFiles.FileNameFor(segments, publishing);

    private static string[] ChunksTiles(SessionManifestV1 manifest) =>
        [.. manifest.Dependencies.Where(ChunkTileFiles.Is).Select(dependency => dependency.Name).Order(StringComparer.Ordinal)];

    private static string[] OtherIndexes(SessionManifestV1 manifest) =>
        [.. Indexes(manifest).Where(dependency => !ChunkTileFiles.Is(dependency)).Select(dependency => dependency.Name)];

    private static StoreDependency Named(SessionManifestV1 manifest, string name) =>
        manifest.Dependencies.Single(dependency => dependency.Name == name);

    private static StoreDependency Dependency(string name, StoreDependencyKind kind) =>
        new(name, kind, 1, "sha256:" + new string('0', 64));

    /// <summary>An indexer that stages tiles named for a generation whose segments the session never held.</summary>
    private sealed class StrayIndexer : ILiveIndexer
    {
        public IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken)
        {
            StoreStagingFile tiles = store.Stage(ChunkTileFiles.FileNameFor(99, generation), StoreDependencyKind.Index);
            tiles.Content.Write("of no chunk"u8);
            _ = tiles.Complete();
            return [tiles];
        }
    }

    /// <summary>
    /// An indexer that stages, as a live follow does, an index of the store's current generation and the tiles of its
    /// newest chunk - or, <paramref name="every"/>, of every chunk no carried tiles describe - each saying which chunk it
    /// describes.
    /// </summary>
    private sealed class ChunkIndexer(bool every = false) : ILiveIndexer
    {
        public IReadOnlyList<StoreStagingFile> Stage(SessionStore store, long generation, CancellationToken cancellationToken)
        {
            if (store.Current is not { } current)
            {
                return [];
            }

            HashSet<long> tiled = [.. current.Dependencies.Where(ChunkTileFiles.Is)
                .Select(dependency => ChunkTileFiles.SegmentsGenerationOf(dependency.Name)!.Value)];
            long[] published = [.. SessionSegments.Names(current).Select(name => SessionSegments.PublishingGeneration(name)!.Value).Distinct()];
            long[] chunks = every
                ? [.. published.Where(chunk => !tiled.Contains(chunk))]
                : [.. published.Where(chunk => chunk == published.Max() && !tiled.Contains(chunk))];
            var staged = new List<StoreStagingFile>();
            foreach (long chunk in chunks)
            {
                StoreStagingFile tiles = store.Stage(ChunkTileFiles.FileNameFor(chunk, generation), StoreDependencyKind.Index);
                tiles.Content.Write(Encoding.UTF8.GetBytes($"of chunk {chunk}"));
                _ = tiles.Complete();
                staged.Add(tiles);
            }

            if (!every)
            {
                StoreStagingFile index = store.Stage(IndexName(generation), StoreDependencyKind.Index);
                index.Content.Write(Encoding.UTF8.GetBytes($"of generation {current.Generation}"));
                _ = index.Complete();
                staged.Add(index);
            }

            return staged;
        }
    }
}
