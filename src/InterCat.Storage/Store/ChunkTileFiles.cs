using System.Globalization;

namespace InterCat.Storage;

/// <summary>
/// The tiles a live follow keeps of one publication's observation segments (`contracts/tile-index-v1.md` §6, store-v1
/// §5): a tile index of the segments one generation added, named for that generation and for the one that published it.
/// Unlike every other index, a generation that carries those segments carries it too, and it is released only with them,
/// so a session still being recorded keeps the tiles of every chunk but its newest.
/// </summary>
public static class ChunkTileFiles
{
    private const string Prefix = "chunk-tiles-";
    private const string Suffix = ".bin";

    /// <summary>
    /// The name of the tiles of generation <paramref name="segments"/>'s observation segments, published by generation
    /// <paramref name="publishing"/>: a chunk tiled again, once its tiles were dropped, is tiled under a name of its own.
    /// </summary>
    public static string FileNameFor(long segments, long publishing) =>
        segments is < 1 or > 9_999_999_999
            ? throw new ArgumentOutOfRangeException(nameof(segments), segments, "A generation is 1..9,999,999,999.")
            : publishing is < 1 or > 9_999_999_999
                ? throw new ArgumentOutOfRangeException(nameof(publishing), publishing, "A generation is 1..9,999,999,999.")
                : string.Create(CultureInfo.InvariantCulture, $"{Prefix}{segments:D10}-{publishing:D10}{Suffix}");

    /// <summary>
    /// The generation whose observation segments the chunk tiles named <paramref name="name"/> describe; null for a name
    /// that is not one of theirs.
    /// </summary>
    public static long? SegmentsGenerationOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == Prefix.Length + 10 + 1 + 10 + Suffix.Length
            && name.StartsWith(Prefix, StringComparison.Ordinal)
            && name[Prefix.Length + 10] == '-'
            && name.EndsWith(Suffix, StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(Prefix.Length, 10), NumberStyles.None, CultureInfo.InvariantCulture, out long segments)
            && long.TryParse(name.AsSpan(Prefix.Length + 11, 10), NumberStyles.None, CultureInfo.InvariantCulture, out long publishing)
            && segments >= 1
            && publishing >= 1
                ? segments
                : null;
    }

    /// <summary>Whether <paramref name="dependency"/> is a chunk's tiles: an index under one of their names.</summary>
    public static bool Is(StoreDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return dependency.Kind == StoreDependencyKind.Index && SegmentsGenerationOf(dependency.Name) is not null;
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is an observation segment of the chunk whose tiles <paramref name="tiles"/>
    /// names: one generation <see cref="SegmentsGenerationOf"/> reads from its name published.
    /// </summary>
    public static bool Describes(string tiles, StoreDependency segment)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Kind == StoreDependencyKind.Segment
            && segment.Name.StartsWith("seg-", StringComparison.Ordinal)
            && SegmentsGenerationOf(tiles) is { } generation
            && SessionSegments.PublishingGeneration(segment.Name) == generation;
    }
}
