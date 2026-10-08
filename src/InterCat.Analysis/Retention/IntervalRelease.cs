using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>Why releasing a session's records read before a boundary would give up nothing (ADR-043).</summary>
public enum IntervalReleaseObstacle
{
    /// <summary>Something would be released.</summary>
    None = 0,

    /// <summary>The session names no admitted journal, so it has no recorded interval to give up.</summary>
    NoJournal = 1,

    /// <summary>
    /// No unit but the newest that holds records reads at any session time, and a release keeps the newest: a single
    /// journal written as one batch, or a recording of one chunk.
    /// </summary>
    NoReleasableUnit = 2,

    /// <summary>The oldest unit a release could give up holds a record read at or after the boundary.</summary>
    NothingBefore = 3,

    /// <summary>Content is kept beside a single journal, whose first batches would be rewritten from under it.</summary>
    ContentBesideJournal = 4,

    /// <summary>
    /// Every row before the boundary that a release could give up is kept as identity evidence - the rows an earlier
    /// release kept - and no unit of the journal lies wholly before it.
    /// </summary>
    AllKept = 5,
}

/// <summary>
/// What releasing a session's records read before a boundary gives up and keeps (ADR-043), measured before anything is
/// written. The unit of release is the journal's: a recording's chunk, or a batch of a single journal, given up whole with
/// every row derived from its records but the rows kept as identity evidence. Session times are nanoseconds.
/// </summary>
public sealed record IntervalReleasePreview
{
    public required long Generation { get; init; }

    /// <summary>The boundary asked for: records read before it are released where their units allow.</summary>
    public required long RequestedNanoseconds { get; init; }

    /// <summary>
    /// The boundary the release achieves: one nanosecond after the latest record it releases, so every record read at or
    /// after it is retained. Null when nothing is released.
    /// </summary>
    public long? BoundaryNanoseconds { get; init; }

    /// <summary>Whether the units are a recording's chunks rather than a single journal's batches.</summary>
    public required bool Chunks { get; init; }

    /// <summary>How many units the journal holds, and how many of them the release gives up, oldest first.</summary>
    public required int Units { get; init; }

    public required int ReleasedUnits { get; init; }

    /// <summary>The chunks given up, oldest first; empty for a single journal's batches.</summary>
    public required IReadOnlyList<string> ReleasedChunks { get; init; }

    public required long Records { get; init; }

    public required long ReleasedRecords { get; init; }

    /// <summary>The observation rows the generation holds.</summary>
    public required long Rows { get; init; }

    /// <summary>The rows of released records given up.</summary>
    public required long ReleasedRows { get; init; }

    /// <summary>The rows of released records kept as the identity evidence of the processes and connections after them.</summary>
    public required long KeptRows { get; init; }

    /// <summary>Rows whose records an earlier journal release gave up; the oldest a release can take, given up or kept.</summary>
    public required long RowsWithoutRecords { get; init; }

    /// <summary>The smallest boundary that releases anything, or null when none does.</summary>
    public long? EarliestReleasingNanoseconds { get; init; }

    /// <summary>The smallest boundary that releases all a release can - every unit but the newest that holds records.</summary>
    public long? MostReleasingNanoseconds { get; init; }

    /// <summary>The derived files the release replaces: every segment and dictionary of a publication holding a row it gives up.</summary>
    public required IReadOnlyList<string> ReplacedFiles { get; init; }

    public required IntervalReleaseObstacle Obstacle { get; init; }

    public bool ReleasesAnything => Obstacle == IntervalReleaseObstacle.None;

    /// <summary>What a release gives up whole: a chunk of a recording, or a batch of a single journal.</summary>
    public string ReleaseUnit => Chunks ? "chunk" : "batch";
}

/// <summary>What an interval release measured, and the retention generation it published.</summary>
public sealed record IntervalReleaseResult(IntervalReleasePreview Preview, RetentionOutcome Retention);

