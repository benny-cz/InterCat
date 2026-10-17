using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Desktop.Tests.HostEdgeRowsTests;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A live session's next publication reads its bytes again, and until it has, states what the last one read, marked
/// updating (§6.4), as a byte ranking's measures stand in: the relationship table's rows - relationships and edges to other
/// hosts alike - and its caption, and the inspector's card, aloud as on screen. None blinks to "reading bytes…" on each
/// publication, and none states the last one's once its own could not be read.
/// </summary>
public sealed class LiveBytesTests
{
    [Fact(DisplayName = "§6.4/R15: a live publication's table and card state the last one's bytes, marked updating aloud as on screen, until its own are read; one whose own cannot be read says so")]
    public void ALivePublicationKeepsTheTablesBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel first = Open(session);
        await first.LayoutReady;
        first.ShowTables = true;
        first.SelectGraphNode(first.GraphDisplay.NodeOf(Process(first, 100).Id)!.Key);
        await first.SelectionBytesReady;
        await first.HostEdgeTrafficReady;
        Assert.Equal("Whole machine · 1 relationship · 3 edges to other hosts", first.RelationshipTableScope);
        Assert.EndsWith(" · 100 B sent · no receive recorded", first.EvidenceSummary, StringComparison.Ordinal);

        // The next publication holds more of what client.exe and browser.exe sent. Until it has read its own, its table and
        // card state what the last one read, and say they are updating, aloud as on screen.
        Publish(session.Store, More());
        using WorkspaceViewModel second = Open(session);
        await second.LayoutReady;
        Assert.Null(second.RestoreNavigation(first.CaptureNavigation()));
        second.AdoptScope(first.CarryScope());
        RelationshipRow Browser(WorkspaceViewModel workspace) =>
            workspace.TableRows.Single(row => row.Name == "browser.exe · PID 300 → 142.250.1.1:443");
        Assert.Equal(("100 B sent across", true), (second.TableRows[0].KnownBytes, second.TableRows[0].BytesUpdating));
        Assert.Contains(", 100 B sent across, updating, evidence ", second.TableRows[0].AccessibleName, StringComparison.Ordinal);
        RelationshipRow browser = Browser(second);
        Assert.Equal(("4", "200 B sent across", true), (browser.Observations, browser.KnownBytes, browser.BytesUpdating));
        Assert.EndsWith(" At this machine's end: 200 B sent · no receive recorded · updating.", browser.Explanation,
            StringComparison.Ordinal);
        Assert.Contains(", 4 observations, 200 B sent across, updating, evidence correlated. ", browser.AccessibleName,
            StringComparison.Ordinal);
        Assert.Equal("Whole machine · 1 relationship · 3 edges to other hosts · bytes updating", second.RelationshipTableScope);
        Assert.EndsWith(" · 100 B sent · no receive recorded · updating", second.EvidenceSummary, StringComparison.Ordinal);
        string drawn = second.GraphDisplay.Edges.Single(edge => !edge.ToHosts).Key;
        Assert.Contains("Bytes: 100 B sent across, each transfer counted once at its sender · updating",
            second.DescribeGraphHover(drawn)!.Lines);
        (WorkspaceNavigationMemento Navigation, ScopeCarry Scope) unread = (second.CaptureNavigation(), second.CarryScope());

        // A row chosen in the table says so in the card.
        second.SelectedTableRow = second.TableRows[0];
        Assert.EndsWith(" · 100 B sent across · updating", second.EvidenceSummary, StringComparison.Ordinal);
        second.SelectedTableRow = browser;
        Assert.Equal("4 records of its connections · 200 B sent across · updating", second.EvidenceSummary);

        // Its own replace them once read; the caption is said again whenever the rows are.
        var said = new List<string?>();
        second.PropertyChanged += (_, changed) => said.Add(changed.PropertyName);
        await second.SelectionBytesReady;
        await second.HostEdgeTrafficReady;
        Assert.Equal(("200 B sent across", false), (second.TableRows[0].KnownBytes, second.TableRows[0].BytesUpdating));
        browser = Browser(second);
        Assert.Equal(("300 B sent across", false), (browser.KnownBytes, browser.BytesUpdating));
        Assert.EndsWith(" At this machine's end: 300 B sent · no receive recorded.", browser.Explanation, StringComparison.Ordinal);
        Assert.Equal("Whole machine · 1 relationship · 3 edges to other hosts", second.RelationshipTableScope);
        Assert.Contains(nameof(WorkspaceViewModel.RelationshipTableScope),
            said.Skip(said.LastIndexOf(nameof(WorkspaceViewModel.TableRows))));
        Assert.Contains("Bytes: 200 B sent across, each transfer counted once at its sender", second.DescribeGraphHover(drawn)!.Lines);
        Assert.Equal("4 records of its connections · 300 B sent across", second.EvidenceSummary);
        Assert.Equal(browser.Key, second.SelectedTableRow?.Key);

