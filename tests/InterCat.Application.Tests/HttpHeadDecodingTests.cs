using System.Globalization;
using System.Security.Cryptography;
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

    [Fact(DisplayName = "P2: a decoder that fails on its bytes decodes nothing and says it failed and how, stopping nothing that asked it")]
    public void ADecoderThatFailsSaysSo()
    {
        // The failure - here a decoder reading past its bytes - is said as the decoding of nothing, under the decoder's
        // name and version and the part it was asked for, so a view, a listing or an export shows why instead of stopping.
        var record = new RawRecordId(Capture, 1, 1, 9);
        int[] none = [];
        DecodedContent failed = ContentDecoders.Contained(ContentDecoders.HttpHeadDecoder, record, "the request head of exchange 7",
            () => new DecodedContent
            {
                DecoderId = "http1-head", Decoder = "past its bytes", DecoderVersion = none[0], Record = record, Read = null, Fields = [],
            });
        Assert.Equal(("http1-head", "InterCat's HTTP/1.1 head decoder", 1, record, "the request head of exchange 7"),
            (failed.DecoderId, failed.Decoder, failed.DecoderVersion, failed.Record, failed.Part));
        Assert.Equal(((ContentRange?)null, 0, false), (failed.Read, failed.Fields.Count, failed.ReachedEnd));
        Assert.Equal("The decoder failed on these bytes (IndexOutOfRangeException: Index was outside the bounds of the array), "
            + "so nothing is decoded: the failure is the decoder's, not the evidence's, and the bytes stay as they are.",
            failed.NotDecoded);

        // A decoding that does not fail is the decoder's own, as it made it; running out of memory is not the decoder's
        // to contain.
        DecodedContent made = failed with { NotDecoded = null };
        Assert.Same(made, ContentDecoders.Contained(ContentDecoders.HttpHeadDecoder, record, null, () => made));
        Assert.Throws<OutOfMemoryException>(() => ContentDecoders.Contained(ContentDecoders.HttpHeadDecoder, record, null,
            () => made with { Fields = [new("past every array", new byte[int.MaxValue].Length.ToString(CultureInfo.InvariantCulture), new(0, 0))] }));
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
    [InlineData("HTTP/1.1 200 Connection Established\r\n\r\n", "CONNECT", 1, 0, true, "As a 200 response to CONNECT, it holds no body - its connection became a tunnel - and its recorded buffers hold none.", false)]
    [InlineData("HTTP/1.1 204 No Content\r\nContent-Length: 10\r\n\r\n", "CONNECT", 0, 0, false, "As a 204 response to CONNECT, it holds no body - its connection became a tunnel - and none was recorded.", false)]
    [InlineData("HTTP/1.1 200 Connection Established\r\nTransfer-Encoding: chunked\r\n\r\n", "CONNECT", 1, 7, true, "As a 200 response to CONNECT, it holds no body - its connection became a tunnel - yet its recorded buffers hold 7 bytes.", true)]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 5\r\n\r\n", "CONNECT", 1, 5, true, "Its head declares 5 bytes, and its recorded buffers hold them all.", false)]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n", "GET", 1, 0, true, "Its head declares no length, so its body ran until its connection closed; its end is its last buffer's.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n", "GET", 3, 900, true, "Its head declares the chunked transfer coding, which WinINet removes before it keeps a body (FX-HTTP-003), so the body's end is its last buffer's, not a length its head declared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", "GET", 1, 9, true, "Its head declares the gzip, chunked transfer coding, which WinINet's capture was not measured to keep framed or unframed, so its body's length is not compared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 30\r\n\r\n", "GET", 2, 30, true, "Its head declares 30 bytes in the gzip content coding, and its recorded buffers hold them all.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 30\r\n\r\n", "GET", 1, 120, true, "Its head declares 30 bytes in the gzip content coding, and its recorded buffers hold 120 bytes: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-004), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: GZip\r\nContent-Length: 30\r\n\r\n", "GET", 1, 12, true, "Its head declares 30 bytes in the GZip content coding, and its recorded buffers hold 12 bytes: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-004), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: X-GZIP\r\nContent-Length: 20\r\n\r\n", "GET", 1, 0, true, "Its head declares 20 bytes in the X-GZIP content coding, and its recorded buffers hold no byte: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-004), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 30\r\n\r\n", "GET", 0, 0, false, "Its head declares 30 bytes in the gzip content coding, and no buffer of its body was recorded.", true)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: br\r\nContent-Length: 30\r\n\r\n", "GET", 1, 120, true, "Its head declares 30 bytes in the br content coding, and its recorded buffers hold 120 bytes: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-008), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: br\r\nContent-Length: 30\r\n\r\n", "GET", 1, 30, true, "Its head declares 30 bytes in the br content coding, and its recorded buffers hold them all.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: Deflate\r\nContent-Length: 30\r\n\r\n", "GET", 2, 95, true, "Its head declares 30 bytes in the Deflate content coding, and its recorded buffers hold 95 bytes: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-008), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: deflate\r\nContent-Length: 30\r\n\r\n", "GET", 1, 0, true, "Its head declares 30 bytes in the deflate content coding, and its recorded buffers hold no byte: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it, and keeps a deflate body it could not decode as no byte (FX-HTTP-008), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: br\r\nContent-Length: 30\r\n\r\n", "GET", 1, 0, true, "Its head declares 30 bytes in the br content coding, and its recorded buffers hold no byte: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-008), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Encoding: deflate\r\nContent-Length: 30\r\n\r\n", "GET", 1, 12, true, "Its head declares 30 bytes in the gzip, deflate content coding, and its recorded buffers hold 12 bytes: WinINet keeps a coded body as its client read it, decoded where the client had WinINet decode it (FX-HTTP-004, FX-HTTP-008), so a body of another length is not said to disagree.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: zstd\r\nContent-Length: 30\r\n\r\n", "GET", 1, 12, true, "Its head declares 30 bytes in the zstd content coding, and its recorded buffers hold 12 bytes; how WinINet's capture keeps a body in that coding was not measured, so its length is not compared.", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Encoding: gzip, compress\r\nContent-Length: 30\r\n\r\n", "GET", 1, 12, true, "Its head declares 30 bytes in the gzip, compress content coding, and its recorded buffers hold 12 bytes; how WinINet's capture keeps a body in that coding was not measured, so its length is not compared.", false)]
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

    [Theory(DisplayName = "P2: a 301, 302, 303, 307 or 308 response naming one Location redirects there, resolved against its request's target and host")]
    [InlineData("POST /fx/0 HTTP/1.1\r\nHost: 127.0.0.1:5000\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /fx/0/moved\r\n\r\n", "302", "/fx/0/moved", "/fx/0/moved", "127.0.0.1:5000")]
    [InlineData("GET /a/b?c=1 HTTP/1.1\r\nHost: Example.Test\r\n\r\n", "HTTP/1.1 301 Moved Permanently\r\nlocation: d?e=2\r\n\r\n", "301", "d?e=2", "/a/d?e=2", "example.test")]
    [InlineData("GET /a/b HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 303 See Other\r\nLocation: ../c#part\r\n\r\n", "303", "../c#part", "/c", "example.test")]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 307 Temporary Redirect\r\nLocation: https://Other.Test:8443/x?y\r\n\r\n", "307", "https://Other.Test:8443/x?y", "/x?y", "other.test:8443")]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 308 Permanent Redirect\r\nLocation: https://other.test/x\r\n\r\n", "308", "https://other.test/x", "/x", "other.test")]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: //other.test:81/x\r\n\r\n", "302", "//other.test:81/x", "/x", "other.test:81")]
    [InlineData("GET /a/b HTTP/1.1\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /c\r\n\r\n", "302", "/c", "/c", null)]
    [InlineData("GET /a/b HTTP/1.1\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: c\r\n\r\n", "302", "c", "/a/c", null)]
    [InlineData(null, "HTTP/1.1 308 Permanent Redirect\r\nLocation: c\r\n\r\n", "308", "c", null, null)]
    [InlineData(null, "HTTP/1.1 308 Permanent Redirect\r\nLocation: /c\r\n\r\n", "308", "/c", "/c", null)]
    [InlineData("OPTIONS * HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: c\r\n\r\n", "302", "c", null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: ftp://example.test/x\r\n\r\n", "302", "ftp://example.test/x", null, null)]
    [InlineData("GET http://Proxied.Test/a/b HTTP/1.1\r\nHost: proxied.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: c\r\n\r\n", "302", "c", "/a/c", "proxied.test")]
    [InlineData("GET http://proxied.test:8080/a/b HTTP/1.1\r\n\r\n", "HTTP/1.1 307 Temporary Redirect\r\nLocation: /d?e\r\n\r\n", "307", "/d?e", "/d?e", "proxied.test:8080")]
    [InlineData("GET http://proxied.test/a HTTP/1.1\r\n\r\n", "HTTP/1.1 301 Moved Permanently\r\nLocation: ftp://proxied.test/x\r\n\r\n", "301", "ftp://proxied.test/x", null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 200 OK\r\nLocation: /b\r\n\r\n", null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 304 Not Modified\r\nLocation: /b\r\n\r\n", null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 300 Multiple Choices\r\nLocation: /b\r\n\r\n", null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nContent-Length: 0\r\n\r\n", null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /b\r\nLocation: /c\r\n\r\n", null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation:\r\n\r\n", null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: example.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /b\r\n", null, null, null, null)]
    public void ARedirectNamesWhereItSendsItsRequest(string? request, string response, string? status, string? location, string? target, string? host)
    {
        var heads = new HttpExchangeHeads(request is null ? null : Head(request, request: true), Head(response, request: false));
        Assert.Equal(status is null ? null : new HttpRedirect(status, location!, target, host), heads.Redirect);

        // A response no buffer of whose head was recorded redirects nowhere that is known.
        Assert.Null(new HttpExchangeHeads(heads.Request, null).Redirect);
    }

    [Theory(DisplayName = "P2: a request naming its whole address went to a proxy, and a CONNECT asked one to open a tunnel, each said so; any other went to none known")]
    [InlineData("POST http://fx.invalid/fx/0 HTTP/1.1\r\nHost: fx.invalid\r\n\r\n", "request", "It asked a proxy for http://fx.invalid/fx/0: a client names a request's whole address only to a proxy (RFC 9112 §3.2.2), and WinINet keeps the request as it sent it there (FX-HTTP-010).")]
    [InlineData("GET https://a.test/x?y HTTP/1.1\r\n\r\n", "request", "It asked a proxy for https://a.test/x?y: a client names a request's whole address only to a proxy (RFC 9112 §3.2.2), and WinINet keeps the request as it sent it there (FX-HTTP-010).")]
    [InlineData("CONNECT fx.invalid:443 HTTP/1.0\r\nHost: fx.invalid:443\r\n\r\n", "tunnel", "It asked a proxy to open a tunnel to fx.invalid:443. WinINet keeps a CONNECT as an exchange of its own, and the requests it then sends through the tunnel over TLS as over TLS directly, each an exchange of its own that no record ties to this one (FX-HTTP-010).")]
    [InlineData("POST /fx/0 HTTP/1.1\r\nHost: fx.invalid\r\n\r\n", null, null)]
    [InlineData("GET ftp://a.test/x HTTP/1.1\r\n\r\n", null, null)]
    [InlineData("OPTIONS * HTTP/1.1\r\n\r\n", null, null)]
    [InlineData("GET  http://a.test/x HTTP/1.1\r\n\r\n", null, null)]
    public void AProxiedRequestSaysHowItWentToAProxy(string request, string? kind, string? statement)
    {
        var heads = new HttpExchangeHeads(Head(request, request: true), Head("HTTP/1.1 200 OK\r\n\r\n", request: false));
        Assert.Equal(kind is null ? null : new HttpProxyUse(kind, statement!), heads.Proxy);

        // A request no buffer of whose head was recorded went to no proxy that is known.
        Assert.Null(new HttpExchangeHeads(null, heads.Response).Proxy);
    }

    [Theory(DisplayName = "P2: a 401 naming WWW-Authenticate, or a 407 naming Proxy-Authenticate, challenges its client for credentials, and names the field that answers it")]
    [InlineData("POST /fx/0 HTTP/1.1\r\nHost: 127.0.0.1:5000\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"FX-HTTP-011\"\r\nContent-Length: 0\r\n\r\n", "401", "WWW-Authenticate", "Basic realm=\"FX-HTTP-011\"", "Authorization", "/fx/0", "127.0.0.1:5000")]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nwww-authenticate: Negotiate\r\nWWW-Authenticate: Basic realm=\"r\"\r\n\r\n", "401", "WWW-Authenticate", "Negotiate, Basic realm=\"r\"", "Authorization", "/a", "h")]
    [InlineData("GET http://p.test/a HTTP/1.1\r\nHost: p.test\r\n\r\n", "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"proxy\"\r\n\r\n", "407", "Proxy-Authenticate", "Basic realm=\"proxy\"", "Proxy-Authorization", "/a", "p.test")]
    [InlineData(null, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic\r\n\r\n", "401", "WWW-Authenticate", "Basic", "Authorization", null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n", null, null, null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate:\r\n\r\n", null, null, null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 407 Proxy Authentication Required\r\nWWW-Authenticate: Basic\r\n\r\n", null, null, null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 200 OK\r\nWWW-Authenticate: Basic\r\n\r\n", null, null, null, null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic\r\n", null, null, null, null, null, null)]
    public void AChallengeNamesWhatAnswersIt(string? request, string response, string? status, string? field, string? challenges, string? answer,
        string? target, string? host)
    {
        var heads = new HttpExchangeHeads(request is null ? null : Head(request, request: true), Head(response, request: false));
        Assert.Equal(status is null ? null : new HttpChallenge(status, field!, challenges!, answer!, target, host), heads.Challenge);

        // A response no buffer of whose head was recorded challenges for nothing that is known.
        Assert.Null(new HttpExchangeHeads(heads.Request, null).Challenge);
    }

    [Theory(DisplayName = "P2: a request carrying credentials says so, by their field and scheme and never their value, and that Basic credentials are readable")]
    [InlineData("POST /fx/0 HTTP/1.1\r\nHost: h\r\nAuthorization: Basic aW50ZXJjYXQtZml4dHVyZTpmeC1odHRwLTAxMS1ub3QtYS1zZWNyZXQ=\r\n\r\n", "Authorization", "Basic", "It carries credentials in its Authorization field, in the Basic scheme, kept as WinINet sent them: a user name and password, encoded, not encrypted, which anyone reading these heads can read (FX-HTTP-011).")]
    [InlineData("GET /a HTTP/1.1\r\nauthorization: basic eDp5\r\n\r\n", "authorization", "basic", "It carries credentials in its authorization field, in the basic scheme, kept as WinINet sent them: a user name and password, encoded, not encrypted, which anyone reading these heads can read (FX-HTTP-011).")]
    [InlineData("GET /a HTTP/1.1\r\nAuthorization: Bearer abc.def\r\n\r\n", "Authorization", "Bearer", "It carries credentials in its Authorization field, in the Bearer scheme, kept as WinINet sent them (FX-HTTP-011).")]
    [InlineData("GET http://p.test/a HTTP/1.1\r\nProxy-Authorization: Basic eDp5\r\n\r\n", "Proxy-Authorization", "Basic", "It carries credentials in its Proxy-Authorization field, in the Basic scheme, kept as WinINet sent them: a user name and password, encoded, not encrypted, which anyone reading these heads can read (FX-HTTP-011).")]
    [InlineData("GET /a HTTP/1.1\r\nAuthorization: Negotiate\r\n\r\n", "Authorization", "Negotiate", "It carries credentials in its Authorization field, in the Negotiate scheme, kept as WinINet sent them (FX-HTTP-011).")]
    [InlineData("GET /a HTTP/1.1\r\nAuthorization:\r\nHost: h\r\n\r\n", null, null, null)]
    [InlineData("GET /a HTTP/1.1\r\nHost: h\r\n\r\n", null, null, null)]
    public void ARequestCarryingCredentialsSaysSo(string request, string? field, string? scheme, string? statement)
    {
        var heads = new HttpExchangeHeads(Head(request, request: true), null);
        Assert.Equal(field is null ? null : new HttpCredentials(field, scheme!, statement!), heads.Credentials);
        Assert.Null(new HttpExchangeHeads(null, null).Credentials);
    }

    /// <summary>
    /// No record ties a challenge to the request WinINet then sent with credentials (FX-HTTP-011), so a listing names as its
    /// answer the first exchange listed after it, begun once its response ended, that asked for its target of its host
    /// carrying the field that answers it.
    /// </summary>
    [Fact(DisplayName = "P2: a listed challenge is answered by the first exchange listed after it, begun once its response ended, that asked for its target of its host carrying credentials")]
    public void AChallengeIsAnsweredByTheFirstLaterRequestCarryingCredentials()
    {
        const string Basic = "Authorization: Basic eDp5\r\n";
        (long First, long? End, string? Request, string? Response)[] listed =
        [
            (10, 100, "POST /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic\r\n\r\n"),
            (50, 60, "POST /a HTTP/1.1\r\nHost: h\r\n" + Basic + "\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (105, 110, "POST /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic\r\n\r\n"),
            (112, 115, "POST /a HTTP/1.1\r\nHost: other\r\n" + Basic + "\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (120, 125, "POST /a HTTP/1.1\r\nHost: h\r\nProxy-Authorization: Basic eDp5\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (130, 135, "POST /a HTTP/1.1\r\nHost: H\r\n" + Basic + "\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (140, 150, "GET http://p.test/b HTTP/1.1\r\nHost: p.test\r\n\r\n", "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic\r\n\r\n"),
            (155, 160, "GET http://p.test/b HTTP/1.1\r\nHost: p.test\r\n" + Basic + "\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (165, 170, "GET http://p.test/b HTTP/1.1\r\nHost: p.test\r\nProxy-Authorization: Basic eDp5\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (180, 185, "POST /a HTTP/1.1\r\nHost: h\r\n" + Basic + "\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (200, 210, "GET /c HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic\r\n\r\n"),
            (220, 225, null, null),
        ];
        HttpExchange[] exchanges =
        [
            .. listed.Select((exchange, at) => new HttpExchange
            {
                Number = at + 1,
                ProcessId = 4_242,
                Process = new ProcessBinding(0, RelationStrength.Direct, ProcessBindingReason.Bound),
                FirstTicks = exchange.First,
                LastTicks = exchange.End ?? exchange.First,
                FirstNanoseconds = exchange.First,
                LastNanoseconds = exchange.End ?? exchange.First,
                ResponseEndNanoseconds = exchange.End,
                RequestHead = new(1, 20, true),
                RequestBody = default,
                ResponseHead = new(1, 20, true),
                ResponseBody = new(1, 0, exchange.End is not null),
                First = (1, 0, (ulong)(at + 1)),
            }),
        ];
        HttpExchangeHeads?[] heads =
        [
            .. listed.Select(exchange => exchange.Request is null
                ? null
                : new HttpExchangeHeads(Head(exchange.Request, request: true), Head(exchange.Response!, request: false))),
        ];

        // The first challenge is answered by the first exchange after it, once its response ended, that asked for /a of h
        // with an Authorization field, its host in any case - not one begun before, nor one challenged too, of another
        // host, or carrying the field that answers a proxy, nor a later one; so is the second; a proxy's challenge by
        // Proxy-Authorization alone; and one none listed answered has none.
        Assert.Equal([5, -1, 5, -1, -1, -1, 8, -1, -1, -1, -1, -1], HttpChallenges.AnsweredBy(exchanges, heads));
        Assert.Throws<ArgumentException>(() => HttpChallenges.AnsweredBy(exchanges, heads[1..]));
    }

    /// <summary>
    /// No record ties a redirect to the request WinINet made to its location (FX-HTTP-009), so a listing names as its
    /// follow-up the first exchange listed after it, begun once its response ended, that asked for its location of its host -
    /// the target alone where a host is known of neither.
    /// </summary>
    [Fact(DisplayName = "P2: a listed redirect is followed by the first exchange listed after it, begun once its response ended, that asked for its location of its host")]
    public void ARedirectIsFollowedByTheFirstLaterExchangeAskingForItsLocation()
    {
        (long First, long? End, string? Request, string? Response)[] listed =
        [
            (10, 100, "GET /a HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /b\r\n\r\n"),
            (50, 60, "GET /b HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (105, 110, "GET /b HTTP/1.1\r\nHost: other\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (120, 125, "GET /b HTTP/1.1\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (130, 140, "GET /c HTTP/1.1\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: http://h/d\r\n\r\n"),
            (150, 155, null, null),
            (160, 165, "GET /d HTTP/1.1\r\nHost: H\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (170, 175, "POST /e HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 307 Temporary Redirect\r\nLocation: /never\r\n\r\n"),
            (200, 205, "GET /b HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (210, null, "GET /f HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 303 See Other\r\nLocation: /b\r\n\r\n"),
            (211, 215, "GET /b HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (216, 218, "GET /h HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /g\r\n\r\n"),
            (219, 222, "GET /x/../g HTTP/1.1\r\nHost: h:80\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (300, null, "GET /self HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: /self\r\n\r\n"),
            (320, 330, "GET /self HTTP/1.1\r\nHost: h\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (400, 410, "GET http://p.test/r HTTP/1.1\r\nHost: p.test\r\n\r\n", "HTTP/1.1 302 Found\r\nLocation: s\r\n\r\n"),
            (420, 425, "GET http://P.test:80/s HTTP/1.1\r\nHost: p.test\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
            (430, 435, "GET /s HTTP/1.1\r\nHost: p.test\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n"),
        ];
        HttpExchange[] exchanges =
        [
            .. listed.Select((exchange, at) => new HttpExchange
            {
                Number = at + 1,
                ProcessId = 4_242,
                Process = new ProcessBinding(0, RelationStrength.Direct, ProcessBindingReason.Bound),
                FirstTicks = exchange.First,
                LastTicks = exchange.End ?? exchange.First,
                FirstNanoseconds = exchange.First,
                LastNanoseconds = exchange.End ?? exchange.First,
                ResponseEndNanoseconds = exchange.End,
                RequestHead = new(1, 20, true),
                RequestBody = default,
                ResponseHead = new(1, 20, true),
                ResponseBody = new(1, 0, exchange.End is not null),
                First = (1, 0, (ulong)(at + 1)),
            }),
        ];
        HttpExchangeHeads?[] heads =
        [
            .. listed.Select(exchange => exchange.Request is null
                ? null
                : new HttpExchangeHeads(Head(exchange.Request, request: true), Head(exchange.Response!, request: false))),
        ];

        // The first redirect's follow-up is the first exchange after it, once its response ended, that asked for /b of h or
        // of no host it named - not one begun before, nor one of another host, nor a later one; an absolute location names
        // its own host, in any case; a redirect that none listed followed has none; one whose response's end was not
        // recorded is followed by the next asking; a request names its target and host as a location does, its dot
        // segments and its scheme's own port aside; a redirect to its own target is followed by a later exchange, never by
        // itself, even where its response's end, which it began before, was not recorded; and one a proxy was asked for,
        // its target the whole address, resolves against it and is followed by the first asking for its location, a whole
        // address too, its host in any case and its scheme's own port aside.
        Assert.Equal([3, -1, -1, -1, 6, -1, -1, -1, -1, 10, -1, 12, -1, 14, -1, 16, -1, -1], HttpRedirects.FollowUps(exchanges, heads));
        Assert.Throws<ArgumentException>(() => HttpRedirects.FollowUps(exchanges, heads[1..]));
    }

    /// <summary>
    /// FX-HTTP-004 measured that WinINet's capture keeps a gzip body as its client read it: the coded bytes where the client
    /// read them, the body before its coding where it had WinINet decode it, under heads kept as the wire carried them. Each
    /// of the fixture's responses, its head rebuilt and checked against the server's truth byte for byte, is set beside its
    /// body as each capture kept it: none is said to disagree, and the decoded bodies would be, but for their coding.
    /// </summary>
    [Fact(DisplayName = "P2: FX-HTTP-004's responses, kept coded or as WinINet decoded them, agree with the length their heads declare or are not said to disagree")]
    public void CodedResponsesAreSetBesideTheirHeadsAsTheirCaptureKeptThem()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-004", "evidence");
        foreach ((string run, bool decoded) in new[] { ("coded", false), ("decoded", true) })
        {
            // The server's truth of each response: its head, the body before its coding and the coded bytes, by length and hash.
            var sent = new Dictionary<(long Call, string Part), (long Length, string Hash)>();
            foreach (string line in File.ReadLines(Path.Combine(evidence, run, "truth-http-server.jsonl")))
            {
                TruthRecord record = System.Text.Json.JsonSerializer.Deserialize<TruthRecord>(line, TruthJson)!;
                if (record is { CallId: { } call, Status: { } part, DeclaredBytes: { } length, ResourceName: { } hash })
                {
                    sent[(call, part)] = (length, hash);
                }
            }

            long[] calls = [.. sent.Keys.Where(key => key.Part == "response-head").Select(key => key.Call).Order()];
            Assert.Equal(64, calls.Length);
            int agreed = 0, notSaid = 0;
            foreach (long call in calls)
            {
                (long coded, _) = sent[(call, "response-body-coded")];
                (long before, _) = sent[(call, "response-body")];
                byte[] head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Encoding: gzip\r\nContent-Length: {coded}\r\nX-InterCat-Fixture: FX-HTTP-004\r\n\r\n"));
                Assert.Equal(sent[(call, "response-head")], (head.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(head))));

                var heads = new HttpExchangeHeads(Head(string.Create(CultureInfo.InvariantCulture, $"POST /fx/{call} HTTP/1.1\r\n\r\n"), request: true),
                    Head(head, request: false));
                var body = new HttpPartRecord(1, decoded ? before : coded, true);
                HttpBodyDeclaration declaration = heads.ResponseBody(body)!;
                Assert.False(declaration.Contradicts, declaration.Statement);
                agreed += declaration.Statement.EndsWith("and its recorded buffers hold them all.", StringComparison.Ordinal) ? 1 : 0;
                notSaid += declaration.Statement.EndsWith("so a body of another length is not said to disagree.", StringComparison.Ordinal) ? 1 : 0;

                // The same head without its coding declares a length the decoded body contradicts: the coding is what stays it.
                if (decoded)
                {
                    var identity = new HttpExchangeHeads(heads.Request,
                        Head(Encoding.ASCII.GetString(head).Replace("Content-Encoding: gzip\r\n", string.Empty, StringComparison.Ordinal), request: false));
                    Assert.True(identity.ResponseBody(body)!.Contradicts);
                }
            }

            Assert.Equal(decoded ? (0, 64) : (64, 0), (agreed, notSaid));
        }
    }

    /// <summary>
    /// FX-HTTP-008 measured that WinINet's capture keeps a body in deflate or br as its client read it: decoded where the
    /// client had WinINet decode it, for br and for deflate written as RFC 1951's raw data - and, for deflate in the zlib
    /// format RFC 9110 names, which WinINet could not decode, as one buffer of no byte. Each of the three recorded runs'
    /// responses, its head rebuilt and checked against the server's truth byte for byte, is set beside its body as the
    /// capture kept it: none is said to disagree, and a deflate body kept as no byte is said to be possibly one WinINet could
    /// not decode.
    /// </summary>
    [Fact(DisplayName = "P2: FX-HTTP-008's responses in deflate and br, kept as WinINet decoded them or as no byte where it could not, are not said to disagree with the length their heads declare")]
    public void DeflateAndBrotliResponsesAreSetBesideTheirHeadsAsTheirCaptureKeptThem()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-HTTP-008", "evidence");
        foreach ((string run, string coding, bool undecodable) in new[] { ("br-decoded", "br", false), ("deflate-raw-decoded", "deflate", false), ("deflate-decoded", "deflate", true) })
        {
            var sent = new Dictionary<(long Call, string Part), (long Length, string Hash)>();
            foreach (string line in File.ReadLines(Path.Combine(evidence, run, "truth-http-server.jsonl")))
            {
                TruthRecord record = System.Text.Json.JsonSerializer.Deserialize<TruthRecord>(line, TruthJson)!;
                if (record is { CallId: { } call, Status: { } part, DeclaredBytes: { } length, ResourceName: { } hash })
                {
                    sent[(call, part)] = (length, hash);
                }
            }

            long[] calls = [.. sent.Keys.Where(key => key.Part == "response-head").Select(key => key.Call).Order()];
            Assert.Equal(64, calls.Length);
            int agreed = 0, notSaid = 0, undecoded = 0, keptEmpty = 0;
            foreach (long call in calls)
            {
                (long coded, _) = sent[(call, "response-body-coded")];
                (long before, _) = sent[(call, "response-body")];
                keptEmpty += coding == "deflate" && (undecodable || before == 0) ? 1 : 0;
                byte[] head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Encoding: {coding}\r\nContent-Length: {coded}\r\nX-InterCat-Fixture: FX-HTTP-008\r\n\r\n"));
                Assert.Equal(sent[(call, "response-head")], (head.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(head))));

                // As the capture kept it: the body before its coding where WinINet decoded it, one buffer of no byte where it
                // could not.
                var heads = new HttpExchangeHeads(Head(string.Create(CultureInfo.InvariantCulture, $"POST /fx/{call} HTTP/1.1\r\n\r\n"), request: true),
                    Head(head, request: false));
                HttpBodyDeclaration declaration = heads.ResponseBody(new HttpPartRecord(1, undecodable ? 0 : before, true))!;
                Assert.False(declaration.Contradicts, declaration.Statement);
                agreed += declaration.Statement.EndsWith("and its recorded buffers hold them all.", StringComparison.Ordinal) ? 1 : 0;
                notSaid += declaration.Statement.EndsWith("(FX-HTTP-008), so a body of another length is not said to disagree.", StringComparison.Ordinal) ? 1 : 0;
                undecoded += declaration.Statement.Contains("keeps a deflate body it could not decode as no byte", StringComparison.Ordinal) ? 1 : 0;
            }

            // Every body decoded is another length than its coded bytes; every deflate body kept as no byte - each one WinINet
            // could not decode, and one decoded to nothing, which no record tells apart - is said to be possibly the first.
            Assert.Equal((0, 64, keptEmpty), (agreed, notSaid, undecoded));
            Assert.Equal(undecodable ? 64 : coding == "deflate" ? 1 : 0, keptEmpty);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions TruthJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root holding InterCat.slnx was not found.");
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

    [Fact(DisplayName = "P2: a process's exchanges read as a table are the ones its rung lists, each with what its heads say, within a scope and a limit, and refused for another session")]
    public void AProcesssExchangesReadAsATable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Conversations(out SourceFieldRowV1[] fields, out var content), fields: fields, content: content);
        string channel = HttpExchangeKeys.Channel(SessionHttpExchangesTests.Client(session.Store));
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, channel);

        // Every exchange the rung lists, a line each in its order, with the figures its row shows and what its heads say;
        // a head not whole is not decoded.
        HttpExchangeTableRead table = SessionHttpExchanges.Table(session.Store, page.SessionId, channel, 10);
        Assert.Equal((page.SessionId, page.Generation, false, (string?)null), (table.SessionId, table.Generation, table.More, table.Problem));
        Assert.Equal(page.Exchanges.Select(row => row.Key), table.Lines.Select(line => line.Key));
        Assert.Equal(page.Exchanges.Select(row => (row.Exchange.Number, row.Exchange.DurationNanoseconds, row.Exchange.Complete,
                row.Exchange.RequestHead, row.Exchange.ResponseBody)),
            table.Lines.Select(line => (line.Number, line.DurationNanoseconds, line.RecordedWhole, line.RequestHead, line.ResponseBody)));
        Assert.All(table.Lines, line => Assert.Equal((page.SessionId, page.Generation, 4_242, channel, (string?)null),
            (line.SessionId, line.Generation, line.ProcessId, line.ProcessKey, line.Unattributed)));
        Assert.Equal(["0.000001", "0.000002"], table.Lines.Select(line => line.FirstSeconds));
        Assert.Equal(("GET /x", "a", "200 OK"), (table.Lines[0].Heads!.Asked, table.Lines[0].Heads!.Host, table.Lines[0].Heads!.Answered));
        Assert.StartsWith("The part is not whole", table.Lines[1].Heads!.Request!.NotDecoded, StringComparison.Ordinal);
        Assert.Null(table.Lines[1].Heads!.Response);

        // A limit lists that many and says it left the rest out; an interval keeps to the exchanges the rung lists there.
        HttpExchangeTableRead first = SessionHttpExchanges.Table(session.Store, page.SessionId, channel, 1);
        Assert.Equal([page.Exchanges[0].Key], first.Lines.Select(line => line.Key));
        Assert.True(first.More);
        var later = new TimeRange(15, 25);
        Assert.Equal([page.Exchanges[1].Key], SessionHttpExchanges.Exchanges(session.Store, channel, interval: later).Exchanges.Select(row => row.Key));
        Assert.Equal([page.Exchanges[1].Key], SessionHttpExchanges.Table(session.Store, page.SessionId, channel, 10, later).Lines.Select(line => line.Key));

        // A process the policy does not admit holds none, which is said; another session's folder, a key naming one exchange
        // rather than a process's, or no room for a line, is refused.
        HttpExchangeTableRead direct = SessionHttpExchanges.Table(session.Store, page.SessionId, channel, 10, policy: EvidencePolicy.DirectOnly);
        Assert.Equal((0, "This generation holds no HTTP exchanges of that process."), (direct.Lines.Count, direct.Problem));
        Assert.Throws<InvalidOperationException>(() => SessionHttpExchanges.Table(session.Store, Guid.NewGuid(), channel, 10));
        Assert.Throws<ArgumentException>(() => SessionHttpExchanges.Table(session.Store, page.SessionId, page.Exchanges[0].Key, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionHttpExchanges.Table(session.Store, page.SessionId, channel, 0));
    }

    [Fact(DisplayName = "I21: a buffer leads to the exchange it belongs to, by its raw identity under the evidence policy, and a record of none says why")]
    public void ABufferLeadsToItsExchange()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Conversations(out SourceFieldRowV1[] fields, out var content), fields: fields, content: content);
        HttpExchangePage page = SessionHttpExchanges.Exchanges(session.Store, HttpExchangeKeys.Channel(SessionHttpExchangesTests.Client(session.Store)));
        SessionEvidenceRecord[] records = [.. SessionEvidenceQuery.Read(session.Store).Records.OrderBy(record => record.Observation.RawRecordOrdinal)];

        // Each buffer of exchange 7 - its request head's two, its response head and its body - leads to it, and each of 8's to 8.
        (string seven, string eight) = (page.Exchanges[0].Key, page.Exchanges[1].Key);
        Assert.Equal([seven, seven, seven, seven, eight, eight], records.Where(record => record.Observation.Mechanism == Mechanism.Http)
            .Select(record => SessionHttpExchanges.ExchangeOf(session.Store, page.SessionId, record).Key));

        // A record of no buffer, or of a process the policy does not admit, leads to none and says why.
        SessionEvidenceRecord lifecycle = records.Single(record => record.Observation.Mechanism != Mechanism.Http);
        Assert.Equal(new HttpExchangeFound(null, "This record is no HTTP buffer, so it belongs to no exchange."),
            SessionHttpExchanges.ExchangeOf(session.Store, page.SessionId, lifecycle));
        HttpExchangeFound direct = SessionHttpExchanges.ExchangeOf(session.Store, page.SessionId, records[1], EvidencePolicy.DirectOnly);
        Assert.Null(direct.Key);
        Assert.StartsWith("Its exchange's process is not admitted under the direct-only evidence policy", direct.Problem, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => SessionHttpExchanges.ExchangeOf(session.Store, Guid.NewGuid(), records[1]));

        // A buffer raised before its process was created binds to no instance, and one whose source named no exchange
        // belongs to none.
        using var early = new TemporarySession();
        ObservationRowV1[] before = [Http(10, 2001, 1)];
        Publish(early.Store, [Lifecycle(100, ObservationKind.Create, 4_242, 1_000_000), .. before], fields: Fields(before, [(0, 7, 0, 3)]));
        SessionEvidenceRecord unbound = SessionEvidenceQuery.Read(early.Store).Records.Single(record => record.Observation.Mechanism == Mechanism.Http);
        Assert.Equal(new HttpExchangeFound(null, "Its exchange's process is bound to no process instance, so its conversation cannot be read."),
            SessionHttpExchanges.ExchangeOf(early.Store, early.Store.SessionId, unbound));
        using var unnamed = new TemporarySession();
        Publish(unnamed.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1_000_000), Http(10, 2001, 1)]);
        SessionEvidenceRecord lone = SessionEvidenceQuery.Read(unnamed.Store).Records.Single(record => record.Observation.Mechanism == Mechanism.Http);
        Assert.Equal(new HttpExchangeFound(null, "This buffer belongs to no exchange this generation groups: its source named none for it."),
            SessionHttpExchanges.ExchangeOf(unnamed.Store, unnamed.Store.SessionId, lone));

        // Two exchanges' buffers sharing an ordinal in two epochs of their source are each their own exchange's.
        using var epochs = new TemporarySession();
        ObservationRowV1[] reused = [Http(10, 2001, 1), Http(20, 2001, 1) with { RawSourceEpoch = 2 }];
        Publish(epochs.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1_000_000), .. reused], fields: Fields(reused, [(0, 7, 0, 3), (1, 8, 0, 3)]));
        HttpExchangePage both = SessionHttpExchanges.Exchanges(epochs.Store, HttpExchangeKeys.Channel(SessionHttpExchangesTests.Client(epochs.Store)));
        Assert.Equal(both.Exchanges.Select(exchange => exchange.Key), SessionEvidenceQuery.Read(epochs.Store).Records
            .Where(record => record.Observation.Mechanism == Mechanism.Http).OrderBy(record => record.Observation.NativeTicks)
            .Select(record => SessionHttpExchanges.ExchangeOf(epochs.Store, epochs.Store.SessionId, record).Key));
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
