using System.Text;
using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class CanonicalImportTests
{
    private static readonly CaptureId Capture = new(Guid.Parse("11111111-2222-4333-8444-555555555555"));
    private static readonly ClockId Clock = new(Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa"));
    private static readonly Guid Provider = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact(DisplayName = "I2: the same bytes under a different retained-evidence policy are a different import")]
    public void RetainedEvidencePolicyIsPartOfImportIdentity()
    {
        ImportSourceIdentity source = ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, [1, 2, 3, 4]);

        ImportIdentity metadataOnly = ImportIdentity.Create(source, RetainedEvidencePolicy.MetadataOnly);
        ImportIdentity withContent = ImportIdentity.Create(source, RetainedEvidencePolicy.ApprovedMetadataAndContent);
        ImportIdentity laterNormalizer = ImportIdentity.Create(source, RetainedEvidencePolicy.MetadataOnly, 2);

        Assert.Equal(metadataOnly.Digest, ImportIdentity.Create(source, RetainedEvidencePolicy.MetadataOnly).Digest);
        Assert.NotEqual(metadataOnly.Digest, withContent.Digest);
        Assert.NotEqual(metadataOnly.Digest, laterNormalizer.Digest);
        Assert.StartsWith("sha256:", metadataOnly.Digest, StringComparison.Ordinal);
        Assert.Equal(71, metadataOnly.Digest.Length);
    }

    [Fact(DisplayName = "I2: a standalone ETL's identity is its bytes and its kind, computed before it is read")]
    public void StandaloneEtlIdentityIsItsBytesAndKind()
    {
        byte[] content = Encoding.ASCII.GetBytes("an etl, as far as identity is concerned");

        ImportSourceIdentity etl = ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, content);
        ImportSourceIdentity sameBytesOtherKind = ImportSourceIdentity.Of(ImportSourceKind.JournalV1, content);
        using var stream = new MemoryStream(content);
        ImportSourceIdentity streamed = ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, stream);

        Assert.Equal(etl, streamed);
        Assert.Equal(content.Length, etl.Length);
        Assert.Equal(etl.ContentDigest, sameBytesOtherKind.ContentDigest);
        Assert.NotEqual(etl, sameBytesOtherKind);
        Assert.NotEqual(
            ImportIdentity.Create(etl, RetainedEvidencePolicy.MetadataOnly).Digest,
            ImportIdentity.Create(sameBytesOtherKind, RetainedEvidencePolicy.MetadataOnly).Digest);
    }

    [Fact(DisplayName = "I14: equal-time byte-identical records keep their multiplicity and gain no order")]
    public void EqualTimeIdenticalRecordsKeepTheirMultiplicity()
    {
        ImportSourceIdentity source = Source();
        using RecordSet forward = RecordSet.Of(
            Record(1, ticks: 500),
            Record(2, ticks: 500),
            Record(3, ticks: 500),
            Record(4, ticks: 900, processorNumber: 1));
        using RecordSet reversed = RecordSet.Of(
            Record(4, ticks: 900, processorNumber: 1),
            Record(3, ticks: 500),
            Record(2, ticks: 500),
            Record(1, ticks: 500));

        using CanonicalImportIndex first = Import(source, forward);
        using CanonicalImportIndex second = Import(source, reversed);

        ImportedRecordEntry[] entries = [.. first.Entries];
        Assert.Equal(ImportIdentityBasis.CanonicalKeyWithOccurrence, first.Summary.Basis);
        Assert.Equal(4, first.Summary.RecordCount);
        Assert.Equal(2, first.Summary.DistinctFactCount);
        Assert.Equal(3, first.Summary.MaximumObservedMultiplicity);
        Assert.Equal([0u, 1u, 2u, 0u], [.. entries.Select(entry => entry.OccurrenceIndex)]);
        Assert.Equal([3u, 3u, 3u, 1u], [.. entries.Select(entry => entry.Multiplicity)]);

        // Delivery order changes nothing about what the source contained: the same facts, the same
        // multiplicity, the same canonical keys in the same canonical order.
        Assert.Equal(
            [.. entries.Select(entry => (entry.Key, entry.NativeTicks, entry.OccurrenceIndex, entry.Multiplicity))],
            [.. second.Entries.Select(entry => (entry.Key, entry.NativeTicks, entry.OccurrenceIndex, entry.Multiplicity))]);
        Assert.Equal(first.Summary.DistinctFactCount, second.Summary.DistinctFactCount);
    }

    [Fact(DisplayName = "I14: equal-time records from different processors are two records, not one seen twice")]
    public void EqualTimeRecordsFromDifferentProcessorsAreDistinct()
    {
        ImportSourceIdentity source = Source();
        using RecordSet records = RecordSet.Of(
            Record(1, ticks: 500, processorNumber: 0),
            Record(2, ticks: 500, processorNumber: 3));

        using CanonicalImportIndex index = Import(source, records);

        Assert.Equal(2, index.Summary.DistinctFactCount);
        Assert.Equal(1, index.Summary.MaximumObservedMultiplicity);
        Assert.Equal(2, index.Entries.Select(entry => entry.Key).Distinct().Count());
    }

    [Fact(DisplayName = "I14: a canonical key changes with every field the envelope carries")]
    public void CanonicalKeyCoversEveryCarriedField()
    {
        ImportSourceIdentity source = Source();
        using RecordEnvelopeV1 baseline = Record(1, ticks: 500);
        CanonicalRecordKey key = CanonicalRecordKeyBuilder.Create(source, baseline);

        using RecordEnvelopeV1 otherOrdinal = baseline with { RecordOrdinal = 99 };
        using RecordEnvelopeV1 otherTicks = Record(1, ticks: 501);
        using RecordEnvelopeV1 otherBody = Record(1, ticks: 500, body: [9, 9]);
        using RecordEnvelopeV1 otherThread = Record(1, ticks: 500, threadId: 777);
        using RecordEnvelopeV1 otherOmissionCount = baseline with { OmittedExtendedItemCount = 4 };

        // The delivery ordinal is not part of the key: for a standalone ETL it is not reproducible.
        Assert.Equal(key, CanonicalRecordKeyBuilder.Create(source, otherOrdinal));
        Assert.NotEqual(key, CanonicalRecordKeyBuilder.Create(source, otherTicks));
        Assert.NotEqual(key, CanonicalRecordKeyBuilder.Create(source, otherBody));
        Assert.NotEqual(key, CanonicalRecordKeyBuilder.Create(source, otherThread));
        Assert.NotEqual(key, CanonicalRecordKeyBuilder.Create(source, otherOmissionCount));
        Assert.NotEqual(
            key,
            CanonicalRecordKeyBuilder.Create(
                ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, [0xff]),
                baseline));
    }

    [Fact(DisplayName = "I7: importing a journal preserves its record identities and its stored order")]
    public void ImportingAJournalPreservesItsIdentitiesAndOrder()
    {
        string path = NewJournalPath();
        try
        {
            WriteJournal(path, [Record(7, ticks: 900), Record(8, ticks: 100), Record(9, ticks: 900)]);

            using CanonicalImportIndex index = CanonicalImporter.ImportJournalFile(
                path,
                RetainedEvidencePolicy.MetadataOnly);
            ImportedRecordEntry[] entries = [.. index.Entries];

            Assert.Equal(ImportIdentityBasis.PreservedRawRecordId, index.Summary.Basis);
            Assert.Equal([7ul, 8ul, 9ul], [.. entries.Select(entry => entry.SourceOrdinal)]);
            Assert.Equal([900L, 100L, 900L], [.. entries.Select(entry => entry.NativeTicks)]);
            Assert.All(entries, entry => Assert.Equal(1u, entry.StreamId));
            Assert.Equal(ImportSourceKind.JournalV1, index.Summary.Identity.Source.Kind);
            Assert.False(index.Spilled);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact(DisplayName = "I7: re-importing the same journal reuses its identity, clock and schema table")]
    public void ReimportingAJournalReusesItsIdentity()
    {
        string path = NewJournalPath();
        try
        {
            WriteJournal(path, [Record(1, ticks: 100), Record(2, ticks: 200)]);

            using CanonicalImportIndex first = CanonicalImporter.ImportJournalFile(
                path,
                RetainedEvidencePolicy.MetadataOnly);
            using CanonicalImportIndex second = CanonicalImporter.ImportJournalFile(
                path,
                RetainedEvidencePolicy.MetadataOnly);

            Assert.Equal(first.Summary.Identity.Digest, second.Summary.Identity.Digest);
            Assert.Equal(first.Summary.SchemaTableDigest, second.Summary.SchemaTableDigest);
            Assert.Equal(Clock, first.Summary.SourceClock.Id);
            Assert.Equal(first.Summary.SourceClock, second.Summary.SourceClock);
            Assert.Equal([.. first.Entries], [.. second.Entries]);
            Assert.StartsWith("sha256:", first.Summary.SchemaTableDigest, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact(DisplayName = "I8: a record naming a clock the source does not describe is refused, not converted")]
    public void ARecordOnAnotherClockIsRefused()
    {
        ImportSourceIdentity source = Source();
        using RecordSet records = RecordSet.Of(
            Record(1, ticks: 100),
            Record(2, ticks: 200) with { ClockId = new(Guid.Parse("12121212-3434-4545-8656-676767676767")) });

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => Import(source, records));

        Assert.Contains("assumed clock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I8: a record whose encoding differs from the source clock's is refused")]
    public void ARecordWithAnotherEncodingIsRefused()
    {
        ImportSourceIdentity source = Source();
        using RecordSet records = RecordSet.Of(
            Record(1, ticks: 100) with { TimestampEncoding = TimestampEncoding.FileTimeUtc });

        Assert.Throws<InvalidDataException>(() => Import(source, records));
    }

    [Theory(DisplayName = "I7: a reference the source's tables do not describe is refused")]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUndescribedReferenceIsRefused(bool schema)
    {
        ImportSourceIdentity source = Source();
        using RecordSet records = RecordSet.Of(schema
            ? Record(1, ticks: 100) with { SchemaReference = 42 }
            : Record(1, ticks: 100) with { AdmissionPolicyReference = 42 });

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => Import(source, records));

        Assert.Contains("this source's table does", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I14: an import that spills to disk produces exactly the index it would in memory")]
    public void SpillingProducesTheSameIndex()
    {
        ImportSourceIdentity source = Source();
        string spillDirectory = NewSpillDirectory();
        try
        {
            using RecordSet resident = Series();
            using RecordSet spilling = Series();
            using CanonicalImportIndex inMemory = Import(source, resident);
            using CanonicalImportIndex spilled = Import(
                source,
                spilling,
                new CanonicalImportOptions { MaximumEntriesInMemory = 4, SpillDirectory = spillDirectory });

            Assert.False(inMemory.Spilled);
            Assert.True(spilled.Spilled);
            Assert.True(spilled.Summary.SpillRunsWritten >= 4, $"{spilled.Summary.SpillRunsWritten} runs");
            Assert.Equal(spilled.Summary.SpillRunsWritten * 4L * ImportedRecordEntry.Size, spilled.Summary.SpilledBytes);
            Assert.Equal([.. inMemory.Entries], [.. spilled.Entries]);
            Assert.Equal(inMemory.Summary.RecordCount, spilled.Summary.RecordCount);
            Assert.Equal(inMemory.Summary.DistinctFactCount, spilled.Summary.DistinctFactCount);
            Assert.Equal(inMemory.Summary.MaximumObservedMultiplicity, spilled.Summary.MaximumObservedMultiplicity);
            Assert.Single(Directory.GetFiles(spillDirectory));

            spilled.Dispose();
            Assert.Empty(Directory.GetFiles(spillDirectory));
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "I14: a cancelled import leaves no spilled run behind")]
    public void ACancelledImportLeavesNoSpillBehind()
    {
        ImportSourceIdentity source = Source();
        string spillDirectory = NewSpillDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            using RecordSet records = Series();

            Assert.Throws<OperationCanceledException>(() => CanonicalImporter.Import(
                source,
                RetainedEvidencePolicy.MetadataOnly,
                ClockDescriptor(),
                Schemas(),
                Cancelling(records.Records, cancellation, after: 9),
                new CanonicalImportOptions { MaximumEntriesInMemory = 4, SpillDirectory = spillDirectory },
                cancellationToken: cancellation.Token));

            Assert.Empty(Directory.GetFiles(spillDirectory));
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "I14: an import bound that would be exceeded is a refusal, not a partial import")]
    [InlineData(2, 1_024, 1_048_576, "more than the 2 records")]
    [InlineData(64_000_000, 1, 1_048_576, "spilled runs it is allowed")]
    [InlineData(64_000_000, 1_024, 2, "share one instant and canonical key")]
    public void ExceedingABoundIsRefused(long maximumRecords, int maximumRuns, int maximumMultiplicity, string reason)
    {
        ImportSourceIdentity source = Source();
        string spillDirectory = NewSpillDirectory();
        try
        {
            using RecordSet records = RecordSet.Of([.. Enumerable.Range(1, 12)
                .Select(ordinal => Record((ulong)ordinal, ticks: 500))]);

            InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => Import(
                source,
                records,
                new CanonicalImportOptions
                {
                    MaximumEntriesInMemory = 4,
                    MaximumRecordCount = maximumRecords,
                    MaximumSpillRuns = maximumRuns,
                    MaximumMultiplicity = maximumMultiplicity,
                    SpillDirectory = spillDirectory,
                }));

            Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(spillDirectory));
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "I14: an import option outside its declared range is refused before anything is read")]
    public void AnOptionOutsideItsRangeIsRefused()
    {
        ImportSourceIdentity source = Source();
        using RecordSet records = RecordSet.Of(Record(1, ticks: 1));

        Assert.Throws<ArgumentException>(() =>
            Import(source, records, new CanonicalImportOptions { MaximumEntriesInMemory = 0 }));
        Assert.Throws<ArgumentException>(() =>
            Import(source, records, new CanonicalImportOptions { MaximumSpillRuns = 0 }));
        Assert.Throws<ArgumentException>(() =>
            Import(source, records, new CanonicalImportOptions { MaximumRecordCount = 0 }));
        Assert.Throws<ArgumentException>(() =>
            Import(source, records, new CanonicalImportOptions { MaximumMultiplicity = 0 }));
    }

    [Fact(DisplayName = "I7: a schema table digest changes with the descriptors, not with their order")]
    public void SchemaTableDigestFollowsTheDescriptors()
    {
        var forward = new JournalV1SchemaTable();
        _ = forward.Intern(Provider, 10, 2, "fingerprint-a");
        _ = forward.Intern(Provider, 11, 1, "fingerprint-b");
        _ = forward.InternPolicy("metadata-only-admitted-projection-v1");

        var reversed = new JournalV1SchemaTable();
        _ = reversed.Intern(Provider, 11, 1, "fingerprint-b");
        _ = reversed.Intern(Provider, 10, 2, "fingerprint-a");
        _ = reversed.InternPolicy("metadata-only-admitted-projection-v1");

        var changed = new JournalV1SchemaTable();
        _ = changed.Intern(Provider, 10, 2, "fingerprint-a");
        _ = changed.Intern(Provider, 11, 1, "fingerprint-changed");
        _ = changed.InternPolicy("metadata-only-admitted-projection-v1");

        Assert.Equal(CanonicalImporter.DigestOf(forward), CanonicalImporter.DigestOf(reversed));
        Assert.NotEqual(CanonicalImporter.DigestOf(forward), CanonicalImporter.DigestOf(changed));
    }

    private static IEnumerable<RecordEnvelopeV1> Cancelling(
        IEnumerable<RecordEnvelopeV1> records,
        CancellationTokenSource cancellation,
        int after)
    {
        int delivered = 0;
        foreach (RecordEnvelopeV1 record in records)
        {
            if (++delivered > after)
            {
                cancellation.Cancel();
            }

            yield return record;
        }
    }

    private static CanonicalImportIndex Import(
        ImportSourceIdentity source,
        RecordSet records,
        CanonicalImportOptions? options = null) =>
        CanonicalImporter.Import(
            source,
            RetainedEvidencePolicy.MetadataOnly,
            ClockDescriptor(),
            Schemas(),
            records.Records,
            options);

    private static RecordSet Series() => RecordSet.Of([.. Enumerable.Range(1, 17)
        .Select(ordinal => Record(
            (ulong)ordinal,
            ticks: 1_000 - (ordinal % 5 * 100),
            threadId: 2_000 + (ordinal % 3)))]);

    private static ImportSourceIdentity Source() =>
        ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, "a fixture etl"u8);

    private static JournalV1SchemaTable Schemas()
    {
        var table = new JournalV1SchemaTable();
        _ = table.Intern(Provider, 10, 2, "fingerprint-a");
        _ = table.InternPolicy("metadata-only-admitted-projection-v1");
        return table;
    }

    private static SourceClockDescriptor ClockDescriptor() => new(
        Clock,
        new HostId(Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff")),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        1_000_000,
        TimestampRounding.NearestEven,
        604_800_000_000_000);

    private static RecordEnvelopeV1 Record(
        ulong ordinal,
        long ticks,
        byte[]? body = null,
        ushort processorNumber = 0,
        int threadId = 2424) => new()
    {
        CaptureId = Capture,
        StreamId = 1,
        SourceEpoch = 1,
        RecordOrdinal = ordinal,
        Header = new(
            Provider,
            10,
            2,
            3,
            4,
            5,
            6,
            0x0102030405060708,
            0x0001,
            0x0002,
            4242,
            threadId,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444")),
        BufferContext = new(processorNumber, 99),
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        NativeTicks = ticks,
        PointerSize = 8,
        SchemaReference = 1,
        AdmissionPolicyReference = 1,
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
        Body = new()
        {
            Classification = BodyClassificationV1.ApprovedMetadata,
            Disposition = BodyDispositionV1.Retained,
            OriginalLength = (body ?? [1, 2, 3]).Length,
            Bytes = EnvelopeBuffer.CopyOf(body ?? [1, 2, 3]),
        },
    };

    private static void WriteJournal(string path, RecordEnvelopeV1[] records)
    {
        using var writer = JournalV1Writer.CreateNewFile(
            path,
            Capture,
            ClockDescriptor(),
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        writer.WriteSchemas(Schemas());
        foreach (RecordEnvelopeV1 record in records)
        {
            writer.Append(record);
        }

        writer.Complete();
    }

    private static string NewJournalPath() => Path.Combine(
        NewSpillDirectory(),
        "import-fixture.icatj");

    private static string NewSpillDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "InterCat.Storage.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Owns the envelopes one case builds, so a refusal never leaks a pooled buffer.</summary>
    private sealed class RecordSet : IDisposable
    {
        private RecordSet(IReadOnlyList<RecordEnvelopeV1> records) => Records = records;

        public IReadOnlyList<RecordEnvelopeV1> Records { get; }

        public static RecordSet Of(params RecordEnvelopeV1[] records) => new(records);

        public void Dispose()
        {
            foreach (RecordEnvelopeV1 record in Records)
            {
                record.Dispose();
            }
        }
    }
}
