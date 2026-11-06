using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// What a process's lifecycle records say it ran in, stated by the inspector beside its lineage: its terminal session and
/// its package (ADR-094) - each named, said to be none, or said to be unrecorded, never one for another (R3).
/// </summary>
public sealed class ProcessIdentityTests
{
    private const string Package = "Microsoft.DesktopAppInstaller_1.29.250.0_x64__8wekyb3d8bbwe";

    [Fact(DisplayName = "R3: the inspector names the terminal session and the package a process's lifecycle records name, says a process in no package is in none, and says what no record named is not recorded")]
    public void TheInspectorStatesWhatAProcessRanIn() => SingleThreadedContext.Run(() =>
    {
        using var session = new TemporarySession();
        ObservationRowV1 winget = Timed(Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Program Files\WindowsApps\winget.exe" });
        ObservationRowV1 child = Timed(Lifecycle(20, ObservationKind.Create, 401, 2) with { ResourceName = @"C:\Tools\child.exe" });
        ObservationRowV1 older = Timed(Lifecycle(30, ObservationKind.Create, 402, 3) with { ResourceName = @"C:\Tools\older.exe" });
        Publish(session.Store, [winget, child, older], fields:
        [
            Field(winget, SourceField.ProcessSessionId, 1),
            Field(winget, SourceField.ProcessPackageFullName, 0) with { Value = null, Text = Package },
            Field(child, SourceField.ProcessSessionId, 0),
            Field(child, SourceField.ProcessPackageFullName, 0),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        ProcessNode Pid(int pid) => workspace.Snapshot.Processes.Single(node => node.ProcessId == pid);

        // Nothing is selected: nothing is said of any process.
        Assert.Equal((string.Empty, string.Empty), (workspace.TerminalSessionText, workspace.PackageText));

        // winget ran in session 1 in its package; its child in session 0 in none, which its record named; the older process's
        // records named neither.
        workspace.SelectProcess(Pid(400).Id);
        Assert.Equal(("1", Package), (workspace.TerminalSessionText, workspace.PackageText));
        workspace.SelectProcess(Pid(401).Id);
        Assert.Equal(("0", "None: its lifecycle record names no package it ran in"), (workspace.TerminalSessionText, workspace.PackageText));
        workspace.SelectProcess(Pid(402).Id);
        Assert.Equal(("Not recorded: no lifecycle record of this process names it", "Not recorded: no lifecycle record of this process names it"),
            (workspace.TerminalSessionText, workspace.PackageText));

        // Each is said again as the selection moves, so the inspector never keeps the process before's.
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        workspace.SelectProcess(Pid(400).Id);
        Assert.Contains(nameof(WorkspaceViewModel.TerminalSessionText), changed);
        Assert.Contains(nameof(WorkspaceViewModel.PackageText), changed);
        return Task.CompletedTask;
    });

    [Fact(DisplayName = "R3: the inspector names the services a process hosted as its capture started and stopped, says none for a process that hosted none of them, and not recorded for a capture that read none")]
    public void TheInspectorStatesAProcesssServices() => SingleThreadedContext.Run(() =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields, ServiceInventoryV1[] inventories) = ServiceHosts();
        Publish(session.Store, rows, fields: fields, services: inventories);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        ProcessNode Node(int pid, int holder) => workspace.Snapshot.Processes.Single(node => node.ProcessId == pid && node.PidHolder == holder);

        Assert.Equal(string.Empty, workspace.ServicesText);
        workspace.SelectProcess(Node(2400, 1).Id);
        Assert.Equal("W32Time, BITS (at the stop alone) · as the capture started and stopped", workspace.ServicesText);
        workspace.SelectProcess(Node(1820, 2).Id);
        Assert.Equal("None as the capture started and stopped", workspace.ServicesText);

        // Each is said again as the selection moves.
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        workspace.SelectProcess(Node(1820, 1).Id);
        Assert.Contains(nameof(WorkspaceViewModel.ServicesText), changed);
        Assert.Equal("Dnscache (at the start alone), nsi (at the start alone) · as the capture started and stopped", workspace.ServicesText);

        // A capture that read none says so of every process.
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields);
        SessionOverviewBundle unread = SessionOverviewProjector.Project(plain.Store);
        using var bare = new WorkspaceViewModel(OverviewWorkspace.From(unread), unread.GraphIdentity,
            new SessionEvidenceSource(plain.Path, unread.SessionId, unread.Generation));
        bare.SelectProcess(bare.Snapshot.Processes.Single(node => node.ProcessId == 2400).Id);
        Assert.Equal("Not recorded: this capture kept no list of the services running", bare.ServicesText);
        return Task.CompletedTask;
    });

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
