namespace InterCat.Storage;

/// <summary>
/// Current state of one store instance's immutable segment-reader cache. The byte budget bounds what cached readers hold:
/// the segment columns and chunks they have read and the dictionaries they decoded. Managed object overhead is qualified
/// separately by the process memory budget (R8, §12, §19.3).
/// </summary>
/// <param name="AdmittedPayloadBytes">What the cached readers hold now, which a trim brings back within the budget.</param>
/// <param name="Bypasses">Readers served uncached because admitting them would have exceeded the budget.</param>
public sealed record SegmentReaderCacheSnapshot(
    long BudgetBytes,
    long AdmittedPayloadBytes,
    int Entries,
    long Hits,
    long Misses,
    long Bypasses);

/// <summary>
/// A bounded set of already-verified immutable segment readers. A reader holds the columns it has read and
/// checked, and the dictionaries it decoded, so reusing it avoids reading them and running their checks again on every
/// projection. Since revision 161 a reader is charged what it holds - the columns and chunk it has read, and the
/// dictionaries it decoded - not its file's length. What a reader holds grows as queries read more of it, so when the
/// cache holds more than its budget, <see cref="Trim"/> gives back every column but the few every projection reads.
/// Pruning drops only the cache's reference: a query already holding a reader remains valid for the generation it
/// leased.
/// </summary>
/// <remarks>
/// Readers are admitted while they fit and served uncached once the cache is full; a reachable reader is never evicted
/// to make room. Every projection reads every segment's session time and mechanism, the columns of its tiles (S4), and
/// under such scans a recency policy would evict exactly the readers the next scan reads first. A reader trimmed to
/// those columns holds about nine bytes a row, so the budget keeps readers for tens of millions of rows rather than the
/// million or so whole files it kept when it charged file lengths. Room is also freed by pruning to the selected
/// generation, as compaction and retention replace segments.
/// </remarks>
internal sealed class SegmentReaderCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long budgetBytes;
    private SessionManifestV1? selectedManifest;
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
                return new(budgetBytes, Charge(), entries.Count, hits, misses, bypasses);
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
        long dictionaryBytes = dictionaries.Sum(dictionary => dictionary.LengthBytes);
        var entry = new Entry(segment, [.. dictionaries], reader, dictionaryBytes);
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

            if (Charge() + entry.Charge > budgetBytes)
            {
                bypasses++;
                return reader;
            }

            entries.Add(segment.Name, entry);
            return reader;
        }
    }

    /// <summary>
    /// Keeps what the cache holds within its budget: when its readers hold more, they give back every column but
    /// <paramref name="kept"/>, one reader at a time until the charge fits. No reader is evicted: a query holding one
    /// keeps what it read, and the next read of a released column reads it again. Returns the bytes released.
    /// </summary>
    public long Trim(IReadOnlySet<SegmentColumnId> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        lock (gate)
        {
            long charge = Charge();
            long released = 0;
            foreach (Entry entry in entries.Values)
            {
                if (charge - released <= budgetBytes)
                {
                    break;
                }

                released += entry.Reader.Release(kept);
            }

            return released;
        }
    }

    /// <summary>What every cached reader holds now. Called under the gate.</summary>
    private long Charge()
    {
        long charge = 0;
        foreach (Entry entry in entries.Values)
        {
            charge += entry.Charge;
        }

        return charge;
    }

    private void Remove(Entry entry)
    {
        _ = entries.Remove(entry.Segment.Name);
    }

    private sealed record Entry(
        StoreDependency Segment,
        IReadOnlyList<StoreDependency> Dictionaries,
        SegmentReaderV1 Reader,
        long DictionaryBytes)
    {
        /// <summary>What the entry holds now: the reader's resident columns and chunk, and its decoded dictionaries.</summary>
        public long Charge => Reader.ResidentBytes + DictionaryBytes;

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
