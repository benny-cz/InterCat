using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

public sealed class NavigationContinuityTests
{
    [Fact(DisplayName = "§6.7: a stale search hit preserves the brush; a real hit opens from the whole session")]
    public void SearchNavigationValidatesBeforeClearingTheBrush()
    {
        using var workspace = new WorkspaceViewModel(SyntheticWorkspace.Create(), "session:one:generation:1");
        TimeRange brush = new(WorkspaceTime.TicksPerSecond, 2 * WorkspaceTime.TicksPerSecond);
        workspace.SelectInterval(brush);
        workspace.SearchText = "cache";
        SearchRow stale = new(new SearchHit(SearchHitKind.Channel, "missing", "Channel", "Gone", 1,
            ["group.app", "missing-process", "missing-channel"]));

        Assert.False(workspace.OpenSearchResult(stale));
        Assert.Equal(brush, workspace.SelectedInterval);
        Assert.Equal("L0 · MACHINE", workspace.LevelBadge);
        Assert.True(workspace.IsSearching);

        SearchRow found = workspace.SearchResults.Single(row => row.Hit.Kind == SearchHitKind.Channel);
        Assert.True(workspace.OpenSearchResult(found));
        Assert.Null(workspace.SelectedInterval);
        Assert.Equal("L3 · CHANNEL", workspace.LevelBadge);
        Assert.False(workspace.IsSearching);
    }