/// <summary>
/// Releases a session's oldest interval (ADR-043, store-v1 §8): the leading units of its admitted journal whose records
/// all read before a boundary, with every row derived from them, except the rows later records rest on for their
/// identities - the lifecycle records of each process a retained record belongs to, the first record of one no lifecycle
/// record names, and the lifecycle, first record and holders' first records of each connection a retained record names.
/// Every retained record therefore binds to the same process instance, at the same strength, and has the same other end
/// and channel key as before, while the interval before the boundary holds only what was kept. Derived files holding a
/// released row are rewritten with every other row unchanged, and indexes go with them.
/// </summary>
public static class IntervalRelease
{
    /// <summary>Measures what releasing the records read before <paramref name="boundaryNanoseconds"/> would give up. Nothing is written.</summary>
    public static IntervalReleasePreview Preview(
        SessionStore store,
        long boundaryNanoseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        return Plan(store, lease.Manifest, boundaryNanoseconds, cancellationToken).Preview;
    }

    /// <summary>
    /// Releases the records read before <paramref name="boundaryNanoseconds"/> where their units allow, and publishes the
    /// retention generation. The rows are read under a lease, released before publication so the files they came from can
    /// go at once unless another reader holds them (I18). Refused, with nothing published, when nothing would be released.
    /// </summary>
    public static IntervalReleaseResult Release(
        SessionStore store,
        long boundaryNanoseconds,
        string reason,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null,
        DerivedGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DerivedGenerationBuilder? builder = null;
        StoreStagingFile? retainedJournal = null;
        try
        {
            ReleasePlan plan;
            using (EvidenceLease lease = store.AcquireLease())
            {
                plan = Plan(store, lease.Manifest, boundaryNanoseconds, cancellationToken);
                if (!plan.Preview.ReleasesAnything)
                {
                    throw new InvalidOperationException(Refusal(plan.Preview) + " Nothing was published.");
                }

                builder = Rewrite(store, lease.Manifest, plan, options, cancellationToken);
            }

            IntervalReleasePreview preview = plan.Preview;
            IntervalJournalRelease journal;
            if (preview.ReleasedUnits == 0)
            {
                journal = new();
            }
            else if (preview.Chunks)
            {
                journal = new() { Chunks = preview.ReleasedChunks, Records = preview.ReleasedRecords };
            }
            else
            {
                (retainedJournal, CommittedBoundary retainedBoundary) =
                    JournalRetention.StageRetainedSuffix(store, preview.ReleasedRecords, cancellationToken);
                journal = new() { RetainedJournal = retainedJournal, RetainedBoundary = retainedBoundary, Records = preview.ReleasedRecords };
            }

            var interval = new ReleasedInterval(preview.BoundaryNanoseconds!.Value, preview.ReleasedRows, preview.KeptRows);
            RetentionOutcome outcome = builder is null
                ? store.CommitIntervalRelease([], [], journal, interval, reason, preview.Generation, committedUtc, nowUtc: nowUtc)
                : builder.CompleteIntervalRelease(preview.ReplacedFiles, journal, interval, reason, committedUtc, nowUtc, cancellationToken);
            return new(preview, outcome);
        }
        finally
        {
            builder?.Dispose();
            retainedJournal?.Dispose();
        }
    }

    /// <summary>Why a preview releases nothing, in a person's words, without the closing that says nothing was published.</summary>
    public static string Refusal(IntervalReleasePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        string unit = preview.ReleaseUnit;
        return preview.Obstacle switch
        {
            IntervalReleaseObstacle.None => "This release gives something up.",
            IntervalReleaseObstacle.NoJournal =>
                "This session names no admitted journal, so it has no recorded interval to release.",
            IntervalReleaseObstacle.NoReleasableUnit =>
                $"No {unit} of this session but its newest holds a record read at a session time, and a release always keeps "
                + $"the newest {unit}, so no boundary releases anything.",
            IntervalReleaseObstacle.NothingBefore =>
                $"No record read before {Seconds(preview.RequestedNanoseconds)} can be released: its oldest {unit} holds records "
                + $"read later, and a {unit} is released whole. The earliest boundary that releases anything is "
                + $"{Seconds(preview.EarliestReleasingNanoseconds!.Value)}.",
            IntervalReleaseObstacle.ContentBesideJournal =>
                "This session keeps content beside its journal. Releasing the journal's first batches would leave content "
                + "whose records it no longer holds, so its content is released on its own first.",
            IntervalReleaseObstacle.AllKept =>
                $"Every row read before {Seconds(preview.RequestedNanoseconds)} that this session can still release is kept as "
                + $"the identity evidence of processes and connections after it, and no {unit} lies wholly before it.",
            _ => preview.Obstacle.ToString(),
        };
    }

