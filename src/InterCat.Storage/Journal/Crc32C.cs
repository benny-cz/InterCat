using System.Buffers.Binary;

namespace InterCat.Storage;

/// <summary>
/// CRC-32C (Castagnoli, polynomial 0x1EDC6F41), the per-record integrity check of journal-v1. A record
/// carries one because a batch checksum can only say that something in the batch is wrong; a per-record
/// check says which record, which is what a reader needs to quarantine one and keep the rest (§18.1).
/// </summary>
/// <remarks>
/// It is implemented here rather than taken from a package so the journal's integrity primitive does not
/// add a dependency to the format. The implementation is the standard reflected table-driven one and is
/// checked against published test vectors.
/// </remarks>
public static class Crc32C
{
    private const uint Polynomial = 0x82F63B78;
    private const uint Seed = 0xFFFFFFFF;

    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC-32C of a span, in the usual reflected, inverted form.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Append(Seed, data));

    /// <summary>Continues a CRC over another span. The caller finishes it with <see cref="Finish"/>.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
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

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
