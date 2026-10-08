using System.Text;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The fixture decoder (§11.2): a small synthetic decoder that proves the model - its fields linked to the fragment it read
/// and to its version, what it did not decode said and why, and nothing decoded without consent or on its own.
/// </summary>
public sealed class ContentDecodingTests
{
    private const string Filler = "The quick brown fox jumps over the lazy dog. ";
    private static readonly Guid FixtureProvider =
        System.Diagnostics.Tracing.EventSource.GetGuid(typeof(ContentFixtureEventSource));

    [Fact(DisplayName = "P2: a fixture message decodes into the fields its bytes hold, each with its bytes, linked to the record and the decoder's version")]
    public void AFixtureMessageDecodesIntoItsFields()
    {
        string whole = "InterCat content fixture message 17 on conversation 3. " + Filler + "The quick";
        DecodedContent decoded = Decode(whole, recordLimit: 4_096);
        Assert.Equal(("intercat-content-fixture", "InterCat's content fixture decoder", 1),
            (decoded.DecoderId, decoded.Decoder, decoded.DecoderVersion));
        Assert.Equal("Decoded by InterCat's content fixture decoder, version 1", decoded.Heading);
        Assert.Equal(new ContentRange(0, whole.Length - 1), decoded.Read);
        Assert.Equal(
        [
            new("message", "17", new(33, 34)),
            new("conversation", "3", new(52, 52)),
            new("filler", $"the fixture's filler sentence, repeated ({whole.Length - 55} bytes)", new(55, whole.Length - 1)),
        ], decoded.Fields);
        Assert.Null(decoded.NotDecoded);

        // The decoding names the record whose fragment it read.
        using var session = new TemporarySession();
        SessionContentDetail detail = Detail(session, whole, 4_096, 7);
        DecodedContent again = ContentDecoders.Decode(detail)!;
        Assert.Equal(detail.Entry!.Fragment.RecordIn(detail.Entry.Header.CaptureId), again.Record);
        Assert.Equal(7UL, again.Record.RecordOrdinal);
        Assert.Equal(ContentDecoders.For(detail.Observation), again.Decoder);
    }

