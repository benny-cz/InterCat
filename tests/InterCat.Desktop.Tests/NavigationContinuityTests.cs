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

        Assert.Contains("nearest available rung", notice, StringComparison.Ordinal);
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

        Assert.Contains("outside retained evidence", notice, StringComparison.Ordinal);
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