    private static string Seconds(long nanoseconds) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", CultureInfo.CurrentCulture) + " s";

    /// <summary>
    /// What a release before <paramref name="boundary"/> takes and keeps. Three reads of the rows: their times, by the unit
    /// of their records; then, when anything is released, the derivations whose identities the kept rows preserve; then which
    /// publications hold a row given up. Holds a count per unit and the kept rows, never the released ones.
    /// </summary>
    private static ReleasePlan Plan(SessionStore store, SessionManifestV1 manifest, long boundary, CancellationToken cancellationToken)
    {
        (IReadOnlyList<JournalReleaseUnit> units, bool chunked) = JournalRetention.Units(store, manifest, cancellationToken);
        var slots = new UnitSlots(units);
        IReadOnlyList<string> observationNames = SessionSegments.Names(manifest);
        IReadOnlyList<string> fieldNames = SessionSegments.FieldNames(manifest);

        // Slot 0 holds the rows whose records an earlier journal release gave up, older than every unit; slot s the rows
        // of unit s - 1.
        long[] rows = new long[units.Count + 1];
        long?[] latest = new long?[units.Count + 1];
        foreach (string name in observationNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
            SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
            SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
            for (int row = 0; row < segment.RowCount; row++)
            {
                int slot = slots.Of(ordinals.UnsignedAt(row)!.Value);
                if (slot < 0)
                {
                    throw new InvalidDataException(
                        $"A row of '{name}' derives from record {ordinals.UnsignedAt(row)!.Value}, which no journal of generation "
                        + $"{manifest.Generation} holds and no earlier release gave up. Nothing was released.");
                }

                rows[slot]++;
                if (times.SignedAt(row) is { } at && (latest[slot] is not { } known || at > known))
                {
                    latest[slot] = at;
                }
            }
        }

        // A release keeps the newest unit that holds records, and every unit after it: a recording's committed boundary
        // names its newest chunk, which a release therefore never reaches.
        int releasable = 1;
        for (int index = 0; index < units.Count && units.Skip(index + 1).Any(unit => unit.Records > 0); index++)
        {
            releasable = index + 2;
        }

        long? earliest = null;
        long? most = null;
        long? running = null;
        long? latestReleased = null;
        int taken = 0;
        bool blocked = false;
        for (int slot = 0; slot < releasable; slot++)
        {
            running = Max(running, latest[slot]);
            if (running is { } reached)
            {
                earliest ??= reached + 1;
                most = reached + 1;
            }

            blocked |= latest[slot] >= boundary;
            if (!blocked)
            {
                latestReleased = Max(latestReleased, latest[slot]);
                taken = slot + 1;
            }
        }

        IntervalReleaseObstacle obstacle =
            units.Count == 0 ? IntervalReleaseObstacle.NoJournal
            : earliest is null ? IntervalReleaseObstacle.NoReleasableUnit
            : latestReleased is null ? IntervalReleaseObstacle.NothingBefore
            : !chunked && taken > 1 && manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
                ? IntervalReleaseObstacle.ContentBesideJournal
                : IntervalReleaseObstacle.None;
        int releasedUnits = obstacle == IntervalReleaseObstacle.None ? Math.Max(0, taken - 1) : 0;
        var preview = new IntervalReleasePreview
        {
            Generation = manifest.Generation,
            RequestedNanoseconds = boundary,
            Chunks = chunked,
            Units = units.Count,
            ReleasedUnits = releasedUnits,
            ReleasedChunks = chunked ? [.. units.Take(releasedUnits).Select(unit => unit.Journal)] : [],
            Records = units.Sum(unit => unit.Records),
            ReleasedRecords = units.Take(releasedUnits).Sum(unit => unit.Records),
            Rows = rows.Sum(),
            ReleasedRows = 0,
            KeptRows = 0,
            RowsWithoutRecords = rows[0],
            EarliestReleasingNanoseconds = earliest,
            MostReleasingNanoseconds = most,
            ReplacedFiles = [],
            Obstacle = obstacle,
        };
        if (obstacle != IntervalReleaseObstacle.None)
        {
            return new(preview, slots, 0, [], []);
        }

        bool Released(ulong ordinal) => slots.Of(ordinal) is var slot && slot >= 0 && slot < taken;
        HashSet<RowAddress> kept = Keep(store, manifest, observationNames, fieldNames, Released, cancellationToken);
        (List<Publication> replaced, long given, long keptFound) =
            Replaced(store, manifest, observationNames, fieldNames, kept, Released, cancellationToken);
        if (keptFound != kept.Count)
        {
            throw new InvalidDataException(
                $"An interval release found {keptFound} of the {kept.Count} rows it keeps. Nothing was released.");
        }

        if (releasedUnits == 0 && given == 0)
        {
            return new(preview with { KeptRows = keptFound, Obstacle = IntervalReleaseObstacle.AllKept }, slots, 0, [], []);
        }

        return new(
            preview with
            {
                BoundaryNanoseconds = latestReleased + 1,
                ReleasedRows = given,
                KeptRows = keptFound,
                ReplacedFiles = [.. replaced.SelectMany(publication => publication.Files)],
            },
            slots,
            taken,
            kept,
            replaced);
    }

