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

    [Fact(DisplayName = "§7.4: an RPC channel's row names who served its calls, and each call's row its other end")]
    public void RpcRowsNameTheirOtherEnd() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields);
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
        Assert.Equal(
            $"RPC client · served by services.exe · 1960 (2 of 3) · 3 calls · 1 failed · median {Median(2_000)}",
            Scm(workspace).Detail);

        // Each call says who served it, or why no one is known to have.
        workspace.SelectedRung = Scm(workspace);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        Assert.Equal(
            [
                "served by services.exe · 1960",
                "other end unresolved: no ALPC send on its thread during the call",
                "served by services.exe · 1960",
            ],
            workspace.RungRows.Select(row => row.Detail.Split(" · ", 3)[2]));
        Assert.Contains("served by services.exe · 1960", workspace.RungRows[0].SpokenName, StringComparison.Ordinal);

        // O opens the call at the other end: the host's call that served it, on the host's own channel, with its records.
        RungRow first = workspace.RungRows[0];
        Assert.Equal(("Open the call services.exe · 1960 served (O)", false), (first.OtherEndMenu, workspace.RungRows[1].HasOtherEnd));
        workspace.SelectedRung = first;
        Assert.True(workspace.OpenOtherEnd());
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Assert.Equal(["RPC request start", "RPC request end"], workspace.RungRows.Select(row => row.Label));
        Assert.StartsWith("Records of RPC call at +", workspace.EvidenceScopeText, StringComparison.Ordinal);
        string crumbs = string.Join(" › ", workspace.Crumbs.Select(crumb => crumb.Label));
        Assert.Contains("services.exe", crumbs, StringComparison.Ordinal);
        Assert.Contains("RPC calls served on svcctl", crumbs, StringComparison.Ordinal);

        // Esc climbs to the host's channel, whose served calls name the caller as theirs.
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        Assert.Equal(3, workspace.RungRows.Count);
        Assert.Contains(workspace.RungRows, row => row.Detail.EndsWith("called by caller.exe · 400", StringComparison.Ordinal));
    });

    [Fact(DisplayName = "§7.4: the graph joins a caller and the process that served it by an RPC edge, read as call records with no size")]
    public void TheGraphDrawsAnRpcEdge()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));

        GraphDisplayEdge drawn = Assert.Single(workspace.GraphDisplay.Edges, edge => edge.Mechanism == Mechanism.Rpc);
        HoverCard card = workspace.DescribeGraphHover(drawn.Key)!;
        Assert.Contains(card.Lines, line => line.StartsWith("Linked RPC call records: 8", StringComparison.Ordinal));
        Assert.Contains("Bytes: none · an RPC call carries no size", card.Lines);
        Assert.Contains("Direction: display order only; each end's RPC rows say which calls and which served", card.Lines);
        Assert.Contains("Double-click opens its source process, whose rows list its RPC channels", card.Lines);
        Assert.Contains("caller.exe", card.Title, StringComparison.Ordinal);
        Assert.Contains("services.exe", card.Title, StringComparison.Ordinal);

        // What the view says it draws names the linked calls, not TCP alone.
        Assert.Equal(OverviewWorkspace.LinkedCallsDisclosure, OverviewWorkspace.DisclosureFor(workspace.Snapshot));
    }

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

    /// <summary>
    /// <see cref="Calls"/> with each call's procedure, and ALPC messages that link the first and third calls to the calls
    /// that served them: each sent on the caller's thread and received on the thread that began the server call (ADR-034).
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkedCalls()
    {
        ObservationRowV1[] messages =
        [
            Timed(Alpc(102, ObservationKind.Send, 400, 401, 60)),
            Timed(Alpc(103, ObservationKind.Receive, 1_960, 1_961, 61)),
            Timed(Alpc(302, ObservationKind.Send, 400, 401, 62)),
            Timed(Alpc(303, ObservationKind.Receive, 1_960, 1_961, 63)),
        ];
        ObservationRowV1[] rows = [.. Calls(), .. messages];
        return (
            rows,
            [
                .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                    .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
                Field(messages[0], SourceField.AlpcMessageId, 21),
                Field(messages[1], SourceField.AlpcMessageId, 21),
                Field(messages[2], SourceField.AlpcMessageId, 22),
                Field(messages[3], SourceField.AlpcMessageId, 22),
            ]);
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>A median as the row states it, in the culture the Desktop formats with.</summary>
    private static string Median(long nanoseconds) =>
        OperationText.Duration(nanoseconds, System.Globalization.CultureInfo.CurrentCulture);

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
}
