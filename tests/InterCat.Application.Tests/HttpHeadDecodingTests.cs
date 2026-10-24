using System.Globalization;
using System.Text;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The HTTP/1.1 head decoder (M8, `contracts/decoders-v1.md`): a whole request or response head WinINet's capture kept,
/// read as its part, into its start line's fields and its field lines, each with its bytes; a head HTTP/1.1 does not allow,
/// or one that ends early, decoded only as far as its bytes show; and a part that is not whole, or kept without consent to
/// inspect it, not at all.
/// </summary>
public sealed class HttpHeadDecodingTests
{
    private static readonly RawRecordId Asked = new(Capture, 1, 1, 9);

    [Fact(DisplayName = "P2: a whole HTTP request or response head decodes into its start line and its field lines, each with its bytes, linked to its part and the decoder's version")]
    public void AWholeHeadDecodesIntoItsLines()
    {
        const string Request = "GET /index.html?q=1 HTTP/1.1\r\nHost: 127.0.0.1:8983\r\nUser-Agent:InterCat \r\nX-Empty:\r\nX-Tab: a\tb\r\nX-Hop2!#$%&'*+.^_`|~: a\r\n\r\n";
        DecodedContent request = Head(Request, request: true);
        Assert.Equal(("http1-head", "InterCat's HTTP/1.1 head decoder", 1), (request.DecoderId, request.Decoder, request.DecoderVersion));
        Assert.Equal("Decoded by InterCat's HTTP/1.1 head decoder, version 1", request.Heading);
        Assert.Equal((Asked, "the request head of exchange 7"), (request.Record, request.Part));
        Assert.Equal(new ContentRange(0, Request.Length - 1), request.Read);
        Assert.Equal(
        [
            new("method", "GET", new(0, 2)),
            new("target", "/index.html?q=1", new(4, 18)),
            new("version", "HTTP/1.1", new(20, 27)),
            new("Host", "127.0.0.1:8983", new(30, 49)),
            new("User-Agent", "InterCat", new(52, 71)),
            new("X-Empty", "", new(74, 81)),
            new("X-Tab", "a\\x09b", new(84, 93)),
            new("X-Hop2!#$%&'*+.^_`|~", "a", new(96, 118)),
        ], request.Fields);
        Assert.Null(request.NotDecoded);

        // A response head's status line, its reason as it is; lines that end in LF alone, as a recipient may read them; and
        // a byte past ASCII written as inert \xHH, never as a character it might be in some encoding.
        byte[] response = Encoding.Latin1.GetBytes("HTTP/1.1 404 Not Found\nContent-Type: text/plain; charset=é\nServer:\tInterCat\t\n\n");
        DecodedContent answered = Head(response, request: false);
        Assert.Equal(
        [
            new("version", "HTTP/1.1", new(0, 7)),
            new("status", "404", new(9, 11)),
            new("reason", "Not Found", new(13, 21)),
            new("Content-Type", "text/plain; charset=\\xE9", new(23, 57)),
            new("Server", "InterCat", new(59, 75)),
        ], answered.Fields);
        Assert.Null(answered.NotDecoded);

        // A status line may give its reason empty, or none at all.
        foreach (string line in new[] { "HTTP/1.1 204 \r\n\r\n", "HTTP/1.1 204\r\n\r\n" })
        {
            Assert.Equal(["version", "status"], Head(line, request: false).Fields.Select(field => field.Name));
        }

        // What follows the empty line is no part of a head, and is said not to be decoded.
        Assert.Equal("4 bytes follow the empty line that ends the head; a head holds none, so they are not decoded.",
            Head("GET /x HTTP/1.1\r\n\r\nbody", request: true).NotDecoded);
        Assert.Equal("1 byte follows the empty line that ends the head; a head holds none, so it is not decoded.",
            Head("GET /x HTTP/1.1\r\n\r\nb", request: true).NotDecoded);
    }