    private static long? Max(long? left, long? right) =>
        left is { } a ? right is { } b ? Math.Max(a, b) : a : right;

    /// <summary>
    /// The released rows a release keeps: the identity evidence of every connection and process a retained row names
    /// (<see cref="TransportRelationIndex.KeepAcross"/>, <see cref="ProcessInstanceIndex.KeepAcross"/>), each derived from the
    /// whole generation, so what is kept is what the whole capture decided.
    /// </summary>
    private static HashSet<RowAddress> Keep(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<string> observationNames,
        IReadOnlyList<string> fieldNames,
        Func<ulong, bool> released,
        CancellationToken cancellationToken)
    {
        SegmentReaderV1[] segments = [.. observationNames.Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SegmentReaderV1[] fields = [.. fieldNames.Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This session's journal names no clock, so its processes cannot be derived.");
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
        TransportRelationIndex relations = TransportRelationIndex.Derive(segments, processes, cancellationToken);
        // A kept row is evidence like a retained one, so its own process and connection are kept too: a process's first
        // record can be a connection's, whose other end then stays as well. Each round only adds rows, so it ends.
        var kept = new HashSet<RowAddress>();
        int known;
        do
        {
            known = kept.Count;
            HashSet<int> owners = OwnersOfStaying(segments, released, kept, cancellationToken);
            relations.KeepAcross(segments, released, kept, owners, cancellationToken);
            processes.KeepAcross(owners, released, kept);
        }
        while (kept.Count > known);

        return kept;
    }

    /// <summary>The PIDs the rows that stay belong to: the retained rows, and the released ones already kept.</summary>
    private static HashSet<int> OwnersOfStaying(
        SegmentReaderV1[] segments,
        Func<ulong, bool> released,
        HashSet<RowAddress> kept,
        CancellationToken cancellationToken)
    {
        var owners = new HashSet<int>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOwners = new RecordOwnerColumns(segment);
            var addresses = new RowAddresses(segment);
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((!released(addresses.Ordinal(row)) || kept.Contains(addresses.At(row)))
                    && rowOwners.At(row, (Mechanism)mechanisms.UnsignedAt(row)!.Value) is { } owner)
                {
                    _ = owners.Add(owner);
                }
            }
        }

        return owners;
    }

