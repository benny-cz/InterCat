using System.Globalization;
using System.Text;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A content search (`contracts/content-v1.md` §4): the records whose kept bytes hold a text, found only in bytes kept with
/// consent to inspect them, each record's on its own, within its budgets, saying what it did not read, and showing no byte
/// unless previews are asked for.
/// </summary>
public sealed class ContentSearchTests
{
    [Fact(DisplayName = "P2: a content search lists the records whose kept bytes hold a text, how often and where first, and shows no byte unless previews are asked for")]
    public void ASearchListsTheRecordsHoldingTheText()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        Guid id = session.Store.SessionId;

        // In reading order: the head naming the host, the body naming it twice, and the UTF-16 record; the head split
        // across two records, the other process's capitalised name and the name past what a cut record kept are not found.
        ContentSearchResult found = ContentSearch.Find(session.Store, id, new() { Text = "example.com" });
        Assert.Equal([1UL, 3UL, 6UL], found.Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.Equal([(1, 32), (2, 9), (1, 12)], found.Hits.Select(hit => (hit.Matches, hit.FirstAt)));
        Assert.All(found.Hits, hit => Assert.Null(hit.Preview));
        Assert.Equal((8, false, 0, (string?)null, false), (found.RecordsRead, found.More, found.WithoutConsent, found.Stopped, found.Partial));
        Assert.Equal(221, found.BytesRead);
        Assert.All(found.Hits, hit => Assert.Equal(hit.Observation, SessionContentQuery.ReadAt(session.Store, id,
            found.Generation, hit.Segment, hit.Row, revealBytes: false).Observation));

        // A letter in either case finds the capitalised name too; one process's records keep to it.
        Assert.Equal([1UL, 3UL, 5UL, 6UL],
            ContentSearch.Find(session.Store, id, new() { Text = "EXAMPLE.com", IgnoreCase = true }).Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.Equal([5UL], ContentSearch.Find(session.Store, id, new() { Text = "example.com", IgnoreCase = true, ProcessId = 5_151 })
            .Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.Empty(ContentSearch.Find(session.Store, id, new() { Text = "EXAMPLE.com" }).Hits);

        // Matches are counted none overlapping another, and in UTF-16 only at a code unit's start.
        Assert.Equal(32, ContentSearch.Find(session.Store, id, new() { Text = "aa" }).Hits.Single().Matches);
        Assert.Empty(ContentSearch.Find(session.Store, id, new() { Text = "ab" }).Hits);

        // Previews, asked for, show the bytes around each first match as inert text.
        ContentSearchHit head = ContentSearch.Find(session.Store, id, new() { Text = "example.com", Previews = true }).Hits[0];
        Assert.Equal(new ContentPreview("HTTP/1.1\\x0D\\x0AHost: ", "example.com", "\\x0D\\x0A\\x0D\\x0A"), head.Preview);
        ContentSearchHit wide = ContentSearch.Find(session.Store, id, new() { Text = "example.com", Previews = true }).Hits[2];
        Assert.Equal(new ContentPreview("wide\\u005C ", "example.com", "\\u00E9"), wide.Preview);
        Assert.Equal(new ContentPreview(string.Empty, "GET", " /index.html HTT"),
            ContentSearch.Find(session.Store, id, new() { Text = "GET", Previews = true }).Hits.Single().Preview);
        Assert.Equal(new ContentPreview("nothing \\x5C ", "here", string.Empty),
            ContentSearch.Find(session.Store, id, new() { Text = "here", Previews = true }).Hits.Single().Preview);

        // A limit lists that many and says there are more; budgets stop the search and say how far it read.
        ContentSearchResult limited = ContentSearch.Find(session.Store, id, new() { Text = "example.com", Limit = 2 });
        Assert.Equal((2, true, false), (limited.Hits.Count, limited.More, limited.Partial));
        ContentSearchResult small = ContentSearch.Find(session.Store, id, new() { Text = "example.com", MaximumBytes = 60 });
        Assert.Equal("The search stopped at its budget of 60 bytes read, after 2 records; the rest were not searched, so a record "
            + "not listed may still hold the text.", small.Stopped);
        Assert.True(small.Partial);
        ContentSearchResult timed = ContentSearch.Find(session.Store, id, new() { Text = "example.com", Budget = TimeSpan.Zero });
        Assert.Equal((0, 0), (timed.RecordsRead, timed.Hits.Count));
        Assert.StartsWith("The search stopped at its budget of 0 seconds, after 0 records;", timed.Stopped, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => ContentSearch.Find(session.Store, Guid.NewGuid(), new() { Text = "x" }));
        Assert.Throws<ArgumentException>(() => ContentSearch.Find(session.Store, id, new() { Text = string.Empty }));
    }

    [Fact(DisplayName = "P2: a content search stopped when asked answers with what it found before, says it was stopped and is partial; one cancelled lists nothing")]
    public void AStoppedSearchSaysSo()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        Guid id = session.Store.SessionId;

        // Stopped, it ends before the next record it would read and answers; cancelled, it throws and answers nothing.
        ContentSearchResult stopped = ContentSearch.Find(session.Store, id, new() { Text = "example.com" }, CancellationToken.None,
            new CancellationToken(canceled: true));
        Assert.Equal((0, 0, true), (stopped.RecordsRead, stopped.Hits.Count, stopped.Partial));
        Assert.Equal("The search was stopped when asked, after 0 records; the rest were not searched, so a record not listed may "
            + "still hold the text.", stopped.Stopped);
        Assert.ThrowsAny<OperationCanceledException>(() => ContentSearch.Find(session.Store, id, new() { Text = "example.com" },
            new CancellationToken(canceled: true)));

        // Read on, a stopped search lists what it had not read, from the record it stopped before.
        ContentSearchResult after = ContentSearch.Continue(session.Store, id, stopped.Continuation!);
        Assert.Equal([1UL, 3UL, 6UL], after.Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.Equal((8, (ContentSearchContinuation?)null), (after.RecordsRead, after.Continuation));

        // A stop asked for once the search has read every record changes nothing.
        using var late = new CancellationTokenSource();
        ContentSearchResult whole = ContentSearch.Find(session.Store, id, new() { Text = "example.com" }, CancellationToken.None, late.Token);
        late.Cancel();
        Assert.Equal((8, 3, (string?)null, false), (whole.RecordsRead, whole.Hits.Count, whole.Stopped, whole.Partial));
    }

    [Fact(DisplayName = "P2: the records a content search lists write as a table, the bytes around each match only where previews were asked for, every text cell neutralized")]
    public void TheRecordsFoundWriteAsATable()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        Guid id = session.Store.SessionId;

        // A line per record listed, in its order, with the locator that shows its bytes, when it was raised and by whom,
        // how often it holds the text and where first, and what was searched for and how; no byte of it.
        var asked = new ContentSearchRequest { Text = "example.com", Limit = 2 };
        ContentSearchResult found = ContentSearch.Find(session.Store, id, asked);
        string[] lines = ContentSearchTable.Csv(found, asked).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(",", ContentSearchTable.Columns), lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.NotNull(found.Clock);
        Assert.Equal([.. found.Hits.Select(hit => string.Join(",", id.ToString("N"), found.Generation.ToString(CultureInfo.InvariantCulture),
            hit.Segment, hit.Row.ToString(CultureInfo.InvariantCulture),
            SessionTimeText.EpochSeconds(found.Clock!.Value, hit.Observation.NativeTicks), "4242", "TCP", "10",
            hit.Matches.ToString(CultureInfo.InvariantCulture), hit.FirstAt.ToString(CultureInfo.InvariantCulture), "example.com", "false"))],
            lines.Skip(1));
        Assert.DoesNotContain("Host", string.Join("\n", lines), StringComparison.Ordinal);

        // A record the kernel raised names the process that owns it, not the kernel.
        var owned = new ContentSearchRequest { Text = "example.com", IgnoreCase = true, ProcessId = 5_151 };
        Assert.EndsWith(",5151,TCP,10,1,0,example.com,true", ContentSearchTable.Csv(ContentSearch.Find(session.Store, id, owned), owned)
            .TrimEnd(), StringComparison.Ordinal);

        // With previews, the bytes around each first match as the search shows them; a cell a spreadsheet would evaluate
        // begins with an apostrophe, and one holding a comma or a quote is quoted.
        using var formula = new TemporarySession();
        ObservationRowV1 record = Record(10, 1, 4_242);
        Publish(formula.Store, [record], content: (ContentHeader(64),
            [Content(record, Encoding.ASCII.GetBytes("=HYPERLINK(\"http://x\",1)"), 64, ContentEncodingV1.Binary)]));
        formula.Store.ReleaseSegmentReaders();
        var previewed = new ContentSearchRequest { Text = "-HYPERLINK", Previews = true, IgnoreCase = true };
        Assert.Empty(ContentSearch.Find(formula.Store, formula.Store.SessionId, previewed).Hits);
        Assert.Equal(string.Join(",", [.. ContentSearchTable.Columns, .. ContentSearchTable.PreviewColumns]),
            ContentSearchTable.Csv(ContentSearch.Find(formula.Store, formula.Store.SessionId, previewed), previewed).TrimEnd());
        previewed = previewed with { Text = "hyperlink" };
        string[] cells = ContentSearchTable.Csv(ContentSearch.Find(formula.Store, formula.Store.SessionId, previewed), previewed)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",1,1,hyperlink,true,'=,HYPERLINK,\"(\"\"http://x\"\",1)\"", cells[1], StringComparison.Ordinal);
        previewed = previewed with { Text = "=hyperlink" };
        Assert.Contains(",'=hyperlink,true,,'=HYPERLINK,", ContentSearchTable.Csv(
            ContentSearch.Find(formula.Store, formula.Store.SessionId, previewed), previewed), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P2: a content search read on from where it ended lists what one search from the start would, never a record twice, every count summed once")]
    public void ASearchReadsOnFromWhereItEnded()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        Guid id = session.Store.SessionId;
        ContentSearchResult whole = ContentSearch.Find(session.Store, id, new() { Text = "example.com" });
        Assert.Null(whole.Continuation);

        // The same records in segments of three rows, so pages end and begin in segments after the first.
        (ObservationRowV1 Row, byte[] Bytes, ContentEncodingV1 Encoding)[] messages = Messages();
        using var segmented = new TemporarySession();
        Publish(segmented.Store, [.. messages.Select(message => message.Row)], rowsPerSegment: 3, content: (ContentHeader(64),
            [.. messages.Select(message => Content(message.Row, message.Bytes, 64, message.Encoding))]));
        segmented.Store.ReleaseSegmentReaders();
        Assert.True(new HashSet<string>(ContentSearch.Find(segmented.Store, id, new() { Text = "e" }).Hits.Select(hit => hit.Segment)).Count > 1);

        // Ended at its limit, a page reads on from the record past it; ended at a budget of bytes or of time, from the record
        // it did not read, reading at least that one, so even a budget no record fits in reads every record in the end.
        foreach ((SessionStore store, ContentSearchRequest request) in new (SessionStore, ContentSearchRequest)[]
        {
            (session.Store, new() { Text = "example.com", Limit = 1 }),
            (session.Store, new() { Text = "example.com", MaximumBytes = 30 }),
            (session.Store, new() { Text = "example.com", Budget = TimeSpan.Zero }),
            (segmented.Store, new() { Text = "example.com", Limit = 1 }),
            (segmented.Store, new() { Text = "example.com", MaximumBytes = 30 }),
        })
        {
            List<ContentSearchResult> pages = [ContentSearch.Find(store, id, request)];
            while (pages[^1].Continuation is { } next && pages.Count < 20)
            {
                Assert.Same(request, next.Request);
                pages.Add(ContentSearch.Continue(store, id, next));
            }

            ContentSearchResult joined = pages.Skip(1).Aggregate(pages[0], (earlier, later) => earlier.Then(later));
            Assert.Null(joined.Continuation);
            Assert.Equal(whole.Hits.Select(Key), joined.Hits.Select(Key));
            Assert.Equal((whole.RecordsRead, whole.BytesRead, whole.WithoutConsent, (string?)null, false),
                (joined.RecordsRead, joined.BytesRead, joined.WithoutConsent, joined.Stopped, joined.More));
            Assert.True(pages.Count > 2, $"{pages.Count} pages");
        }

        // The pages of a limited search say there are more until the last, which reads to the end.
        ContentSearchResult first = ContentSearch.Find(session.Store, id, new() { Text = "example.com", Limit = 2 });
        Assert.Equal((true, false, 6), (first.More, first.Partial, first.RecordsRead));
        ContentSearchResult rest = ContentSearch.Continue(session.Store, id, first.Continuation!);
        Assert.Equal([6UL], rest.Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.Equal((false, 2, (ContentSearchContinuation?)null), (rest.More, rest.RecordsRead, rest.Continuation));

        // Read on in another session, or in one that no longer holds every segment the search read, it is refused.
        using var other = new TemporarySession(Guid.NewGuid());
        Publish(other.Store, [.. Messages().Select(message => message.Row)]);
        other.Store.ReleaseSegmentReaders();
        Assert.Equal(SessionStore.AnotherSession, Assert.Throws<InvalidOperationException>(() =>
            ContentSearch.Continue(other.Store, other.Store.SessionId, first.Continuation!)).Message);
        var released = new ContentSearchContinuation(id, first.Continuation!.Request, ["segment-released-since"], 0, false,
            new Dictionary<(long Exchange, ushort Event, int Process, Mechanism Mechanism), ContentSearch.Seam>());
        Assert.Equal("The session no longer holds every segment this search had read, so it cannot read on from where it stopped; "
            + "search again from its start.", Assert.Throws<InvalidOperationException>(() =>
                ContentSearch.Continue(session.Store, id, released)).Message);

        static (ulong, int, int) Key(ContentSearchHit hit) => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt);
    }

    [Fact(DisplayName = "P2: content kept without consent to inspect it is never searched, and is counted; a session keeping none says so")]
    public void ContentKeptWithoutConsentIsNeverSearched()
    {
        using var session = Session(ContentInspectionV1.Disabled);
        ContentSearchResult found = ContentSearch.Find(session.Store, session.Store.SessionId, new() { Text = "example.com" });
        Assert.Equal((0, 0L, 8, true), (found.Hits.Count, found.BytesRead, found.WithoutConsent, found.Partial));

        using var none = new TemporarySession();
        Publish(none.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 5, 4_242, 1)]);
        ContentSearchResult nothing = ContentSearch.Find(none.Store, none.Store.SessionId, new() { Text = "example.com" });
        Assert.Equal(("The session keeps no content, so there was nothing to search.", false, 0),
            (nothing.NoneKept, nothing.Partial, nothing.RecordsRead));
    }

    [Fact(DisplayName = "R21: a record the capture kept no byte of, its content limit reached, is counted as not searched; an empty message kept whole is not")]
    public void ARecordKeptWithNoByteIsCounted()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = [Record(10, 1, 4_242), Record(20, 2, 4_242), Record(30, 3, 4_242)];
        (ContentFragmentV1 omitted, _) = Content(rows[2], new byte[50], 64);
        Publish(session.Store, rows, content: (ContentHeader(64),
        [
            Content(rows[0], Encoding.ASCII.GetBytes("example.com"), 64, ContentEncodingV1.Binary),
            Content(rows[1], Array.Empty<byte>(), 64),
            (omitted with { Disposition = ContentDispositionV1.OmittedBySessionLimit, Kept = 0 }, ReadOnlyMemory<byte>.Empty),
        ]));
        session.Store.ReleaseSegmentReaders();

        // The record found is read; the empty one holds nothing to find; the omitted one is counted, the answer whole, as a
        // cut record's is, since nothing of it was kept to search.
        ContentSearchResult found = ContentSearch.Find(session.Store, session.Store.SessionId, new() { Text = "example.com" });
        Assert.Equal((1, 1, 1, false), (found.Hits.Count, found.RecordsRead, found.Omitted, found.Partial));
        Assert.Equal(2, found.Then(found).Omitted);
    }

    [Fact(DisplayName = "P6: a content search kept to a process instance finds that instance's records alone, never those of another process given its PID again")]
    public void ASearchKeepsToOneHolderOfAReusedPid()
    {
        // PID 4242 held twice - created at 1 and exited at 30, then created again at 40 - each holder's records naming the
        // host, among them an HTTP buffer of the first, bound to it by the PID that raised it.
        ObservationRowV1[] records =
        [
            Record(10, 1, 4_242),
            Record(15, 2, 4_242) with
            {
                OwnerProcessId = null,
                HeaderProcessId = 4_242,
                Mechanism = Mechanism.Http,
                Layer = ObservationLayer.Application,
                EventId = 2001,
                ByteDomain = ByteDomain.ApplicationPayload,
            },
            Record(20, 3, 4_242),
            Record(50, 4, 4_242),
        ];
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 4_242, 100) with { ResourceName = @"C:\Tools\first.exe" },
            Lifecycle(30, ObservationKind.Exit, 4_242, 101),
            Lifecycle(40, ObservationKind.Create, 4_242, 102) with { ResourceName = @"C:\Tools\second.exe" },
            .. records,
        ], content: (ContentHeader(64), [.. records.Select(row => Content(row, Encoding.ASCII.GetBytes("Host: example.com"), 64,
            ContentEncodingV1.Binary))]));
        session.Store.ReleaseSegmentReaders();
        Guid id = session.Store.SessionId;
        ProcessNode[] holders = [.. SessionOverviewProjector.Project(session.Store).Nodes.Where(node => node.ProcessId == 4_242)
            .OrderBy(node => node.PidHolder)];
        Assert.Equal(["first.exe", "second.exe"], holders.Select(node => node.Name));

        // By its PID a search lists every record either holder raised; by an instance, that holder's alone, and only where
        // the evidence policy admits their binding. The first holder's records bind to it by their PID and time, a
        // correlation, which a policy of direct evidence alone does not admit; the second's, whose end was not seen, bind
        // to it as a candidate, which only a policy that admits candidates does.
        Assert.Equal([1UL, 2UL, 3UL, 4UL], Ordinals(new() { Text = "example.com", ProcessId = 4_242 }));
        Assert.Equal([1UL, 2UL, 3UL], Ordinals(new() { Text = "example.com", ProcessInstance = holders[0].Id }));
        Assert.Empty(Ordinals(new() { Text = "example.com", ProcessInstance = holders[0].Id, Policy = EvidencePolicy.DirectOnly }));
        Assert.Empty(Ordinals(new() { Text = "example.com", ProcessInstance = holders[1].Id }));
        Assert.Equal([4UL], Ordinals(new() { Text = "example.com", ProcessInstance = holders[1].Id, Policy = EvidencePolicy.IncludeCandidates }));

        // The process's own records a policy leaves out are counted, and no other process's are.
        Assert.Equal((0, 3, 1, 0, 3), (NotAdmitted(new() { Text = "example.com", ProcessInstance = holders[0].Id }),
            NotAdmitted(new() { Text = "example.com", ProcessInstance = holders[0].Id, Policy = EvidencePolicy.DirectOnly }),
            NotAdmitted(new() { Text = "example.com", ProcessInstance = holders[1].Id }),
            NotAdmitted(new() { Text = "example.com", ProcessInstance = holders[1].Id, Policy = EvidencePolicy.IncludeCandidates }),
            ContentSearch.Find(session.Store, id, new() { Text = "example.com", ProcessInstance = holders[0].Id }).RecordsRead));

        // An instance the generation does not hold, a PID and an instance at once, or a policy InterCat does not define,
        // is refused.
        Assert.Throws<InvalidOperationException>(() => ContentSearch.Find(session.Store, id,
            new() { Text = "example.com", ProcessInstance = new ProcessInstanceId(Guid.NewGuid()) }));
        Assert.Throws<ArgumentException>(() => ContentSearch.Find(session.Store, id,
            new() { Text = "example.com", ProcessId = 4_242, ProcessInstance = holders[0].Id }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentSearch.Find(session.Store, id,
            new() { Text = "example.com", ProcessInstance = holders[0].Id, Policy = (EvidencePolicy)99 }));

        ulong[] Ordinals(ContentSearchRequest request) =>
            [.. ContentSearch.Find(session.Store, id, request).Hits.Select(hit => hit.Observation.RawRecordOrdinal)];
        int NotAdmitted(ContentSearchRequest request) => ContentSearch.Find(session.Store, id, request).NotAdmitted;
    }

    [Fact(DisplayName = "P2: a content search finds a text two buffers of one HTTP part split, none counted twice across the part, and never across a gap, a cut or another part")]
    public void ASearchFindsATextTwoBuffersSplit()
    {
        // Process 4242's HTTP buffers, each part's in order: a request head naming the host across its seam; a body naming
        // it whole, then across each of its two seams; a body whose match ends where another would begin across the seam;
        // parts split across a buffer never recorded, a buffer the session's 26-byte record limit cut, a part's end, a
        // part's start, two parts and three buffers; a UTF-16 body naming it across its seam; bytes that read as the
        // host's name only across two encodings, or across a UTF-16 buffer that ends inside a code unit; and a body whose
        // later buffer holds "abab" where a match across the seam already took its first two bytes.
        (long Exchange, ushort Event, long Sequence, long Flags, byte[] Bytes, bool Wide)[] buffers =
        [
            (7, 2001, 0, 1, A("GET / HTTP/1.1\r\nHost: exam"), false), (7, 2001, 1, 2, A("ple.com\r\n\r\n"), false),
            (8, 2004, 0, 1, A("example.com and exa"), false), (8, 2004, 1, 0, A("mple.com, ex"), false), (8, 2004, 2, 2, A("ample.com"), false),
            (9, 2004, 0, 1, A("xxabab"), false), (9, 2004, 1, 2, A("abxx"), false),
            (10, 2004, 0, 1, A("Host: exam"), false), (10, 2004, 2, 2, A("ple.com"), false),
            (11, 2004, 0, 1, A("0123456789012345Host: examYYYY"), false), (11, 2004, 1, 2, A("ple.com"), false),
            (12, 2004, 0, 3, A("Host: exam"), false), (12, 2004, 1, 2, A("ple.com"), false),
            (13, 2004, 0, 1, A("Host: exam"), false), (13, 2004, 1, 1, A("ple.com"), false),
            (14, 2003, 0, 1, A("Host: exam"), false), (14, 2004, 1, 2, A("ple.com"), false),
            (15, 2004, 0, 1, A("Host: ex"), false), (15, 2004, 1, 0, A("am"), false), (15, 2004, 2, 2, A("ple.com"), false),
            (16, 2004, 0, 1, W("Host: exam"), true), (16, 2004, 1, 2, W("ple.com"), true),
            (17, 2004, 0, 1, W("\u7865\u6D61"), true), (17, 2004, 1, 2, A("ple.com"), false),
            (18, 2004, 0, 1, [.. A("AAAAAAAAAAAAA"), .. W("exam")], true), (18, 2004, 1, 2, W("ple.com"), true),
            (19, 2004, 0, 1, A("xxab"), false), (19, 2004, 1, 2, A("abab"), false),
        ];
        ObservationRowV1[] rows = [.. buffers.Select((buffer, index) => Http(10 + index, buffer.Event, (ulong)index + 1))];
        using var session = new TemporarySession();
        Publish(session.Store, rows,
            fields:
            [
                .. buffers.SelectMany((buffer, index) => new[]
                {
                    Field(rows[index], SourceField.HttpExchangeId, buffer.Exchange),
                    Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                    Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
                }),
            ],
            content: (ContentHeader(26), [.. buffers.Select((buffer, index) => Content(rows[index], buffer.Bytes, 26,
                buffer.Wide ? ContentEncodingV1.Utf16LittleEndian : ContentEncodingV1.Binary))]));
        session.Store.ReleaseSegmentReaders();
        Guid id = session.Store.SessionId;

        // A match the seam splits is the later buffer's, begun before its first byte; one that would overlap a match the
        // buffer before already holds is not counted again; and nothing is found across anything not kept between them.
        ContentSearchResult found = ContentSearch.Find(session.Store, id, new() { Text = "example.com" });
        Assert.Equal([(2UL, 1, -4), (3UL, 1, 0), (4UL, 1, -3), (5UL, 1, -2), (22UL, 1, -8)],
            found.Hits.Select(hit => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt)));
        Assert.Equal((28, 293L), (found.RecordsRead, found.BytesRead));
        Assert.Equal(found.Hits.Select(hit => hit.Observation.RawRecordOrdinal),
            ContentSearch.Find(session.Store, id, new() { Text = "EXAMPLE.com", IgnoreCase = true }).Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.Equal([(6UL, 1, 2), (28UL, 1, -2)], ContentSearch.Find(session.Store, id, new() { Text = "abab" }).Hits
            .Select(hit => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt)));

        // Read on page by page, one record found or one buffer read at a time, the search finds the same, the ends of the
        // parts it had open kept from page to page.
        foreach (ContentSearchRequest paged in new ContentSearchRequest[]
        {
            new() { Text = "example.com", Limit = 1 }, new() { Text = "example.com", MaximumBytes = 1 },
            new() { Text = "abab", Limit = 1 }, new() { Text = "abab", MaximumBytes = 1 },
        })
        {
            ContentSearchResult page = ContentSearch.Find(session.Store, id, paged);
            List<ContentSearchHit> listed = [.. page.Hits];
            for (int pages = 1; page.Continuation is { } next && pages < 100; pages++)
            {
                page = ContentSearch.Continue(session.Store, id, next);
                listed.AddRange(page.Hits);
            }

            Assert.Null(page.Continuation);

            Assert.Equal(ContentSearch.Find(session.Store, id, paged with { Limit = 50, MaximumBytes = ContentSearch.DefaultMaximumBytes })
                .Hits.Select(hit => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt)),
                listed.Select(hit => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt)));
        }

        // Asked for, the bytes around a match across a seam are shown from both buffers.
        ContentSearchHit[] previewed = [.. ContentSearch.Find(session.Store, id, new() { Text = "example.com", Previews = true }).Hits];
        Assert.Equal(new ContentPreview("HTTP/1.1\\x0D\\x0AHost: ", "example.com", "\\x0D\\x0A\\x0D\\x0A"), previewed[0].Preview);
        Assert.Equal(new ContentPreview("example.com and ", "example.com", ", ex"), previewed[2].Preview);
        Assert.Equal(new ContentPreview("Host: ", "example.com", string.Empty), previewed[4].Preview);
        Assert.Equal(("byte 0", "4 bytes before it, in the buffer before it in its part"),
            (ContentSearch.FirstAtText(previewed[1], CultureInfo.InvariantCulture), ContentSearch.FirstAtText(previewed[0], CultureInfo.InvariantCulture)));

        static byte[] A(string text) => Encoding.ASCII.GetBytes(text);
        static byte[] W(string text) => Encoding.Unicode.GetBytes(text);
    }

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

    /// <summary>
    /// Eight records' messages, each kept up to 64 bytes under <paramref name="inspection"/>: process 4242's request head
    /// naming the host; its head split across two records; its body naming the host twice; a record naming nothing;
    /// process 5151's record, raised by the kernel, naming it capitalised; a UTF-16 record naming it; a cut record naming
    /// it only past what was kept; and a UTF-16 record whose bytes hold "ab" only across its code units.
    /// </summary>
    private static TemporarySession Session(ContentInspectionV1 inspection)
    {
        (ObservationRowV1 Row, byte[] Bytes, ContentEncodingV1 Encoding)[] messages = Messages();
        var session = new TemporarySession();
        Publish(session.Store, [.. messages.Select(message => message.Row)], content: (ContentHeader(64, inspection),
            [.. messages.Select(message => Content(message.Row, message.Bytes, 64, message.Encoding))]));
        session.Store.ReleaseSegmentReaders();
        return session;
    }

    private static (ObservationRowV1 Row, byte[] Bytes, ContentEncodingV1 Encoding)[] Messages() =>
    [
        (Record(10, 1, 4_242), Encoding.ASCII.GetBytes("GET /index.html HTTP/1.1\r\nHost: example.com\r\n\r\n"), ContentEncodingV1.Binary),
        (Record(20, 2, 4_242), Encoding.ASCII.GetBytes("Host: exam"), ContentEncodingV1.Binary),
        (Record(21, 3, 4_242), Encoding.ASCII.GetBytes("ple.com\r\nexample.com, example.com"), ContentEncodingV1.Binary),
        (Record(22, 4, 4_242), Encoding.ASCII.GetBytes("nothing \\ here"), ContentEncodingV1.Binary),
        (Record(30, 5, 5_151) with { HeaderProcessId = 4 }, Encoding.ASCII.GetBytes("Example.com"), ContentEncodingV1.Binary),
        (Record(40, 6, 4_242), Encoding.Unicode.GetBytes("wide\\ example.comé"), ContentEncodingV1.Utf16LittleEndian),
        (Record(50, 7, 4_242), Encoding.ASCII.GetBytes(new string('a', 70) + "example.com"), ContentEncodingV1.Binary),
        (Record(60, 8, 4_242), [0x41, 0x61, 0x00, 0x62, 0x00, 0x00], ContentEncodingV1.Utf16LittleEndian),
    ];

    private static ObservationRowV1 Record(long ticks, ulong ordinal, int process) =>
        Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 10, process, ordinal) with { Direction = Direction.Outbound };
}
