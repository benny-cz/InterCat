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
/// A process's RPC channels and a channel's calls answer the scope the rung counts, as the paired channels beside them
/// do (§6.4): under a brush they count the calls it holds, and the previous rows stay until the new ones are read.
/// </summary>
public sealed class RpcScopeTests
{
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "§6.4: under a brush a process's RPC channels and a channel's calls count the calls the brush holds")]
    public void RpcRowsFollowTheBrush() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        Assert.Equal($"RPC client · 3 calls · 1 failed · median {Median(2_000)}", Scm(workspace).Detail);

        // The brush holds the first two calls' stops: both complete in it. The row stays while the brush is read.
        workspace.SelectInterval(new TimeRange(110, 250));
        await workspace.IntervalReady;
        Assert.Equal($"RPC client · 3 calls · 1 failed · median {Median(2_000)}", Scm(workspace).Detail);
        await workspace.RpcReady;
        Assert.Equal($"RPC client · 2 calls · median {Median(1_000)}", Scm(workspace).Detail);
        Assert.Equal(3, Scm(workspace).Source.ObservationCount);

        // The channel's rung lists those two calls, and follows the brush when it moves.
        workspace.SelectedRung = Scm(workspace);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        Assert.Equal(2, workspace.RungRows.Count);
        workspace.SelectInterval(new TimeRange(250, 500));
        await workspace.IntervalReady;
        await workspace.RpcReady;
        Assert.Single(workspace.RungRows);

        // Cleared, the channel's rung lists all three again.
        workspace.ClearSelection();
        await workspace.IntervalReady;
        await workspace.RpcReady;
        Assert.Equal(3, workspace.RungRows.Count);
    });

    /// <summary>The process's service-control-manager channel as the rail shows it.</summary>
    private static RungRow Scm(WorkspaceViewModel workspace) =>
        workspace.RungRows.Single(row => row.Label.StartsWith("svcctl", StringComparison.Ordinal));

    /// <summary>
    /// PID 400 calls the service control manager three times - at 100, 200 and 300, the last failing - and starts a
    /// call to another interface at 400 that never ends; PID 1960 serves the three.
    /// </summary>
    private static ObservationRowV1[] Calls() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\caller.exe" }),
        Timed(Lifecycle(2, ObservationKind.Inventory, 1_960, 2) with { ResourceName = @"C:\Windows\System32\services.exe" }),
        Timed(RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), ServiceControl)),
        Timed(RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0)),
        Timed(RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 20, Activity(2), ServiceControl)),
        Timed(RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 21, Activity(2), status: 0)),
        Timed(RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, Activity(3), ServiceControl)),
        Timed(RpcCall(340, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, Activity(3), status: 5)),
        Timed(RpcCall(400, ObservationKind.RequestStart, Direction.Outbound, 400, 40, Activity(4), Guid.Parse("0a74ef1c-41a4-4e06-83ae-dc74fb1cdd53"))),
        Timed(RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 50, Activity(11), ServiceControl)),
        Timed(RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 51, Activity(11), status: 0)),
        Timed(RpcCall(205, ObservationKind.RequestStart, Direction.Inbound, 1_960, 52, Activity(12), ServiceControl)),
        Timed(RpcCall(207, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 53, Activity(12), status: 0)),
        Timed(RpcCall(305, ObservationKind.RequestStart, Direction.Inbound, 1_960, 54, Activity(13), ServiceControl)),
        Timed(RpcCall(330, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 55, Activity(13), status: 5)),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>A median as the row states it, in the culture the Desktop formats with.</summary>
    private static string Median(long nanoseconds) =>
        OperationText.Duration(nanoseconds, System.Globalization.CultureInfo.CurrentCulture);

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
}
