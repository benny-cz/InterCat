using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The window's grouping (§6.3): the overview, projected by executable, regrouped by the terminal session each process's
/// lifecycle records name, without reading a record again, and the ladder and its totals over either.
/// </summary>
public sealed class WorkspaceGroupingTests
{
    [Fact(DisplayName = "§6.3: regrouped by terminal session, the machine rung lists each session's processes together and those naming none apart, with the same total")]
    public void RegroupsBySession()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        using var session = new TemporarySession();
        Publish(session.Store, rows, fields: fields);
        WorkspaceSnapshot projected = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        Assert.Equal([LaneGrouping.Executable, LaneGrouping.UserSession], WorkspaceGrouping.Offered(projected));
        Assert.Equal(LaneGrouping.Executable, WorkspaceGrouping.Of(projected));
        Assert.Same(projected, WorkspaceGrouping.Regroup(projected, LaneGrouping.Executable));

        // The sessions in their order, then the processes whose records named none, each named as icat names it.
        WorkspaceSnapshot regrouped = WorkspaceGrouping.Regroup(projected, LaneGrouping.UserSession);
        Assert.Equal(LaneGrouping.UserSession, WorkspaceGrouping.Of(regrouped));
        Assert.Equal(
            [
                ("session:0", "Terminal session 0"), ("session:1", "Terminal session 1"), ("session:2", "Terminal session 2"),
                ("session:unknown", "Terminal session not recorded"),
            ],
            regrouped.Groups.Select(group => (group.Key, group.Name)));
        Assert.All(regrouped.Groups, group => Assert.Equal(LaneGrouping.UserSession, group.Kind));
        Assert.Equal(
            [(100, 1u, "session:1"), (101, 2u, "session:2"), (200, 1u, "session:1"), (300, 0u, "session:0"), (400, (uint?)null, "session:unknown")],
            regrouped.Processes.OrderBy(process => process.ProcessId)
                .Select(process => (process.ProcessId, process.TerminalSession, process.GroupKey)));

        // Only the grouping changes: the same instances with the same records, and the same relationships.
        Assert.Equal(
            projected.Processes.Select(process => (process.Id, process.Records)),
            regrouped.Processes.Select(process => (process.Id, process.Records)));
        Assert.Equal(projected.Edges, regrouped.Edges);

        // The machine rung's rows are the sessions, each its members' records together, and they total what the
        // executables total.
        LadderView byExecutable = LadderProjection.Project(projected, SyntheticWorkspace.Root(projected));
        NavigationState root = SyntheticWorkspace.Root(regrouped);
        LadderView bySession = LadderProjection.Project(regrouped, root);
        Assert.Equal(byExecutable.ObservationCount, bySession.ObservationCount);
        Assert.Equal(4, bySession.Rows.Count);
        LadderRow first = bySession.Rows.Single(row => row.Key == "session:1");
        Assert.Equal(
            regrouped.Processes.Where(process => process.ProcessId is 100 or 200).Sum(process => process.Records),
            first.ObservationCount);
        Assert.Equal("2 process instances", first.Detail);

        // A session's rung lists exactly its processes, and the breadcrumb names the session.
        var ladder = new DetailLadder(root);
        Assert.True(ladder.TryDescend(LadderProjection.DescentFor(first, root, regrouped.Extent), out _));
        Assert.Equal("Machine › Group: Terminal session 1", LadderProjection.Breadcrumb(ladder));
        Assert.Equal(
            [100, 200],
            LadderProjection.Project(regrouped, ladder.Current).Rows
                .Select(row => regrouped.Processes.Single(process => process.Id.ToString() == row.Key).ProcessId)
                .Order());

