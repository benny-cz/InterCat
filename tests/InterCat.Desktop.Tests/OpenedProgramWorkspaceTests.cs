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
/// A program's rung, with nothing chosen there, describes the program it opened (§6.3, R15), as an opened channel's rung
/// describes its channel: what choosing it on the machine rung said - its processes, how they were grouped, the connections
/// they made and accepted, the programs they talked with, the other hosts they reached and the ports they accepted
/// connections at - described, not chosen, so nothing is highlighted for it and the ways back from its lists come back to
/// nothing chosen.
/// </summary>
public sealed class OpenedProgramWorkspaceTests
{
    [Fact(DisplayName = "§6.3: a program's rung, with nothing chosen there, describes the program it opened, with its pairs, hosts and listening ports, each listed whole on its own")]
    public void AProgramsRungDescribesTheProgramItOpened() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;

        // Chosen on the machine rung, the hub says what it is and lists what its processes did.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "hub.exe");
        string grouping = workspace.GroupingExplanation;
        string connections = workspace.SelectionConnections;
        string[] pairs = [.. workspace.SelectionPrograms.Select(row => row.Pair)];
        string[] hosts = [.. workspace.ReachedHostRows.Select(row => row.Address)];
        string[] ports = [.. workspace.ListenerRows.Select(Said)];
        Assert.StartsWith("Grouped by the executable", grouping, StringComparison.Ordinal);
        Assert.Equal("7 made · 7 accepted at ports 8001, 8002, 8003 and 4 more, as the capture saw them open", connections);
        Assert.Equal(5, pairs.Length);
        Assert.Equal(5, hosts.Length);
        Assert.Equal(5, ports.Length);

        // Opened, its rung says the same of it, its processes drawn there on their own, and chooses nothing: no group is
        // chosen and no node is highlighted for it.
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;
        Assert.Equal("Group: hub.exe", workspace.Crumbs[^1].Label);
        Assert.Equal("hub.exe", workspace.SelectionTitle);
        Assert.Equal("2 processes · 2 drawn on their own" + Environment.NewLine + @"C:\Apps\hub.exe", workspace.SelectionSubtitle);
        Assert.Null(workspace.SelectedGroup);
        Assert.Empty(workspace.SelectedGraphNodeKeys);
        Assert.Equal(grouping, workspace.GroupingExplanation);
        Assert.Equal(connections, workspace.SelectionConnections);
        Assert.Equal(pairs, workspace.SelectionPrograms.Select(row => row.Pair));
        Assert.Equal(hosts, workspace.ReachedHostRows.Select(row => row.Address));
        Assert.Equal(ports, workspace.ListenerRows.Select(Said));
        Assert.Equal("7 listening ports of its processes, the busiest first", workspace.ListenersHeading);

        // Each list is asked for whole on its own, there as on the machine rung.
        Assert.True(workspace.TogglePrograms());
        Assert.Equal(7, workspace.SelectionPrograms.Count);
        Assert.True(workspace.ToggleReachedHosts());
        Assert.Equal(7, workspace.ReachedHostRows.Count);
        Assert.True(workspace.ToggleListeners());
        Assert.Equal(7, workspace.ListenerRows.Count);

