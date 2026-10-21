using InterCat.Analysis;
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
/// The processes an aggregate counts (§6.3): No relationships, Other hosts only, Other processes and an opened group's
/// Other members, chosen, list them in the inspector, the busiest first, each with what it did, so a process the graph
/// does not draw on its own is read where it is counted, and Enter on one opens its rung.
/// </summary>
public sealed class AggregateMembersWorkspaceTests
{
    [Fact(DisplayName = "§6.3: an aggregate chosen lists the processes it counts, the busiest first, each with its records and connections to other hosts, and Enter on one opens its rung")]
    public void AnAggregateListsItsProcesses() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // Nothing chosen, nothing is listed.
        Assert.False(workspace.ListsMembers);
        Assert.Empty(workspace.MemberRows);
        Assert.Equal(string.Empty, workspace.MembersHeading);

        // Other hosts only, chosen, lists the two quietest programs, the busier first, each with what it did.
        string hostOnly = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;
        workspace.SelectGraphNode(hostOnly);
        Assert.True(workspace.ListsMembers);
        Assert.Equal("Its 2 processes, the busiest first", workspace.MembersHeading);
        Assert.Equal(
            [
                ("program1.exe · PID 101", "4 records · 1 TCP connection to 1 host"),
                ("program0.exe · PID 100", "3 records · 1 TCP connection from 1 host · accepted here at port 445"),
            ],
            workspace.MemberRows.Select(row => (row.Name, row.Detail)));
        Assert.Equal("program1.exe · PID 101, 4 records · 1 TCP connection to 1 host. Press E to list its records, or Enter to "
            + "open it.", workspace.MemberRows[0].AccessibleName);
        Assert.False(workspace.HasMembersNote);

