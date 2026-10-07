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
}
