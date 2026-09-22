using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>What gives an imported record its persistent identity.</summary>
public enum ImportIdentityBasis
{
    /// <summary>
    /// The source's own acquisition ordinals, preserved unchanged. A journal assigned them before
    /// parallel decode, so they are reproducible and replay keeps them (§18.4).
    /// </summary>
    PreservedRawRecordId = 1,

    /// <summary>
    /// The canonical key and an occurrence index. Callback delivery order is not reproducible for a
    /// standalone ETL, so identity comes from what the record carries and repeats are counted.
    /// </summary>
    CanonicalKeyWithOccurrence = 2,
}

/// <summary>
/// One imported record's index entry. Which field is its identity depends on the import's basis: a
/// journal's is the raw record ID this entry carries, and a standalone ETL's is the canonical key with
/// its occurrence index. The source ordinal is always kept, but for an ETL it is metadata about how the
/// run happened to read the file, never a reproducibility guarantee.
/// </summary>
public readonly record struct ImportedRecordEntry(
    CanonicalRecordKey Key,
    long NativeTicks,
    ulong SourceOrdinal,
    uint StreamId,
    uint SourceEpoch,
    uint OccurrenceIndex,
    uint Multiplicity)
{
    /// <summary>Fixed width, so a run of entries spills and merges without a length prefix.</summary>
    public const int Size = 64;

    public static ImportedRecordEntry Read(ReadOnlySpan<byte> source) => new(
        CanonicalRecordKey.Read(source),
        BinaryPrimitives.ReadInt64BigEndian(source[32..]),
        BinaryPrimitives.ReadUInt64BigEndian(source[40..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[48..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[52..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[56..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[60..]));

    public void Write(Span<byte> destination)
    {
        Key.Write(destination);
        BinaryPrimitives.WriteInt64BigEndian(destination[32..], NativeTicks);
        BinaryPrimitives.WriteUInt64BigEndian(destination[40..], SourceOrdinal);
        BinaryPrimitives.WriteUInt32BigEndian(destination[48..], StreamId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[52..], SourceEpoch);
        BinaryPrimitives.WriteUInt32BigEndian(destination[56..], OccurrenceIndex);
        BinaryPrimitives.WriteUInt32BigEndian(destination[60..], Multiplicity);
    }

    /// <summary>
    /// The canonical order: time, then the record's own content, then the ordinal that separates two
    /// entries nothing else distinguishes. That last tie-break makes the sort total and the run
    /// repeatable; it is not a claim that one of two indistinguishable records came first.
    /// </summary>
    internal static int Compare(ImportedRecordEntry left, ImportedRecordEntry right)
    {
        int order = left.NativeTicks.CompareTo(right.NativeTicks);
        if (order != 0)
        {
            return order;
        }

        order = left.Key.CompareTo(right.Key);
        if (order != 0)
        {
            return order;
        }

        order = left.StreamId.CompareTo(right.StreamId);
        if (order != 0)
        {
            return order;
        }

        order = left.SourceEpoch.CompareTo(right.SourceEpoch);
        return order != 0 ? order : left.SourceOrdinal.CompareTo(right.SourceOrdinal);
    }

    internal bool SameFactAs(ImportedRecordEntry other) =>
        NativeTicks == other.NativeTicks && Key == other.Key;
}

/// <summary>The declared bounds an import runs inside. Every one of them is a refusal, not a truncation.</summary>
public sealed record CanonicalImportOptions
{
    public static CanonicalImportOptions Default { get; } = new();

    /// <summary>How many index entries may be held before a run is sorted and spilled.</summary>
    public int MaximumEntriesInMemory { get; init; } = 262_144;

    /// <summary>How many spilled runs may exist at once before the import refuses.</summary>
    public int MaximumSpillRuns { get; init; } = 1_024;

    /// <summary>How many records one import may read.</summary>
    public long MaximumRecordCount { get; init; } = 64_000_000;

    /// <summary>How many indistinguishable records may share one canonical key and instant.</summary>
    public int MaximumMultiplicity { get; init; } = 1_048_576;

    /// <summary>Where spilled runs are written. The caller's temporary directory when null.</summary>
    public string? SpillDirectory { get; init; }

    internal string? Validate() =>
        MaximumEntriesInMemory is < 1 or > 16_777_216
            ? "An import holds between 1 and 16,777,216 entries in memory."
            : MaximumSpillRuns is < 1 or > 65_536
                ? "An import allows between 1 and 65,536 spilled runs."
                : MaximumRecordCount < 1
                    ? "An import reads at least one record."
                    : MaximumMultiplicity is < 1 or > 16_777_216
                        ? "An import allows between 1 and 16,777,216 indistinguishable repeats."
                        : null;
}

/// <summary>What one import measured about itself.</summary>
public sealed record CanonicalImportSummary(
    ImportIdentity Identity,
    ImportIdentityBasis Basis,
    SourceClockDescriptor SourceClock,
    string SchemaTableDigest,
    long RecordCount,
    long DistinctFactCount,
    int MaximumObservedMultiplicity,
    int SpillRunsWritten,
    long SpilledBytes);

/// <summary>
/// The result of one import: a summary and its index, streamed rather than returned as a list, so a
/// source larger than memory is read the same way as a small one.
/// </summary>
public sealed class CanonicalImportIndex : IDisposable
{
    private readonly IReadOnlyList<ImportedRecordEntry>? resident;
    private readonly string? path;
    private bool disposed;

    internal CanonicalImportIndex(
        CanonicalImportSummary summary,
        IReadOnlyList<ImportedRecordEntry>? resident,
        string? path)
    {
        Summary = summary;
        this.resident = resident;
        this.path = path;
    }

    public CanonicalImportSummary Summary { get; }

    /// <summary>True when the index was merged through a file rather than held in memory.</summary>
    public bool Spilled => path is not null;

    public IEnumerable<ImportedRecordEntry> Entries
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return resident ?? ReadFromFile();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (path is not null)
        {
            CanonicalImporter.Remove(path);
        }
    }

    private IEnumerable<ImportedRecordEntry> ReadFromFile()
    {
        using FileStream stream = File.OpenRead(path!);
        byte[] buffer = new byte[ImportedRecordEntry.Size];
        while (true)
        {
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0)
            {
                yield break;
            }

            if (read != buffer.Length)
            {
                throw new InvalidDataException("A merged import index ends inside an entry.");
            }

            yield return ImportedRecordEntry.Read(buffer);
        }
    }
}

/// <summary>
/// The canonical importer of §18.4. It never needs elevation and never decodes a payload: it reads
/// admitted envelopes, keys them, orders them and counts what repeats. A journal keeps its own record
/// identities; a standalone ETL gets canonical ones, because its delivery order is not reproducible.
/// </summary>
public static class CanonicalImporter
{
    public static CanonicalImportIndex Import(
        ImportSourceIdentity source,
        RetainedEvidencePolicy retainedEvidence,
        SourceClockDescriptor sourceClock,
        JournalV1SchemaTable schemas,
        IEnumerable<RecordEnvelopeV1> records,
        CanonicalImportOptions? options = null,
        uint normalizerContractVersion = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(records);
        CanonicalImportOptions bounds = options ?? CanonicalImportOptions.Default;
        string? problem = bounds.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        ImportIdentity identity = ImportIdentity.Create(source, retainedEvidence, normalizerContractVersion);
        ImportIdentityBasis basis = source.Kind == ImportSourceKind.JournalV1
            ? ImportIdentityBasis.PreservedRawRecordId
            : ImportIdentityBasis.CanonicalKeyWithOccurrence;
        string schemaDigest = DigestOf(schemas);
        var spills = new List<string>();
        long spilledBytes = 0;
        try
        {
            List<ImportedRecordEntry> run = new(Math.Min(bounds.MaximumEntriesInMemory, 4_096));
            List<ImportedRecordEntry>? ordered = basis == ImportIdentityBasis.PreservedRawRecordId
                ? new List<ImportedRecordEntry>()
                : null;
            long count = 0;
            foreach (RecordEnvelopeV1 record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireDescribed(record, sourceClock, schemas, count);
                if (++count > bounds.MaximumRecordCount)
                {
                    throw new InvalidDataException(
                        $"This source carries more than the {bounds.MaximumRecordCount} records one import "
                        + "reads. It is refused rather than imported in part.");
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
                    // A journal's stored order is its acquisition order, which is reproducible; keeping
                    // it is the whole point of preserving its identities.
                    ordered.Add(entry);
                    continue;
                }

                run.Add(entry);
                if (run.Count >= bounds.MaximumEntriesInMemory)
                {
                    spilledBytes += Spill(run, spills, bounds, cancellationToken);
                }
            }

            return ordered is not null
                ? Resident(identity, basis, sourceClock, schemaDigest, ordered, spills, spilledBytes)
                : Merge(
                    identity,
                    basis,
                    sourceClock,
                    schemaDigest,
                    run,
                    spills,
                    spilledBytes,
                    bounds,
                    cancellationToken);
        }
        catch
        {
            foreach (string spill in spills)
            {
                Remove(spill);
            }

            throw;
        }
    }

    /// <summary>
    /// Imports an InterCat journal. The file's bytes give the source identity before anything is read
    /// from them, and its own record identities, clock and schema table are what the import reuses.
    /// </summary>
    public static CanonicalImportIndex ImportJournalFile(
        string path,
        RetainedEvidencePolicy retainedEvidence,
        CanonicalImportOptions? options = null,
        uint normalizerContractVersion = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        ImportSourceIdentity source = ImportSourceIdentity.OfFile(ImportSourceKind.JournalV1, full);
        using JournalV1Contents contents = JournalV1Reader.ReadFile(full);
        return Import(
            source,
            retainedEvidence,
            contents.SourceClock,
            contents.Schemas,
            contents.Records,
            options,
            normalizerContractVersion,
            cancellationToken);
    }

    /// <summary>
    /// A deterministic identity for the schema and policy tables a source carries, so two imports of the
    /// same evidence agree that their records were admitted against the same descriptors.
    /// </summary>
    public static string DigestOf(JournalV1SchemaTable schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var canonical = new StringBuilder("InterCat.Import.SchemaTable.v1\n");
        foreach (JournalSchemaV1 schema in schemas.Schemas
            .OrderBy(entry => entry.ProviderId)
            .ThenBy(entry => entry.EventId)
            .ThenBy(entry => entry.Version))
        {
            canonical.Append(CultureInfo.InvariantCulture, $"s|{schema.ProviderId:D}|{schema.EventId}|{schema.Version}|{schema.Fingerprint}\n");
        }

        foreach (JournalPolicyV1 policy in schemas.Policies.OrderBy(entry => entry.PolicyId, StringComparer.Ordinal))
        {
            canonical.Append(CultureInfo.InvariantCulture, $"p|{policy.PolicyId}\n");
        }

        return CanonicalImportContract.Render(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leaked spill file must never replace the failure that produced it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A record whose clock the source does not describe, or whose schema and policy references the
    /// source's tables do not resolve, is refused. Importing it would mean reading it against something
    /// the evidence never named (I8, §18.3).
    /// </summary>
    private static void RequireDescribed(
        RecordEnvelopeV1 record,
        SourceClockDescriptor sourceClock,
        JournalV1SchemaTable schemas,
        long position)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ClockId != sourceClock.Id || record.TimestampEncoding != sourceClock.Encoding)
        {
            throw new InvalidDataException(
                $"Record {position} names clock {record.ClockId} encoded as {record.TimestampEncoding}, "
                + $"but this source describes {sourceClock.Id} encoded as {sourceClock.Encoding}. Its "
                + "reading is refused rather than read against an assumed clock.");
        }

        if (record.SchemaReference is { } schema && schemas.Find(schema) is null)
        {
            throw new InvalidDataException(
                $"Record {position} was admitted against schema {schema}, which this source's table does "
                + "not describe.");
        }

        if (schemas.FindPolicy(record.AdmissionPolicyReference) is null)
        {
            throw new InvalidDataException(
                $"Record {position} names admission policy {record.AdmissionPolicyReference}, which this "
                + "source's table does not describe.");
        }
    }

    private static CanonicalImportIndex Resident(
        ImportIdentity identity,
        ImportIdentityBasis basis,
        SourceClockDescriptor sourceClock,
        string schemaDigest,
        List<ImportedRecordEntry> entries,
        List<string> spills,
        long spilledBytes)
    {
        foreach (string spill in spills)
        {
            Remove(spill);
        }

        return new(
            new(
                identity,
                basis,
                sourceClock,
                schemaDigest,
                entries.Count,
                entries.Count,
                1,
                0,
                spilledBytes),
            entries,
            null);
    }

    private static long Spill(
        List<ImportedRecordEntry> run,
        List<string> spills,
        CanonicalImportOptions bounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (spills.Count >= bounds.MaximumSpillRuns)
        {
            throw new InvalidDataException(
                $"This import needs more than the {bounds.MaximumSpillRuns} spilled runs it is allowed. "
                + "It is refused rather than continued without a bound.");
        }

        run.Sort(ImportedRecordEntry.Compare);
        string path = NewSpillPath(bounds);
        using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[ImportedRecordEntry.Size];
            foreach (ImportedRecordEntry entry in run)
            {
                entry.Write(buffer);
                stream.Write(buffer);
            }
        }

        spills.Add(path);
        long bytes = (long)run.Count * ImportedRecordEntry.Size;
        run.Clear();
        return bytes;
    }

    private static string NewSpillPath(CanonicalImportOptions bounds)
    {
        string directory = bounds.SpillDirectory is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Path.GetTempPath(), "InterCat.Import");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"import-{Guid.NewGuid():N}.run");
    }

    private static CanonicalImportIndex Merge(
        ImportIdentity identity,
        ImportIdentityBasis basis,
        SourceClockDescriptor sourceClock,
        string schemaDigest,
        List<ImportedRecordEntry> tail,
        List<string> spills,
        long spilledBytes,
        CanonicalImportOptions bounds,
        CancellationToken cancellationToken)
    {
        tail.Sort(ImportedRecordEntry.Compare);
        using var readers = new SpillReaderSet(spills, tail);
        var counted = new MultiplicityWriter(bounds, spills.Count > 0 ? NewSpillPath(bounds) : null);
        try
        {
            while (readers.TryTake(out ImportedRecordEntry entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                counted.Add(entry);
            }

            counted.Complete();
            return new(
                new(
                    identity,
                    basis,
                    sourceClock,
                    schemaDigest,
                    counted.RecordCount,
                    counted.DistinctFactCount,
                    counted.MaximumMultiplicity,
                    spills.Count,
                    spilledBytes),
                counted.Resident,
                counted.Path);
        }
        catch
        {
            counted.Abandon();
            throw;
        }
        finally
        {
            readers.Dispose();
            foreach (string spill in spills)
            {
                Remove(spill);
            }
        }
    }

    /// <summary>
    /// Merges the sorted runs. Entries that share an instant and a canonical key are indistinguishable:
    /// they are counted and enumerated, and no ordering is invented between them (§18.4).
    /// </summary>
    private sealed class MultiplicityWriter(CanonicalImportOptions bounds, string? path) : IDisposable
    {
        private readonly List<ImportedRecordEntry> group = [];
        private readonly List<ImportedRecordEntry>? resident = path is null ? [] : null;
        private readonly FileStream? stream = path is null
            ? null
            : new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);

        private readonly byte[] buffer = new byte[ImportedRecordEntry.Size];
        private bool abandoned;

        public long RecordCount { get; private set; }

        public long DistinctFactCount { get; private set; }

        public int MaximumMultiplicity { get; private set; }

        public string? Path => abandoned ? null : path;

        public IReadOnlyList<ImportedRecordEntry>? Resident => resident;

        public void Add(ImportedRecordEntry entry)
        {
            if (group.Count > 0 && !group[0].SameFactAs(entry))
            {
                Flush();
            }

            if (group.Count >= bounds.MaximumMultiplicity)
            {
                throw new InvalidDataException(
                    $"More than {bounds.MaximumMultiplicity} records share one instant and canonical key. "
                    + "The import is refused rather than counting an unbounded repeat.");
            }

            group.Add(entry);
        }

        public void Complete()
        {
            Flush();
            stream?.Flush();
            Dispose();
        }

        public void Abandon()
        {
            Dispose();
            abandoned = true;
            if (path is not null)
            {
                Remove(path);
            }
        }

        public void Dispose() => stream?.Dispose();

        private void Flush()
        {
            if (group.Count == 0)
            {
                return;
            }

            uint multiplicity = (uint)group.Count;
            MaximumMultiplicity = Math.Max(MaximumMultiplicity, group.Count);
            DistinctFactCount++;
            for (int occurrence = 0; occurrence < group.Count; occurrence++)
            {
                ImportedRecordEntry counted = group[occurrence] with
                {
                    OccurrenceIndex = (uint)occurrence,
                    Multiplicity = multiplicity,
                };
                RecordCount++;
                if (resident is not null)
                {
                    resident.Add(counted);
                }
                else
                {
                    counted.Write(buffer);
                    stream!.Write(buffer);
                }
            }

            group.Clear();
        }
    }

    /// <summary>A k-way merge over the spilled runs and the final in-memory run.</summary>
    private sealed class SpillReaderSet : IDisposable
    {
        private readonly List<SpillReader> readers = [];
        private readonly List<ImportedRecordEntry> tail;
        private int tailPosition;

        public SpillReaderSet(IReadOnlyList<string> spills, List<ImportedRecordEntry> tail)
        {
            this.tail = tail;
            try
            {
                foreach (string spill in spills)
                {
                    readers.Add(new(spill));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool TryTake(out ImportedRecordEntry entry)
        {
            int best = -1;
            bool fromTail = tailPosition < tail.Count;
            entry = fromTail ? tail[tailPosition] : default;
            bool any = fromTail;
            for (int index = 0; index < readers.Count; index++)
            {
                if (readers[index].Current is not { } candidate)
                {
                    continue;
                }

                if (!any || ImportedRecordEntry.Compare(candidate, entry) < 0)
                {
                    entry = candidate;
                    best = index;
                    any = true;
                }
            }

            if (!any)
            {
                return false;
            }

            if (best < 0)
            {
                tailPosition++;
            }
            else
            {
                readers[best].Advance();
            }

            return true;
        }

        public void Dispose()
        {
            foreach (SpillReader reader in readers)
            {
                reader.Dispose();
            }

            readers.Clear();
        }
    }

    private sealed class SpillReader : IDisposable
    {
        private readonly FileStream stream;
        private readonly byte[] buffer = new byte[ImportedRecordEntry.Size];

        public SpillReader(string path)
        {
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            Advance();
        }

        public ImportedRecordEntry? Current { get; private set; }

        public void Advance()
        {
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0)
            {
                Current = null;
                return;
            }

            Current = read == buffer.Length
                ? ImportedRecordEntry.Read(buffer)
                : throw new InvalidDataException("A spilled import run ends inside an entry.");
        }

        public void Dispose() => stream.Dispose();
    }
}