        // No relationships lists its three, as busy as each other, by name whatever its case; Other hosts, which stands for no
        // process, and a process drawn on its own list none.
        workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet).Key);
        Assert.Equal(["quiet0.exe · PID 200", "QUIET1.EXE · PID 201", "quiet2.exe · PID 202"], workspace.MemberRows.Select(row => row.Name));
        Assert.All(workspace.MemberRows, row => Assert.Equal("1 record", row.Detail));
        workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);
        Assert.False(workspace.ListsMembers);
        workspace.SelectGraphNode(workspace.GraphDisplay.NodeOf(Process(workspace, 109).Id)!.Key);
        Assert.False(workspace.ListsMembers);

        // Enter on one opens its rung through its group; a process the list does not hold opens nothing.
        workspace.SelectGraphNode(hostOnly);
        MemberRow busier = workspace.MemberRows[0];
        Assert.False(workspace.OpenMember(busier with { Process = Process(workspace, 109).Id }));
        Assert.Single(workspace.Crumbs);
        Assert.True(workspace.OpenMember(busier));
        Assert.Equal(3, workspace.Crumbs.Count);
        Assert.Equal("Group: program1.exe", workspace.Crumbs[1].Label);
        Assert.EndsWith("program1.exe · PID 101", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.4: a process chosen in an aggregate's list is its own records - the card counts them, the timeline highlights them and E lists them - until a node is chosen in the graph")]
    public void AProcessChosenInTheListIsItsRecords() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        string hostOnly = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;
        workspace.SelectGraphNode(hostOnly);
        Assert.Null(workspace.ChosenMemberRow);
        Assert.Equal("Selected aggregate", workspace.EvidenceHeading);
        Assert.Equal("Other hosts only", workspace.TimelineHighlightName);

        // Chosen in the list, program1.exe is the records the card counts and the timeline highlights, as if chosen alone.
        workspace.ChosenMemberRow = workspace.MemberRows[0];
        Assert.Equal(workspace.MemberRows[0], workspace.ChosenMemberRow);
        Assert.Equal("Selected process", workspace.EvidenceHeading);
        Assert.StartsWith("4 own records, mostly TCP · no admitted relationship · ", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.Equal("program1.exe · PID 101", workspace.TimelineHighlightName);
        await workspace.HighlightReady;

        // A row the list does not hold chooses none, and the aggregate's records are the selection's again.
        workspace.ChosenMemberRow = workspace.MemberRows[0] with { Process = Process(workspace, 109).Id };
        Assert.Null(workspace.ChosenMemberRow);
        Assert.Equal("Other hosts only", workspace.TimelineHighlightName);

        // A node chosen in the graph, the same aggregate again included, lets the choice go.
        workspace.ChosenMemberRow = workspace.MemberRows[1];
        workspace.SelectGraphNode(hostOnly);
        Assert.Null(workspace.ChosenMemberRow);
        Assert.Equal("Selected aggregate", workspace.EvidenceHeading);

        // E lists the chosen process's records, saying where they were chosen.
        workspace.ChosenMemberRow = workspace.MemberRows[1];
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(3, workspace.RungRows.Count);
        FilterRow scope = Assert.Single(workspace.Filters, filter => filter.Field == "scope");
        Assert.Equal("Records of: program0.exe · PID 100", scope.Chip);
        Assert.EndsWith("with program0.exe · PID 100 chosen among the processes Other hosts only counts.", scope.Reason,
            StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.3: Enter or a double click on an aggregate chooses it and asks the keyboard to its processes' list, keeping a process chosen there; a focus's context node opens nothing")]
    public void OpeningAnAggregateAsksForItsList() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        var asked = new List<int>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.MembersAsked)) asked.Add(workspace.MembersAsked);
        };

        // Its card says what the gesture does.
        string hostOnly = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;
        Assert.Equal("Double-click or Enter takes the keyboard to its processes in the inspector",
            workspace.DescribeGraphHover(hostOnly)!.Lines[^1]);

        // Opened unchosen, Other hosts only is chosen and its list asked for the keyboard.
        Assert.True(workspace.OpenGraphNode(hostOnly));
        Assert.Equal((hostOnly, true), (workspace.SelectedCluster?.Key, workspace.ListsMembers));
        Assert.Equal([1], asked);

        // Opened again, chosen already, a process chosen in its list stays chosen.
        workspace.ChosenMemberRow = workspace.MemberRows[1];
        Assert.True(workspace.OpenGraphNode(hostOnly));
        Assert.Equal([1, 2], asked);
        Assert.Equal("program0.exe · PID 100", workspace.ChosenMemberRow?.Name);

        // No relationships the same; a process's rung's context node opens nothing.
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet).Key));
        Assert.Equal(GraphNodeKind.Quiet, workspace.SelectedCluster?.Kind);
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.NodeOf(Process(workspace, 109).Id)!.Key));
        await workspace.LayoutReady;
        GraphDisplayNode context = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Context);
        Assert.False(workspace.OpenGraphNode(context.Key));
        Assert.Equal(3, asked.Count);
    });

    [Fact(DisplayName = "§3.2: the way back from a process opened in an aggregate's list chooses the aggregate and that process there again, its row asked for the keyboard")]
    public void TheWayBackFromAListedProcessChoosesItAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        var asked = new List<int>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.MembersAsked)) asked.Add(workspace.MembersAsked);
        };
        string hostOnly = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;
        workspace.SelectGraphNode(hostOnly);

        // program0.exe opened from the list, and its records listed from its rung, a way further down: up from them is its
        // rung, the next its group, it chosen there, and the next where the way down began.
        Assert.True(workspace.OpenMember(workspace.MemberRows[1]));
        Assert.Equal(3, workspace.Crumbs.Count);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(4, workspace.Crumbs.Count);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());
        Assert.Equal("program0.exe · PID 100", workspace.SelectedProcess?.NameWithPid);
        Assert.Empty(asked);
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal((hostOnly, "program0.exe · PID 100"), (workspace.SelectedCluster?.Key, workspace.ChosenMemberRow?.Name));
        Assert.Equal([1], asked);

        // The breadcrumb's way back does the same.
        Assert.True(workspace.OpenMember(workspace.MemberRows[0]));
        workspace.ReturnTo(0);
        await workspace.LayoutReady;
        Assert.Equal((hostOnly, "program1.exe · PID 101"), (workspace.SelectedCluster?.Key, workspace.ChosenMemberRow?.Name));
        Assert.Equal([1, 2], asked);

        // Forward to the process again and back, it is chosen there again, as back and forward retrace one way.
        Assert.True(workspace.GoForward());
        Assert.True(workspace.GoForward());
        Assert.Equal(3, workspace.Crumbs.Count);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal((hostOnly, "program1.exe · PID 101"), (workspace.SelectedCluster?.Key, workspace.ChosenMemberRow?.Name));
        Assert.Equal([1, 2, 3], asked);

        // The crumb of the rung shown, chosen again, is no way back: what was chosen there since stays chosen.
        string quiet = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet).Key;
        workspace.SelectGraphNode(quiet);
        workspace.ReturnTo(0);
        Assert.Equal(quiet, workspace.SelectedCluster?.Key);
        Assert.Equal([1, 2, 3], asked);

        // Down again by the ranked table and back, the group's row is chosen, as ever: a way down of its own replaces the
        // list's.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "program9.exe");
        Assert.True(workspace.Descend());
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal((null, "program9.exe"), (workspace.SelectedCluster?.Key, workspace.SelectedRung?.Label));

        // A process opened from the list and then another found by a search: the way back is the search's.
        workspace.SelectGraphNode(hostOnly);
        Assert.True(workspace.OpenMember(workspace.MemberRows[0]));
        workspace.SearchText = "program9";
        Assert.True(workspace.OpenSearchResult(workspace.SearchResults.First(hit => hit.Label.StartsWith("program9.exe · PID", StringComparison.Ordinal))));
        workspace.ReturnTo(0);
        await workspace.LayoutReady;
        Assert.Null(workspace.SelectedCluster);

        // A process opened from the graph comes back to its group's row, as ever.
        string drawn = workspace.GraphDisplay.NodeOf(Process(workspace, 109).Id)!.Key;
        workspace.SelectGraphNode(drawn);
        Assert.True(workspace.OpenGraphNode(drawn));
        workspace.ReturnTo(0);
        await workspace.LayoutReady;
        Assert.Null(workspace.SelectedCluster);
        Assert.Equal([1, 2, 3], asked);
    });

    [Fact(DisplayName = "§3.2: E on a process chosen in an aggregate's list is a way down from the list too: back from its records, the aggregate and the process are chosen there again, its row asked for the keyboard")]
    public void TheWayBackFromTheRecordsOfAListedProcessChoosesItAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        var asked = new List<int>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.MembersAsked)) asked.Add(workspace.MembersAsked);
        };
        string hostOnly = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;
        workspace.SelectGraphNode(hostOnly);
        workspace.ChosenMemberRow = workspace.MemberRows[1];
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;

        // Its choice let go beside its records, the aggregate chosen again in the graph, Esc still comes back to it.
        workspace.SelectGraphNode(hostOnly);
        Assert.Null(workspace.ChosenMemberRow);
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal((hostOnly, "program0.exe · PID 100"), (workspace.SelectedCluster?.Key, workspace.ChosenMemberRow?.Name));
        Assert.Equal([1], asked);

        // E on the aggregate itself is a way down of its own: back from its records, nothing of the list is chosen again.
        workspace.SelectGraphNode(hostOnly);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.ChosenMemberRow);
        Assert.Equal([1], asked);
    });

    [Fact(DisplayName = "§3.2: a process opened from an opened group's Other members comes back to that list at the group's rung, and forward and back again")]
    public void TheWayBackToAGroupsOtherMembersChoosesItAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, GroupRows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        var asked = new List<int>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.MembersAsked)) asked.Add(workspace.MembersAsked);
        };
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svc.exe");
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;
        string others = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherMembers).Key;
        workspace.SelectGraphNode(others);
        Assert.Equal("2 processes of this group not drawn on their own, none with a relationship. They are listed below.",
            workspace.SelectionSubtitle);
        Assert.Equal(["svc.exe · PID 302", "svc.exe · PID 303"], workspace.MemberRows.Select(row => row.Name));

        // Opened from the list, svc.exe 303's rung is a rung below the group's, where Esc chooses it in the list again.
        Assert.True(workspace.OpenMember(workspace.MemberRows[1]));
        Assert.Equal(3, workspace.Crumbs.Count);
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal((others, "svc.exe · PID 303"), (workspace.SelectedCluster?.Key, workspace.ChosenMemberRow?.Name));
        Assert.Equal([1], asked);

        // Climbed past to the machine and forward down again, the way back comes to the list once more.
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal([1], asked);
        Assert.True(workspace.GoForward());
        Assert.True(workspace.GoForward());
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal((others, "svc.exe · PID 303"), (workspace.SelectedCluster?.Key, workspace.ChosenMemberRow?.Name));
        Assert.Equal([1, 2], asked);
    });

    [Fact(DisplayName = "§6.3: under a brush an aggregate's processes are listed by their records in it, their connections to other hosts the whole session's, as said")]
    public void UnderABrushTheListCountsTheInterval() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key);

        // Brushed over the processes' starts alone, each holds one record there, so they are listed by name, and their
        // connections, which a brush does not re-count, say they are the whole session's.
        // The list is restated once the interval's counts arrive, not only when it is chosen.
        var restated = new List<string?>();
        workspace.PropertyChanged += (_, changed) => restated.Add(changed.PropertyName);
        long start = workspace.Snapshot.Extent.StartTicks;
        workspace.SelectInterval(new TimeRange(start, start + 50));
        restated.Clear();
        await workspace.IntervalReady;
        Assert.Contains(nameof(WorkspaceViewModel.MemberRows), restated);
        Assert.Equal(
            [
                ("program0.exe · PID 100", "1 record · 1 TCP connection from 1 host · accepted here at port 445 · whole session"),
                ("program1.exe · PID 101", "1 record · 1 TCP connection to 1 host · whole session"),
            ],
            workspace.MemberRows.Select(row => (row.Name, row.Detail)));
    });

    [Fact(DisplayName = "§6.3: an aggregate's list names its busiest hundred processes and says how many more there are and where to read them")]
    public void AnAggregateListsItsBusiestHundred() => SingleThreadedContext.Run(async () =>
    {
        // A hundred and five idle processes, the later the busier, none related to anything or reaching another host.
        ProcessGroup pool = new("idle", "idle.exe", LaneGrouping.Executable);
        ProcessNode[] idle = [.. Enumerable.Range(1, 105).Select(index => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), 1_000 + index, "idle.exe", "test",
            pool.Key, 0.5, 0.5, CoverageState.Covered) { Activity = [new MechanismCount(Mechanism.ProcessLifecycle, index)] })];
        using var workspace = new WorkspaceViewModel(
            new WorkspaceSnapshot("Idle", new TimeRange(0, 10), [pool], idle, [], [], [], [], []), "idle-hundred");
        await workspace.LayoutReady;

        workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet).Key);
        Assert.Equal("Its busiest 100 of 105 processes", workspace.MembersHeading);
        Assert.Equal(100, workspace.MemberRows.Count);
        Assert.Equal(("idle.exe · PID 1105", "105 records"), (workspace.MemberRows[0].Name, workspace.MemberRows[0].Detail));
        Assert.Equal(("idle.exe · PID 1006", "6 records"), (workspace.MemberRows[^1].Name, workspace.MemberRows[^1].Detail));
        Assert.True(workspace.HasMembersNote);
        Assert.Equal("5 more, quieter: the ranked table lists every one, and a search (Ctrl+F) finds one by its name or PID.",
            workspace.MembersNote);
    });

    [Fact(DisplayName = "§6.3: Other processes, folded for the graph's budget, lists its processes with their relationships")]
    public void OtherProcessesListsTheirRelationships() => SingleThreadedContext.Run(async () =>
    {
        // A hundred and ten programs of one process each, related in pairs, the later the busier: past the graph's budget
        // of a hundred nodes the eleven least active fold into Other processes.
        ProcessGroup[] groups = [.. Enumerable.Range(0, 110)
            .Select(index => new ProcessGroup($"g{index:D3}", $"app{index:D3}.exe", LaneGrouping.Executable))];
        ProcessNode[] processes = [.. groups.Select((group, index) => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), 1_000 + index, $"app{index:D3}.exe", "test",
            group.Key, 0.5, 0.5, CoverageState.Covered) { Activity = [new MechanismCount(Mechanism.Tcp, 2 + index)] })];
        CommunicationEdge[] pairs = [.. Enumerable.Range(0, 55).Select(pair => new CommunicationEdge($"pair-{pair}",
            processes[2 * pair].Id, processes[(2 * pair) + 1].Id, Mechanism.Tcp, 1 + pair, null, RelationStrength.Direct)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [],
        })];
        using var workspace = new WorkspaceViewModel(
            new WorkspaceSnapshot("Pairs", new TimeRange(0, 10), groups, processes, pairs, [], [], [], []), "pairs-remainder");
        await workspace.LayoutReady;

        GraphDisplayNode remainder = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Remainder);
        workspace.SelectGraphNode(remainder.Key);
        Assert.Equal($"Its {remainder.Members.Count} processes, the busiest first", workspace.MembersHeading);
        ProcessNode[] folded = [.. processes.Where(process => remainder.Members.Contains(process.Id)).OrderByDescending(process => process.Records)];
        Assert.Equal(folded.Select(process => (process.NameWithPid, $"{process.Records} records · 1 relationship")),
            workspace.MemberRows.Select(row => (row.Name, row.Detail)));
    });

    [Fact(DisplayName = "R7: a newer generation keeps the process chosen in an aggregate's list and the way back to it, and says once when the list no longer holds it")]
    public void ANewerGenerationKeepsTheListsChoice() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel first = Open(session);
        await first.LayoutReady;
        string hostOnly = first.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;
        first.SelectGraphNode(hostOnly);
        first.ChosenMemberRow = first.MemberRows[1];
        WorkspaceNavigationMemento chosen = first.CaptureNavigation();

        using WorkspaceViewModel second = Open(session);
        await second.LayoutReady;
        Assert.Null(second.RestoreNavigation(chosen));
        Assert.Equal((hostOnly, "program0.exe · PID 100"), (second.SelectedCluster?.Key, second.ChosenMemberRow?.Name));

        // Opened from the list, its rung comes back to the list's row in the next generation too.
        Assert.True(second.OpenMember(second.MemberRows[1]));
        using WorkspaceViewModel third = Open(session);
        await third.LayoutReady;
        Assert.Null(third.RestoreNavigation(second.CaptureNavigation()));
        Assert.Equal(3, third.Crumbs.Count);
        Assert.True(third.Ascend());
        Assert.True(third.Ascend());
        await third.LayoutReady;
        Assert.Equal((hostOnly, "program0.exe · PID 100"), (third.SelectedCluster?.Key, third.ChosenMemberRow?.Name));

        // program0.exe, busier in a later generation, is drawn on its own: its list no longer holds it, which is said once.
        ulong ordinal = 1_000;
        Publish(session.Store, [.. Enumerable.Range(0, 20).Select(send => Transfer(1_000 + send, ObservationKind.Send,
                AccountingSide.SendSide, 100, 100, ordinal++).Between("192.168.1.10:445", "52.0.0.1:51000")
            with { SessionRelativeTicks = 100 * (1_000L + send) })]);
        using WorkspaceViewModel fourth = Open(session);
        await fourth.LayoutReady;
        Assert.Equal("program0.exe · PID 100 is no longer among the processes Other hosts only lists, so Other hosts only "
            + "alone is chosen.", fourth.RestoreNavigation(chosen));
        Assert.Equal(hostOnly, fourth.SelectedCluster?.Key);
        Assert.Null(fourth.ChosenMemberRow);
    });

    [Theory(DisplayName = "§6.3: a group too large to draw whole folds its least active members into Other members, which says so, and its relationships, beside any that have none")]
    [InlineData(0, "121 processes of this group not drawn on their own, its least active · 61 relationships, 60 among themselves. They are listed below.")]
    [InlineData(2, "123 processes of this group not drawn on their own: 2 without a relationship, the rest its least active · 61 relationships, 60 among themselves. They are listed below.")]
    public void OtherMembersSaysWhatItFolds(int quiet, string described) => SingleThreadedContext.Run(async () =>
    {
        // big.exe's 220 processes related in pairs, the later the busier, and any that relate to nothing: opened, its 99
        // busiest are drawn, to the graph's budget of 100 nodes, and the rest folded - 60 pairs whole and one member of the
        // 61st, whose other is drawn.
        ProcessGroup big = new("big", "big.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(0, 220 + quiet).Select(index => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), 1_000 + index, "big.exe", "test",
            big.Key, 0.5, 0.5, CoverageState.Covered) { Activity = [new MechanismCount(Mechanism.Tcp, 2 + index)] })];
        CommunicationEdge[] pairs = [.. Enumerable.Range(0, 110).Select(pair => new CommunicationEdge($"pair-{pair}",
            processes[2 * pair].Id, processes[(2 * pair) + 1].Id, Mechanism.Tcp, 1 + pair, null, RelationStrength.Direct)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [],
        })];
        using var workspace = new WorkspaceViewModel(
            new WorkspaceSnapshot("Big", new TimeRange(0, 10), [big], processes, pairs, [], [], [], []), "big-group");
        await workspace.LayoutReady;
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;

        workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherMembers).Key);
        Assert.Equal(described, workspace.SelectionSubtitle);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    /// <summary>
    /// svc.exe's four processes, PIDs 300 to 303: 300 connects to 301 over loopback and sends to it, and 302 and 303 only
    /// start.
    /// </summary>
    private static ObservationRowV1[] GroupRows()
    {
        const string Client = "127.0.0.1:50000";
        const string Server = "127.0.0.1:8080";
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>();
        for (int index = 0; index < 4; index++)
        {
            rows.Add(Lifecycle(1 + index, ObservationKind.Create, 300 + index, ordinal++)
                with { ResourceName = @"C:\Apps\svc.exe", SessionRelativeTicks = 100 * (1 + index) });
        }

        rows.Add(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, ordinal++).Between(Client, Server)
            with { SessionRelativeTicks = 1_000 });
        rows.Add(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 301, ordinal++).Between(Server, Client)
            with { SessionRelativeTicks = 1_100 });
        rows.Add(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 64, 300, ordinal++).Between(Client, Server)
            with { SessionRelativeTicks = 1_200 });
        rows.Add(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 301, ordinal++).Between(Server, Client)
            with { SessionRelativeTicks = 1_300 });
        return [.. rows];
    }

    /// <summary>
    /// Ten programs of one process each, program0.exe (PID 100) to program9.exe (PID 109), each connecting to a host of its
    /// own - program0.exe accepting the connection, at its port 445 - and sending to it once more than the one before; and
    /// three processes with no connection, quiet0.exe,
    /// QUIET1.EXE and quiet2.exe (PIDs 200 to 202). Every process starts within 1.4 µs, and every connection opens after
    /// 11 µs.
    /// </summary>
    internal static ObservationRowV1[] Rows()
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>();
        for (int index = 0; index < 10; index++)
        {
            rows.Add(Lifecycle(1 + index, ObservationKind.Create, 100 + index, ordinal++)
                with { ResourceName = $@"C:\Apps\program{index}.exe", SessionRelativeTicks = 100 * (1 + index) });
        }

        for (int index = 0; index < 3; index++)
        {
            rows.Add(Lifecycle(11 + index, ObservationKind.Create, 200 + index, ordinal++)
                with { ResourceName = index == 1 ? @"C:\APPS\QUIET1.EXE" : $@"C:\Apps\quiet{index}.exe", SessionRelativeTicks = 100 * (11 + index) });
        }

        for (int index = 0; index < 10; index++)
        {
            // program0's one connection is one it accepted from its host, at its own port 445.
            string local = index == 0 ? "192.168.1.10:445" : $"192.168.1.10:{52_000 + index}";
            string remote = index == 0 ? "52.0.0.1:51000" : $"52.0.0.{index + 1}:443";
            rows.Add(Transfer(100 + (long)ordinal, index == 0 ? ObservationKind.Accept : ObservationKind.Connect,
                    AccountingSide.EndpointActivity, 0, 100 + index, ordinal)
                .Between(local, remote) with { SessionRelativeTicks = 100 * (100 + (long)ordinal) });
            ordinal++;
            for (int send = 0; send <= index; send++)
            {
                rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 100 + index, ordinal)
                    .Between(local, remote) with { SessionRelativeTicks = 100 * (100 + (long)ordinal) });
                ordinal++;
            }
        }

        return [.. rows];
    }
}
