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

    [Fact(DisplayName = "R22: an exchange whose request body's last buffer was not recorded says that body is not whole")]
    public void ARequestBodyNotRecordedWholeIsSaid()
    {
        // Exchange 1's request body ended in the buffer read at 31; the capture without it holds the body's first alone.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = HttpExchangeRows();
        ObservationRowV1 lost = rows.Single(row => row.NativeTicks == 31);
        using var session = new TemporarySession();
        Publish(session.Store, [.. rows.Where(row => row != lost)],
            fields: [.. fields.Where(field => field.RawRecordOrdinal != lost.RawRecordOrdinal)]);
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, HttpExchangeKeys.Channel(Client(session.Store)));

        HttpExchangeRow first = page.Exchanges[0];
        Assert.Equal((1L, 1, false), (first.Exchange.Number, first.Exchange.RequestBody.Buffers, first.Exchange.Complete));
        Assert.Equal([HttpPart.RequestBody], first.NotRecordedWhole());
        Assert.Equal("request 181 B + 64 B · response 115 B + 1,000 B · not recorded whole: request body",
            first.Parts(CultureInfo.InvariantCulture));

        // Its response still ended, so how long it took is known all the same.
        Assert.Equal(2_000L, first.Exchange.DurationNanoseconds);
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

    [Theory(DisplayName = "§10.2: HTTP buffers whose sizes add up past what a sum holds, in one part or across a channel's exchanges, are refused with why, never summed small")]
    [InlineData(8, 9)]
    [InlineData(4, 11)]
    [InlineData(8, 13)]
    public void BuffersPastASumAreRefused(int first, int second)
    {
        // Two buffers, each of a size no capture records: exchange 1's response body's two; exchange 2's and exchange 3's
        // request heads; exchange 1's and exchange 3's response bodies.
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows(out SourceFieldRowV1[] fields);
        Publish(session.Store,
            [.. rows.Select((row, index) => index == first || index == second ? row with { ByteValue = 5_000_000_000_000_000_000 } : row)],
            fields: fields);
        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => SessionHttpExchanges.Channels(session.Store, Client(session.Store)));
        Assert.Equal(SegmentMeasurement.PastASum, refusal.Message);
        Assert.True(SegmentMeasurement.IsUnsummable(refusal));
    }

    /// <summary>
    /// FX-HTTP-005 measured that an asynchronous WinINet client's exchanges are recorded as a synchronous one's, and that in
    /// either a buffer is raised on whichever of the client's threads finished the step it records: the thread that called,
    /// where the call finished at once, or one of WinINet's own, where it waited - every head among them. The fixture's
    /// ledger holds each buffer the lab probe captured of 64 exchanges made asynchronously eight at once, the server pacing
    /// its chunked responses, in time order with its exchange number, place, ends and length, the request its exchange's
    /// head named and which kind of thread raised it, never its bytes. Grouped as a session's records are, it is one
    /// exchange to a request, every buffer its request's, every part whole and as long as the server's truth.
    /// </summary>
    [Fact(DisplayName = "R22: FX-HTTP-005's asynchronous exchanges, eight at once and raised on the client's threads and WinINet's, are one exchange to a request, each part whole and as long as the server's truth")]
    public void AsynchronousExchangesAreOneToARequest()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-005", "evidence", "eight-at-once");
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request, bool Client)[] ledger =
        [
            .. File.ReadLines(Path.Combine(evidence, "buffers.csv")).Skip(1).Select(line => line.Split(',')).Select(field => (
                long.Parse(field[0], CultureInfo.InvariantCulture), ushort.Parse(field[1], CultureInfo.InvariantCulture),
                long.Parse(field[2], CultureInfo.InvariantCulture), long.Parse(field[3], CultureInfo.InvariantCulture),
                long.Parse(field[4], CultureInfo.InvariantCulture), long.Parse(field[5], CultureInfo.InvariantCulture),
                int.Parse(field[6], CultureInfo.InvariantCulture), field[7] == "client")),
        ];
        var truth = new Dictionary<(long Call, string Part), long>();
        foreach (string line in File.ReadLines(Path.Combine(evidence, "truth-http-server.jsonl")))
        {
            using var record = System.Text.Json.JsonDocument.Parse(line);
            if (record.RootElement.TryGetProperty("callId", out var call) && call.ValueKind == System.Text.Json.JsonValueKind.Number
                && record.RootElement.TryGetProperty("status", out var part) && part.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                truth[(call.GetInt64(), part.GetString()!)] = record.RootElement.GetProperty("declaredBytes").GetInt64();
            }
        }

        // Both kinds of thread raised buffers, and an exchange's buffers came from both.
        Assert.Contains(ledger, buffer => buffer.Client);
        Assert.Contains(ledger, buffer => !buffer.Client);
        Assert.Contains(ledger.GroupBy(buffer => buffer.Request), request => request.Select(buffer => buffer.Client).Distinct().Count() == 2);

        // Each buffer a record of the client, process 4242, raised on one of its threads that made requests or on another.
        ObservationRowV1[] buffers =
        [
            .. ledger.Select((buffer, index) => HttpBuffer(buffer.Ticks + 10, buffer.Event, (ulong)(index + 1), buffer.Bytes) with
            {
                HeaderThreadId = buffer.Client ? 4_243 : 4_244,
            }),
        ];
        SourceFieldRowV1[] fields =
        [
            .. ledger.SelectMany((buffer, index) => new[]
            {
                Field(buffers[index], SourceField.HttpExchangeId, buffer.Number),
                Field(buffers[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(buffers[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 100_000), .. buffers], fields: fields);

        HttpChannelSummary channel = Assert.Single(SessionHttpExchanges.Channels(session.Store, Client(session.Store)).Channels);
        Assert.Equal((64L, 64L, (long)ledger.Length), (channel.Exchanges, channel.Complete, channel.Records));
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, channel.Key);
        Assert.Equal(64, page.Exchanges.Count);

        // Each exchange is one request's, by the path its head named: every request once, every buffer of an exchange its
        // request's, and each part as long as the server's truth of it - a chunked body as its client read it.
        int[] requests = [.. page.Exchanges.Select(row => ledger[(int)row.Exchange.First.Ordinal - 1].Request)];
        Assert.Equal(Enumerable.Range(0, 64), requests.Order());
        foreach ((HttpExchangeRow row, int request) in page.Exchanges.Zip(requests))
        {
            HttpExchange exchange = row.Exchange;
            Assert.True(exchange.Complete, row.Parts(CultureInfo.InvariantCulture));
            Assert.Equal(
                (truth[(request, "request-head")], truth[(request, "request-body")], truth[(request, "response-head")], truth[(request, "response-body")]),
                (exchange.RequestHead.Bytes, exchange.RequestBody.Bytes, exchange.ResponseHead.Bytes, exchange.ResponseBody.Bytes));
            SessionEvidencePage records = SessionEvidenceQuery.Read(session.Store, operationKey: row.Key);
            Assert.Equal(exchange.Records, records.Records.Count);
            Assert.All(records.Records, record => Assert.Equal(request, ledger[(int)record.Observation.RawRecordOrdinal - 1].Request));
        }

        // They ran at once: most began before an earlier one's response ended.
        Assert.True(page.Exchanges.Select((row, index) => (row, index)).Count(pair => page.Exchanges.Take(pair.index)
            .Any(earlier => earlier.Exchange.ResponseEndNanoseconds > pair.row.Exchange.FirstNanoseconds)) > 32);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root holding InterCat.slnx was not found.");
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
