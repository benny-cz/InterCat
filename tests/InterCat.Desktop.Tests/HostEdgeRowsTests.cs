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
/// The relationship table as the graph's whole table equivalent (R15): after the relationships it lists the graph's edges
/// to other hosts, each saying where its connections ran, who opened them and what Enter opens; chosen, one chooses its
/// source as a click on its edge does, and Enter opens what a double click on it does.
/// </summary>
public sealed class HostEdgeRowsTests
{
    [Fact(DisplayName = "R15: the relationship table lists the graph's edges to other hosts after its relationships, each saying where it ran, who opened it and what Enter opens")]
    public void TheTableListsTheEdgesToOtherHosts() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // The relationship first, then each edge the graph draws to other hosts, the busiest first.
        Assert.Equal("Whole machine · 1 relationship · 3 edges to other hosts", workspace.RelationshipTableScope);
        Assert.Equal(
            [
                ("client.exe · PID 100 → server.exe · PID 200", false),
                ("browser.exe · PID 300 → 142.250.1.1:443", true),
                ("server.exe · PID 200 ← 192.168.1.30", true),
                ("svc.exe · PID 400 ↔ [2001:db8::1]:53", true),
            ],
            workspace.TableRows.Select(row => (row.Name, row.ToHosts)));
        Assert.Same(workspace.Relationships[0], workspace.TableRows[0]);

        // Each says where its connections ran, who opened them, and what Enter opens, aloud as on screen.
        RelationshipRow browser = workspace.TableRows[1];
        Assert.Equal(("TCP", "3", "not counted"), (browser.Mechanism, browser.Observations, browser.KnownBytes));
        Assert.Equal("1 TCP connection to 1 host. Host: 142.250.1.1. Who is at it is not observed.", browser.Explanation);
        Assert.Equal("Direction: browser.exe · PID 300 connected to 142.250.1.1:443", browser.DirectionLine);
        Assert.Equal("its process's rung, which lists each connection", browser.Opens);
        Assert.Equal("browser.exe · PID 300 and 142.250.1.1:443 over TCP, browser.exe · PID 300 connected to 142.250.1.1:443, "
            + "3 observations, evidence correlated. 1 TCP connection to 1 host. Host: 142.250.1.1. Who is at it is not "
            + "observed. Press Enter to open its process's rung, which lists each connection.", browser.AccessibleName);
        Assert.Equal("Direction: 192.168.1.30 connected to server.exe · PID 200", workspace.TableRows[2].DirectionLine);
        Assert.Equal("Direction: display order only, not who initiated or sent", workspace.TableRows[3].DirectionLine);

        // Chosen, an edge to other hosts chooses its source, as a click on the edge does, and stays chosen in the table,
        // its line beneath saying it; the relationship table's relationship is not chosen.
        workspace.SelectedTableRow = browser;
        Assert.Equal(browser.SourceNode, workspace.SelectedGraphNodeKey);
        Assert.Equal("browser.exe · PID 300", workspace.SelectedProcess?.NameWithPid);
        Assert.Equal(browser.Key, workspace.SelectedTableRow?.Key);
        Assert.Null(workspace.SelectedRelationship);
        Assert.True(workspace.HasSelectedTableRow);
        Assert.Equal("1 TCP connection to 1 host. Host: 142.250.1.1. Who is at it is not observed. Direction: browser.exe · "
            + "PID 300 connected to 142.250.1.1:443. Enter opens its process's rung, which lists each connection.",
            workspace.SelectedTableRowExplanation);

        // Another node chosen in the graph lets it go, and its source chosen there again is no choice the table made; the
        // relationship chosen in the table is the table's choice.
        workspace.SelectGraphNode(workspace.GraphDisplay.NodeOf(Process(workspace, 100).Id)!.Key);
        Assert.Null(workspace.SelectedTableRow);
        workspace.SelectGraphNode(browser.SourceNode);
        Assert.Null(workspace.SelectedTableRow);
        workspace.SelectedTableRow = browser;
        workspace.SelectedTableRow = workspace.TableRows[0];
        Assert.Equal(workspace.TableRows[0].Key, workspace.SelectedRelationship?.Key);
        Assert.Same(workspace.SelectedRelationship, workspace.SelectedTableRow);
        Assert.Equal(workspace.SelectedRelationshipExplanation, workspace.SelectedTableRowExplanation);

