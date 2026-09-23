using System.Globalization;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>The bounds one derived generation runs inside. Each is a flush or a refusal, never a truncation.</summary>
public sealed record DerivedGenerationOptions
{
    public static DerivedGenerationOptions Default { get; } = new();

    /// <summary>§20.1's staged-row target: a segment is flushed when it reaches this many rows.</summary>
    public int RowsPerSegment { get; init; } = SegmentFormatV1.DefaultRowsPerSegment;

    /// <summary>§20.1's staged-byte target, whichever comes first.</summary>
    public long StagedBytesPerSegment { get; init; } = 64L * 1024 * 1024;

    /// <summary>How many segments one generation may publish before it refuses.</summary>
    public int MaximumSegments { get; init; } = 1_024;

    /// <summary>
    /// How many admitted records one journal batch holds. It is the granularity retention can act at, because
    /// a batch is the unit ADR-010's release works in: a journal written as one batch has no releasable
    /// boundary at all. Smaller batches cost one more frame header and checksum each.
    /// </summary>
    public int JournalBatchRecords { get; init; } = 4_096;

    public string? Validate() =>
        RowsPerSegment is < 1 or > SegmentFormatV1.MaximumRowsPerSegment
            ? $"A segment holds between 1 and {SegmentFormatV1.MaximumRowsPerSegment} rows."
            : StagedBytesPerSegment is < 4_096 or > SegmentFormatV1.MaximumSegmentBytes
                ? $"A segment stages between 4,096 and {SegmentFormatV1.MaximumSegmentBytes} bytes."
                : MaximumSegments is < 1 or > 4_000
                    ? "A generation publishes between 1 and 4,000 segments."
                    : JournalBatchRecords is < 1 or > 1_000_000
                        ? "A journal batch holds between 1 and 1,000,000 records."
                        : null;
}

/// <summary>What one published segment holds, as the generation that published it recorded it.</summary>
public sealed record PublishedSegmentSummary(
    string Name,
    Guid SegmentId,
    int RowCount,
    long MinNativeTicks,
    long MaxNativeTicks,
    long LengthBytes,
    IReadOnlyList<string> DictionaryNames);

/// <summary>What one derived generation published.</summary>
public sealed record DerivedGenerationResult(
    SessionManifestV1 Manifest,
    string JournalName,
    long JournalRecords,
    long JournalBytes,
    IReadOnlyList<PublishedSegmentSummary> Segments)
{
    public long RowCount => Segments.Sum(segment => (long)segment.RowCount);

    /// <summary>The `source-fields-v1` segments the generation published beside its observation segments.</summary>
    public IReadOnlyList<PublishedSegmentSummary> FieldSegments { get; init; } = [];

    public long FieldRowCount => FieldSegments.Sum(segment => (long)segment.RowCount);
}

/// <summary>
/// Publishes one generation the way §20.1's commit sequence requires: the admitted journal is written and
/// made durable first, the derived segments and their dictionaries are staged and flushed next, and only then
/// is a manifest written that names every one of them with its length and digest.
/// </summary>
/// <remarks>
/// Nothing here relaxes the ordering for convenience. A segment is staged only after the journal batch its
/// rows derive from has been flushed to the device, so a generation can never reference evidence that was
/// not durable when it was derived — which is the whole point of the committed boundary (ADR-010).
///
/// The generation is one atomic publication: an interruption anywhere before the pointer leaves the previous
/// generation current and this one's files unreferenced, and a caller that abandons the builder removes what
/// it staged.
/// </remarks>
public sealed class DerivedGenerationBuilder : IDisposable
{
    private readonly SessionStore store;
    private readonly SegmentIdentityV1 identity;
    private readonly DerivedGenerationOptions options;
    private readonly long generation;
    private readonly long? sourceGeneration;
    private readonly StoreStagingFile? journalFile;
    private readonly JournalV1Writer? journal;
    private readonly CommittedBoundary? retainedBoundary;
    private readonly bool compacting;
    private readonly List<StoreStagingFile> staged = [];
    private readonly List<PendingSegment> segments = [];
    private readonly List<PendingSegment> fieldSegments = [];
    private SegmentWriterV1 open;
    private SourceFieldWriterV1 openFields;
    private ushort nextDictionaryId = 1;
    private bool completed;
    private bool planStaged;
    private bool ledgerStaged;
    private bool disposed;

