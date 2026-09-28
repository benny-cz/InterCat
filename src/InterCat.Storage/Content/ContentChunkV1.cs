using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary><c>EN-ContentClassification</c> (§23): what a fragment's bytes are, as its source's validated contract says.</summary>
public enum ContentClassificationV1 : byte
{
    OpaqueProviderData = 1,
    TransportFragment = 2,
    ApplicationPayload = 3,
    DecodedFields = 4,
    EncryptedContent = 5,
}

/// <summary>How a fragment's bytes are encoded, as its source declares it; never guessed from the bytes (content-v1 §1).</summary>
public enum ContentEncodingV1 : byte
{
    Binary = 1,
    Utf8 = 2,
    Utf16LittleEndian = 3,
}

/// <summary>How a fragment's bytes were kept (content-v1 §1).</summary>
public enum ContentDispositionV1 : byte
{
    /// <summary>Every byte the source delivered for the record.</summary>
    Whole = 1,

    /// <summary>A prefix: the message was longer than the per-record limit, and its original length says by how much.</summary>
    TruncatedByRecordLimit = 2,

    /// <summary>None: the session limit had been reached when the record was admitted (I13).</summary>
    OmittedBySessionLimit = 3,
}

/// <summary>The inspection consent content was kept under (`capture-profile-preview-v1`): whether a person may see its bytes.</summary>
public enum ContentInspectionV1 : byte
{
    Disabled = 1,
    HexAndText = 2,
}

/// <summary>What every fragment of one chunk was kept under (content-v1 §3).</summary>
/// <param name="CaptureId">The capture whose records the fragments belong to.</param>
/// <param name="PolicyId">The content admission policy the fragments were kept under.</param>
/// <param name="RecordLimit">The per-record limit: no fragment keeps more.</param>
/// <param name="Inspection">Whether a person may see the fragments' bytes.</param>
public sealed record ContentChunkHeaderV1(CaptureId CaptureId, string PolicyId, int RecordLimit, ContentInspectionV1 Inspection);

/// <summary>
/// One record's content, everything but its bytes (content-v1 §1): the record it belongs to, what the bytes are, and how
/// many were kept of how many.
/// </summary>
/// <param name="Offset">Where the fragment begins in its message, when the source states it; null otherwise.</param>
/// <param name="OriginalLength">The message's length, when the source exposes it; null otherwise.</param>
/// <param name="Kept">How many bytes the fragment holds.</param>
public sealed record ContentFragmentV1(
    uint StreamId,
    uint SourceEpoch,
    ulong RecordOrdinal,
    ContentClassificationV1 Classification,
    Direction Direction,
    ContentEncodingV1 Encoding,
    ContentDispositionV1 Disposition,
    long? Offset,
    long? OriginalLength,
    int Kept)
{
    /// <summary>The record's raw identity within <paramref name="captureId"/>.</summary>
    public RawRecordId RecordIn(CaptureId captureId) => new(captureId, StreamId, SourceEpoch, RecordOrdinal);

    /// <summary>
    /// How many bytes of the message the fragment lacks, when its original length is known: after a truncation, those past
    /// what was kept; after an omission, all of them. Null when the source exposes no length (content-v1 §1).
    /// </summary>
    public long? Missing => OriginalLength is { } original ? Math.Max(0, original - (Offset ?? 0) - Kept) : null;
}

/// <summary>A fragment as a chunk's reader found it, checked, and where its bytes are in the chunk.</summary>
/// <param name="BytesPosition">The offset of the fragment's first kept byte from the start of the chunk.</param>
/// <param name="FragmentPosition">The offset of the fragment itself, which its checksum covers from.</param>
public sealed record ContentChunkEntryV1(ContentFragmentV1 Fragment, long FragmentPosition, long BytesPosition);

/// <summary>A chunk as its reader found it: the header and every fragment, checked, without their bytes.</summary>
public sealed record ContentChunkContentsV1(ContentChunkHeaderV1 Header, IReadOnlyList<ContentChunkEntryV1> Entries);

