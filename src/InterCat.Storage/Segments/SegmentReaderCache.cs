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
/// Since revision 169 a reader also keeps values derived from its rows, such as a derivation's bindings, charged as
/// columns are; a trim gives them back only when giving back the columns was not enough. Since revision 876 the budget
/// holds while queries read, not only once their leases end: a cached reader that comes to hold more past the budget
/// has every other give back its columns but the few every projection reads, so a derivation reading every segment's
/// columns under one lease holds the budget and the reader it reads rather than every column it read (S2). Pruning
/// drops only the cache's reference: a query already holding a reader remains valid for the generation it leased.
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
    private readonly IReadOnlySet<SegmentColumnId> resident;
    private readonly Action<SegmentReaderV1> grown;
    private SessionManifestV1? selectedManifest;
    private long hits;
    private long misses;
    private long bypasses;

    /// <param name="resident">The columns a reader keeps when the budget holds while it is read: those every projection reads.</param>
    public SegmentReaderCache(long budgetBytes, IReadOnlySet<SegmentColumnId> resident)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentNullException.ThrowIfNull(resident);
        this.budgetBytes = budgetBytes;
        this.resident = resident;
        grown = Grown;
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
    /// The cached reader of a segment, as <see cref="TryGet"/> finds it, without counting a hit or a miss: for a question
    /// asked of a reader the store may already hold, such as the readings it spans, that opens nothing either way.
    /// </summary>
    public bool TryPeek(
        StoreDependency segment,
        SessionManifestV1 manifest,
        out SegmentReaderV1 reader)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(manifest);
        lock (gate)
        {
            bool held = entries.TryGetValue(segment.Name, out Entry? entry) && entry.Matches(segment, manifest);
            reader = held ? entry!.Reader : null!;
            return held;
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
            foreach (Entry entry in entries.Values)
            {
                entry.Reader.Grew = null;
            }

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
            reader.Grew = grown;
            return reader;
        }
    }

    /// <summary>
    /// Has every cached reader give back every column but <paramref name="kept"/>, whatever the cache holds against its
    /// budget, and returns the bytes released: before a pass that reads the whole generation through readers of its own,
    /// so it never holds a second copy of what queries read (revision 875). Values derived from rows stay, as a trim
    /// leaves them until columns were not enough; no reader is evicted.
    /// </summary>
    public long GiveBack(IReadOnlySet<SegmentColumnId> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        lock (gate)
        {
            long released = 0;
            foreach (Entry entry in entries.Values)
            {
                released += entry.Reader.Release(kept);
            }

            return released;
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
            long released = GiveBackColumns(kept, charge, spared: null);

            // Values derived from rows go only after every column: they are a fraction of the columns they spare a query
            // from reading, and derived again only at that cost (SegmentReaderV1.DerivedRows).
            foreach (Entry entry in entries.Values)
            {
                if (charge - released <= budgetBytes)
                {
                    break;
                }

                released += entry.Reader.ReleaseDerived();
            }

            return released;
        }
    }

    /// <summary>
    /// Keeps the budget while a query reads: told that <paramref name="reader"/>, cached, came to hold more, has every
    /// other cached reader give back every column but those every projection reads while the charge passes the budget.
    /// The reader that grew keeps what it holds, which its query is reading, and what readers derived from their rows
    /// stays until a trim, when the query's lease ends; a reader passed is read again when a query next needs it
    /// (revision 876).
    /// </summary>
    private void Grown(SegmentReaderV1 reader)
    {
        lock (gate)
        {
            _ = GiveBackColumns(resident, Charge(), reader);
        }
    }

    /// <summary>
    /// Has the cached readers give back every column but <paramref name="kept"/>, a reader at a time while
    /// <paramref name="charge"/> less what they released passes the budget, sparing <paramref name="spared"/>, and
    /// returns the bytes released. The readers give back in the order they were cached, the oldest segments first: a
    /// live view reads its newest most. Called under the gate.
    /// </summary>
    private long GiveBackColumns(IReadOnlySet<SegmentColumnId> kept, long charge, SegmentReaderV1? spared)
    {
        long released = 0;
        foreach (Entry entry in entries.Values)
        {
            if (charge - released <= budgetBytes)
            {
                break;
            }

            if (!ReferenceEquals(entry.Reader, spared))
            {
                released += entry.Reader.Release(kept);
            }
        }

        return released;
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
        entry.Reader.Grew = null;
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
