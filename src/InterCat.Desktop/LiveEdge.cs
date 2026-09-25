using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>One tenth of a second of the live preview, placed on the session's presentation axis.</summary>
/// <param name="Interval">The bin's half-open interval in presentation ticks.</param>
/// <param name="Counts">Records by mechanism, most first; every count is positive.</param>
public sealed record LiveEdgeBin(TimeRange Interval, IReadOnlyList<(Mechanism Mechanism, int Count)> Counts)
{
    public int Total => Counts.Sum(count => count.Count);

    /// <summary>The records of one mechanism in this bin; zero when it has none.</summary>
    public int CountOf(Mechanism mechanism) => Counts.FirstOrDefault(count => count.Mechanism == mechanism).Count;
}

/// <summary>
/// What a live capture has journaled after the chunks the workspace shows: the broker's preview counts (broker-v1 §5.9)
/// for the chunks a viewer has not yet derived, on the timeline's own axis. Counts only - drawn as a labelled preview
/// beyond the published timeline, never ranked, selected, exported or added to a published count (§19.3).
/// </summary>
/// <param name="Bins">Bins in time order.</param>
/// <param name="ShownChunks">The chunks the displayed generation holds: the preview draws the ones after them.</param>
/// <param name="OpenChunk">The chunk the broker is writing.</param>
/// <param name="UncoveredChunks">Chunks after those shown that the preview no longer covers, because the viewer is
/// further behind than the broker keeps counts for; their records are neither shown nor previewed yet.</param>
/// <param name="UnbinnedRecords">
/// At most this many records of the previewed chunks are in no bin (outside the broker's time window or count bound).
/// </param>
public sealed record LiveEdge(
    IReadOnlyList<LiveEdgeBin> Bins,
    int ShownChunks,
    int OpenChunk,
    int UncoveredChunks,
    long UnbinnedRecords)
{
    public long PreviewedRecords => Bins.Sum(bin => (long)bin.Total);

    /// <summary>
    /// Places the preview's counts for chunks after <paramref name="shownChunks"/> on the presentation axis of
    /// <paramref name="clock"/>, the clock the displayed generation was published on. A bin whose instant is outside the
    /// clock's plausible range is left out rather than placed at a guessed time; null when nothing is left to preview.
    /// </summary>
    public static LiveEdge? From(BrokerCapturePreview preview, int shownChunks, SourceClockDescriptor clock)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentOutOfRangeException.ThrowIfNegative(shownChunks);
        var bins = new SortedDictionary<long, Dictionary<Mechanism, int>>();
        foreach (BrokerPreviewCount count in preview.Counts)
        {
            if (count.Chunk <= shownChunks)
            {
                continue;
            }

            if (!bins.TryGetValue(count.Bin, out Dictionary<Mechanism, int>? tally))
            {
                tally = [];
                bins.Add(count.Bin, tally);
            }

            tally[count.Mechanism] = checked(tally.GetValueOrDefault(count.Mechanism) + count.Count);
        }

        var placed = new List<LiveEdgeBin>(bins.Count);
        foreach ((long bin, Dictionary<Mechanism, int> tally) in bins)
        {
            if (PresentationTick(clock, checked(bin * preview.BinNativeTicks)) is not { } start
                || PresentationTick(clock, checked((bin + 1) * preview.BinNativeTicks)) is not { } end
                || end <= start)
            {
                continue;
            }

            placed.Add(new(new TimeRange(start, end), Array.AsReadOnly(
                [.. tally.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key)
                    .Select(entry => (entry.Key, entry.Value))])));
        }

        int uncovered = Math.Max(0, preview.FirstChunk - shownChunks - 1);
        return placed.Count == 0 && uncovered == 0
            ? null
            : new LiveEdge(placed.AsReadOnly(), shownChunks, preview.OpenChunk, uncovered, preview.UnbinnedRecords);
    }

    /// <summary>
    /// A native reading's presentation tick, as the timeline places a record: its session nanoseconds divided by 100,
    /// truncated as the derived session-relative ticks are. Null outside the clock's plausible distance.
    /// </summary>
    private static long? PresentationTick(SourceClockDescriptor clock, long nativeTicks)
    {
        Int128 nanoseconds = SourceClockMath.SessionNanoseconds(clock, nativeTicks);
        return nanoseconds > clock.MaximumAbsoluteSessionNanoseconds || nanoseconds < -clock.MaximumAbsoluteSessionNanoseconds
            ? null
            : (long)(nanoseconds / 100);
    }
}