    [Fact(DisplayName = "§6.7: letting go of the choice on the machine rung lets go of the ranked row it made, said to the table")]
    public void LettingGoOfTheChoiceLetsGoOfTheRankedRow()
    {
        using var workspace = new WorkspaceViewModel(SyntheticWorkspace.Create(), "session:one:generation:1");
        var said = new List<string?>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.SelectedRung))
            {
                said.Add(workspace.SelectedRung?.Key);
            }
        };

        workspace.SelectedRung = workspace.RungRows[0];
        Assert.NotEqual("Nothing selected", workspace.SelectionTitle);
        Assert.False(workspace.Ascend());
        Assert.Equal("Nothing selected", workspace.SelectionTitle);
        Assert.Null(workspace.SelectedRung);
        Assert.Null(said[^1]);
    }

    [Fact(DisplayName = "R7: a newer generation keeps the relationship chosen, and says once when the session no longer holds it")]
    public void ANewerGenerationKeepsTheRelationshipChosen()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        old.SelectedRelationship = old.Relationships.Single(row => row.Key == "edge.api-indexer");
        WorkspaceNavigationMemento saved = old.CaptureNavigation();

        using var refreshed = new WorkspaceViewModel(source, "session:one:generation:2");
        Assert.Null(refreshed.RestoreNavigation(saved));
        Assert.Equal("edge.api-indexer", refreshed.SelectedRelationship?.Key);

        using var changed = new WorkspaceViewModel(
            source with { Edges = [.. source.Edges.Where(edge => edge.Key != "edge.api-indexer")] }, "session:one:generation:3");
        Assert.Equal("The relationship chosen is no longer in the session, so nothing is selected.", changed.RestoreNavigation(saved));
        Assert.Null(changed.SelectedRelationship);
    }

    [Fact(DisplayName = "R7: a newer generation keeps the processes chosen together, and says once how many of them it no longer holds")]
    public void ANewerGenerationKeepsTheChosenSet()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        ProcessNode[] chosen = [.. source.Processes.Take(3)];
        foreach (ProcessNode process in chosen)
        {
            old.ToggleGraphNodeInSelection(old.GraphDisplay.NodeOf(process.Id)!.Key);
        }

        Assert.Equal(Ids(chosen), Ids(old.ChosenProcesses));
        WorkspaceNavigationMemento saved = old.CaptureNavigation();

        using var refreshed = new WorkspaceViewModel(source, "session:one:generation:2");
        Assert.Null(refreshed.RestoreNavigation(saved));
        Assert.Equal(Ids(chosen), Ids(refreshed.ChosenProcesses));

        // A set begun from a ranked group's row, which stays the table's row, is the selection again, not that group.
        using var ranked = new WorkspaceViewModel(source, "session:two:generation:1");
        ranked.SelectedRung = ranked.RungRows[0];
        ranked.ToggleRungInSelection(ranked.RungRows[1]);
        Assert.Null(ranked.SelectedGroup);
        WorkspaceNavigationMemento besideRow = ranked.CaptureNavigation();
        Assert.Equal(ranked.RungRows[0].Key, besideRow.SelectedRungKey);
        using var rankedNext = new WorkspaceViewModel(source, "session:two:generation:2");
        Assert.Null(rankedNext.RestoreNavigation(besideRow));
        Assert.Equal(Ids(ranked.ChosenProcesses), Ids(rankedNext.ChosenProcesses));
        Assert.Null(rankedNext.SelectedGroup);

        // One of the three gone, the other two stay chosen together, which is said; all gone, nothing is chosen.
        using var fewer = new WorkspaceViewModel(Without(source, chosen.Take(1)), "session:one:generation:3");
        Assert.Equal("1 of the 3 processes chosen is no longer in the session; the rest stay chosen.", fewer.RestoreNavigation(saved));
        Assert.Equal(Ids(chosen.Skip(1)), Ids(fewer.ChosenProcesses));
        using var twoFewer = new WorkspaceViewModel(Without(source, chosen.Take(2)), "session:one:generation:4");
        Assert.Equal("2 of the 3 processes chosen are no longer in the session; the rest stay chosen.",
            twoFewer.RestoreNavigation(saved));
        Assert.Equal(Ids(chosen.Skip(2)), Ids(twoFewer.ChosenProcesses));
        using var none = new WorkspaceViewModel(Without(source, chosen), "session:one:generation:5");
        Assert.Equal("None of the processes chosen is still in the session, so nothing is selected.", none.RestoreNavigation(saved));
        Assert.False(none.HasMultiSelection);
    }

    /// <summary>Processes by their instance identities, in one order whatever theirs.</summary>
    private static string[] Ids(IEnumerable<ProcessNode> processes) =>
        [.. processes.Select(process => process.Id.ToString()).Order(StringComparer.Ordinal)];

    /// <summary>A snapshot without <paramref name="gone"/> and the relationships they are an end of.</summary>
    private static WorkspaceSnapshot Without(WorkspaceSnapshot source, IEnumerable<ProcessNode> gone)
    {
        HashSet<ProcessInstanceId> ids = [.. gone.Select(process => process.Id)];
        return source with
        {
            Processes = [.. source.Processes.Where(process => !ids.Contains(process.Id))],
            Edges = [.. source.Edges.Where(edge => !ids.Contains(edge.SourceId) && !ids.Contains(edge.TargetId))],
        };
    }

    [Fact(DisplayName = "§6.7: live publication keeps a search query and the selected hit")]
    public void SameSessionRefreshKeepsSearchSelection()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        old.SearchText = "cache";
        old.SelectedSearchResult = old.SearchResults.Single(row => row.Hit.Kind == SearchHitKind.Channel);
        WorkspaceNavigationMemento saved = old.CaptureNavigation();

        using var refreshed = new WorkspaceViewModel(source, "session:one:generation:2");
        Assert.Null(refreshed.RestoreNavigation(saved));
        Assert.Equal("cache", refreshed.SearchText);
        Assert.Equal(old.SelectedSearchResult.Hit.Key, refreshed.SelectedSearchResult?.Hit.Key);
        Assert.True(refreshed.IsSearching);
        Assert.False(refreshed.ShowsRankedTable);
    }

    [Fact]
    public void SameSessionRefreshKeepsRungSelectionBrushAndTableMode()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        old.SelectedRung = old.RungRows[0];
        Assert.True(old.Descend());
        old.SelectedRung = old.RungRows[0];
        Assert.True(old.Descend());
        old.SelectedRung = old.RungRows[0];
        Assert.True(old.Descend());
        old.SelectedRung = old.RungRows[0];
        old.SelectInterval(new TimeRange(2 * WorkspaceTime.TicksPerSecond, 4 * WorkspaceTime.TicksPerSecond));
        old.ShowTables = true;
        WorkspaceNavigationMemento saved = old.CaptureNavigation();

        WorkspaceSnapshot next = source with
        {
            Extent = new TimeRange(0, 30 * WorkspaceTime.TicksPerSecond),
        };
        using var refreshed = new WorkspaceViewModel(next, "session:one:generation:2");
        string? notice = refreshed.RestoreNavigation(saved);

        Assert.Null(notice);
        Assert.Equal(old.LevelBadge, refreshed.LevelBadge);
        Assert.Equal(old.Crumbs.Select(row => row.Label), refreshed.Crumbs.Select(row => row.Label));
        Assert.Equal(saved.SelectedRungKey, refreshed.SelectedRung?.Key);
        Assert.Equal(saved.SelectedProcess, refreshed.SelectedProcess?.Id);
        Assert.Equal(saved.SelectedInterval, refreshed.SelectedInterval);
        Assert.True(refreshed.ShowTables);
        Assert.Equal(next.Extent, refreshed.CaptureNavigation().Breadcrumb[0].Viewport);
    }

    [Fact]
    public void ARemovedFocusReturnsToNearestSurvivingRung()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        old.SelectedRung = old.RungRows[0];
        Assert.True(old.Descend());
        string missingGroup = old.CaptureNavigation().Breadcrumb[^1].Focus!.Value.Key;
        WorkspaceNavigationMemento saved = old.CaptureNavigation();

        WorkspaceSnapshot next = source with
        {
            Groups = [.. source.Groups.Where(group => group.Key != missingGroup)],
            Processes = [.. source.Processes.Where(process => process.GroupKey != missingGroup)],
        };
        using var refreshed = new WorkspaceViewModel(next, "session:one:generation:2");
        string? notice = refreshed.RestoreNavigation(saved);

        Assert.Equal($"{saved.Breadcrumb[^1].Crumb} is no longer in the session, so the view went back to the machine.", notice);
        Assert.Equal("L0 · MACHINE", refreshed.LevelBadge);
        Assert.False(refreshed.CanAscend);
        Assert.Null(refreshed.SelectedRung);
    }

    [Fact]
    public void AnExpiredBrushIsClearedInsteadOfMovedToOtherEvidence()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        old.SelectInterval(new TimeRange(WorkspaceTime.TicksPerSecond, 2 * WorkspaceTime.TicksPerSecond));

        WorkspaceSnapshot next = source with
        {
            Extent = new TimeRange(10 * WorkspaceTime.TicksPerSecond, 30 * WorkspaceTime.TicksPerSecond),
            Timeline = [.. source.Timeline.Where(bucket => bucket.Interval.EndTicks > 10 * WorkspaceTime.TicksPerSecond)],
        };
        using var refreshed = new WorkspaceViewModel(next, "session:one:generation:2");
        string? notice = refreshed.RestoreNavigation(old.CaptureNavigation());

        Assert.Equal("The analysis interval lay in time the session released, so it is cleared.", notice);
        Assert.Null(refreshed.SelectedInterval);
        Assert.Equal(next.Extent, refreshed.CaptureNavigation().Breadcrumb[0].Viewport);
    }

    [Fact]
    public void DirectEvidenceJumpRestoresItsVisibleScopeFilter()
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        old.SelectedRung = old.RungRows[0];
        Assert.True(old.Descend());
        Assert.True(old.ShowEvidence());

        using var refreshed = new WorkspaceViewModel(source, "session:one:generation:2");
        Assert.Null(refreshed.RestoreNavigation(old.CaptureNavigation()));
        Assert.Equal("L5 · EVIDENCE", refreshed.LevelBadge);
        Assert.Contains(refreshed.Filters, filter => filter.Field == "scope");
    }
}