    private DerivedGenerationBuilder(
        SessionStore store,
        SegmentIdentityV1 identity,
        DerivedGenerationOptions options,
        long generation,
        StoreStagingFile? journalFile,
        JournalV1Writer? journal,
        CommittedBoundary? retainedBoundary = null,
        long? sourceGeneration = null,
        bool compacting = false)
    {
        this.store = store;
        this.identity = identity;
        this.options = options;
        this.generation = generation;
        this.journalFile = journalFile;
        this.journal = journal;
        this.retainedBoundary = retainedBoundary;
        this.sourceGeneration = sourceGeneration;
        this.compacting = compacting;
        open = new(identity, 0);
        openFields = new(identity, 0);
    }

    /// <summary>The journal this generation derives from. A caller appends the admitted envelopes it keyed.</summary>
    public JournalV1Writer Journal => journal
        ?? throw new InvalidOperationException("A re-derivation reads the retained journal; it does not write a new one.");

    /// <summary>The capture, clock and derivation every segment of this generation names.</summary>
    public SegmentIdentityV1 Identity => identity;

    /// <summary>How many rows have been derived so far, across every segment of this generation.</summary>
    public long RowCount { get; private set; }

    public int SegmentCount => segments.Count + (open.RowCount > 0 ? 1 : 0);

    /// <summary>
    /// Retains the exact compiled descriptor interpretation that reads this journal's metadata projections.
    /// It is an evidence companion, not a derived index: without it a later machine cannot safely re-derive.
    /// </summary>
    public void StageNormalizerPlan(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || planStaged)
        {
            throw new InvalidOperationException("A generation stages its normalizer plan once, before publication.");
        }

