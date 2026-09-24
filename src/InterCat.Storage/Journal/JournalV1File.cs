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
    private long pendingEncodedBytes;

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
    /// Bytes already handed to the journal's stream. What <see cref="ProjectedCompleteLength"/> adds beyond this - the
    /// pending batch and the terminal - is not yet written anywhere, so a free-space admission check counts it as future
    /// growth. The stream's own write buffer may still hold part of this length; callers bound that separately.
    /// </summary>
    public long WrittenLength
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return stream.CanSeek
                ? stream.Position
                : throw new NotSupportedException("A written length needs a seekable journal stage.");
        }
    }

    /// <summary>Exact final file length if Complete were called now, including pending batch and terminal.</summary>
    public long ProjectedCompleteLength
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!stream.CanSeek)
            {
                throw new NotSupportedException("A byte quota needs a seekable journal stage.");
            }

            return checked(stream.Position + (pending.Count == 0 ? 0 : 76 + pendingEncodedBytes)
                + (completed ? 0 : 40));
        }
    }

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
        pendingEncodedBytes = checked(pendingEncodedBytes + JournalV1Codec.EncodedRecordLength(record));
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
        pendingEncodedBytes = 0;
    }

    /// <summary>
    /// Appends only if the eventual complete file, including an unflushed batch and terminal, fits.
    /// A false result leaves ownership of the record with the caller. No bytes are written on refusal.
    /// </summary>
    public bool TryAppendWithin(RecordEnvelopeV1 record, long maximumCompleteLength)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        if (completed || !wroteSchemas)
        {
            throw new InvalidOperationException("A bounded append needs an open journal with its schema table.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumCompleteLength);

        // A new batch needs a 40-byte frame and 36-byte count/identity prefix. An existing
        // pending batch already has that overhead in ProjectedCompleteLength.
        long projected = checked(ProjectedCompleteLength
            + JournalV1Codec.EncodedRecordLength(record)
            + (pending.Count == 0 ? 76 : 0));
        if (projected > maximumCompleteLength)
        {
            return false;
        }

        Append(record);
        return true;
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
    /// <summary>
    /// Replays one verified journal batch at a time. Each batch's pooled envelopes are released before the
    /// next is read, so re-derivation does not load a long capture into memory. A malformed or missing
    /// terminal refuses the replay; the caller must abandon any staged derivation on that exception.
    /// </summary>
    public static long ReplayBatches(
        Stream stream,
        Action<CaptureId, SourceClockDescriptor, JournalV1SchemaTable, JournalBatchV1> onBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onBatch);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Journal replay needs a readable, seekable owned file.", nameof(stream));
        }

        stream.Position = 0;
        byte[] header = new byte[JournalV1Codec.HeaderLength];
        ReadFramePart(stream, header);
        (CaptureId capture, _) = JournalV1Codec.DecodeHeader(header);
        SourceClockDescriptor? clock = null;
        JournalV1SchemaTable? schemas = null;
        bool sawBatch = false;
        bool terminal = false;
        long records = 0;
        byte[] frameHeader = new byte[8];
        byte[] digest = new byte[32];
        Span<byte> measured = stackalloc byte[32];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadFramePart(stream, frameHeader);
            JournalFrameKind kind = (JournalFrameKind)BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
            int length = BinaryPrimitives.ReadInt32LittleEndian(frameHeader.AsSpan(4));
            if (length < 0 || length > JournalV1Codec.MaximumFrameBytes)
            {
                throw new InvalidDataException("A journal-v1 replay frame length is outside its bound.");
            }

            byte[] payload = new byte[length];
            ReadFramePart(stream, payload);
            ReadFramePart(stream, digest);
            System.Security.Cryptography.SHA256.HashData(payload, measured);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(measured, digest))
            {
                throw new InvalidDataException($"A journal-v1 replay {kind} frame fails its checksum.");
            }

            switch (kind)
            {
                case JournalFrameKind.SourceClock when clock is null && !sawBatch:
                    clock = JournalV1Codec.DecodeClock(payload);
                    break;
                case JournalFrameKind.SchemaTable when schemas is null && !sawBatch:
                    schemas = JournalV1Codec.DecodeSchemaTable(payload);
                    break;
                case JournalFrameKind.RecordBatch when clock is not null && schemas is not null:
                    sawBatch = true;
                    using (JournalBatchV1 batch = JournalV1Codec.DecodeBatch(payload, capture))
                    {
                        onBatch(capture, clock.Value, schemas, batch);
                        records = checked(records + batch.Records.Count);
                    }

                    break;
                case JournalFrameKind.Terminal:
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("A journal-v1 replay has bytes after its terminal frame.");
                    }

                    terminal = true;
                    break;
                default:
                    throw new InvalidDataException(
                        $"A journal-v1 replay has an unknown, repeated or out-of-order {kind} frame.");
            }

            if (terminal)
            {
                break;
            }
        }

        if (!terminal || clock is null || schemas is null)
        {
            throw new InvalidDataException(
                "A journal-v1 replay needs one source clock, one schema table and a complete terminal frame.");
        }

        return records;
    }

    /// <summary>
    /// Finds one record by its raw identity. The header, clock and schema frames are verified as a replay verifies
    /// them, and a batch that is read is verified, decoded and checked against its declaration as usual. Unless
    /// <paramref name="exhaustive"/> is set, a batch whose declared first and last ordinals do not bracket the
    /// target's is skipped unread. That is exact for a journal stored in acquisition order, which is how the live
    /// recorder and the importer write one: a single admission counter numbers every stream's records in the order
    /// they are stored. The contract does not promise that order, so a skipping search can only find a record faster;
    /// a caller must not report a record absent until an exhaustive search agrees. The envelope is valid only during
    /// <paramref name="onFound"/>; its pooled buffers are released afterwards.
    /// </summary>
    /// <returns>Whether the record was found. A journal of another capture holds none of its records.</returns>
    public static bool FindRecord(
        Stream stream,
        RawRecordId target,
        Action<SourceClockDescriptor, JournalV1SchemaTable, RecordEnvelopeV1> onFound,
        bool exhaustive = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onFound);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("A journal lookup needs a readable, seekable owned file.", nameof(stream));
        }

        stream.Position = 0;
        byte[] header = new byte[JournalV1Codec.HeaderLength];
        ReadFramePart(stream, header);
        (CaptureId capture, _) = JournalV1Codec.DecodeHeader(header);
        if (capture != target.CaptureId)
        {
            return false;
        }

        const int Declaration = sizeof(int) + (2 * (sizeof(uint) + sizeof(uint) + sizeof(ulong)));
        SourceClockDescriptor? clock = null;
        JournalV1SchemaTable? schemas = null;
        byte[] frameHeader = new byte[8];
        byte[] declaration = new byte[Declaration];
        byte[] digest = new byte[32];
        Span<byte> measured = stackalloc byte[32];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadFramePart(stream, frameHeader);
            JournalFrameKind kind = (JournalFrameKind)BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
            int length = BinaryPrimitives.ReadInt32LittleEndian(frameHeader.AsSpan(4));
            if (length < 0 || length > JournalV1Codec.MaximumFrameBytes)
            {
                throw new InvalidDataException("A journal-v1 lookup frame length is outside its bound.");
            }

            if (kind == JournalFrameKind.RecordBatch && clock is not null && schemas is not null)
            {
                if (length < Declaration)
                {
                    throw new InvalidDataException("A journal-v1 batch is shorter than its identity declaration.");
                }

                ReadFramePart(stream, declaration);
                ulong firstOrdinal = BinaryPrimitives.ReadUInt64LittleEndian(declaration.AsSpan(12));
                ulong lastOrdinal = BinaryPrimitives.ReadUInt64LittleEndian(declaration.AsSpan(28));
                bool mayHold = exhaustive
                    || (target.RecordOrdinal >= firstOrdinal && target.RecordOrdinal <= lastOrdinal);
                long remaining = (long)length - Declaration + digest.Length;
                if (!mayHold)
                {
                    if (stream.Position + remaining > stream.Length)
                    {
                        throw new InvalidDataException("A journal-v1 lookup ends inside a frame.");
                    }

                    stream.Seek(remaining, SeekOrigin.Current);
                    continue;
                }

                byte[] batchPayload = new byte[length];
                declaration.CopyTo(batchPayload, 0);
                try
                {
                    stream.ReadExactly(batchPayload, Declaration, length - Declaration);
                }
                catch (EndOfStreamException exception)
                {
                    throw new InvalidDataException("A journal-v1 lookup ends inside a frame.", exception);
                }

                ReadFramePart(stream, digest);
                System.Security.Cryptography.SHA256.HashData(batchPayload, measured);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(measured, digest))
                {
                    throw new InvalidDataException("A journal-v1 lookup RecordBatch frame fails its checksum.");
                }

                using JournalBatchV1 batch = JournalV1Codec.DecodeBatch(batchPayload, capture);
                foreach (RecordEnvelopeV1 envelope in batch.Records)
                {
                    if (envelope.Id == target)
                    {
                        onFound(clock.Value, schemas, envelope);
                        return true;
                    }
                }

                continue;
            }

            byte[] payload = new byte[length];
            ReadFramePart(stream, payload);
            ReadFramePart(stream, digest);
            System.Security.Cryptography.SHA256.HashData(payload, measured);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(measured, digest))
            {
                throw new InvalidDataException($"A journal-v1 lookup {kind} frame fails its checksum.");
            }

            switch (kind)
            {
                case JournalFrameKind.SourceClock when clock is null:
                    clock = JournalV1Codec.DecodeClock(payload);
                    break;
                case JournalFrameKind.SchemaTable when schemas is null:
                    schemas = JournalV1Codec.DecodeSchemaTable(payload);
                    break;
                case JournalFrameKind.Terminal:
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("A journal-v1 lookup has bytes after its terminal frame.");
                    }

                    return false;
                default:
                    throw new InvalidDataException(
                        $"A journal-v1 lookup has an unknown, repeated or out-of-order {kind} frame.");
            }
        }

        throw new InvalidDataException(
            "A journal-v1 lookup needs one source clock, one schema table and a complete terminal frame.");
    }

    private static void ReadFramePart(Stream stream, byte[] buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("A journal-v1 replay ends inside a frame.", exception);
        }
    }

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

    /// <summary>
    /// Reads only the journal's identity and the source clock it declares, without decoding a batch. The
    /// contract puts the clock frame before any batch, so the read stops there: a caller that needs to interpret
    /// native readings - a rate per second, an interval in seconds - does not pay for every record to learn what
    /// clock produced them (I8). The frame's own checksum is verified; the file's other frames are not read.
    /// </summary>
    public static (CaptureId CaptureId, SourceClockDescriptor Clock) ReadSourceClock(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[JournalV1Codec.HeaderLength];
        ReadExactly(stream, header, "its header");
        (CaptureId captureId, _) = JournalV1Codec.DecodeHeader(header);

        // At most a schema table can precede the clock frame, so the scan is bounded by the frame kinds the
        // contract allows before a batch rather than by the file's length.
        Span<byte> frameHeader = stackalloc byte[8];
        Span<byte> checksum = stackalloc byte[32];
        Span<byte> expected = stackalloc byte[32];
        for (int frame = 0; frame < 2; frame++)
        {
            ReadExactly(stream, frameHeader, "a frame header");
            var kind = (JournalFrameKind)BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
            int length = BinaryPrimitives.ReadInt32LittleEndian(frameHeader[4..]);
            if (length < 0 || length > JournalV1Codec.MaximumFrameBytes)
            {
                throw new InvalidDataException("A journal-v1 frame length is outside its bound.");
            }

            switch (kind)
            {
                case JournalFrameKind.SourceClock:
                    byte[] payload = new byte[length];
                    ReadExactly(stream, payload, "its source clock frame");
                    ReadExactly(stream, checksum, "its source clock frame");
                    System.Security.Cryptography.SHA256.HashData(payload, expected);
                    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, checksum)
                        ? (captureId, JournalV1Codec.DecodeClock(payload))
                        : throw new InvalidDataException("A journal-v1 SourceClock frame fails its checksum.");
                case JournalFrameKind.SchemaTable:
                    Skip(stream, (long)length + checksum.Length);
                    break;
                default:
                    throw new InvalidDataException(
                        $"This journal reaches a {kind} frame before it declares a source clock. Records whose "
                        + "clock the file does not describe are incomplete evidence, not records with a default "
                        + "clock.");
            }
        }

        throw new InvalidDataException(
            "This journal declares no source clock before its records, so its native readings belong to a clock "
            + "the file does not name.");
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string what)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"A journal-v1 file ends inside {what}.");
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length)
            {
                throw new InvalidDataException("A journal-v1 file ends inside a frame.");
            }

            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        byte[] discard = new byte[Math.Min(count, 81_920)];
        while (count > 0)
        {
            int read = stream.Read(discard, 0, (int)Math.Min(count, discard.Length));
            if (read == 0)
            {
                throw new InvalidDataException("A journal-v1 file ends inside a frame.");
            }

            count -= read;
        }
    }
}
