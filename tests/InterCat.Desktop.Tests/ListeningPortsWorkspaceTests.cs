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
/// The session's listening ports, in the inspector while it describes nothing on the machine rung (§6.3): each port its
/// processes accepted connections at, with the process that accepted them and who connected, the busiest first, as `icat
/// ports` lists them, every one on request; Enter on one chooses that process, and Esc comes back to the port.
/// </summary>
public sealed class ListeningPortsWorkspaceTests
{
    [Fact(DisplayName = "§6.3: with nothing chosen, the inspector lists the ports the machine's processes accepted connections at, the busiest first, each with who connected, every one on request")]
    public void NothingChosenListsTheListeningPorts() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;
        // What the inspector last heard of them, so a binding is never left reading a stale list or visibility.
        var heard = new List<int>();
        var shown = new List<bool>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.ListenerRows))
            {
                heard.Add(workspace.ListenerRows.Count);
            }
            else if (changed.PropertyName == nameof(WorkspaceViewModel.ShowsListeners))
            {
                shown.Add(workspace.ShowsListeners);
            }
        };

        // Nothing chosen: the busiest five, each by its port and the process that accepted there, then how many and from
        // whom, as icat ports says it - one kind of end with the count, several each with their own.
        Assert.Equal("Nothing selected", workspace.SelectionTitle);
        Assert.True(workspace.ShowsListeners);
        Assert.Equal("6 listening ports, the busiest first", workspace.ListenersHeading);
        Assert.Equal("6 listening ports, the busiest first: the ports this machine's processes accepted connections at; Enter "
            + "on one chooses the process that accepted them", workspace.ListenersListName);
        Assert.Equal(
            [
                "7000 | helper.exe · PID\u00A0300 | 5 connections accepted · 4 from 4 processes of 4 programs · 1 from 10.0.0.6",
                "8080 | server.exe · PID\u00A0200 | 4 connections accepted from 3 processes of probe.exe, client.exe and helper.exe",
                "8443 | server.exe · PID\u00A0200 | 4 connections accepted from 3 hosts",
                "9000 | server.exe · PID\u00A0200 | 4 connections accepted from unobserved processes on this machine",
                "8080 | probe.exe · PID\u00A0500 | 1 connection accepted from client.exe · PID 100",
            ],
            workspace.ListenerRows.Select(Said));
        Assert.Equal("helper.exe · PID 300 accepted 5 connections at port 7000, 4 from 4 processes of 4 programs, 1 from "
            + "10.0.0.6. Press Enter to choose that process.", workspace.ListenerRows[0].AccessibleName);
        Assert.Equal("server.exe · PID 200 accepted 4 connections at port 8443, from 3 hosts. Press Enter to choose that "
            + "process.", workspace.ListenerRows[2].AccessibleName);
        Assert.Equal((ushort)8443, workspace.ListenerRows[2].Port);
        Assert.Equal(Process(workspace, 200).Id, workspace.ListenerRows[2].Process);

        // A button offers the sixth, a process that connected to itself, and then the busiest five again.
        Assert.True(workspace.OffersListenersToggle);
        Assert.Equal("Show all 6 listening ports", workspace.ListenersToggleLabel);
        Assert.True(workspace.ToggleListeners());
        Assert.True(workspace.ListsAllListeners);
        Assert.Equal("52011 | pipe.exe · PID\u00A0400 | 1 connection accepted from pipe.exe · PID 400 (itself)",
            Said(workspace.ListenerRows[5]));
        Assert.Equal(6, heard[^1]);
        Assert.Equal("Show the busiest 5", workspace.ListenersToggleLabel);
        Assert.False(workspace.ShowsListenersMore);
        Assert.False(workspace.ToggleListeners());
        Assert.False(workspace.ListsAllListeners);
        Assert.Equal(5, heard[^1]);

        // Anything chosen takes the list away, and letting it go brings it back: a process, a program, Other hosts, a
        // relationship, processes chosen together and a time range; so does a rung below the machine's.
        Action[] choices =
        [
            () => workspace.SelectProcess(Process(workspace, 100).Id),
            () => workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe"),
            () => workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key),
            () => workspace.SelectedRelationship = workspace.Relationships[0],
            () =>
            {
                workspace.ToggleGraphNodeInSelection(workspace.GraphDisplay.NodeOf(Process(workspace, 100).Id)!.Key);
                workspace.ToggleGraphNodeInSelection(workspace.GraphDisplay.NodeOf(Process(workspace, 300).Id)!.Key);
            },
            () => workspace.SelectInterval(workspace.Snapshot.Extent),
        ];
        foreach (Action choose in choices)
        {
            choose();
            Assert.NotEqual("Nothing selected", workspace.SelectionTitle);
            Assert.False(workspace.ShowsListeners, workspace.SelectionTitle);
            Assert.False(shown[^1], workspace.SelectionTitle);
            workspace.ClearSelection();
            Assert.True(workspace.ShowsListeners);
            Assert.True(shown[^1]);
        }

        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.True(workspace.Descend());
        workspace.ClearSelection();
        Assert.Equal("Nothing selected", workspace.SelectionTitle);
        Assert.False(workspace.ShowsListeners);
        Assert.True(workspace.Ascend());
        workspace.ClearSelection();
        Assert.True(workspace.ShowsListeners);
    });

    [Fact(DisplayName = "§6.3: Enter on a listening port chooses the process that accepted its connections, and Esc comes back to the port, in a newer generation too")]
    public void EnterChoosesAListenersProcessAndEscComesBack() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;
        ProcessInstanceId server = Process(workspace, 200).Id, client = Process(workspace, 100).Id;

        // The server's 8080 chooses the server, whose inspector says who connected to it, there.
        ListenerRow port = workspace.ListenerRows.Single(row => row.Port == 8080 && row.Process == server);
        Assert.True(workspace.ChooseListener(port));
        Assert.Equal(server, workspace.SelectedProcess?.Id);
        Assert.False(workspace.ShowsListeners);
        Assert.Contains(workspace.SelectionPeers, row => row.Peer == "← client.exe · PID 100" && row.Detail.Contains("TCP at 8080",
            StringComparison.Ordinal));

        // A port it lists no longer, chosen something, goes nowhere.
        Assert.False(workspace.ChooseListener(port));

        // Esc comes back: nothing is chosen again, and the port is the one returned, whose row the keyboard goes back to.
        Assert.Equal(new ListenerHop(8080, server, 0), workspace.ComeBackFromListener());
        Assert.Equal("Nothing selected", workspace.SelectionTitle);
        Assert.True(workspace.ShowsListeners);
        Assert.Null(workspace.ComeBackFromListener());

        // A way down the selection has since moved from is let go, the choice left where it is.
        Assert.True(workspace.ChooseListener(port));
        workspace.SelectProcess(client);
        Assert.Null(workspace.ComeBackFromListener());
        Assert.Equal(client, workspace.SelectedProcess?.Id);
        workspace.ClearSelection();

        // So is one whose rung the ladder has since left, its process still the one chosen.
        Assert.True(workspace.ChooseListener(port));
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.NodeOf(server)!.Key));
        Assert.Equal(server, workspace.SelectedProcess?.Id);
        Assert.Null(workspace.ComeBackFromListener());
        while (workspace.Ascend())
        {
        }

        workspace.ClearSelection();

        // A port the list does not hold, the sixth while the busiest five are named, is none to choose.
        ListenerRow itself = new("pipe.exe · PID 400", string.Empty, string.Empty)
        {
            Port = 52011,
            Process = Process(workspace, 400).Id,
        };
        Assert.False(workspace.ChooseListener(itself));
        Assert.Null(workspace.SelectedProcess);

        // A newer generation keeps the way back, and the list asked for whole.
        Assert.True(workspace.ToggleListeners());
        Assert.True(workspace.ChooseListener(workspace.ListenerRows.Single(row => row.Port == 52011)));
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal((new ListenerHop(52011, Process(workspace, 400).Id, 0), true), (saved.ListenerHop, saved.ListenersListedWhole));
        using WorkspaceViewModel next = Open(session, 2);
        await next.LayoutReady;
        Assert.Null(next.RestoreNavigation(saved));
        Assert.Equal(400, next.SelectedProcess?.ProcessId);
        Assert.Equal(new ListenerHop(52011, Process(next, 400).Id, 0), next.ComeBackFromListener());
        Assert.True(next.ListsAllListeners);
        Assert.Equal(6, next.ListenerRows.Count);
    });

    [Fact(DisplayName = "§6.3: a session whose processes accepted no connection lists no port, and one with more than a hundred says where the rest are read")]
    public void ListeningPortsSayWhatTheyLeaveOut() => SingleThreadedContext.Run(async () =>
    {
        // The client connected to another host and to nothing else: no port is listed, and nothing is offered.
        using var outbound = new TemporarySession();
        Publish(outbound.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe" },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100).Between("192.168.1.10:52000", "203.0.113.1:443"),
        ]);
        using (WorkspaceViewModel workspace = Open(outbound, 1))
        {
            await workspace.LayoutReady;
            Assert.Equal("Nothing selected", workspace.SelectionTitle);
            Assert.False(workspace.ShowsListeners);
            Assert.Empty(workspace.ListenerRows);
            Assert.False(workspace.OffersListenersToggle);
            Assert.False(workspace.ToggleListeners());
        }

        // One port is said as one; a hundred and two are listed a hundred at most, and the rest said where they are read.
        using var one = new TemporarySession();
        Publish(one.Store,
        [
            Lifecycle(1, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Apps\server.exe" },
            Accepted(10, 200, "192.168.1.10:445", "10.0.0.6:50000"),
        ]);
        using (WorkspaceViewModel workspace = Open(one, 1))
        {
            await workspace.LayoutReady;
            Assert.Equal("1 listening port", workspace.ListenersHeading);
            Assert.Equal("445 | server.exe · PID\u00A0200 | 1 connection accepted from 10.0.0.6", Said(workspace.ListenerRows.Single()));
            Assert.False(workspace.OffersListenersToggle);
        }

        using var many = new TemporarySession();
        Publish(many.Store,
        [
            Lifecycle(1, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Apps\server.exe" },
            .. Enumerable.Range(0, 102).Select(index => Accepted(10 + index, 200, $"192.168.1.10:{9000 + index}", "10.0.0.6:50000")),
        ]);
        using (WorkspaceViewModel workspace = Open(many, 1))
        {
            await workspace.LayoutReady;
            Assert.Equal("102 listening ports, the busiest first", workspace.ListenersHeading);
            Assert.Equal("Show the busiest 100 of 102 listening ports", workspace.ListenersToggleLabel);
            Assert.False(workspace.ShowsListenersMore);
            Assert.True(workspace.ToggleListeners());
            Assert.Equal(100, workspace.ListenerRows.Count);
            Assert.True(workspace.ShowsListenersMore);
            Assert.Equal("2 more, quieter: icat ports --limit 102 lists them all", workspace.ListenersMore);
        }
    });

    private static WorkspaceViewModel Open(TemporarySession session, int generation)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), $"{overview.GraphIdentity}:{generation}",
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    /// <summary>
    /// A listening port's row as the inspector shows it: its port's column, then its process, its PID kept to its number by a
    /// no-break space, and who connected.
    /// </summary>
    private static string Said(ListenerRow row) => $"{row.PortText} | {row.Listener} | {row.Detail}";

    /// <summary>
    /// icat ports' session: helper.exe's PID 300 accepts at its 7000 from four processes of the capture and from 10.0.0.6;
    /// server.exe's PID 200 at its 8080 from three processes, at its 8443 from three other hosts, and at its 9000 from ends
    /// on this machine no record holds; probe.exe's PID 500 at its 8080 from the client; and pipe.exe's PID 400 connects
    /// to itself. A datagram flow, and a connection made to another host, are no one's listening.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Apps\server.exe" },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\helper.exe" },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\pipe.exe" },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Apps\probe.exe" },
        .. Connection(10, 100, "127.0.0.1:50000", 200, "127.0.0.1:8080"),
        .. Connection(20, 500, "127.0.0.1:50001", 200, "127.0.0.1:8080"),
        .. Connection(30, 500, "127.0.0.1:50002", 200, "127.0.0.1:8080"),
        .. Connection(35, 300, "127.0.0.1:50007", 200, "127.0.0.1:8080"),
        Accepted(40, 200, "192.168.1.10:8443", "10.0.0.9:51000"),
        Accepted(41, 200, "192.168.1.10:8443", "10.0.0.10:51000"),
        Accepted(42, 200, "192.168.1.10:8443", "10.0.0.20:51000"),
        Accepted(43, 200, "192.168.1.10:8443", "10.0.0.20:51001"),
        Accepted(50, 200, "127.0.0.1:9000", "127.0.0.1:52000"),
        Accepted(51, 200, "[::1]:9000", "[::1]:52001"),
        Accepted(52, 200, "[::ffff:192.168.1.10]:9000", "[::ffff:127.0.0.2]:52002"),
        Accepted(53, 200, "192.168.1.10:9000", "192.168.1.10:52003"),
        .. Connection(60, 200, "127.0.0.1:50003", 300, "127.0.0.1:7000"),
        .. Connection(63, 100, "127.0.0.1:50005", 300, "127.0.0.1:7000"),
        .. Connection(66, 500, "127.0.0.1:50008", 300, "127.0.0.1:7000"),
        .. Connection(70, 400, "127.0.0.1:50009", 300, "127.0.0.1:7000"),
        Accepted(73, 300, "192.168.1.10:7000", "10.0.0.6:51002"),
        .. Connection(75, 100, "127.0.0.1:50006", 500, "127.0.0.2:8080"),
        .. Connection(80, 400, "127.0.0.1:52010", 400, "127.0.0.1:52011"),
        Transfer(92, ObservationKind.Send, AccountingSide.SendSide, 4, 100).Between("127.0.0.1:53100", "127.0.0.1:5353")
            with { Mechanism = Mechanism.Udp, EventId = 42 },
        Transfer(93, ObservationKind.Receive, AccountingSide.ReceiveSide, 4, 300).Between("127.0.0.1:53100", "127.0.0.1:5353")
            with { Mechanism = Mechanism.Udp, EventId = 42 },
        Transfer(94, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100).Between("192.168.1.10:52020", "203.0.113.1:443"),
    ];

    /// <summary>One TCP connection <paramref name="maker"/> made to <paramref name="taker"/>, opened at its end, and a send across it.</summary>
    private static ObservationRowV1[] Connection(long at, int maker, string from, int taker, string to) =>
    [
        Transfer(at, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker).Between(from, to),
        Transfer(at + 1, ObservationKind.Send, AccountingSide.SendSide, 8, maker).Between(from, to),
        Transfer(at + 2, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, taker).Between(to, from),
    ];

    /// <summary>A connection <paramref name="taker"/> accepted at <paramref name="local"/>, whose other end no record holds.</summary>
    private static ObservationRowV1 Accepted(long at, int taker, string local, string remote) =>
        Transfer(at, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, taker).Between(local, remote);
}