    /// <summary>
    /// The publications - the segments and dictionaries one generation published - holding a row the release gives up, with
    /// how many observation rows it gives up and how many of the kept rows it found. A publication holding none is carried.
    /// </summary>
    private static (List<Publication> Replaced, long Given, long Kept) Replaced(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<string> observationNames,
        IReadOnlyList<string> fieldNames,
        HashSet<RowAddress> kept,
        Func<ulong, bool> released,
        CancellationToken cancellationToken)
    {
        var observations = new HashSet<string>(observationNames, StringComparer.OrdinalIgnoreCase);
        var fields = new HashSet<string>(fieldNames, StringComparer.OrdinalIgnoreCase);
        var files = new SortedDictionary<long, List<string>>();
        foreach (StoreDependency dependency in manifest.Dependencies.Where(dependency =>
            dependency.Kind is StoreDependencyKind.Segment or StoreDependencyKind.Dictionary))
        {
            long generation = SessionSegments.PublishingGeneration(dependency.Name)
                ?? throw new InvalidDataException(
                    $"'{dependency.Name}' is not named for the generation that published it, so the dictionaries its rows "
                    + "resolve through cannot be told. Nothing was released.");
            if (!files.TryGetValue(generation, out List<string>? names))
            {
                files[generation] = names = [];
            }

            names.Add(dependency.Name);
        }

        var replaced = new List<Publication>();
        long given = 0;
        long found = 0;
        foreach ((long generation, List<string> names) in files)
        {
            bool replace = false;
            foreach (string name in names.Order(StringComparer.Ordinal))
            {
                bool observation = observations.Contains(name);
                if (!observation && !fields.Contains(name))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                var addresses = new RowAddresses(segment);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    if (!released(addresses.Ordinal(row)))
                    {
                        continue;
                    }

                    if (kept.Contains(addresses.At(row)))
                    {
                        found += observation ? 1 : 0;
                        continue;
                    }

                    given += observation ? 1 : 0;
                    replace = true;
                }
            }

            if (replace)
            {
                replaced.Add(new(
                    generation,
                    [.. names.Where(observations.Contains).Order(StringComparer.Ordinal)],
                    [.. names.Where(fields.Contains).Order(StringComparer.Ordinal)],
                    [.. names.Order(StringComparer.Ordinal)]));
            }
        }

