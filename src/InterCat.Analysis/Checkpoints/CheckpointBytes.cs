using System.Buffers.Binary;
using System.Text;

namespace InterCat.Analysis;

/// <summary>
/// Writes a derivation checkpoint's little-endian fields to a stream, a buffer at a time, and refuses to write more than
/// a checkpoint may hold (`contracts/derivation-checkpoint-v1.md` §3).
/// </summary>
internal sealed class CheckpointWriter(Stream destination)
{
    private readonly byte[] buffer = new byte[64 * 1024];
    private int used;

    /// <summary>How many bytes were written, including those still buffered.</summary>
    public long Written { get; private set; }

    public void U8(byte value) => Reserve(1)[0] = value;

    public void Flag(bool value) => U8(value ? (byte)1 : (byte)0);

    public void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void I64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void U64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

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
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException($"'{value}' is longer than a checkpoint's short strings.");
        }

        U8((byte)bytes.Length);
        Bytes(bytes);
    }

    /// <summary>A string of at most <see cref="CheckpointReader.MaximumStringBytes"/> UTF-8 bytes, after its four-byte length.</summary>
    public void Str32(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > CheckpointReader.MaximumStringBytes)
        {
            throw new InvalidOperationException("A name is longer than a checkpoint's strings.");
        }

        U32((uint)bytes.Length);
        Bytes(bytes);
    }

    /// <summary>Writes what is buffered. Nothing is written past <see cref="DerivationCheckpoint.MaximumBytes"/>.</summary>
    public void Flush()
    {
        destination.Write(buffer, 0, used);
        used = 0;
    }

    private void Bytes(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int take = Math.Min(bytes.Length, buffer.Length);
            bytes[..take].CopyTo(Reserve(take));
            bytes = bytes[take..];
        }
    }

    private Span<byte> Reserve(int count)
    {
        if (Written + count > DerivationCheckpoint.MaximumBytes)
        {
            throw new InvalidOperationException(
                $"The derivation state is larger than a checkpoint's {DerivationCheckpoint.MaximumBytes / (1024 * 1024)} MiB, "
                + "so none is written; opening the session derives it from its segments.");
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
/// Reads a derivation checkpoint's fields, refusing with <see cref="InvalidDataException"/> anything the bytes cannot
/// hold: a field past the end, a count larger than the bytes left could hold, a string that is not UTF-8.
/// </summary>
internal sealed class CheckpointReader(ReadOnlyMemory<byte> bytes)
{
    /// <summary>The longest string a checkpoint holds: an image path far beyond any Windows allows.</summary>
    public const int MaximumStringBytes = 1024 * 1024;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private int position;

    public int Remaining => bytes.Length - position;

    public byte U8() => Take(1)[0];

    public bool Flag() => U8() switch
    {
        0 => false,
        1 => true,
        var other => throw Invalid($"A flag holds {other}, where only 0 and 1 are defined."),
    };

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public Guid Identity() => new(Take(16));

    /// <summary>A count of elements that each take at least <paramref name="minimumBytes"/>, which the bytes left must be able to hold.</summary>
    public int Count(int minimumBytes)
    {
        uint count = U32();
        return count > (uint)(Remaining / minimumBytes)
            ? throw Invalid($"A count of {count:N0} is more than the {Remaining:N0} bytes left can hold.")
            : (int)count;
    }

    public string Str8() => Text(Take(U8()));

    public string Str32()
    {
        uint length = U32();
        return length > MaximumStringBytes
            ? throw Invalid($"A string of {length:N0} bytes is longer than a checkpoint holds.")
            : Text(Take(checked((int)length)));
    }

    public void RequireEnd()
    {
        if (Remaining != 0)
        {
            throw Invalid($"{Remaining:N0} bytes follow the last end.");
        }
    }

    public static InvalidDataException Invalid(string reason) => new("The derivation checkpoint is not readable: " + reason);

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

    private static string Text(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return Strict.GetString(utf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The derivation checkpoint is not readable: a string is not UTF-8.", exception);
        }
    }
}