    [Fact(DisplayName = "P2: a head HTTP/1.1 does not allow, or one that ends early, decodes only as far as its bytes show, and says why the rest does not")]
    public void AHeadIsDecodedOnlyAsFarAsItsBytesShow()
    {
        const string RequestLine = "Its first line is not a request line - a method, a target and an HTTP version, one space apart - so nothing is decoded.";
        const string StatusLine = "Its first line is not a status line - an HTTP version, a three-digit status code and a reason, one space apart - so nothing is decoded.";
        foreach ((string head, bool request, string[] fields, string why) in new[]
        {
            // Ending before the empty line that ends a head: after a whole line, or inside one, or inside the first.
            ("GET /x HTTP/1.1\r\nHost: a\r\n", true, new[] { "method", "target", "version", "Host" },
                "The head ends without the empty line that ends a head, so it may have held more fields than those decoded."),
            ("GET /x HTTP/1.1\r\nHost: a\r\nAcc", true, ["method", "target", "version", "Host"],
                "The head ends inside line 3, from byte 26, before the empty line that ends a head, so that line is not decoded."),
            ("GET /x HTT", true, [], "The head ends before its first line does, so nothing is decoded."),

            // A first line that is not a request line, or not a status line.
            ("GET  /x HTTP/1.1\r\n\r\n", true, [], RequestLine),
            ("GET /x HTTP/1.10\r\n\r\n", true, [], RequestLine),
            ("G@T /x HTTP/1.1\r\n\r\n", true, [], RequestLine),
            ("GET /\u0001 HTTP/1.1\r\n\r\n", true, [], RequestLine),
            ("GET /x y HTTP/1.1\r\n\r\n", true, [], RequestLine),
            ("GET /x\r\n\r\n", true, [], RequestLine),
            ("\r\nGET /x HTTP/1.1\r\n\r\n", true, [], RequestLine),
            ("HTTP/1.1 20 OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1 20\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1 x00 OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1 20x OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/x.1 200 OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1x1 200 OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.x 200 OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1 200OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1 200 O\u0001K\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1 2x0 OK\r\n\r\n", false, [], StatusLine),
            ("HTTP/1.1-200 OK\r\n\r\n", false, [], StatusLine),
            ("HTTX/1.1 200 OK\r\n\r\n", false, [], StatusLine),

            // A field line that is not one stops the decoding at its start.
            ("GET /x HTTP/1.1\r\nHost a\r\n\r\n", true, ["method", "target", "version"],
                "Line 2, from byte 17, holds no colon, so it is not a field line, and the head is not decoded from it on."),
            ("GET /x HTTP/1.1\r\nHost : a\r\n\r\n", true, ["method", "target", "version"],
                "Line 2, from byte 17, names its field with nothing, or with a character a field name cannot hold - whitespace before its colon is one - so the head is not decoded from it on."),
            ("GET /x HTTP/1.1\r\n: a\r\n\r\n", true, ["method", "target", "version"],
                "Line 2, from byte 17, names its field with nothing, or with a character a field name cannot hold - whitespace before its colon is one - so the head is not decoded from it on."),
            ("GET /x HTTP/1.1\r\nHost: a\r\n b\r\n\r\n", true, ["method", "target", "version", "Host"],
                "Line 3, from byte 26, begins with whitespace, continuing the line before it as HTTP/1.1 no longer allows, so the head is not decoded from it on."),
            ("GET /x HTTP/1.1\r\nHost: a\r\n\tb\r\n\r\n", true, ["method", "target", "version", "Host"],
                "Line 3, from byte 26, begins with whitespace, continuing the line before it as HTTP/1.1 no longer allows, so the head is not decoded from it on."),
            ("GET /x HTTP/1.1\r\nX: a\u0001b\r\n\r\n", true, ["method", "target", "version"],
                "Line 2, from byte 17, holds a control character in its value, so the head is not decoded from it on."),
            ("GET /x HTTP/1.1\r\nX: a\u007Fb\r\n\r\n", true, ["method", "target", "version"],
                "Line 2, from byte 17, holds a control character in its value, so the head is not decoded from it on."),

            // A carriage return that ends no line.
            ("GET /x HTTP/1.1\r\nHost: a\rb\r\n\r\n", true, ["method", "target", "version"],
                "Line 2, from byte 17, holds a carriage return that ends no line, so the head is not decoded from it on."),
            ("GET /x\r HTTP/1.1\r\n\r\n", true, [], "Line 1, from byte 0, holds a carriage return that ends no line, so nothing is decoded."),
        })
        {
            DecodedContent decoded = Head(head, request);
            Assert.True(fields.SequenceEqual(decoded.Fields.Select(field => field.Name)), head);
            Assert.True(why == decoded.NotDecoded, $"{head}: {decoded.NotDecoded}");
            Assert.Equal(new ContentRange(0, head.Length - 1), decoded.Read);
        }
    }

