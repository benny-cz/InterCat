using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §19.5's view filter in the window's model: the rail names InterCat's own processes, and once they are set aside counts
/// them with their records, which the timeline and the evidence still count; the setting is named as changed, an export
/// says what it set aside, a ranking's totals leave them out, and a process's lineage still names them.
/// </summary>
public sealed class CollectorSetAsideTests
{
    [Fact(DisplayName = "§19.5: the rail names InterCat's own processes, offers to set them aside, and says nothing for a capture naming none")]
    public async Task TheRailNamesInterCatsOwn()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        using WorkspaceViewModel workspace = Open(session, aside: false);
        await workspace.LayoutReady;

        Assert.True(workspace.OffersCollectorsAside);
        Assert.False(workspace.CollectorsSetAside);
        Assert.Equal("InterCat's own: intercat-broker.exe · PID 4120 #1", workspace.CollectorsLine);
        Assert.Equal("InterCat's own processes, which collected this capture: intercat-broker.exe · PID 4120 #1 (InterCat's "
            + "broker). Set aside, they leave the ranked rows, the graph and the channels, with the channels they are an end "
            + "of; no record is removed, and the timeline and the evidence still count theirs.", workspace.CollectorsText);
        Assert.Equal("Set aside", workspace.CollectorsCommand);
        Assert.Empty(workspace.ChangedSettings);
        Assert.Contains(workspace.RungRows, row => row.Label == "intercat-broker.exe");

        using var unnamed = new TemporarySession();
        PublishCollected(unnamed.Store, named: false);
        using WorkspaceViewModel none = Open(unnamed, aside: true);
        Assert.False(none.OffersCollectorsAside);
        Assert.False(none.CollectorsSetAside);
        Assert.Equal((string.Empty, string.Empty), (none.CollectorsLine, none.CollectorsText));
        Assert.Contains(none.RungRows, row => row.Label == "intercat-broker.exe");