        return (replaced, given, found);
    }

    /// <summary>
    /// Adds every row the release keeps of the publications it replaces - the retained rows and the kept ones, unchanged - to
    /// a new generation's segments, publication by publication, so no new segment spans a publication left as it was. Null
    /// when no publication is replaced.
    /// </summary>
    private static DerivedGenerationBuilder? Rewrite(
        SessionStore store,
        SessionManifestV1 manifest,
        ReleasePlan plan,
        DerivedGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        DerivedGenerationBuilder? builder = null;
        try
        {
            foreach (Publication publication in plan.Replaced)
            {
                foreach (string name in publication.Observations)
                {
                    SegmentReaderV1 segment = Open(name);
                    var addresses = new RowAddresses(segment);
                    for (int row = 0; row < segment.RowCount; row++)
                    {
                        if (plan.Keeps(addresses, row))
                        {
                            builder!.AddRow(segment.Row(row));
                        }
                    }
                }

                foreach (string name in publication.Fields)
                {
                    SegmentReaderV1 segment = Open(name);
                    var addresses = new RowAddresses(segment);
                    for (int row = 0; row < segment.RowCount; row++)
                    {
                        if (plan.Keeps(addresses, row))
                        {
                            builder!.AddFieldRow(segment.FieldRow(row));
                        }
                    }
                }

                builder?.FlushSegment();
            }

            return builder;
        }
        catch
        {
            builder?.Dispose();
            throw;
        }

        // Every segment of a release is one capture's, on one clock, under one derivation.
        SegmentReaderV1 Open(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
            var identity = new SegmentIdentityV1
            {
                CaptureId = segment.CaptureId,
                ClockId = segment.ClockId,
                TimestampEncoding = segment.TimestampEncoding,
                Derivation = segment.Derivation,
            };
            builder ??= DerivedGenerationBuilder.BeginIntervalRelease(store, identity, manifest.Generation, options);
            if (builder.Identity != identity)
            {
                throw new InvalidDataException(
                    $"'{name}' belongs to another capture, clock or derivation than the segments before it. An interval "
                    + "release rewrites one derivation of one capture, so nothing was published.");
            }

            return segment;
        }
    }

    /// <summary>What a release takes: its preview, the units it gives up, the rows it keeps of them, and the publications it rewrites.</summary>
    private sealed record ReleasePlan(
        IntervalReleasePreview Preview,
        UnitSlots Slots,
        int Taken,
        HashSet<RowAddress> Kept,
        List<Publication> Replaced)
    {
        /// <summary>Whether a row stays: a retained record's, or one kept as identity evidence.</summary>
        public bool Keeps(RowAddresses addresses, int row) =>
            Slots.Of(addresses.Ordinal(row)) is var slot && (slot < 0 || slot >= Taken || Kept.Contains(addresses.At(row)));
    }

    /// <summary>The segments and dictionaries one generation published: its observation and field segments, and all its files.</summary>
    private sealed record Publication(
        long Generation,
        IReadOnlyList<string> Observations,
        IReadOnlyList<string> Fields,
        IReadOnlyList<string> Files);

    /// <summary>
    /// The unit a record's number falls in, as a slot: 0 for a number older than every unit's, whose record an earlier
    /// release gave up, and s for unit s - 1. A number between two units' or past the last one's has none, -1.
    /// </summary>
    private sealed class UnitSlots
    {
        private readonly ulong[] firsts;
        private readonly ulong[] lasts;
        private readonly int[] slots;

        public UnitSlots(IReadOnlyList<JournalReleaseUnit> units)
        {
            var numbered = new List<(ulong First, ulong Last, int Slot)>();
            for (int index = 0; index < units.Count; index++)
            {
                if (units[index] is { FirstOrdinal: { } first, LastOrdinal: { } last })
                {
                    if (numbered.Count > 0 && first <= numbered[^1].Last)
                    {
                        throw new InvalidDataException(
                            $"The journal's records are not stored in the order they were numbered: record {first} follows "
                            + $"record {numbered[^1].Last}. Which rows derive from its oldest records cannot be told by their "
                            + "numbers, so nothing was released.");
                    }

                    numbered.Add((first, last, index + 1));
                }
            }

            firsts = [.. numbered.Select(unit => unit.First)];
            lasts = [.. numbered.Select(unit => unit.Last)];
            slots = [.. numbered.Select(unit => unit.Slot)];
        }

        public int Of(ulong ordinal)
        {
            if (firsts.Length == 0 || ordinal < firsts[0])
            {
                return 0;
            }

            int low = 0;
            int high = firsts.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (firsts[middle] <= ordinal)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return ordinal <= lasts[low] ? slots[low] : -1;
        }
    }

    /// <summary>The columns a row's address is read from, observation or field segment alike.</summary>
    private readonly ref struct RowAddresses(SegmentReaderV1 segment)
    {
        private readonly SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
        private readonly SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
        private readonly SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
        private readonly SegmentColumnSlice factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
        private readonly SegmentColumnSlice factLow = segment.Slice(SegmentColumnId.FactKeyLow);

        public ulong Ordinal(int row) => ordinals.UnsignedAt(row)!.Value;

        public RowAddress At(int row) => new(
            (uint)streams.UnsignedAt(row)!.Value,
            (uint)epochs.UnsignedAt(row)!.Value,
            ordinals.UnsignedAt(row)!.Value,
            new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
    }
}
