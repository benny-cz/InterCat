using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A session's HTTP exchanges as the ladder reads them (ADR-037, M8): a process's exchanges a page at a time, each with what
/// was recorded of its four parts, and the records of the exchanges or of one - all from records' metadata and source
/// fields, never from their content.
/// </summary>
public sealed class SessionHttpExchangesTests
{
    [Fact(DisplayName = "R22: a process's HTTP exchanges are its numbers' uses in time, each part recorded whole or said not to be")]
    public void ExchangesAreTheirNumbersUsesInTime()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        ProcessInstanceId client = Client(session.Store);

        HttpChannelSummary channel = Assert.Single(SessionHttpExchanges.Channels(session.Store, client).Channels);
        Assert.Equal(HttpExchangeKeys.Channel(client), channel.Key);
        Assert.Equal((5L, 3L, 17L), (channel.Exchanges, channel.Complete, channel.Records));
        Assert.Equal((181L + 64 + 36 + 181 + 181 + 181, 115L + 900 + 100 + 115 + 115 + 50 + 115 + 7 + 115 + 9),
            (channel.RequestBytes, channel.ResponseBytes));
        Assert.Equal("5 exchanges · 2 not recorded whole · median 200 ns", channel.Outcome(CultureInfo.InvariantCulture));

        // Exchange 1's buffers and exchange 2's interleave in time. Exchange 1's number is used again after its first use
        // ended, and exchange 3's too, that use's request head lost: each use is an exchange of its own.
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, channel.Key);
        Assert.Null(page.Problem);
        Assert.False(page.More);
        Assert.Equal([10L, 15L, 40L, 100L, 200L], page.Exchanges.Select(row => row.Exchange.FirstTicks));
        Assert.Equal([1L, 2L, 3L, 1L, 3L], page.Exchanges.Select(row => row.Exchange.Number));
        Assert.Equal([true, true, false, true, false], page.Exchanges.Select(row => row.Exchange.Complete));
        Assert.Equal(5, page.Exchanges.Select(row => row.Key).Distinct().Count());

        // WinINet raised the buffer that ends exchange 1's request body after its response ended: it is still exchange 1's,
        // and the exchange took as long as its response, not as long as that buffer's lateness.
        HttpExchangeRow first = page.Exchanges[0];
        Assert.Equal("request 181 B + 100 B · response 115 B + 1,000 B", first.Parts(CultureInfo.InvariantCulture));
        Assert.Equal((2_000L, 31L), (first.Exchange.DurationNanoseconds!.Value, first.Exchange.LastTicks));
        Assert.Equal("request 0 B · response 115 B + 9 B · not recorded whole: request head",
            page.Exchanges[4].Parts(CultureInfo.InvariantCulture));
        Assert.Equal("request 181 B · response 115 B + 0 B", page.Exchanges[1].Parts(CultureInfo.InvariantCulture));

        // An exchange whose response's last buffer was not recorded has no duration, and says which part is not whole.
        HttpExchangeRow cut = page.Exchanges[2];
        Assert.Null(cut.Exchange.DurationNanoseconds);
        Assert.Equal("request 181 B · response 115 B + 50 B · not recorded whole: response body", cut.Parts(CultureInfo.InvariantCulture));

        // Each key finds its exchange again; a page reads on from an offset.
        Assert.True(HttpExchangeKeys.TryParseExchange(page.Exchanges[3].Key, out string parsed, out var located));
        Assert.Equal((channel.Key, 13UL), (parsed, located.Ordinal));
        HttpExchangePage second = SessionHttpExchanges.Exchanges(session.Store, channel.Key, offset: 1, pageSize: 2);
        Assert.Equal([15L, 40L], second.Exchanges.Select(row => row.Exchange.FirstTicks));
        Assert.True(second.More);

        // Within an interval an exchange counts where its last buffer falls, so each counts in exactly one place in time.
        HttpChannelSummary late = Assert.Single(SessionHttpExchanges.Channels(session.Store, client, new TimeRange(90, 200)).Channels);
        Assert.Equal((1L, 3L), (late.Exchanges, late.Records));

        // A process that made no exchange has none, and a key of it is a problem stated, not an empty page.
        ProcessInstanceId other = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 7).Id;
        Assert.Empty(SessionHttpExchanges.Channels(session.Store, other).Channels);
        Assert.NotNull(SessionHttpExchanges.Exchanges(session.Store, HttpExchangeKeys.Channel(other)).Problem);
    }

    [Fact(DisplayName = "I21: an exchange's evidence is its own buffers, and a process's exchanges are all of theirs")]
    public void AnExchangesEvidenceIsItsBuffers()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        string channel = HttpExchangeKeys.Channel(Client(session.Store));
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, channel);

        SessionEvidencePage exchange = SessionEvidenceQuery.Read(session.Store, operationKey: page.Exchanges[1].Key);
        Assert.Equal([15L, 25L, 26L], exchange.Records.Select(record => record.Observation.NativeTicks));
        Assert.Equal(page.Exchanges[1].Key, exchange.OperationKey);

        SessionEvidencePage all = SessionEvidenceQuery.Read(session.Store, operationKey: channel);
        Assert.Equal(17, all.Records.Count);
        Assert.All(all.Records, record => Assert.Equal(Mechanism.Http, record.Observation.Mechanism));

        // The rule that groups them is part of what a page's cursor names.
        Assert.NotEqual(exchange.QueryIdentity, all.QueryIdentity);
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, channelKey: "tcp:x", operationKey: channel));
    }

    private static ProcessInstanceId Client(SessionStore store) =>
        SessionOverviewProjector.Project(store).Nodes.Single(node => node.ProcessId == 4_242).Id;

    /// <summary>
    /// Five exchanges of process 4242: exchange 1 in six buffers, its request and response bodies in two each, the request
    /// body's last raised after the response ended, interleaved with exchange 2's three, whose request had no body; exchange
    /// 3, whose response body's last buffer was not recorded; exchange 1's number again; and exchange 3's again, its request
    /// head lost. Process 7 made none.
    /// </summary>
    private static ObservationRowV1[] Rows(out SourceFieldRowV1[] fields)
    {
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes)[] buffers =
        [
            (10, 2001, 1, 0, 3, 181), (11, 2002, 1, 0, 1, 64),
            (15, 2001, 2, 0, 3, 181),
            (20, 2003, 1, 0, 3, 115),
            (25, 2003, 2, 0, 3, 115), (26, 2004, 2, 0, 3, 0),
            (29, 2004, 1, 0, 1, 900), (30, 2004, 1, 1, 2, 100), (31, 2002, 1, 1, 2, 36),
            (40, 2001, 3, 0, 3, 181), (41, 2003, 3, 0, 3, 115), (42, 2004, 3, 0, 1, 50),
            (100, 2001, 1, 0, 3, 181), (101, 2003, 1, 0, 3, 115), (102, 2004, 1, 0, 3, 7),
            (200, 2003, 3, 0, 3, 115), (201, 2004, 3, 0, 3, 9),
        ];
        ObservationRowV1[] rows = [.. buffers.Select((buffer, index) => Http(buffer.Ticks, buffer.Event, (ulong)(index + 1), buffer.Bytes))];
        fields =
        [
            .. buffers.SelectMany((buffer, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, buffer.Number),
                Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        return [Lifecycle(1, ObservationKind.Create, 4_242, 100), Lifecycle(2, ObservationKind.Inventory, 7, 101), .. rows];
    }

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by process 4242, with a session time.</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 4_242,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
            SessionRelativeTicks = ticks * 100,
        };
}