/// <summary>
/// Writes and reads `content-v1` chunks (ADR-036): restricted evidence beside the journal, a record's content kept with
/// what a person needs to read it honestly (I21). The format is checked by its own checksums, so a reader checks each
/// fragment before interpreting a byte of it, and nothing that reads metadata reads a chunk.
/// </summary>
public static class ContentChunkV1
{
    /// <summary>The most bytes one fragment keeps under any request (`ContentCapturePolicyCompiler`'s own bound).</summary>
    public const int MaximumRecordLimit = 1024 * 1024;

    /// <summary>The most fragments one chunk holds: far past any scoped source's records in one publication.</summary>
    public const int MaximumFragments = 1_048_576;

    /// <summary>The longest content admission policy identity a chunk names.</summary>
    public const int MaximumPolicyBytes = 128;

    private const ushort Major = 1;
    private const ushort Minor = 0;

    /// <summary>A fragment's fixed fields, before its bytes: record, codes, offset, original length and kept count.</summary>
    private const int FragmentHeadBytes = 4 + 4 + 8 + 4 + 8 + 8 + 4;

    private static ReadOnlySpan<byte> Magic => "ICATCNT1"u8;

    /// <summary>The published name of a generation's content chunk.</summary>
    public static string FileName(long generation) =>
        string.Create(CultureInfo.InvariantCulture, $"content-{generation:D10}.icatc");

    /// <summary>The generation a content chunk's name carries, or null for any other name.</summary>
    public static long? GenerationOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == "content-".Length + 10 + ".icatc".Length
            && name.StartsWith("content-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".icatc", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(name.AsSpan("content-".Length, 10), NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
            ? generation
            : null;
    }

    /// <summary>Why a header cannot be written or read, or null when it can.</summary>
    public static string? Problem(ContentChunkHeaderV1 header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.CaptureId.Value == Guid.Empty ? "it names no capture"
            : string.IsNullOrEmpty(header.PolicyId) || header.PolicyId.Length > MaximumPolicyBytes
                || header.PolicyId.Any(character => character is < ' ' or > '~')
                ? $"its policy is not 1 to {MaximumPolicyBytes} printable ASCII characters"
            : header.RecordLimit is < 1 or > MaximumRecordLimit
                ? $"its record limit {header.RecordLimit:N0} is outside 1 to {MaximumRecordLimit:N0} bytes"
            : !Enum.IsDefined(header.Inspection) ? $"its inspection code {(byte)header.Inspection} is not defined"
            : null;
    }

