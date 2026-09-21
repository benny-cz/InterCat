using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class JournalProbeFileTests
{
    [Fact(DisplayName = "IC-009: a multi-batch probe file replays only after a complete terminal frame")]
    public async Task CompleteMultiBatchFileRoundTrips()
    {
        string path = NewPath();
        try
        {
            JournalProbeFileSummary summary;
            await using (JournalProbeFileWriter writer = JournalProbeFileWriter.CreateNew(path, batchRecordCapacity: 2))
            {
                for (int ordinal = 1; ordinal <= 5; ordinal++)
                {
                    await writer.AppendAsync(BuildEnvelope(ordinal));
                }

                summary = await writer.CompleteAsync();
            }

            IReadOnlyList<JournalProbeEnvelope> replayed = JournalProbeFileWriter.ReadComplete(path);

            Assert.Equal(5, summary.Records);
            Assert.Equal(3, summary.Batches);
            Assert.Equal(4, summary.DurableFlushes);
            Assert.Equal(5, replayed.Count);
            Assert.Equal((ulong)5, replayed[^1].Id.RecordOrdinal);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact(DisplayName = "IC-009: a probe file without its terminal frame is rejected as incomplete")]
    public async Task IncompleteFileIsRejected()
    {
        string path = NewPath();
        try
        {
            await using (JournalProbeFileWriter writer = JournalProbeFileWriter.CreateNew(path, batchRecordCapacity: 1))
            {
                await writer.AppendAsync(BuildEnvelope(1));
            }

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => JournalProbeFileWriter.ReadComplete(path));
            Assert.Contains("terminal", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact(DisplayName = "IC-009: creating a journal probe file never replaces existing evidence")]
    public async Task ExistingFileIsNeverOverwritten()
    {
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "keep me");
        try
        {
            Assert.Throws<IOException>(() => JournalProbeFileWriter.CreateNew(path));
            Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact(DisplayName = "I8: a probe file carries the capture's source clock and replays it unchanged")]
    public void SourceClockFrameRoundTrips()
    {
        string path = NewPath();
        try
        {
            SourceClockDescriptor clock = BuildClock();
            JournalProbeFileSummary summary;
            using (JournalProbeFileWriter writer = JournalProbeFileWriter.CreateNew(
                path,
                batchRecordCapacity: 2,
                flushEachBatch: true,
                sourceClock: clock,
                synchronous: true))
            {
                writer.Append(BuildEnvelope(1));
                writer.Append(BuildEnvelope(2));
                summary = writer.Complete(TimeSpan.FromMilliseconds(3), writerThreadAllocatedBytes: 512);
            }

            JournalProbeFileContents contents = JournalProbeFileWriter.ReadCompleteFile(path);

            Assert.Equal(clock, contents.SourceClock);
            Assert.Equal(2, contents.Records.Count);
            Assert.Equal(TimeSpan.FromMilliseconds(3), summary.Writer.WriterThreadCpu);
            Assert.Equal(512, summary.Writer.WriterThreadAllocatedBytes);
            Assert.Equal(summary.DurableFlushes, summary.Writer.DurableFlushLatency.Samples);
            Assert.Equal(summary.Batches, summary.Writer.EncodeLatency.Samples);
            Assert.True(summary.Writer.PayloadBytesWritten > 0);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact(DisplayName = "R21: a probe file without a clock frame reports no clock rather than a default one")]
    public async Task FileWithoutClockReportsNoClock()
    {
        string path = NewPath();
        try
        {
            await using (JournalProbeFileWriter writer = JournalProbeFileWriter.CreateNew(path, batchRecordCapacity: 2))
            {
                await writer.AppendAsync(BuildEnvelope(1));
                _ = await writer.CompleteAsync();
            }

            JournalProbeFileContents contents = JournalProbeFileWriter.ReadCompleteFile(path);

            Assert.Null(contents.SourceClock);
            Assert.Single(contents.Records);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact(DisplayName = "I8: a corrupted clock frame is refused instead of yielding a partial descriptor")]
    public void CorruptedClockFrameIsRefused()
    {
        byte[] encoded = JournalProbeCodec.EncodeClock(BuildClock());
        encoded[^1] ^= 0xFF;

        Assert.True(JournalProbeCodec.IsClockFrame(encoded));
        Assert.Throws<InvalidDataException>(() => JournalProbeCodec.DecodeClock(encoded));
    }

    private static SourceClockDescriptor BuildClock() => new(
        new ClockId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
        new HostId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        987_654_321,
        TimestampRounding.NearestEven,
        604_800_000_000_000);

    private static JournalProbeEnvelope BuildEnvelope(int ordinal) => new()
    {
        Id = new(new CaptureId(Guid.Parse("11111111-1111-4111-8111-111111111111")), 1, 1, (ulong)ordinal),
        ProviderGuid = Guid.Parse("22222222-2222-4222-8222-222222222222"),
        EventId = 10,
        Version = 0,
        Opcode = 1,
        NativeTimestamp = new(
            new ClockId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
            TimestampEncoding.Qpc,
            ordinal * 100),
        HeaderProcessId = 42,
        HeaderThreadId = 43,
        ProcessorNumber = 2,
        PointerSize = 8,
        ActivityId = Guid.Empty,
        RelatedActivityId = Guid.Empty,
        SchemaFingerprint = "fixture-v0",
        AdmissionPolicyId = "probe-file-test-v0",
        Body = new()
        {
            Classification = JournalProbeBodyClassification.ApprovedMetadata,
            Disposition = JournalProbeBodyDisposition.Retained,
            OriginalLength = 1,
            RetainedBytes = new byte[] { (byte)ordinal },
        },
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
    };

    private static string NewPath() => Path.Combine(
        AppContext.BaseDirectory,
        "journal-probe-test-artifacts",
        $"{Guid.NewGuid():N}.ijp0");

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