        if (bytes.Length is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "A retained normalizer plan is between 1 B and 1 MiB.");
        }

        Stage($"normalizer-plan-{generation:D10}.json", StoreDependencyKind.DerivationPlan, bytes.ToArray());
        planStaged = true;
    }

    /// <summary>
    /// Retains what the capture's sources could observe and what they lost (`contracts/coverage-v1.md`). Like the
    /// plan, it is evidence about the capture: the journal holds admitted records only, so nothing rebuilds it.
    /// </summary>
    public void StageCoverageLedger(CoverageLedgerV1 ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || ledgerStaged)
        {
            throw new InvalidOperationException("A generation stages its coverage ledger once, before publication.");
        }

        Stage(CoverageLedgerFileName(generation), StoreDependencyKind.CoverageLedger, ledger.Encode());
        ledgerStaged = true;
    }

    /// <summary>The published name of a generation's coverage ledger.</summary>
    public static string CoverageLedgerFileName(long generation) => $"coverage-{generation:D10}.json";

    /// <summary>
    /// Begins a generation. The journal is staged immediately, because §20.1's first step is making the
    /// admitted evidence durable and the derived files may not precede it.
    /// </summary>
    public static DerivedGenerationBuilder Begin(
        SessionStore store,
        SegmentIdentityV1 identity,
        SourceClockDescriptor sourceClock,
        DateTimeOffset createdUtc,
        DerivedGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        DerivedGenerationOptions bounds = options ?? DerivedGenerationOptions.Default;
        string? problem = bounds.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        if (sourceClock.Id != identity.ClockId)
        {
            throw new ArgumentException(
                "A generation's segments and its journal name one clock. Deriving rows against a clock the "
                + "journal does not describe would make their instants unreadable (I8).",
                nameof(sourceClock));
        }

        long generation = store.NextGeneration;
        StoreStagingFile journalFile = store.Stage(
            SegmentFormatV1.JournalFileName(generation),
            StoreDependencyKind.Journal);
        try
        {
            JournalV1Writer journal = JournalV1Writer.Create(
                journalFile.Content,
                identity.CaptureId,
                sourceClock,
                createdUtc,
                bounds.JournalBatchRecords);
            return new(store, identity, bounds, generation, journalFile, journal);
        }
        catch
        {
            journalFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Begins a replacement derivation over an already-published, verified journal. The caller holds an
    /// evidence lease and replays its records; no new journal is staged or copied. Publication keeps that
    /// exact boundary and replaces only derived files in the next generation.
    /// </summary>
    public static DerivedGenerationBuilder BeginFromJournal(
        SessionStore store,
        SegmentIdentityV1 identity,
        SourceClockDescriptor sourceClock,
        CommittedBoundary boundary,
        long sourceGeneration,
        DerivedGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(boundary);
        DerivedGenerationOptions bounds = options ?? DerivedGenerationOptions.Default;
        if (bounds.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        if (store.Current is not { } current
            || current.Generation != sourceGeneration
            || current.Boundary != boundary
            || !boundary.IsDeclared)
        {
            throw new InvalidOperationException(
                "A replacement derivation needs the current generation's exact committed journal boundary.");
        }

        if (identity.ClockId != sourceClock.Id)
        {
            throw new ArgumentException("The retained journal and the new segments must name one source clock.", nameof(identity));
        }

        return new(store, identity, bounds, store.NextGeneration, journalFile: null, journal: null, boundary,
            sourceGeneration);
    }

    /// <summary>
    /// Begins a compaction of the current generation's derived files (§20.1, ADR-026). The caller adds the rows of
    /// the files it replaces, unchanged, and publishes them with <see cref="CompleteCompaction"/>; no journal is
    /// staged, and the committed boundary is carried as it is.
    /// </summary>
    public static DerivedGenerationBuilder BeginCompaction(
        SessionStore store,
        SegmentIdentityV1 identity,
        long sourceGeneration,
        DerivedGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        DerivedGenerationOptions bounds = options ?? DerivedGenerationOptions.Default;
        if (bounds.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        if (store.Current?.Generation != sourceGeneration)
        {
            throw new InvalidOperationException("A compaction needs the current generation it was planned against.");
        }

        return new(store, identity, bounds, store.NextGeneration, journalFile: null, journal: null,
            retainedBoundary: null, sourceGeneration, compacting: true);
    }

    /// <summary>
    /// Stages whatever is still open and publishes the compaction generation: the new segments replace the derived
    /// files named, which the generation releases.
    /// </summary>
    public (DerivedGenerationResult Generation, RetentionOutcome Retention) CompleteCompaction(
        IReadOnlyList<string> replaced,
        string reason,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!compacting || completed)
        {
            throw new InvalidOperationException(
                completed ? "This generation has already been published." : "This builder is not compacting.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        FlushSegment();
        RetentionOutcome outcome = store.CommitCompaction(
            staged,
            replaced,
            reason,
            sourceGeneration!.Value,
            committedUtc,
            expectedGeneration: generation);
        completed = true;
        CommittedBoundary boundary = outcome.Manifest.Boundary;
        return (
            new(
                outcome.Manifest,
                boundary.JournalName,
                boundary.CommittedRecords,
                boundary.CommittedBytes,
                [.. segments.Select(Summarize)])
            {
                FieldSegments = [.. fieldSegments.Select(Summarize)],
            },
            outcome);
    }

    /// <summary>Adds one derived row. A full segment is flushed before the row that would pass its bound.</summary>
    public void AddRow(ObservationRowV1 row)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(row);
        if (completed)
        {
            throw new InvalidOperationException("This generation has already been published.");
        }

        if (open.RowCount >= options.RowsPerSegment || open.StagedBytes >= options.StagedBytesPerSegment)
        {
            FlushSegment();
        }

        open.Add(row);
        RowCount++;
    }

    /// <summary>How many source field rows have been derived so far, across every fields segment.</summary>
    public long FieldRowCount { get; private set; }

    /// <summary>
    /// Adds one source correlation or object field of an observation already added. It is staged into
    /// `source-fields-v1` segments that flush with the observation segments, so a generation publishes an
    /// observation's fields together with the observation.
    /// </summary>
    public void AddFieldRow(SourceFieldRowV1 row)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(row);
        if (completed)
        {
            throw new InvalidOperationException("This generation has already been published.");
        }

        if (openFields.RowCount >= options.RowsPerSegment || openFields.StagedBytes >= options.StagedBytesPerSegment)
        {
            FlushFieldSegment();
        }

        openFields.Add(row);
        FieldRowCount++;
    }

    /// <summary>
    /// Stages the open segment and its dictionaries. The journal's pending batch is flushed to the device
    /// first, so the evidence behind these rows is durable before the derivation that names it is staged.
    /// </summary>
    public void FlushSegment()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        FlushObservationSegment();

        // The fields segment follows its observations, so an observation segment keeps the dictionary ids it
        // would have had with no fields at all, and its bytes stay a function of its own rows.
        FlushFieldSegment();
    }

    private void FlushObservationSegment()
    {
        if (open.RowCount == 0)
        {
            return;
        }

        if (segments.Count + fieldSegments.Count >= options.MaximumSegments)
        {
            throw new InvalidOperationException(
                $"This generation has staged {segments.Count + fieldSegments.Count} segments, its declared bound. It "
                + "refuses rather than publishing past it.");
        }

        journal?.FlushBatch();
        FlushJournalToDevice();

        SegmentBuildResult built = open.Build(nextDictionaryId);
        var dictionaryNames = new List<string>(built.Dictionaries.Count);
        foreach (SegmentDictionaryV1 dictionary in built.Dictionaries)
        {
            string name = SegmentFormatV1.DictionaryFileName(generation, dictionary.DictionaryId);
            Stage(name, StoreDependencyKind.Dictionary, dictionary.Encode());
            dictionaryNames.Add(name);
            nextDictionaryId = (ushort)(dictionary.DictionaryId + 1);
        }

        string segmentName = SegmentFormatV1.SegmentFileName(generation, segments.Count);
        Stage(segmentName, StoreDependencyKind.Segment, built.Segment);
        segments.Add(new(
            segmentName,
            built.SegmentId,
            built.RowCount,
            built.MinNativeTicks,
            built.MaxNativeTicks,
            built.Segment.Length,
            dictionaryNames));
        open = new(identity, segments.Count);
    }

    /// <summary>Stages the open fields segment, after making the journal behind its rows durable.</summary>
    private void FlushFieldSegment()
    {
        if (openFields.RowCount == 0)
        {
            return;
        }

        if (segments.Count + fieldSegments.Count >= options.MaximumSegments)
        {
            throw new InvalidOperationException(
                $"This generation has staged {segments.Count + fieldSegments.Count} segments, its declared bound. It "
                + "refuses rather than publishing past it.");
        }

        journal?.FlushBatch();
        FlushJournalToDevice();
        SegmentBuildResult built = openFields.Build(nextDictionaryId);
        var dictionaryNames = new List<string>(built.Dictionaries.Count);
        foreach (SegmentDictionaryV1 dictionary in built.Dictionaries)
        {
            string name = SegmentFormatV1.DictionaryFileName(generation, dictionary.DictionaryId);
            Stage(name, StoreDependencyKind.Dictionary, dictionary.Encode());
            dictionaryNames.Add(name);
            nextDictionaryId = (ushort)(dictionary.DictionaryId + 1);
        }

        string segmentName = SegmentFormatV1.FieldSegmentFileName(generation, fieldSegments.Count);
        Stage(segmentName, StoreDependencyKind.Segment, built.Segment);
        fieldSegments.Add(new(
            segmentName,
            built.SegmentId,
            built.RowCount,
            built.MinNativeTicks,
            built.MaxNativeTicks,
            built.Segment.Length,
            dictionaryNames));
        openFields = new(identity, fieldSegments.Count);
    }

    /// <summary>
    /// Finishes the journal, stages whatever is still open, and publishes the generation with the committed
    /// boundary ADR-010 requires: which journal it derives from, how much of it was durable, and its digest.
    /// </summary>
    public DerivedGenerationResult Complete(DateTimeOffset committedUtc, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            throw new InvalidOperationException("This generation has already been published.");
        }

        if (compacting)
        {
            throw new InvalidOperationException("A compaction publishes through CompleteCompaction.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        journal?.Complete();
        FlushSegment();
        CommittedBoundary boundary;
        StoreCommitResult commit;
        if (retainedBoundary is { } retained)
        {
            boundary = retained;
            commit = store.CommitReplacingDerived(staged, sourceGeneration!.Value, boundary, committedUtc,
                expectedGeneration: generation, cancellationToken: cancellationToken);
        }
        else
        {
            StoreDependency journalDependency = journalFile!.Complete();
            boundary = new(
                journalDependency.Name,
                journalDependency.LengthBytes,
                journal!.RecordsWritten,
                journalDependency.Digest);

            // The journal is published first, so the rename order matches the commit sequence.
            List<StoreStagingFile> all = [journalFile, .. staged];
            commit = store.Commit(all, boundary, committedUtc,
                expectedGeneration: generation, cancellationToken: cancellationToken);
        }

        completed = true;
        return new(
            commit.Manifest,
            boundary.JournalName,
            boundary.CommittedRecords,
            boundary.CommittedBytes,
            [.. segments.Select(Summarize)])
        {
            FieldSegments = [.. fieldSegments.Select(Summarize)],
        };
    }

    private static PublishedSegmentSummary Summarize(PendingSegment segment) => new(
        segment.Name,
        segment.SegmentId,
        segment.RowCount,
        segment.MinNativeTicks,
        segment.MaxNativeTicks,
        segment.LengthBytes,
        segment.DictionaryNames);

    /// <summary>Abandoning an unpublished generation leaves staging files for coordinated review.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        journal?.Dispose();
        journalFile?.Dispose();
        foreach (StoreStagingFile file in staged)
        {
            file.Dispose();
        }
    }

    /// <summary>
    /// Pushes the journal's written bytes to the device. The staging stream is opened write-through, so this
    /// is the barrier that makes §20.1's first step true rather than nearly true.
    /// </summary>
    private void FlushJournalToDevice()
    {
        if (journalFile is null)
        {
            return;
        }

        if (journalFile.Content is FileStream file)
        {
            file.Flush(flushToDisk: true);
            return;
        }

        journalFile.Content.Flush();
    }

    private void Stage(string name, StoreDependencyKind kind, byte[] content)
    {
        StoreStagingFile file = store.Stage(name, kind);
        staged.Add(file);
        file.Content.Write(content);
        _ = file.Complete();
    }

    private sealed record PendingSegment(
        string Name,
        Guid SegmentId,
        int RowCount,
        long MinNativeTicks,
        long MaxNativeTicks,
        long LengthBytes,
        IReadOnlyList<string> DictionaryNames);
}

/// <summary>
/// Opens what a generation published. A segment names its dictionaries by id, and a generation publishes
/// each under a name that carries that id, so a reader resolves them from the manifest without a side index.
/// </summary>
public static class SessionSegments
{
    /// <summary>The `observation-v1` segments a generation names, in the order the manifest records them.</summary>
    public static IReadOnlyList<string> Names(SessionManifestV1 manifest) => NamesOf(manifest, SegmentTableId.ObservationV1);

    /// <summary>
    /// The `source-fields-v1` segments a generation names, in manifest order. A generation derived before the table
    /// existed names none, and a reader that needs the fields says so rather than inventing them.
    /// </summary>
    public static IReadOnlyList<string> FieldNames(SessionManifestV1 manifest) => NamesOf(manifest, SegmentTableId.SourceFieldsV1);

    private static IReadOnlyList<string> NamesOf(SessionManifestV1 manifest, SegmentTableId table)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return
        [
            .. manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Segment && TableOf(dependency.Name) == table)
                .Select(dependency => dependency.Name),
        ];
    }

    /// <summary>The table a published segment name holds, read from its prefix; null for a name of no segment table.</summary>
    private static SegmentTableId? TableOf(string name) =>
        name.StartsWith("seg-", StringComparison.Ordinal)
            ? SegmentTableId.ObservationV1
            : name.StartsWith("fld-", StringComparison.Ordinal)
                ? SegmentTableId.SourceFieldsV1
                : null;

    /// <summary>
    /// Opens one published segment together with the dictionaries its columns reference. A dictionary the
    /// generation does not name is a refusal: a segment whose codes cannot be resolved is not read with the
    /// codes shown as values.
    /// </summary>
    public static SegmentReaderV1 Open(IOwnedDirectory directory, SessionManifestV1 manifest, string segmentName)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        OwnedFileName.Require(segmentName, nameof(segmentName));
        StoreDependency segmentDependency = manifest.Dependencies.FirstOrDefault(dependency =>
            dependency.Name.Equals(segmentName, StringComparison.OrdinalIgnoreCase)
            && dependency.Kind == StoreDependencyKind.Segment)
            ?? throw new InvalidDataException(
                $"Generation {manifest.Generation} names no segment '{segmentName}'.");

        long generation = GenerationOf(segmentDependency.Name);
        byte[] bytes = ReadAll(directory, segmentDependency.Name);
        var dictionaries = new List<SegmentDictionaryV1>();
        foreach (ushort id in SegmentReaderV1.ReferencedDictionaryIds(bytes))
        {
            string name = SegmentFormatV1.DictionaryFileName(generation, id);
            if (!manifest.Dependencies.Any(dependency =>
                dependency.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && dependency.Kind == StoreDependencyKind.Dictionary))
            {
                throw new InvalidDataException(
                    $"Segment '{segmentName}' references dictionary {id}, which generation "
                    + $"{manifest.Generation} does not name as '{name}'.");
            }

            dictionaries.Add(SegmentDictionaryV1.Decode(ReadAll(directory, name)));
        }

        SegmentReaderV1 reader = SegmentReaderV1.Open(bytes, dictionaries);
        return reader.Table == TableOf(segmentDependency.Name)
            ? reader
            : throw new InvalidDataException(
                $"'{segmentName}' is named as a {TableOf(segmentDependency.Name)} segment and holds {reader.Table}.");
    }

    /// <summary>
    /// The coverage ledger a generation names, or null for a legacy generation that publishes none: every coverage
    /// such a generation is asked about is unknown (`contracts/coverage-v1.md` §1). Two ledgers are a refusal, because
    /// a reader could not say which one describes the capture.
    /// </summary>
    public static CoverageLedgerV1? CoverageLedger(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] ledgers =
        [
            .. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger),
        ];
        if (ledgers.Length > 1)
        {
            throw new InvalidDataException(
                $"Generation {manifest.Generation} names {ledgers.Length} coverage ledgers; one capture has one.");
        }

        if (ledgers.Length == 0)
        {
            return null;
        }

        if (ledgers[0].LengthBytes is < 1 or > CoverageLedgerV1.MaximumBytes)
        {
            throw new InvalidDataException("The coverage ledger is outside its 1 MiB bound.");
        }

        byte[] bytes = new byte[checked((int)ledgers[0].LengthBytes)];
        using (FileStream stream = directory.OpenOwnedFile(
            ledgers[0].Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            stream.ReadExactly(bytes);
        }

        return CoverageLedgerV1.Decode(bytes);
    }

    /// <summary>
    /// The source clock a generation's native readings are on, read from the journal its committed boundary
    /// names, or null when the generation names no journal. A reader needs it to turn native ticks into seconds,
    /// and it is read from the evidence rather than assumed: every segment carries only the clock's identity,
    /// and the journal is where that identity is described (I8, ADR-010).
    /// </summary>
    public static SourceClockDescriptor? SourceClock(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Boundary.IsDeclared)
        {
            return null;
        }

        StoreDependency journal = manifest.Dependencies.FirstOrDefault(dependency =>
            dependency.Name.Equals(manifest.Boundary.JournalName, StringComparison.OrdinalIgnoreCase)
            && dependency.Kind == StoreDependencyKind.Journal)
            ?? throw new InvalidDataException(
                $"Generation {manifest.Generation} derives from journal '{manifest.Boundary.JournalName}' and "
                + "does not name it as a dependency.");
        using FileStream stream = directory.OpenOwnedFile(
            journal.Name,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        return JournalV1Reader.ReadSourceClock(stream).Clock;
    }

    /// <summary>
    /// The generation that published a derived file, read from its name: `seg-`, `fld-` or `dict-` followed by the
    /// ten-digit generation. Null for any other name, such as a journal, a plan or a ledger.
    /// </summary>
    public static long? PublishingGeneration(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (TableOf(name) is not null)
        {
            return name.Length == 25 ? GenerationOf(name) : null;
        }

        return name.Length == 26
            && name.StartsWith("dict-", StringComparison.Ordinal)
            && name[15] == '-'
            && name.EndsWith(".icatd", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(5, 10), NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
            && generation >= 1
                ? generation
                : null;
    }

    /// <summary>The generation a published segment name belongs to.</summary>
    private static long GenerationOf(string segmentName)
    {
        // `seg-` or `fld-<generation:D10>-<ordinal:D4>.icats`. The name is parsed rather than trusted, so a
        // dependency that is not one of this format's names is refused instead of resolving to a plausible generation.
        return segmentName.Length == 25
            && TableOf(segmentName) is not null
            && segmentName[14] == '-'
            && segmentName.EndsWith(".icats", StringComparison.Ordinal)
            && long.TryParse(segmentName.AsSpan(4, 10), NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
            && generation >= 1
                ? generation
                : throw new InvalidDataException(
                    $"'{segmentName}' is not a segment-v1 published name, so the generation that published it "
                    + "cannot be read from it.");
    }

    private static byte[] ReadAll(IOwnedDirectory directory, string name)
    {
        using FileStream stream = directory.OpenOwnedFile(
            name,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        if (stream.Length > SegmentFormatV1.MaximumSegmentBytes)
        {
            throw new InvalidDataException(
                $"'{name}' is {stream.Length} bytes, past the {SegmentFormatV1.MaximumSegmentBytes} this "
                + "format holds.");
        }

        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
