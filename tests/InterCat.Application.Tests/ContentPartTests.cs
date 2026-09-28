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