        // The synthetic tour is no session, so it names nothing either.
        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "tour");
        Assert.False(tour.OffersCollectorsAside);
    }

    [Fact(DisplayName = "§19.5: set aside, InterCat's own leave the rows, are counted in the rail, named as a changed setting and in an export, and leave a ranking's totals")]
    public async Task SetAsideTheyAreCountedNotShown()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        using WorkspaceViewModel workspace = Open(session, aside: true);
        await workspace.LayoutReady;

        Assert.True(workspace.OffersCollectorsAside);
        Assert.True(workspace.CollectorsSetAside);
        Assert.Equal("InterCat's own set aside: 1 process, 3 records", workspace.CollectorsLine);
        Assert.Equal("InterCat's own processes are set aside from the ranked rows, the graph and the channels: "
            + "intercat-broker.exe · PID 4120 #1 (InterCat's broker), with 3 records over the whole session. No record is "
            + "removed: the timeline and the evidence still count theirs. Show them puts them back.", workspace.CollectorsText);
        Assert.Equal("Show them", workspace.CollectorsCommand);
        Assert.Equal(["InterCat.exe", "tool.exe"], workspace.RungRows.Select(row => row.Label).Order());
        Assert.DoesNotContain(workspace.GraphDisplay.Nodes, node => node.Label.Contains("broker", StringComparison.Ordinal));

        // The filter is a setting changed from its default, named as an investigation would name it.
        Assert.Equal(["InterCat's own processes set aside"], workspace.ChangedSettings);
        Assert.Equal("1 setting changed", workspace.ChangedSettingsSummary);
        Assert.Equal("Not the default view: InterCat's own processes set aside.", workspace.ChangedSettingsText);

        // An export of the view counts what it set aside.
        ExportContext export = workspace.DescribeExport(DateTimeOffset.UnixEpoch);
        Assert.Equal((1, 3L), (export.SetAsideProcesses, export.SetAsideRecords));
        Assert.Contains(CollectorText.SetAsideCaveat(1, 3), export.Caveats);

        // A ranking by peers counts the processes with a peer among the rows shown: the client, not the broker.
        workspace.RankBy = RankingMetric.ActivePeers;
        await workspace.RankingReady;
        Assert.Equal("1 process has a peer", workspace.RankingNote);
        using WorkspaceViewModel shown = Open(session, aside: false);
        shown.RankBy = RankingMetric.ActivePeers;
        await shown.RankingReady;
        Assert.Equal("2 processes have a peer", shown.RankingNote);
        Assert.Equal((0, 0L), (shown.DescribeExport(DateTimeOffset.UnixEpoch).SetAsideProcesses,
            shown.DescribeExport(DateTimeOffset.UnixEpoch).SetAsideRecords));
    }

    [Fact(DisplayName = "§19.5: a process's lineage names a parent or child set aside as InterCat's own, never as missing from the capture")]
    public void LineageNamesWhatIsSetAside()
    {
        DateTimeOffset created = CollectorBrokerCreated;
        using var session = new TemporarySession();
        ObservationRowV1 launcher = Timed(Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Windows\services.exe" });
        ObservationRowV1 broker = Timed(Lifecycle(20, ObservationKind.Create, 401, 2) with
        {
            ResourceName = @"C:\Program Files\InterCat\intercat-broker.exe",
        });
        ObservationRowV1 helper = Timed(Lifecycle(30, ObservationKind.Create, 402, 3) with { ResourceName = @"C:\Tools\helper.exe" });
        ObservationRowV1 sibling = Timed(Lifecycle(40, ObservationKind.Create, 403, 4) with { ResourceName = @"C:\Tools\tool.exe" });
        Publish(session.Store, [launcher, broker, helper, sibling], fields:
        [
            Field(launcher, SourceField.ProcessStartSequence, 800),
            Field(broker, SourceField.ProcessStartSequence, 801),
            Field(broker, SourceField.ProcessCreateTime, created.ToFileTime()),
            Field(broker, SourceField.ParentProcessId, 400),
            Field(broker, SourceField.ParentStartSequence, 800),
            Field(helper, SourceField.ProcessStartSequence, 802),
            Field(helper, SourceField.ParentProcessId, 401),
            Field(helper, SourceField.ParentStartSequence, 801),
            Field(sibling, SourceField.ProcessStartSequence, 803),
            Field(sibling, SourceField.ParentProcessId, 400),
            Field(sibling, SourceField.ParentStartSequence, 800),
        ], collectors: new CollectorIdentitiesV1
        {
            Contract = CollectorIdentitiesV1.ContractName,
            CaptureId = TestSessions.Capture.Value,
            Processes = [new() { Role = CollectorRole.Broker, ProcessId = 401, CreatedUtc = created }],
        });
        using WorkspaceViewModel workspace = Open(session, aside: true);
        ProcessNode Pid(int pid) => workspace.Snapshot.Processes.Single(node => node.ProcessId == pid);
        Assert.DoesNotContain(workspace.Snapshot.Processes, node => node.ProcessId == 401);

        // The broker's child names it as its parent, set aside, and cannot go to it.
        workspace.SelectProcess(Pid(402).Id);
        Assert.Equal("intercat-broker.exe · PID 401 · linked by its start key · InterCat's broker, set aside", workspace.ParentText);
        Assert.False(workspace.CanSelectParent);
        Assert.False(workspace.SelectParent());

        // The process that started it counts it among its children, after those the view shows, and selects only those.
        workspace.SelectProcess(Pid(400).Id);
        Assert.Equal("1 process: tool.exe · PID 403 · 1 process of InterCat's own, set aside: intercat-broker.exe · PID 401",
            workspace.ChildrenText);
        Assert.True(workspace.SelectChildren());
        Assert.Equal(403, workspace.SelectedProcess?.ProcessId);

        // Without the sibling it would have none shown, and still names the one set aside rather than none.
        using var lone = new TemporarySession();
        Publish(lone.Store, [launcher, broker], fields:
        [
            Field(launcher, SourceField.ProcessStartSequence, 800),
            Field(broker, SourceField.ProcessStartSequence, 801),
            Field(broker, SourceField.ProcessCreateTime, created.ToFileTime()),
            Field(broker, SourceField.ParentProcessId, 400),
            Field(broker, SourceField.ParentStartSequence, 800),
        ], collectors: new CollectorIdentitiesV1
        {
            Contract = CollectorIdentitiesV1.ContractName,
            CaptureId = TestSessions.Capture.Value,
            Processes = [new() { Role = CollectorRole.Broker, ProcessId = 401, CreatedUtc = created }],
        });
        using WorkspaceViewModel alone = Open(lone, aside: true);
        alone.SelectProcess(alone.Snapshot.Processes.Single(node => node.ProcessId == 400).Id);
        Assert.Equal("1 process of InterCat's own, set aside: intercat-broker.exe · PID 401", alone.ChildrenText);
        Assert.False(alone.CanSelectChildren);

        // Shown, the broker is a child and a parent as any process is.
        using WorkspaceViewModel shown = Open(session, aside: false);
        shown.SelectProcess(shown.Snapshot.Processes.Single(node => node.ProcessId == 402).Id);
        Assert.Equal("intercat-broker.exe · PID 401 · linked by its start key", shown.ParentText);
        Assert.True(shown.CanSelectParent);
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>The session as the window opens it, with InterCat's own set aside or shown.</summary>
    private static WorkspaceViewModel Open(TemporarySession session, bool aside)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot whole = OverviewWorkspace.From(overview);
        WorkspaceSnapshot snapshot = aside ? WorkspaceCollectors.SetAside(whole) : whole;
        return new WorkspaceViewModel(snapshot, WorkspaceCollectors.Identity(overview.GraphIdentity, aside),
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation)
            {
                SetAside = WorkspaceCollectors.Instances(snapshot),
            });
    }
}
