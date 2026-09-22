using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace InterCat.Storage;

/// <summary>
/// One published dictionary a segment's columns reference. Its entries are sorted by their UTF-8 bytes and
/// unique, so the file is a function of the value set rather than of the order the values were met: two
/// derivations over the same rows publish the same dictionary bytes.
/// </summary>
/// <remarks>
/// A code is the entry's index. Absence is never a code — a column without a value for a row says so in its
/// null bitmap — so code 0 is an ordinary entry and no reader has to know a sentinel.
/// </remarks>
public sealed class SegmentDictionaryV1
{
    private readonly string[] entries;
    private readonly Dictionary<string, uint> codes;

    private SegmentDictionaryV1(ushort dictionaryId, SegmentDictionaryKind kind, string[] entries)
    {
        DictionaryId = dictionaryId;
        Kind = kind;
        this.entries = entries;
        codes = new(entries.Length, StringComparer.Ordinal);
        for (int index = 0; index < entries.Length; index++)
        {
            codes[entries[index]] = (uint)index;
        }
    }

    public ushort DictionaryId { get; }

    public SegmentDictionaryKind Kind { get; }

    public int Count => entries.Length;

    public IReadOnlyList<string> Entries => entries;

    public string this[uint code] =>
        code < (uint)entries.Length
            ? entries[code]
            : throw new InvalidDataException(
                $"Dictionary {DictionaryId} holds {entries.Length} entries; a column references code {code}.");

    public bool TryGetCode(string value, out uint code) => codes.TryGetValue(value, out code);

    /// <summary>
    /// Builds a dictionary from a value set, refusing rather than truncating when the set passes §10.2's
    /// budget. A caller that meets the refusal encodes the column into the variable chunk instead, which is
    /// the fallback that budget exists to have.
    /// </summary>
    public static SegmentDictionaryV1 Create(
        ushort dictionaryId,
        SegmentDictionaryKind kind,
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A dictionary declares a known kind.");
        }

        var distinct = new SortedSet<string>(SegmentTextOrder.Instance);
        long bytes = 0;
        foreach (string value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            int length = Encoding.UTF8.GetByteCount(value);
            if (length > SegmentFormatV1.MaximumTextBytes)
            {
                throw new ArgumentException(
                    $"A dictionary value is at most {SegmentFormatV1.MaximumTextBytes} UTF-8 bytes; one is "
                    + $"{length}.",
                    nameof(values));
            }

            if (distinct.Add(value))
            {
                bytes += length;
            }
        }

