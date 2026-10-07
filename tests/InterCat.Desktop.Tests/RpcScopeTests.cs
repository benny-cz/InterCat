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

        // Esc climbs to the host's channel, whose served calls name the caller as theirs, on the call it came up from.
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        Assert.Equal(3, workspace.RungRows.Count);
        Assert.Contains(workspace.RungRows, row => row.Detail.EndsWith("called by caller.exe · 400", StringComparison.Ordinal));
        Assert.Equal(first.OtherEndKey, workspace.SelectedRung?.Key);
    });

    [Fact(DisplayName = "§6.7: the inspector describes a chosen RPC channel or call in full, with what its keys do, until a process is chosen")]
    public void TheInspectorDescribesAChosenCall() => SingleThreadedContext.Run(async () =>
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

        // A channel's row: its whole name and what it holds, which the rail cuts short; the process's lineage steps aside.
        workspace.SelectedRung = Scm(workspace);
        Assert.Equal("RPC calls to svcctl (Service Control Manager)", workspace.SelectionTitle);
        Assert.Equal(Scm(workspace).Detail, workspace.SelectionSubtitle);
        Assert.Equal("Enter lists its calls", workspace.SelectionActions);
        Assert.False(workspace.ShowsLineage);

        // Opened, its own rung's inspector names the channel as its row did, as the card counts it, until a call is chosen;
        // the row is read with the rung, and the window is told when it is.
        string scm = workspace.SelectionSubtitle;
        var raised = new List<string?>();
        workspace.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);
        Assert.True(workspace.Descend());
        raised.Clear();
        await workspace.RpcReady;
        Assert.Contains(nameof(WorkspaceViewModel.SelectionSubtitle), raised);
        Assert.Equal("This RPC channel", workspace.EvidenceHeading);
        Assert.Equal(("RPC calls to svcctl (Service Control Manager)", scm), (workspace.SelectionTitle, workspace.SelectionSubtitle));
        Assert.False(workspace.ShowsLineage);

        // A call's row: how long it took and its procedure, how it ended and who served it, and what O opens.
        RungRow first = workspace.RungRows[0];
        workspace.SelectedRung = first;
        Assert.StartsWith("RPC call at +", workspace.SelectionTitle, StringComparison.Ordinal);
        Assert.Equal($"{first.Label} · {first.Detail}", workspace.SelectionSubtitle);
        Assert.EndsWith("served by services.exe · 1960", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal("Enter opens its records · O opens the call services.exe · 1960 served", workspace.SelectionActions);

        // A call no link reached says only what Enter does.
        workspace.SelectedRung = workspace.RungRows[1];
        Assert.Equal("Enter opens its records", workspace.SelectionActions);

        // Back up from the call at the other end, the served call is described with the call its caller made.
        workspace.SelectedRung = first;
        Assert.True(workspace.OpenOtherEnd());
        await workspace.EvidenceReady;
        Assert.Null(workspace.DescribedRow);
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        Assert.EndsWith("called by caller.exe · 400", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal("Enter opens its records · O opens the call caller.exe · 400 made", workspace.SelectionActions);

        // A process chosen afterwards, as in the graph, is described instead.
        ProcessNode host = workspace.Snapshot.Processes.Single(node => node.ProcessId == 1_960);
        workspace.SelectedProcess = host;
        Assert.Null(workspace.DescribedRow);
        Assert.Equal((host.Name, string.Empty), (workspace.SelectionTitle, workspace.SelectionActions));

        // Its PID is an identity, named as the graph and the rows name it, never written as a quantity ("PID 1,960").
        Assert.Equal($"PID 1960 · {host.Role}", workspace.SelectionSubtitle);
    });

    [Fact(DisplayName = "§3.2: Esc from a call's records lands on that call, however far down its channel's pages, and a brush holding it keeps it")]
    public void EscLandsOnTheCallItLeft() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ManyCalls(250));
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
        workspace.SelectedRung = Scm(workspace);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        Assert.Equal(SessionRpcCalls.DefaultPageSize, workspace.RungRows.Count);

        // A call on the first page: its records, then Esc, and it is selected again.
        RungRow early = workspace.RungRows[5];
        workspace.SelectedRung = early;
        Assert.True(workspace.Descend());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        Assert.Equal((100, early.Key), (workspace.RungRows.Count, workspace.SelectedRung?.Key));

        // A call on the second page: the channel is read again through that page, and the call is selected.
        await workspace.LoadMoreAsync();
        RungRow later = workspace.RungRows[130];
        workspace.SelectedRung = later;
        Assert.True(workspace.Descend());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        Assert.Equal((200, later.Key), (workspace.RungRows.Count, workspace.SelectedRung?.Key));
        Assert.True(workspace.CanLoadMore);

        // A brush holding the first 181 calls lists them all, through the selected one's page, and keeps it selected.
        workspace.SelectInterval(new TimeRange(0, 1_005 + (180 * 10) + 1));
        await workspace.IntervalReady;
        await workspace.RpcReady;
        Assert.Equal((181, later.Key), (workspace.RungRows.Count, workspace.SelectedRung?.Key));
        Assert.False(workspace.CanLoadMore);
    });

    [Fact(DisplayName = "§7.4: the graph joins a caller and the process that served it by an RPC edge, read as call records with no size")]
    public void TheGraphDrawsAnRpcEdge() => SingleThreadedContext.Run(async () =>
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
        Assert.Contains("Rule: rpc-call-peer, version 1 · evidence: the calls its links join, by key", card.Lines);
        Assert.Contains("Bytes: none · an RPC call carries no size", card.Lines);
        Assert.Contains("Direction: display order only; each end's RPC rows say which calls and which served", card.Lines);
        Assert.Contains("Double-click opens the calls its links join", card.Lines);
        Assert.Contains("caller.exe", card.Title, StringComparison.Ordinal);
        Assert.Contains("services.exe", card.Title, StringComparison.Ordinal);

        // What the view says it draws names the linked calls, not TCP alone.
        Assert.Equal(OverviewWorkspace.LinkedCallsDisclosure, OverviewWorkspace.DisclosureFor(workspace.Snapshot));

        // A double click opens the calls its links join: the source process's evidence step, read by the relationship's own
        // key, lists both linked calls' records at both ends, and Esc climbs to that process.
        CommunicationEdge relationship = workspace.Snapshot.Edges.Single(edge => edge.Key == Assert.Single(drawn.Relationships));
        ProcessNode source = workspace.Snapshot.Processes.Single(node => node.Id == relationship.SourceId);
        ProcessNode target = workspace.Snapshot.Processes.Single(node => node.Id == relationship.TargetId);
        string calls = $"Records of RPC calls linked between {source.NameWithPid} and {target.NameWithPid}";
        Assert.True(workspace.OpenGraphEdge(drawn.Key));
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Assert.Equal(calls, workspace.EvidenceScopeText);
        Assert.Equal(8, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, row => Assert.StartsWith("RPC request ", row.Label, StringComparison.Ordinal));
        Assert.True(workspace.Ascend());
        Assert.Contains(source.Name, workspace.Crumbs[^1].Label, StringComparison.Ordinal);
        Assert.Equal(3, workspace.Crumbs.Count);

        // There, the relationship chosen and then one of the process's own RPC channels: the channel replaces it.
        await workspace.RpcReady;
        workspace.SelectedRelationship = Assert.Single(workspace.Relationships, candidate => candidate.Key == relationship.Key);
        workspace.SelectedRung = workspace.RungRows.First(candidate => RpcChannelKeys.IsRpc(candidate.Key));
        Assert.Equal((null, null), (workspace.SelectedRelationship, workspace.HighlightedEdgeKey));

        // Its row in the relationship table says what Enter opens, and opens the same.
        workspace.ReturnTo(0);
        RelationshipRow row = Assert.Single(workspace.Relationships, candidate => candidate.Key == relationship.Key);
        Assert.Equal("the calls its links join", row.Opens);

        // Chosen, it is the selection: its edge is haloed, and the timeline highlights its linked calls, read by its key.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        workspace.SelectedRelationship = row;
        Assert.EndsWith(" Enter opens the calls its links join.", workspace.SelectedRelationshipExplanation, StringComparison.Ordinal);
        Assert.Equal(relationship.Key, workspace.HighlightedEdgeKey);
        await workspace.HighlightReady;
        Assert.Equal(8, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));
        Assert.Equal($"{row.Source} ↔ {row.Target}", workspace.TimelineHighlightName);

        // The inspector counts its calls' records, which carry no size, and E lists them, read by its key.
        Assert.Equal(("Selected relationship", "8 linked RPC call records · an RPC call carries no size"),
            (workspace.EvidenceHeading, workspace.EvidenceSummary));
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal((calls, 8), (workspace.EvidenceScopeText, workspace.RungRows.Count));
        Assert.True(workspace.Ascend());
        workspace.SelectedRelationship = row;

        // Clearing the selection lets it go, as opening it does once it is open.
        workspace.ClearSelection();
        Assert.Null(workspace.SelectedRelationship);
        workspace.SelectedRelationship = row;
        Assert.True(workspace.OpenSelectedRelationship());
        Assert.Null(workspace.SelectedRelationship);
        await workspace.EvidenceReady;
        Assert.Equal(calls, workspace.EvidenceScopeText);
        Assert.Equal(8, workspace.RungRows.Count);

        // Without the session's records there are no calls to open, so the edge opens nothing rather than an empty rung.
        using var unread = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity);
        Assert.False(unread.OpenGraphEdge(Assert.Single(unread.GraphDisplay.Edges, edge => edge.Mechanism == Mechanism.Rpc).Key));
        Assert.Single(unread.Crumbs);
    });

    /// <summary>The process's service-control-manager channel as the rail shows it.</summary>
    private static RungRow Scm(WorkspaceViewModel workspace) =>
        workspace.RungRows.Single(row => row.Label.StartsWith("svcctl", StringComparison.Ordinal));

    [Fact(DisplayName = "§6.4: an RPC channel chosen among a process's rows is what the card counts, the timeline highlights and E lists")]
    public void AChosenRpcChannelIsWhatEvidenceLists() => SingleThreadedContext.Run(async () =>
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

        // Its three calls to the service control manager, a start and a stop each, carry no size; the timeline highlights
        // those six records.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        RungRow scm = Scm(workspace);
        workspace.SelectedRung = scm;
        Assert.Equal("Selected RPC channel", workspace.EvidenceHeading);
        Assert.Equal("6 call records · an RPC call carries no size", workspace.EvidenceSummary);
        Assert.Equal(scm.Source.Label, workspace.TimelineHighlightName);
        await workspace.HighlightReady;
        Assert.Equal(6, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));

        // E lists those six, and not the call to the other interface, naming where the channel was chosen.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(6, workspace.RungRows.Count);
        Assert.StartsWith("Records of " + scm.Source.Label, workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Contains("from the process rung with this RPC channel chosen",
            Assert.Single(workspace.Filters, filter => filter.Field == "scope").Reason, StringComparison.Ordinal);

        // The process chosen in its place, as its node's click chooses it, E lists its own records: its start, the six,
        // and its call to the other interface.
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        workspace.SelectProcess(client.Id);
        Assert.Equal("Selected process", workspace.EvidenceHeading);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(8, workspace.RungRows.Count);
    });

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

    /// <summary>PID 400 calls the service control manager <paramref name="count"/> times, each 5 ticks long, 10 apart from 1,000.</summary>
    private static ObservationRowV1[] ManyCalls(int count) =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\caller.exe" }),
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Timed(RpcCall(1_000 + (index * 10), ObservationKind.RequestStart, Direction.Outbound, 400, (ulong)(100 + (index * 2)),
                Activity(1_000 + index), ServiceControl)),
            Timed(RpcCall(1_005 + (index * 10), ObservationKind.RequestEnd, Direction.Outbound, 400, (ulong)(101 + (index * 2)),
                Activity(1_000 + index), status: 0)),
        }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>A median as the row states it, in the culture the Desktop formats with.</summary>
    private static string Median(long nanoseconds) =>
        OperationText.Duration(nanoseconds, System.Globalization.CultureInfo.CurrentCulture);

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
}
