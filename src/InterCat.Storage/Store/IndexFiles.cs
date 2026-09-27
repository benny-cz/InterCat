using System.Buffers.Binary;
using System.Text;

namespace InterCat.Storage;

/// <summary>
/// Writes an index file's little-endian fields to a stream, a buffer at a time, and refuses to write more than the
/// index may hold. The indexes a generation publishes (`contracts/derivation-checkpoint-v1.md`,
/// `contracts/overview-index-v1.md`) share these field encodings.
/// </summary>
public sealed class IndexFileWriter(Stream destination, long maximumBytes, string what)
{
    private readonly byte[] buffer = new byte[64 * 1024];
    private int used;

    /// <summary>How many bytes were written, including those still buffered.</summary>
    public long Written { get; private set; }

    public void Raw(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int take = Math.Min(bytes.Length, buffer.Length);
            bytes[..take].CopyTo(Reserve(take));
            bytes = bytes[take..];
        }
    }

    public void U8(byte value) => Reserve(1)[0] = value;

    public void Flag(bool value) => U8(value ? (byte)1 : (byte)0);

    public void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void I64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void U64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    /// <summary>A 128-bit address, its 16 bytes in network order as a segment holds it (`segment-v1` §5).</summary>
    public void Address128(UInt128 value) => BinaryPrimitives.WriteUInt128BigEndian(Reserve(16), value);

    public void Identity(Guid value)
    {
        if (!value.TryWriteBytes(Reserve(16)))
        {
            throw new InvalidOperationException("A GUID did not fit its 16 bytes.");
        }
    }

    public void Count(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        U32((uint)count);
    }

    /// <summary>A string of at most 255 UTF-8 bytes, after its one-byte length.</summary>
    public void Str8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException($"'{value}' is longer than an index's short strings.");
        }

        U8((byte)bytes.Length);
        Raw(bytes);
    }

    /// <summary>A string of at most <see cref="IndexFileReader.MaximumStringBytes"/> UTF-8 bytes, after its four-byte length.</summary>
    public void Str32(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > IndexFileReader.MaximumStringBytes)
        {
            throw new InvalidOperationException("A name is longer than an index's strings.");
        }

        U32((uint)bytes.Length);
        Raw(bytes);
    }

    /// <summary>Writes what is buffered.</summary>
    public void Flush()
    {
        destination.Write(buffer, 0, used);
        used = 0;
    }

    private Span<byte> Reserve(int count)
    {
        if (Written + count > maximumBytes)
        {
            throw new InvalidOperationException(
                $"The {what} would be larger than its {maximumBytes / (1024 * 1024)} MiB, so none is written.");
        }

        if (buffer.Length - used < count)
        {
            Flush();
        }

        Span<byte> span = buffer.AsSpan(used, count);
        used += count;
        Written += count;
        return span;
    }
}

/// <summary>
/// Reads an index file's fields, refusing with <see cref="InvalidDataException"/> anything the bytes cannot hold: a field
/// past the end, a count larger than the bytes left could hold, a string that is not UTF-8.
/// </summary>
public sealed class IndexFileReader(ReadOnlyMemory<byte> bytes, string what)
{
    /// <summary>The longest string an index holds: an image path far beyond any Windows allows.</summary>
    public const int MaximumStringBytes = 1024 * 1024;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private int position;

    public int Remaining => bytes.Length - position;

    public byte U8() => Take(1)[0];

    public bool Flag() => U8() switch
    {
        0 => false,
        1 => true,
        var other => throw Invalid($"a flag holds {other}, where only 0 and 1 are defined."),
    };

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public UInt128 Address128() => BinaryPrimitives.ReadUInt128BigEndian(Take(16));

    public Guid Identity() => new(Take(16));

    /// <summary>Whether the next bytes are exactly <paramref name="expected"/>; they are consumed either way.</summary>
    public bool Matches(ReadOnlySpan<byte> expected) =>
        expected.Length <= Remaining && Take(expected.Length).SequenceEqual(expected);

    /// <summary>A count of elements that each take at least <paramref name="minimumBytes"/>, which the bytes left must be able to hold.</summary>
    public int Count(int minimumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBytes);
        uint count = U32();
        return count > (uint)(Remaining / minimumBytes)
            ? throw Invalid($"a count of {count:N0} is more than the {Remaining:N0} bytes left can hold.")
            : (int)count;
    }

    public string Str8() => Text(Take(U8()));

    public string Str32()
    {
        uint length = U32();
        return length > MaximumStringBytes
            ? throw Invalid($"a string of {length:N0} bytes is longer than an index holds.")
            : Text(Take(checked((int)length)));
    }

    public void RequireEnd()
    {
        if (Remaining != 0)
        {
            throw Invalid($"{Remaining:N0} bytes follow its last field.");
        }
    }

    /// <summary>The refusal of this index, and why.</summary>
    public InvalidDataException Invalid(string reason) => new($"The {what} is not readable: {reason}");

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
        {
            throw Invalid("it ends inside a field.");
        }

        ReadOnlySpan<byte> span = bytes.Span.Slice(position, count);
        position += count;
        return span;
    }

    private string Text(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return Strict.GetString(utf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The {what} is not readable: a string is not UTF-8.", exception);
        }
    }
}
