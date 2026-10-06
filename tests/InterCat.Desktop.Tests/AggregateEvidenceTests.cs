using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// An aggregate drawn in the graph - processes with no relationship counted in one node, or everything outside a rung's
/// focus - stands for its processes wherever the selection is read (§6.4, I5): the card counts their own records, the
/// timeline highlights them and E lists them, as for the same processes chosen together.
/// </summary>
public sealed class AggregateEvidenceTests
{
    [Fact(DisplayName = "§6.4: an aggregate chosen in the graph is what the card counts, the timeline highlights and E lists: its processes' own records, at every rung")]
    public async Task AnAggregateIsItsProcessesRecords()
    {
        using var session = new TemporarySession();
        PublishMachine(session);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);

        // At the machine rung the three quiet processes are one node: their three creations, with no relationship and no
        // byte between them.
        await AssertTheAggregateIsItsProcesses(workspace, GraphNodeKind.Quiet, 3,
            "3 own records, all process lifecycle · no admitted paired TCP relationship · nothing sent · nothing received");

        // At client.exe's rung everything else but its server is the context node: the quiet three and the peer, whose
        // own send to the server is the one relationship it holds.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;
        const string Context = "5 own records, mostly process lifecycle · 2 paired TCP observations on 1 relationship · 4 B sent · nothing received";
        await AssertTheAggregateIsItsProcesses(workspace, GraphNodeKind.Context, 5, Context);

        // On the client's channel to the server, the context node chosen is what the card counts, rather than the channel.
        workspace.ClearSelection();
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        await workspace.LayoutReady;
        Assert.Equal("This channel", workspace.EvidenceHeading);
        await AssertTheAggregateIsItsProcesses(workspace, GraphNodeKind.Context, 5, Context);
    }

    /// <summary>
    /// The aggregate of <paramref name="kind"/> chosen: the card counts its processes' records and what they sent and
    /// received, the timeline highlights those records, and E lists exactly them; the way back finds it still chosen.
    /// </summary>
    private static async Task AssertTheAggregateIsItsProcesses(
        WorkspaceViewModel workspace, GraphNodeKind kind, int records, string counted)
    {
        GraphDisplayNode aggregate = Assert.Single(workspace.GraphDisplay.Nodes, node => node.Kind == kind);
        workspace.SelectGraphNode(aggregate.Key);
        await workspace.SelectionBytesReady;
        Assert.Equal(("Selected aggregate", counted), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        await workspace.HighlightReady;
        Assert.Equal(records, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(records, workspace.RungRows.Count);
        Assert.StartsWith($"Records owned by {aggregate.Members.Count} selected processes", workspace.EvidenceScopeText,
            StringComparison.Ordinal);
        Assert.Contains($"with the aggregate {aggregate.Label} selected", Assert.Single(workspace.Filters, filter => filter.Field == "scope").Reason,
            StringComparison.Ordinal);
        Assert.True(workspace.Ascend());
    }

    /// <summary>
    /// client.exe sends server.exe 8 bytes, and peer.exe sends it 4, each over a channel of its own; quiet-a.exe,
    /// quiet-b.exe and quiet-c.exe are created and do nothing else.
    /// </summary>
    private static void PublishMachine(TemporarySession session) => Publish(session.Store,
    [
        Timed(Lifecycle(5, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\server.exe" }),
        Timed(Lifecycle(6, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(7, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\quiet-a.exe" }),
        Timed(Lifecycle(8, ObservationKind.Create, 301, 4) with { ResourceName = @"C:\Tools\quiet-b.exe" }),
        Timed(Lifecycle(9, ObservationKind.Create, 302, 5) with { ResourceName = @"C:\Tools\quiet-c.exe" }),
        Timed(Lifecycle(10, ObservationKind.Create, 400, 6) with { ResourceName = @"C:\Tools\peer.exe" }),
        Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 8).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 4, 400, 9).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(65, ObservationKind.Receive, AccountingSide.ReceiveSide, 4, 200, 10).Between("127.0.0.1:8080", "127.0.0.1:50001")),
    ]);

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
