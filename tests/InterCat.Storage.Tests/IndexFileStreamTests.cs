using System.Security.Cryptography;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// An index file read as it streams rather than held whole (revision 885): every field reads as the bytes held whole read
/// it, a window at a time, and a published file is refused for its length before a byte is read and for its digest once
/// every byte has been, as one read whole is, so a reader of a large index holds what it makes of the fields alone (§12).
/// </summary>
public sealed class IndexFileStreamTests
{
    [Fact(DisplayName = "§12: an index file read as it streams reads every field as its bytes held whole read them, across its window's edges and past its width, and refuses what they refuse")]
    public void AStreamReadsAsTheBytesHeldWhole()
    {
        // Fields enough to pass the 64 KiB window several times, a few straddling its edges, and a string wider than it.
        byte[] bytes = Written(writer =>
        {
            writer.U16(7);
            writer.Count(30_000);
            for (int field = 0; field < 30_000; field++)
            {
                writer.I64(field * 1_000_003L);
                writer.U8((byte)field);
                writer.Identity(new Guid(field, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
            }

            writer.Str32(new string('x', 200_000));
            writer.Str8("the end");
        });
        Assert.True(bytes.Length > 4 * 64 * 1024);

        string held = Read(new IndexFileReader(bytes, "test index"));
        using var stream = new MemoryStream(bytes);
        Assert.Equal(held, Read(new IndexFileReader(new TrickleStream(stream), bytes.Length, "test index")));

        // Cut short, followed by more, or counting more than it holds, it is refused in the same words either way.
        foreach (byte[] damaged in new[] { bytes[..^3], [.. bytes, 0], [.. bytes[..2], 0xFF, 0xFF, 0xFF, 0x7F] })
        {
            string whole = Assert.Throws<InvalidDataException>(() => Read(new IndexFileReader(damaged, "test index"))).Message;
            using var damagedStream = new MemoryStream(damaged);
            Assert.Equal(whole, Assert.Throws<InvalidDataException>(
                () => Read(new IndexFileReader(new TrickleStream(damagedStream), damaged.Length, "test index"))).Message);
        }

        // A stream shorter than it was said to hold ends inside a field.
        using var short_ = new MemoryStream(bytes[..100]);
        Assert.Equal("The test index is not readable: it ends inside a field.",
            Assert.Throws<InvalidDataException>(() => Read(new IndexFileReader(short_, bytes.Length, "test index"))).Message);
    }

    [Fact(DisplayName = "§12: a published file read as it streams is refused as one read whole is: of another length before a byte is read, and by its digest once every byte has been, even where its reader refused it first")]
    public void AStreamedFileIsRefusedAsOneReadWhole()
    {
        string path = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.IndexStream", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            IOwnedDirectory directory = LocalOwnedDirectory.Open(path);
            byte[] bytes = Written(writer =>
            {
                for (int field = 0; field < 20_000; field++)
                {
                    writer.I64(field);
                }
            });
            const string Name = "operation-index-0000000002.bin";
            File.WriteAllBytes(Path.Combine(path, Name), bytes);
            var published = new StoreDependency(Name, StoreDependencyKind.Index, bytes.Length,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));

            long Sum(Stream stream, int length)
            {
                var reader = new IndexFileReader(stream, length, "test index");
                long sum = 0;
                while (reader.Remaining > 0)
                {
                    sum += reader.I64();
                }

                return sum;
            }

            Assert.Equal(Enumerable.Range(0, 20_000).Sum(field => (long)field), SessionSegments.ReadVerified(directory, published, 1 << 20, Sum));

            // Another length is refused before a byte is read, in the words a whole read uses; past the bound, before the
            // file is opened.
            bool read = false;
            StoreDependency longer = published with { LengthBytes = bytes.Length + 1 };
            Assert.Equal(
                Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified(directory, longer, 1 << 20)).Message,
                Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified(directory, longer, 1 << 20, (stream, length) => read = true)).Message);
            Assert.False(read);
            Assert.Equal(
                Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified(directory, published, bytes.Length - 1)).Message,
                Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified(directory, published, bytes.Length - 1, Sum)).Message);

            // Bytes changed since they were published are refused by their digest, as read whole, whatever the reader made
            // of them - and where it refused them itself, the digest is still what is said.
            byte[] changed = [.. bytes];
            changed[^1] ^= 1;
            File.WriteAllBytes(Path.Combine(path, Name), changed);
            string whole = Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified(directory, published, 1 << 20)).Message;
            Assert.Equal(whole, Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified(directory, published, 1 << 20, Sum)).Message);
            Assert.Equal(whole, Assert.Throws<InvalidDataException>(() => SessionSegments.ReadVerified<long>(directory, published, 1 << 20,
                (stream, length) => throw new InvalidDataException("The test index is not readable: a field says so."))).Message);

            // The bytes as published, refused by their reader, are refused in its words.
            File.WriteAllBytes(Path.Combine(path, Name), bytes);
            Assert.Equal("The test index is not readable: a field says so.", Assert.Throws<InvalidDataException>(
                () => SessionSegments.ReadVerified<long>(directory, published, 1 << 20,
                    (stream, length) => throw new InvalidDataException("The test index is not readable: a field says so."))).Message);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static byte[] Written(Action<IndexFileWriter> write)
    {
        using var written = new MemoryStream();
        var writer = new IndexFileWriter(written, 1 << 30, "test index");
        write(writer);
        writer.Flush();
        return written.ToArray();
    }

    /// <summary>Every field read, as one comparable text; the reader refuses what it cannot read as it reads it.</summary>
    private static string Read(IndexFileReader reader)
    {
        var text = new System.Text.StringBuilder();
        _ = text.Append(reader.U16()).Append(';');
        int count = reader.Count(25);
        for (int field = 0; field < count; field++)
        {
            _ = text.Append(reader.I64()).Append(',').Append(reader.U8()).Append(',').Append(reader.Identity()).Append(';');
        }

        _ = text.Append(reader.Str32().Length).Append(';').Append(reader.Str8());
        reader.RequireEnd();
        return text.ToString();
    }

    /// <summary>A stream that hands over a few bytes at a read, as a file may, so a window is filled by several reads.</summary>
    private sealed class TrickleStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1_000));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