        // A publication following one that had not read its own yet states what the one before it read.
        using (WorkspaceViewModel next = Open(session))
        {
            await next.LayoutReady;
            Assert.Null(next.RestoreNavigation(unread.Navigation));
            next.AdoptScope(unread.Scope);
            Assert.Equal(("100 B sent across", true), (next.TableRows[0].KnownBytes, next.TableRows[0].BytesUpdating));
            Assert.Equal(("200 B sent across", true), (Browser(next).KnownBytes, Browser(next).BytesUpdating));
        }

        // A table shown once the rest of its bytes are in states its edges' traffic as the last publication read it, and
        // says so again when its own arrives.
        using (WorkspaceViewModel hidden = Open(session))
        {
            await hidden.LayoutReady;
            Assert.Null(hidden.RestoreNavigation(unread.Navigation with { ShowTables = false }));
            hidden.AdoptScope(unread.Scope);
            await hidden.SelectionBytesReady;
            hidden.ShowTables = true;
            Assert.Equal(("200 B sent across", false), (hidden.TableRows[0].KnownBytes, hidden.TableRows[0].BytesUpdating));
            Assert.Equal(("200 B sent across", true), (Browser(hidden).KnownBytes, Browser(hidden).BytesUpdating));
            Assert.EndsWith(" · bytes updating", hidden.RelationshipTableScope, StringComparison.Ordinal);
            bool saidCurrent = false;
            hidden.PropertyChanged += (_, changed) => saidCurrent |=
                changed.PropertyName == nameof(WorkspaceViewModel.RelationshipTableScope)
                && !hidden.RelationshipTableScope.Contains("updating", StringComparison.Ordinal);
            await hidden.HostEdgeTrafficReady;
            Assert.Equal(("300 B sent across", false), (Browser(hidden).KnownBytes, Browser(hidden).BytesUpdating));
            Assert.Equal("Whole machine · 1 relationship · 3 edges to other hosts", hidden.RelationshipTableScope);
            Assert.True(saidCurrent);
        }

        // A publication whose own cannot be read says so, never what the last one read.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var third = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(Path.Combine(session.Path, "gone"), overview.SessionId, overview.Generation));
        await third.LayoutReady;
        Assert.Null(third.RestoreNavigation(second.CaptureNavigation()));
        third.AdoptScope(second.CarryScope());
        await third.SelectionBytesReady;
        await third.HostEdgeTrafficReady;
        Assert.All(third.TableRows, row => Assert.Equal(("bytes could not be read", false), (row.KnownBytes, row.BytesUpdating)));
        Assert.Equal("Whole machine · 1 relationship · 3 edges to other hosts", third.RelationshipTableScope);
        Assert.Equal("4 records of its connections · bytes could not be read", third.EvidenceSummary);

