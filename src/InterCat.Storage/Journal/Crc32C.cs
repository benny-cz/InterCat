using System.Buffers.Binary;
using System.Numerics;

namespace InterCat.Storage;

/// <summary>
/// CRC-32C (Castagnoli, polynomial 0x1EDC6F41), the per-record integrity check of journal-v1. A record
/// carries one because a batch checksum can only say that something in the batch is wrong; a per-record
/// check says which record, which is what a reader needs to quarantine one and keep the rest (§18.1).
/// </summary>
/// <remarks>
/// It is implemented here rather than taken from a package so the journal's integrity primitive does not
/// add a dependency to the format. Eight bytes at a time go through <see cref="BitOperations.Crc32C(uint, ulong)"/>,
/// which is the processor's own CRC-32C instruction where there is one (SSE4.2, Arm64) and a software table
/// elsewhere: a byte table checked a 64 MiB column at 0.5 GiB/s, and a segment's columns are checked on every
/// first read. The result is the standard reflected CRC-32C, checked against published test vectors and a
/// bitwise reference at every length and alignment.
/// </remarks>
public static class Crc32C
{
    private const uint Seed = 0xFFFFFFFF;

    /// <summary>The CRC-32C of a span, in the usual reflected, inverted form.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Append(Seed, data));

    /// <summary>Continues a CRC over another span. The caller finishes it with <see cref="Finish"/>.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        // The instruction consumes a word's bytes lowest first, which is their order in memory once the word is read
        // little-endian, so the words and the byte tail make the same checksum a byte-at-a-time pass would.
        while (data.Length >= sizeof(ulong))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[sizeof(ulong)..];
        }

        foreach (byte value in data)
        {
            crc = BitOperations.Crc32C(crc, value);
        }

        return crc;
    }

    /// <summary>Starts a running CRC.</summary>
    public static uint Start() => Seed;

    /// <summary>Inverts a running CRC into its final value.</summary>
    public static uint Finish(uint crc) => crc ^ Seed;

    /// <summary>Writes a CRC little-endian, which is how journal-v1 stores every fixed-width field.</summary>
    public static void Write(Span<byte> destination, uint crc) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination, crc);
}