    [Fact(DisplayName = "P2: a head decoding reads at most what a view shows at once, within its time budget, and says where it stopped")]
    public void AHeadDecodingIsBounded()
    {
        // A head longer than a view decodes the lines whole in the first 64 KiB, and says the rest is not decoded.
        string line = "X-Long: " + new string('a', 990) + "\r\n";
        string head = "GET /x HTTP/1.1\r\n" + string.Concat(Enumerable.Repeat(line, 80)) + "\r\n";
        DecodedContent bounded = Head(head, request: true);
        Assert.Equal(new ContentRange(0, ContentDecoders.MaximumBytes - 1), bounded.Read);
        Assert.Equal(3 + ((ContentDecoders.MaximumBytes - 17) / line.Length), bounded.Fields.Count);
        Assert.Equal("The decoder read the first 65,536 bytes, as many as a view shows at once, and the head does not end in them; "
            + "the rest is not decoded.", bounded.NotDecoded);

        // Out of time, it stops where it was.
        DecodedContent stopped = Head("GET /x HTTP/1.1\r\n\r\n", request: true, TimeSpan.Zero);
        Assert.Empty(stopped.Fields);
        Assert.Equal("The decoder stopped at its time budget, at byte 0; the rest is not decoded.", stopped.NotDecoded);
    }