        // Restated, as when the table's bytes are read, the edge chosen stays chosen.
        workspace.SelectedTableRow = workspace.TableRows[3];
        workspace.ShowTables = true;
        await workspace.SelectionBytesReady;
        Assert.Equal(workspace.TableRows[3].Key, workspace.SelectedTableRow?.Key);

        // The selection cleared lets it go, and so does a way down from the rung: E lists its process's records, as with
        // the process chosen, and the table there, which lists the rung's edges still, has none chosen.
        workspace.SelectedTableRow = browser;
        workspace.ClearSelection();
        Assert.Null(workspace.SelectedTableRow);
        workspace.SelectedTableRow = browser;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal("Records owned by browser.exe · PID 300", workspace.EvidenceScopeText);
        Assert.Contains(workspace.TableRows, row => row.Key == browser.Key);
        Assert.Null(workspace.SelectedTableRow);
        Assert.True(workspace.Ascend());

        // Enter opens what a double click on its edge opens: its process's rung, where the choice made at the rung left is
        // let go, though its process's own edge to the host is listed there too.
        workspace.SelectedTableRow = browser;
        Assert.True(workspace.OpenSelectedTableRow());
        Assert.Equal("Process: browser.exe · PID 300", workspace.Crumbs[^1].Label);
        Assert.Contains(workspace.TableRows, row => row.Key == browser.Key);
        Assert.Null(workspace.SelectedTableRow);
    });

    [Fact(DisplayName = "§3.2/R15: the way back from what an edge to other hosts opened or listed chooses its row again with its source, and a newer generation keeps it chosen")]
    public void TheWayBackChoosesTheEdgeAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Rows(), .. Fleet()]);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // E with the edge chosen lists its process's records, the table there choosing none; Esc back chooses it again
        // with its process, and asks for the keyboard to go to its row.
        RelationshipRow browser = workspace.TableRows.Single(row => row.Name == "browser.exe · PID 300 → 142.250.1.1:443");
        workspace.SelectedTableRow = browser;
        int asked = workspace.RelationshipAsked;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Null(workspace.SelectedTableRow);
        Assert.True(workspace.Ascend());
        Assert.Equal(browser.Key, workspace.SelectedTableRow?.Key);
        Assert.Equal("browser.exe · PID 300", workspace.SelectedProcess?.NameWithPid);
        Assert.Equal(asked + 1, workspace.RelationshipAsked);

        // Enter opens its process's rung through its group's: Esc back to the group's rung is no way back to it, and the
        // breadcrumb back to the machine's, which nothing there chose, chooses it again with its process.
        Assert.True(workspace.OpenSelectedTableRow());
        Assert.Equal("Process: browser.exe · PID 300", workspace.Crumbs[^1].Label);
        Assert.True(workspace.Ascend());
        Assert.Equal("Group: browser.exe", workspace.Crumbs[^1].Label);
        Assert.Null(workspace.SelectedTableRow);
        workspace.ClearSelection();
        workspace.ReturnTo(0);
        Assert.Equal(browser.Key, workspace.SelectedTableRow?.Key);
        Assert.Equal("browser.exe · PID 300", workspace.SelectedProcess?.NameWithPid);
        Assert.Equal(asked + 2, workspace.RelationshipAsked);

        // A group's edge opens its group's rung, and Esc back chooses it again with the group.
        RelationshipRow workers = workspace.TableRows.Single(row => row.Source == "worker.exe");
        workspace.SelectedTableRow = workers;
        Assert.True(workspace.OpenSelectedTableRow());
        Assert.Equal("Group: worker.exe", workspace.Crumbs[^1].Label);
        Assert.True(workspace.Ascend());
        Assert.Equal(workers.Key, workspace.SelectedTableRow?.Key);
        Assert.Equal(workers.SourceNode, workspace.SelectedGraphNodeKey);

        // A newer generation chooses it again while its source is still the selection, and not once it is not.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal(workers.Key, saved.ChosenHostEdge);
        using (WorkspaceViewModel newer = Open(session))
        {
            await newer.LayoutReady;
            Assert.Null(newer.RestoreNavigation(saved));
            Assert.Equal(workers.Key, newer.SelectedTableRow?.Key);
        }

        using (WorkspaceViewModel newer = Open(session))
        {
            await newer.LayoutReady;
            Assert.Null(newer.RestoreNavigation(saved with { SelectedRungKey = null }));
            Assert.Null(newer.SelectedTableRow);
        }

        // A relationship's choice is no edge's.
        workspace.SelectedTableRow = workspace.TableRows[0];
        Assert.Null(workspace.CaptureNavigation().ChosenHostEdge);
    });

    [Fact(DisplayName = "§3.2/R15: an aggregate's edge to other hosts stays chosen with a process chosen in its list, and the way back from that process's records, or a newer generation, chooses both again")]
    public void AnAggregatesEdgeKeepsItsListsChoice() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Spread());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // Enter on the aggregate's edge lists its processes in the inspector, the edge still chosen.
        RelationshipRow folded = workspace.TableRows.Single(row => row.Source == "Other hosts only");
        Assert.Equal(("Other hosts only → Other hosts · 2 hosts", "its processes in the inspector"), (folded.Name, folded.Opens));
        workspace.SelectedTableRow = folded;
        Assert.True(workspace.OpenSelectedTableRow());
        Assert.Equal(folded.Key, workspace.SelectedTableRow?.Key);

        // A process chosen in the list leaves it chosen; E lists that process's records, and Esc back chooses the
        // aggregate, the process in its list and the edge again.
        MemberRow member = workspace.MemberRows[0];
        workspace.ChosenMemberRow = member;
        Assert.Equal(folded.Key, workspace.SelectedTableRow?.Key);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(member.Process, workspace.ChosenMemberRow?.Process);
        Assert.Equal(folded.Key, workspace.SelectedTableRow?.Key);

        // A newer generation chooses all three again.
        using WorkspaceViewModel newer = Open(session);
        await newer.LayoutReady;
        Assert.Null(newer.RestoreNavigation(workspace.CaptureNavigation()));
        Assert.Equal(member.Process, newer.ChosenMemberRow?.Process);
        Assert.Equal(folded.Key, newer.SelectedTableRow?.Key);
    });

    [Fact(DisplayName = "R15: an edge to other hosts from a group or an aggregate says what Enter opens there: the group, or its processes' list")]
    public void AnAggregatesEdgeOpensWhatItsDoubleClickDoes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Rows(), .. Fleet()]);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // worker.exe's three processes each reach a host, drawn as their group's one node: Enter opens the group's rung.
        RelationshipRow workers = workspace.TableRows.Single(row => row.Source == "worker.exe");
        Assert.Equal(("worker.exe → Other hosts · 3 hosts", "its group"), (workers.Name, workers.Opens));
        Assert.Equal("Direction: worker.exe connected to Other hosts, in all 3 of its connections", workers.DirectionLine);
        workspace.SelectedTableRow = workers;
        Assert.Equal(workers.Key, workspace.SelectedTableRow?.Key);
        Assert.True(workspace.OpenSelectedTableRow());
        Assert.Equal("Group: worker.exe", workspace.Crumbs[^1].Label);

        // There, the rung's own process's edge opens nothing: its rows list each connection already.
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        RelationshipRow own = workspace.TableRows.Single(row => row.ToHosts);
        Assert.Equal(string.Empty, own.Opens);
        Assert.EndsWith(". Each process's rung lists its connections.", own.AccessibleName, StringComparison.Ordinal);
        workspace.SelectedTableRow = own;
        Assert.EndsWith(". Enter opens nothing here; each process's rung lists its connections.",
            workspace.SelectedTableRowExplanation, StringComparison.Ordinal);
        Assert.False(workspace.OpenSelectedTableRow());
    });

    [Fact(DisplayName = "R21: a table whose rung draws only edges to other hosts lists them, and says why it lists none only where it lists nothing")]
    public void ATableOfOnlyEdgesToOtherHostsListsThem() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 10 },
            Tcp(100, ObservationKind.Connect, 300, 100, "192.168.1.10:52000", "142.250.1.1:443"),
        ]);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        Assert.Equal("Whole machine · 0 relationships · 1 edge to other hosts", workspace.RelationshipTableScope);
        Assert.Equal("browser.exe · PID 300 → 142.250.1.1:443", Assert.Single(workspace.TableRows).Name);
        Assert.Null(workspace.TableRowsAbsent);
        Assert.StartsWith("No relationship here. ", workspace.RelationshipsAbsent, StringComparison.Ordinal);
    });

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ObservationRowV1 Tcp(long tick, ObservationKind kind, int pid, ulong ordinal, string local, string remote) =>
        Transfer(tick, kind, kind is ObservationKind.Send or ObservationKind.Receive
                ? kind == ObservationKind.Send ? AccountingSide.SendSide : AccountingSide.ReceiveSide
                : AccountingSide.EndpointActivity,
            kind is ObservationKind.Send or ObservationKind.Receive ? 100 : 0, pid, ordinal).Between(local, remote)
            with { SessionRelativeTicks = 1_000 + tick };

    /// <summary>
    /// client.exe connecting to server.exe over loopback; browser.exe connecting to 142.250.1.1:443; server.exe accepting a
    /// connection from 192.168.1.30 at its 8080; svc.exe's 400 sending datagrams to an IPv6 host's 53, and 401 only
    /// starting.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 10 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 20 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 30 },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Windows\System32\svc.exe", SessionRelativeTicks = 40 },
        Lifecycle(5, ObservationKind.Create, 401, 5) with { ResourceName = @"C:\Windows\System32\svc.exe", SessionRelativeTicks = 50 },
        Tcp(100, ObservationKind.Connect, 100, 100, "127.0.0.1:50000", "127.0.0.1:9000"),
        Tcp(101, ObservationKind.Send, 100, 101, "127.0.0.1:50000", "127.0.0.1:9000"),
        Tcp(102, ObservationKind.Receive, 200, 102, "127.0.0.1:9000", "127.0.0.1:50000"),
        Tcp(110, ObservationKind.Connect, 300, 110, "192.168.1.10:52000", "142.250.1.1:443"),
        Tcp(111, ObservationKind.Send, 300, 111, "192.168.1.10:52000", "142.250.1.1:443"),
        Tcp(112, ObservationKind.Send, 300, 112, "192.168.1.10:52000", "142.250.1.1:443"),
        Tcp(120, ObservationKind.Accept, 200, 120, "192.168.1.10:8080", "192.168.1.30:52001"),
        Tcp(121, ObservationKind.Receive, 200, 121, "192.168.1.10:8080", "192.168.1.30:52001"),
        Transfer(130, ObservationKind.Send, AccountingSide.SendSide, 60, 400, 130).Between("[2001:db8::10]:51000", "[2001:db8::1]:53")
            with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 1_130 },
    ];

    /// <summary>
    /// Ten programs' processes, each only connecting to a host of its own, each more quietly than the one before: the graph
    /// draws eight of them, and the two quietest as Other hosts only.
    /// </summary>
    private static ObservationRowV1[] Spread() =>
    [
        .. Enumerable.Range(0, 10).Select(index => Lifecycle(20 + index, ObservationKind.Create, 700 + index, (ulong)(20 + index))
            with { ResourceName = $@"C:\Apps\app{index}.exe", SessionRelativeTicks = 70 + index }),
        .. Enumerable.Range(0, 10).SelectMany(index => Enumerable.Range(0, 10 - index).Select(record => Tcp(300 + (index * 10) + record,
            record == 0 ? ObservationKind.Connect : ObservationKind.Send, 700 + index, (ulong)(300 + (index * 10) + record),
            $"192.168.1.10:{54000 + index}", $"30.0.0.{1 + index}:443"))),
    ];

    /// <summary>worker.exe's three processes, each connecting to a host of its own.</summary>
    private static ObservationRowV1[] Fleet() =>
    [
        .. Enumerable.Range(0, 3).Select(index => Lifecycle(10 + index, ObservationKind.Create, 600 + index, (ulong)(10 + index))
            with { ResourceName = @"C:\Tools\worker.exe", SessionRelativeTicks = 60 + index }),
        .. Enumerable.Range(0, 3).Select(index => Tcp(200 + index, ObservationKind.Connect, 600 + index, (ulong)(200 + index),
            $"192.168.1.10:{53000 + index}", $"20.0.0.{1 + index}:443")),
    ];
}
