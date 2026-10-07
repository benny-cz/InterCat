using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.8's defaults: a setting a person changed from its first-run default is named for the rail in the words an
/// investigation keeps it in (R5), as each change is made, and none is named while every setting is at its default.
/// </summary>
public sealed class DefaultViewTests
{
    [Fact(DisplayName = "§6.8: a setting changed from its default is named in the words an investigation keeps it in, as it changes, a rate only under a ranking that has one")]
    public async Task AChangedSettingIsNamed()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);

        // Opened, every setting is at its default, and none is named.
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        Assert.False(workspace.DiffersFromDefaults);
        Assert.Equal(string.Empty, workspace.ChangedSettingsText);

        // Each change is named as it is made, and the rail is told.
        var raised = new List<string?>();
        workspace.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        Assert.Equal("Not the default view: its rows ranked by bytes sent.", workspace.ChangedSettingsText);
        Assert.Equal("1 setting changed", workspace.ChangedSettingsSummary);
        Assert.Contains(nameof(WorkspaceViewModel.DiffersFromDefaults), raised);
        workspace.PerSecond = true;
        Assert.Equal("Not the default view: its rows ranked by bytes sent per second.", workspace.ChangedSettingsText);
        raised.Clear();
        workspace.ScalesEachLane = true;
        Assert.Equal("Not the default view: its rows ranked by bytes sent per second and each of its timeline lanes on its own "
            + "scale.", workspace.ChangedSettingsText);
        Assert.Equal("2 settings changed", workspace.ChangedSettingsSummary);
        Assert.Contains(nameof(WorkspaceViewModel.ChangedSettingsSummary), raised);

        // A median has no rate, so per second is not named under it; records per second differ from the default too.
        workspace.RankBy = RankingMetric.RpcCallTime;
        await workspace.RankingReady;
        Assert.Equal("Not the default view: its rows ranked by RPC call times and each of its timeline lanes on its own scale.",
            workspace.ChangedSettingsText);
        workspace.RankBy = RankingMetric.Records;
        await workspace.RankingReady;
        Assert.Equal("Not the default view: its rows ranked by records per second and each of its timeline lanes on its own "
            + "scale.", workspace.ChangedSettingsText);
        workspace.PerSecond = false;
        workspace.ScalesEachLane = false;
        Assert.False(workspace.DiffersFromDefaults);
        Assert.Empty(workspace.ChangedSettings);
        Assert.Equal((string.Empty, string.Empty), (workspace.ChangedSettingsSummary, workspace.ChangedSettingsText));

        // Grouped by terminal session and counting candidates, a workspace names both.
        SessionOverviewBundle candidates = SessionOverviewProjector.Project(session.Store, EvidencePolicy.IncludeCandidates);
        using var grouped = new WorkspaceViewModel(
            WorkspaceGrouping.Regroup(OverviewWorkspace.From(candidates), LaneGrouping.UserSession),
            WorkspaceGrouping.Identity(candidates.GraphIdentity, LaneGrouping.UserSession),
            new SessionEvidenceSource(session.Path, candidates.SessionId, candidates.Generation, EvidencePolicy.IncludeCandidates));
        Assert.Equal("Not the default view: its processes grouped by terminal session and its records counted with candidates.",
            grouped.ChangedSettingsText);

        // The tour, which no person set, names none.
        using var tour = new WorkspaceViewModel();
        tour.ScalesEachLane = true;
        Assert.False(tour.DiffersFromDefaults);
    }
}
