namespace InterCat.Storage;

/// <summary>
/// Current state of one store instance's immutable segment-reader cache. The byte budget is admission accounting over
/// the published segment and dictionary payload lengths held by cached readers; managed object overhead is qualified
/// separately by the process memory budget (R8, §12, §19.3).
/// </summary>
/// <param name="Bypasses">Readers served uncached because admitting them would have exceeded the budget.</param>
public sealed record SegmentReaderCacheSnapshot(
    long BudgetBytes,
    long AdmittedPayloadBytes,
    int Entries,
    long Hits,
    long Misses,
    long Bypasses);

/// <summary>
/// A payload-bounded set of already-verified immutable segment readers. A reader owns the segment bytes and decoded
/// dictionaries it was opened with, so reusing it avoids re-reading the file and re-running segment and dictionary
/// integrity checks on every projection. Pruning drops only the cache's reference: a query already holding a reader
/// remains valid for the generation it leased.
/// </summary>
/// <remarks>
/// Readers are admitted while they fit and served uncached once the cache is full; a reachable reader is never evicted
/// to make room. Until S4, every projection and query scans the whole session in the same order, and under such scans a
/// recency policy evicts exactly the readers the next scan reads first: a session larger than the budget would hit
/// nothing and still pay for the churn. Admitting only what fits keeps a stable share cached instead. Room is freed by
/// pruning to the selected generation, as compaction and retention replace segments. A viewport-scoped reader (S4) is
/// the point to revisit this.
/// </remarks>
internal sealed class SegmentReaderCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long budgetBytes;
    private SessionManifestV1? selectedManifest;
    private long admittedPayloadBytes;
    private long hits;
    private long misses;
    private long bypasses;

    public SegmentReaderCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        this.budgetBytes = budgetBytes;
    }

    public SegmentReaderCacheSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return new(budgetBytes, admittedPayloadBytes, entries.Count, hits, misses, bypasses);
            }
        }
    }

    /// <summary>
    /// Returns a cached reader only when the manifest still names the exact immutable segment and every dictionary the
    /// reader was decoded with. This prevents a cache hit from making an otherwise incomplete generation appear readable
    /// after retention or corruption changed its dependency set.
    /// </summary>
    public bool TryGet(
        StoreDependency segment,
        SessionManifestV1 manifest,
        out SegmentReaderV1 reader)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(manifest);
        lock (gate)
        {
            if (!entries.TryGetValue(segment.Name, out Entry? entry) || !entry.Matches(segment, manifest))
            {
                misses++;
                reader = null!;
                return false;
            }

            hits++;
            reader = entry.Reader;
            return true;
        }
    }

    /// <summary>
    /// Removes cached readers that the selected generation can no longer reach. Active queries keep their own reader
    /// references; pruning only releases the store cache's copy, so a retention publication does not leave released
    /// evidence resident merely because an earlier query touched it.
    /// </summary>
    public void Prune(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (gate)
        {
            selectedManifest = manifest;
            foreach (Entry unreachable in entries.Values.Where(entry => !entry.IsReachableFrom(manifest)).ToArray())
            {
                Remove(unreachable);
            }
        }
    }

    /// <summary>Drops every cached reader. As with pruning, a query already holding one keeps it.</summary>
    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            admittedPayloadBytes = 0;
        }
    }

    /// <summary>
    /// Offers a reader after its segment and dictionaries were fully opened, and returns the one to use: the cached copy
    /// when another request cached the same segment meanwhile, else this one, cached if it fits.
    /// </summary>
    public SegmentReaderV1 Add(
        StoreDependency segment,
        IReadOnlyList<StoreDependency> dictionaries,
        SegmentReaderV1 reader)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(dictionaries);
        ArgumentNullException.ThrowIfNull(reader);
        long bytes = checked(segment.LengthBytes + dictionaries.Sum(dictionary => dictionary.LengthBytes));
        var entry = new Entry(segment, [.. dictionaries], reader, bytes);
        lock (gate)
        {
            // A query can finish opening an older leased generation after retention selected a newer one. Cache the
            // result only when its exact immutable dependencies are still reachable from the store's selected manifest.
            if (selectedManifest is null || !entry.IsReachableFrom(selectedManifest))
            {
                return reader;
            }

            // Another request can finish the same immutable open while this one is reading. Reuse the first valid
            // cache entry rather than retaining duplicate byte arrays.
            if (entries.TryGetValue(segment.Name, out Entry? existing))
            {
                if (existing.SameDependencies(entry))
                {
                    return existing.Reader;
                }

                Remove(existing);
            }

            if (admittedPayloadBytes + bytes > budgetBytes)
            {
                bypasses++;
                return reader;
            }

            entries.Add(segment.Name, entry);
            admittedPayloadBytes += bytes;
            return reader;
        }
    }

    private void Remove(Entry entry)
    {
        _ = entries.Remove(entry.Segment.Name);
        admittedPayloadBytes -= entry.AdmittedPayloadBytes;
    }

    private sealed record Entry(
        StoreDependency Segment,
        IReadOnlyList<StoreDependency> Dictionaries,
        SegmentReaderV1 Reader,
        long AdmittedPayloadBytes)
    {
        public bool SameDependencies(Entry other) =>
            Segment == other.Segment && Dictionaries.SequenceEqual(other.Dictionaries);

        public bool Matches(StoreDependency segment, SessionManifestV1 manifest) =>
            Segment == segment && IsReachableFrom(manifest);

        public bool IsReachableFrom(SessionManifestV1 manifest)
        {
            StoreDependency? namedSegment = manifest.Dependencies.FirstOrDefault(candidate =>
                candidate.Kind == StoreDependencyKind.Segment
                && candidate.Name.Equals(Segment.Name, StringComparison.OrdinalIgnoreCase));
            if (namedSegment != Segment)
            {
                return false;
            }

            foreach (StoreDependency dictionary in Dictionaries)
            {
                StoreDependency? named = manifest.Dependencies.FirstOrDefault(candidate =>
                    candidate.Kind == StoreDependencyKind.Dictionary
                    && candidate.Name.Equals(dictionary.Name, StringComparison.OrdinalIgnoreCase));
                if (named != dictionary)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
