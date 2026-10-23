using System.Globalization;
using System.Security.Cryptography;
using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// §20.1 and R11: a compaction copies each segment's columns as they are rather than reading its rows back whole, and
/// publishes exactly the segments and dictionaries writing every row again would.
/// </summary>
public sealed partial class SegmentCompactionTests
{
    private static readonly string[] Names = ["svchost.exe", "chrome.exe", @"\Device\Afd", "lsass.exe", "naïve-名前"];

    [Theory(DisplayName = "I15: compaction copies each segment's columns as they are, and publishes, byte for byte, the segments and dictionaries writing every row again would, flushing at the same rows and bytes")]
    [InlineData(3, 37, 6_000L)]
    [InlineData(11, 1_000, 9_000L)]
    [InlineData(20_261_023, 250_000, 64L * 1024 * 1024)]
    public void CompactionCopiesColumnsAsWritingRowsWould(int seed, int rowsPerSegment, long bytesPerSegment)
    {
        var generation = new DerivedGenerationOptions { RowsPerSegment = rowsPerSegment, StagedBytesPerSegment = bytesPerSegment };
        using var copied = new TemporarySession();
        using var written = new TemporarySession();
        foreach (TemporarySession session in new[] { copied, written })
        {
            var random = new Random(seed);
            ulong ordinal = 1;
            for (int unit = 0; unit < 9; unit++)
            {
                int records = 1 + random.Next(120);
                PublishVaried(session.Store, random, ordinal, records, sixes: unit % 3 == 1);
                ordinal += (ulong)records;
            }
        }

        // The units' segments are of both tables: every third holds an IPv6 address, and the rest are observation-v1.
        SessionManifestV1 before = copied.Store.Current!;
        Assert.Equal(
            [SegmentTableId.ObservationV1, SegmentTableId.ObservationV2],
            SessionSegments.Names(before).Select(name => SessionSegments.Open(copied.Store.Root, before, name).Table).Distinct().Order());
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) recorded = RowsOf(copied.Store, before);

        CompactionResult result = SegmentCompaction.Compact(copied.Store, Committed, generationOptions: generation)!;
        CompactByRows(written.Store, generation);
        Assert.Equal(DerivedFiles(written), DerivedFiles(copied));

