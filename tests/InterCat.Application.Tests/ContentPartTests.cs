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
