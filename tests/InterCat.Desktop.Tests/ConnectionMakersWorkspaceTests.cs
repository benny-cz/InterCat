using Avalonia;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Which process made a relationship's connections, on the graph (§6.8, §7.4): a relationship's card says who connected to
/// whom where the capture saw its connections open - a connect at one end, an accept at the other - counted where they
/// went both ways or some opened unseen, and display order only where it saw none open.
/// </summary>
public sealed class ConnectionMakersWorkspaceTests
{
    private const string Client = "client.exe · PID 100";
    private const string Server = "server.exe · PID 200";

    [Fact(DisplayName = "§6.8: a relationship's card says which process made its connections, counted where they went both ways or some opened unseen, and display order only where the capture saw none open")]
    public void ARelationshipsCardSaysWhoConnected() => SingleThreadedContext.Run(async () =>
    {
        // Both ways, and one the capture saw open neither way.
        Assert.Equal($"Direction: {Client} connected to {Server} in 2 connections, {Server} connected to {Client} in 1 connection; "
            + "the capture saw 1 connection open neither way", await DirectionAsync(Mixed(), clientFirst: true));
        Assert.Equal($"Direction: {Server} connected to {Client} in 1 connection, {Client} connected to {Server} in 2 connections; "
            + "the capture saw 1 connection open neither way", await DirectionAsync(Mixed(), clientFirst: false));

        // One way only, whichever end the edge is drawn from.
        Assert.Equal($"Direction: {Client} connected to {Server}, in all 2 of its connections",
            await DirectionAsync(OneWay(), clientFirst: true));
        Assert.Equal($"Direction: {Client} connected to {Server}, in all 2 of its connections",
            await DirectionAsync(OneWay(), clientFirst: false));
        Assert.Equal($"Direction: {Client} connected to {Server}", await DirectionAsync(OneWay()[..^3], clientFirst: true));

        // None seen opening: display order only.
        Assert.Equal("Direction: display order only, not who initiated or sent", await DirectionAsync(Unseen(), clientFirst: true));
    });