        // And each is every row as it was recorded.
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) copies = RowsOf(copied.Store, copied.Store.Current!);
        Assert.Equal(recorded.Rows, copies.Rows);
        Assert.Equal(recorded.Fields, copies.Fields);
        Assert.Equal((result.Plan.CoalescedRows, result.Plan.CoalescedFieldRows), (result.Generation.RowCount, result.Generation.FieldRowCount));
        if (rowsPerSegment < 1_000)
        {
            // Bounds this small cut the copied segments within a source segment, by rows and by bytes.
            Assert.True(SessionSegments.Names(copied.Store.Current!).Count > 9);
        }
    }

    [Theory(DisplayName = "I15: compaction copies the names of a segment whose distinct names passed a dictionary's bytes, kept in its chunk, as writing its rows again would: into one segment that keeps them so too, and into segments cut by bytes among them")]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(6L * 1024 * 1024)]
    public void CompactionCopiesNamesKeptInTheChunk(long bytesPerSegment)
    {
        // Distinct names of 4,000 bytes each, 2,100 of them: past the 8 MiB a dictionary holds, so the segment keeps them in
        // its variable chunk, and a unit that size is coalesced only under a larger byte target.
        var options = new CompactionOptions { TargetBytes = 64L * 1024 * 1024 };
        var generation = new DerivedGenerationOptions { StagedBytesPerSegment = bytesPerSegment };
        static string Long(ulong ordinal) => ordinal.ToString("D8", CultureInfo.InvariantCulture) + new string('x', 3_992);
        using var copied = new TemporarySession();
        using var written = new TemporarySession();
        foreach (TemporarySession session in new[] { copied, written })
        {
            PublishChunk(session.Store, 1, 2_100, row => row with { ResourceName = Long(row.RawRecordOrdinal) });
            PublishChunk(session.Store, 2_101, 30, row => row with { ResourceName = row.RawRecordOrdinal % 2 == 0 ? "short" : null });
        }

        SessionManifestV1 before = copied.Store.Current!;
        SegmentReaderV1 chunked = SessionSegments.Open(copied.Store.Root, before, SessionSegments.Names(before)[0]);
        Assert.Equal(SegmentColumnEncoding.VariableReference, chunked.Column(SegmentColumnId.ResourceName)!.Encoding);

        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) recorded = RowsOf(copied.Store, before);
        _ = SegmentCompaction.Compact(copied.Store, Committed, options, generation)!;
        CompactByRows(written.Store, generation, options);
        Assert.Equal(DerivedFiles(written), DerivedFiles(copied));
        Assert.Equal(recorded.Rows, RowsOf(copied.Store, copied.Store.Current!).Rows);

        // Under the larger bound the names stay in one segment's chunk; under the smaller, each segment's are a dictionary's.
        SessionManifestV1 after = copied.Store.Current!;
        SegmentColumnEncoding[] encodings =
        [
            .. SessionSegments.Names(after)
                .Select(name => SessionSegments.Open(copied.Store.Root, after, name).Column(SegmentColumnId.ResourceName)!.Encoding),
        ];
        Assert.Equal(
            bytesPerSegment > 8L * 1024 * 1024 ? [SegmentColumnEncoding.VariableReference] : [.. Enumerable.Repeat(SegmentColumnEncoding.Dictionary, 2)],
            encodings);
    }

    [Fact(DisplayName = "I15: a segment's rows are copied only into a segment of its table, or of the table it extends, and only rows it holds")]
    public void ACopyTakesOnlyItsTablesRows()
    {
        using var session = new TemporarySession();
        PublishChunk(session.Store, 1, 10);
        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1 observations = SessionSegments.Open(session.Store.Root, manifest, Assert.Single(SessionSegments.Names(manifest)));
        SegmentReaderV1 fields = SessionSegments.Open(session.Store.Root, manifest, Assert.Single(SessionSegments.FieldNames(manifest)));
        Assert.Throws<ArgumentException>(() => new SegmentWriterV1(Identity, 0).AddRows(fields, 0, 1));
        Assert.Throws<ArgumentException>(() => new SourceFieldWriterV1(Identity, 0).AddRows(observations, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentWriterV1(Identity, 0).AddRows(observations, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentWriterV1(Identity, 0).AddRows(observations, -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentWriterV1(Identity, 0).AddRows(observations, 9, 2));
        var whole = new SegmentWriterV1(Identity, 0);
        whole.AddRows(observations, 0, 10);
        Assert.Equal(10, whole.RowCount);

        // A generation published takes no more rows.
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.BeginCompaction(session.Store, Identity, manifest.Generation);
        builder.AddRows(observations);
        builder.AddFieldRows(fields);
        _ = builder.CompleteCompaction(
            [.. manifest.Dependencies.Where(dependency => dependency.Kind is StoreDependencyKind.Segment or StoreDependencyKind.Dictionary)
                .Select(dependency => dependency.Name)],
            "Copied whole.",
            Committed);
        Assert.Equal("This generation has already been published.", Assert.Throws<InvalidOperationException>(() => builder.AddRows(observations)).Message);
        Assert.Equal("This generation has already been published.", Assert.Throws<InvalidOperationException>(() => builder.AddFieldRows(fields)).Message);
    }

    /// <summary>
    /// Compacts as <see cref="SegmentCompaction.Compact"/> plans and publishes, writing every row the planned segments hold
    /// again, one by one, from the rows their readers materialize.
    /// </summary>
    private static void CompactByRows(SessionStore store, DerivedGenerationOptions generation, CompactionOptions? options = null)
    {
        CompactionPlan plan = SegmentCompaction.Plan(store, options);
        SessionManifestV1 manifest = store.Current!;
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.BeginCompaction(store, Identity, manifest.Generation, generation);
        foreach (IReadOnlyList<CompactionUnit> run in plan.Runs)
        {
            foreach (CompactionUnit unit in run)
            {
                foreach (string name in unit.Files.Where(name => name.StartsWith("seg-", StringComparison.Ordinal)))
                {
                    SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                    for (int row = 0; row < segment.RowCount; row++)
                    {
                        builder.AddRow(segment.Row(row));
                    }
                }

                foreach (string name in unit.Files.Where(name => name.StartsWith("fld-", StringComparison.Ordinal)))
                {
                    SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                    for (int row = 0; row < segment.RowCount; row++)
                    {
                        builder.AddFieldRow(segment.FieldRow(row));
                    }
                }
            }

            builder.FlushSegment();
        }

        _ = builder.CompleteCompaction([.. plan.Coalesced.SelectMany(unit => unit.Files)], "Every row written again.", Committed);
    }

    /// <summary>The current generation's segments and dictionaries, by name, with the digest of each one's bytes.</summary>
    private static (string Name, string Digest)[] DerivedFiles(TemporarySession session) =>
    [
        .. session.Store.Current!.Dependencies
            .Where(dependency => dependency.Kind is StoreDependencyKind.Segment or StoreDependencyKind.Dictionary)
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
            .Select(dependency => (dependency.Name,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(session.Path, dependency.Name)))))),
    ];

    /// <summary>
    /// Publishes a unit of rows that differ in every way a column can: three schemas, names pooled, unique and absent,
    /// owners, activities and statuses present and absent, IPv4 endpoints and, where <paramref name="sixes"/>, now and then
    /// IPv6 ones, at least one; bytes measured and lost; each with a connection field and, now and then, a field of text
    /// or none, delivered in no order of their readings.
    /// </summary>
    private static void PublishVaried(SessionStore store, Random random, ulong firstOrdinal, int records, bool sixes)
    {
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(store, Identity, TestClock, Committed);
        var schemas = new JournalV1SchemaTable();
        uint schema = schemas.Intern(Provider, 10, 0, "sha256:" + new string('a', 64));
        uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
        builder.Journal.WriteSchemas(schemas);
        for (int index = 0; index < records; index++)
        {
            ulong ordinal = firstOrdinal + (ulong)index;
            long ticks = 1_000 + (100L * (long)ordinal) + random.Next(-250, 250);
            ushort eventId = (ushort)(10 + random.Next(3));
            string? name = random.Next(3) switch
            {
                0 => null,
                1 => Names[random.Next(Names.Length)],
                _ => string.Create(CultureInfo.InvariantCulture, $"unique-{ordinal}"),
            };
            ObservationRowV1 row = Row(ticks, ordinal) with
            {
                EventId = eventId,
                SchemaFingerprint = "sha256:" + new string((char)('a' + eventId - 10), 64),
                SessionRelativeTicks = random.Next(4) == 0 ? null : ticks * 100,
                OwnerProcessId = random.Next(3) == 0 ? null : 100 + random.Next(5),
                ActivityId = random.Next(2) == 0 ? null : new Guid(random.Next(), 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]),
                RelatedActivityId = random.Next(5) == 0 ? new Guid(random.Next(), 9, 8, [7, 6, 5, 4, 3, 2, 1, 0]) : null,
                SourceIdentifier = random.Next(6) == 0 ? new Guid(random.Next(), 3, 3, [3, 3, 3, 3, 3, 3, 3, 3]) : null,
                ResourceName = name,
                Markers = name is not null && random.Next(4) == 0 ? SegmentRowMarkers.ResourceNameTruncated : SegmentRowMarkers.None,
            };
            if (random.Next(4) == 0)
            {
                row = row with { StatusCode = random.Next(-5, 5), StatusAvailability = FieldAvailability.Present };
            }

            if (random.Next(5) == 0)
            {
                row = row with { ByteValue = null, ByteAvailability = FieldAvailability.EventLost };
            }

            int endpoints = sixes && index == records / 2 ? 0 : sixes ? random.Next(20) : 1 + random.Next(19);
            if (endpoints == 0)
            {
                row = row with
                {
                    EndpointAddressFamily = 6,
                    SourceEndpointAddressV6 = ((UInt128)(ulong)random.NextInt64() << 64) | (ulong)random.NextInt64(),
                    DestinationEndpointAddressV6 = random.Next(2) == 0 ? null : (UInt128)(ulong)random.NextInt64(),
                    SourceEndpointPort = (ushort)random.Next(1, 65_536),
                };
            }
            else if (endpoints < 12)
            {
                row = row with
                {
                    EndpointAddressFamily = 4,
                    SourceEndpointAddress = (uint)random.Next(),
                    DestinationEndpointAddress = random.Next(3) == 0 ? null : (uint)random.Next(),
                    SourceEndpointPort = (ushort)random.Next(1, 65_536),
                    DestinationEndpointPort = (ushort)random.Next(1, 65_536),
                };
            }

            builder.AddRow(row);
            builder.AddFieldRow(new SourceFieldRowV1
            {
                RawStreamId = row.RawStreamId,
                RawSourceEpoch = row.RawSourceEpoch,
                RawRecordOrdinal = row.RawRecordOrdinal,
                FactKey = row.FactKey,
                NativeTicks = row.NativeTicks,
                Field = SourceField.ConnectionId,
                Value = 0x7000 + (long)ordinal,
                Availability = FieldAvailability.Present,
            });
            if (random.Next(3) == 0)
            {
                bool named = random.Next(2) == 0;
                builder.AddFieldRow(new SourceFieldRowV1
                {
                    RawStreamId = row.RawStreamId,
                    RawSourceEpoch = row.RawSourceEpoch,
                    RawRecordOrdinal = row.RawRecordOrdinal,
                    FactKey = row.FactKey,
                    NativeTicks = row.NativeTicks,
                    Field = SourceField.FileKey,
                    Text = named ? Names[random.Next(Names.Length)] : null,
                    Availability = named ? FieldAvailability.Present : FieldAvailability.NotExposed,
                });
            }

            builder.Journal.Append(Envelope(ticks, ordinal, schema, policy));
        }

        _ = builder.Complete(Committed);
    }
}