    [Fact(DisplayName = "P2: a head is decoded only as its whole part, from bytes kept with consent to inspect them, and a body by no decoder")]
    public void AHeadIsDecodedOnlyWhole()
    {
        // Exchange 7: a request head in two buffers, a response head and a response body; exchange 8's request head with its
        // second buffer never recorded; exchange 9's cut by the record limit; and exchange 10's, one empty buffer.
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            Http(10, 2001, 1), Http(11, 2001, 2), Http(12, 2003, 3), Http(13, 2004, 4),
            Http(20, 2001, 5), Http(21, 2001, 6),
            Http(30, 2001, 7),
            Http(40, 2001, 8),
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 9),
        ];
        (int Row, long Exchange, long Sequence, long Flags)[] parts =
        [
            (0, 7, 0, 1), (1, 7, 1, 2), (2, 7, 0, 3), (3, 7, 0, 3),
            (4, 8, 0, 1), (5, 8, 2, 2),
            (6, 9, 0, 3),
            (7, 10, 0, 3),
        ];
        string[] messages =
        [
            "GET /index.html HTTP/1.1\r\nHo", "st: example\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n", "hello",
            "GET /a HTTP/1.1\r\n", "\r\n",
            "GET /a-long-target HTTP/1.1\r\nHost: example\r\n\r\n",
            "",
            "other",
        ];
        Publish(session.Store, rows, fields: Fields(rows, parts), content: (ContentHeader(recordLimit: 32),
            [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 32, ContentEncodingV1.Binary))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        DecodedContent? Decoded(int index, bool reveal = true)
        {
            ObservationRowV1 row = page.Records[index].Observation;
            SessionContentPartDetail part = SessionContentPartQuery.Read(session.Store, page.SessionId, row, reveal);
            return ContentDecoders.DecodePart(part, row, page.Records[index].Content!.Fragment.RecordIn(Capture));
        }

        // Asked from either buffer, the whole request head decodes as one, a field across the two buffers whole.
        DecodedContent whole = Decoded(1)!;
        Assert.Equal(("the request head of exchange 7", 2UL), (whole.Part, whole.Record.RecordOrdinal));
        Assert.Equal(new DecodedField("Host", "example", new(26, 38)), whole.Fields[^1]);
        Assert.Equal(whole.Fields, Decoded(0)!.Fields);
        Assert.Equal(["version", "status", "reason"], Decoded(2)!.Fields.Select(field => field.Name));
        Assert.Equal(ContentDecoders.ForPart(page.Records[0].Observation), whole.Decoder);

        // A body, a record of no part, and a head's buffer no exchange numbers, are decoded by none.
        Assert.Null(Decoded(3));
        Assert.Null(ContentDecoders.ForPart(page.Records[3].Observation));
        Assert.Null(Decoded(8));
        Assert.Null(ContentDecoders.DecodePart(new SessionContentPartDetail { IsPart = false, Statement = "No part." },
            page.Records[0].Observation, Asked));

        // A head missing a buffer, or cut, is never decoded, nor one that holds nothing.
        foreach (int index in new[] { 4, 6 })
        {
            DecodedContent notWhole = Decoded(index)!;
            Assert.Equal((null, 0), (notWhole.Read, notWhole.Fields.Count));
            Assert.Equal("The part is not whole, so it is not decoded: what it lacks could hold any of its lines, and a decoding "
                + "never stands in for missing bytes.", notWhole.NotDecoded);
        }

        Assert.Equal("The part holds no bytes, so there is nothing to decode.", Decoded(7)!.NotDecoded);

        // A decoding reads the part's bytes, which must have been read.
        Assert.Throws<InvalidOperationException>(() => Decoded(1, reveal: false));

        // Bytes kept without consent to inspect them are never decoded.
        using var withheld = new TemporarySession();
        Publish(withheld.Store, rows[..2], fields: Fields(rows, parts[..2]), content: (ContentHeader(recordLimit: 32,
            inspection: ContentInspectionV1.Disabled),
            [.. messages[..2].Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 32, ContentEncodingV1.Binary))]));
        SessionEvidencePage hidden = SessionEvidenceQuery.Read(withheld.Store);
        SessionContentPartDetail kept = SessionContentPartQuery.Read(withheld.Store, hidden.SessionId, hidden.Records[0].Observation, true);
        DecodedContent refused = ContentDecoders.DecodePart(kept, hidden.Records[0].Observation, Asked)!;
        Assert.Equal((null, "The capture kept the part's bytes without consent to inspect them, so they are never decoded."),
            (refused.Read, refused.NotDecoded));

        // A part too long to assemble is said to be.
        var longer = new SessionContentPartDetail
        {
            IsPart = true,
            Name = "the response head of exchange 11",
            Complete = true,
            Length = SessionContentPartQuery.MaximumPartBytes + 1,
            Statement = "Too long.",
        };
        Assert.Equal($"The part is longer than the {SessionContentPartQuery.MaximumPartBytes.ToString("N0", CultureInfo.CurrentCulture)} "
            + "bytes a part is assembled up to, so it is not decoded.",
            ContentDecoders.DecodePart(longer, page.Records[2].Observation, Asked)!.NotDecoded);
        Assert.Throws<ArgumentNullException>(() => ContentDecoders.DecodePart(null!, rows[0], Asked));
        Assert.Throws<ArgumentNullException>(() => ContentDecoders.DecodePart(longer, null!, Asked));
        Assert.Throws<ArgumentNullException>(() => ContentDecoders.ForPart(null!));

        // InterCat's decoders, each at its version with what it reads.
        Assert.Equal([("intercat-content-fixture", 1), ("http1-head", 1)], ContentDecoders.Known.Select(decoder => (decoder.Id, decoder.Version)));
    }

    [Fact(DisplayName = "P2: an exchange's heads say what its request asked for, of which host, and what its response answered, from their start lines alone")]
    public void AnExchangesHeadsSayWhatTheyHold()
    {
        // The request line's method and target, the first Host field in any case, and the status line's status and reason:
        // a field line named as a start line's field never stands for it.
        var heads = new HttpExchangeHeads(
            Head("GET /x HTTP/1.1\r\nmethod: POST\r\nhost: Example\r\nHost: second\r\n\r\n", request: true),
            Head("HTTP/1.1 204\r\nreason: not the reason\r\n\r\n", request: false));
        Assert.Equal(("GET /x", "Example", "204"), (heads.Asked, heads.Host, heads.Answered));
        Assert.Equal("200 OK", new HttpExchangeHeads(null, Head("HTTP/1.1 200 OK\r\n\r\n", request: false)).Answered);
        Assert.Equal("200", new HttpExchangeHeads(null, Head("HTTP/1.1 200 \r\n\r\n", request: false)).Answered);

        // A head not recorded, a start line not decoded, and a request that names no host say nothing.
        var none = new HttpExchangeHeads(null, null);
        Assert.Equal((null, null, null), (none.Asked, none.Host, none.Answered));
        var undecoded = new HttpExchangeHeads(Head("GET  /x HTTP/1.1\r\nHost: a\r\n\r\n", request: true),
            Head("HTTP/1.1 20 OK\r\n\r\n", request: false));
        Assert.Equal((null, null, null), (undecoded.Asked, undecoded.Host, undecoded.Answered));
        Assert.Null(new HttpExchangeHeads(Head("GET /x HTTP/1.1\r\nAccept: */*\r\n\r\n", request: true), null).Host);
    }

    [Theory(DisplayName = "P2: a body is set beside the length its head declares, and said to disagree with it only where its recorded bytes do")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "GET", 1, 5, true, "Its head declares 5 bytes, and its recorded buffers hold them all.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "GET", 1, 3, true, "Its head declares 5 bytes, and its recorded buffers hold 3 of them, though their places and ends make the body whole.", true)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "GET", 2, 3, false, "Its head declares 5 bytes, and its recorded buffers hold 3 of them.", true)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "GET", 2, 7, true, "Its head declares 5 bytes, and its recorded buffers hold 7 bytes, more than it declared.", true)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "GET", 0, 0, false, "Its head declares 5 bytes, and no buffer of its body was recorded.", true)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", null, 0, 0, false, "Its head declares 5 bytes, and no buffer of its body was recorded; its request's method was not decoded, and a response to HEAD holds none.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "HEAD", 0, 0, false, "As a response to HEAD, it holds no body, and none was recorded.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "HEAD", 1, 5, true, "As a response to HEAD, it holds no body, yet its recorded buffers hold 5 bytes.", true)]
    [InlineData("HTTP/1.1 204 No Content\r\n\r\n", "GET", 1, 0, true, "As a 204 response, it holds no body, and its recorded buffers hold none.", false)]
    [InlineData("HTTP/1.1 304 Not Modified\r\nContent-Length: 20\r\n\r\n", "GET", 0, 0, false, "As a 304 response, it holds no body, and none was recorded.", false)]
    [InlineData("HTTP/1.1 101 Switching Protocols\r\n\r\n", "GET", 1, 4, true, "As a 101 response, it holds no body, yet its recorded buffers hold 4 bytes.", true)]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n", "GET", 3, 900, true, "Its head declares the chunked transfer coding, which WinINet removes before it keeps a body (FX-HTTP-003), so the body's end is its last buffer's, not a length its head declared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", "GET", 1, 9, true, "Its head declares the gzip, chunked transfer coding, which WinINet's capture was not measured to keep framed or unframed, so its body's length is not compared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 30\r\n\r\n", "GET", 1, 120, true, "Its head declares 30 bytes in the gzip content coding, and whether WinINet's capture keeps such a body coded was not measured, so its length is not compared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: identity\r\ncontent-length: 5, 5\r\n\r\n", "GET", 1, 5, true, "Its head declares 5 bytes, and its recorded buffers hold them all.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n", "GET", 1, 5, true, "Its Content-Length, 5, 6, is not one length, so its body is not compared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 99999999999999999999\r\n\r\n", "GET", 1, 5, true, "Its Content-Length, 99999999999999999999, is not one length, so its body is not compared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", "GET", 0, 0, false, "Its head declares an empty body, and none was recorded.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", "GET", 1, 0, true, "Its head declares an empty body, and its recorded buffers hold no byte.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nServer: x\r\n\r\n", "GET", 2, 50, true, "Its head declares no length, so its body ran until its connection closed; its end is its last buffer's.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nBad Field: x\r\nContent-Length: 5\r\n\r\n", "GET", 1, 5, true, "Its head was not decoded to its end, so what it declares of its body is not known.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n", "GET", 1, 5, true, "Its head was not decoded to its end, so what it declares of its body is not known.", false)]
    [InlineData("POST /u HTTP/1.1\r\nContent-Length: 4\r\n\r\n", null, 1, 4, true, "Its head declares 4 bytes, and its recorded buffers hold them all.", false)]
    [InlineData("POST /u HTTP/1.1\r\nContent-Length: 4\r\n\r\n", null, 0, 0, false, "Its head declares 4 bytes, and no buffer of its body was recorded.", true)]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n\r\n", null, 0, 0, false, "Its head declares no body, and none was recorded.", false)]
    [InlineData("GET / HTTP/1.1\r\n\r\n", null, 1, 3, true, "Its head declares no body, yet its recorded buffers hold 3 bytes.", true)]
    [InlineData("POST /u HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n", null, 2, 40, true, "Its head declares the chunked transfer coding, which WinINet's capture was not measured to keep framed or unframed, so its body's length is not compared.", false)]
    public void ABodyIsSetBesideItsDeclaredLength(string head, string? method, int buffers, long bytes, bool whole, string statement, bool contradicts)
    {
        // A head starting with its version is a response's, beside the request line of <paramref name="method"/> where
        // its request head was decoded; any other is a request's.
        var body = new HttpPartRecord(buffers, bytes, whole);
        bool request = !head.StartsWith("HTTP/", StringComparison.Ordinal);
        HttpExchangeHeads heads = request
            ? new(Head(head, request: true), null)
            : new(method is null ? null : Head($"{method} /x HTTP/1.1\r\n\r\n", request: true), Head(head, request: false));
        Assert.Equal(new HttpBodyDeclaration(statement, contradicts), request ? heads.RequestBody(body) : heads.ResponseBody(body));
    }

    [Fact(DisplayName = "P2: a head decoded to the empty line that ends it says so, and a head not decoded declares nothing of its body")]
    public void AHeadDecodedToItsEndSaysSo()
    {
        Assert.True(Head("GET / HTTP/1.1\r\nHost: a\r\n\r\n", request: true).ReachedEnd);
        Assert.True(Head("GET / HTTP/1.1\r\n\r\nmore", request: true).ReachedEnd);
        Assert.False(Head("GET / HTTP/1.1\r\nHost: a\r\n", request: true).ReachedEnd);
        Assert.False(Head("GET / HTTP/1.1\r\nNo colon\r\n\r\n", request: true).ReachedEnd);
        var body = new HttpPartRecord(1, 5, true);
        Assert.Equal((null, null), (new HttpExchangeHeads(null, null).RequestBody(body), new HttpExchangeHeads(null, null).ResponseBody(body)));
        DecodedContent unread = ContentDecoders.DecodePart(
            new SessionContentPartDetail { IsPart = true, Name = "the response head of exchange 7", Complete = false, Statement = "Not whole." },
            Http(1, 2003, 9), Asked)!;
        Assert.Null(unread.Read);
        Assert.Null(new HttpExchangeHeads(null, unread).ResponseBody(body));
    }

    [Fact(DisplayName = "I21: parts read together are each the part their buffer belongs to, exactly as one read alone is")]
    public void PartsReadTogetherAreEachAsReadAlone()
    {
        // Exchange 7's request head in two buffers and its response head; exchange 8's request head with a buffer never
        // recorded; a TCP record, of no part; another process's exchange 7, its request head's two buffers raised at the
        // same moments as the first's; and exchange 9's request head without its last buffer, then its response head
        // without its first, whose two buffers would make one whole head.
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            Http(10, 2001, 1), Http(11, 2001, 2), Http(12, 2003, 3), Http(20, 2001, 4), Http(21, 2001, 5),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 6),
            Http(10, 2001, 7) with { HeaderProcessId = 4_141 }, Http(11, 2001, 8) with { HeaderProcessId = 4_141 },
            Http(40, 2001, 9), Http(41, 2003, 10),
        ];
        (int Row, long Exchange, long Sequence, long Flags)[] parts =
        [
            (0, 7, 0, 1), (1, 7, 1, 2), (2, 7, 0, 3), (3, 8, 0, 1), (4, 8, 2, 2), (6, 7, 0, 1), (7, 7, 1, 2), (8, 9, 0, 1),
            (9, 9, 1, 2),
        ];
        string[] messages =
        [
            "GET /x HTTP/1.1\r\nHo", "st: a\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n", "GET /a HTTP/1.1\r\n", "\r\n", "other",
            "GET /b HTTP/1.1\r\n", "\r\n", "GET /c HTTP/1.1\r\n", "Server: x\r\n\r\n",
        ];
        Publish(session.Store, rows, fields: Fields(rows, parts), content: (ContentHeader(recordLimit: 64),
            [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 64, ContentEncodingV1.Binary))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        ObservationRowV1[] asked = [.. page.Records.Select(record => record.Observation)];
        IReadOnlyList<SessionContentPartDetail> together = SessionContentPartQuery.ReadMany(session.Store, page.SessionId, asked, true);
        Assert.Equal(asked.Length, together.Count);
        for (int index = 0; index < asked.Length; index++)
        {
            SessionContentPartDetail alone = SessionContentPartQuery.Read(session.Store, page.SessionId, asked[index], true);
            Assert.Equal((alone.IsPart, alone.Name, alone.Complete, alone.Statement, alone.Length),
                (together[index].IsPart, together[index].Name, together[index].Complete, together[index].Statement, together[index].Length));
            Assert.Equal(alone.Bytes, together[index].Bytes);
            Assert.Equal(alone.Buffers.Select(buffer => buffer.Record), together[index].Buffers.Select(buffer => buffer.Record));
        }

        Assert.Equal((true, true, false, false, true), (Of(1).Complete, Of(3).Complete, Of(4).Complete, Of(6).IsPart, Of(7).Complete));
        Assert.Equal("GET /x HTTP/1.1\r\nHost: a\r\n\r\n"u8.ToArray(), Of(2).Bytes);
        Assert.Equal("GET /b HTTP/1.1\r\n\r\n"u8.ToArray(), Of(8).Bytes);
        Assert.Equal((false, 1, false, 1), (Of(9).Complete, Of(9).Buffers.Count, Of(10).Complete, Of(10).Buffers.Count));
        Assert.Empty(SessionContentPartQuery.ReadMany(session.Store, page.SessionId, [], true));
        Assert.Throws<ArgumentNullException>(() => SessionContentPartQuery.ReadMany(session.Store, page.SessionId, null!, true));

        SessionContentPartDetail Of(ulong ordinal) => together[Array.FindIndex(asked, row => row.RawRecordOrdinal == ordinal)];
    }

    [Fact(DisplayName = "P2: an exchange's conversation is read by its key, from its records alone until its heads are asked for, then each decoded whole")]
    public void AConversationDecodesItsHeadsOnlyWhenAsked()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Conversations(out SourceFieldRowV1[] fields, out var content), fields: fields, content: content);
        string channel = HttpExchangeKeys.Channel(SessionHttpExchangesTests.Client(session.Store));
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, channel);
        (string seven, string eight) = (page.Exchanges[0].Key, page.Exchanges[1].Key);

        // Read from its records alone, the exchange is the one its row lists, and nothing of its heads is read.
        HttpConversation listed = SessionHttpExchanges.Conversation(session.Store, page.SessionId, seven, decodeHeads: false);
        Assert.Equal((seven, page.Exchanges[0].Exchange, page.Generation, (HttpExchangeHeads?)null, (string?)null),
            (listed.Exchange!.Key, listed.Exchange.Exchange, listed.Generation, listed.Heads, listed.Problem));

        // Asked for, each head is read whole and decoded: what the request asked for, of which host, and what was answered.
        HttpConversation asked = SessionHttpExchanges.Conversation(session.Store, page.SessionId, seven, decodeHeads: true);
        HttpExchangeHeads heads = asked.Heads!;
        Assert.Equal(("GET /x", "a", "200 OK"), (heads.Asked, heads.Host, heads.Answered));
        Assert.Equal(["method", "target", "version", "Host"], heads.Request!.Fields.Select(field => field.Name));
        Assert.Equal("the request head of exchange 7", heads.Request.Part);
        Assert.Equal(page.Exchanges[0].Exchange, asked.Exchange!.Exchange);

        // A head not whole is not decoded, and says why; a head never recorded has no decoding at all.
        HttpConversation second = SessionHttpExchanges.Conversation(session.Store, page.SessionId, eight, decodeHeads: true);
        Assert.Equal(page.Exchanges[1].Exchange, second.Exchange!.Exchange);
        HttpExchangeHeads lacking = second.Heads!;
        Assert.Null(lacking.Request!.Read);
        Assert.StartsWith("The part is not whole", lacking.Request.NotDecoded, StringComparison.Ordinal);
        Assert.Null(lacking.Response);

        // An exchange its process's binding does not admit under the policy asked for is not read, as its listing does not
        // hold it: its buffers are bound to their process by correlation, never directly.
        HttpConversation direct = SessionHttpExchanges.Conversation(session.Store, page.SessionId, seven, decodeHeads: true,
            EvidencePolicy.DirectOnly);
        Assert.Equal((null, null), (direct.Exchange, direct.Heads));
        Assert.StartsWith("This generation holds no such exchange of that process", direct.Problem, StringComparison.Ordinal);

        // An exchange the generation does not hold is said to be so; another session's folder, or a key naming no
        // exchange, is refused.
        HttpConversation gone = SessionHttpExchanges.Conversation(session.Store, page.SessionId,
            HttpExchangeKeys.Exchange(channel, 9, 9, 9), decodeHeads: true);
        Assert.Equal((null, null), (gone.Exchange, gone.Heads));
        Assert.StartsWith("This generation holds no such exchange of that process", gone.Problem, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            SessionHttpExchanges.Conversation(session.Store, Guid.NewGuid(), seven, decodeHeads: false));
        Assert.Throws<ArgumentException>(() => SessionHttpExchanges.Conversation(session.Store, page.SessionId, channel, decodeHeads: false));
    }

    /// <summary>
    /// Process 4242's exchange 7, its request head in two buffers, its response head and body each in one; and its exchange 8,
    /// whose request head lacks a buffer and which has no response; each buffer's bytes kept with consent to inspect them.
    /// </summary>
    internal static ObservationRowV1[] Conversations(out SourceFieldRowV1[] fields,
        out (ContentChunkHeaderV1 Header, IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> Fragments) content)
    {
        ObservationRowV1[] buffers = [Http(10, 2001, 1), Http(11, 2001, 2), Http(12, 2003, 3), Http(13, 2004, 4), Http(20, 2001, 5), Http(21, 2001, 6)];
        fields = Fields(buffers, [(0, 7, 0, 1), (1, 7, 1, 2), (2, 7, 0, 3), (3, 7, 0, 3), (4, 8, 0, 1), (5, 8, 2, 2)]);
        string[] messages = ["GET /x HTTP/1.1\r\nHo", "st: a\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "hello", "GET /a HTTP/1.1\r\n", "\r\n"];
        content = (ContentHeader(recordLimit: 64),
            [.. messages.Select((message, index) => Content(buffers[index], Encoding.ASCII.GetBytes(message), 64, ContentEncodingV1.Binary))]);
        return [Lifecycle(1, ObservationKind.Create, 4_242, 1_000_000), .. buffers];
    }

    /// <summary>A whole head of exchange 7, decoded from its bytes.</summary>
    private static DecodedContent Head(string head, bool request, TimeSpan? budget = null) =>
        Head(Encoding.ASCII.GetBytes(head), request, budget);

    private static DecodedContent Head(byte[] head, bool request, TimeSpan? budget = null)
    {
        var part = new SessionContentPartDetail
        {
            IsPart = true,
            Name = request ? "the request head of exchange 7" : "the response head of exchange 7",
            Complete = true,
            Length = head.Length,
            Bytes = head,
            BytesRead = true,
            Statement = "Whole.",
        };
        return ContentDecoders.DecodePart(part, Http(1, request ? (ushort)2001 : (ushort)2003, 9), Asked, budget)!;
    }

    private static SourceFieldRowV1[] Fields(ObservationRowV1[] rows, (int Row, long Exchange, long Sequence, long Flags)[] parts) =>
    [
        .. parts.SelectMany(part => new[]
        {
            Field(rows[part.Row], SourceField.HttpExchangeId, part.Exchange),
            Field(rows[part.Row], SourceField.ContentBufferSequence, part.Sequence),
            Field(rows[part.Row], SourceField.ContentBufferFlags, part.Flags),
        }),
    ];

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