        // A process chosen there is described instead, without the program's lists; letting it go describes the program
        // again, its lists still whole.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "PID 201");
        Assert.Equal(201, workspace.SelectedProcess?.ProcessId);
        Assert.StartsWith("PID 201", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal(string.Empty, workspace.GroupingExplanation);
        Assert.False(workspace.ShowsPrograms);
        Assert.False(workspace.ShowsListeners);
        workspace.ClearSelection();
        Assert.Equal(("hub.exe", true, true, true), (workspace.SelectionTitle, workspace.ListsAllPrograms,
            workspace.ListsAllReachedHosts, workspace.ListsAllListeners));

        // Back on the machine rung the hub is chosen, as the way down went through it.
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        Assert.Equal("hub.exe", workspace.SelectedGroup?.Name);
        workspace.ClearSelection();
        Assert.Equal("Nothing selected", workspace.SelectionTitle);

        // A process's rung, reached from the hub's, describes no program with nothing chosen there.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "hub.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "PID 200");
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;
        workspace.ClearSelection();
        Assert.Equal(3, workspace.Crumbs.Count);
        Assert.Equal("Nothing selected", workspace.SelectionTitle);
        Assert.Equal(string.Empty, workspace.GroupingExplanation);
        Assert.False(workspace.ShowsListeners);
    });

    [Fact(DisplayName = "§6.3: at a program's rung, Enter on one of its ports or pairs chooses the process or program, and Esc comes back to the program described, not chosen")]
    public void TheOpenedProgramsListsComeBackToItDescribed() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;
        string hub = workspace.RungRows.Single(row => row.Label == "hub.exe").Key;
        string helper = workspace.RungRows.Single(row => row.Label == "helper.exe").Key;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "hub.exe");
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;

        // A port chooses the process that accepted its connections; Esc describes the program again, its port the one returned.
        ListenerRow port = workspace.ListenerRows.Single(row => row.Port == 8001);
        Assert.True(workspace.ChooseListener(port));
        Assert.Equal(200, workspace.SelectedProcess?.ProcessId);
        Assert.Equal(new ListenerHop(8001, port.Process, 1) { Program = hub }, workspace.ComeBackFromListener());
        Assert.Equal(("hub.exe", null), (workspace.SelectionTitle, workspace.SelectedGroup?.Name));
        Assert.Empty(workspace.SelectedGraphNodeKeys);
        Assert.Contains(workspace.ListenerRows, row => row.Port == 8001);

        // A pair the hub's process made, the quietest by name, chooses the program at its other end, whose own pairs are then
        // listed; Esc describes the hub again.
        Assert.True(workspace.TogglePrograms());
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms.Single(row => row.Pair == "hub.exe → helper.exe")));
        Assert.Equal(("helper.exe", "helper.exe"), (workspace.SelectionTitle, workspace.SelectedGroup?.Name));
        Assert.Equal(helper, workspace.ComeBackFromProgram());
        Assert.Equal(("hub.exe", null), (workspace.SelectionTitle, workspace.SelectedGroup?.Name));
        Assert.Empty(workspace.SelectedGraphNodeKeys);

        // A pair made to it goes the other way, and Esc comes back the same.
        Assert.True(workspace.ChooseProgram(workspace.SelectionPrograms.Single(row => row.Pair == "a1.exe → hub.exe")));
        Assert.Equal("a1.exe", workspace.SelectedGroup?.Name);
        Assert.NotNull(workspace.ComeBackFromProgram());
        Assert.Equal(("hub.exe", null), (workspace.SelectionTitle, workspace.SelectedGroup?.Name));
    });

    [Fact(DisplayName = "§6.3: a program's rung says where the rest of the program's hosts and pairs are read, by the program's name, past the most it lists")]
    public void AnOpenedProgramSaysWhereTheRestAreRead() => SingleThreadedContext.Run(async () =>
    {
        // The hub reaches a hundred and two hosts, and a hundred and two programs connect to it.
        using var session = new TemporarySession();
        var rows = new List<ObservationRowV1> { Lifecycle(1, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Apps\hub.exe" } };
        for (int index = 0; index < 102; index++)
        {
            rows.Add(Lifecycle(2 + index, ObservationKind.Create, 1000 + index, (ulong)(2 + index))
                with { ResourceName = $@"C:\Apps\p{index:D3}.exe" });
            rows.AddRange(Connection(1000 + (10 * index), 1000 + index, $"127.0.0.1:{40000 + index}", 200, "127.0.0.1:8000"));
            rows.Add(Transfer(5000 + index, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 200)
                .Between($"192.168.1.10:{52000 + index}", $"198.51.100.{1 + index}:443"));
        }

        Publish(session.Store, [.. rows]);
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "hub.exe");
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;

        Assert.True(workspace.ToggleReachedHosts());
        Assert.Equal("2 more, quieter: icat hosts --program hub.exe lists them all", workspace.ReachedHostsMore);
        Assert.True(workspace.TogglePrograms());
        Assert.Equal("2 more, quieter: icat programs --program hub.exe lists them all", workspace.ProgramsMore);
    });

    private static WorkspaceViewModel Open(TemporarySession session, int generation)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), $"{overview.GraphIdentity}:{generation}",
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static string Said(ListenerRow row) => $"{row.PortText} | {row.Listener} | {row.Detail}";

    /// <summary>
    /// The hub's two processes: six programs each connect to the first at a port of its own, and another host connects to it
    /// at 8443; the second connects to the helper's 7000 and reaches six other hosts.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Apps\hub.exe" },
            Lifecycle(2, ObservationKind.Create, 201, 2) with { ResourceName = @"C:\Apps\hub.exe" },
            Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\helper.exe" },
        };
        for (int index = 1; index <= 6; index++)
        {
            rows.Add(Lifecycle(3 + index, ObservationKind.Create, 100 + index, (ulong)(3 + index))
                with { ResourceName = $@"C:\Apps\a{index}.exe" });
            rows.AddRange(Connection(100 + (10 * index), 100 + index, $"127.0.0.1:{50000 + index}", 200, $"127.0.0.1:{8000 + index}"));
            rows.Add(Transfer(200 + index, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 201)
                .Between($"192.168.1.10:{52000 + index}", $"203.0.113.{index}:443"));
        }

        rows.Add(Transfer(300, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200)
            .Between("192.168.1.10:8443", "10.0.0.9:51000"));
        rows.AddRange(Connection(310, 201, "127.0.0.1:50100", 300, "127.0.0.1:7000"));
        return [.. rows];
    }

    /// <summary>One TCP connection <paramref name="maker"/> made to <paramref name="taker"/>, opened at its end, and a send across it.</summary>
    private static ObservationRowV1[] Connection(long at, int maker, string from, int taker, string to) =>
    [
        Transfer(at, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker).Between(from, to),
        Transfer(at + 1, ObservationKind.Send, AccountingSide.SendSide, 8, maker).Between(from, to),
        Transfer(at + 2, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, taker).Between(to, from),
    ];
}
