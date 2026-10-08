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

    [Fact(DisplayName = "R21: the timeline counts an exchange's buffers apart, the same records its evidence reads")]
    public void TheTimelineCountsAnExchangeApart()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, HttpExchangeKeys.Channel(Client(session.Store)));

        // Exchange 2's evidence scope is its timeline's focus: its three buffers, among exchange 1's interleaved with them.
        EvidenceScope scope = new("Records of an HTTP exchange", null, [], null, null) { OperationKey = page.Exchanges[1].Key };
        TimelineFocus focus = TimelineFocus.Of(scope)!;
        Assert.Equal(page.Exchanges[1].Key, focus.OperationKey);
        SessionFocusedTimeline timeline = SessionTimelineQuery.Focused(session.Store, new TimeRange(0, 400), 40, focus);
        Assert.Equal(3, timeline.Focus.Sum(bucket => bucket.ObservationCount));
        Assert.Equal([1, 2], timeline.Focus.Select((bucket, column) => (bucket, column)).Where(pair => pair.bucket.ObservationCount > 0)
            .Select(pair => pair.bucket.ObservationCount));

        // A process's exchanges are all of its buffers.
        TimelineFocus all = TimelineFocus.Of(new EvidenceScope("Records of HTTP exchanges", null, [], null, null)
            { OperationKey = HttpExchangeKeys.Channel(Client(session.Store)) })!;
        Assert.Equal(17, SessionTimelineQuery.Focused(session.Store, new TimeRange(0, 400), 40, all).Focus.Sum(bucket => bucket.ObservationCount));
    }

    [Fact(DisplayName = "§6.2: a process's HTTP exchanges in view are spans from their first buffer to their response's end, and density past the budget")]
    public void ExchangesInViewAreSpansOrDensity()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        string channel = HttpExchangeKeys.Channel(Client(session.Store));
        HttpExchangePage listed = SessionHttpExchanges.Exchanges(session.Store, channel);

        // Each exchange runs from its first buffer to the one that ended its response - exchange 1's late request-body
        // buffer at 31 is not its end - or, where its response's end was not recorded, to its last buffer.
        HttpExchangeSpanPage page = SessionHttpExchanges.Spans(session.Store, channel, new TimeRange(0, 400));
        Assert.Null(page.Problem);
        Assert.Null(page.Density);
        Assert.Equal(5, page.Total);
        Assert.Equal(
            [(10L, 30L, true, true), (15L, 26L, true, true), (40L, 42L, false, false), (100L, 102L, true, true), (200L, 201L, true, false)],
            page.Exchanges.Select(span => (span.FirstTicks, span.LastTicks, span.Ended, span.Complete)));
        Assert.Equal(listed.Exchanges.Select(row => row.Key), page.Exchanges.Select(span => span.Key));
        Assert.Equal((1L, 281L, 1_115L), (page.Exchanges[0].Number, page.Exchanges[0].RequestBytes, page.Exchanges[0].ResponseBytes));

        // An interval holds every exchange that runs in it, however it began or ended.
        Assert.Equal([10L, 15L, 40L], SessionHttpExchanges.Spans(session.Store, channel, new TimeRange(25, 41)).Exchanges
            .Select(span => span.FirstTicks));

        // Past the budget no exchange is dropped: each runs in every column from its first buffer's to its end's, and
        // those not recorded whole are counted apart.
        HttpExchangeSpanPage dense = SessionHttpExchanges.Spans(session.Store, channel, new TimeRange(0, 400), budget: 2, columns: 40);
        HttpExchangeDensity density = Assert.IsType<HttpExchangeDensity>(dense.Density);
        Assert.Empty(dense.Exchanges);
        Assert.Equal(5, dense.Total);
        long[] running = new long[40];
        running[1] = 2;
        running[2] = 2;
        running[3] = 1;
        running[4] = 1;
        running[10] = 1;
        running[20] = 1;
        long[] incomplete = new long[40];
        incomplete[4] = 1;
        incomplete[20] = 1;
        Assert.Equal(running, density.Running);
        Assert.Equal(incomplete, density.Incomplete);
        Assert.Equal((2L, new TimeRange(40, 50)), (density.Maximum, density.ColumnInterval(4)));

        // A process that made none has none to draw, which is said rather than drawn empty.
        ProcessInstanceId other = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 7).Id;
        Assert.NotNull(SessionHttpExchanges.Spans(session.Store, HttpExchangeKeys.Channel(other), new TimeRange(0, 400)).Problem);
    }

    internal static ProcessInstanceId Client(SessionStore store) =>
        SessionOverviewProjector.Project(store).Nodes.Single(node => node.ProcessId == 4_242).Id;

    /// <inheritdoc cref="TestSessions.HttpExchangeRows"/>
    internal static ObservationRowV1[] Rows(out SourceFieldRowV1[] fields)
    {
        (ObservationRowV1[] rows, fields) = HttpExchangeRows();
        return rows;
    }
}
