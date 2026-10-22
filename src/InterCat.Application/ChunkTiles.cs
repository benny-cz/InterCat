using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The tiles a live follow keeps of each chunk it publishes (`contracts/tile-index-v1.md` §6): a tile index of the
/// observation segments one generation added, binding no record, staged into a later generation and carried by every
/// generation after it that names those segments (store-v1 §5). A reader of a session still being recorded counts its
/// overview and its timeline from the tiles of every chunk but the newest, whose rows it reads (§12.1 S1, S4).
/// </summary>
internal static class ChunkTiles
{
    /// <summary>
    /// The most chunks one publication tiles: the one the generation it extends added, and older ones that no tiles
    /// describe - recorded before chunks were tiled, or whose tiles were dropped - newest first.
    /// </summary>
    internal const int MaximumChunksPerPublication = 4;

    /// <summary>
    /// The rows past which a publication tiles no further chunk: it always tiles the first, and each next one only while
    /// those it tiled hold fewer, so tiling what earlier publications left costs a publication about a chunk's worth of
    /// reading rather than the session's.
    /// </summary>
    internal const long MaximumRowsPerPublication = 1_000_000;

    /// <summary>
    /// The blocks a chunk's tiles keep decoded: a timeline reads a few of a chunk's, and a long recording names hundreds of
    /// chunks, so each keeps few rather than a generation's tile index's budget.
    /// </summary>
    internal const int CachedBlocks = 64;

    /// <summary>The chunk tiles <paramref name="manifest"/> names, in its order.</summary>
    public static StoreDependency[] NamedBy(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return [.. manifest.Dependencies.Where(ChunkTileFiles.Is)];
    }

    /// <summary>
    /// The generations whose observation segments <paramref name="manifest"/> names and none of the chunk tiles it names
    /// describe, newest first.
    /// </summary>
    public static long[] Untiled(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        HashSet<long> tiled = [.. NamedBy(manifest).Select(dependency => ChunkTileFiles.SegmentsGenerationOf(dependency.Name)!.Value)];
        return [.. SessionSegments.Names(manifest)
            .Select(SessionSegments.PublishingGeneration)
            .OfType<long>()
            .Where(chunk => !tiled.Contains(chunk))
            .Distinct()
            .OrderDescending()];
    }

    /// <summary>
    /// Stages into generation <paramref name="generation"/> the tiles of the chunks of <paramref name="manifest"/>, the
    /// generation it extends, that no chunk tiles describe: newest first, as many as
    /// <see cref="MaximumChunksPerPublication"/> and <see cref="MaximumRowsPerPublication"/> allow. A chunk that cannot be
    /// tiled is passed over, and a later publication tiles it: tiles save a reader time and never change an answer.
    /// <paramref name="maximumRows"/> is <see cref="MaximumRowsPerPublication"/> but in a test of that bound.
    /// </summary>
    public static List<StoreStagingFile> Stage(
        SessionStore store,
        SessionManifestV1 manifest,
        long generation,
        CancellationToken cancellationToken,
        long maximumRows = MaximumRowsPerPublication)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        var staged = new List<StoreStagingFile>();
        StoreDependency[] observed = SessionOverviewIndex.ObservationSegments(manifest);
        long rows = 0;
        try
        {
            foreach (long chunk in Untiled(manifest).Take(MaximumChunksPerPublication))
            {
                if (staged.Count > 0 && rows >= maximumRows)
                {
                    break;
                }

                StoreStagingFile? file = null;
                try
                {
                    (StoreDependency Dependency, SegmentReaderV1 Reader)[] segments = [.. observed
                        .Where(segment => SessionSegments.PublishingGeneration(segment.Name) == chunk)
                        .Select(segment => (segment, SessionSegments.Open(store, manifest, segment.Name)))];
                    file = store.Stage(ChunkTileFiles.FileNameFor(chunk, generation), StoreDependencyKind.Index);
                    _ = SessionTileIndex.WriteUnbound(file.Content, manifest.SessionId, manifest.Generation, segments, cancellationToken);
                    _ = file.Complete();
                    staged.Add(file);
                    file = null;
                    rows += segments.Sum(segment => (long)segment.Reader.RowCount);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
                    or InvalidOperationException)
                {
                    file?.Dispose();
                }
                catch
                {
                    file?.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            // Cancelled, or failed otherwise: nothing staged here is published.
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }

            throw;
        }

        return staged;
    }
}
