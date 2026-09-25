using InterCat.Domain;

namespace InterCat.Capture.Recording;

/// <summary>One preview count: the records of one chunk, one mechanism and one time bin.</summary>
/// <param name="Chunk">The chunk's sequence number: 1 for the capture's first chunk.</param>
/// <param name="Bin">The bin's index: its first native reading divided by the bin width.</param>
public readonly record struct LivePreviewCount(int Chunk, long Bin, Mechanism Mechanism, int Count);

/// <summary>
/// What a live capture has journaled and not yet handed to a follower, as counts. It covers the chunk being written and
/// the most recently published ones, so a viewer whose published view lags the broker by a chunk or two still sees
/// every record at least once: first here, then exactly, once the chunk that holds it is derived.
/// </summary>
/// <param name="BinNativeTicks">The width of one bin in native clock ticks.</param>
/// <param name="OpenChunk">The sequence number of the chunk being written.</param>
/// <param name="RetainedChunks">How many published chunks before it the counts still cover.</param>
/// <param name="CountedRecords">Every record of the covered chunks, binned or not.</param>
/// <param name="UnbinnedRecords">Covered records no bin holds: outside the time window, or past the entry bound.</param>
public sealed record LivePreviewSnapshot(
    long BinNativeTicks,
    int OpenChunk,
    int RetainedChunks,
    long CountedRecords,
    long UnbinnedRecords,
    IReadOnlyList<LivePreviewCount> Counts)
{
    /// <summary>The first chunk the counts cover.</summary>
    public int FirstChunk => OpenChunk - RetainedChunks;
}

/// <summary>
/// Counts a live capture's journaled records by mechanism and time bin until the chunk that holds them is published and
/// followed, so an ordinary viewer can draw activity before its exact publication (plan §12's steady-state latency,
/// §19.3's labelled preview). Only the chunk writer counts; a status request reads a bounded snapshot. It holds counts
/// only - no record, name, address or process - so a preview discloses no more than the mechanism of a record and when
/// it happened.
/// </summary>
public sealed class LivePreviewTally
{
    /// <summary>Published chunks whose counts are kept after they publish, for a viewer still deriving them.</summary>
    public const int RetainedPublishedChunks = 4;

    /// <summary>The newest bins a chunk keeps; an older record is counted as unbinned rather than growing the tally.</summary>
    public const int WindowBins = 256;

    /// <summary>The most counts a snapshot carries: newest chunks and bins first, the rest counted as unbinned.</summary>
    public const int MaximumCounts = 2_000;

    /// <summary>Bins per second: a tenth of a second, finer than a live chunk and coarser than a pointer can aim at.</summary>
    public const int BinsPerSecond = 10;

    private readonly Lock gate = new();
    private readonly Queue<ChunkCounts> published = new();
    private ChunkCounts open;

    public LivePreviewTally(long nativeTicksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nativeTicksPerSecond);
        BinNativeTicks = Math.Max(1, nativeTicksPerSecond / BinsPerSecond);
        open = new ChunkCounts(1);
    }

    public long BinNativeTicks { get; }

    /// <summary>Counts one journaled record of the chunk being written. A reading before the clock's zero is not binned.</summary>
    public void Count(Mechanism mechanism, long nativeTicks)
    {
        lock (gate)
        {
            open.Add(nativeTicks < 0 ? null : nativeTicks / BinNativeTicks, mechanism);
        }
    }

    /// <summary>The chunk being written was published: it is kept for a lagging follower, and a new one begins.</summary>
    public void ChunkPublished()
    {
        lock (gate)
        {
            published.Enqueue(open);
            if (published.Count > RetainedPublishedChunks)
            {
                _ = published.Dequeue();
            }

            open = new ChunkCounts(open.Sequence + 1);
        }
    }

    /// <summary>A bounded copy for one status request: the newest chunks and bins first, never more than the wire holds.</summary>
    public LivePreviewSnapshot Read()
    {
        lock (gate)
        {
            ChunkCounts[] chunks = [open, .. published.Reverse()];
            long counted = chunks.Sum(chunk => chunk.Records);
            long unbinned = chunks.Sum(chunk => chunk.Unbinned);
            var counts = new List<LivePreviewCount>(Math.Min(MaximumCounts, chunks.Sum(chunk => chunk.Entries)));
            foreach (ChunkCounts chunk in chunks)
            {
                foreach (LivePreviewCount count in chunk.NewestFirst())
                {
                    if (counts.Count < MaximumCounts)
                    {
                        counts.Add(count);
                    }
                    else
                    {
                        unbinned += count.Count;
                    }
                }
            }

            return new(BinNativeTicks, open.Sequence, published.Count, counted, unbinned, counts.AsReadOnly());
        }
    }

    /// <summary>One chunk's counts, trimmed to its newest bins as it grows.</summary>
    private sealed class ChunkCounts(int sequence)
    {
        private readonly Dictionary<(long Bin, Mechanism Mechanism), int> counts = [];

        // Bins are never negative, so starting below them keeps "newest - WindowBins" far from overflow.
        private long newest = -1;

        public int Sequence { get; } = sequence;

        public long Records { get; private set; }

        public long Unbinned { get; private set; }

        public int Entries => counts.Count;

        public void Add(long? bin, Mechanism mechanism)
        {
            Records++;
            if (bin is not { } index || index <= newest - WindowBins)
            {
                Unbinned++;
                return;
            }

            if (index > newest)
            {
                newest = index;
                if (counts.Count > 0 && counts.Keys.Any(key => key.Bin <= newest - WindowBins))
                {
                    foreach ((long Bin, Mechanism Mechanism) stale in counts.Keys.Where(key => key.Bin <= newest - WindowBins).ToArray())
                    {
                        Unbinned += counts[stale];
                        _ = counts.Remove(stale);
                    }
                }
            }

            (long, Mechanism) key = (index, mechanism);
            int current = counts.GetValueOrDefault(key);
            if (current == int.MaxValue)
            {
                Unbinned++;
                return;
            }

            counts[key] = current + 1;
        }

        public IEnumerable<LivePreviewCount> NewestFirst() => counts
            .OrderByDescending(entry => entry.Key.Bin)
            .ThenBy(entry => entry.Key.Mechanism)
            .Select(entry => new LivePreviewCount(Sequence, entry.Key.Bin, entry.Key.Mechanism, entry.Value));
    }
}
