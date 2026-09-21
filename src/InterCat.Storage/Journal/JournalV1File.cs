using System.Buffers.Binary;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>What one complete journal-v1 file holds. Its records are owned by the caller who read them.</summary>
public sealed record JournalV1Contents(
    CaptureId CaptureId,
    DateTimeOffset CreatedUtc,
    SourceClockDescriptor SourceClock,
    JournalV1SchemaTable Schemas,
    IReadOnlyList<JournalBatchV1> Batches) : IDisposable
{
    public IEnumerable<RecordEnvelopeV1> Records => Batches.SelectMany(batch => batch.Records);

    public void Dispose()
    {
        foreach (JournalBatchV1 batch in Batches)
        {
            batch.Dispose();
        }
    }
}

/// <summary>
/// Writes a journal-v1 file. The framing and its order are contract; durability, crash recovery and the
/// committed-boundary protocol of §20.1 are IC-016's, and this writer deliberately implements neither:
/// it produces a file, and a caller that needs a commit protocol builds one on top of this.
/// </summary>
public sealed class JournalV1Writer : IDisposable
{
    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly List<RecordEnvelopeV1> pending;
    private readonly int batchCapacity;
    private bool wroteSchemas;
    private bool completed;
    private bool disposed;

    private JournalV1Writer(Stream stream, bool ownsStream, int batchCapacity)
    {
        this.stream = stream;
        this.ownsStream = ownsStream;
        this.batchCapacity = batchCapacity;
        pending = new(batchCapacity);
    }

    public CaptureId CaptureId { get; private init; }

    public long BatchesWritten { get; private set; }

    public long RecordsWritten { get; private set; }

    /// <summary>
    /// Starts a journal on a stream the caller owns. The header and the clock frame are written at once,
    /// because a journal whose records name a clock the file does not describe is incomplete evidence.
    /// </summary>
    public static JournalV1Writer Create(
        Stream stream,
        CaptureId captureId,
        SourceClockDescriptor sourceClock,
        DateTimeOffset createdUtc,
        int batchCapacity = 4_096,
        bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (batchCapacity is < 1 or > JournalV1Codec.MaximumRecordsPerBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(batchCapacity));
        }

        var writer = new JournalV1Writer(stream, ownsStream, batchCapacity) { CaptureId = captureId };
        stream.Write(JournalV1Codec.EncodeHeader(captureId, createdUtc));
        stream.Write(JournalV1Codec.EncodeClock(sourceClock));
        return writer;
    }

    /// <summary>
    /// Starts a journal at a path that must not exist. Evidence is never replaced by a later run: a
    /// capture that would overwrite one refuses instead, and the caller chooses another name (P16).
    /// </summary>
    public static JournalV1Writer CreateNewFile(
        string path,
        CaptureId captureId,
        SourceClockDescriptor sourceClock,
        DateTimeOffset createdUtc,
        int batchCapacity = 4_096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)
            ?? throw new ArgumentException("The journal path has no parent directory.", nameof(path)));
        var stream = new FileStream(
            full,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.Read,
                BufferSize = 128 * 1024,
                Options = FileOptions.SequentialScan,
            });
        try
        {
            return Create(stream, captureId, sourceClock, createdUtc, batchCapacity, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes the schema and policy tables. They must precede the first batch, so that a reader which
    /// stops at a truncated batch has already read every reference that batch could have used.
    /// </summary>
    public void WriteSchemas(JournalV1SchemaTable table)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(table);
        if (BatchesWritten > 0)
        {
            throw new InvalidOperationException(
                "The schema table precedes the first batch. A record cannot reference a table written after it.");
        }

        stream.Write(JournalV1Codec.EncodeSchemaTable(table));
        wroteSchemas = true;
    }

    /// <summary>
    /// Takes ownership of one envelope. The writer disposes it when its batch is written, which returns
    /// its pooled buffers: the envelope's single owner passes from the caller to the writer here (§18.1).
    /// </summary>
    public void Append(RecordEnvelopeV1 record)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        if (completed)
        {
            throw new InvalidOperationException("This journal is already complete.");
        }

        if (record.CaptureId != CaptureId)
        {
            record.Dispose();
            throw new InvalidOperationException(
                "A journal-v1 file is one capture. A record from another capture belongs in its own journal.");
        }

        pending.Add(record);
        if (pending.Count >= batchCapacity)
        {
            FlushBatch();
        }
    }

    /// <summary>Writes the pending records as one batch and returns their buffers.</summary>
    public void FlushBatch()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pending.Count == 0)
        {
            return;
        }

        stream.Write(JournalV1Codec.EncodeBatch(pending));
        BatchesWritten++;
        RecordsWritten += pending.Count;
        foreach (RecordEnvelopeV1 record in pending)
        {
            record.Dispose();
        }

        pending.Clear();
    }

    /// <summary>
    /// Writes the terminal frame. A journal without one was interrupted, and a reader refuses it rather
    /// than presenting a partial capture as a complete one (§20.1).
    /// </summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            return;
        }

        if (!wroteSchemas)
        {
            WriteSchemas(new());
        }

        FlushBatch();
        stream.Write(JournalV1Codec.EncodeTerminal());
        stream.Flush();
        completed = true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (RecordEnvelopeV1 record in pending)
        {
            record.Dispose();
        }

        pending.Clear();
        if (ownsStream)
        {
            stream.Dispose();
        }
    }
}

