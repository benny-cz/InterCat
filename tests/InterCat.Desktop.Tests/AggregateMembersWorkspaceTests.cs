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
            [("program1.exe · PID 101", "4 records · 1 TCP connection to 1 host"), ("program0.exe · PID 100", "3 records · 1 TCP connection to 1 host")],
            workspace.MemberRows.Select(row => (row.Name, row.Detail)));
        Assert.Equal("program1.exe · PID 101, 4 records · 1 TCP connection to 1 host. Press Enter to open it.",
            workspace.MemberRows[0].AccessibleName);
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
                ("program0.exe · PID 100", "1 record · 1 TCP connection to 1 host · whole session"),
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
        // Two hundred and ten programs of one process each, related in pairs, the later the busier: past the graph's budget
        // of nodes the least active fold into Other processes.
        ProcessGroup[] groups = [.. Enumerable.Range(0, 210)
            .Select(index => new ProcessGroup($"g{index:D3}", $"app{index:D3}.exe", LaneGrouping.Executable))];
        ProcessNode[] processes = [.. groups.Select((group, index) => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), 1_000 + index, $"app{index:D3}.exe", "test",
            group.Key, 0.5, 0.5, CoverageState.Covered) { Activity = [new MechanismCount(Mechanism.Tcp, 2 + index)] })];
        CommunicationEdge[] pairs = [.. Enumerable.Range(0, 105).Select(pair => new CommunicationEdge($"pair-{pair}",
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

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    /// <summary>
    /// Ten programs of one process each, program0.exe (PID 100) to program9.exe (PID 109), each connecting to a host of its
    /// own and sending to it once more than the one before; and three processes with no connection, quiet0.exe,
    /// QUIET1.EXE and quiet2.exe (PIDs 200 to 202). Every process starts within 0.12 µs, and every connection opens after
    /// 10 µs.
    /// </summary>
    internal static ObservationRowV1[] Rows()
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>();
        for (int index = 0; index < 10; index++)
        {
            rows.Add(Lifecycle(index, ObservationKind.Create, 100 + index, ordinal++)
                with { ResourceName = $@"C:\Apps\program{index}.exe", SessionRelativeTicks = 100 + index });
        }

        for (int index = 0; index < 3; index++)
        {
            rows.Add(Lifecycle(10 + index, ObservationKind.Create, 200 + index, ordinal++)
                with { ResourceName = index == 1 ? @"C:\APPS\QUIET1.EXE" : $@"C:\Apps\quiet{index}.exe", SessionRelativeTicks = 110 + index });
        }

        for (int index = 0; index < 10; index++)
        {
            string local = $"192.168.1.10:{52_000 + index}";
            string remote = $"52.0.0.{index + 1}:443";
            rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100 + index, ordinal)
                .Between(local, remote) with { SessionRelativeTicks = 10_000 + (long)ordinal });
            ordinal++;
            for (int send = 0; send <= index; send++)
            {
                rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 100 + index, ordinal)
                    .Between(local, remote) with { SessionRelativeTicks = 10_000 + (long)ordinal });
                ordinal++;
            }
        }

        return [.. rows];
    }
}
