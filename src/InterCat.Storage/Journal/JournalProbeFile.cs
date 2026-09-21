using System.Buffers.Binary;
using System.Diagnostics;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// What the writer stage cost. Encode and durable flush are separate quantities because they are paid on
/// different resources, and the per-flush distribution is kept rather than an average (section 12).
/// </summary>
public sealed record JournalProbeWriterMetrics
{
    public required long PayloadBytesWritten { get; init; }
    public required LatencyHistogramSnapshot EncodeLatency { get; init; }
    public required LatencyHistogramSnapshot DurableFlushLatency { get; init; }

    /// <summary>Processor time of the writing thread, or null when it moved threads or cannot be read.</summary>
    public required TimeSpan? WriterThreadCpu { get; init; }

    /// <summary>Bytes the writing thread allocated, or null when the writer could not isolate a thread.</summary>
    public required long? WriterThreadAllocatedBytes { get; init; }
}

public sealed record JournalProbeFileSummary(
    string Path,
    long Bytes,
    long Records,
    int Batches,
    int DurableFlushes,
    JournalProbeWriterMetrics Writer);

/// <summary>What one complete probe file holds: the capture's clock, when present, and its records.</summary>
public sealed record JournalProbeFileContents(
    SourceClockDescriptor? SourceClock,
    IReadOnlyList<JournalProbeEnvelope> Records);

/// <summary>
/// Disposable IC-009 file framing around checksummed probe batches. This is intentionally version 0 and
/// must not be recognized as journal-v1. A zero-length terminal frame distinguishes a complete run from
/// a process-crash tail, and CreateNew prevents accidental evidence replacement.
/// </summary>
public sealed class JournalProbeFileWriter : IAsyncDisposable, IDisposable
{
    private const uint FileMagic = 0x30464a49; // "IJF0" in little-endian byte order.
    private const ushort FileVersion = 0;
    private const int HeaderLength = 8;
    private const int MaximumFrameBytes = JournalProbeCodec.MaximumBatchBytes + 48;

    private readonly FileStream stream;
    private readonly List<JournalProbeEnvelope> pending;
    private readonly int batchRecordCapacity;
    private readonly bool flushEachBatch;
    private readonly LatencyHistogram encodeLatency = new();
    private readonly LatencyHistogram flushLatency = new();
    private long payloadBytes;
    private long recordCount;
    private int batchCount;
    private int durableFlushCount;
    private bool completed;
    private bool disposed;

    private JournalProbeFileWriter(
        string path,
        FileStream stream,
        int batchRecordCapacity,
        bool flushEachBatch)
    {
        Path = path;
        this.stream = stream;
        this.batchRecordCapacity = batchRecordCapacity;
        this.flushEachBatch = flushEachBatch;
        pending = new(batchRecordCapacity);
    }

    public string Path { get; }

    /// <summary>The source clock this file carries, or null when the caller supplied none.</summary>
    public SourceClockDescriptor? SourceClock { get; private set; }

    /// <summary>
    /// Creates a new probe file. A supplied source clock is written as the first frame, so replay converts
    /// native ticks from the file alone (ADR-006). <paramref name="synchronous"/> opens the handle without
    /// overlapped I/O, which is what a writer thread needs for its own flush latency to be measurable.
    /// </summary>
    public static JournalProbeFileWriter CreateNew(
        string outputPath,
        int batchRecordCapacity = 4_096,
        bool flushEachBatch = true,
        SourceClockDescriptor? sourceClock = null,
        bool synchronous = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (batchRecordCapacity is < 1 or > JournalProbeCodec.MaximumRecordCount)
        {
            throw new ArgumentOutOfRangeException(nameof(batchRecordCapacity));
        }

        string path = System.IO.Path.GetFullPath(outputPath);
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The journal probe path has no parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.Read,
            BufferSize = 128 * 1024,
            Options = synchronous
                ? FileOptions.SequentialScan
                : FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        FileStream stream = new(path, options);
        try
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            BinaryPrimitives.WriteUInt32LittleEndian(header, FileMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], FileVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
            stream.Write(header);
            var writer = new JournalProbeFileWriter(path, stream, batchRecordCapacity, flushEachBatch);
            if (sourceClock is { } clock)
            {
                byte[] encoded = JournalProbeCodec.EncodeClock(clock);
                writer.WriteLength(encoded.Length);
                stream.Write(encoded);
                writer.SourceClock = clock;
            }

            return writer;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask AppendAsync(
        JournalProbeEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        AddPending(envelope);
        if (pending.Count >= batchRecordCapacity)
        {
            await FlushBatchAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends without yielding, for a writer that owns its thread. Staying on one thread is what makes
    /// the writer's own processor time attributable to the writer (section 12).
    /// </summary>
    public void Append(JournalProbeEnvelope envelope)
    {
        AddPending(envelope);
        if (pending.Count >= batchRecordCapacity)
        {
            FlushBatch();
        }
    }

    public async Task<JournalProbeFileSummary> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!completed)
        {
            await FlushBatchAsync(cancellationToken).ConfigureAwait(false);
            await WriteLengthAsync(0, cancellationToken).ConfigureAwait(false);
            await FlushDurablyAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
        }

        return BuildSummary(null, null);
    }

    /// <summary>
    /// Completes the file on the calling thread and attributes the writer stage to it. The two totals are
    /// the caller's own thread deltas; a caller that cannot isolate a thread passes null rather than zero.
    /// </summary>
    public JournalProbeFileSummary Complete(TimeSpan? writerThreadCpu = null, long? writerThreadAllocatedBytes = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!completed)
        {
            FlushBatch();
            WriteLength(0);
            FlushDurably();
            completed = true;
        }

        return BuildSummary(writerThreadCpu, writerThreadAllocatedBytes);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stream.Dispose();
    }

