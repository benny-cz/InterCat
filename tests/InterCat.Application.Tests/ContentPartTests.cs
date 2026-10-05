using System.Globalization;
using System.Text;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A message part reassembled from its buffers for a person (M8's first step, ADR-037): the records of one exchange, one
/// event and one raising process, in their order, whole only when every buffer from the first to the last was kept.
/// </summary>
public sealed class ContentPartTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact(DisplayName = "I21: a part is assembled only from every buffer from its first to its last, each kept whole; else its gaps are named")]
    public void APartIsWholeOnlyWhenEveryBufferIsKept()
    {
        using var session = new TemporarySession();

        // Exchange 7's response body in three buffers, the last empty; exchange 8's with its middle buffer missing; and
        // exchange 9's request head cut by the 8-byte record limit. A TCP transfer beside them belongs to no part.
        ObservationRowV1[] rows =
        [
            Http(10, 2004, 1), Http(11, 2004, 2), Http(12, 2004, 3),
            Http(20, 2004, 4), Http(22, 2004, 6),
            Http(30, 2001, 7),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 8),
        ];
        (int Row, long Exchange, long Sequence, long Flags)[] parts =
            [(0, 7, 0, 1), (1, 7, 1, 0), (2, 7, 2, 2), (3, 8, 0, 1), (4, 8, 2, 2), (5, 9, 0, 3)];
        SourceFieldRowV1[] fields =
        [
            .. parts.SelectMany(part => new[]
            {
                Field(rows[part.Row], SourceField.HttpExchangeId, part.Exchange),
                Field(rows[part.Row], SourceField.ContentBufferSequence, part.Sequence),
                Field(rows[part.Row], SourceField.ContentBufferFlags, part.Flags),
            }),
        ];
        string[] messages = ["hel", "lo ", "", "ab", "", "POST /x HTTP/1.1\r\n\r\n"];
        Publish(session.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8),
            [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 8, ContentEncodingV1.Binary))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionContentPartDetail Part(int index, bool reveal = true) =>
            SessionContentPartQuery.Read(session.Store, page.SessionId, page.Records[index].Observation, reveal);

        // Every buffer of exchange 7's body was kept whole, so from any of them the part is whole, and its bytes are asked for.
        SessionContentPartDetail whole = Part(1);
        Assert.Equal((true, true, 6L), (whole.IsPart, whole.Complete, whole.Length!.Value));
        Assert.Equal("hello "u8.ToArray(), whole.Bytes);
        Assert.Equal("The response body of exchange 7: 3 buffers, from its first to its last, all kept whole - 6 bytes.", whole.Statement);
        Assert.Equal([0L, 1L, 2L], whole.Buffers.Select(buffer => buffer.Sequence));
        Assert.Null(Part(1, reveal: false).Bytes);
        Assert.Equal(
        [
            "-- Buffer 0, the part's first: 3 bytes, kept whole; bytes 0 to 2 of the part",
            ContentBytesView.Rows("hel"u8, 0).Single().Line,
            "-- Buffer 1: 3 bytes, kept whole; bytes 3 to 5 of the part",
            ContentBytesView.Rows("lo "u8, 0).Single().Line,
            "-- Buffer 2, the part's last: no bytes",
        ], ContentBytesView.PartLines(whole, new(0, 2), CultureInfo.InvariantCulture).Lines.Select(line => line.Line));

        // The record's facts say which buffer of which part it is, rather than that nothing joins it to another.
        Assert.Equal("Buffer 1 of the response body of exchange 7, of 3 recorded; every buffer of the part was kept whole, so it can be shown as one",
            ContentBytesView.Facts(page.Records[1].Content!, page.Records[1].Observation, 1, null, whole)
                .Single(fact => fact.Label == "Reassembly").Value);

        // A missing buffer, or one cut, leaves the part unassembled, and says which.
        SessionContentPartDetail gap = Part(3);
        Assert.False(gap.Complete);
        Assert.Null(gap.Bytes);
        Assert.Contains("buffer 1 was not recorded", gap.Statement, StringComparison.Ordinal);
        SessionContentPartDetail cut = Part(5);
        Assert.Equal((false, "the request head of exchange 9"), (cut.Complete, cut.Name));
        Assert.Contains("buffer 0 was not kept whole", cut.Statement, StringComparison.Ordinal);

        // A record of a source that numbers no parts belongs to none.
        Assert.False(Part(6).IsPart);
    }

    [Fact(DisplayName = "R22: an exchange number used again in one process ID is two parts, told apart in time, never one")]
    public void AReusedExchangeNumberIsTwoParts()
    {
        using var session = new TemporarySession();

        // WinINet numbers a process's exchanges from 1 (ADR-037), so a process ID used again numbers them again. Exchange 5's
        // response body twice - "abcd" in two buffers, then "EFGH" in three - and a third use that lost its first buffer. Beside
        // them, exchanges 6 and 7 at once, their buffers interleaved in time.
        ObservationRowV1[] rows =
        [
            Http(100, 2004, 1), Http(110, 2004, 2),
            Http(500, 2004, 3), Http(510, 2004, 4), Http(520, 2004, 5),
            Http(900, 2004, 6), Http(910, 2004, 7),
            Http(1_000, 2004, 8), Http(1_001, 2004, 9), Http(1_002, 2004, 10), Http(1_003, 2004, 11),
        ];
        (long Exchange, long Sequence, long Flags)[] places =
        [
            (5, 0, 1), (5, 1, 2),
            (5, 0, 1), (5, 1, 0), (5, 2, 2),
            (5, 1, 0), (5, 2, 2),
            (6, 0, 1), (7, 0, 1), (6, 1, 2), (7, 1, 2),
        ];
        SourceFieldRowV1[] fields =
        [
            .. places.SelectMany((place, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, place.Exchange),
                Field(rows[index], SourceField.ContentBufferSequence, place.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, place.Flags),
            }),
        ];
        string[] messages = ["ab", "cd", "EF", "GH", "", "xy", "z", "six-", "seven-", "6", "7"];
        Publish(session.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8),
            [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 8, ContentEncodingV1.Binary))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionContentPartDetail Part(int index) =>
            SessionContentPartQuery.Read(session.Store, page.SessionId, page.Records[index].Observation, revealBytes: true);

        // Each use of the number is its own part, whichever of its buffers is asked from, and never the other's bytes.
        SessionContentPartDetail first = Part(1);
        Assert.Equal((true, 2), (first.Complete, first.Buffers.Count));
        Assert.Equal("abcd"u8.ToArray(), first.Bytes);
        Assert.Contains("Exchange 5 names 3 exchanges of this process ID in the session", first.Statement, StringComparison.Ordinal);
        SessionContentPartDetail second = Part(2);
        Assert.Equal((true, 3), (second.Complete, second.Buffers.Count));
        Assert.Equal("EFGH"u8.ToArray(), second.Bytes);

        // The third use began after the second ended, so its missing first buffer is missing, not borrowed from another use.
        SessionContentPartDetail third = Part(6);
        Assert.False(third.Complete);
        Assert.Null(third.Bytes);
        Assert.Contains("its first buffer was not recorded", third.Statement, StringComparison.Ordinal);

        // Two exchanges at once: their buffers interleave in time, and each part is its own exchange's.
        Assert.Equal("six-6"u8.ToArray(), Part(9).Bytes);
        Assert.Equal("seven-7"u8.ToArray(), Part(8).Bytes);
        Assert.DoesNotContain("names", Part(8).Statement, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P2: a part that is not whole is shown with each gap in place, and no byte stands in for what is missing")]
    public void APartThatIsNotWholeShowsItsGaps()
    {
        using var session = new TemporarySession();

        // Exchange 8's response body: buffer 0 kept whole, buffer 1 never recorded, buffer 2 cut by the 8-byte record limit,
        // buffer 3 recorded after the capture reached its content limit, buffer 4 recorded with no content kept, and
        // nothing after it flagged last. Exchange 9's request head lost its first two buffers; exchange 10's response head
        // has one buffer its source did not flag first.
        ObservationRowV1[] rows =
        [
            Http(10, 2004, 1), Http(12, 2004, 2), Http(13, 2004, 3), Http(14, 2004, 4),
            Http(20, 2001, 5), Http(21, 2001, 6), Http(30, 2003, 7),
        ];
        (long Exchange, long Sequence, long Flags)[] places =
            [(8, 0, 1), (8, 2, 0), (8, 3, 0), (8, 4, 0), (9, 2, 0), (9, 3, 2), (10, 0, 2)];
        SourceFieldRowV1[] fields =
        [
            .. places.SelectMany((place, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, place.Exchange),
                Field(rows[index], SourceField.ContentBufferSequence, place.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, place.Flags),
            }),
        ];
        (ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes) omitted = Content(rows[2], "fifth"u8.ToArray(), 8, ContentEncodingV1.Binary);
        Publish(session.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8),
        [
            Content(rows[0], "head"u8.ToArray(), 8, ContentEncodingV1.Binary),
            Content(rows[1], "0123456789AB"u8.ToArray(), 8, ContentEncodingV1.Binary),
            (omitted.Fragment with { Disposition = ContentDispositionV1.OmittedBySessionLimit, Kept = 0 }, ReadOnlyMemory<byte>.Empty),
            Content(rows[4], "tail"u8.ToArray(), 8, ContentEncodingV1.Binary),
            Content(rows[5], "end"u8.ToArray(), 8, ContentEncodingV1.Binary),
            Content(rows[6], "solo"u8.ToArray(), 8, ContentEncodingV1.Binary),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionContentPartDetail Part(int index, bool reveal = true) =>
            SessionContentPartQuery.Read(session.Store, page.SessionId, page.Records[index].Observation, reveal);

        // Each gap is a piece of the part where it falls: what of it is known, and where it begins while that is known.
        SessionContentPartDetail body = Part(1);
        Assert.Equal((false, (byte[]?)null, true), (body.Complete, body.Bytes, body.BytesRead));
        Assert.Equal(
        [
            (ContentPartPieceKind.Kept, (long?)0, (long?)0, (long?)4, (long?)0),
            (ContentPartPieceKind.NotRecorded, 1, 1, null, 4),
            (ContentPartPieceKind.Kept, 2, 2, 8, null),
            (ContentPartPieceKind.Cut, 2, 2, 4, null),
            (ContentPartPieceKind.NotKept, 3, 3, 5, null),
            (ContentPartPieceKind.NotKept, 4, 4, null, null),
            (ContentPartPieceKind.NotRecorded, 5, null, null, null),
        ], body.Pieces.Select(piece => (piece.Kind, piece.FirstBuffer, piece.LastBuffer, piece.Length, piece.PartOffset)));
        Assert.Equal(["head", "01234567"], body.Pieces.Where(piece => piece.Bytes is not null)
            .Select(piece => Encoding.ASCII.GetString(piece.Bytes!.Value.Span)));
        Assert.EndsWith("It is shown with each gap in place and never as one, and nothing stands in for what is missing.",
            body.Statement, StringComparison.Ordinal);
        Assert.Equal("Buffer 2 of the response body of exchange 8, of 4 recorded; the part is not whole, so it is shown with its gaps in place, never as one",
            ContentBytesView.Facts(page.Records[1].Content!, page.Records[1].Observation, 1, Invariant, body)
                .Single(fact => fact.Label == "Reassembly").Value);

        // Shown, every gap is a line of its own between the buffers' bytes, each numbered from its buffer's first byte.
        Assert.Equal(new ContentBufferRange(0, 4), ContentBytesView.RecordedBuffers(body));
        ContentPartLines all = ContentBytesView.PartLines(body, new(0, 4), Invariant);
        Assert.Equal(
        [
            "-- Buffer 0, the part's first: 4 bytes, kept whole; bytes 0 to 3 of the part",
            ContentBytesView.Rows("head"u8, 0).Single().Line,
            "-- Buffer 1 was not recorded: its length is not known, and nothing stands in for it",
            "-- Buffer 2: its first 8 bytes kept",
            ContentBytesView.Rows("01234567"u8, 0).Single().Line,
            "-- The rest of buffer 2, 4 bytes, was cut by the 8-byte record limit; nothing stands in for it",
            "-- Buffer 3: none of its 5 bytes were kept, as the capture had reached its content limit; nothing stands in for them",
            "-- Buffer 4: none of its bytes were kept; nothing stands in for them",
            "-- What followed buffer 4 was not recorded: how much followed is not known, and nothing stands in for it",
        ], all.Lines.Select(line => line.Line));
        Assert.Equal((12L, (long?)null), (all.Bytes, all.StoppedBefore));
        Assert.Equal([false, false, true, false, false, true, true, true, true],
            all.Lines.Select(line => line is ContentMark { Gap: true }));
        Assert.Equal(ContentBytesView.Dump(all.Lines), string.Concat(all.Lines.Select(line => line.Line + Environment.NewLine)));

        // A chosen run of buffers shows its own, and the gaps among them; the gaps before or after the part only at its ends.
        Assert.Equal(["-- Buffer 1 was not recorded: its length is not known, and nothing stands in for it"],
            ContentBytesView.PartLines(body, new(1, 1), Invariant).Lines.Select(line => line.Line));
        Assert.Equal(4, ContentBytesView.PartLines(body, new(2, 3), Invariant).Lines.Count);

        // Buffers lost before the first recorded one, or a first its source did not flag, leave its place in the part unknown.
        SessionContentPartDetail head = Part(4);
        Assert.Equal(["-- Buffers 0 to 1 were not recorded: their lengths are not known, and nothing stands in for them",
            "-- Buffer 2: 4 bytes, kept whole", ContentBytesView.Rows("tail"u8, 0).Single().Line,
            "-- Buffer 3, the part's last: 3 bytes, kept whole", ContentBytesView.Rows("end"u8, 0).Single().Line],
            ContentBytesView.PartLines(head, ContentBytesView.RecordedBuffers(head)!.Value, Invariant).Lines.Select(line => line.Line));
        Assert.Equal(["-- Buffer 3, the part's last: 3 bytes, kept whole", ContentBytesView.Rows("end"u8, 0).Single().Line],
            ContentBytesView.PartLines(head, new(3, 3), Invariant).Lines.Select(line => line.Line));
        Assert.Equal(["-- What came before buffer 0 is not known: its source did not flag it the part's first, and nothing stands in for it",
            "-- Buffer 0, the part's last: 4 bytes, kept whole", ContentBytesView.Rows("solo"u8, 0).Single().Line],
            ContentBytesView.PartLines(Part(6), new(0, 0), Invariant).Lines.Select(line => line.Line));

        // Not asked for, no byte is read; a whole part's pieces are all kept, with no gap among them.
        Assert.False(Part(1, reveal: false).BytesRead);
        Assert.All(Part(1, reveal: false).Pieces, piece => Assert.Null(piece.Bytes));

        // A typed run of buffers is read within those recorded, and refused in words outside them.
        Assert.True(ContentBytesView.TryParseBuffers("2", "0x3", new(0, 4), out ContentBufferRange typed, out _));
        Assert.Equal(new ContentBufferRange(2, 3), typed);
        Assert.False(ContentBytesView.TryParseBuffers("3", "9", new(0, 4), out _, out string? problem));
        Assert.Equal("The part's recorded buffers are 0 to 4; choose buffers within them.", problem);
        Assert.False(ContentBytesView.TryParseBuffers("3", "2", new(0, 4), out _, out problem));
        Assert.Equal("The first buffer comes after the last.", problem);
        Assert.Equal(("buffer 3", "buffers 0 to 4"), (new ContentBufferRange(3, 3).Describe(Invariant), new ContentBufferRange(0, 4).Describe(Invariant)));
    }

    [Fact(DisplayName = "P2: a view of a part with gaps stops at the bytes it shows at once, at a buffer's start, and says so")]
    public void AGappedPartsViewStopsAtItsBound()
    {
        using var session = new TemporarySession();

        // Four 30,000-byte buffers of one body, the third never recorded: the view holds two of them, then stops.
        ObservationRowV1[] rows = [Http(10, 2004, 1), Http(11, 2004, 2), Http(13, 2004, 3)];
        (long Sequence, long Flags)[] places = [(0, 1), (1, 0), (3, 2)];
        SourceFieldRowV1[] fields =
        [
            .. places.SelectMany((place, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, 3),
                Field(rows[index], SourceField.ContentBufferSequence, place.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, place.Flags),
            }),
        ];
        byte[][] buffers = [.. Enumerable.Range(0, 3).Select(index => Enumerable.Repeat((byte)('a' + index), 30_000).ToArray())];
        Publish(session.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 32_768),
            [.. buffers.Select((bytes, index) => Content(rows[index], bytes, 32_768, ContentEncodingV1.Binary))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionContentPartDetail body = SessionContentPartQuery.Read(session.Store, page.SessionId, page.Records[0].Observation, revealBytes: true);

        ContentPartLines lines = ContentBytesView.PartLines(body, new(0, 3), Invariant);
        Assert.Equal((60_000L, (long?)3), (lines.Bytes, lines.StoppedBefore));
        Assert.Equal("-- The view shows at most 65,536 bytes at once, so it stops before buffer 3; choose from it to read on",
            lines.Lines[^1].Line);
        Assert.Contains("-- Buffer 2 was not recorded: its length is not known, and nothing stands in for it",
            lines.Lines.Select(line => line.Line));
        Assert.Equal(3_750, lines.Lines.Count(line => line is ContentHexRow));

        // From the buffer it stopped before, the view reads on.
        ContentPartLines on = ContentBytesView.PartLines(body, new(3, 3), Invariant);
        Assert.Equal((30_000L, (long?)null), (on.Bytes, on.StoppedBefore));
        Assert.Equal("-- Buffer 3, the part's last: 30,000 bytes, kept whole", on.Lines[0].Line);
    }

    [Fact(DisplayName = "P2: a buffer longer than a view shows at once is shown from its first byte, and the view says where it stopped")]
    public void ABufferLongerThanTheViewIsShownFromItsStart()
    {
        using var session = new TemporarySession();

        // One 70,000-byte buffer, then a missing one: the view shows the first 65,536 bytes and stops before the next.
        ObservationRowV1[] rows = [Http(10, 2004, 1), Http(12, 2004, 2)];
        (long Sequence, long Flags)[] places = [(0, 1), (2, 2)];
        SourceFieldRowV1[] fields =
        [
            .. places.SelectMany((place, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, 2),
                Field(rows[index], SourceField.ContentBufferSequence, place.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, place.Flags),
            }),
        ];
        byte[] large = [.. Enumerable.Range(0, 70_000).Select(index => (byte)index)];
        Publish(session.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 70_000),
            [Content(rows[0], large, 70_000, ContentEncodingV1.Binary), Content(rows[1], "z"u8.ToArray(), 70_000, ContentEncodingV1.Binary)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionContentPartDetail body = SessionContentPartQuery.Read(session.Store, page.SessionId, page.Records[0].Observation, revealBytes: true);

        ContentPartLines lines = ContentBytesView.PartLines(body, new(0, 2), Invariant);
        Assert.Equal((65_536L, (long?)1), (lines.Bytes, lines.StoppedBefore));
        Assert.Equal(4_096, lines.Lines.Count(line => line is ContentHexRow));
        Assert.Equal(ContentBytesView.Rows(large.AsSpan(65_520, 16), 65_520).Single().Line, lines.Lines[^2].Line);
        Assert.Equal("-- The view shows the first 65,536 of buffer 0's 70,000 bytes, the most it shows at once; its own record shows the rest",
            lines.Lines[^1].Line);

        // Chosen alone, the long buffer stops the view with nothing after it to read on to.
        Assert.Null(ContentBytesView.PartLines(body, new(0, 0), Invariant).StoppedBefore);
    }

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by process 4242, whose payload names no owner.</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, 1, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 4_242,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        };
}