    [Fact(DisplayName = "§6.3: an edge's card is titled as its row in the relationship table names it, the end that made its connections first, whichever end the graph draws first")]
    public void AnEdgesCardIsTitledByWhoConnected() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. OneWay(), .. Updater(), .. Workers()]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        string Title(GraphDisplayEdge edge) => workspace.DescribeGraphHover(edge.Key)!.Title;

        // The client connected to the server: named first, as the inspector names the relationship chosen.
        GraphDisplayEdge pair = Assert.Single(workspace.GraphDisplay.Edges, edge => !edge.ToHosts);
        Assert.Equal($"{Client} → {Server}", Title(pair));
        workspace.SelectedRelationship = workspace.Relationships.Single(row => row.Key == Assert.Single(pair.Relationships));
        Assert.Equal(workspace.SelectionTitle, Title(pair));

        // The updater connected out to another host, and the workers' processes accepted connections from them.
        Assert.Equal(["Other hosts → worker.exe", "updater.exe · PID 300 → Other hosts"],
            workspace.GraphDisplay.Edges.Where(edge => edge.ToHosts).Select(Title).Order(StringComparer.Ordinal));
    });

    [Fact(DisplayName = "§6.8: the inspector names a chosen relationship with the mark of who connected between its ends, says who beneath, and how it was derived in its explanation")]
    public void TheInspectorSaysWhoConnectedForAChosenRelationship() => SingleThreadedContext.Run(async () =>
    {
        var highlighted = new List<string?>();
        // Each says where its connections arrived beside what it ran over: the server's 8080, and the client's 9000 too where
        // the server connected to it; none where none was seen opening.
        foreach ((ObservationRowV1[] rows, string? said, string over) in new (ObservationRowV1[], string?, string)[]
        {
            (OneWay(), $"{Client} connected to {Server}", "TCP at 8080"),
            (OneWay()[..^3], $"{Client} connected to {Server}", "TCP at 8080"),
            (Mixed(), "each connected to the other", "TCP at 8080 and 9000"),
            (Unseen(), null, "TCP"),
        })
        {
            using var session = new TemporarySession();
            Publish(session.Store, rows);
            SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
            using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
                new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
            await workspace.LayoutReady;
            RelationshipRow chosen = Assert.Single(workspace.Relationships);
            workspace.SelectedRelationship = chosen;

            // The client alone connected: named first, an arrow from it, whichever end the graph draws first; each way, both;
            // none seen, none.
            string mark = said switch
            {
                null => "↔",
                "each connected to the other" => "⇄",
                _ => "→",
            };
            if (mark == "→")
            {
                Assert.Equal(Client, chosen.Source);
            }

            Assert.Equal($"{chosen.Source} {mark} {chosen.Target}", workspace.SelectionTitle);
            Assert.Equal(over, chosen.Mechanism);
            Assert.StartsWith(over + " · ", workspace.DescribeGraphHover(Assert.Single(workspace.GraphDisplay.Edges).Key)!.Lines[0],
                StringComparison.Ordinal);
            Assert.Equal(string.Join(" · ", new[] { over, said, chosen.Evidence + " evidence" }.OfType<string>()),
                workspace.SelectionSubtitle);
            Assert.Equal(("How it was derived", chosen.Explanation), (workspace.ExplanationHeading, workspace.Explanation));
            Assert.StartsWith("Correlated evidence, derived by transport-endpoint-relation", workspace.Explanation, StringComparison.Ordinal);

            // The timeline names the records it highlights by the same name, where they are one channel's.
            if (workspace.TimelineHighlightName is { } name)
            {
                Assert.Equal(workspace.SelectionTitle, name);
            }

            highlighted.Add(workspace.TimelineHighlightName);

            // Let go, the inspector explains nothing of it.
            workspace.SelectedRelationship = null;
            Assert.False(workspace.HasExplanation);
        }

        // The one connection the client made is one channel, highlighted under the name with its arrow.
        Assert.Equal("client.exe · PID 100 → server.exe · PID 200", highlighted[1]);
    });

    [Fact(DisplayName = "§6.3: a process's card counts the connections it made and accepted as the capture saw them open, and the rest of the machine's leaves theirs to their own rungs")]
    public void AProcesssCardCountsTheConnectionsItMadeAndAccepted() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Mixed(), .. Updater()]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        string Key(int pid) => workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(process => process.ProcessId == pid).Id)!.Key;
        IReadOnlyList<string> Card(int pid) => workspace.DescribeGraphHover(Key(pid))!.Lines;

        // The client made two of its connections to the server and accepted the one the server made, at its own port 9000, and
        // the capture saw one open neither way, which counts for neither end. The updater connected out to another host, to
        // a port of this machine no process of the capture held, and to itself, which it accepted too, at 53002: its card
        // counts every one, though the graph draws only the first.
        const string ClientMade = "Connections: 2 made · 1 accepted at port 9000, as the capture saw them open";
        Assert.Equal(ClientMade, Card(100)[1]);
        Assert.Equal("Connections: 1 made · 2 accepted at port 8080, as the capture saw them open", Card(200)[1]);
        Assert.Equal(["Other hosts: 1 TCP connection to 1 host", "Connections: 3 made · 1 accepted at port 53002, as the capture saw them open"],
            Card(300).Skip(1).Take(2));

        // At the client's rung the updater is the rest of the machine, whose connections are left to its own rung.
        Assert.True(workspace.OpenGraphNode(Key(100)));
        await workspace.LayoutReady;
        GraphDisplayNode rest = Assert.Single(workspace.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Context);
        Assert.Single(rest.Members);
        Assert.DoesNotContain(workspace.DescribeGraphHover(rest.Key)!.Lines, line => line.StartsWith("Connections:", StringComparison.Ordinal));
        Assert.Equal(ClientMade, Card(100)[1]);
    });

    [Fact(DisplayName = "§6.7: a port alone finds the process that accepted its own machine's connections at it, listed and marked in the graph, as its card names the port")]
    public void APortSearchFindsTheProcessThatListenedAtIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Mixed());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        string Key(int pid) => workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(process => process.ProcessId == pid).Id)!.Key;

        // The server accepted the client's connections at 8080, and the client the server's at 9000: each is found by its own.
        workspace.SearchText = ":8080";
        Assert.Contains(Key(200), workspace.SearchedGraphNodeKeys);
        Assert.DoesNotContain(Key(100), workspace.SearchedGraphNodeKeys);
        Assert.Equal("TCP connections accepted at the port searched",
            workspace.SearchResults.Single(row => row.Hit.Label == "server.exe · PID 200").Hit.Detail);
        workspace.SearchText = ":9000";
        Assert.Contains(Key(100), workspace.SearchedGraphNodeKeys);
        Assert.DoesNotContain(Key(200), workspace.SearchedGraphNodeKeys);

        // A port no process accepted at finds none of them.
        workspace.SearchText = ":7070";
        Assert.DoesNotContain(workspace.SearchResults, row => row.Hit.Kind == SearchHitKind.Process);
    });

    [Fact(DisplayName = "§6.8: the inspector states the connections a selected process or group made and accepted, as its card counts them, the whole session's under a brush, and not recorded where no open is")]
    public void TheInspectorStatesTheSelectionsConnections() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Mixed(), .. Workers()]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        ProcessInstanceId Id(int pid) => workspace.Snapshot.Processes.Single(process => process.ProcessId == pid).Id;
        // What the inspector last heard of them, so a binding is never left reading a stale count.
        var heard = new List<string>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.SelectionConnections))
            {
                heard.Add(workspace.SelectionConnections);
            }
        };

        // Nothing selected, nothing said; a process says what its card does, beneath its own heading.
        Assert.False(workspace.ShowsConnections);
        workspace.SelectProcess(Id(100));
        Assert.Equal("2 made · 1 accepted at port 9000, as the capture saw them open", workspace.SelectionConnections);
        Assert.True(workspace.ShowsConnections);
        Assert.Equal(workspace.SelectionConnections, heard[^1]);
        workspace.SelectProcess(Id(600));
        Assert.Equal("None the capture saw open", workspace.SelectionConnections);
        Assert.Equal(workspace.SelectionConnections, heard[^1]);

        // A group's are its members' together, at each port its members accepted at.
        workspace.ClearSelection();
        Assert.False(workspace.ShowsConnections);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "worker.exe");
        Assert.NotNull(workspace.SelectedGroup);
        Assert.Equal("0 made · 2 accepted at ports 9000 and 9001, as the capture saw them open", workspace.SelectionConnections);
        Assert.Equal(workspace.SelectionConnections, heard[^1]);

        // Under a brush they are still the whole session's, and say so.
        workspace.SelectProcess(Id(100));
        workspace.SelectInterval(new TimeRange(workspace.Snapshot.Extent.StartTicks, workspace.Snapshot.Extent.StartTicks + 5));
        await workspace.IntervalReady;
        Assert.Equal("2 made · 1 accepted at port 9000, as the capture saw them open · whole session", workspace.SelectionConnections);
        Assert.Equal(workspace.SelectionConnections, heard[^1]);

        // A session that records no connection's open cannot say how many were made or accepted.
        using var unseen = new TemporarySession();
        Publish(unseen.Store, Unseen());
        SessionOverviewBundle unopened = SessionOverviewProjector.Project(unseen.Store);
        using var blind = new WorkspaceViewModel(OverviewWorkspace.From(unopened), unopened.GraphIdentity,
            new SessionEvidenceSource(unseen.Path, unopened.SessionId, unopened.Generation));
        blind.SelectProcess(blind.Snapshot.Processes.Single(process => process.ProcessId == 100).Id);
        Assert.Equal("Not recorded: this session records no connection's open, so how many were made or accepted is unknown",
            blind.SelectionConnections);
    });

    [Fact(DisplayName = "§7.4: a process's channels lead with an arrow toward a peer it connected to, or from one that connected to it, and both ways where the capture cannot say")]
    public void AProcesssChannelsLeadWithWhoConnected() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Mixed());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single();
        Assert.True(workspace.Descend());

        // The client made two of its connections to the server and the server one to it; the capture saw one open neither way.
        Assert.Equal(
            [
                ("← " + Server, "Connection from " + Server),
                ("→ " + Server, "Connection to " + Server),
                ("→ " + Server, "Connection to " + Server),
                ("↔ " + Server, "Channel with " + Server),
            ],
            workspace.RungRows.Select(row => (row.Label, row.SpokenLabel!)).Order());
    });

    [Fact(DisplayName = "§7.4: at a process's rung a peer of its own program is named by its PID, as the graph names it there, and read aloud whole")]
    public void APeerOfTheProcesssOwnProgramIsNamedByItsPid() => SingleThreadedContext.Run(async () =>
    {
        // Two of browser.exe's processes connect to the third, which connects to the server.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 301, 2) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 302, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
            Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 130 },
            .. Connection(10, 301, 300, "127.0.0.1:50001", "127.0.0.1:9222"),
            .. Connection(20, 302, 300, "127.0.0.1:50002", "127.0.0.1:9222"),
            .. Connection(30, 300, 400, "127.0.0.1:50003", "127.0.0.1:8080"),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        ProcessNode browser = workspace.Snapshot.Processes.Single(process => process.ProcessId == 300);
        foreach (string key in new[] { browser.GroupKey, browser.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        Assert.Equal(
            [
                ("← PID 301", "Connection from browser.exe · PID 301"),
                ("← PID 302", "Connection from browser.exe · PID 302"),
                ("→ server.exe · PID 400", "Connection to server.exe · PID 400"),
            ],
            workspace.RungRows.Select(row => (row.Label, row.SpokenLabel!)).Order());
        ProcessNode child = workspace.Snapshot.Processes.Single(process => process.ProcessId == 301);
        Assert.Equal("PID 301", GraphView.DrawnName(workspace.GraphDisplay.Nodes.Single(node => node.Process == child.Id),
            workspace.GraphDisplay));
    });

    [Fact(DisplayName = "§6.3: a chosen program's inspector names the programs its processes talked with, the busiest first, each maker first, as icat overview sums them")]
    public void AChosenProgramNamesTheProgramsItTalkedWith() => SingleThreadedContext.Run(async () =>
    {
        // Three of browser.exe's processes each connect to the server, and two of them to the first; the server connects
        // to five more programs once each.
        string[] others = ["alpha", "beta", "gamma", "delta", "epsilon"];
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 301, 2) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 302, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
            Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 130 },
            .. others.Select((name, index) => Lifecycle(5 + index, ObservationKind.Create, 500 + index, (ulong)(5 + index))
                with { ResourceName = $@"C:\Apps\{name}.exe", SessionRelativeTicks = 140 + index }),
            .. Connection(20, 300, 400, "127.0.0.1:50000", "127.0.0.1:8080"),
            .. Connection(30, 301, 400, "127.0.0.1:50001", "127.0.0.1:8080"),
            .. Connection(40, 302, 400, "127.0.0.1:50002", "127.0.0.1:8080"),
            .. Connection(50, 301, 300, "127.0.0.1:50003", "127.0.0.1:9222"),
            .. Connection(60, 302, 300, "127.0.0.1:50004", "127.0.0.1:9222"),
            .. others.SelectMany((_, index) => Connection(70 + (10 * index), 400, 500 + index, $"127.0.0.1:{51000 + index}",
                $"127.0.0.1:{7000 + index}")),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        // What the inspector last heard of them, so a binding is never left reading a stale list, heading or list name.
        var heard = new List<int>();
        var headings = new List<string>();
        var names = new List<string>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.SelectionPrograms))
            {
                heard.Add(workspace.SelectionPrograms.Count);
            }
            else if (changed.PropertyName == nameof(WorkspaceViewModel.ProgramsHeading))
            {
                headings.Add(workspace.ProgramsHeading);
            }
            else if (changed.PropertyName == nameof(WorkspaceViewModel.ProgramsListName))
            {
                names.Add(workspace.ProgramsListName);
            }
        };

        // Nothing chosen, nothing named.
        Assert.False(workspace.ShowsPrograms);
        Assert.Empty(workspace.SelectionPrograms);

        // The browser's processes connected to the server three times, and to each other twice, each at the port their
        // connections arrived at.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "browser.exe");
        Assert.True(workspace.ShowsPrograms);
        Assert.Equal(
            [
                ("browser.exe → server.exe", "3 relationships · TCP at 8080 · 12 records"),
                ("browser.exe → browser.exe", "2 relationships · TCP at 9222 · 8 records"),
            ],
            workspace.SelectionPrograms.Select(row => (row.Pair, row.Counts)));
        Assert.Equal(2, heard[^1]);
        Assert.False(workspace.ShowsProgramsMore);
        Assert.False(workspace.OffersProgramsToggle);
        Assert.False(workspace.TogglePrograms());
        Assert.False(workspace.ListsAllPrograms);
        // Each row says itself whole as the keyboard reaches it, who connected in words, and what Enter on it does; its own
        // processes' pair goes nowhere, and says no Enter.
        Assert.Equal(
            [
                "browser.exe connected to server.exe: 3 relationships, TCP at 8080, 12 records. Press Enter to choose the "
                    + "program at its other end.",
                "browser.exe connected to browser.exe: 2 relationships, TCP at 9222, 8 records",
            ],
            workspace.SelectionPrograms.Select(row => row.AccessibleName));
        Assert.Equal("Relationships by program; Enter on one chooses the program at its other end", workspace.ProgramsListName);

        // The server talked with six programs, five of them named, ties by name, and the sixth counted.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.Equal(
            ["browser.exe → server.exe", "server.exe → alpha.exe", "server.exe → beta.exe", "server.exe → delta.exe",
                "server.exe → epsilon.exe"],
            workspace.SelectionPrograms.Select(row => row.Pair));
        Assert.Equal("1 relationship · TCP at 7004 · 4 records", workspace.SelectionPrograms[^1].Counts);
        Assert.Equal("server.exe connected to epsilon.exe: 1 relationship, TCP at 7004, 4 records. Press Enter to choose the "
            + "program at its other end.", workspace.SelectionPrograms[^1].AccessibleName);

        // A button offers the sixth: every pair on request, and the busiest five again.
        Assert.True(workspace.OffersProgramsToggle);
        Assert.Equal("Show all 6 programs", workspace.ProgramsToggleLabel);
        Assert.True(workspace.TogglePrograms());
        Assert.True(workspace.ListsAllPrograms);
        Assert.Equal("server.exe → gamma.exe", workspace.SelectionPrograms[^1].Pair);
        Assert.Equal(6, heard[^1]);
        Assert.Equal("Show the busiest 5", workspace.ProgramsToggleLabel);
        Assert.False(workspace.ShowsProgramsMore);
        Assert.False(workspace.TogglePrograms());
        Assert.False(workspace.ListsAllPrograms);
        Assert.Equal(5, heard[^1]);
        Assert.Equal("Show all 6 programs", workspace.ProgramsToggleLabel);

        // Enter on a pair chooses the program at its other end, as choosing its ranked row does, its own pairs then listed:
        // from the server the browser, named first as the one that connected, and from the browser the server again, then
        // alpha.exe, which lists the server it was come to from. Which programs talked with which is walked a pair at a time.
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms[0]));
        Assert.Equal(("browser.exe", "browser.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));
        Assert.Equal(2, heard[^1]);
        Assert.False(workspace.ChooseProgram(workspace.SelectionPrograms[1]));
        Assert.Equal("browser.exe", workspace.SelectedGroup?.Name);
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms[0]));
        Assert.Equal("server.exe", workspace.SelectedGroup?.Name);
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms[1]));
        Assert.Equal(("alpha.exe", "alpha.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));
        Assert.Equal(["server.exe → alpha.exe"], workspace.SelectionPrograms.Select(row => row.Pair));
        Assert.Equal(1, heard[^1]);

        // Esc's way back comes back along the hops, the latest first, each saying the program it left, the ranked row of the
        // one come back to chosen with it.
        Assert.Equal(Key(workspace, "alpha.exe"), workspace.ComeBackFromProgram());
        Assert.Equal(("server.exe", "server.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));
        Assert.Equal(Key(workspace, "server.exe"), workspace.ComeBackFromProgram());
        Assert.Equal(("browser.exe", "browser.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));
        Assert.Equal(Key(workspace, "browser.exe"), workspace.ComeBackFromProgram());
        Assert.Equal("server.exe", workspace.SelectedGroup?.Name);
        Assert.Null(workspace.ComeBackFromProgram());
        Assert.Equal("server.exe", workspace.SelectedGroup?.Name);

        // A hop the selection has since moved away from is forgotten, with the hops before it, even once a program they chose
        // is chosen again.
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms[1]));
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms[0]));
        Assert.Equal("server.exe", workspace.SelectedGroup?.Name);
        foreach (string program in (string[])["beta.exe", "alpha.exe", "server.exe"])
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == program);
            Assert.Null(workspace.ComeBackFromProgram());
            Assert.Equal(program, workspace.SelectedGroup?.Name);
        }

        // Under a brush they are still the whole session's, and say so; a pair still chooses its other end and its row.
        workspace.SelectInterval(new TimeRange(workspace.Snapshot.Extent.StartTicks, workspace.Snapshot.Extent.StartTicks + 5));
        await workspace.IntervalReady;
        Assert.Equal("Relationships by program · whole session", workspace.ProgramsHeading);
        Assert.Equal(workspace.ProgramsHeading, headings[^1]);
        Assert.Equal("Relationships by program, over the whole session; Enter on one chooses the program at its other end",
            workspace.ProgramsListName);
        Assert.Equal(workspace.ProgramsListName, names[^1]);
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms[0]));
        Assert.Equal(("browser.exe", "browser.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));

        // A process is not a program, and neither is a group of processes by their terminal session.
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(process => process.ProcessId == 400).Id);
        Assert.False(workspace.ShowsPrograms);
        Assert.Equal(0, heard[^1]);
        using var bySession = new WorkspaceViewModel(WorkspaceGrouping.Regroup(OverviewWorkspace.From(overview), LaneGrouping.UserSession),
            WorkspaceGrouping.Identity(overview.GraphIdentity, LaneGrouping.UserSession),
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await bySession.LayoutReady;
        bySession.SelectedRung = bySession.RungRows[0];
        Assert.NotNull(bySession.SelectedGroup);
        Assert.False(bySession.ShowsPrograms);

        static string Key(WorkspaceViewModel workspace, string program) =>
            workspace.Snapshot.Groups.Single(group => group.Name == program).Key;
    });

    [Fact(DisplayName = "§6.3: Esc comes back along a chosen program's pairs only on the rung each hop was made at")]
    public void AProgramsHopIsNoWayBackFromAnotherRung() => SingleThreadedContext.Run(async () =>
    {
        // The browser connects to each of twenty-six server processes, more of one program than the graph draws apart, so
        // at the browser's rung the server is drawn as one node; the first of them connects to alpha.exe.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 500, 2) with { ResourceName = @"C:\Apps\alpha.exe", SessionRelativeTicks = 110 },
            .. Enumerable.Range(0, 26).Select(index => Lifecycle(3 + index, ObservationKind.Create, 400 + index, (ulong)(3 + index))
                with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 120 + index }),
            .. Enumerable.Range(0, 26).SelectMany(index => Connection(200 + (10 * index), 300, 400 + index,
                $"127.0.0.1:{50000 + index}", $"127.0.0.1:{8000 + index}")),
            .. Connection(600, 400, 500, "127.0.0.1:51000", "127.0.0.1:7000"),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        string server = workspace.Snapshot.Groups.Single(group => group.Name == "server.exe").Key;
        string alpha = workspace.Snapshot.Groups.Single(group => group.Name == "alpha.exe").Key;

        // A hop to the server made on the machine's rung...
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "browser.exe");
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms.Single()));
        Assert.Equal(server, workspace.SelectedGroup?.Key);

        // ...is no way back on the browser's rung, where the server is chosen by its one node.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "browser.exe");
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;
        GraphDisplayNode drawn = Assert.IsType<GraphDisplayNode>(
            workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(process => process.ProcessId == 400).Id));
        Assert.Equal(GraphNodeKind.Group, drawn.Kind);
        workspace.SelectGraphNode(drawn.Key);
        Assert.Equal(server, workspace.SelectedGroup?.Key);
        Assert.Null(workspace.ComeBackFromProgram());
        Assert.Equal(server, workspace.SelectedGroup?.Key);

        // One made there is the way back there.
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms.Single(row => row.Pair == "server.exe → alpha.exe")));
        Assert.Equal(alpha, workspace.SelectedGroup?.Key);
        Assert.Equal(alpha, workspace.ComeBackFromProgram());
        Assert.Equal(server, workspace.SelectedGroup?.Key);
    });

    [Fact(DisplayName = "§6.3: a hub's relationships and a busy program's pairs are listed whole on request, a hundred at most and where to read the rest said, and a newer generation keeps them whole")]
    public void AHubsListsAreListedWholeOnRequest() => SingleThreadedContext.Run(async () =>
    {
        // Hub Service.exe's PID 1000 connects to the one process of each of 101 programs.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 1000, 1) with { ResourceName = @"C:\Apps\Hub Service.exe", SessionRelativeTicks = 100 },
            .. Enumerable.Range(0, 101).Select(index => Lifecycle(2 + index, ObservationKind.Create, 2000 + index, (ulong)(2 + index))
                with { ResourceName = $@"C:\Apps\peer{index:D3}.exe", SessionRelativeTicks = 110 + index }),
            .. Enumerable.Range(0, 101).SelectMany(index => Connection(300 + (10 * index), 1000, 2000 + index,
                $"127.0.0.1:{40000 + index}", $"127.0.0.1:{9000 + index}")),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        ProcessInstanceId hub = workspace.Snapshot.Processes.Single(process => process.ProcessId == 1000).Id;

        // The hub's process names its busiest five, and offers a hundred of its 101, the one left said where to read.
        workspace.SelectProcess(hub);
        Assert.Equal(5, workspace.SelectionPeers.Count);
        Assert.Equal("Show the busiest 100 of 101 relationships", workspace.PeersToggleLabel);
        Assert.False(workspace.ShowsPeersMore);
        Assert.True(workspace.TogglePeers());
        Assert.Equal(100, workspace.SelectionPeers.Count);
        Assert.Equal("→ peer099.exe · PID 2099", workspace.SelectionPeers[^1].Peer);
        Assert.Equal("1 more, quieter: icat processes --pid 1000 lists each process it exchanged data with", workspace.PeersMore);
        Assert.True(workspace.ShowsPeersMore);

        // So does its program, of its pairs, its name put so a shell takes it whole where it says where to read the rest.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "Hub Service.exe");
        Assert.Equal(5, workspace.SelectionPrograms.Count);
        Assert.Equal("Show the busiest 100 of 101 programs", workspace.ProgramsToggleLabel);
        Assert.False(workspace.ShowsProgramsMore);
        Assert.True(workspace.TogglePrograms());
        Assert.Equal(100, workspace.SelectionPrograms.Count);
        Assert.Equal("Hub Service.exe → peer099.exe", workspace.SelectionPrograms[^1].Pair);
        Assert.Equal("1 more, quieter: icat programs --program \"Hub Service.exe\" lists them all", workspace.ProgramsMore);
        Assert.True(workspace.ShowsProgramsMore);

        // Another program names its busiest, and offers no more; each list asked for whole stays so for its own.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "peer000.exe");
        Assert.False(workspace.ListsAllPrograms);
        Assert.False(workspace.OffersProgramsToggle);
        Assert.False(workspace.ShowsProgramsMore);
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(process => process.ProcessId == 2000).Id);
        Assert.False(workspace.ListsAllPeers);
        Assert.False(workspace.OffersPeersToggle);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "Hub Service.exe");
        Assert.Equal(100, workspace.SelectionPrograms.Count);

        // A newer generation keeps both whole.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal((hub, workspace.SelectedGroup?.Key), (saved.PeersListedWhole, saved.ProgramsListedWhole));
        using var next = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity + ":2",
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await next.LayoutReady;
        Assert.Null(next.RestoreNavigation(saved));
        Assert.Equal(("Hub Service.exe", 100), (next.SelectedGroup?.Name, next.SelectionPrograms.Count));
        next.SelectProcess(hub);
        Assert.Equal(100, next.SelectionPeers.Count);
    });

    [Fact(DisplayName = "R7: a newer generation keeps the way back along a walk of relationships or of programs, and lets a hop go where the session no longer holds where it began")]
    public void ANewerGenerationKeepsTheWayBackAlongAWalk() => SingleThreadedContext.Run(async () =>
    {
        // browser.exe's PID 300 connects to server.exe's PID 400, and the server to alpha.exe's PID 500.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 400, 2) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 500, 3) with { ResourceName = @"C:\Apps\alpha.exe", SessionRelativeTicks = 120 },
            .. Connection(20, 300, 400, "127.0.0.1:50000", "127.0.0.1:8080"),
            .. Connection(30, 400, 500, "127.0.0.1:50001", "127.0.0.1:7000"),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var old = await Generation(1);
        ProcessInstanceId browser = Process(old, 300).Id, server = Process(old, 400).Id, alpha = Process(old, 500).Id;
        string browsers = Key(old, "browser.exe"), servers = Key(old, "server.exe"), alphas = Key(old, "alpha.exe");

        // A walk of relationships from the browser's process to alpha's...
        old.SelectProcess(browser);
        Assert.True(old.ChoosePeer(old.SelectionPeers.Single()));
        Assert.True(old.ChoosePeer(old.SelectionPeers.Single(row => row.PeerId == alpha)));
        WorkspaceNavigationMemento walked = old.CaptureNavigation();
        Assert.Equal([new PeerHop(browser, server, 0), new PeerHop(server, alpha, 0)], walked.PeerHops);
        Assert.Null(walked.ProgramHops);

        // ...is come back along in the next generation, hop by hop.
        using (WorkspaceViewModel next = await Generation(2))
        {
            Assert.Null(next.RestoreNavigation(walked));
            Assert.Equal(alpha, next.SelectedProcess?.Id);
            Assert.Equal(alpha, next.ComeBackFromPeer());
            Assert.Equal(server, next.SelectedProcess?.Id);
            Assert.Equal(server, next.ComeBackFromPeer());
            Assert.Equal(browser, next.SelectedProcess?.Id);
            Assert.Null(next.ComeBackFromPeer());
        }

        // So is a walk of programs, each ranked row chosen again with its program.
        old.SelectedRung = old.RungRows.Single(row => row.Label == "browser.exe");
        Assert.True(old.ChooseProgram(old.SelectionPrograms.Single()));
        Assert.True(old.ChooseProgram(old.SelectionPrograms.Single(row => row.OtherKey == alphas)));
        WorkspaceNavigationMemento programs = old.CaptureNavigation();
        Assert.Equal([new ProgramHop(browsers, servers, 0), new ProgramHop(servers, alphas, 0)], programs.ProgramHops);
        using (WorkspaceViewModel next = await Generation(3))
        {
            Assert.Null(next.RestoreNavigation(programs));
            Assert.Equal(("alpha.exe", "alpha.exe"), (next.SelectedGroup?.Name, next.SelectedRung?.Label));
            Assert.Equal(alphas, next.ComeBackFromProgram());
            Assert.Equal(("server.exe", "server.exe"), (next.SelectedGroup?.Name, next.SelectedRung?.Label));
            Assert.Equal(servers, next.ComeBackFromProgram());
            Assert.Equal(("browser.exe", "browser.exe"), (next.SelectedGroup?.Name, next.SelectedRung?.Label));
            Assert.Null(next.ComeBackFromProgram());
        }

        // A hop from a process or a program the session no longer holds is let go, with those before it, and the choice
        // stays where Esc came back to.
        var gone = new ProcessInstanceId(Guid.NewGuid());
        using (WorkspaceViewModel next = await Generation(4))
        {
            _ = next.RestoreNavigation(walked with { PeerHops = [new PeerHop(gone, browser, 0), new PeerHop(browser, server, 0),
                new PeerHop(server, alpha, 0)] });
            Assert.Equal(alpha, next.ComeBackFromPeer());
            Assert.Equal(server, next.ComeBackFromPeer());
            Assert.Null(next.ComeBackFromPeer());
            Assert.Equal(browser, next.SelectedProcess?.Id);
        }

        using (WorkspaceViewModel next = await Generation(5))
        {
            _ = next.RestoreNavigation(programs with { ProgramHops = [new ProgramHop("executable:GONE.EXE", browsers, 0),
                new ProgramHop(browsers, servers, 0), new ProgramHop(servers, alphas, 0)] });
            Assert.Equal(alphas, next.ComeBackFromProgram());
            Assert.Equal(servers, next.ComeBackFromProgram());
            Assert.Null(next.ComeBackFromProgram());
            Assert.Equal("browser.exe", next.SelectedGroup?.Name);
        }

        async Task<WorkspaceViewModel> Generation(int generation)
        {
            var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), $"{overview.GraphIdentity}:{generation}",
                new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
            await workspace.LayoutReady;
            return workspace;
        }

        static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
            workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

        static string Key(WorkspaceViewModel workspace, string program) =>
            workspace.Snapshot.Groups.Single(group => group.Name == program).Key;
    });

    [Fact(DisplayName = "§6.3: a chosen process's inspector names the processes it talked with, the busiest first, each marked from its side as its own connections count them, and RPC calls as made or served")]
    public void AChosenProcessNamesTheProcessesItTalkedWith() => SingleThreadedContext.Run(async () =>
    {
        // browser.exe's PID 300 connects to the server twice; its PID 301 and the server each connect to the other once; the
        // server connects to five more programs once each, and sends a resolver a datagram, which opens nothing; the
        // resolver connects to itself, as a self-pipe does.
        string[] others = ["alpha", "beta", "gamma", "delta", "epsilon"];
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 301, 2) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 600, 3) with { ResourceName = @"C:\Apps\resolver.exe", SessionRelativeTicks = 120 },
            Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 130 },
            .. others.Select((name, index) => Lifecycle(5 + index, ObservationKind.Create, 500 + index, (ulong)(5 + index))
                with { ResourceName = $@"C:\Apps\{name}.exe", SessionRelativeTicks = 140 + index }),
            .. Connection(20, 300, 400, "127.0.0.1:50000", "127.0.0.1:8080"),
            .. Connection(30, 300, 400, "127.0.0.1:50001", "127.0.0.1:8080"),
            .. Connection(40, 301, 400, "127.0.0.1:50002", "127.0.0.1:8080"),
            .. Connection(50, 400, 301, "127.0.0.1:50003", "127.0.0.1:9222"),
            .. others.SelectMany((_, index) => Connection(70 + (10 * index), 400, 500 + index, $"127.0.0.1:{51000 + index}",
                $"127.0.0.1:{7000 + index}")),
            .. Connection(120, 600, 600, "127.0.0.1:52000", "127.0.0.1:52001"),
            Datagram(130, ObservationKind.Send, AccountingSide.SendSide, 400),
            Datagram(131, ObservationKind.Receive, AccountingSide.ReceiveSide, 600),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        // What the inspector last heard of them, so a binding is never left reading a stale list or heading.
        var heard = new List<int>();
        var headings = new List<string>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.SelectionPeers))
            {
                heard.Add(workspace.SelectionPeers.Count);
            }
            else if (changed.PropertyName == nameof(WorkspaceViewModel.PeersHeading))
            {
                headings.Add(workspace.PeersHeading);
            }
        };

        // Nothing chosen, nothing named.
        Assert.False(workspace.ShowsPeers);
        Assert.Empty(workspace.SelectionPeers);

        // The server: the browser's PID 300 connected to it twice, it and PID 301 each to the other, then the five it connected
        // to, the busiest first and ties by name, three of its eight left counted.
        workspace.SelectProcess(Process(workspace, 400).Id);
        Assert.True(workspace.ShowsPeers);
        Assert.Equal(
            [
                ("← browser.exe · PID 300", "2 accepted · TCP at 8080 · 8 records"),
                ("⇄ browser.exe · PID 301", "1 made · 1 accepted · TCP at 8080 and 9222 · 8 records"),
                ("→ alpha.exe · PID 500", "1 made · TCP at 7000 · 4 records"),
                ("→ beta.exe · PID 501", "1 made · TCP at 7001 · 4 records"),
                ("→ delta.exe · PID 503", "1 made · TCP at 7003 · 4 records"),
            ],
            workspace.SelectionPeers.Select(row => (row.Peer, row.Detail)));
        Assert.Equal(5, heard[^1]);
        Assert.False(workspace.ShowsPeersMore);
        // Each row says itself whole as the keyboard reaches it, who connected in words, and what Enter on it does.
        Assert.Equal(
            [
                "browser.exe · PID 300 connected to it: 2 accepted, TCP at 8080, 8 records",
                "it and browser.exe · PID 301 each connected to the other: 1 made, 1 accepted, TCP at 8080 and 9222, 8 records",
                "it connected to alpha.exe · PID 500: 1 made, TCP at 7000, 4 records",
                "it connected to beta.exe · PID 501: 1 made, TCP at 7001, 4 records",
                "it connected to delta.exe · PID 503: 1 made, TCP at 7003, 4 records",
            ],
            workspace.SelectionPeers.Select(row => row.Spoken));
        Assert.Equal("browser.exe · PID 300 connected to it: 2 accepted, TCP at 8080, 8 records. Press Enter to choose the process at "
            + "its other end.", workspace.SelectionPeers[0].AccessibleName);
        Assert.Equal("Its relationships, the busiest first; Enter on one chooses the process at its other end",
            workspace.PeersListName);

        // A button offers the other three: every relationship on request, the busiest first, and the busiest five again.
        Assert.True(workspace.OffersPeersToggle);
        Assert.Equal("Show all 8 relationships", workspace.PeersToggleLabel);
        Assert.True(workspace.TogglePeers());
        Assert.True(workspace.ListsAllPeers);
        Assert.Equal(["→ epsilon.exe · PID 504", "→ gamma.exe · PID 502", "resolver.exe · PID 600"],
            workspace.SelectionPeers.Skip(5).Select(row => row.Peer));
        Assert.Equal(8, heard[^1]);
        Assert.Equal("Show the busiest 5", workspace.PeersToggleLabel);
        Assert.False(workspace.ShowsPeersMore);
        Assert.False(workspace.TogglePeers());
        Assert.False(workspace.ListsAllPeers);
        Assert.Equal(5, heard[^1]);

        // The resolver's flow opened nothing, and its connection to itself is drawn with no arrowhead, as its edge's card says
        // display order only: neither row is marked or says a count of connections.
        workspace.SelectProcess(Process(workspace, 600).Id);
        Assert.Equal(
            [
                ("resolver.exe · PID 600 (itself)", "TCP · 4 records", "resolver.exe · PID 600 (itself): TCP, 4 records"),
                ("server.exe · PID 400", "UDP · 2 records", "server.exe · PID 400: UDP, 2 records"),
            ],
            workspace.SelectionPeers.Select(row => (row.Peer, row.Detail, row.Spoken)));
        Assert.False(workspace.ShowsPeersMore);
        Assert.False(workspace.OffersPeersToggle);
        Assert.False(workspace.TogglePeers());
        Assert.False(workspace.ListsAllPeers);

        // Its connection to itself goes nowhere, and says no Enter; its other end, the server, is chosen as a click on its
        // node chooses it, its own relationships then listed, and from the server alpha.exe, which lists the server it was
        // come to from: who connected to whom is walked a relationship at a time.
        Assert.Equal("resolver.exe · PID 600 (itself): TCP, 4 records", workspace.SelectionPeers[0].AccessibleName);
        Assert.False(workspace.ChoosePeer(workspace.SelectionPeers[0]));
        Assert.Equal(600, workspace.SelectedProcess?.ProcessId);
        Assert.True(workspace.ChoosePeer(workspace.SelectionPeers[1]));
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);
        Assert.Equal("← browser.exe · PID 300", workspace.SelectionPeers[0].Peer);
        Assert.Equal(5, heard[^1]);
        Assert.True(workspace.ChoosePeer(workspace.SelectionPeers[2]));
        Assert.Equal(500, workspace.SelectedProcess?.ProcessId);
        Assert.Equal(["← server.exe · PID 400"], workspace.SelectionPeers.Select(row => row.Peer));

        // Esc's way back comes back along the hops, the latest first, saying which process each left: to the server, whose
        // five busiest do not hold the resolver, and then to the resolver.
        Assert.Equal(Process(workspace, 500).Id, workspace.ComeBackFromPeer());
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);
        Assert.Equal(Process(workspace, 400).Id, workspace.ComeBackFromPeer());
        Assert.Equal(600, workspace.SelectedProcess?.ProcessId);
        Assert.Null(workspace.ComeBackFromPeer());
        Assert.Equal(600, workspace.SelectedProcess?.ProcessId);

        // A hop the selection has since moved away from is forgotten, with the hops before it.
        Assert.True(workspace.ChoosePeer(workspace.SelectionPeers[1]));
        Assert.True(workspace.ChoosePeer(workspace.SelectionPeers[2]));
        workspace.SelectProcess(Process(workspace, 400).Id);
        Assert.Null(workspace.ComeBackFromPeer());
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);
        Assert.Null(workspace.ComeBackFromPeer());

        // Under a brush they are still the whole session's, and say so.
        workspace.SelectProcess(Process(workspace, 400).Id);
        workspace.SelectInterval(new TimeRange(workspace.Snapshot.Extent.StartTicks, workspace.Snapshot.Extent.StartTicks + 5));
        await workspace.IntervalReady;
        Assert.Equal("Its relationships, the busiest first · whole session", workspace.PeersHeading);
        Assert.Equal(workspace.PeersHeading, headings[^1]);
        Assert.Equal("Its relationships, the busiest first, over the whole session; Enter on one chooses the process at its "
            + "other end", workspace.PeersListName);

        // A program is not a process: it names programs.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.False(workspace.ShowsPeers);
        Assert.Equal(0, heard[^1]);

        // At the server's own rung a connection chosen among its rows is what the inspector describes, and the server's
        // relationships give way to it. A hop to the server made on the machine's rung is no way back from its own rung.
        workspace.SelectProcess(Process(workspace, 600).Id);
        Assert.True(workspace.ChoosePeer(workspace.SelectionPeers[1]));
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single();
        Assert.True(workspace.Descend());
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);
        Assert.Null(workspace.ComeBackFromPeer());
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);
        workspace.SelectedRung = workspace.RungRows.First(row => row.Label.Contains("browser.exe", StringComparison.Ordinal));
        Assert.NotNull(workspace.DescribedRow);
        Assert.False(workspace.ShowsPeers);
        Assert.Equal(0, heard[^1]);

        // Of RPC calls, those it made and those it served.
        using var calls = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(calls.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        SessionOverviewBundle called = SessionOverviewProjector.Project(calls.Store);
        using var rpc = new WorkspaceViewModel(OverviewWorkspace.From(called), called.GraphIdentity,
            new SessionEvidenceSource(calls.Path, called.SessionId, called.Generation));
        await rpc.LayoutReady;
        rpc.SelectProcess(Process(rpc, 400).Id);
        Assert.Equal(("→ PID 1960", "2 calls made · RPC · 8 records"), (rpc.SelectionPeers[0].Peer, rpc.SelectionPeers[0].Detail));
        rpc.SelectProcess(Process(rpc, 1960).Id);
        Assert.Equal(("← PID 400", "2 calls served · RPC · 8 records", "PID 400 called it: 2 calls served, RPC, 8 records"),
            (rpc.SelectionPeers[0].Peer, rpc.SelectionPeers[0].Detail, rpc.SelectionPeers[0].Spoken));

        static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
            workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

        // A datagram from the server's port 61000 to the resolver's port 53, as either end records it.
        static ObservationRowV1 Datagram(long ticks, ObservationKind kind, AccountingSide side, int owner) =>
            Transfer(ticks, kind, side, 40, owner, (ulong)ticks).Between("127.0.0.1:61000", "127.0.0.1:53")
                with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = ticks * 100 };
    });

    /// <summary>One TCP connection <paramref name="maker"/> made to <paramref name="taker"/>, opened, and a send across it.</summary>
    private static ObservationRowV1[] Connection(long ticks, int maker, int taker, string from, string to) =>
    [
        Transfer(ticks, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker, (ulong)ticks).Between(from, to)
            with { SessionRelativeTicks = ticks * 100 },
        Transfer(ticks + 1, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, taker, (ulong)ticks + 1).Between(to, from)
            with { SessionRelativeTicks = (ticks + 1) * 100 },
        Transfer(ticks + 2, ObservationKind.Send, AccountingSide.SendSide, 64, maker, (ulong)ticks + 2).Between(from, to)
            with { SessionRelativeTicks = (ticks + 2) * 100 },
        Transfer(ticks + 3, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, taker, (ulong)ticks + 3).Between(to, from)
            with { SessionRelativeTicks = (ticks + 3) * 100 },
    ];

    [Fact(DisplayName = "§6.3: an arrowhead points at the disc its connections ran to, standing just off it, wider than its stroke by a margin either side, and is left out where the edge cannot hold one at each end")]
    public void AnArrowheadPointsAtTheDiscItsConnectionsRanTo()
    {
        // Along a level edge whose discs reach 10 px along it, under a 2 px stroke: its tip a pixel off the disc at 100, its
        // base 9.6 px back and 4 px aside - the stroke's half and 3 px more.
        (Point Tip, Point Left, Point Right) toward = GraphView.Arrowhead(new Point(0, 0), new Point(100, 0), 11, 11, 2)!.Value;
        Assert.Equal((89d, 0d), (toward.Tip.X, toward.Tip.Y));
        Assert.Equal((79.4, 4d, 79.4, -4d), (Math.Round(toward.Left.X, 6), Math.Round(toward.Left.Y, 6),
            Math.Round(toward.Right.X, 6), Math.Round(toward.Right.Y, 6)));

        // Under a 6 px stroke it is wider and longer, still 3 px beyond the stroke either side.
        (Point Tip, Point Left, Point Right) thick = GraphView.Arrowhead(new Point(0, 0), new Point(100, 0), 11, 11, 6)!.Value;
        Assert.Equal((89d, 74.6, 6d), (thick.Tip.X, Math.Round(thick.Left.X, 6), Math.Round(thick.Left.Y, 6)));

        // The other way, and down a vertical edge, it points as the edge runs, standing off its own end's disc.
        (Point Tip, Point Left, Point Right) back = GraphView.Arrowhead(new Point(100, 0), new Point(0, 0), 11, 5, 2)!.Value;
        Assert.Equal((5d, 0d, 14.6, -4d), (back.Tip.X, back.Tip.Y, Math.Round(back.Left.X, 6), Math.Round(back.Left.Y, 6)));
        (Point Tip, Point Left, Point Right) down = GraphView.Arrowhead(new Point(0, 0), new Point(0, 100), 11, 11, 2)!.Value;
        Assert.Equal((0d, 89d, -4d, 79.4), (Math.Round(down.Tip.X, 6), Math.Round(down.Tip.Y, 6), Math.Round(down.Left.X, 6),
            Math.Round(down.Left.Y, 6)));

        // An edge that carries a head at each end and cannot hold both, with 6 px of stroke between them, past both
        // stand-offs draws neither.
        Assert.Null(GraphView.Arrowhead(new Point(0, 0), new Point(47, 0), 11, 11, 2));
        Assert.NotNull(GraphView.Arrowhead(new Point(0, 0), new Point(48, 0), 11, 11, 2));
        Assert.Null(GraphView.Arrowhead(new Point(0, 0), new Point(48, 0), 11, 12, 2));

        // One that carries a lone head needs room for it and 6 px of stroke behind it alone.
        Assert.Null(GraphView.Arrowhead(new Point(0, 0), new Point(37, 0), 11, 11, 2, both: false));
        Assert.NotNull(GraphView.Arrowhead(new Point(0, 0), new Point(38, 0), 11, 11, 2, both: false));

        // A disc reaches its radius along an edge through its centre, less along one drawn in a lane beside it, and nothing
        // along one that passes outside it.
        Assert.Equal(10, GraphView.DiscReach(10, new Point(5, 5), new Point(5, 5)));
        Assert.Equal(6, GraphView.DiscReach(10, new Point(0, 0), new Point(0, 8)), 9);
        Assert.Equal(8, GraphView.DiscReach(10, new Point(0, 0), new Point(-6, 0)), 9);
        Assert.Equal(0, GraphView.DiscReach(10, new Point(0, 0), new Point(0, 12)));
    }

    [Fact(DisplayName = "§6.3: a node's label keeps clear of an arrowhead where a slot clear of every head is left, and covers one rather than go unnamed")]
    public void ALabelKeepsClearOfAnArrowhead()
    {
        Rect pane = new(0, 0, 400, 300);
        Point node = new(200, 150);
        var points = new Dictionary<string, Point>(StringComparer.Ordinal) { ["node"] = node };
        var radii = new Dictionary<string, double>(StringComparer.Ordinal) { ["node"] = 10 };

        // With nothing about it, a node's label goes below it.
        Rect below = new(170, 163, 60, 20);
        Assert.Equal(below, GraphView.FindLabelBox(pane, node, 10, 60, 20, "node", points, radii, [], [], mustShow: false));

        // The head of an edge that meets it from beneath, standing a pixel off it, sends the label above.
        Rect head = GraphView.HeadBox(GraphView.Arrowhead(new Point(200, 250), node, 0, 11, 1.25)!.Value);
        Assert.Equal((196.375, 161d, 7.25, 8.7), (head.X, head.Y, head.Width, Math.Round(head.Height, 6)));
        Assert.Equal(new Rect(170, 117, 60, 20),
            GraphView.FindLabelBox(pane, node, 10, 60, 20, "node", points, radii, [], [head], mustShow: false));

        // Where heads cover every slot, it takes the first that hides no node and no label, over a head.
        Assert.Equal(below, GraphView.FindLabelBox(pane, node, 10, 60, 20, "node", points, radii, [], [pane], mustShow: false));
    }

    [Fact(DisplayName = "§6.3: a host's square reaches its side's half along an axis, further toward a corner, from a lane beside its centre to the side the lane leaves by, and nothing from outside it")]
    public void AHostsSquareReachesToTheSideAnEdgeLeavesBy()
    {
        // Through the centre of a square 16 px a side's half across, along each axis and toward a corner.
        Point center = new(50, 50);
        Assert.Equal(16, GraphView.SquareReach(16, center, center, new Point(150, 50)));
        Assert.Equal(16, GraphView.SquareReach(16, center, center, new Point(-50, 50)));
        Assert.Equal(16, GraphView.SquareReach(16, center, center, new Point(50, 150)));
        Assert.Equal(16, GraphView.SquareReach(16, center, center, new Point(50, -50)));
        Assert.Equal(16 * Math.Sqrt(2), GraphView.SquareReach(16, center, center, new Point(150, 150)), 9);
        Assert.Equal(16 * Math.Sqrt(2), GraphView.SquareReach(16, center, center, new Point(-50, -50)), 9);

        // From a lane 4 px below the centre: toward the lower right it leaves by the bottom side, 12 px down; toward the
        // upper right by the right side, before the top 20 px up; and straight across by the right side.
        Assert.Equal(12 * Math.Sqrt(2), GraphView.SquareReach(16, center, new Point(50, 54), new Point(150, 154)), 9);
        Assert.Equal(16 * Math.Sqrt(2), GraphView.SquareReach(16, center, new Point(50, 54), new Point(150, -46)), 9);
        Assert.Equal(16, GraphView.SquareReach(16, center, new Point(50, 54), new Point(150, 54)));

        // From a lane 4 px right of the centre, straight back across it leaves by the left side, 20 px away; from one 4 px
        // below it, straight up by the top, 20 px away.
        Assert.Equal(20, GraphView.SquareReach(16, center, new Point(54, 50), new Point(-46, 50)));
        Assert.Equal(20, GraphView.SquareReach(16, center, new Point(50, 54), new Point(50, -46)));

        // A host drawn on its own and Other hosts are such squares; a process, a group and the rest are discs.
        GraphDisplayNode Drawn(GraphNodeKind kind) => new("node", "node", kind, null, null, null, [], 0, 0, 0);
        Point corner = new(150, 150);
        Assert.Equal(9 * Math.Sqrt(2), GraphView.Reach(Drawn(GraphNodeKind.Host), 9, center, center, corner), 9);
        Assert.Equal(16 * Math.Sqrt(2), GraphView.Reach(Drawn(GraphNodeKind.OtherHosts), 16, center, center, corner), 9);
        Assert.Equal(9, GraphView.Reach(Drawn(GraphNodeKind.Process), 9, center, center, corner), 9);
        Assert.Equal(9, GraphView.Reach(Drawn(GraphNodeKind.OtherHostsOnly), 9, center, center, corner), 9);

        // From outside the square, or toward nowhere, it reaches nothing.
        Assert.Equal(0, GraphView.SquareReach(16, center, new Point(70, 50), new Point(150, 50)));
        Assert.Equal(0, GraphView.SquareReach(16, center, new Point(50, 30), new Point(50, 150)));
        Assert.Equal(0, GraphView.SquareReach(16, center, center, center));
    }

    /// <summary>
    /// The direction line of the card of the one relationship drawn between client.exe and server.exe, as the edge is drawn
    /// from the client's node, or from the server's when <paramref name="clientFirst"/> is false: the card's own where the
    /// graph draws it so, else the same edge's read reversed.
    /// </summary>
    private static async Task<string> DirectionAsync(ObservationRowV1[] rows, bool clientFirst)
    {
        using var session = new TemporarySession();
        Publish(session.Store, rows);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        GraphDisplayEdge edge = Assert.Single(workspace.GraphDisplay.Edges, drawn => !drawn.ToHosts);
        string clientKey = workspace.GraphDisplay.NodeOf(overview.Nodes.Single(node => node.ProcessId == 100).Id)!.Key;
        string card = workspace.DescribeGraphHover(edge.Key)!.Lines.Single(line => line.StartsWith("Direction: ", StringComparison.Ordinal));
        if ((edge.SourceKey == clientKey) == clientFirst)
        {
            return card;
        }

        string source = workspace.GraphDisplay.Node(edge.SourceKey)!.Title;
        string target = workspace.GraphDisplay.Node(edge.TargetKey)!.Title;
        return WorkspaceViewModel.DirectionLine(edge with
        {
            SourceKey = edge.TargetKey,
            TargetKey = edge.SourceKey,
            MadeFromSource = edge.MadeFromTarget,
            MadeFromTarget = edge.MadeFromSource,
        }, target, source);
    }

    /// <summary>
    /// The client connects to the server's 8080 twice, seen at its end; the server to the client's 9000, seen at the
    /// client's accept; and one more connection to 8080 opens unseen.
    /// </summary>
    private static ObservationRowV1[] Mixed() =>
    [
        .. Processes(),
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 6).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 8).Between("127.0.0.1:8080", "127.0.0.1:50001"),
        Transfer(30, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 100, 9).Between("127.0.0.1:9000", "127.0.0.1:50002"),
        Transfer(31, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 10).Between("127.0.0.1:50002", "127.0.0.1:9000"),
        Transfer(32, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 100, 11).Between("127.0.0.1:9000", "127.0.0.1:50002"),
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12).Between("127.0.0.1:50003", "127.0.0.1:8080"),
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:50003"),
    ];

    /// <summary>The client connects to the server's 8080 twice, the second time seen at the server's accept alone.</summary>
    private static ObservationRowV1[] OneWay() =>
    [
        .. Processes(),
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 6).Between("127.0.0.1:8080", "127.0.0.1:50001"),
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 8).Between("127.0.0.1:8080", "127.0.0.1:50001"),
    ];

    /// <summary>
    /// An updater that connects out to another host's 443 and sends there, connects to this machine's 9999, whose other end
    /// no record holds, and connects to itself, each seen at its end.
    /// </summary>
    private static ObservationRowV1[] Updater() =>
    [
        Lifecycle(45, ObservationKind.Create, 300, 14) with { ResourceName = @"C:\Tools\updater.exe" },
        Transfer(50, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 15).Between("192.168.1.10:52000", "10.0.0.5:443"),
        Transfer(51, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 16).Between("192.168.1.10:52000", "10.0.0.5:443"),
        Transfer(52, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 17).Between("127.0.0.1:53000", "127.0.0.1:9999"),
        Transfer(53, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 18).Between("127.0.0.1:53001", "127.0.0.1:53002"),
        Transfer(54, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 19).Between("127.0.0.1:53002", "127.0.0.1:53001"),
    ];

    /// <summary>Two worker.exe instances, each accepting a connection from another host, and an idle process.</summary>
    private static ObservationRowV1[] Workers() =>
    [
        Lifecycle(45, ObservationKind.Create, 400, 20) with { ResourceName = @"C:\Tools\worker.exe" },
        Lifecycle(46, ObservationKind.Create, 500, 21) with { ResourceName = @"C:\Tools\worker.exe" },
        Lifecycle(47, ObservationKind.Create, 600, 22) with { ResourceName = @"C:\Tools\idle.exe" },
        Transfer(60, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 400, 23).Between("192.168.1.10:9000", "10.0.0.7:50002"),
        Transfer(61, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 500, 24).Between("192.168.1.10:9001", "10.0.0.8:50003"),
    ];

    /// <summary>A TCP connection between them, seen opening at neither end.</summary>
    private static ObservationRowV1[] Unseen() =>
    [
        .. Processes(),
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000"),
    ];

    private static ObservationRowV1[] Processes() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
    ];
}
