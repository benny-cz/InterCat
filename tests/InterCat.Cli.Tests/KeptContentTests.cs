using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat content as a person runs it on a record's kept bytes (§3.7, ADR-036, R18): shown inert, as text where the source
/// declares text, and saved as they are, a chosen range of them; what there is not to show said in words; a whole HTTP part
/// shown and saved as one; and a range outside what was kept refused by what was kept.
/// </summary>
public sealed class KeptContentTests : IDisposable
{
    private readonly TemporaryDirectory root = new();
    private readonly TemporarySession kept = new();
    private readonly SessionEvidencePage page;

    public KeptContentTests()
    {
        // A text message with a control character in it, a binary one cut at the record limit, an empty one, and one the
        // capture could not keep, its content limit reached.
        ObservationRowV1[] rows =
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 11, 100, 1),
            Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 100, 2),
            Transfer(14, ObservationKind.Send, AccountingSide.SendSide, 0, 100, 3),
            Transfer(16, ObservationKind.Send, AccountingSide.SendSide, 50, 100, 4),
        ];
        (ContentFragmentV1 notKept, _) = Content(rows[3], new byte[50], 64);
        Publish(kept.Store, rows, content: (ContentHeader(recordLimit: 64),
        [
            Content(rows[0], "hello\u0000world"u8.ToArray(), 64),
            Content(rows[1], new byte[100], 64, ContentEncodingV1.Binary),
            Content(rows[2], Array.Empty<byte>(), 64),
            (notKept with { Disposition = ContentDispositionV1.OmittedBySessionLimit, Kept = 0 }, ReadOnlyMemory<byte>.Empty),
        ]));
        page = SessionEvidenceQuery.Read(kept.Store);
        kept.Store.ReleaseSegmentReaders();
    }

    public void Dispose()
    {
        kept.Dispose();
        root.Dispose();
    }

    [Fact(DisplayName = "§3.7: icat content shows a record's kept bytes, as text where its source declares text, and saves those chosen")]
    public async Task KeptBytesAreShownAndSaved()
    {
        // Shown, inert, and read as the text its source declares, its control character a mark.
        (InterCatExitCode code, string shown, string said) = await Run([.. Locator(1), "--reveal"]);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("  00000000  68 65 6C 6C 6F 00 77 6F  72 6C 64                 hello.world", shown, StringComparison.Ordinal);
        Assert.Contains("THE SAME BYTES AS THE TEXT THEIR SOURCE DECLARES", shown, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  hello␀world\r?$", shown);

        // A range of them, and a first byte past those kept refused by what was kept, not by its default last byte.
        (_, string chosen, _) = await Run([.. Locator(1), "--reveal", "--from", "2", "--to", "5"]);
        Assert.Contains("  Chosen: bytes 2 to 5 (4 bytes).", chosen, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  llo␀\r?$", chosen);
        (code, _, said) = await Run([.. Locator(1), "--reveal", "--from", "99"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("The kept bytes are 0 to 10; choose a range within them.", said, StringComparison.Ordinal);

        // A binary message is never read as text, and a range past its cut is refused the same way.
        (_, string binary, _) = await Run([.. Locator(2), "--reveal", "--from", "60"]);
        Assert.DoesNotContain("THE SAME BYTES AS THE TEXT", binary, StringComparison.Ordinal);
        (code, _, said) = await Run([.. Locator(2), "--reveal", "--to", "70"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("The kept bytes are 0 to 63; choose a range within them.", said, StringComparison.Ordinal);

        // Saved as they are, never over a file without --overwrite, which applies to --save alone.
        string saved = Path.Combine(root.Path, "saved.bin");
        (code, _, said) = await Run([.. Locator(1), "--save", saved]);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains($"Saved bytes 0 to 10 (11 bytes) to {saved}, as they are.", said, StringComparison.Ordinal);
        Assert.Equal("hello\u0000world"u8.ToArray(), File.ReadAllBytes(saved));
        (code, _, said) = await Run([.. Locator(1), "--save", saved]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{saved} exists. Pass --overwrite to replace it.", said, StringComparison.Ordinal);
        (code, _, _) = await Run([.. Locator(1), "--save", saved, "--overwrite", "--from", "6"]);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Equal("world"u8.ToArray(), File.ReadAllBytes(saved));
        (code, _, said) = await Run([.. Locator(1), "--overwrite"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--overwrite applies only to --save.", said, StringComparison.Ordinal);

        // A locator option given twice is said to be, not called unknown.
        (code, _, said) = await Run([.. Locator(1), "--generation", "7"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--generation is given more than once; give it once.", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.7: icat content says when there is nothing to show: an empty message, one not kept, and bytes kept without consent")]
    public async Task NothingToShowIsSaid()
    {
        (InterCatExitCode empty, string nothing, _) = await Run([.. Locator(3), "--reveal"]);
        Assert.Equal(InterCatExitCode.Success, empty);
        Assert.Contains("The message held no bytes, so there is nothing to show or save.", nothing, StringComparison.Ordinal);

        // Not kept, the answer is whole when no byte was asked for, and partial when one was.
        (InterCatExitCode notAsked, string notKept, _) = await Run(Locator(4));
        Assert.Equal(InterCatExitCode.Success, notAsked);
        Assert.Matches(@"(?m)^  Kept +None: the capture had reached its content limit when this record arrived\r?$", notKept);
        Assert.Contains("No byte of this message was kept, so there is nothing to show or save.", notKept, StringComparison.Ordinal);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, (await Run([.. Locator(4), "--reveal"])).Code);

        // A search counts the record not kept as not searched, its answer whole; the empty message holds nothing to find.
        (InterCatExitCode searched, string found, string why) = await Run("content", kept.Path, "--find", "world");
        Assert.True(searched == InterCatExitCode.Success, why);
        Assert.Matches(@"(?m)^  Searched +the kept bytes of 2 records, 75 B\r?$", found);
        Assert.Matches(@"(?m)^  Not searched +1 record the capture kept no byte of, having reached its content limit\r?$", found);
        string nowhere = Path.Combine(root.Path, "nowhere");
        (InterCatExitCode missing, _, string none) = await Run("content", nowhere, "--find", "world");
        Assert.Equal(InterCatExitCode.InvalidInvocation, missing);
        Assert.Contains($"No session directory at {nowhere}.", none, StringComparison.Ordinal);

        // Kept without consent to inspect it, it is never shown or saved one record at a time.
        using var withheld = new TemporarySession();
        ObservationRowV1 row = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 5, 100, 1);
        Publish(withheld.Store, [row], content: (ContentHeader(recordLimit: 64, inspection: ContentInspectionV1.Disabled),
            [Content(row, "hello"u8.ToArray(), 64)]));
        SessionEvidencePage hidden = SessionEvidenceQuery.Read(withheld.Store);
        withheld.Store.ReleaseSegmentReaders();
        string[] locator =
        [
            "content", withheld.Path, "--session-id", hidden.SessionId.ToString(), "--generation",
            hidden.Generation.ToString(CultureInfo.InvariantCulture), "--segment", hidden.Records[0].SegmentName, "--row",
            hidden.Records[0].SegmentRow.ToString(CultureInfo.InvariantCulture),
        ];
        string target = Path.Combine(root.Path, "withheld.bin");
        foreach (string[] asked in new string[][] { [.. locator, "--reveal"], [.. locator, "--save", target] })
        {
            (InterCatExitCode code, string output, string said) = await Run(asked);
            Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
            Assert.Contains("The capture kept these bytes without consent to inspect them, so they are never shown or saved one "
                + "record at a time; an original evidence package carries them as evidence.", said, StringComparison.Ordinal);
            Assert.DoesNotContain("68 65 6C 6C 6F", output, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(target));
    }

    [Fact(DisplayName = "P2: icat content shows and saves a whole HTTP part as one, and refuses --part of a record that belongs to none")]
    public async Task AWholePartIsShownAndSavedAsOne()
    {
        // Exchange 4's response body in two buffers, both kept whole.
        using var http = new TemporarySession();
        ObservationRowV1[] rows = [Body(10, 1), Body(12, 2)];
        (long Sequence, long Flags)[] places = [(0, 1), (1, 2)];
        SourceFieldRowV1[] fields =
        [
            .. places.SelectMany((place, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, 4),
                Field(rows[index], SourceField.ContentBufferSequence, place.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, place.Flags),
            }),
        ];
        Publish(http.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8),
            [Content(rows[0], "head"u8.ToArray(), 8, ContentEncodingV1.Binary), Content(rows[1], "tail"u8.ToArray(), 8, ContentEncodingV1.Binary)]));
        SessionEvidencePage parts = SessionEvidenceQuery.Read(http.Store);
        http.Store.ReleaseSegmentReaders();
        string[] locator =
        [
            "content", http.Path, "--session-id", parts.SessionId.ToString(), "--generation",
            parts.Generation.ToString(CultureInfo.InvariantCulture), "--segment", parts.Records[0].SegmentName, "--row",
            parts.Records[0].SegmentRow.ToString(CultureInfo.InvariantCulture),
        ];

        (InterCatExitCode hidden, string facts, _) = await Run([.. locator, "--part"]);
        Assert.Equal(InterCatExitCode.Success, hidden);
        Assert.Matches(@"(?m)^  Part +The response body of exchange 4: 2 buffers, from its first to its last, all kept whole - 8 bytes\.\r?$",
            facts);
        Assert.Contains("Its bytes are hidden. Add --reveal to show the whole part, bounded and inert, or --save <file> to write "
            + "it as it is.", facts, StringComparison.Ordinal);

        (_, string whole, _) = await Run([.. locator, "--part", "--reveal"]);
        Assert.Contains("THE WHOLE PART · INERT HEXADECIMAL", whole, StringComparison.Ordinal);
        Assert.Contains("  Chosen: bytes 0 to 7 (8 bytes) of the response body of exchange 4.", whole, StringComparison.Ordinal);
        Assert.Contains("  00000000  68 65 61 64 74 61 69 6C                           headtail", whole, StringComparison.Ordinal);
        (_, string chosen, _) = await Run([.. locator, "--part", "--reveal", "--from", "2", "--to", "5"]);
        Assert.Contains("  00000002  61 64 74 61                                       adta", chosen, StringComparison.Ordinal);
        (InterCatExitCode past, _, string pastSaid) = await Run([.. locator, "--part", "--reveal", "--from", "9"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, past);
        Assert.Contains("The kept bytes are 0 to 7; choose a range within them.", pastSaid, StringComparison.Ordinal);

        string saved = Path.Combine(root.Path, "part.bin");
        (InterCatExitCode written, _, string writing) = await Run([.. locator, "--part", "--save", saved]);
        Assert.Equal(InterCatExitCode.Success, written);
        Assert.Contains($"Saved bytes 0 to 7 (8 bytes) of the response body of exchange 4 to {saved}, as they are.", writing,
            StringComparison.Ordinal);
        Assert.Equal("headtail"u8.ToArray(), File.ReadAllBytes(saved));

        // A record of no HTTP part has no part to show.
        (InterCatExitCode refused, _, string refusal) = await Run([.. Locator(1), "--part"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Contains("--part applies to a buffer of an HTTP head or body; this record's belongs to no part.", refusal,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.7: icat content says a record's facts and keeps its bytes hidden until --reveal or --save asks for them")]
    public async Task BytesStayHiddenUntilAsked()
    {
        (InterCatExitCode code, string shown, string said) = await Run(Locator(1));
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Kept +All of it: bytes 0 to 10 \(11 bytes\)\r?$", shown);
        Assert.Contains("Its bytes are hidden. Add --reveal to show them, bounded and inert, or --save <file> to write them as "
            + "they are.", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("68 65 6C 6C 6F", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", shown, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.7: icat content and icat raw refuse a locator naming no segment or row as a mistake in it, and a stale one as stale")]
    public async Task ABadLocatorIsAMistakeInIt()
    {
        SessionEvidenceRecord record = page.Records.Single(record => record.Observation.RawRecordOrdinal == 1);
        string generation = page.Generation.ToString(CultureInfo.InvariantCulture);
        foreach (string command in new[] { "content", "raw" })
        {
            string[] Asked(string generationText, string segment, string row) =>
            [
                command, kept.Path, "--session-id", page.SessionId.ToString(), "--generation", generationText, "--segment",
                segment, "--row", row,
            ];

            // A segment the generation does not name is a mistake in the locator, not damage to the session.
            (InterCatExitCode code, _, string said) = await Run(Asked(generation, "observations-9999.seg", "0"));
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains($"Generation {generation} names no observation segment 'observations-9999.seg'. Take the record's "
                + "locator from icat evidence.", said, StringComparison.Ordinal);

            // So is a row the segment does not hold, the first past its last, and the rows it does hold are said.
            (code, _, said) = await Run(Asked(generation, record.SegmentName, "4"));
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains($"Segment '{record.SegmentName}' holds rows 0 to 3; row 4 is not one of them.", said,
                StringComparison.Ordinal);

            // A generation the session no longer reads is followed to no row.
            (code, _, said) = await Run(Asked("7", record.SegmentName, "0"));
            Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
            Assert.Contains("locator names another session or generation. Restart from icat evidence; no row was silently "
                + "shifted.", said, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "P2: icat content says why it shows no byte of a part kept without consent, and that an empty part holds none")]
    public async Task APartItMayNotShowSaysWhy()
    {
        const string Withheld = "The capture kept this part's bytes without consent to inspect them, so it is never shown or "
            + "saved; an original evidence package carries them as evidence.";

        // Exchange 4's response body whole in two buffers, and in two with the one between them never recorded, each kept
        // without consent to inspect it.
        using var whole = new TemporarySession();
        string[] wholePart = PartLocator(whole, [(0, 1, "head"u8.ToArray()), (1, 2, "tail"u8.ToArray())], ContentInspectionV1.Disabled);
        using var gapped = new TemporarySession();
        string[] gappedPart = PartLocator(gapped, [(0, 1, "head"u8.ToArray()), (2, 2, "tail"u8.ToArray())], ContentInspectionV1.Disabled);
        string target = Path.Combine(root.Path, "withheld-part.bin");
        foreach (string[] asked in new string[][]
        {
            [.. wholePart, "--part", "--reveal"], [.. wholePart, "--part", "--save", target], [.. gappedPart, "--part", "--reveal"],
        })
        {
            (InterCatExitCode code, string output, string said) = await Run(asked);
            Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
            Assert.Contains(Withheld, said, StringComparison.Ordinal);
            Assert.DoesNotContain("68 65 61 64", output, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(target));

        // A part recorded whole that held no bytes has none to show, and nothing asked for is missing.
        using var empty = new TemporarySession();
        string[] emptyPart = PartLocator(empty, [(0, 3, [])]);
        (InterCatExitCode none, string nothing, string why) = await Run([.. emptyPart, "--part", "--reveal"]);
        Assert.True(none == InterCatExitCode.Success, why);
        Assert.Contains("The part holds no bytes, so there is nothing to show or save.", nothing, StringComparison.Ordinal);
    }

    /// <summary>
    /// Publishes exchange 4's response body as <paramref name="buffers"/>, each numbered and flagged as its source would, and
    /// returns the locator of its first buffer's record.
    /// </summary>
    private static string[] PartLocator(TemporarySession http, (long Sequence, long Flags, byte[] Bytes)[] buffers,
        ContentInspectionV1 inspection = ContentInspectionV1.HexAndText)
    {
        ObservationRowV1[] rows = [.. buffers.Select((_, index) => Body(10 + (2 * index), (ulong)(index + 1)))];
        SourceFieldRowV1[] fields =
        [
            .. buffers.SelectMany((buffer, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, 4),
                Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        Publish(http.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8, inspection: inspection),
            [.. buffers.Select((buffer, index) => Content(rows[index], buffer.Bytes, 8, ContentEncodingV1.Binary))]));
        SessionEvidencePage parts = SessionEvidenceQuery.Read(http.Store);
        http.Store.ReleaseSegmentReaders();
        return
        [
            "content", http.Path, "--session-id", parts.SessionId.ToString(), "--generation",
            parts.Generation.ToString(CultureInfo.InvariantCulture), "--segment", parts.Records[0].SegmentName, "--row",
            parts.Records[0].SegmentRow.ToString(CultureInfo.InvariantCulture),
        ];
    }

    private string[] Locator(ulong ordinal)
    {
        SessionEvidenceRecord record = page.Records.Single(record => record.Observation.RawRecordOrdinal == ordinal);
        return
        [
            "content", kept.Path, "--session-id", page.SessionId.ToString(), "--generation",
            page.Generation.ToString(CultureInfo.InvariantCulture), "--segment", record.SegmentName, "--row",
            record.SegmentRow.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>A WinINet response body record of process 4242, whose payload names no owner.</summary>
    private static ObservationRowV1 Body(long ticks, ulong ordinal) =>
        Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = 2004,
            HeaderProcessId = 4_242,
            Direction = Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        };

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