        // Only the projection, grouped by executable, is regrouped; the graph's identity names the grouping.
        Assert.Throws<ArgumentException>(() => WorkspaceGrouping.Regroup(regrouped, LaneGrouping.Executable));
        Assert.Equal("session:a", WorkspaceGrouping.Identity("session:a", LaneGrouping.Executable));
        Assert.NotEqual("session:a", WorkspaceGrouping.Identity("session:a", LaneGrouping.UserSession));
    }

    [Fact(DisplayName = "§6.3: a session whose lifecycle records name no terminal session offers grouping by executable alone")]
    public void NoSessionIsNoChoice()
    {
        (ObservationRowV1[] rows, _) = TerminalSessions();
        using var session = new TemporarySession();
        Publish(session.Store, rows);
        WorkspaceSnapshot projected = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        Assert.All(projected.Processes, process => Assert.Null(process.TerminalSession));
        Assert.Equal([LaneGrouping.Executable], WorkspaceGrouping.Offered(projected));
        Assert.Equal([LaneGrouping.Executable], WorkspaceGrouping.Offered(SyntheticWorkspace.Create()));
    }

    [Fact(DisplayName = "§6.3: regrouped by service container, each host of the services listed is a row of its own named by its services, every other process its executable's row, those a listed service may have run in one row apart, under keys the rankings read, with the same total")]
    public void RegroupsByServiceContainer()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields, ServiceInventoryV1[] inventories) = ServiceContainers();
        using var session = new TemporarySession();
        Publish(session.Store, rows, fields: fields, services: inventories);
        WorkspaceSnapshot projected = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        Assert.Equal([LaneGrouping.Executable, LaneGrouping.ServiceContainer], WorkspaceGrouping.Offered(projected));

        // The two hosts of services, named as icat metric names them; then the executables of the processes hosting none,
        // svchost.exe's other process among them; then helper.exe, which a listed service may have run in.
        WorkspaceSnapshot regrouped = WorkspaceGrouping.Regroup(projected, LaneGrouping.ServiceContainer);
        Assert.Equal(LaneGrouping.ServiceContainer, WorkspaceGrouping.Of(regrouped));
        ProcessNode Process(int pid) => regrouped.Processes.Single(process => process.ProcessId == pid);
        Assert.Equal(
            [
                (WorkspaceGrouping.ContainerKey(Process(1820).Id), "PID 1820 svchost.exe hosting Dnscache, nsi", "Dnscache,nsi"),
                (WorkspaceGrouping.ContainerKey(Process(2400).Id), "PID 2400 svchost.exe hosting W32Time", "W32Time"),
            ],
            regrouped.Groups.Where(group => group.Services is not null)
                .Select(group => (group.Key, group.Name, string.Join(',', group.Services!.Select(service => service.Name)))));
        Assert.All(regrouped.Groups, group => Assert.Equal(LaneGrouping.ServiceContainer, group.Kind));
        Assert.Equal(
            [
                (1820, WorkspaceGrouping.ContainerKey(Process(1820).Id)),
                (2400, WorkspaceGrouping.ContainerKey(Process(2400).Id)),
                (2500, WorkspaceGrouping.ContainerExecutableKey(projected.Processes.Single(process => process.ProcessId == 2500).GroupKey)),
                (3000, WorkspaceGrouping.ContainerExecutableKey(projected.Processes.Single(process => process.ProcessId == 3000).GroupKey)),
                (3100, WorkspaceGrouping.ServiceHostUnknownKey),
                (3200, WorkspaceGrouping.ContainerExecutableKey(ProcessGroup.UnwitnessedExecutableKey)),
            ],
            regrouped.Processes.OrderBy(process => process.ProcessId).Select(process => (process.ProcessId, process.GroupKey)));
        Assert.Equal(GroupingText.ServiceHostNotKnown, regrouped.Groups.Single(group => group.Key == WorkspaceGrouping.ServiceHostUnknownKey).Name);
        Assert.Equal(
            projected.Groups.Single(group => group.Key == Process(2500).GroupKey.Replace("container|", string.Empty, StringComparison.Ordinal)).Name,
            regrouped.Groups.Single(group => group.Key == Process(2500).GroupKey).Name);
        Assert.Equal(projected.Edges, regrouped.Edges);

        // The machine rung's rows are the groups, and they total what the executables total.
        LadderView byExecutable = LadderProjection.Project(projected, SyntheticWorkspace.Root(projected));
        LadderView byContainer = LadderProjection.Project(regrouped, SyntheticWorkspace.Root(regrouped));
        Assert.Equal(byExecutable.ObservationCount, byContainer.ObservationCount);
        Assert.Equal(regrouped.Groups.Count, byContainer.Rows.Count);
        Assert.Equal("1 process instance", byContainer.Rows.Single(row => row.Key == WorkspaceGrouping.ContainerKey(Process(1820).Id)).Detail);

        // The peer ranking keys its groups as the regrouped rows are keyed: each host's peer is the other host, and the
        // processes of svchost.exe that host none are not its hosts' group.
        SessionPeerMeasures peers = SessionPeerRanking.Measure(session.Store, null);
        Assert.Equal(1L, peers.ByGroup[WorkspaceGrouping.ContainerKey(Process(1820).Id)].Peers);
        Assert.Equal(1L, peers.ByGroup[WorkspaceGrouping.ContainerKey(Process(2400).Id)].Peers);
        WorkspaceSnapshot measured = OverviewWorkspace.WithPeers(regrouped, peers);
        Assert.Equal(1L, measured.Groups.Single(group => group.Key == WorkspaceGrouping.ContainerKey(Process(1820).Id)).Peers!.Peers);
        Assert.Equal(2L, peers.ByGroup[projected.Processes.Single(process => process.ProcessId == 2500).GroupKey].Peers);
        Assert.True(peers.ByGroup.GetValueOrDefault(Process(2500).GroupKey)?.Peers is null or 0);
        Assert.Equal(1L, peers.ByGroup[WorkspaceGrouping.ServiceHostUnknownKey].Unknown);

        // The graph's identity names the grouping, and a capture that listed no services does not offer it.
        Assert.NotEqual(WorkspaceGrouping.Identity("session:a", LaneGrouping.UserSession), WorkspaceGrouping.Identity("session:a", LaneGrouping.ServiceContainer));
        using var unlisted = new TemporarySession();
        Publish(unlisted.Store, rows, fields: fields);
        Assert.Equal([LaneGrouping.Executable], WorkspaceGrouping.Offered(OverviewWorkspace.From(SessionOverviewProjector.Project(unlisted.Store))));
    }
}
