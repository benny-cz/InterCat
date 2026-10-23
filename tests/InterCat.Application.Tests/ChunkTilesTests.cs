using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// tile-index-v1 §6: a live follow stages the tiles of the chunk the generation it extends added, which every later
/// generation carries, so a session still being recorded counts its overview and its timeline from the tiles of every chunk
/// but its newest, as their rows count (§12.1 S1, S4). They clear the shared derivation cache, so they run alone.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class ChunkTilesTests
{
    [Theory(DisplayName = "S4: a live generation's overview counts every chunk but its newest from the tiles staged of it, opening only the newest chunk's segments, and counts as every segment's rows do")]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(20_261_022)]
    public void TheOverviewCountsChunksFromTheirTiles(int seed)
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        var random = new Random(seed);
        ObservationRowV1[][] chunks = Chunks(random, 3 + random.Next(4), out int backwards);
        for (int chunk = 0; chunk < chunks.Length; chunk++)
        {
            // The chunk whose readings go backwards is one segment, which keeps no tiles.
            int rowsPerSegment = chunk == backwards ? chunks[chunk].Length : 1 + random.Next(chunks[chunk].Length);
            _ = Publish(session.Store, chunks[chunk], rowsPerSegment: rowsPerSegment, indexer: LiveCheckpoints.Instance);
        }

        // Every chunk but the newest is tiled, each by the generation after it.
        SessionManifestV1 manifest = session.Store.Current!;
        Assert.Equal(chunks.Length, manifest.Generation);
        Assert.Equal(
            [.. Enumerable.Range(1, chunks.Length - 1).Select(chunk => ChunkTileFiles.FileNameFor(chunk, chunk + 1))],
            ChunkTiles.NamedBy(manifest).Select(dependency => dependency.Name).Order(StringComparer.Ordinal));
        Assert.Equal([manifest.Generation], ChunkTiles.Untiled(manifest));

        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionManifestV1 viewed = viewer.Current!;
        long? retained = SessionOverviewProjector.RetainedFromTicks(viewed);
        OverviewCounts tiled = SessionOverviewProjector.CountFromChunks(viewer, viewed, SessionDerivationCache.For(viewed), retained, default)
            ?? throw new Xunit.Sdk.XunitException("The generation names its chunks' tiles, so its overview counts from them.");
        Assert.InRange(viewer.SegmentReaderCache.Entries, 0, Newest(viewed) + (backwards < chunks.Length - 1 ? 1 : 0));
        OverviewCounts read = SessionOverviewProjector.Count(
            [.. SessionSegments.Names(viewed).Select(name => SessionSegments.Open(viewer, viewed, name))], retained, default);
        Assert.Equal(Text(read), Text(tiled));
        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S4: a live generation opened cold draws the overview and the zooms a session of the same chunks recorded without tiles draws, opening only its newest chunk's segments and one whose readings go backwards")]
    public void ALiveGenerationOpenedColdDrawsFromChunksTiles()
    {
        using var session = new TemporarySession();
        using var twin = new TemporarySession();

        // The second chunk's readings go backwards in the one segment it is published as, which keeps no tiles, so its rows
        // are read.
        ObservationRowV1[][] chunks = Chunks(new Random(29), 5, out int backwards, backwardsAt: 1);
        for (int chunk = 0; chunk < chunks.Length; chunk++)
        {
            int rowsPerSegment = chunk == backwards ? chunks[chunk].Length : 7;
            _ = Publish(session.Store, chunks[chunk], rowsPerSegment: rowsPerSegment, indexer: LiveCheckpoints.Instance);
            _ = Publish(twin.Store, chunks[chunk], rowsPerSegment: rowsPerSegment);
        }

        Assert.Empty(ChunkTiles.NamedBy(twin.Store.Current!));
        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle drawn = SessionOverviewProjector.Project(viewer);
        Assert.InRange(viewer.SegmentReaderCache.Entries, 2, Newest(viewer.Current!) + NewestFields(viewer.Current!) + 1);
        SessionDerivationCache.Clear();
        SessionOverviewBundle untiled = SessionOverviewProjector.Project(twin.Store);
        Assert.Equal(Text(untiled), Text(drawn));

        // A zoom over the whole extent, at a column per few records, and over a part of it.
        TimeRange extent = drawn.Extent!.Value;
        foreach ((TimeRange interval, int columns) in new[]
        {
            (extent, 256),
            (new TimeRange(extent.StartTicks + (extent.SpanTicks / 4), extent.StartTicks + (extent.SpanTicks / 2)), 37),
        })
        {
            viewer.ReleaseSegmentReaders();
            SessionTimelineDetail zoomed = SessionTimelineQuery.Detail(viewer, interval, columns);
            Assert.Equal(Met(viewer, interval, viewer.Current!.Generation) + Met(viewer, interval, backwards + 1),
                viewer.SegmentReaderCache.Entries);
            Assert.Equal(Text(SessionTimelineQuery.Detail(twin.Store, interval, columns)), Text(zoomed));
        }

        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S4: a chunk's tiles that cannot be read are read around, so the overview and the zooms count as the chunk's rows do")]
    public void DamagedChunksTilesAreReadAround()
    {
        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        ObservationRowV1[][] chunks = Chunks(new Random(11), 4, out _);
        foreach (ObservationRowV1[] chunk in chunks)
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 5, indexer: LiveCheckpoints.Instance);
            _ = Publish(twin.Store, chunk, rowsPerSegment: 5);
        }

        // One chunk's tiles rewritten in the middle, as many bytes as before, and another's gone.
        StoreDependency[] tiles = ChunkTiles.NamedBy(session.Store.Current!);
        string rewritten = Path.Combine(session.Path, tiles[0].Name);
        byte[] bytes = File.ReadAllBytes(rewritten);
        for (int at = bytes.Length / 3; at < bytes.Length / 2; at++)
        {
            bytes[at] ^= 0x5A;
        }

        File.WriteAllBytes(rewritten, bytes);
        File.Delete(Path.Combine(session.Path, tiles[1].Name));

        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(session.Store.Current!.Generation, viewer.Current!.Generation);
        SessionOverviewBundle drawn = SessionOverviewProjector.Project(viewer);
        SessionDerivationCache.Clear();
        Assert.Equal(Text(SessionOverviewProjector.Project(twin.Store)), Text(drawn));
        TimeRange extent = drawn.Extent!.Value;
        Assert.Equal(Text(SessionTimelineQuery.Detail(twin.Store, extent, 200)), Text(SessionTimelineQuery.Detail(viewer, extent, 200)));
        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S4: a publication tiles the chunks no tiles describe, newest first, at most four, and none more once those it tiled hold its bound of rows, until every chunk but the newest is tiled")]
    public void APublicationTilesWhatEarlierOnesLeft()
    {
        using var session = new TemporarySession();
        ObservationRowV1[][] chunks = Chunks(new Random(5), 10, out _);
        for (int chunk = 0; chunk < 7; chunk++)
        {
            _ = Publish(session.Store, chunks[chunk], rowsPerSegment: 9);
        }

        Assert.Empty(ChunkTiles.NamedBy(session.Store.Current!));
        Assert.Equal([7L, 6, 5, 4, 3, 2, 1], ChunkTiles.Untiled(session.Store.Current!));

        // A bound of no row tiles the newest chunk alone: the bound counts only past the first.
        SessionManifestV1 seventh = session.Store.Current!;
        List<StoreStagingFile> bounded = ChunkTiles.Stage(session.Store, seventh, 8, default, maximumRows: 0);
        Assert.Equal([ChunkTileFiles.FileNameFor(7, 8)], bounded.Select(file => file.PublishedName));
        foreach (StoreStagingFile file in bounded)
        {
            file.Dispose();
        }

        // A bound of one row too: the newest chunk holds more.
        bounded = ChunkTiles.Stage(session.Store, seventh, 8, default, maximumRows: 1);
        Assert.Equal([ChunkTileFiles.FileNameFor(7, 8)], bounded.Select(file => file.PublishedName));
        foreach (StoreStagingFile file in bounded)
        {
            file.Dispose();
        }

        _ = Publish(session.Store, chunks[7], rowsPerSegment: 9, indexer: LiveCheckpoints.Instance);
        Assert.Equal([3L, 2, 1], ChunkTiles.Untiled(session.Store.Current!).Where(chunk => chunk < 8));
        _ = Publish(session.Store, chunks[8], rowsPerSegment: 9, indexer: LiveCheckpoints.Instance);
        Assert.Equal([9L], ChunkTiles.Untiled(session.Store.Current!));
        Assert.Equal(
            [
                ChunkTileFiles.FileNameFor(1, 9), ChunkTileFiles.FileNameFor(2, 9), ChunkTileFiles.FileNameFor(3, 9),
                ChunkTileFiles.FileNameFor(4, 8), ChunkTileFiles.FileNameFor(5, 8), ChunkTileFiles.FileNameFor(6, 8),
                ChunkTileFiles.FileNameFor(7, 8), ChunkTileFiles.FileNameFor(8, 9),
            ],
            ChunkTiles.NamedBy(session.Store.Current!).Select(dependency => dependency.Name).Order(StringComparer.Ordinal));
        _ = Publish(session.Store, chunks[9], rowsPerSegment: 9, indexer: LiveCheckpoints.Instance);
        Assert.Equal([10L], ChunkTiles.Untiled(session.Store.Current!));
    }

    [Fact(DisplayName = "S4: a later generation's derivation takes the chunks' tiles an earlier one opened, and opens only those of the chunk it adds")]
    public void ALaterGenerationTakesTheChunksTilesOpened()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[][] chunks = Chunks(new Random(13), 5, out _);
        for (int chunk = 0; chunk < 4; chunk++)
        {
            _ = Publish(session.Store, chunks[chunk], rowsPerSegment: 6, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 earlier = session.Store.Current!;
        IReadOnlyList<TileIndexFile> opened = SessionDerivationCache.For(earlier).TiledChunks(session.Store.Root);
        Assert.Equal(3, opened.Count);
        _ = Publish(session.Store, chunks[4], rowsPerSegment: 6, indexer: LiveCheckpoints.Instance);
        IReadOnlyList<TileIndexFile> later = SessionDerivationCache.For(session.Store.Current!).TiledChunks(session.Store.Root);
        Assert.Equal(4, later.Count);
        Assert.All(opened, file => Assert.Contains(later, taken => ReferenceEquals(taken, file)));
        Assert.Same(later, SessionDerivationCache.For(session.Store.Current!).TiledChunks(session.Store.Root));
    }

    [Fact(DisplayName = "S4: a chunk's tiles bind no record: they state the derivation of no instance and no channel, which a derivation of the session's records does not match, and a live generation names no tile index a brush or a focus could count bindings from")]
    public void AChunksTilesBindNoRecord()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[][] chunks = Chunks(new Random(7), 3, out _);
        foreach (ObservationRowV1[] chunk in chunks)
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 8, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        ProcessInstanceIndex none = ProcessInstanceIndex.Derive([], TestClock, []);
        Assert.True(TileDerivation.Unbound.Matches(none, TransportRelationIndex.Derive([], none)));
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        Assert.False(TileDerivation.Unbound.Matches(processes, TransportRelationIndex.Derive(observations, processes)));

        StoreDependency[] named = ChunkTiles.NamedBy(manifest);
        Assert.Equal(chunks.Length - 1, named.Length);
        foreach (StoreDependency tiles in named)
        {
            TileIndexFile file = SessionTileIndex.Open(session.Store.Root, tiles, manifest.SessionId);
            Assert.Equal(
                (TileDerivation.Unbound.BindingRule, TileDerivation.Unbound.RelationRule, 0, 0),
                (file.Derivation.BindingRule, file.Derivation.RelationRule, file.Derivation.Instances, file.Derivation.Channels));
            Assert.Equal(TileDerivation.Unbound.Fingerprint, file.Derivation.Fingerprint);
            long chunk = ChunkTileFiles.SegmentsGenerationOf(tiles.Name)!.Value;
            Assert.Equal(
                [.. SessionSegments.Names(manifest).Where(name => SessionSegments.PublishingGeneration(name) == chunk)],
                file.Segments.Order(StringComparer.Ordinal));
        }

        Assert.Null(SessionTileIndex.NamedBy(manifest));
        Assert.Null(SessionDerivationCache.For(manifest).PersistedTiles(session.Store.Root));
    }

    /// <summary>
    /// The chunks of a capture as a live follow publishes them: four processes' lifecycle records first, then their
    /// transfers among themselves and to other hosts, over TCP and UDP, a few with no session time, and in chunk
    /// <paramref name="backwards"/> - <paramref name="backwardsAt"/>, or one drawn - a last record read before the one ahead
    /// of it, so a segment holding both keeps no tiles.
    /// </summary>
    private static ObservationRowV1[][] Chunks(Random random, int count, out int backwards, int? backwardsAt = null)
    {
        var chunks = new ObservationRowV1[count][];
        ulong ordinal = 0;
        long ticks = 10;
        int drawn = random.Next(count);
        backwards = backwardsAt ?? drawn;
        for (int chunk = 0; chunk < count; chunk++)
        {
            int rows = 6 + random.Next(30);
            var made = new ObservationRowV1[rows];
            for (int index = 0; index < rows; index++)
            {
                ticks += 1 + random.Next(1_000);
                ordinal++;
                ObservationKind kind = random.Next(2) == 0 ? ObservationKind.Send : ObservationKind.Receive;
                ObservationRowV1 row = chunk == 0 && index < 4
                    ? Lifecycle(ticks, ObservationKind.Create, 100 + index, ordinal)
                    : Transfer(ticks, kind, kind == ObservationKind.Send ? AccountingSide.SendSide : AccountingSide.ReceiveSide,
                            random.Next(4) == 0 ? null : random.Next(10_000), 100 + random.Next(4), ordinal)
                        .Between(
                            string.Create(CultureInfo.InvariantCulture, $"10.0.0.{1 + random.Next(4)}:{40_000 + random.Next(3)}"),
                            string.Create(CultureInfo.InvariantCulture, $"10.0.0.{5 + random.Next(3)}:443"))
                        with { Mechanism = random.Next(3) == 0 ? Mechanism.Udp : Mechanism.Tcp };
                long? session = random.Next(10) == 0 && index < rows - 2 ? null : ticks * 100;
                if (chunk == backwards && index == rows - 1)
                {
                    session = (ticks * 100) - 200_000;
                }

                made[index] = row with { SessionRelativeTicks = session };
            }

            chunks[chunk] = made;
        }

        return chunks;
    }

    private static (SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) SegmentsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        return ([.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))],
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))]);
    }

    /// <summary>How many observation segments the newest chunk of <paramref name="manifest"/> added.</summary>
    private static int Newest(SessionManifestV1 manifest) =>
        SessionSegments.Names(manifest).Count(name => SessionSegments.PublishingGeneration(name) == manifest.Generation);

    /// <summary>
    /// How many of the observation segments generation <paramref name="chunk"/> published hold a native reading
    /// <paramref name="interval"/> meets.
    /// </summary>
    private static int Met(SessionStore store, TimeRange interval, long chunk)
    {
        SessionManifestV1 manifest = store.Current!;
        (long First, long End) readings = SessionNativeInterval.Readings(interval, SessionSegments.SourceClock(store.Root, manifest)!.Value);
        return SessionSegments.Names(manifest).Count(name => SessionSegments.PublishingGeneration(name) == chunk
            && SessionNativeInterval.Meets(SessionSegments.NativeRange(store, manifest, name), readings));
    }

    /// <summary>How many source-field segments the newest chunk of <paramref name="manifest"/> added.</summary>
    private static int NewestFields(SessionManifestV1 manifest) =>
        SessionSegments.FieldNames(manifest).Count(name => SessionSegments.PublishingGeneration(name) == manifest.Generation);

    private static string Text(OverviewCounts counts) => string.Create(CultureInfo.InvariantCulture,
        $"{counts.Rows} rows, {counts.WithoutTime} untimed, {counts.BeforeExtent} before {counts.Extent}; main {Text(counts.Main)}; minimap {Text(counts.Minimap)}");

    private static string Text(TimelineColumns? columns) => columns is null ? "none" : string.Create(CultureInfo.InvariantCulture,
        $"{columns.Interval} [{string.Join(',', columns.Counts)}] [{string.Join(',', columns.Tallies().Select(tally => $"{tally.Column}:{tally.Mechanism}={tally.Count}"))}]");

    private static string Text(SessionOverviewBundle bundle) => JsonSerializer.Serialize(new
    {
        bundle.Extent,
        bundle.ObservationRows,
        bundle.RowsWithoutSessionTime,
        bundle.Timeline,
        bundle.MechanismLanes,
        bundle.Minimap,
        Nodes = bundle.Nodes.Select(node => $"{node.Id} {node.Records}"),
        Edges = bundle.Edges.Select(edge => $"{edge.Key} {edge.ObservationCount}"),
    });

    private static string Text(SessionTimelineDetail detail) => JsonSerializer.Serialize(new { detail.Interval, detail.Buckets, detail.MechanismLanes });
}
