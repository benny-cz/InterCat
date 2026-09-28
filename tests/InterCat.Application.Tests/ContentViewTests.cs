using System.Globalization;
using System.Text;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// How a person inspects kept content (§3.7, §11.2, ADR-036): its facts before any byte; its bytes only when asked, with
/// consent, and while the session keeps them; inert hexadecimal, and declared text with every control made visible.
/// </summary>
public sealed class ContentViewTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§11.2: the hex view is inert: sixteen bytes a line, at their offsets in the message, with printable ASCII beside them")]
    public void TheHexViewIsInert()
    {
        byte[] bytes = [.. "InterCat content"u8, 0x00, 0x01, 0xFF, (byte)'!'];
        IReadOnlyList<ContentHexRow> rows = ContentBytesView.Rows(bytes, 4_096);

        Assert.Equal(2, rows.Count);
        Assert.Equal("00001000  49 6E 74 65 72 43 61 74  20 63 6F 6E 74 65 6E 74  InterCat content", rows[0].Line);
        Assert.Equal((4_112L, "...!"), (rows[1].Offset, rows[1].Text));
        Assert.StartsWith("00 01 FF 21 ", rows[1].Hex, StringComparison.Ordinal);

        // A short last line keeps the text column where the others have it.
        Assert.Equal(rows[0].Hex.Length, rows[1].Hex.Length);
        Assert.Equal(2, ContentBytesView.Dump(bytes, 0).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentBytesView.Rows(new byte[ContentBytesView.MaximumShownBytes + 1], 0));

        // A range longer than the view is shown from its first byte.
        var range = new ContentRange(10, 10 + (2L * ContentBytesView.MaximumShownBytes));
        Assert.Equal(new ContentRange(10, 10 + ContentBytesView.MaximumShownBytes - 1), ContentBytesView.Shown(range));
    }

    [Fact(DisplayName = "§11.2: text its source declares is shown with every control, invisible and private character made visible")]
    public void DeclaredTextShowsWhatTheBytesHold()
    {
        byte[] utf8 = [.. Encoding.UTF8.GetBytes("a\u0000b\r\nc\rd\te‮fg​h\u007Fi\u0085j"), 0xFF];
        ContentText text = ContentBytesView.Text(utf8, ContentEncodingV1.Utf8);
        Assert.Equal("a␀b\nc␍d\te⟨U+202E⟩f⟨U+E000⟩g⟨U+200B⟩h␡i⟨U+0085⟩j�", text.Text);
        Assert.Equal((false, 7, 1), (text.Cut, text.Escaped, text.Replaced));

        // UTF-16 cut mid-character shows the cut, and nothing past the bound is shown.
        byte[] utf16 = [.. Encoding.Unicode.GetBytes("hi"), 0x41];
        Assert.Equal("hi�", ContentBytesView.Text(utf16, ContentEncodingV1.Utf16LittleEndian).Text);
        ContentText bounded = ContentBytesView.Text(Encoding.UTF8.GetBytes(new string('a', ContentBytesView.MaximumTextCharacters + 10)),
            ContentEncodingV1.Utf8);
        Assert.Equal((true, ContentBytesView.MaximumTextCharacters), (bounded.Cut, bounded.Text.Length));

        // Bytes a source declares binary are never read as text.
        Assert.False(ContentBytesView.DeclaresText(ContentEncodingV1.Binary));
        Assert.Throws<ArgumentException>(() => ContentBytesView.Text(utf8, ContentEncodingV1.Binary));
    }

    [Fact(DisplayName = "§11.2: a typed byte range reads as a person writes it, and one outside the kept bytes is refused in words")]
    public void ATypedRangeIsReadAndChecked()
    {
        var kept = new ContentRange(0, 4_095);
        Assert.True(ContentBytesView.TryParseRange("16", "0x1F", kept, out ContentRange hex, out _));
        Assert.Equal(new ContentRange(16, 31), hex);
        Assert.True(ContentBytesView.TryParseRange("4 000", "4,095", kept, out ContentRange grouped, out _));
        Assert.Equal(new ContentRange(4_000, 4_095), grouped);
        Assert.True(ContentBytesView.TryParseRange("1 000", " 2 000 ", kept, out ContentRange spaced, out _));
        Assert.Equal(new ContentRange(1_000, 2_000), spaced);

        string? Problem(string? first, string? last) =>
            ContentBytesView.TryParseRange(first, last, kept, out _, out string? problem) ? null : problem;
        Assert.Equal("Enter the first byte as an offset, like 16 or 0x10.", Problem("", "5"));
        Assert.Equal("Enter the first byte as an offset, like 16 or 0x10.", Problem("-1", "5"));
        Assert.Equal("Enter the last byte as an offset, like 4095 or 0xFFF.", Problem("0", "0x"));
        Assert.Equal("The first byte comes after the last.", Problem("10", "5"));
        Assert.EndsWith("choose a range within them.", Problem("0", "4096"), StringComparison.Ordinal);
        Assert.Equal("16 to 31 (16 bytes)", hex.Describe(CultureInfo.InvariantCulture)["bytes ".Length..]);
    }

    [Fact(DisplayName = "§3.7: before any byte, the viewer says what the content is, how much was kept, what is missing and under what")]
    public void TheFactsComeFirst()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8), Kept(rows)));
        IReadOnlyList<SessionEvidenceRecord> records = SessionEvidenceQuery.Read(session.Store).Records;
        Dictionary<string, string> FactsOf(int index) => ContentBytesView
            .Facts(records[index].Content!, records[index].Observation, 1, CultureInfo.InvariantCulture)
            .ToDictionary(fact => fact.Label, fact => fact.Value);

        Dictionary<string, string> cut = FactsOf(1);
        Assert.Equal("An application payload, which the record's owner sent", cut["What it is"]);
        Assert.StartsWith("Microsoft-Windows-Kernel-Network · event ", cut["Source"], StringComparison.Ordinal);
        Assert.Equal("UTF-8 text, as its source declares", cut["Encoding"]);
        Assert.Equal("16 bytes", cut["Message"]);
        Assert.Equal("Bytes 0 to 7 (8 bytes), cut by the 8-byte record limit", cut["Kept"]);
        Assert.Equal("Bytes 8 to 15 (8 bytes). They were never recorded, and nothing stands in for them", cut["Missing"]);
        Assert.Equal("One fragment, from its message's first byte; it is not joined with any other record's content", cut["Reassembly"]);
        Assert.Equal("test-scoped-content-v1, at most 8 bytes a record; its bytes are shown only when you ask", cut["Kept under"]);
        Assert.Equal("content-0000000001.icatc, beside the journal, read in generation 1", cut["Stored in"]);

        Dictionary<string, string> whole = FactsOf(0);
        Assert.Equal(("All of it: bytes 0 to 5 (6 bytes)", "None"), (whole["Kept"], whole["Missing"]));

        Dictionary<string, string> omitted = FactsOf(2);
        Assert.Equal("An application payload, which the record's owner received", omitted["What it is"]);
        Assert.Equal("Binary, as its source declares; it is never read as text", omitted["Encoding"]);
        Assert.Equal("None: the capture had reached its content limit when this record arrived", omitted["Kept"]);
        Assert.Equal("Bytes 0 to 299 (300 bytes). They were never recorded, and nothing stands in for them", omitted["Missing"]);
        Assert.Null(ContentBytesView.Kept(records[2].Content!.Fragment));
    }

    [Fact(DisplayName = "ADR-036: a record's bytes are read only when asked, only with consent, and only while its session keeps them")]
    public void BytesAreReadOnlyWhenAskedAndAllowed()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] early = Rows();
        ObservationRowV1[] late = Rows(100);
        Publish(session.Store, early, content: (ContentHeader(recordLimit: 8), Kept(early)));
        Publish(session.Store, late, content: (ContentHeader(recordLimit: 8), Kept(late)));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord cut = page.Records[1];

        SessionContentDetail facts = SessionContentQuery.Read(session.Store, page.SessionId, cut, revealBytes: false);
        Assert.True(facts.Available);
        Assert.Null(facts.Bytes);
        Assert.Equal(cut.Content, facts.Entry);
        SessionContentDetail revealed = SessionContentQuery.Read(session.Store, page.SessionId, cut, revealBytes: true);
        Assert.Equal("01234567"u8.ToArray(), revealed.Bytes);
        Assert.Equal(cut.Observation, revealed.Observation);

        // The page's own locator reads the same, and a locator of another generation is refused, never followed.
        SessionContentDetail located = SessionContentQuery.ReadAt(session.Store, page.SessionId, page.Generation,
            cut.SegmentName, cut.SegmentRow, revealBytes: true);
        Assert.Equal(revealed.Bytes, located.Bytes);
        Assert.Throws<InvalidOperationException>(() => SessionContentQuery.ReadAt(session.Store, page.SessionId,
            page.Generation + 1, cut.SegmentName, cut.SegmentRow, revealBytes: true));
        Assert.Throws<InvalidOperationException>(() => SessionContentQuery.Read(session.Store, Guid.NewGuid(), cut, revealBytes: false));

        // Released with its journal chunk, the content is gone and the record says so.
        _ = JournalRetention.Release(session.Store, early.Length, "keep the last chunk", DateTimeOffset.UnixEpoch);
        SessionContentDetail released = SessionContentQuery.Read(session.Store, page.SessionId, cut, revealBytes: true);
        Assert.False(released.Available);
        Assert.Contains("retention release", released.UnavailableReason, StringComparison.Ordinal);
        Assert.Null(released.Bytes);

        // Kept without consent to inspect them, the bytes are never read for a person.
        using var withheld = new TemporarySession();
        Publish(withheld.Store, early, content: (ContentHeader(recordLimit: 8, inspection: ContentInspectionV1.Disabled), Kept(early)));
        SessionEvidencePage withheldPage = SessionEvidenceQuery.Read(withheld.Store);
        SessionContentDetail refused = SessionContentQuery.Read(withheld.Store, withheldPage.SessionId, withheldPage.Records[1],
            revealBytes: true);
        Assert.True(refused.Available);
        Assert.Null(refused.Bytes);
        Assert.Contains("never shown", ContentBytesView.Facts(refused.Entry!, refused.Observation, refused.Generation,
            CultureInfo.InvariantCulture).Single(fact => fact.Label == "Kept under").Value, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§11.2: bytes a person chose to save are written whole, as they are, or not at all")]
    public async Task SavedBytesAreWrittenAsTheyAre()
    {
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.ContentView.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string destination = Path.Combine(folder, "chosen.bin");
            byte[] bytes = [0x00, 0xFF, 0x0A, 0x0D, 0x1B];
            await ExportFileWriter.WriteAsync(destination, bytes, overwrite: false);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            await Assert.ThrowsAsync<IOException>(() => ExportFileWriter.WriteAsync(destination, bytes.AsMemory(1), overwrite: false));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Three TCP records from <paramref name="first"/>: two sends and a receive.</summary>
    private static ObservationRowV1[] Rows(long first = 0) =>
    [
        Transfer(first + 10, ObservationKind.Send, AccountingSide.SendSide, 6, 100, (ulong)first + 1).Between(ClientEnd, ServerEnd),
        Transfer(first + 20, ObservationKind.Send, AccountingSide.SendSide, 16, 100, (ulong)first + 2).Between(ClientEnd, ServerEnd),
        Transfer(first + 30, ObservationKind.Receive, AccountingSide.ReceiveSide, 300, 100, (ulong)first + 3).Between(ClientEnd, ServerEnd),
    ];

    /// <summary>The first record's message kept whole, the second's cut by the 8-byte limit, the third's omitted by the session's.</summary>
    private static IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> Kept(ObservationRowV1[] rows) =>
    [
        Content(rows[0], Encoding.UTF8.GetBytes("GET /x"), 8),
        Content(rows[1], Encoding.UTF8.GetBytes("0123456789ABCDEF"), 8),
        (new ContentFragmentV1(rows[2].RawStreamId, rows[2].RawSourceEpoch, rows[2].RawRecordOrdinal,
            ContentClassificationV1.ApplicationPayload, Direction.Inbound, ContentEncodingV1.Binary,
            ContentDispositionV1.OmittedBySessionLimit, 0, 300, 0), ReadOnlyMemory<byte>.Empty),
    ];
}