        // What it could not read it carries to the next publication neither.
        ScopeCarry onward = third.CarryScope();
        Assert.Equal((null, null), (onward.WholeDescribedBytes, onward.HostReaches));
    });

    [Fact(DisplayName = "§6.4: under a brush, a live publication's table states the last one's bytes for the interval, marked updating, until its own are read")]
    public void ALivePublicationKeepsTheIntervalsBytes() => SingleThreadedContext.Run(async () =>
    {
        // Timed as the clock reads them, so a brush reads their bytes; client.exe sends again after the interval brushed.
        static ObservationRowV1[] Timed(IEnumerable<ObservationRowV1> rows) =>
            [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];
        using var session = new TemporarySession();
        Publish(session.Store, Timed([.. Rows(), Tcp(125, ObservationKind.Send, 100, 125, "127.0.0.1:50000", "127.0.0.1:9000")]));
        using WorkspaceViewModel first = Open(session);
        await first.LayoutReady;
        first.ShowTables = true;
        await first.SelectionBytesReady;
        Assert.Equal("200 B sent across", first.TableRows[0].KnownBytes);
        first.SelectInterval(new TimeRange(100, 102));
        await first.IntervalReady;
        await first.SelectionBytesReady;
        Assert.Equal("100 B sent across", first.TableRows[0].KnownBytes);

        // The next publication states the interval's bytes as the last one read them, never the whole session's.
        Publish(session.Store, Timed(More()));
        using WorkspaceViewModel second = Open(session);
        await second.LayoutReady;
        Assert.Null(second.RestoreNavigation(first.CaptureNavigation()));
        second.AdoptScope(first.CarryScope());
        Assert.Equal(("100 B sent across", true), (second.TableRows[0].KnownBytes, second.TableRows[0].BytesUpdating));
        Assert.EndsWith(" · bytes updating", second.RelationshipTableScope, StringComparison.Ordinal);
        await second.IntervalReady;
        await second.SelectionBytesReady;
        await second.HostEdgeTrafficReady;
        Assert.Equal(("100 B sent across", false), (second.TableRows[0].KnownBytes, second.TableRows[0].BytesUpdating));
        Assert.DoesNotContain("updating", second.RelationshipTableScope, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.4: a live publication keeps a channel chosen among a process's rows described, its card stating its bytes as the last one read them, marked updating, until its own are read")]
    public void ALivePublicationKeepsAChosenChannelsBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel first = Open(session);
        await first.LayoutReady;
        first.SelectedRung = first.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(first.Descend());
        first.SelectedRung = first.RungRows[0];
        Assert.True(first.Descend());
        first.SelectedRung = first.RungRows.Single(row => row.Label == "→ server.exe · PID 200");
        await first.SelectionBytesReady;
        Assert.Equal(("Selected channel", "3 observed records at its two ends · 100 B sent across"),
            (first.EvidenceHeading, first.EvidenceSummary));
        await first.HighlightReady;
        Assert.Equal("127.0.0.1:9000 ↔ 127.0.0.1:50000", first.TimelineHighlightName);

        // The next publication describes the channel still, and highlights its records, not the process its rung was reached
        // with.
        Publish(session.Store, More());
        using WorkspaceViewModel second = Open(session);
        await second.LayoutReady;
        Assert.Null(second.RestoreNavigation(first.CaptureNavigation()));
        second.AdoptScope(first.CarryScope());
        Assert.Equal(first.SelectedRung!.Key, second.DescribedRow?.Key);
        Assert.Equal(("Selected channel", "5 observed records at its two ends · 100 B sent across · updating"),
            (second.EvidenceHeading, second.EvidenceSummary));
        await second.SelectionBytesReady;
        Assert.Equal("5 observed records at its two ends · 200 B sent across", second.EvidenceSummary);
        await second.HighlightReady;
        Assert.Equal("127.0.0.1:9000 ↔ 127.0.0.1:50000", second.TimelineHighlightName);

        // Put back without the bytes the last one read, it describes and highlights the channel all the same.
        using (WorkspaceViewModel restored = Open(session))
        {
            await restored.LayoutReady;
            Assert.Null(restored.RestoreNavigation(second.CaptureNavigation()));
            Assert.Equal("Selected channel", restored.EvidenceHeading);
            await restored.HighlightReady;
            Assert.Equal("127.0.0.1:9000 ↔ 127.0.0.1:50000", restored.TimelineHighlightName);
        }

        // Its process chosen again there is the selection again, in the next publication too.
        second.SelectProcess(Process(second, 100).Id);
        Assert.Null(second.DescribedRow);
        using WorkspaceViewModel third = Open(session);
        await third.LayoutReady;
        Assert.Null(third.RestoreNavigation(second.CaptureNavigation()));
        Assert.Null(third.DescribedRow);
        Assert.Equal("Selected process", third.EvidenceHeading);
    });

    /// <summary>
    /// What a later publication of <see cref="HostEdgeRowsTests.Rows"/> adds: client.exe sending server.exe 100 B more, and
    /// browser.exe its host.
    /// </summary>
    private static ObservationRowV1[] More() =>
    [
        Tcp(150, ObservationKind.Send, 100, 150, "127.0.0.1:50000", "127.0.0.1:9000"),
        Tcp(151, ObservationKind.Receive, 200, 151, "127.0.0.1:9000", "127.0.0.1:50000"),
        Tcp(152, ObservationKind.Send, 300, 152, "192.168.1.10:52000", "142.250.1.1:443"),
    ];
}