    private void AddPending(JournalProbeEnvelope envelope)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(envelope);
        if (completed)
        {
            throw new InvalidOperationException("The journal probe file is already complete.");
        }

        if (recordCount + pending.Count >= JournalProbeCodec.MaximumRecordCount)
        {
            throw new InvalidDataException("Journal probe file exceeds its bounded record count.");
        }

        pending.Add(envelope);
    }

    private JournalProbeFileSummary BuildSummary(TimeSpan? writerThreadCpu, long? writerThreadAllocatedBytes) => new(
        Path,
        stream.Length,
        recordCount,
        batchCount,
        durableFlushCount,
        new()
        {
            PayloadBytesWritten = payloadBytes,
            EncodeLatency = encodeLatency.Read(),
            DurableFlushLatency = flushLatency.Read(),
            WriterThreadCpu = writerThreadCpu,
            WriterThreadAllocatedBytes = writerThreadAllocatedBytes,
        });

    private byte[]? EncodePending()
    {
        if (pending.Count == 0)
        {
            return null;
        }

        long started = Stopwatch.GetTimestamp();
        byte[] encoded = JournalProbeCodec.EncodeBatch(pending);
        encodeLatency.RecordTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);
        recordCount += pending.Count;
        batchCount++;
        payloadBytes += encoded.Length;
        pending.Clear();
        return encoded;
    }

    private async Task FlushBatchAsync(CancellationToken cancellationToken)
    {
        byte[]? encoded = EncodePending();
        if (encoded is null)
        {
            return;
        }

        await WriteLengthAsync(encoded.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        if (flushEachBatch)
        {
            await FlushDurablyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void FlushBatch()
    {
        byte[]? encoded = EncodePending();
        if (encoded is null)
        {
            return;
        }

        WriteLength(encoded.Length);
        stream.Write(encoded);
        if (flushEachBatch)
        {
            FlushDurably();
        }
    }

    private async Task WriteLengthAsync(int length, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
    }

    private void WriteLength(int length)
    {
        Span<byte> prefix = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        stream.Write(prefix);
    }

    private async Task FlushDurablyAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        flushLatency.RecordTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);
        durableFlushCount++;
    }

    private void FlushDurably()
    {
        long started = Stopwatch.GetTimestamp();
        stream.Flush();
        stream.Flush(flushToDisk: true);
        flushLatency.RecordTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);
        durableFlushCount++;
    }

    /// <summary>Reads a complete probe file, including the source clock frame when the writer wrote one.</summary>
    public static JournalProbeFileContents ReadCompleteFile(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        string path = System.IO.Path.GetFullPath(inputPath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != FileMagic || reader.ReadUInt16() != FileVersion || reader.ReadUInt16() != 0)
        {
            throw new InvalidDataException("Not a supported IC-009 journal probe file.");
        }

        SourceClockDescriptor? clock = null;
        var records = new List<JournalProbeEnvelope>();
        bool terminalSeen = false;
        while (stream.Position < stream.Length)
        {
            int frameLength;
            try
            {
                frameLength = reader.ReadInt32();
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Journal probe file ended inside a frame length.", exception);
            }

            if (frameLength == 0)
            {
                terminalSeen = true;
                break;
            }

            if (frameLength < 0 || frameLength > MaximumFrameBytes)
            {
                throw new InvalidDataException("Journal probe file frame length is outside its bound.");
            }

            byte[] encoded = reader.ReadBytes(frameLength);
            if (encoded.Length != frameLength)
            {
                throw new InvalidDataException("Journal probe file ended inside a declared batch.");
            }

            if (JournalProbeCodec.IsClockFrame(encoded))
            {
                if (clock is not null || records.Count > 0)
                {
                    throw new InvalidDataException("A journal probe file carries at most one leading clock frame.");
                }

                clock = JournalProbeCodec.DecodeClock(encoded);
                continue;
            }

            IReadOnlyList<JournalProbeEnvelope> batch = JournalProbeCodec.DecodeBatch(encoded);
            if (records.Count + batch.Count > JournalProbeCodec.MaximumRecordCount)
            {
                throw new InvalidDataException("Journal probe file exceeds its bounded record count.");
            }

            records.AddRange(batch);
        }

        if (!terminalSeen)
        {
            throw new InvalidDataException("Journal probe file has no complete terminal frame.");
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Journal probe file has bytes after its terminal frame.");
        }

        return new(clock, records);
    }

    public static IReadOnlyList<JournalProbeEnvelope> ReadComplete(string inputPath) =>
        ReadCompleteFile(inputPath).Records;
}
