namespace InterCat.Storage;

/// <summary>
/// Where a segment reader's bytes come from: a whole file already in memory, or a published file read one column at a
/// time. A reader checks every piece it takes against that piece's own checksum, so where the bytes came from never
/// decides whether they are trusted.
/// </summary>
internal abstract class SegmentBytes
{
    /// <summary>The file's length in bytes.</summary>
    public abstract long Length { get; }

    /// <summary>The whole file when it is held in memory, so a reader can check its trailer; null when it is read in pieces.</summary>
    public abstract ReadOnlyMemory<byte>? Whole { get; }

    /// <summary>A range of the file.</summary>
    public abstract ReadOnlyMemory<byte> Read(int offset, int length);

    /// <summary>One column's values and, when it is nullable, its null bitmap; empty otherwise.</summary>
    public abstract (ReadOnlyMemory<byte> Values, ReadOnlyMemory<byte> Nulls) ReadColumn(SegmentColumnDescriptor column);
}

/// <summary>A segment held whole in memory: every read is a slice of it, and nothing is copied.</summary>
internal sealed class ResidentSegmentBytes(ReadOnlyMemory<byte> file) : SegmentBytes
{
    public override long Length => file.Length;

    public override ReadOnlyMemory<byte>? Whole => file;

    public override ReadOnlyMemory<byte> Read(int offset, int length) => file.Slice(offset, length);

    public override (ReadOnlyMemory<byte> Values, ReadOnlyMemory<byte> Nulls) ReadColumn(SegmentColumnDescriptor column) =>
        (file.Slice(column.ValueOffset, column.ValueLength),
            column.Nullable ? file.Slice(column.NullBitmapOffset, column.NullBitmapLength) : ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// A published segment read a piece at a time. Each read opens the file, checks that it still has the length its
/// generation recorded, reads and closes it, so a reader holds no handle between reads and never keeps a file open that
/// retention would remove. The evidence lease the caller holds is what keeps the file there (store-v1 §8).
/// </summary>
internal sealed class PublishedSegmentFile : SegmentBytes
{
    private readonly IOwnedDirectory directory;
    private readonly string name;
    private readonly long length;

    private PublishedSegmentFile(IOwnedDirectory directory, string name, long length)
    {
        this.directory = directory;
        this.name = name;
        this.length = length;
    }

    public override long Length => length;

    public override ReadOnlyMemory<byte>? Whole => null;

    /// <summary>
    /// Opens a published segment for reading: its header and directories from minor 1, which is all a reader needs before
    /// it reads a column, or the whole file at minor 0, whose directories and chunk only its trailer covers. The header's
    /// own checksum is checked before its counts decide how much more is read.
    /// </summary>
    public static (SegmentBytes Source, ReadOnlyMemory<byte> Prefix) Open(IOwnedDirectory directory, string name, long length)
    {
        ArgumentNullException.ThrowIfNull(directory);
        OwnedFileName.Require(name, nameof(name));
        if (length is < SegmentFormatV1.HeaderLength + SegmentFormatV1.TrailerLength or > SegmentFormatV1.MaximumSegmentBytes)
        {
            throw new InvalidDataException(
                $"'{name}' is recorded as {length} bytes, outside what a segment-v1 file can be.");
        }

        var file = new PublishedSegmentFile(directory, name, length);
        using FileStream stream = file.OpenChecked();
        byte[] header = ReadAt(stream, 0, SegmentFormatV1.HeaderLength);
        (ushort minor, int directoriesEnd) = SegmentReaderV1.DirectoriesOf(header);
        if (minor < SegmentFormatV1.StructureChecksumMinor)
        {
            byte[] whole = GC.AllocateUninitializedArray<byte>((int)length);
            header.CopyTo(whole, 0);
            stream.ReadExactly(whole.AsSpan(SegmentFormatV1.HeaderLength));
            return (new ResidentSegmentBytes(whole), whole);
        }

        if (directoriesEnd > length - SegmentFormatV1.TrailerLength)
        {
            throw new InvalidDataException("A segment-v1 directory is not where its header says it is.");
        }

        byte[] prefix = GC.AllocateUninitializedArray<byte>(directoriesEnd);
        header.CopyTo(prefix, 0);
        stream.ReadExactly(prefix.AsSpan(SegmentFormatV1.HeaderLength));
        return (file, prefix);
    }

    public override ReadOnlyMemory<byte> Read(int offset, int length)
    {
        using FileStream stream = OpenChecked();
        return ReadAt(stream, offset, length);
    }

    public override (ReadOnlyMemory<byte> Values, ReadOnlyMemory<byte> Nulls) ReadColumn(SegmentColumnDescriptor column)
    {
        ArgumentNullException.ThrowIfNull(column);
        using FileStream stream = OpenChecked();
        byte[] values = ReadAt(stream, column.ValueOffset, column.ValueLength);
        byte[] nulls = column.Nullable ? ReadAt(stream, column.NullBitmapOffset, column.NullBitmapLength) : [];
        return (values, nulls);
    }

    private FileStream OpenChecked()
    {
        FileStream stream = directory.OpenOwnedFile(name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        if (stream.Length != length)
        {
            long found = stream.Length;
            stream.Dispose();
            throw new InvalidDataException(
                $"'{name}' is {found} bytes where its generation recorded {length}. A published segment never changes, so "
                + "it is not read.");
        }

        return stream;
    }

    private static byte[] ReadAt(FileStream stream, int offset, int count)
    {
        byte[] bytes = count == 0 ? [] : GC.AllocateUninitializedArray<byte>(count);
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }
}