    /// <summary>Why a fragment cannot be kept under <paramref name="recordLimit"/>, or null when it can (content-v1 §3).</summary>
    public static string? Problem(ContentFragmentV1 fragment, int recordLimit)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        return !Enum.IsDefined(fragment.Classification) ? $"its classification code {(byte)fragment.Classification} is not defined"
            : fragment.Direction is not (Direction.UnknownDirection or Direction.Outbound or Direction.Inbound)
                ? $"its direction code {(byte)fragment.Direction} is not a record's"
            : !Enum.IsDefined(fragment.Encoding) ? $"its encoding code {(byte)fragment.Encoding} is not defined"
            : !Enum.IsDefined(fragment.Disposition) ? $"its disposition code {(byte)fragment.Disposition} is not defined"
            : fragment.Kept < 0 || fragment.Kept > recordLimit
                ? $"it keeps {fragment.Kept:N0} bytes, outside 0 to the record limit of {recordLimit:N0}"
            : fragment.Offset is < 0 ? "its offset is negative"
            : fragment.OriginalLength is < 0 ? "its original length is negative"
            : fragment.Disposition switch
            {
                ContentDispositionV1.Whole when fragment.OriginalLength is { } original && original != fragment.Kept =>
                    $"it is kept whole, yet its message is {original:N0} bytes and it keeps {fragment.Kept:N0}",
                ContentDispositionV1.TruncatedByRecordLimit when fragment.Kept != recordLimit =>
                    "it is truncated by the record limit without keeping the limit",
                ContentDispositionV1.TruncatedByRecordLimit when fragment.OriginalLength is not { } original || original <= fragment.Kept =>
                    "it is truncated without an original length longer than what it keeps",
                ContentDispositionV1.OmittedBySessionLimit when fragment.Kept != 0 =>
                    "it is omitted by the session limit, yet keeps bytes",
                _ => null,
            };
    }

    /// <summary>
    /// Writes a chunk: its header, then every fragment with the bytes it keeps, in record order. Everything is checked
    /// before a byte is written, so a refused chunk writes nothing.
    /// </summary>
    public static void Write(
        Stream destination,
        ContentChunkHeaderV1 header,
        IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> fragments)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(fragments);
        if (Problem(header) is { } headerProblem)
        {
            throw new ArgumentException($"A content chunk cannot be written: {headerProblem}.", nameof(header));
        }

        if (fragments.Count > MaximumFragments)
        {
            throw new ArgumentException($"A content chunk holds at most {MaximumFragments:N0} fragments.", nameof(fragments));
        }

        ContentFragmentV1? previous = null;
        foreach ((ContentFragmentV1 fragment, ReadOnlyMemory<byte> bytes) in fragments)
        {
            string? problem = Problem(fragment, header.RecordLimit)
                ?? (bytes.Length != fragment.Kept ? $"it keeps {fragment.Kept:N0} bytes and carries {bytes.Length:N0}" : null)
                ?? (previous is not null && Compare(previous, fragment) >= 0
                    ? "it does not follow the record before it: fragments are in record order, one a record" : null);
            if (problem is not null)
            {
                throw new ArgumentException(
                    $"Record {fragment.StreamId}/{fragment.SourceEpoch}/{fragment.RecordOrdinal}'s content cannot be kept: {problem}.",
                    nameof(fragments));
            }

            previous = fragment;
        }

        byte[] policy = Encoding.ASCII.GetBytes(header.PolicyId);
        byte[] head = new byte[Magic.Length + 2 + 2 + 16 + 1 + policy.Length + 4 + 1 + 4];
        Magic.CopyTo(head);
        int at = Magic.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(at), Major);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(at + 2), Minor);
        at += 4;
        _ = header.CaptureId.Value.TryWriteBytes(head.AsSpan(at, 16));
        at += 16;
        head[at++] = (byte)policy.Length;
        policy.CopyTo(head.AsSpan(at));
        at += policy.Length;
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(at), header.RecordLimit);
        at += 4;
        head[at++] = (byte)header.Inspection;
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(at), (uint)fragments.Count);
        destination.Write(head);
        Span<byte> checksum = stackalloc byte[4];
        Crc32C.Write(checksum, Crc32C.Compute(head));
        destination.Write(checksum);

        Span<byte> fixedFields = stackalloc byte[FragmentHeadBytes];
        foreach ((ContentFragmentV1 fragment, ReadOnlyMemory<byte> bytes) in fragments)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fixedFields, fragment.StreamId);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedFields[4..], fragment.SourceEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[8..], fragment.RecordOrdinal);
            fixedFields[16] = (byte)fragment.Classification;
            fixedFields[17] = (byte)fragment.Direction;
            fixedFields[18] = (byte)fragment.Encoding;
            fixedFields[19] = (byte)fragment.Disposition;
            BinaryPrimitives.WriteInt64LittleEndian(fixedFields[20..], fragment.Offset ?? -1);
            BinaryPrimitives.WriteInt64LittleEndian(fixedFields[28..], fragment.OriginalLength ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(fixedFields[36..], fragment.Kept);
            destination.Write(fixedFields);
            destination.Write(bytes.Span);
            Crc32C.Write(checksum, Crc32C.Finish(Crc32C.Append(Crc32C.Append(Crc32C.Start(), fixedFields), bytes.Span)));
            destination.Write(checksum);
        }
    }

    /// <summary>
    /// A chunk's header and how many fragments it declares, checked against the header's checksum, without reading a
    /// fragment: what a disclosure states before anything is copied.
    /// </summary>
    public static (ContentChunkHeaderV1 Header, int Fragments) ReadHeader(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek)
        {
            throw new ArgumentException("A content chunk is read from a seekable stream.", nameof(source));
        }

        source.Position = 0;
        (ContentChunkHeaderV1 header, uint count) = ReadHeaderCore(source);
        return (header, checked((int)count));
    }

    /// <summary>
    /// Reads a chunk's header and every fragment, checking each one's checksum and rules, without keeping their bytes. A
    /// chunk that fails any check is refused whole (content-v1 §3).
    /// </summary>
    public static ContentChunkContentsV1 Read(Stream source, CaptureId expectedCapture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek)
        {
            throw new ArgumentException("A content chunk is read from a seekable stream.", nameof(source));
        }

        source.Position = 0;
        (ContentChunkHeaderV1 header, uint count) = ReadHeaderCore(source);
        if (header.CaptureId != expectedCapture)
        {
            throw Invalid($"it holds content of capture {header.CaptureId}, not this session's {expectedCapture}.");
        }

        var entries = new List<ContentChunkEntryV1>((int)count);
        byte[] fixedFields = new byte[FragmentHeadBytes];
        byte[] buffer = new byte[64 * 1024];
        ContentFragmentV1? previous = null;
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long fragmentPosition = source.Position;
            ReadExactly(source, fixedFields, "a fragment");
            var fragment = new ContentFragmentV1(
                BinaryPrimitives.ReadUInt32LittleEndian(fixedFields),
                BinaryPrimitives.ReadUInt32LittleEndian(fixedFields.AsSpan(4)),
                BinaryPrimitives.ReadUInt64LittleEndian(fixedFields.AsSpan(8)),
                (ContentClassificationV1)fixedFields[16],
                (Direction)fixedFields[17],
                (ContentEncodingV1)fixedFields[18],
                (ContentDispositionV1)fixedFields[19],
                Optional(BinaryPrimitives.ReadInt64LittleEndian(fixedFields.AsSpan(20)), "offset"),
                Optional(BinaryPrimitives.ReadInt64LittleEndian(fixedFields.AsSpan(28)), "original length"),
                BinaryPrimitives.ReadInt32LittleEndian(fixedFields.AsSpan(36)));
            if (Problem(fragment, header.RecordLimit) is { } problem)
            {
                throw Invalid($"fragment {index:N0} cannot be read: {problem}.");
            }

            if (previous is not null && Compare(previous, fragment) >= 0)
            {
                throw Invalid($"fragment {index:N0} does not follow the record before it.");
            }

            long bytesPosition = source.Position;
            uint crc = Crc32C.Append(Crc32C.Start(), fixedFields);
            for (int left = fragment.Kept; left > 0;)
            {
                int take = Math.Min(left, buffer.Length);
                ReadExactly(source, buffer.AsSpan(0, take), "a fragment's bytes");
                crc = Crc32C.Append(crc, buffer.AsSpan(0, take));
                left -= take;
            }

            ReadExactly(source, buffer.AsSpan(0, 4), "a fragment's checksum");
            if (Crc32C.Finish(crc) != BinaryPrimitives.ReadUInt32LittleEndian(buffer))
            {
                throw Invalid($"fragment {index:N0} does not match its checksum.");
            }

            entries.Add(new(fragment, fragmentPosition, bytesPosition));
            previous = fragment;
        }

        if (source.Position != source.Length)
        {
            throw Invalid($"{source.Length - source.Position:N0} bytes follow its last fragment.");
        }

        return new(header, entries);
    }

    /// <summary>Reads and checks a header from the chunk's start, and the fragment count it declares.</summary>
    private static (ContentChunkHeaderV1 Header, uint Count) ReadHeaderCore(Stream source)
    {
        byte[] fixedHead = new byte[Magic.Length + 2 + 2 + 16 + 1];
        ReadExactly(source, fixedHead, "its header");
        if (!fixedHead.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw Invalid("it does not begin with a content chunk's magic.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(fixedHead.AsSpan(Magic.Length));
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(fixedHead.AsSpan(Magic.Length + 2));
        if (major != Major || minor != Minor)
        {
            throw Invalid($"it is version {major}.{minor}, which this build does not read.");
        }

        var capture = new CaptureId(new Guid(fixedHead.AsSpan(Magic.Length + 4, 16)));
        int policyLength = fixedHead[^1];
        byte[] rest = new byte[policyLength + 4 + 1 + 4 + 4];
        ReadExactly(source, rest, "its header");
        uint computed = Crc32C.Finish(Crc32C.Append(Crc32C.Append(Crc32C.Start(), fixedHead), rest.AsSpan(0, rest.Length - 4)));
        if (computed != BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(rest.Length - 4)))
        {
            throw Invalid("its header does not match its checksum.");
        }

        // Checked as bytes: decoding first would turn a byte past ASCII into a printable '?'.
        if (rest.AsSpan(0, policyLength).ContainsAnyExceptInRange((byte)' ', (byte)'~'))
        {
            throw Invalid($"its policy is not 1 to {MaximumPolicyBytes} printable ASCII characters.");
        }

        string policy = Encoding.ASCII.GetString(rest, 0, policyLength);
        var header = new ContentChunkHeaderV1(
            capture,
            policy,
            BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(policyLength)),
            (ContentInspectionV1)rest[policyLength + 4]);
        if (Problem(header) is { } headerProblem)
        {
            throw Invalid(headerProblem + ".");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(policyLength + 5));
        if (count > MaximumFragments || count > (ulong)(source.Length - source.Position) / (FragmentHeadBytes + 4))
        {
            throw Invalid($"it declares {count:N0} fragments, more than it can hold.");
        }

        return (header, count);
    }

    /// <summary>
    /// The first <paramref name="maximum"/> bytes of one fragment a <see cref="Read"/> found, after checking the fragment
    /// against its checksum again: the chunk is read afresh, and nothing read earlier vouches for it.
    /// </summary>
    public static byte[] ReadBytes(Stream source, ContentChunkEntryV1 entry, int maximum)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        int kept = entry.Fragment.Kept;
        source.Position = entry.FragmentPosition;
        byte[] fixedFields = new byte[FragmentHeadBytes];
        ReadExactly(source, fixedFields, "a fragment");
        if (source.Position != entry.BytesPosition || BinaryPrimitives.ReadInt32LittleEndian(fixedFields.AsSpan(36)) != kept)
        {
            throw Invalid("a fragment is not where its chunk was read to have it.");
        }

        byte[] bytes = new byte[kept];
        ReadExactly(source, bytes, "a fragment's bytes");
        byte[] checksum = new byte[4];
        ReadExactly(source, checksum, "a fragment's checksum");
        if (Crc32C.Finish(Crc32C.Append(Crc32C.Append(Crc32C.Start(), fixedFields), bytes))
            != BinaryPrimitives.ReadUInt32LittleEndian(checksum))
        {
            throw Invalid("a fragment does not match its checksum.");
        }

        return maximum >= kept ? bytes : bytes[..maximum];
    }

    /// <summary>Record order: by stream, then source epoch, then ordinal.</summary>
    private static int Compare(ContentFragmentV1 left, ContentFragmentV1 right)
    {
        int stream = left.StreamId.CompareTo(right.StreamId);
        if (stream != 0) return stream;
        int epoch = left.SourceEpoch.CompareTo(right.SourceEpoch);
        return epoch != 0 ? epoch : left.RecordOrdinal.CompareTo(right.RecordOrdinal);
    }

    /// <summary>-1 is "none"; anything below it is refused.</summary>
    private static long? Optional(long value, string what) => value switch
    {
        -1 => null,
        < -1 => throw Invalid($"a fragment's {what} is {value}, where -1 means none."),
        _ => value,
    };

    private static void ReadExactly(Stream source, Span<byte> destination, string what)
    {
        try
        {
            source.ReadExactly(destination);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"The content chunk is not readable: it ends inside {what}.", exception);
        }
    }

    private static InvalidDataException Invalid(string reason) => new("The content chunk is not readable: " + reason);
}
