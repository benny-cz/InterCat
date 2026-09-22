using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// Builds a canonical import one record at a time. A replay that pushes records through a callback has
/// no enumerable to hand the importer, and buffering every envelope first would undo the bound the
/// importer exists to keep. The builder reads each envelope, keeps only its 64-byte index entry, and
/// lets the caller dispose the envelope immediately.
/// </summary>
public sealed class CanonicalImportBuilder : IDisposable
{
    private readonly ImportSourceIdentity source;
    private readonly SourceClockDescriptor sourceClock;
    private readonly JournalV1SchemaTable schemas;
    private readonly CanonicalImportOptions bounds;
    private readonly ImportIdentity identity;
    private readonly List<string> spills = [];
    private readonly List<ImportedRecordEntry> run;
    private readonly List<ImportedRecordEntry>? ordered;
    private long spilledBytes;
    private long count;
    private bool completed;
    private bool disposed;

    public CanonicalImportBuilder(
        ImportSourceIdentity source,
        RetainedEvidencePolicy retainedEvidence,
        SourceClockDescriptor sourceClock,
        JournalV1SchemaTable schemas,
        CanonicalImportOptions? options = null,
        uint normalizerContractVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        this.bounds = options ?? CanonicalImportOptions.Default;
        string? problem = this.bounds.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        this.source = source;
        this.sourceClock = sourceClock;
        this.schemas = schemas;
        identity = ImportIdentity.Create(source, retainedEvidence, normalizerContractVersion);
        Basis = source.Kind == ImportSourceKind.JournalV1
            ? ImportIdentityBasis.PreservedRawRecordId
            : ImportIdentityBasis.CanonicalKeyWithOccurrence;
        SchemaTableDigest = CanonicalImporter.DigestOf(schemas);
        run = new(Math.Min(this.bounds.MaximumEntriesInMemory, 4_096));
        ordered = Basis == ImportIdentityBasis.PreservedRawRecordId ? [] : null;
    }

    public ImportIdentity Identity => identity;

    public ImportIdentityBasis Basis { get; }

    public string SchemaTableDigest { get; }

    /// <summary>How many records have been read so far.</summary>
    public long RecordCount => count;

    /// <summary>
    /// Reads one record's index entry. The envelope is not retained: the caller may dispose it as soon
    /// as this returns.
    /// </summary>
    public void Add(RecordEnvelopeV1 record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        if (completed)
        {
            throw new InvalidOperationException("This import has already been completed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        CanonicalImporter.RequireSourceDescribes(record, sourceClock, schemas, count);
        if (++count > bounds.MaximumRecordCount)
        {
            throw new InvalidDataException(
                $"This source carries more than the {bounds.MaximumRecordCount} records one import reads. "
                + "It is refused rather than imported in part.");
        }

        var entry = new ImportedRecordEntry(
            CanonicalRecordKeyBuilder.Create(source, record),
            record.NativeTicks,
            record.RecordOrdinal,
            record.StreamId,
            record.SourceEpoch,
            0,
            1);
        if (ordered is not null)
        {
            // A journal's stored order is its acquisition order, which is reproducible; keeping it is
            // the whole point of preserving its identities.
            ordered.Add(entry);
            return;
        }

        run.Add(entry);
        if (run.Count >= bounds.MaximumEntriesInMemory)
        {
            spilledBytes += CanonicalImporter.SpillRun(run, spills, bounds, cancellationToken);
        }
    }

    /// <summary>Merges what was read into the final index. The builder is finished either way.</summary>
    public CanonicalImportIndex Complete(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            throw new InvalidOperationException("This import has already been completed.");
        }

        completed = true;
        try
        {
            return CanonicalImporter.Finish(
                identity,
                Basis,
                sourceClock,
                SchemaTableDigest,
                run,
                ordered,
                spills,
                spilledBytes,
                bounds,
                cancellationToken);
        }
        catch
        {
            DiscardSpills();
            throw;
        }
    }

    /// <summary>Abandoning an unfinished import removes everything it spilled.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!completed)
        {
            DiscardSpills();
        }
    }

    private void DiscardSpills()
    {
        foreach (string spill in spills)
        {
            CanonicalImporter.Remove(spill);
        }

        spills.Clear();
    }
}
