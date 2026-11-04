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
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request, bool Client)[] ledger =
            GroupedAsTheirRequests("FX-HTTP-005", heads: true);

        // Both kinds of thread raised buffers, and an exchange's buffers came from both.
        Assert.Contains(ledger, buffer => buffer.Client);
        Assert.Contains(ledger, buffer => !buffer.Client);
        Assert.Contains(ledger.GroupBy(buffer => buffer.Request), request => request.Select(buffer => buffer.Client).Distinct().Count() == 2);
    }

    /// <summary>
    /// FX-HTTP-006 measured that WinINet keeps an exchange it made over HTTP/2 as it keeps one over HTTP/1.1 - its bodies as
    /// its client sent and read them, in the same buffers, numbered and flagged - but its heads in HTTP/1.1's form, which no
    /// wire carried, so the server's truth holds no head's bytes. Eight clients' 64 exchanges went over one HTTP/2
    /// connection, its streams interleaved. The ledger of that run, grouped as a session's records are, is one exchange to
    /// a request, every buffer its request's, every head recorded whole and every body as long as the server's truth.
    /// </summary>
    [Fact(DisplayName = "R22: FX-HTTP-006's exchanges over HTTP/2, eight clients' multiplexed on one connection, are one exchange to a request, each part whole and each body as long as the server's truth")]
    public void Http2ExchangesAreOneToARequest()
    {
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request, bool Client)[] ledger =
            GroupedAsTheirRequests("FX-HTTP-006", heads: false);

        // The server's account: one connection carried every request, each over HTTP/2.
        string[] truth = [.. File.ReadLines(Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-006", "evidence", "eight-at-once", "truth-http-server.jsonl"))];
        Assert.Single(truth.Where(line => line.Contains("\"status\":\"request-connection\"", StringComparison.Ordinal))
            .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("resourceName").GetString()).Distinct());
        Assert.Equal(64, truth.Count(line => line.Contains("\"status\":\"request-protocol\"", StringComparison.Ordinal)
            && line.Contains("\"resourceName\":\"HTTP/2\"", StringComparison.Ordinal)));
        Assert.Equal(387, ledger.Length);
    }

    /// <summary>
    /// FX-HTTP-007 measured that a request body its client writes in pieces with InternetWriteFile, after sending the head
    /// with HttpSendRequestEx, is kept a buffer to a write - a write past 64,511 bytes cut as a whole body is - and ended by
    /// an empty buffer flagged last, as a body handed to HttpSendRequest whole is. The ledger of eight clients' 64 exchanges,
    /// their bodies written in pieces of up to 4,096 bytes, grouped as a session's records are, is one exchange to a
    /// request, each part whole and as long as the server's truth, and each body as many buffers as its client's writes and
    /// the one that ends it.
    /// </summary>
    [Fact(DisplayName = "R22: FX-HTTP-007's request bodies, written in pieces eight clients at once, are each its exchange's, a buffer to a write and one to end it, every part whole and as long as the server's truth")]
    public void BodiesWrittenInPiecesAreTheirExchanges()
    {
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request, bool Client)[] ledger =
            GroupedAsTheirRequests("FX-HTTP-007", heads: true);

        // The client's own account of each body's writes, in order: the buffers holding bytes are those writes, one to one.
        var writes = new Dictionary<long, List<long>>();
        foreach (string line in File.ReadLines(Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-007", "evidence", "eight-at-once", "truth-http-client.jsonl")))
        {
            using var record = System.Text.Json.JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("status").ValueKind == System.Text.Json.JsonValueKind.String
                && record.RootElement.GetProperty("status").GetString() == "request-body-write")
            {
                long call = record.RootElement.GetProperty("callId").GetInt64();
                if (!writes.TryGetValue(call, out List<long>? lengths)) writes[call] = lengths = [];
                lengths.Add(record.RootElement.GetProperty("declaredBytes").GetInt64());
            }
        }

        Assert.Equal(63, writes.Count);
        Assert.True(writes.Values.Sum(lengths => lengths.Count) > 300);
        foreach ((long call, List<long> lengths) in writes)
        {
            (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request, bool Client)[] body =
                [.. ledger.Where(buffer => buffer.Request == call && buffer.Event == 2002).OrderBy(buffer => buffer.Sequence)];
            Assert.Equal(lengths, body.Where(buffer => buffer.Bytes > 0).Select(buffer => buffer.Bytes));
            Assert.Equal((0L, 2L), (body[^1].Bytes, body[^1].Flags));
        }
    }

    /// <summary>
    /// FX-HTTP-009 measured that WinINet follows a redirect by itself, and that its capture keeps the request it makes to the
    /// location as an exchange of its own, numbered apart - three to fifteen after its redirect's, eight clients at once -
    /// and begun once the redirect's response ended: a POST answered 302 asked again as a GET of no body, and one answered
    /// 307, once its client confirmed it, as the same POST with the same body. Each run's ledger of 64 requests, grouped as
    /// a session's records are, each head rebuilt and checked against the server's truth byte for byte, is two exchanges to
    /// a request, each part whole and as long as the server's truth of its leg; and each redirect names its location and is
    /// followed by its own request there, the first listed after it to ask for it.
    /// </summary>
    [Theory(DisplayName = "R22: FX-HTTP-009's redirected requests, eight clients at once, are two exchanges each - the redirect and the request WinINet made to its location - each part whole, and each redirect followed by its own")]
    [InlineData("302-eight-at-once", 302, "Found", "GET")]
    [InlineData("307-eight-at-once", 307, "Temporary Redirect", "POST")]
    public void RedirectsAreTwoExchangesEachFollowedByItsOwn(string run, int status, string reason, string method)
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-009", "evidence", run);
        LedgerBuffer[] ledger = LedgerOf(evidence);
        Dictionary<(long Call, string Part), (long Length, string Hash)> truth = ServerTruthOf(evidence);

        using var scenario = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json")));
        string host = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{scenario.RootElement.GetProperty("port").GetInt32()}");

        // Each leg's heads as the wire carried them, rebuilt and checked against the server's truth byte for byte: the
        // request the server redirected and its redirect, then the request WinINet made to the location and its answer.
        byte[] Head(int request, bool moved, bool response)
        {
            const string Fixture = "X-InterCat-Fixture: FX-HTTP-009\r\n";
            string text = (moved, response) switch
            {
                (false, false) => string.Create(CultureInfo.InvariantCulture,
                    $"POST /fx/{request} HTTP/1.1\r\n{Fixture}User-Agent: InterCat-FX-HTTP-009\r\nHost: {host}\r\nContent-Length: {truth[(request, "redirected-request-body")].Length}\r\nConnection: Keep-Alive\r\nCache-Control: no-cache\r\n\r\n"),
                (false, true) => string.Create(CultureInfo.InvariantCulture,
                    $"HTTP/1.1 {status} {reason}\r\nLocation: /fx/{request}/moved\r\nContent-Length: 0\r\n{Fixture}\r\n"),
                // Followed by WinINet alone, the GET keeps the request's own fields but its body's; sent again by its client,
                // the POST takes the client's field anew, after WinINet's own.
                (true, false) when method == "GET" => string.Create(CultureInfo.InvariantCulture,
                    $"GET /fx/{request}/moved HTTP/1.1\r\n{Fixture}User-Agent: InterCat-FX-HTTP-009\r\nHost: {host}\r\nConnection: Keep-Alive\r\nCache-Control: no-cache\r\n\r\n"),
                (true, false) => string.Create(CultureInfo.InvariantCulture,
                    $"POST /fx/{request}/moved HTTP/1.1\r\nUser-Agent: InterCat-FX-HTTP-009\r\nHost: {host}\r\nContent-Length: {truth[(request, "request-body")].Length}\r\nConnection: Keep-Alive\r\nCache-Control: no-cache\r\n{Fixture}\r\n"),
                (true, true) => string.Create(CultureInfo.InvariantCulture,
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {truth[(request, "response-body")].Length}\r\n{Fixture}\r\n"),
            };
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
            string logged = (moved ? string.Empty : response ? "redirect-" : "redirected-") + (response ? "response-head" : "request-head");
            Assert.Equal(truth[(request, logged)], (bytes.LongLength, Sha256(bytes)));
            return bytes;
        }

        Assert.Equal(256, ledger.Count(buffer => buffer.Event is 2001 or 2003));
        using TemporarySession session = Published(ledger, buffer => Head(buffer.Request, buffer.Moved, response: buffer.Event == 2003));

        HttpChannelSummary channel = Assert.Single(SessionHttpExchanges.Channels(session.Store, Client(session.Store)).Channels);
        Assert.Equal((128L, 128L, (long)ledger.Length), (channel.Exchanges, channel.Complete, channel.Records));
        HttpExchangePage page = SessionHttpExchanges.ExchangesUpTo(session.Store, channel.Key, 1_000);
        HttpExchangeTableRead table = SessionHttpExchanges.Table(session.Store, page.SessionId, channel.Key, 1_000);
        Assert.Equal(page.Exchanges.Select(row => row.Key), table.Lines.Select(line => line.Key));

        // Each exchange is one leg of one request, by its first buffer: every buffer of it that leg's, every part whole and
        // as long as the server's truth of that leg.
        var legs = new Dictionary<(int Request, bool Moved), (HttpExchangeRow Row, HttpExchangeLine Line)>();
        foreach ((HttpExchangeRow row, HttpExchangeLine line) in page.Exchanges.Zip(table.Lines))
        {
            var first = ledger[(int)row.Exchange.First.Ordinal - 1];
            legs.Add((first.Request, first.Moved), (row, line));
            HttpExchange exchange = row.Exchange;
            Assert.True(exchange.Complete, row.Parts(CultureInfo.InvariantCulture));
            (string requestPart, string responsePart) = first.Moved ? ("request-", "response-") : ("redirected-request-", "redirect-response-");
            Assert.Equal(
                (truth[(first.Request, requestPart + "head")].Length, truth[(first.Request, requestPart + "body")].Length,
                    truth[(first.Request, responsePart + "head")].Length, truth[(first.Request, responsePart + "body")].Length),
                (exchange.RequestHead.Bytes, exchange.RequestBody.Bytes, exchange.ResponseHead.Bytes, exchange.ResponseBody.Bytes));
            SessionEvidencePage records = SessionEvidenceQuery.Read(session.Store, operationKey: row.Key);
            Assert.Equal(exchange.Records, records.Records.Count);
            Assert.All(records.Records, record =>
                Assert.Equal((first.Request, first.Moved), (ledger[(int)record.Observation.RawRecordOrdinal - 1].Request, ledger[(int)record.Observation.RawRecordOrdinal - 1].Moved)));
        }

        // Each redirect names its location, and is followed by its own request there: numbered apart, so its number is no
        // guide, and begun once the redirect's response ended. The request there redirects nowhere, and no body is said to
        // disagree with its head.
        Assert.Equal(128, legs.Count);
        for (int request = 0; request < 64; request++)
        {
            (HttpExchangeRow redirected, HttpExchangeLine redirectedLine) = legs[(request, false)];
            (HttpExchangeRow moved, HttpExchangeLine movedLine) = legs[(request, true)];
            string location = string.Create(CultureInfo.InvariantCulture, $"/fx/{request}/moved");
            Assert.Equal((string.Create(CultureInfo.InvariantCulture, $"POST /fx/{request}"), method + " " + location),
                (redirectedLine.Heads!.Asked, movedLine.Heads!.Asked));
            Assert.Equal(new HttpRedirect(status.ToString(CultureInfo.InvariantCulture), location, location, host), redirectedLine.Heads.Redirect);
            Assert.Equal(new HttpFollowUp(moved.Key, moved.Exchange.Number), redirectedLine.FollowedBy);
            Assert.Equal(((HttpRedirect?)null, (HttpFollowUp?)null), (movedLine.Heads.Redirect, movedLine.FollowedBy));
            Assert.True(moved.Exchange.Number - redirected.Exchange.Number > 1);
            Assert.True(moved.Exchange.FirstNanoseconds >= redirected.Exchange.ResponseEndNanoseconds);
            Assert.All(new[] { (redirectedLine, redirected), (movedLine, moved) }, leg =>
            {
                Assert.False(leg.Item1.Heads!.RequestBody(leg.Item2.Exchange.RequestBody)!.Contradicts);
                Assert.False(leg.Item1.Heads.ResponseBody(leg.Item2.Exchange.ResponseBody)!.Contradicts);
            });
        }
    }

    /// <summary>
    /// FX-HTTP-010 measured that WinINet's capture keeps a request it makes through a proxy as it sent it there: over HTTP
    /// in absolute form, its target the whole address; over TLS through a tunnel it first asks the proxy to CONNECT, which
    /// it keeps as an exchange of its own - a head, and an answer of no body - and the request it then sends through the
    /// tunnel as over TLS directly, an exchange of its own too, which no record ties to the CONNECT. Each run's ledger of
    /// eight clients' 64 requests, grouped as a session's records are, each head rebuilt and checked against the server's
    /// truth byte for byte, is one exchange to a request and, over TLS, one to each CONNECT: every part whole and as long as
    /// the server's truth, each request a proxy was asked for and each CONNECT said so, and each CONNECT's answer said to
    /// hold no body.
    /// </summary>
    [Theory(DisplayName = "R22: FX-HTTP-010's requests through a proxy, eight clients at once, are each one exchange and over TLS each CONNECT one more, every part whole and each said to have gone to a proxy")]
    [InlineData("http-eight-at-once", false)]
    [InlineData("tls-eight-at-once", true)]
    public void RequestsThroughAProxyAreTheirExchanges(string run, bool tls)
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-010", "evidence", run);
        LedgerBuffer[] ledger = LedgerOf(evidence);
        var connects = new List<(string Part, long Length, string Hash)>();
        Dictionary<(long Call, string Part), (long Length, string Hash)> truth = ServerTruthOf(evidence, connects);

        // Each head as the wire to the proxy carried it, rebuilt and checked against the server's truth byte for byte: over
        // HTTP each request in absolute form; over TLS each CONNECT and its answer, alike every time, then each request in
        // origin form.
        const string Fixture = "X-InterCat-Fixture: FX-HTTP-010\r\n";
        byte[] connect = "CONNECT fx.invalid:443 HTTP/1.0\r\nUser-Agent: InterCat-FX-HTTP-010\r\nHost: fx.invalid:443\r\nContent-Length: 0\r\nProxy-Connection: Keep-Alive\r\nPragma: no-cache\r\n\r\n"u8.ToArray();
        byte[] established = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        Assert.Equal((tls ? 64 : 0, tls ? 64 : 0), (connects.Count(entry => entry.Part == "connect-request-head"),
            connects.Count(entry => entry.Part == "connect-response-head")));
        Assert.All(connects, entry => Assert.Equal(
            entry.Part == "connect-request-head" ? (connect.LongLength, Sha256(connect)) : (established.LongLength, Sha256(established)),
            (entry.Length, entry.Hash)));
        byte[] Head(LedgerBuffer buffer)
        {
            if (buffer.Request < 0)
            {
                return buffer.Event == 2001 ? connect : established;
            }

            int request = buffer.Request;
            string text = buffer.Event == 2003
                ? string.Create(CultureInfo.InvariantCulture,
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {truth[(request, "response-body")].Length}\r\n{Fixture}\r\n")
                : tls
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"POST /fx/{request} HTTP/1.1\r\n{Fixture}User-Agent: InterCat-FX-HTTP-010\r\nHost: fx.invalid\r\nContent-Length: {truth[(request, "request-body")].Length}\r\nConnection: Keep-Alive\r\nCache-Control: no-cache\r\n\r\n")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"POST http://fx.invalid/fx/{request} HTTP/1.1\r\n{Fixture}User-Agent: InterCat-FX-HTTP-010\r\nHost: fx.invalid\r\nContent-Length: {truth[(request, "request-body")].Length}\r\nProxy-Connection: Keep-Alive\r\nPragma: no-cache\r\n\r\n");
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
            Assert.Equal(truth[(request, buffer.Event == 2003 ? "response-head" : "request-head")], (bytes.LongLength, Sha256(bytes)));
            return bytes;
        }

        using TemporarySession session = Published(ledger, Head);
        HttpChannelSummary channel = Assert.Single(SessionHttpExchanges.Channels(session.Store, Client(session.Store)).Channels);
        long exchanges = tls ? 128 : 64;
        Assert.Equal((exchanges, exchanges, (long)ledger.Length), (channel.Exchanges, channel.Complete, channel.Records));
        HttpExchangePage page = SessionHttpExchanges.ExchangesUpTo(session.Store, channel.Key, 1_000);
        HttpExchangeTableRead table = SessionHttpExchanges.Table(session.Store, page.SessionId, channel.Key, 1_000);
        Assert.Equal(page.Exchanges.Select(row => row.Key), table.Lines.Select(line => line.Key));

        // Each exchange is one request's or one CONNECT's, by its first buffer, every buffer of it its own and each part
        // whole and as long as the server's truth: a request in absolute form said to have asked a proxy for it, a CONNECT
        // to have opened a tunnel and its answer to hold no body, and a request through the tunnel nothing of a proxy.
        var requests = new HashSet<int>();
        int tunnels = 0;
        foreach ((HttpExchangeRow row, HttpExchangeLine line) in page.Exchanges.Zip(table.Lines))
        {
            LedgerBuffer first = ledger[(int)row.Exchange.First.Ordinal - 1];
            HttpExchange exchange = row.Exchange;
            Assert.True(exchange.Complete, row.Parts(CultureInfo.InvariantCulture));
            SessionEvidencePage records = SessionEvidenceQuery.Read(session.Store, operationKey: row.Key);
            Assert.Equal(exchange.Records, records.Records.Count);
            Assert.All(records.Records, record => Assert.Equal(first.Number, ledger[(int)record.Observation.RawRecordOrdinal - 1].Number));
            HttpExchangeHeads heads = line.Heads!;
            if (first.Request < 0)
            {
                tunnels++;
                Assert.Equal((connect.LongLength, 0L, established.LongLength, 0L),
                    (exchange.RequestHead.Bytes, exchange.RequestBody.Bytes, exchange.ResponseHead.Bytes, exchange.ResponseBody.Bytes));
                Assert.Equal(("CONNECT fx.invalid:443", "tunnel"), (heads.Asked, heads.Proxy?.Kind));
                Assert.Equal(new HttpBodyDeclaration("As a 200 response to CONNECT, it holds no body - its connection became a tunnel - and "
                    + "its recorded buffers hold none.", false), heads.ResponseBody(exchange.ResponseBody));
                continue;
            }

            Assert.True(requests.Add(first.Request));
            Assert.Equal(
                (truth[(first.Request, "request-head")].Length, truth[(first.Request, "request-body")].Length,
                    truth[(first.Request, "response-head")].Length, truth[(first.Request, "response-body")].Length),
                (exchange.RequestHead.Bytes, exchange.RequestBody.Bytes, exchange.ResponseHead.Bytes, exchange.ResponseBody.Bytes));
            Assert.Equal(tls ? null : "request", heads.Proxy?.Kind);
            Assert.False(heads.RequestBody(exchange.RequestBody)!.Contradicts);
            Assert.False(heads.ResponseBody(exchange.ResponseBody)!.Contradicts);
        }

        Assert.Equal((64, tls ? 64 : 0), (requests.Count, tunnels));
    }

    /// <summary>
    /// One buffer a lab probe's ledger holds, in time order, never its bytes: its 100 ns readings after the first, event,
    /// exchange number, place, ends and length, the request its exchange's head named (-1 for none), whether a thread that
    /// made a request raised it, and, where the run was redirected, its leg.
    /// </summary>
    private readonly record struct LedgerBuffer(long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request,
        bool Client, string? Leg)
    {
        /// <summary>Whether it is of the leg WinINet made to a redirect's location.</summary>
        public bool Moved => Leg == "moved";
    }

    private static LedgerBuffer[] LedgerOf(string evidence) =>
    [
        .. File.ReadLines(Path.Combine(evidence, "buffers.csv")).Skip(1).Select(line => line.Split(',')).Select(field => new LedgerBuffer(
            long.Parse(field[0], CultureInfo.InvariantCulture), ushort.Parse(field[1], CultureInfo.InvariantCulture),
            long.Parse(field[2], CultureInfo.InvariantCulture), long.Parse(field[3], CultureInfo.InvariantCulture),
            long.Parse(field[4], CultureInfo.InvariantCulture), long.Parse(field[5], CultureInfo.InvariantCulture),
            int.Parse(field[6], CultureInfo.InvariantCulture), field[7] == "client", field.Length > 8 ? field[8] : null)),
    ];

    /// <summary>
    /// The server's truth of a run: each request's parts by length and SHA-256; and, into <paramref name="unnumbered"/>, in
    /// order, each part it logged of no request.
    /// </summary>
    private static Dictionary<(long Call, string Part), (long Length, string Hash)> ServerTruthOf(string evidence,
        List<(string Part, long Length, string Hash)>? unnumbered = null)
    {
        var truth = new Dictionary<(long Call, string Part), (long Length, string Hash)>();
        foreach (string line in File.ReadLines(Path.Combine(evidence, "truth-http-server.jsonl")))
        {
            using var record = System.Text.Json.JsonDocument.Parse(line);
            if (!record.RootElement.TryGetProperty("status", out var part) || part.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                continue;
            }

            (long Length, string Hash) logged = (record.RootElement.GetProperty("declaredBytes").GetInt64(),
                record.RootElement.GetProperty("resourceName").GetString()!);
            if (record.RootElement.TryGetProperty("callId", out var call) && call.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                truth[(call.GetInt64(), part.GetString()!)] = logged;
            }
            else
            {
                unnumbered?.Add((part.GetString()!, logged.Length, logged.Hash));
            }
        }

        return truth;
    }

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>
    /// A ledger's buffers published as records of the client, process 4242, each raised on one of its threads that made
    /// requests or on another, and each head - every one a single buffer - kept with consent to inspect it, as
    /// <paramref name="head"/> rebuilds it.
    /// </summary>
    private static TemporarySession Published(LedgerBuffer[] ledger, Func<LedgerBuffer, byte[]> head)
    {
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
        var heads = new List<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)>();
        for (int index = 0; index < ledger.Length; index++)
        {
            if (ledger[index].Event is 2001 or 2003)
            {
                byte[] bytes = head(ledger[index]);
                Assert.Equal((bytes.LongLength, 3L), (ledger[index].Bytes, ledger[index].Flags));
                heads.Add(Content(buffers[index], bytes, 1_024, ContentEncodingV1.Binary));
            }
        }

        var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 100_000), .. buffers], fields: fields,
            content: (ContentHeader(recordLimit: 1_024), heads));
        return session;
    }

    /// <summary>
    /// The ledger of <paramref name="fixture"/>'s run eight at once, grouped as a session's records are: one exchange to a
    /// request, every buffer of an exchange its request's, every part whole and each as long as the server's truth of it -
    /// a chunked body as its client read it - or, where the server holds no head's bytes, the heads recorded whole.
    /// </summary>
    private static (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes, int Request, bool Client)[]
        GroupedAsTheirRequests(string fixture, bool heads)
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", fixture, "evidence", "eight-at-once");
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
        // request's, and each part as long as the server's truth of it - a chunked body as its client read it - or, where the
        // server holds no head's bytes, recorded whole.
        int[] requests = [.. page.Exchanges.Select(row => ledger[(int)row.Exchange.First.Ordinal - 1].Request)];
        Assert.Equal(Enumerable.Range(0, 64), requests.Order());
        foreach ((HttpExchangeRow row, int request) in page.Exchanges.Zip(requests))
        {
            HttpExchange exchange = row.Exchange;
            Assert.True(exchange.Complete, row.Parts(CultureInfo.InvariantCulture));
            Assert.Equal((truth[(request, "request-body")], truth[(request, "response-body")]), (exchange.RequestBody.Bytes, exchange.ResponseBody.Bytes));
            Assert.Equal(heads, truth.ContainsKey((request, "request-head")) && truth.ContainsKey((request, "response-head")));
            if (heads)
            {
                Assert.Equal((truth[(request, "request-head")], truth[(request, "response-head")]), (exchange.RequestHead.Bytes, exchange.ResponseHead.Bytes));
            }
            else
            {
                Assert.True(exchange.RequestHead is { Whole: true, Bytes: > 0 } && exchange.ResponseHead is { Whole: true, Bytes: > 0 });
            }

            SessionEvidencePage records = SessionEvidenceQuery.Read(session.Store, operationKey: row.Key);
            Assert.Equal(exchange.Records, records.Records.Count);
            Assert.All(records.Records, record => Assert.Equal(request, ledger[(int)record.Observation.RawRecordOrdinal - 1].Request));
        }

        // They ran at once: most began before an earlier one's response ended.
        Assert.True(page.Exchanges.Select((row, index) => (row, index)).Count(pair => page.Exchanges.Take(pair.index)
            .Any(earlier => earlier.Exchange.ResponseEndNanoseconds > pair.row.Exchange.FirstNanoseconds)) > 32);
        return ledger;
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
