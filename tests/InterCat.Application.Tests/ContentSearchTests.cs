using System.Buffers.Text;
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
        var released = new ContentSearchContinuation(id, first.Continuation!.Request, ["segment-released-since"], [], 0, false,
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
        // part's start and two parts; one split across three buffers; a UTF-16 body naming it across its seam; bytes that read as the
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
        Assert.Equal([(2UL, 1, -4), (3UL, 1, 0), (4UL, 1, -3), (5UL, 1, -2), (20UL, 1, -4), (22UL, 1, -8)],
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
        Assert.Equal(("byte 0", "4 bytes before it in its part"),
            (ContentSearch.FirstAtText(previewed[1], CultureInfo.InvariantCulture), ContentSearch.FirstAtText(previewed[0], CultureInfo.InvariantCulture)));

        static byte[] A(string text) => Encoding.ASCII.GetBytes(text);
        static byte[] W(string text) => Encoding.Unicode.GetBytes(text);
    }

    [Fact(DisplayName = "P2: a content search finds a text however many buffers of one HTTP part split it, as one search of the part's bytes would, read on or not")]
    public void ASearchFindsATextManyBuffersSplit()
    {
        // Process 4242's HTTP parts, each a text cut at random places into buffers of one to seven bytes, or in UTF-16 of
        // one to four code units. "abcab" overlaps itself, so where each match begins hangs on every match before it in its
        // part. Beside them, the host's name split across four buffers; across three, the second flagged a part's first;
        // and across three, the second in another encoding.
        var random = new Random(800);
        (string Text, bool Wide)[] parts =
        [
            ("xxabcabcabxabcabcabcabxxxabcab abcabcab", false), ("abcababcababcabcabxabc abcab", false),
            ("ABCABcabcAbCab abcab, abcabcabcab", false), ("abcabcabcabcabcab, abcab", true), ("x abcabcab ABCAB abcab", true),
        ];
        var buffers = new List<(long Exchange, long Sequence, long Flags, byte[] Bytes, bool Wide)>();
        for (int part = 0; part < parts.Length; part++)
        {
            byte[] bytes = Encoded(parts[part].Text, parts[part].Wide);
            var cut = new List<byte[]>();
            for (int at = 0; at < bytes.Length; at += cut[^1].Length)
            {
                cut.Add(bytes[at..Math.Min(bytes.Length, at + (parts[part].Wide ? 2 * random.Next(1, 5) : random.Next(1, 8)))]);
            }

            buffers.AddRange(cut.Select((buffer, index) =>
                ((long)part + 1, (long)index, (index == 0 ? 1L : 0) | (index == cut.Count - 1 ? 2L : 0), buffer, parts[part].Wide)));
        }

        buffers.AddRange(
        [
            (9, 0, 1, Encoded("Host: ex", false), false), (9, 1, 0, Encoded("am", false), false), (9, 2, 0, Encoded("pl", false), false),
            (9, 3, 2, Encoded("e.com", false), false),
            (10, 0, 1, Encoded("Host: ex", false), false), (10, 1, 1, Encoded("am", false), false), (10, 2, 2, Encoded("ple.com", false), false),
            (11, 0, 1, Encoded("Host: ex", false), false), (11, 1, 0, Encoded("am", true), true), (11, 2, 2, Encoded("ple.com", false), false),
        ]);
        ObservationRowV1[] rows = [.. buffers.Select((buffer, index) => Http(10 + index, 2004, (ulong)index + 1))];
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
            content: (ContentHeader(64), [.. buffers.Select((buffer, index) => Content(rows[index], buffer.Bytes, 64,
                buffer.Wide ? ContentEncodingV1.Utf16LittleEndian : ContentEncodingV1.Binary))]));
        session.Store.ReleaseSegmentReaders();
        Guid id = session.Store.SessionId;

        // Each record holds the matches that end in it as one search of its part's bytes from the start finds them, none
        // overlapping another, its first begun where that search began it; read on a record at a time, or a match at a
        // time, the search finds the same, and read on from its token as from where it stopped: the ends of the parts it
        // had open read again from where they are kept, whole, the bytes before each match shown alike.
        foreach (ContentSearchRequest request in new ContentSearchRequest[] { new() { Text = "abcab" }, new() { Text = "ABCAB", IgnoreCase = true } })
        {
            var expected = new List<(ulong Ordinal, int Matches, int FirstAt)>();
            int offset = 0;
            foreach ((string text, bool wide) in parts)
            {
                byte[] bytes = Encoded(request.IgnoreCase ? Lower(text) : text, wide);
                byte[] pattern = Encoded(request.IgnoreCase ? Lower(request.Text) : request.Text, wide);
                var ends = new List<(int Start, int End)>();
                for (int at = 0; at + pattern.Length <= bytes.Length;)
                {
                    if ((!wide || at % 2 == 0) && bytes.AsSpan(at, pattern.Length).SequenceEqual(pattern))
                    {
                        ends.Add((at, at + pattern.Length));
                        at += pattern.Length;
                    }
                    else
                    {
                        at++;
                    }
                }

                int start = 0;
                for (int index = offset; index < buffers.Count && buffers[index].Exchange == buffers[offset].Exchange; index++)
                {
                    int length = buffers[index].Bytes.Length;
                    (int Start, int End)[] held = [.. ends.Where(match => match.End > start && match.End <= start + length)];
                    if (held.Length > 0) expected.Add(((ulong)index + 1, held.Length, held[0].Start - start));
                    start += length;
                }

                offset += buffers.Skip(offset).TakeWhile(buffer => buffer.Exchange == buffers[offset].Exchange).Count();
            }

            Assert.Contains(expected, hit => hit.FirstAt < 0 && -hit.FirstAt > buffers[(int)hit.Ordinal - 2].Bytes.Length);
            foreach (ContentSearchRequest asked in new[]
            {
                request, request with { MaximumBytes = 1 }, request with { Limit = 1 }, request with { MaximumBytes = 1, Previews = true },
            })
            {
                ContentSearchResult page = ContentSearch.Find(session.Store, id, asked);
                List<ContentSearchHit> listed = [.. page.Hits];
                for (int pages = 1; page.Continuation is { } next && pages < 500; pages++)
                {
                    ContentSearchResult onward = ContentSearch.Continue(session.Store, id, next);
                    ContentSearchContinuation read = ContentSearchToken.Read(session.Store, id, asked, ContentSearchToken.Write(next));
                    Assert.Equal(Ends(next), Ends(read));
                    page = ContentSearch.Continue(session.Store, id, read);
                    Assert.Equal(onward.Hits.Select(Listed), page.Hits.Select(Listed));
                    listed.AddRange(page.Hits);
                }

                Assert.Null(page.Continuation);
                Assert.Equal(expected, listed.Select(hit => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt)));
            }
        }

        // The host's name split across four buffers is the last one's, begun 6 bytes before it, and its preview shows them
        // all; across a part's start, or a buffer of another encoding, it is not found.
        ContentSearchHit found = Assert.Single(ContentSearch.Find(session.Store, id, new() { Text = "example.com", Previews = true }).Hits);
        Assert.Equal(((ulong)buffers.FindIndex(buffer => buffer is { Exchange: 9, Sequence: 3 }) + 1, 1, -6),
            (found.Observation.RawRecordOrdinal, found.Matches, found.FirstAt));
        Assert.Equal(new ContentPreview("Host: ", "example.com", string.Empty), found.Preview);
        Assert.Equal("6 bytes before it in its part", ContentSearch.FirstAtText(found, CultureInfo.InvariantCulture));

        static byte[] Encoded(string text, bool wide) => wide ? Encoding.Unicode.GetBytes(text) : Encoding.ASCII.GetBytes(text);
        static string Lower(string text) => new([.. text.Select(letter => letter is >= 'A' and <= 'Z' ? (char)(letter + 32) : letter)]);
        static (ulong, int, int, ContentPreview?) Listed(ContentSearchHit hit) =>
            (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt, hit.Preview);
    }

    [Fact(DisplayName = "P16: a search reads on from its token as from where it stopped, and the token holds no byte kept nor the text looked for")]
    public void ASearchReadsOnFromItsToken()
    {
        // Exchange 9's request names the host across three buffers, each record in a segment of its own; exchange 12's,
        // a part left open, names it whole in a buffer longer than the end a seam keeps.
        (long Exchange, long Sequence, long Flags, string Text)[] buffers =
            [(9, 0, 1, "Host: ex"), (12, 0, 1, "GET /search?q=example.com&page=2"), (9, 1, 0, "ampl"), (9, 2, 2, "e.com")];
        using var session = HttpSession(buffers);
        SessionStore store = session.Store;
        Guid id = store.SessionId;

        // A byte at a time, the search reads on from a token after each record, holding the ends of its open parts as it
        // held them, and finds the name the seams split as one search does, the bytes before it read again from the
        // segments that keep them.
        var request = new ContentSearchRequest { Text = "example.com", MaximumBytes = 1, Previews = true };
        ContentSearchResult page = ContentSearch.Find(store, id, request);
        List<ContentSearchHit> listed = [.. page.Hits];
        var tokens = new List<string>();
        while (page.Continuation is { } next && tokens.Count < 10)
        {
            tokens.Add(ContentSearchToken.Write(next));
            ContentSearchContinuation read = ContentSearchToken.Read(store, id, request, tokens[^1]);
            Assert.Equal(Ends(next), Ends(read));
            page = ContentSearch.Continue(store, id, read);
            listed.AddRange(page.Hits);
        }

        Assert.Equal([(2UL, 1, 14), (4UL, 1, -6)], listed.Select(hit => (hit.Observation.RawRecordOrdinal, hit.Matches, hit.FirstAt)));
        Assert.Equal(new ContentPreview("Host: ", "example.com", string.Empty), listed[1].Preview);
        Assert.Equal(4, tokens.Count);
        ContentSearchContinuation last = ContentSearchToken.Read(store, id, request, tokens[^1]);
        Assert.Equal("Host: exampl", Encoding.ASCII.GetString(last.Seams[(9, 2004, 4_242, Mechanism.Http)].Tail));
        Assert.Equal("search?q=example.com&page=2", Encoding.ASCII.GetString(last.Seams[(12, 2004, 4_242, Mechanism.Http)].Tail));

        // No token holds a byte that was kept, nor the text looked for.
        foreach (string token in tokens)
        {
            byte[] held = Base64Url.DecodeFromChars(token);
            foreach (string kept in new[] { "Host: ex", "GET /", "search", "page=2", "ampl", "e.com", "example.com" })
            {
                Assert.True(held.AsSpan().IndexOf(Encoding.ASCII.GetBytes(kept)) < 0, $"A token holds \"{kept}\".");
            }
        }

        // The search reads on within budgets, a limit and previews of its own, and an evidence policy binds no record
        // where no process instance is asked for; reading on from a token reads at least the record it stopped before.
        ContentSearchResult limited = ContentSearch.Continue(store, id, ContentSearchToken.Read(store, id, request with
        {
            Limit = 1, MaximumBytes = ContentSearch.DefaultMaximumBytes, Budget = TimeSpan.FromMinutes(1), Previews = false,
            Policy = EvidencePolicy.DirectOnly,
        }, tokens[1]));
        Assert.Equal([2UL], limited.Hits.Select(hit => hit.Observation.RawRecordOrdinal));
        Assert.True(limited.More);
        Assert.Null(limited.Hits[0].Preview);

        // A search asked otherwise is refused another's token, and a token of another session or no token at all is
        // refused, saying so.
        var instance = new ProcessInstanceId(Guid.NewGuid());
        foreach (ContentSearchRequest other in new[]
        {
            request with { Text = "example.org" }, request with { IgnoreCase = true }, request with { ProcessId = 4_242 },
            request with { ProcessInstance = instance },
        })
        {
            Assert.Equal(ContentSearchToken.AnotherSearch,
                Assert.Throws<ArgumentException>(() => ContentSearchToken.Read(store, id, other, tokens[^1])).Message);
        }

        var bound = new ContentSearchContinuation(id, request with { ProcessInstance = instance }, last.Segments, last.SegmentsDigest,
            0, false, new Dictionary<(long Exchange, ushort Event, int Process, Mechanism Mechanism), ContentSearch.Seam>());
        Assert.Equal(EvidencePolicy.IncludeCorrelated, ContentSearchToken.Read(store, id, bound.Request, ContentSearchToken.Write(bound))
            .Request.Policy);
        foreach (ContentSearchRequest other in new[]
        {
            bound.Request with { Policy = EvidencePolicy.DirectOnly }, bound.Request with { ProcessInstance = new(Guid.NewGuid()) },
        })
        {
            Assert.Equal(ContentSearchToken.AnotherSearch,
                Assert.Throws<ArgumentException>(() => ContentSearchToken.Read(store, id, other, ContentSearchToken.Write(bound))).Message);
        }

        // A copy of the session reads on from it, holding the same segments; another session refuses it.
        using var copy = HttpSession(buffers);
        Assert.Equal(Ends(last), Ends(ContentSearchToken.Read(copy.Store, id, request, tokens[^1])));
        using var another = HttpSession(buffers, Guid.NewGuid());
        Assert.Equal(ContentSearchToken.AnotherSessions, Assert.Throws<ArgumentException>(() =>
            ContentSearchToken.Read(another.Store, another.Store.SessionId, request, tokens[^1])).Message);
        Assert.Equal(SessionStore.AnotherSession, Assert.Throws<InvalidOperationException>(() =>
            ContentSearchToken.Read(store, Guid.NewGuid(), request, tokens[^1])).Message);
        foreach (string malformed in new[]
        {
            string.Empty, "*", Base64Url.EncodeToString([1, 0, 0, 0]), Token(version: 2), Token(segments: 0, exchanges: []), Token(row: -1),
            Token(counted: 2), Token(count: -1, exchanges: []), Token(eventId: -1), Token(eventId: ushort.MaxValue + 1),
            Token(sources: 0, free: 0), Token(exchanges: [9, 9]), Token(segment: -1), Token(segment: 1), Token(sourceRow: -1),
            Token(from: -1), Token(length: 0, free: 0), Token(free: -1), Token(free: 2), Token(trailing: true), Token(cut: true),
        })
        {
            Assert.Equal(ContentSearchToken.Malformed,
                Assert.Throws<ArgumentException>(() => ContentSearchToken.Read(store, id, request, malformed)).Message);
        }

        // As a search writes it, with every field at its edge, it is a token - of another session.
        Assert.Equal(ContentSearchToken.AnotherSessions,
            Assert.Throws<ArgumentException>(() => ContentSearchToken.Read(store, id, request, Token())).Message);

        // Bytes at a part's end the session keeps no more, or never kept to inspect, are refused, as are segments it no
        // longer holds as the search read them.
        foreach (ContentSearch.SeamSource gone in new ContentSearch.SeamSource[] { new(0, 1, 0, 1), new(0, 0, 8, 1), new(0, 0, 0, 9) })
        {
            Assert.Equal(ContentSearchToken.BytesGone, Assert.Throws<InvalidOperationException>(() =>
                ContentSearchToken.Read(store, id, request, ContentSearchToken.Write(Crafted(last, store, gone)))).Message);
        }

        using var hidden = Session(ContentInspectionV1.Disabled);
        using var none = new TemporarySession();
        Publish(none.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 5, 4_242, 1)]);
        none.Store.ReleaseSegmentReaders();
        foreach (SessionStore keeping in new[] { hidden.Store, none.Store })
        {
            Assert.Equal(ContentSearchToken.BytesGone, Assert.Throws<InvalidOperationException>(() => ContentSearchToken.Read(keeping,
                keeping.SessionId, request, ContentSearchToken.Write(Crafted(last, keeping, new(0, 0, 0, 1))))).Message);
        }

        foreach (ContentSearchContinuation moved in new[]
        {
            new ContentSearchContinuation(id, request, [.. last.Segments, "seg-released"], last.SegmentsDigest, 0, false, last.Seams),
            new ContentSearchContinuation(id, request, last.Segments, new byte[32], 0, false, last.Seams),
        })
        {
            Assert.Equal(ContentSearch.SegmentsChanged, Assert.Throws<InvalidOperationException>(() =>
                ContentSearchToken.Read(store, id, request, ContentSearchToken.Write(moved))).Message);
        }

        // A generation published since that keeps the segments the search read in their place and adds more, as a capture
        // being followed does, reads on from the token.
        Publish(store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 5, 4_242, 100)]);
        store.ReleaseSegmentReaders();
        Assert.Equal([(4UL, -6)], ContentSearch.Continue(store, id, ContentSearchToken.Read(store, id, request, tokens[^1])).Hits
            .Select(hit => (hit.Observation.RawRecordOrdinal, hit.FirstAt)));

        // A continuation whose one open part ends in the bytes named, of the session given, as a search of it would hold.
        static ContentSearchContinuation Crafted(ContentSearchContinuation from, SessionStore store, ContentSearch.SeamSource source)
        {
            using EvidenceLease lease = store.AcquireLease();
            string[] names = [.. SessionSegments.Names(lease.Manifest).Take(1)];
            return new(lease.Manifest.SessionId, from.Request, names, ContentSearchToken.SegmentsDigest(lease.Manifest, names), 0,
                false, new Dictionary<(long Exchange, ushort Event, int Process, Mechanism Mechanism), ContentSearch.Seam>
                {
                    [(9, 2004, 4_242, Mechanism.Http)] = from.Seams[(9, 2004, 4_242, Mechanism.Http)] with { Free = 0, Sources = [source] },
                });
        }
    }

    /// <summary>
    /// A token's bytes laid out as a search writes them - a session of its own, digests of nothing, and one open part of
    /// one source - with each field as given, so each one alone may be made what no search writes.
    /// </summary>
    private static string Token(byte version = 1, int segments = 1, int row = 0, byte counted = 1, int? count = null,
        int[]? exchanges = null, int eventId = ushort.MaxValue, int sources = 1, int segment = 0, int sourceRow = 0, int from = 0,
        int length = 1, int free = 1, bool trailing = false, bool cut = false)
    {
        exchanges ??= [9];
        using var written = new MemoryStream();
        using (var writer = new BinaryWriter(written, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(version);
            writer.Write(Guid.NewGuid().ToByteArray());
            writer.Write7BitEncodedInt(segments);
            writer.Write(new byte[16]);
            writer.Write7BitEncodedInt(row);
            writer.Write(counted);
            writer.Write7BitEncodedInt(count ?? exchanges.Length);
            foreach (int exchange in exchanges)
            {
                writer.Write7BitEncodedInt64(exchange);
                writer.Write7BitEncodedInt(eventId);
                writer.Write7BitEncodedInt(4_242);
                writer.Write7BitEncodedInt((int)Mechanism.Http);
                writer.Write7BitEncodedInt64(0);
                writer.Write7BitEncodedInt(free);
                writer.Write7BitEncodedInt(sources);
                for (int index = 0; index < sources; index++)
                {
                    writer.Write7BitEncodedInt(segment);
                    writer.Write7BitEncodedInt(sourceRow);
                    writer.Write7BitEncodedInt(from);
                    writer.Write7BitEncodedInt(length);
                }
            }

            if (trailing) writer.Write((byte)0);
        }

        byte[] bytes = written.ToArray();
        return Base64Url.EncodeToString(cut ? bytes[..^1] : bytes);
    }

    /// <summary>The ends of the parts a continuation holds, and where it reads on, as text that compares whole.</summary>
    private static string Ends(ContentSearchContinuation continuation) =>
        string.Join("; ", continuation.Seams.OrderBy(seam => seam.Key).Select(seam => string.Create(CultureInfo.InvariantCulture,
            $"{seam.Key} {seam.Value.Sequence} {seam.Value.Whole} {seam.Value.Wide} {seam.Value.Kept} {seam.Value.Free} "
            + $"{Convert.ToHexString(seam.Value.Tail)} {string.Join(",", seam.Value.Sources)}")))
        + string.Create(CultureInfo.InvariantCulture,
            $" | {continuation.Request} {string.Join(",", continuation.Segments)} {continuation.Row} {continuation.Counted}");

    /// <summary>Process 4242's HTTP request buffers, each record in a segment of its own, its bytes kept with consent to inspect them.</summary>
    private static TemporarySession HttpSession((long Exchange, long Sequence, long Flags, string Text)[] buffers, Guid? id = null)
    {
        ObservationRowV1[] rows = [.. buffers.Select((_, index) => Http(10 + index, 2004, (ulong)index + 1))];
        var session = new TemporarySession(id);
        Publish(session.Store, rows, rowsPerSegment: 1,
            fields:
            [
                .. buffers.SelectMany((buffer, index) => new[]
                {
                    Field(rows[index], SourceField.HttpExchangeId, buffer.Exchange),
                    Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                    Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
                }),
            ],
            content: (ContentHeader(64), [.. buffers.Select((buffer, index) => Content(rows[index], Encoding.ASCII.GetBytes(buffer.Text),
                64, ContentEncodingV1.Binary))]));
        session.Store.ReleaseSegmentReaders();
        return session;
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
