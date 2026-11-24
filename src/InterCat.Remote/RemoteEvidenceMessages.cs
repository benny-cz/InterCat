using System.Buffers.Binary;
using System.Text;

namespace InterCat.Remote;

/// <summary>
/// A page of a capture's evidence listing that a controller asks for (`remote-agent-v1` §4.5): the generation it listed
/// before (0 for the current one), the first file it wants and how many at most (0 for as many as a page holds).
/// </summary>
public sealed record RemoteListRequest(Guid CaptureId, long Generation, uint First, uint Most)
{
    /// <summary>
    /// Since version 1.7, whether a controller following the capture asks: a capture still recording is then listed by its
    /// newest generation, not refused (`remote-agent-v1` §4.11).
    /// </summary>
    public bool Following { get; init; }

    internal byte[] Encode()
    {
        RemoteFieldWriter writer = new RemoteFieldWriter()
            .WriteGuid(1, CaptureId)
            .WriteInt64(2, Generation)
            .WriteUInt32(3, First)
            .WriteUInt32(4, Most);
        return (Following ? writer.WriteBoolean(5, true) : writer).ToArray();
    }

    internal static RemoteListRequest Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), fields.ReadInt64(2), fields.ReadUInt32(3), fields.ReadUInt32(4))
        {
            Following = fields.Has(5) && fields.ReadBoolean(5),
        };
    }
}

/// <summary>
/// A page of a capture's evidence listing (`remote-agent-v1` §4.5): the generation listed and its pointer's bytes, how many
/// files it names and their bytes in all, and from <see cref="First"/> as many of them as one page holds.
/// </summary>
public sealed record RemoteEvidencePage(
    Guid CaptureId,
    long Generation,
    byte[] PointerBytes,
    uint Total,
    long TotalBytes,
    uint First,
    IReadOnlyList<RemoteEvidenceFile> Files)
{
    /// <summary>The most bytes a page's files take, so the page with its pointer stays within one frame's payload.</summary>
    internal const int MaximumFilesBytes = 56 * 1_024;

    /// <summary>
    /// Since version 1.7, whether the capture still records: a generation listed to a controller following it, which a
    /// newer one follows (`remote-agent-v1` §4.11).
    /// </summary>
    public bool StillRecording { get; init; }

    /// <summary>The bytes one file takes in a page: its name's length and name, its length and its SHA-256.</summary>
    internal static int EncodedSize(RemoteEvidenceFile file) => 1 + Encoding.ASCII.GetByteCount(file.Name) + 8 + RemoteEvidenceFile.Sha256Length;

    internal byte[] Encode()
    {
        byte[] files = new byte[Files.Sum(EncodedSize)];
        int at = 0;
        foreach (RemoteEvidenceFile file in Files)
        {
            int length = Encoding.ASCII.GetBytes(file.Name, files.AsSpan(at + 1));
            files[at] = checked((byte)length);
            at += 1 + length;
            BinaryPrimitives.WriteInt64LittleEndian(files.AsSpan(at), file.Length);
            at += 8;
            file.Sha256.CopyTo(files.AsSpan(at));
            at += RemoteEvidenceFile.Sha256Length;
        }

        RemoteFieldWriter writer = new RemoteFieldWriter()
            .WriteGuid(1, CaptureId)
            .WriteInt64(2, Generation)
            .WriteBytes(3, PointerBytes)
            .WriteUInt32(4, Total)
            .WriteInt64(5, TotalBytes)
            .WriteUInt32(6, First)
            .WriteBytes(7, files);
        return (StillRecording ? writer.WriteBoolean(8, true) : writer).ToArray();
    }

    internal static RemoteEvidencePage Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        byte[] packed = fields.ReadBytesUpTo(7, MaximumFilesBytes);
        var files = new List<RemoteEvidenceFile>();
        int at = 0;
        while (at < packed.Length)
        {
            int length = packed[at];
            if (length is 0 or > RemoteEvidenceNames.MaximumLength || packed.Length - at < 1 + length + 8 + RemoteEvidenceFile.Sha256Length)
            {
                throw new InvalidDataException("A file of the listing ends part way, or has no name.");
            }

            byte[] name = packed[(at + 1)..(at + 1 + length)];
            if (name.Any(character => character is < 0x21 or > 0x7E))
            {
                throw new InvalidDataException("A file's name in the listing is not printable ASCII.");
            }

            at += 1 + length;
            long size = BinaryPrimitives.ReadInt64LittleEndian(packed.AsSpan(at));
            at += 8;
            files.Add(new RemoteEvidenceFile(Encoding.ASCII.GetString(name), size, packed[at..(at + RemoteEvidenceFile.Sha256Length)]));
            at += RemoteEvidenceFile.Sha256Length;
        }

        return new(fields.ReadGuid(1), fields.ReadInt64(2), fields.ReadBytesUpTo(3, RemoteEvidenceSnapshot.MaximumPointerBytes),
            fields.ReadUInt32(4), fields.ReadInt64(5), fields.ReadUInt32(6), files)
        {
            StillRecording = fields.Has(8) && fields.ReadBoolean(8),
        };
    }
}

/// <summary>A read of a listed file of a capture's evidence: from which byte, and how many at most.</summary>
public sealed record RemoteReadRequest(Guid CaptureId, string Name, long Offset, uint Count)
{
    internal byte[] Encode() => new RemoteFieldWriter()
        .WriteGuid(1, CaptureId)
        .WriteText(2, Name, RemoteEvidenceNames.MaximumLength)
        .WriteInt64(3, Offset)
        .WriteUInt32(4, Count)
        .ToArray();

    internal static RemoteReadRequest Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadGuid(1), fields.ReadText(2, RemoteEvidenceNames.MaximumLength), fields.ReadInt64(3), fields.ReadUInt32(4));
    }
}

/// <summary>Bytes of an evidence file from <see cref="Offset"/>: as many as asked, fewer only at the file's end.</summary>
public sealed record RemoteFileBytes(long Offset, byte[] Bytes)
{
    /// <summary>The most bytes one read answers with: a field's most.</summary>
    public const int MaximumBytes = RemoteFields.MaximumFieldBytes;

    internal byte[] Encode() => new RemoteFieldWriter().WriteInt64(1, Offset).WriteBytes(2, Bytes).ToArray();

    internal static RemoteFileBytes Decode(byte[] payload)
    {
        RemoteFields fields = RemoteFields.Read(payload);
        return new(fields.ReadInt64(1), fields.ReadBytesUpTo(2, MaximumBytes));
    }
}