/// <summary>Reads a complete journal-v1 file, refusing anything it cannot read rather than guessing.</summary>
public static class JournalV1Reader
{
    public static JournalV1Contents Read(ReadOnlySpan<byte> file)
    {
        (CaptureId captureId, DateTimeOffset createdUtc) = JournalV1Codec.DecodeHeader(file);
        int position = JournalV1Codec.HeaderLength;
        SourceClockDescriptor? clock = null;
        JournalV1SchemaTable schemas = new();
        var batches = new List<JournalBatchV1>();
        bool terminal = false;
        Span<byte> expected = stackalloc byte[32];

        try
        {
            while (position < file.Length && !terminal)
            {
                if (position + 8 > file.Length)
                {
                    throw new InvalidDataException("A journal-v1 file ends inside a frame header.");
                }

                var kind = (JournalFrameKind)BinaryPrimitives.ReadUInt32LittleEndian(file[position..]);
                int length = BinaryPrimitives.ReadInt32LittleEndian(file[(position + 4)..]);
                if (length < 0 || length > JournalV1Codec.MaximumFrameBytes)
                {
                    throw new InvalidDataException("A journal-v1 frame length is outside its bound.");
                }

                int payloadStart = position + 8;
                int checksumStart = payloadStart + length;
                if (checksumStart + 32 > file.Length)
                {
                    throw new InvalidDataException("A journal-v1 file ends inside a frame.");
                }

                ReadOnlySpan<byte> payload = file.Slice(payloadStart, length);
                System.Security.Cryptography.SHA256.HashData(payload, expected);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    expected,
                    file.Slice(checksumStart, 32)))
                {
                    throw new InvalidDataException($"A journal-v1 {kind} frame fails its checksum.");
                }

                switch (kind)
                {
                    case JournalFrameKind.SourceClock:
                        if (clock is not null)
                        {
                            throw new InvalidDataException(
                                "A journal-v1 file carries one source clock. Two would leave every record's "
                                + "native reading ambiguous about which clock produced it.");
                        }

                        clock = JournalV1Codec.DecodeClock(payload);
                        break;
                    case JournalFrameKind.SchemaTable:
                        schemas = JournalV1Codec.DecodeSchemaTable(payload);
                        break;
                    case JournalFrameKind.RecordBatch:
                        if (clock is null)
                        {
                            throw new InvalidDataException(
                                "A journal-v1 batch precedes its source clock frame. Records whose clock the "
                                + "file does not describe are incomplete evidence, not records with a "
                                + "default clock.");
                        }

                        batches.Add(JournalV1Codec.DecodeBatch(payload, captureId));
                        break;
                    case JournalFrameKind.Terminal:
                        terminal = true;
                        break;
                    default:
                        throw new InvalidDataException(
                            $"A journal-v1 file carries frame kind {(uint)kind}, which this reader does not "
                            + "implement. An unknown frame is refused, never skipped.");
                }

                position = checksumStart + 32;
            }

            if (!terminal)
            {
                throw new InvalidDataException(
                    "This journal has no terminal frame: the capture that wrote it was interrupted. A "
                    + "partial capture is not presented as a complete one.");
            }

            if (position != file.Length)
            {
                throw new InvalidDataException("A journal-v1 file has bytes after its terminal frame.");
            }

            // The contract puts exactly one clock frame before every batch. A file without one describes
            // no clock at all, and reading it would mean presenting native readings against an assumed
            // clock - which is the conversion I8 forbids. It is refused instead (contracts/journal-v1.md).
            return clock is not { } described
                ? throw new InvalidDataException(
                    "This journal describes no source clock. Its records' native readings belong to a clock "
                    + "the file does not name, so they are refused rather than read against an assumed one.")
                : new JournalV1Contents(captureId, createdUtc, described, schemas, batches);
        }
        catch
        {
            foreach (JournalBatchV1 batch in batches)
            {
                batch.Dispose();
            }

            throw;
        }
    }

    public static JournalV1Contents ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(Path.GetFullPath(path)));
    }
}