    [Fact(DisplayName = "P2: a fixture message the capture cut, or that ends or leaves its header, decodes only what its bytes show and says why the rest does not")]
    public void WhatIsNotDecodedIsSaid()
    {
        // Cut by the record limit: the header and the filler kept are decoded, and the rest is said never to have been kept.
        string message = "InterCat content fixture message 4 on conversation 2. " + string.Concat(Enumerable.Repeat(Filler, 4));
        DecodedContent cut = Decode(message, recordLimit: 64);
        Assert.Equal(["message", "conversation", "filler"], cut.Fields.Select(field => field.Name));
        Assert.Equal(new ContentRange(54, 63), cut.Fields[2].Bytes);
        Assert.Equal($"The capture kept the first 64 of the message's {message.Length} bytes; the rest was never kept, so it "
            + "is not decoded.", cut.NotDecoded);

        // A message that ends inside its header names nothing past the fields its bytes hold whole: a number the bytes end
        // on could have been longer, so it is not decoded.
        foreach ((string text, string[] fields, string why) in new[]
        {
            ("InterCat content fix", Array.Empty<string>(), "The message ends inside the fixture's header, at byte 20, so it names "
                + "no more than the fields decoded."),
            ("InterCat content fixture message 17", [], "The message ends inside the fixture's header, at byte 33, so it names no "
                + "more than the fields decoded."),
            ("InterCat content fixture message 17 on conv", ["message"], "The message ends inside the fixture's header, at byte "
                + "43, so it names no more than the fields decoded."),
        })
        {
            DecodedContent ended = Decode(text, recordLimit: 4_096);
            Assert.Equal(fields, ended.Fields.Select(field => field.Name));
            Assert.Equal(why, ended.NotDecoded);
        }

        // Kept bytes that end inside the header say so, not that the message does.
        Assert.Equal("The kept bytes end inside the fixture's header, at byte 16, so the rest of it, and what it names, is not "
            + "decoded.", Decode(message, recordLimit: 16).NotDecoded);

        // A byte message leaves the header at once, and a filler that leaves the sentence says where.
        DecodedContent bytes = Decode("\u0001\u0002binary", recordLimit: 4_096);
        Assert.Empty(bytes.Fields);
        Assert.Equal("At byte 0 it leaves the header a fixture text message begins with, so it carries no fields the decoder "
            + "reads; the fixture's byte messages are bytes alone.", bytes.NotDecoded);
        // A number is digits, and no more of them than a fixture's number has: where none begins, or one more follows, the
        // bytes leave the header there, and decode no number.
        foreach ((string text, string at) in new[]
        {
            ("InterCat content fixture message x", "At byte 33"),
            ("InterCat content fixture message 1234567890123456789 on conversation 1. ", "At byte 51"),
            ("InterCat content fixture message 7 on conversation . ", "At byte 51"),
        })
        {
            DecodedContent left = Decode(text, 4_096);
            Assert.Equal(text.Contains(" 7 ", StringComparison.Ordinal) ? ["message"] : [], left.Fields.Select(field => field.Name));
            Assert.StartsWith(at + " it leaves the header", left.NotDecoded, StringComparison.Ordinal);
        }

        DecodedContent longest = Decode("InterCat content fixture message 123456789012345678 on conversation 1. ", 4_096);
        Assert.Equal(new DecodedField("message", "123456789012345678", new(33, 50)), longest.Fields[0]);
        DecodedContent strayed = Decode("InterCat content fixture message 5 on conversation 1. The quick brown cat", 4_096);
        Assert.Equal("19 bytes, which leave the fixture's filler sentence at byte 70", strayed.Fields[2].Value);
        Assert.Null(strayed.NotDecoded);

        // Bytes kept from past the message's first byte hold no header to read, and nothing is decoded.
        using var later = new TemporarySession();
        ObservationRowV1 row = Fixture(10, 1);
        Publish(later.Store, [row], content: (ContentHeader(recordLimit: 64),
        [
            (new ContentFragmentV1(row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal,
                ContentClassificationV1.ApplicationPayload, Direction.Outbound, ContentEncodingV1.Binary,
                ContentDispositionV1.TruncatedByRecordLimit, 10, message.Length, 64), Encoding.Latin1.GetBytes(message[10..74])),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(later.Store);
        DecodedContent from = ContentDecoders.Decode(SessionContentQuery.Read(later.Store, page.SessionId, page.Records.Single(), true))!;
        Assert.Equal(((ContentRange?)null, 0, "The capture kept the message from byte 10, not from its first, where a fixture "
            + "message's header begins, so nothing is decoded."), (from.Read, from.Fields.Count, from.NotDecoded));
    }

    [Fact(DisplayName = "P2: a decoding reads at most what a view shows at once, within its time budget, and says where it stopped")]
    public void ADecodingIsBounded()
    {
        string header = "InterCat content fixture message 9 on conversation 3. ";
        string longest = header + string.Concat(Enumerable.Repeat(Filler, 2_000));
        DecodedContent read = Decode(longest, recordLimit: 1 << 20);
        Assert.Equal(new ContentRange(0, ContentDecoders.MaximumBytes - 1), read.Read);
        Assert.Equal(new ContentRange(header.Length, ContentDecoders.MaximumBytes - 1), read.Fields[2].Bytes);
        Assert.Equal("The decoder read the first 65,536 kept bytes, as many as a view shows at once; the rest is not decoded.",
            read.NotDecoded);

        // Past its budget, the decoder stops where it is and decodes no filler it did not finish reading.
        using var session = new TemporarySession();
        DecodedContent stopped = ContentDecoders.Decode(Detail(session, longest, 1 << 20, 1), TimeSpan.Zero)!;
        Assert.Equal(["message", "conversation"], stopped.Fields.Select(field => field.Name));
        Assert.Equal("The decoder stopped at its time budget, at byte 4,096; the rest is not decoded.", stopped.NotDecoded);
    }

    [Fact(DisplayName = "ADR-036: nothing is decoded without consent to inspect it, nor what no decoder reads, and an empty or omitted message says it holds nothing to decode")]
    public void NothingIsDecodedWithoutConsentOrDecoder()
    {
        // Kept without consent to inspect them, a fixture message's bytes are never decoded; an empty message and one
        // never kept hold nothing to decode, whatever the consent. None of them is read.
        using var session = new TemporarySession();
        ObservationRowV1[] rows = [Fixture(1, 1), Fixture(2, 2), Fixture(3, 3), Fixture(4, 4)];
        ObservationRowV1 tcp = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 9);
        Publish(session.Store, [.. rows, tcp], content: (ContentHeader(recordLimit: 64, inspection: ContentInspectionV1.Disabled),
        [
            Content(rows[0], Encoding.UTF8.GetBytes("InterCat content fixture message 1 on conversation 1. "), 64,
                ContentEncodingV1.Binary),
            Content(rows[1], ReadOnlyMemory<byte>.Empty, 64, ContentEncodingV1.Binary),
            (new ContentFragmentV1(rows[2].RawStreamId, rows[2].RawSourceEpoch, rows[2].RawRecordOrdinal,
                ContentClassificationV1.ApplicationPayload, Direction.Inbound, ContentEncodingV1.Binary,
                ContentDispositionV1.OmittedBySessionLimit, 0, 300, 0), ReadOnlyMemory<byte>.Empty),
            Content(tcp, "other"u8.ToArray(), 64, ContentEncodingV1.Binary),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionContentDetail Read(ulong ordinal) => SessionContentQuery.Read(session.Store, page.SessionId,
            page.Records.Single(record => record.Observation.RawRecordOrdinal == ordinal), revealBytes: true);
        Assert.Equal(
        [
            ("The capture kept these bytes without consent to inspect them, so they are never decoded.", null),
            ("The message holds no bytes, so there is nothing to decode.", null),
            ("No byte of the message was kept, so there is nothing to decode.", (ContentRange?)null),
        ], new ulong[] { 1, 2, 3 }.Select(ordinal => ContentDecoders.Decode(Read(ordinal))!)
            .Select(decoded => (decoded.NotDecoded, decoded.Read)));
        Assert.All(new ulong[] { 1, 2, 3 }, ordinal => Assert.Empty(ContentDecoders.Decode(Read(ordinal))!.Fields));

        // A record no decoder reads - one InterCat's fixture did not raise, or one whose content was not kept - is decoded
        // by none.
        Assert.Null(ContentDecoders.Decode(Read(9)));
        Assert.Null(ContentDecoders.Decode(Read(4)));
        Assert.Null(ContentDecoders.For(tcp));
        Assert.Null(ContentDecoders.For(Fixture(5, 5) with { EventId = 3 }));
        Assert.Null(ContentDecoders.For(tcp with { EventId = ContentFixtureEventSource.MessageSentId }));
        Assert.Equal("InterCat's content fixture decoder", ContentDecoders.For(Fixture(5, 5)));
        Assert.Equal("InterCat's content fixture decoder",
            ContentDecoders.For(Fixture(6, 6) with { EventId = ContentFixtureEventSource.MessageReceivedId }));

        // A decoding reads the kept bytes, which a caller must have read.
        using var unread = new TemporarySession();
        SessionContentDetail hidden = Detail(unread, "InterCat content fixture message 1 on conversation 1. ", 64, 1, reveal: false);
        Assert.Throws<InvalidOperationException>(() => ContentDecoders.Decode(hidden));
    }

    private static DecodedContent Decode(string message, int recordLimit)
    {
        using var session = new TemporarySession();
        return ContentDecoders.Decode(Detail(session, message, recordLimit, 1))!;
    }

    /// <summary>The kept content of one fixture record holding <paramref name="message"/>, as a person's read returns it.</summary>
    private static SessionContentDetail Detail(TemporarySession session, string message, int recordLimit, ulong ordinal, bool reveal = true)
    {
        ObservationRowV1 row = Fixture(10, ordinal);
        Publish(session.Store, [row], content: (ContentHeader(recordLimit: recordLimit),
            [Content(row, Encoding.Latin1.GetBytes(message), recordLimit, ContentEncodingV1.Binary)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        return SessionContentQuery.Read(session.Store, page.SessionId, page.Records.Single(), reveal);
    }

    /// <summary>A record InterCat's content fixture raised: a message its raiser sent.</summary>
    private static ObservationRowV1 Fixture(long ticks, ulong ordinal) =>
        Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 1, 100, ordinal) with
        {
            ProviderId = FixtureProvider,
            EventId = ContentFixtureEventSource.MessageSentId,
            Mechanism = Mechanism.ApplicationSdk,
            Layer = ObservationLayer.Application,
            ByteDomain = ByteDomain.ApplicationPayload,
        };
}
