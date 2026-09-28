using System.Text;
using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// The `content-v1` chunk (ADR-036): a record's kept content with what I21 asks of it, checked by its own checksums, and
/// every rule of `contracts/content-v1.md` §3 refused on write and on read.
/// </summary>
public sealed class ContentChunkTests
{
    private static readonly CaptureId Capture = new(Guid.Parse("11111111-2222-4333-8444-555555555555"));

    private static readonly ContentChunkHeaderV1 Header = new(Capture, "test-scoped-content-v1", 8, ContentInspectionV1.HexAndText);

    [Fact(DisplayName = "I21: a chunk keeps each record's content with its classification, direction, lengths and how it was cut")]
    public void AChunkKeepsWhatI21Asks()
    {
        (ContentFragmentV1, ReadOnlyMemory<byte>)[] fragments =
        [
            Fragment(10, "hello", ContentDispositionV1.Whole, original: 5),
            Fragment(11, "0123456789", ContentDispositionV1.TruncatedByRecordLimit, original: 10),
            Fragment(12, string.Empty, ContentDispositionV1.OmittedBySessionLimit, original: 300, direction: Direction.Inbound),
        ];
        using var stream = new MemoryStream();
        ContentChunkV1.Write(stream, Header, fragments);

        ContentChunkContentsV1 read = ContentChunkV1.Read(stream, Capture);
        Assert.Equal(Header, read.Header);
        Assert.Equal(fragments.Select(fragment => fragment.Item1), read.Entries.Select(entry => entry.Fragment));
        Assert.Equal("hello"u8.ToArray(), ContentChunkV1.ReadBytes(stream, read.Entries[0], 64));
        Assert.Equal("012"u8.ToArray(), ContentChunkV1.ReadBytes(stream, read.Entries[1], 3));
        Assert.Empty(ContentChunkV1.ReadBytes(stream, read.Entries[2], 64));
        Assert.Equal((Header, 3), ContentChunkV1.ReadHeader(stream));

        // A truncated fragment lacks what the limit cut; an omitted one lacks all of it; a whole one lacks nothing.
        Assert.Equal([0L, 2L, 300L], read.Entries.Select(entry => entry.Fragment.Missing!.Value));
        Assert.Equal("content-0000000042.icatc", ContentChunkV1.FileName(42));
        Assert.Equal(42L, ContentChunkV1.GenerationOf("content-0000000042.icatc"));
        Assert.Null(ContentChunkV1.GenerationOf("journal-0000000042.icatj"));
    }

    [Fact(DisplayName = "I21: a fragment that misstates how it was cut, or breaks record order, is never written")]
    public void AMisstatedFragmentIsNeverWritten()
    {
        void Refused(ContentChunkHeaderV1 header, params (ContentFragmentV1, ReadOnlyMemory<byte>)[] fragments)
        {
            using var stream = new MemoryStream();
            _ = Assert.Throws<ArgumentException>(() => ContentChunkV1.Write(stream, header, fragments));
            Assert.Equal(0, stream.Length);
        }

        Refused(Header, Fragment(11, "a", ContentDispositionV1.Whole, 1), Fragment(10, "b", ContentDispositionV1.Whole, 1));
        Refused(Header, Fragment(10, "a", ContentDispositionV1.Whole, 1), Fragment(10, "b", ContentDispositionV1.Whole, 1));
        Refused(Header, Fragment(10, "123456789", ContentDispositionV1.Whole, 9));
        Refused(Header, Fragment(10, "abc", ContentDispositionV1.Whole, 4));
        Refused(Header, Fragment(10, "abc", ContentDispositionV1.TruncatedByRecordLimit, 10));
        Refused(Header, Fragment(10, "01234567", ContentDispositionV1.TruncatedByRecordLimit, 8));
        Refused(Header, Fragment(10, "01234567", ContentDispositionV1.TruncatedByRecordLimit, null));
        Refused(Header, Fragment(10, "a", ContentDispositionV1.OmittedBySessionLimit, 5));
        Refused(Header, Fragment(10, "a", ContentDispositionV1.Whole, 1, direction: Direction.Bidirectional));
        Refused(Header, (Fragment(10, "abc", ContentDispositionV1.Whole, 3).Item1, "ab"u8.ToArray()));
        Refused(Header with { PolicyId = string.Empty }, Fragment(10, "a", ContentDispositionV1.Whole, 1));
        Refused(Header with { PolicyId = "politika-č" }, Fragment(10, "a", ContentDispositionV1.Whole, 1));
        Refused(Header with { RecordLimit = 0 }, Fragment(10, string.Empty, ContentDispositionV1.Whole, 0));
        Refused(Header with { CaptureId = new(Guid.Empty) }, Fragment(10, "a", ContentDispositionV1.Whole, 1));
    }

    [Fact(DisplayName = "I21: a chunk whose bytes changed, or that belongs to another capture, is refused whole")]
    public void ADamagedChunkIsRefused()
    {
        using var stream = new MemoryStream();
        ContentChunkV1.Write(stream, Header,
        [
            Fragment(10, "hello", ContentDispositionV1.Whole, 5),
            Fragment(11, "world", ContentDispositionV1.Whole, 5),
        ]);
        byte[] bytes = stream.ToArray();
        int policyEnd = 8 + 2 + 2 + 16 + 1 + Header.PolicyId.Length;

        void Refused(byte[] damaged, string reason, CaptureId? capture = null)
        {
            using var copy = new MemoryStream(damaged);
            InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => ContentChunkV1.Read(copy, capture ?? Capture));
            Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
        }

        byte[] Changed(int at, byte value)
        {
            byte[] copy = [.. bytes];
            copy[at] = value;
            return copy;
        }

        Refused(Changed(bytes.Length - 6, (byte)(bytes[^6] ^ 0xFF)), "does not match its checksum");
        Refused(Changed(policyEnd, (byte)(bytes[policyEnd] ^ 0x01)), "header does not match its checksum");
        Refused(Changed(0, (byte)'X'), "magic");
        Refused(Changed(8, 2), "version 2.0");
        Refused([.. bytes, 0], "follow its last fragment");
        Refused(bytes[..^5], "ends inside");
        Refused(bytes, "not this session's", new CaptureId(Guid.NewGuid()));
    }

    private static (ContentFragmentV1, ReadOnlyMemory<byte>) Fragment(
        ulong ordinal,
        string text,
        ContentDispositionV1 disposition,
        long? original,
        Direction direction = Direction.Outbound)
    {
        byte[] message = Encoding.UTF8.GetBytes(text);
        byte[] kept = disposition == ContentDispositionV1.TruncatedByRecordLimit ? message[..Math.Min(message.Length, 8)] : message;
        return (new ContentFragmentV1(1, 1, ordinal, ContentClassificationV1.ApplicationPayload, direction,
            ContentEncodingV1.Utf8, disposition, 0, original, kept.Length), kept);
    }
}