        return distinct.Count > SegmentFormatV1.MaximumDictionaryEntries
            ? throw new SegmentDictionaryBudgetException(
                $"This column has {distinct.Count} distinct values, past the {SegmentFormatV1.MaximumDictionaryEntries} "
                + "a dictionary holds.")
            : bytes > SegmentFormatV1.MaximumDictionaryBytes
                ? throw new SegmentDictionaryBudgetException(
                    $"This column's distinct values are {bytes} bytes, past the "
                    + $"{SegmentFormatV1.MaximumDictionaryBytes} a dictionary holds.")
                : new(dictionaryId, kind, [.. distinct]);
    }

    /// <summary>Encodes the dictionary as its published file.</summary>
    public byte[] Encode()
    {
        byte[][] payloads = new byte[entries.Length][];
        int valueBytes = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            payloads[index] = Encoding.UTF8.GetBytes(entries[index]);
            valueBytes += payloads[index].Length;
        }

        int offsetsLength = (entries.Length + 1) * sizeof(uint);
        int offsetsOffset = SegmentFormatV1.DictionaryHeaderLength;
        int valuesOffset = offsetsOffset + offsetsLength;
        int fileLength = valuesOffset + valueBytes + SegmentFormatV1.TrailerLength;
        byte[] file = new byte[fileLength];

        int cursor = valuesOffset;
        for (int index = 0; index < payloads.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                file.AsSpan(offsetsOffset + (index * sizeof(uint))),
                (uint)(cursor - valuesOffset));
            payloads[index].CopyTo(file.AsSpan(cursor));
            cursor += payloads[index].Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(offsetsOffset + (payloads.Length * sizeof(uint))),
            (uint)valueBytes);

        Span<byte> header = file.AsSpan(0, SegmentFormatV1.DictionaryHeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header, SegmentFormatV1.DictionaryMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], SegmentFormatV1.FormatMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], SegmentFormatV1.FormatMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], DictionaryId);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)offsetsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)offsetsLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], (uint)valuesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)valueBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)fileLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[48..],
            Crc32C.Compute(file.AsSpan(valuesOffset, valueBytes)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[52..],
            Crc32C.Compute(file.AsSpan(offsetsOffset, offsetsLength)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], Crc32C.Compute(header[..44]));
        SHA256.HashData(
            file.AsSpan(0, fileLength - SegmentFormatV1.TrailerLength),
            file.AsSpan(fileLength - SegmentFormatV1.TrailerLength));
        return file;
    }

    /// <summary>
    /// Reads a published dictionary, refusing anything it cannot read exactly. Every declared extent is
    /// checked against the file's own length before a byte of it is touched (§20.1).
    /// </summary>
    public static SegmentDictionaryV1 Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < SegmentFormatV1.DictionaryHeaderLength + SegmentFormatV1.TrailerLength)
        {
            throw new InvalidDataException("A segment-v1 dictionary is shorter than its own header.");
        }

        ReadOnlySpan<byte> header = file[..SegmentFormatV1.DictionaryHeaderLength];
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != SegmentFormatV1.DictionaryMagic)
        {
            throw new InvalidDataException("This file is not a segment-v1 dictionary.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (major != SegmentFormatV1.FormatMajor)
        {
            throw new InvalidDataException(
                $"This dictionary declares format major {major}; this reader implements "
                + $"{SegmentFormatV1.FormatMajor}. An unknown major version is refused, never read at a "
                + "guessed layout.");
        }

        uint required = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (required != 0)
        {
            throw new InvalidDataException(
                $"This dictionary requires features 0x{required:x8}, none of which this reader implements.");
        }

        if (Crc32C.Compute(header[..44]) != BinaryPrimitives.ReadUInt32LittleEndian(header[44..]))
        {
            throw new InvalidDataException("A segment-v1 dictionary header fails its checksum.");
        }

        ushort dictionaryId = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        var kind = (SegmentDictionaryKind)BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException(
                $"Dictionary {dictionaryId} declares kind {(ushort)kind}, which this reader does not implement.");
        }

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        uint offsetsOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        uint offsetsLength = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        uint valuesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        uint valuesLength = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
        uint fileLength = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);

        if (entryCount > SegmentFormatV1.MaximumDictionaryEntries
            || valuesLength > SegmentFormatV1.MaximumDictionaryBytes)
        {
            throw new InvalidDataException(
                $"Dictionary {dictionaryId} declares {entryCount} entries over {valuesLength} bytes, past the "
                + "bounds this format holds.");
        }

        if (fileLength != file.Length
            || offsetsLength != (entryCount + 1) * sizeof(uint)
            || offsetsOffset != SegmentFormatV1.DictionaryHeaderLength
            || valuesOffset != offsetsOffset + offsetsLength
            || (long)valuesOffset + valuesLength + SegmentFormatV1.TrailerLength != fileLength)
        {
            throw new InvalidDataException(
                $"Dictionary {dictionaryId} declares extents that do not fit its own {file.Length} bytes.");
        }

        Span<byte> digest = stackalloc byte[SegmentFormatV1.TrailerLength];
        SHA256.HashData(file[..(int)(fileLength - SegmentFormatV1.TrailerLength)], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, file[(int)(fileLength - SegmentFormatV1.TrailerLength)..]))
        {
            throw new InvalidDataException($"Dictionary {dictionaryId} fails its trailing digest.");
        }

        ReadOnlySpan<byte> offsets = file.Slice((int)offsetsOffset, (int)offsetsLength);
        ReadOnlySpan<byte> values = file.Slice((int)valuesOffset, (int)valuesLength);
        if (Crc32C.Compute(offsets) != BinaryPrimitives.ReadUInt32LittleEndian(header[52..])
            || Crc32C.Compute(values) != BinaryPrimitives.ReadUInt32LittleEndian(header[48..]))
        {
            throw new InvalidDataException($"Dictionary {dictionaryId} fails a section checksum.");
        }

        string[] entries = new string[entryCount];
        uint previous = 0;
        for (uint index = 0; index < entryCount; index++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(offsets[(int)(index * sizeof(uint))..]);
            uint end = BinaryPrimitives.ReadUInt32LittleEndian(offsets[(int)((index + 1) * sizeof(uint))..]);
            if (start != previous || end < start || end > valuesLength)
            {
                throw new InvalidDataException(
                    $"Dictionary {dictionaryId} entry {index} spans [{start}, {end}), which is not the next "
                    + "run of its values.");
            }

            entries[index] = Encoding.UTF8.GetString(values.Slice((int)start, (int)(end - start)));
            previous = end;
        }

        if (previous != valuesLength)
        {
            throw new InvalidDataException(
                $"Dictionary {dictionaryId} leaves {valuesLength - previous} value bytes no entry claims.");
        }

        // The order is part of the contract, not a convenience: a code is an index into a sorted unique
        // list, so a file whose entries are unsorted or repeated does not mean what a reader assumes.
        for (int index = 1; index < entries.Length; index++)
        {
            if (SegmentTextOrder.Instance.Compare(entries[index - 1], entries[index]) >= 0)
            {
                throw new InvalidDataException(
                    $"Dictionary {dictionaryId} is not sorted and unique at entry {index}.");
            }
        }

        return new(dictionaryId, kind, entries);
    }
}

/// <summary>
/// Raised when a value set is past §10.2's dictionary budget. It is a signal to use the variable-chunk
/// fallback for that column, not a failure of the derivation.
/// </summary>
public sealed class SegmentDictionaryBudgetException(string message) : Exception(message);

/// <summary>
/// Orders text by its UTF-8 bytes, so the sort a writer applies and the sort a reader verifies are the same
/// on every machine. A culture-aware or UTF-16 ordinal comparison would put the same values in a different
/// order for some inputs, and the dictionary's codes are positions in that order.
/// </summary>
internal sealed class SegmentTextOrder : IComparer<string>
{
    public static SegmentTextOrder Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        // UTF-8 byte order is code-point order, so the comparison walks code points rather than encoding both
        // values to compare them. A UTF-16 ordinal comparison is not the same relation: it puts a surrogate
        // pair before U+E000 where UTF-8 puts it after.
        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            int leftPoint = CodePointAt(left, ref leftIndex);
            int rightPoint = CodePointAt(right, ref rightIndex);
            if (leftPoint != rightPoint)
            {
                return leftPoint < rightPoint ? -1 : 1;
            }
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int CodePointAt(string value, ref int index)
    {
        char first = value[index++];
        if (!char.IsHighSurrogate(first) || index >= value.Length || !char.IsLowSurrogate(value[index]))
        {
            return first;
        }

        return char.ConvertToUtf32(first, value[index++]);
    }
}
