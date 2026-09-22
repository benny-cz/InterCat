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

    public string? Validate() =>
        RowsPerSegment is < 1 or > SegmentFormatV1.MaximumRowsPerSegment
            ? $"A segment holds between 1 and {SegmentFormatV1.MaximumRowsPerSegment} rows."
            : StagedBytesPerSegment is < 4_096 or > SegmentFormatV1.MaximumSegmentBytes
                ? $"A segment stages between 4,096 and {SegmentFormatV1.MaximumSegmentBytes} bytes."
                : MaximumSegments is < 1 or > 4_000
                    ? "A generation publishes between 1 and 4,000 segments."
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
    private readonly StoreStagingFile journalFile;
    private readonly JournalV1Writer journal;
    private readonly List<StoreStagingFile> staged = [];
    private readonly List<PendingSegment> segments = [];
    private SegmentWriterV1 open;
    private ushort nextDictionaryId = 1;
    private bool completed;
    private bool disposed;

    private DerivedGenerationBuilder(
        SessionStore store,
        SegmentIdentityV1 identity,
        DerivedGenerationOptions options,
        long generation,
        StoreStagingFile journalFile,
        JournalV1Writer journal)
    {
        this.store = store;
        this.identity = identity;
        this.options = options;
        this.generation = generation;
        this.journalFile = journalFile;
        this.journal = journal;
        open = new(identity, 0);
    }

    /// <summary>The journal this generation derives from. A caller appends the admitted envelopes it keyed.</summary>
    public JournalV1Writer Journal => journal;

    /// <summary>How many rows have been derived so far, across every segment of this generation.</summary>
    public long RowCount { get; private set; }

    public int SegmentCount => segments.Count + (open.RowCount > 0 ? 1 : 0);

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
                createdUtc);
            return new(store, identity, bounds, generation, journalFile, journal);
        }
        catch
        {
            journalFile.Dispose();
            throw;
        }
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

    /// <summary>
    /// Stages the open segment and its dictionaries. The journal's pending batch is flushed to the device
    /// first, so the evidence behind these rows is durable before the derivation that names it is staged.
    /// </summary>
    public void FlushSegment()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (open.RowCount == 0)
        {
            return;
        }

        if (segments.Count >= options.MaximumSegments)
        {
            throw new InvalidOperationException(
                $"This generation has staged {segments.Count} segments, its declared bound. It refuses rather "
                + "than publishing past it.");
        }

        journal.FlushBatch();
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

        cancellationToken.ThrowIfCancellationRequested();
        journal.Complete();
        FlushSegment();
        StoreDependency journalDependency = journalFile.Complete();
        var boundary = new CommittedBoundary(
            journalDependency.Name,
            journalDependency.LengthBytes,
            journal.RecordsWritten,
            journalDependency.Digest);

        // The journal is published first, so the rename order matches the order the sequence requires even
        // if a crash lands between two of them.
        List<StoreStagingFile> all = [journalFile, .. staged];
        StoreCommitResult commit = store.Commit(all, boundary, committedUtc, cancellationToken);
        completed = true;
        var published = new List<PublishedSegmentSummary>(segments.Count);
        foreach (PendingSegment segment in segments)
        {
            published.Add(new(
                segment.Name,
                segment.SegmentId,
                segment.RowCount,
                segment.MinNativeTicks,
                segment.MaxNativeTicks,
                segment.LengthBytes,
                segment.DictionaryNames));
        }

        return new(
            commit.Manifest,
            journalDependency.Name,
            journal.RecordsWritten,
            journalDependency.LengthBytes,
            published);
    }

    /// <summary>Abandoning an unpublished generation leaves only staging files, which the next open removes.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        journal.Dispose();
        journalFile.Dispose();
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
    /// <summary>The segments a generation names, in the order the manifest records them.</summary>
    public static IReadOnlyList<string> Names(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return
        [
            .. manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Segment)
                .Select(dependency => dependency.Name),
        ];
    }

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

        return SegmentReaderV1.Open(bytes, dictionaries);
    }

    /// <summary>The generation a published segment name belongs to.</summary>
    private static long GenerationOf(string segmentName)
    {
        // `seg-<generation:D10>-<ordinal:D4>.icats`. The name is parsed rather than trusted, so a dependency
        // that is not one of this format's names is refused instead of resolving to a plausible generation.
        return segmentName.Length == 25
            && segmentName.StartsWith("seg-", StringComparison.Ordinal)
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
