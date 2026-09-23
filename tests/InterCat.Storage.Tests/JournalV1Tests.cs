using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// The IC-011 acceptance tests: a golden framing corpus, extended-data round-trip, buffer ownership and
/// callback lifetime. Journal-v1 is a frozen contract, so a change to its bytes has to be a deliberate
/// one that updates the golden file along with an ADR (§13.6).
/// </summary>
public sealed class JournalV1Tests
{
    private static readonly CaptureId Capture = new(Guid.Parse("11111111-2222-4333-8444-555555555555"));
    private static readonly DateTimeOffset Created = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "I1: a journal-v1 file has exactly the bytes its golden corpus records")]
    public void GoldenFramingIsByteExact()
    {
        byte[] produced = BuildGoldenJournal();
        string goldenPath = GoldenPath();

        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllBytes(goldenPath, produced);
        }

        byte[] golden = File.ReadAllBytes(goldenPath);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(golden)),
            Convert.ToHexString(SHA256.HashData(produced)));
        Assert.Equal(golden.Length, produced.Length);
        Assert.Equal(golden, produced);
    }

    [Fact(DisplayName = "I1: replaying a journal preserves every field of every record")]
    public void EveryFieldSurvivesAReplay()
    {
        byte[] file = BuildGoldenJournal();

        using JournalV1Contents contents = JournalV1Reader.Read(file);

        Assert.Equal(Capture, contents.CaptureId);
        Assert.Equal(Created, contents.CreatedUtc);
        Assert.Equal(Clock(), contents.SourceClock);
        Assert.Equal(2, contents.Batches.Count);

        RecordEnvelopeV1[] records = [.. contents.Records];
        Assert.Equal(3, records.Length);

        RecordEnvelopeV1 first = records[0];
        Assert.Equal(new RawRecordId(Capture, 1, 1, 1), first.Id);
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), first.Header.ProviderId);
        Assert.Equal(10, first.Header.EventId);
        Assert.Equal(2, first.Header.Version);
        Assert.Equal(0x0102030405060708UL, first.Header.Keyword);
        Assert.Equal(4242, first.Header.ProcessId);
        Assert.Equal(Guid.Parse("33333333-3333-4333-8333-333333333333"), first.Header.ActivityId);
        Assert.Equal(7, first.BufferContext.ProcessorNumber);
        Assert.Equal(99, first.BufferContext.LoggerId);
        Assert.Equal(TimestampEncoding.Qpc, first.TimestampEncoding);
        Assert.Equal(123_456_789, first.NativeTicks);
        Assert.Equal(8, first.PointerSize);
        Assert.Equal(1u, first.SchemaReference);
        Assert.Equal(1u, first.AdmissionPolicyReference);
        Assert.Equal(BodyClassificationV1.ApprovedMetadata, first.Body.Classification);
        Assert.Equal(BodyDispositionV1.Retained, first.Body.Disposition);
        Assert.Equal<byte>([1, 2, 3, 4], first.Body.Bytes.ToArray());

        // Extended items keep their type, linkage, original length and bytes, so a truncated item is
        // still readable as a prefix of a longer one (I21).
        Assert.Equal(2, first.ExtendedItems.Count);
        Assert.Equal(0x000D, first.ExtendedItems[0].Type);
        Assert.Equal<byte>([9, 9, 9, 9, 9, 9, 9, 9], first.ExtendedItems[0].Bytes.ToArray());
        Assert.False(first.ExtendedItems[0].Truncated);
        Assert.Equal(900, first.ExtendedItems[1].OriginalLength);
        Assert.True(first.ExtendedItems[1].Truncated);
        Assert.Equal(1, first.OmittedExtendedItemCount);

        RecordEnvelopeV1 omitted = records[1];
        Assert.Equal(BodyDispositionV1.OmittedUnknownSchema, omitted.Body.Disposition);
        Assert.Equal(4_096, omitted.Body.OriginalLength);
        Assert.Equal(0, omitted.Body.RetainedLength);
        Assert.Null(omitted.SchemaReference);

        JournalSchemaV1 schema = Assert.Single(contents.Schemas.Schemas);
        Assert.Equal("provider/10/v2/8", schema.Fingerprint);
        JournalPolicyV1 policy = Assert.Single(contents.Schemas.Policies);
        Assert.Equal("metadata-only-v1", policy.PolicyId);
    }

    [Fact(DisplayName = "I1: a batch declares the first and last identity it actually carries")]
    public void BatchIdentitiesMatchTheirRecords()
    {
        using JournalV1Contents contents = JournalV1Reader.Read(BuildGoldenJournal());

        foreach (JournalBatchV1 batch in contents.Batches)
        {
            Assert.Equal(batch.Records[0].Id, batch.First);
            Assert.Equal(batch.Records[^1].Id, batch.Last);
        }
    }

    [Fact(DisplayName = "R9: an envelope owns its bytes after the source memory changes")]
    public void EnvelopeBytesSurviveSourceMutation()
    {
        byte[] source = [1, 2, 3, 4];
        using EnvelopeBuffer buffer = EnvelopeBuffer.CopyOf(source);

        source[0] = 0xFF;
        source[3] = 0xFF;

        Assert.Equal<byte>([1, 2, 3, 4], buffer.ToArray());
    }

    [Fact(DisplayName = "R9: a returned buffer refuses every read rather than handing out another owner's bytes")]
    public void AReturnedBufferRefusesEveryRead()
    {
        EnvelopeBuffer buffer = EnvelopeBuffer.CopyOf([1, 2, 3]);
        Assert.False(buffer.IsReturned);
        Assert.Equal(3, buffer.Length);

        buffer.Dispose();

        Assert.True(buffer.IsReturned);
        Assert.Throws<ObjectDisposedException>(() => buffer.Length);
        Assert.Throws<ObjectDisposedException>(() => buffer.ToArray());

        // Disposing twice must not return the array twice: that would hand one buffer to two owners.
        buffer.Dispose();
        Assert.True(buffer.IsReturned);
    }

    [Fact(DisplayName = "R9: writing a batch returns every buffer the records held")]
    public void WritingABatchReturnsItsBuffers()
    {
        using var stream = new MemoryStream();
        using JournalV1Writer writer = JournalV1Writer.Create(stream, Capture, Clock(), Created);
        writer.WriteSchemas(new());
        RecordEnvelopeV1 record = Record(1, body: [1, 2, 3]);
        EnvelopeBuffer body = record.Body.Bytes;
        EnvelopeBuffer item = record.ExtendedItems[0].Bytes;

        writer.Append(record);
        writer.FlushBatch();

        Assert.True(body.IsReturned);
        Assert.True(item.IsReturned);
    }

    [Theory(DisplayName = "R16: journal quota accounts for pending records, batch frames and terminal exactly")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void BoundedAppendUsesExactCompleteLength(int batchCapacity)
    {
        using var stream = new MemoryStream();
        using JournalV1Writer writer = JournalV1Writer.Create(
            stream, Capture, Clock(), Created, batchCapacity);
        writer.WriteSchemas(new());
        long emptyLength = writer.ProjectedCompleteLength;
        Assert.Equal(stream.Length + 40, emptyLength);

        for (ulong ordinal = 1; ordinal <= 5; ordinal++)
        {
            RecordEnvelopeV1 record = Record(ordinal, body: new byte[(int)ordinal]);
            long required = writer.ProjectedCompleteLength
                + JournalV1Codec.EncodedRecordLength(record)
                + (ordinal == 1 || (ordinal - 1) % (ulong)batchCapacity == 0 ? 76 : 0);
            Assert.False(writer.TryAppendWithin(record, required - 1));
            Assert.False(record.Body.Bytes.IsReturned);
            Assert.True(writer.TryAppendWithin(record, required));
            Assert.Equal(required, writer.ProjectedCompleteLength);
        }

        long projected = writer.ProjectedCompleteLength;
        writer.Complete();
        Assert.Equal(projected, stream.Length);
        Assert.Equal(projected, writer.ProjectedCompleteLength);
        using JournalV1Contents replay = JournalV1Reader.Read(stream.ToArray());
        Assert.Equal(5, replay.Records.Count());
    }

    [Fact(DisplayName = "R9: a writer that is disposed mid-batch returns the buffers it was holding")]
    public void DisposingAWriterReturnsPendingBuffers()
    {
        using var stream = new MemoryStream();
        RecordEnvelopeV1 record = Record(1, body: [1, 2, 3]);
        EnvelopeBuffer body = record.Body.Bytes;

        using (JournalV1Writer writer = JournalV1Writer.Create(stream, Capture, Clock(), Created))
        {
            writer.Append(record);
            Assert.False(body.IsReturned);
        }

        Assert.True(body.IsReturned);
    }

    [Fact(DisplayName = "I1: a journal without its terminal frame is refused as an interrupted capture")]
    public void AnInterruptedJournalIsRefused()
    {
        byte[] complete = BuildGoldenJournal();
        byte[] truncated = complete[..^40];

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => JournalV1Reader.Read(truncated).Dispose());

        Assert.Contains("interrupted", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "I1: a corrupted record fails its own integrity check, naming the record")]
    public void ACorruptedRecordIsNamed()
    {
        byte[] file = BuildGoldenJournal();

        // Flip a byte inside the first batch's payload, past every frame header.
        int offset = JournalV1Codec.HeaderLength + 200;
        file[offset] ^= 0xFF;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => JournalV1Reader.Read(file).Dispose());

        Assert.Contains("checksum", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "I1: a journal from another format major is refused, never read at a guessed layout")]
    public void AnotherMajorVersionIsRefused()
    {
        byte[] file = BuildGoldenJournal();
        file[4] = 2;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => JournalV1Reader.Read(file).Dispose());

        Assert.Contains("major", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "P16: starting a journal never replaces evidence that is already there")]
    public void AJournalNeverReplacesExistingEvidence()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "journal-v1-tests",
            $"{Guid.NewGuid():N}.icatj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "evidence from an earlier run");
        try
        {
            Assert.Throws<IOException>(
                () => JournalV1Writer.CreateNewFile(path, Capture, Clock(), Created).Dispose());
            Assert.Equal("evidence from an earlier run", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "I8: a corrupted source clock frame is refused, never read as a partial clock")]
    public void ACorruptedClockFrameIsRefused()
    {
        byte[] file = BuildGoldenJournal();

        // The clock frame follows the header; flip a byte inside its payload.
        file[JournalV1Codec.HeaderLength + 12] ^= 0xFF;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => JournalV1Reader.Read(file).Dispose());

        Assert.Contains("SourceClock", failure.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: a journal that names no clock is refused, never read against an assumed one")]
    public void ClocklessJournalIsRefused()
    {
        CaptureId captureId = CaptureId.New();
        byte[] file =
        [
            .. JournalV1Codec.EncodeHeader(captureId, Created),
            .. JournalV1Codec.EncodeSchemaTable(new JournalV1SchemaTable()),
            .. JournalV1Codec.EncodeTerminal(),
        ];

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => JournalV1Reader.Read(file));

        Assert.Contains("describes no source clock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I8: a second clock frame is refused rather than silently replacing the first")]
    public void SecondClockFrameIsRefused()
    {
        CaptureId captureId = CaptureId.New();
        byte[] file =
        [
            .. JournalV1Codec.EncodeHeader(captureId, Created),
            .. JournalV1Codec.EncodeClock(Clock()),
            .. JournalV1Codec.EncodeClock(SecondClock()),
            .. JournalV1Codec.EncodeTerminal(),
        ];

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => JournalV1Reader.Read(file));

        Assert.Contains("one source clock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: CRC-32C matches its published vectors")]
    public void Crc32CMatchesItsVectors()
    {
        Assert.Equal(0u, Crc32C.Compute([]));
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
        Assert.Equal(0x8A9136AAu, Crc32C.Compute(new byte[32]));
        byte[] ones = new byte[32];
        Array.Fill(ones, (byte)0xFF);
        Assert.Equal(0x62A8AB43u, Crc32C.Compute(ones));
    }

    /// <summary>
    /// The corpus. Every value is fixed, so the bytes this produces are the contract: a change to the
    /// format changes this file, and changing this file is a deliberate act.
    /// </summary>
    private static byte[] BuildGoldenJournal()
    {
        var schemas = new JournalV1SchemaTable();
        uint schema = schemas.Intern(
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            10,
            2,
            "provider/10/v2/8");
        uint policy = schemas.InternPolicy("metadata-only-v1");

        using var stream = new MemoryStream();
        using (JournalV1Writer writer = JournalV1Writer.Create(
            stream,
            Capture,
            Clock(),
            Created,
            batchCapacity: 2))
        {
            writer.WriteSchemas(schemas);
            writer.Append(Record(1, body: [1, 2, 3, 4], schemaReference: schema, policyReference: policy));
            writer.Append(OmittedBodyRecord(2, policy));
            writer.Append(Record(3, body: [5], schemaReference: schema, policyReference: policy));
            writer.Complete();
        }

        return stream.ToArray();
    }

    /// <summary>A second, different clock, for the file that wrongly declares two.</summary>
    private static SourceClockDescriptor SecondClock() => new(
        new ClockId(Guid.Parse("99999999-8888-4777-8666-555555555555")),
        new HostId(Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff")),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        1_000_000,
        TimestampRounding.NearestEven,
        604_800_000_000_000);

    private static SourceClockDescriptor Clock() => new(
        new ClockId(Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa")),
        new HostId(Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff")),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        1_000_000,
        TimestampRounding.NearestEven,
        604_800_000_000_000);

    private static RecordEnvelopeV1 Record(
        ulong ordinal,
        byte[] body,
        uint schemaReference = 1,
        uint policyReference = 1) => new()
    {
        CaptureId = Capture,
        StreamId = 1,
        SourceEpoch = 1,
        RecordOrdinal = ordinal,
        Header = new(
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
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
            2424,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444")),
        BufferContext = new(7, 99),
        ClockId = new(Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa")),
        TimestampEncoding = TimestampEncoding.Qpc,
        NativeTicks = 123_456_789,
        PointerSize = 8,
        SchemaReference = schemaReference,
        AdmissionPolicyReference = policyReference,
        ExtendedItems =
        [
            new()
            {
                Type = 0x000D,
                Flags = 0,
                OriginalLength = 8,
                Bytes = EnvelopeBuffer.CopyOf([9, 9, 9, 9, 9, 9, 9, 9]),
            },
            new()
            {
                Type = 0x0006,
                Flags = 1,
                OriginalLength = 900,
                Bytes = EnvelopeBuffer.CopyOf(Encoding.ASCII.GetBytes("stack")),
            },
        ],
        OmittedExtendedItemCount = 1,
        Body = new()
        {
            Classification = BodyClassificationV1.ApprovedMetadata,
            Disposition = BodyDispositionV1.Retained,
            OriginalLength = body.Length,
            Bytes = EnvelopeBuffer.CopyOf(body),
        },
    };

    /// <summary>A record whose body policy refused. It keeps the original length and the reason (I13).</summary>
    private static RecordEnvelopeV1 OmittedBodyRecord(ulong ordinal, uint policyReference)
    {
        RecordEnvelopeV1 template = Record(ordinal, [], policyReference: policyReference);
        template.Body.Dispose();
        return template with
        {
            SchemaReference = null,
            Body = new()
            {
                Classification = BodyClassificationV1.OpaqueUnknown,
                Disposition = BodyDispositionV1.OmittedUnknownSchema,
                OriginalLength = 4_096,
                Bytes = EnvelopeBuffer.Empty,
            },
        };
    }

    private static string GoldenPath()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return Path.Combine(
            current?.FullName ?? throw new DirectoryNotFoundException("repository root"),
            "fixtures",
            "FX-JOURNAL-002",
            "golden",
            "journal-v1.icatj");
    }
}
