using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The window's processes grouped by terminal session (§6.3): each session is named, in icat's words, wherever a group is
/// - the ranked rows, the graph's folded node and its card, the inspector's explanation, a search hit and the evidence step
/// - and the processes whose lifecycle records named none are a group of their own, never placed in a guessed session.
/// </summary>
public sealed class GroupingTests
{
    private const string Bound = " Each member's own records are bound to it by process-binding, version 4.";
    private const string Coverage = " Coverage over the session: unknown.";

    [Fact(DisplayName = "§6.3: grouped by terminal session, a busy session folds into one graph node its card names, and the inspector, a search and the evidence step name the session")]
    public void ASessionIsNamedWhereverItIsShown() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = Workers(30);
        Publish(session.Store, rows, fields: fields);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot projected = OverviewWorkspace.From(overview);
        var source = new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation);

        // By executable, each worker is a group of one, drawn on its own, and the selector offers both groupings.
        using (var byExecutable = new WorkspaceViewModel(projected, overview.GraphIdentity, source))
        {
            await byExecutable.LayoutReady;
            Assert.Equal(["Executable", "Terminal session"], byExecutable.GroupingOptions.Select(option => option.Label));
            Assert.Equal(LaneGrouping.Executable, byExecutable.SelectedGrouping.Grouping);
            Assert.True(byExecutable.ShowsGroupingChoice);
            Assert.DoesNotContain(byExecutable.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Group);
        }

        using var workspace = new WorkspaceViewModel(WorkspaceGrouping.Regroup(projected, LaneGrouping.UserSession),
            WorkspaceGrouping.Identity(overview.GraphIdentity, LaneGrouping.UserSession), source);
        await workspace.LayoutReady;
        Assert.Equal(LaneGrouping.UserSession, workspace.Grouping);
        Assert.Equal("Terminal session", workspace.SelectedGrouping.Label);
        Assert.Equal(
            ["Terminal session 0", "Terminal session 1", "Terminal session not recorded"],
            workspace.RungRows.Select(row => row.Label).Order(StringComparer.Ordinal));

        // Session 1's thirty workers are more than a group draws one by one, so the graph folds them into the session's
        // node, and its card says what they share.
        GraphDisplayNode folded = Assert.Single(workspace.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Group);
        Assert.Equal(("Terminal session 1", 30), (folded.Label, folded.Members.Count));
        HoverCard card = workspace.DescribeGraphHover(folded.Key)!;
        Assert.Equal("30 processes of this terminal session, drawn as one node · 30 relationships", card.Lines[0]);

        // The inspector says how each group was formed, the unrecorded apart under no guessed session.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "Terminal session 1");
        Assert.Equal(("How its processes are grouped", "Grouped by the terminal session its members' lifecycle records name."
            + Bound + Coverage), (workspace.ExplanationHeading, workspace.Explanation));
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "Terminal session not recorded");
        Assert.Equal("Grouped here because no lifecycle record names the terminal session its members ran in: none is given "
            + "a guessed one." + Bound + Coverage, workspace.Explanation);

        // A search names a session's hit by what its members share.
        workspace.SearchText = "terminal session 1";
        SearchHit hit = workspace.SearchResults[0].Hit;
        Assert.Equal(("Terminal session 1", "Terminal session · 30 processes"), (hit.Label, hit.Detail));
        workspace.SearchText = string.Empty;

        // Evidence from a selected session reads exactly its processes' records, and says it was reached from it.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "Terminal session 0");
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal("Evidence was reached from the machine rung with this terminal session group selected.",
            workspace.Filters.Single().Reason);
        Assert.StartsWith("Records owned by the 1 instance of Terminal session 0", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.False(workspace.ShowsGroupingChoice);
    });

    [Fact(DisplayName = "§6.3: the selector stands at the machine rung of a session that names a terminal session, and nowhere else")]
    public void TheSelectorStandsWhereTheGroupsAre() => SingleThreadedContext.Run(async () =>
    {
        using var named = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(named.Store, rows, fields: fields);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(named.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(named.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        Assert.True(workspace.ShowsGroupingChoice);

        // A group's rung lists its processes, which the grouping does not change, so it offers no choice.
        var changes = new List<string?>();
        workspace.PropertyChanged += (_, changed) => changes.Add(changed.PropertyName);
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        Assert.False(workspace.ShowsGroupingChoice);
        Assert.Contains(nameof(WorkspaceViewModel.ShowsGroupingChoice), changes);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.ShowsGroupingChoice);

        // A session whose lifecycle records name no session has one grouping, and the tour none to choose.
        using var unnamed = new TemporarySession();
        Publish(unnamed.Store, rows);
        SessionOverviewBundle plain = SessionOverviewProjector.Project(unnamed.Store);
        using var single = new WorkspaceViewModel(OverviewWorkspace.From(plain), plain.GraphIdentity,
            new SessionEvidenceSource(unnamed.Path, plain.SessionId, plain.Generation));
        Assert.Equal(["Executable"], single.GroupingOptions.Select(option => option.Label));
        Assert.False(single.ShowsGroupingChoice);
        using var tour = new WorkspaceViewModel();
        Assert.False(tour.ShowsGroupingChoice);
    });

    /// <summary>
    /// <paramref name="count"/> workers, each its own executable, in terminal session 1, each calling the service in session
    /// 0; and a tool whose lifecycle records name no session, sending to an address nothing in the capture holds.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) Workers(int count)
    {
        ObservationRowV1 service = Lifecycle(1, ObservationKind.Create, 300, 1)
            with { ResourceName = @"C:\Windows\service.exe", SessionRelativeTicks = 100 };
        ObservationRowV1 tool = Lifecycle(2, ObservationKind.Create, 400, 2)
            with { ResourceName = @"C:\Tools\tool.exe", SessionRelativeTicks = 200 };
        List<ObservationRowV1> rows = [service, tool];
        List<SourceFieldRowV1> fields = [Field(service, SourceField.ProcessSessionId, 0)];
        for (int index = 0; index < count; index++)
        {
            ObservationRowV1 worker = Lifecycle(3 + index, ObservationKind.Create, 1_000 + index, (ulong)(3 + index))
                with { ResourceName = string.Create(CultureInfo.InvariantCulture, $@"C:\Tools\worker{index}.exe"), SessionRelativeTicks = (3 + index) * 100L };
            rows.Add(worker);
            fields.Add(Field(worker, SourceField.ProcessSessionId, 1));
            long ticks = 100 + (2 * index);
            string near = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{50_000 + index}");
            rows.Add(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000 + index, (ulong)(100 + (2 * index)))
                .Between(near, "127.0.0.1:9090") with { SessionRelativeTicks = ticks * 100 });
            rows.Add(Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 300, (ulong)(101 + (2 * index)))
                .Between("127.0.0.1:9090", near) with { SessionRelativeTicks = (ticks + 1) * 100 });
        }

        rows.Add(Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 8, 400, 300).Between("127.0.0.1:50999", "127.0.0.1:7070")
            with { SessionRelativeTicks = 30_000 });
        return ([.. rows], [.. fields]);
    }
}
