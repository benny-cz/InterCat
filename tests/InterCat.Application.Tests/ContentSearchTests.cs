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
