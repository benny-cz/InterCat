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
/// The window's resource hubs (§6.3, ADR-099): a section two processes mapped is drawn as a node of its own, joined to each
/// by a membership, and wherever the graph is read it says what the resource is, who held it and that holding it proves
/// shared access, never traffic (P27).
/// </summary>
public sealed class ResourceHubWorkspaceTests
{
    [Fact(DisplayName = "§6.3: the window draws a section two processes mapped as a hub between them, and its cards say what it is, who held it and that holding it is no traffic")]
    public void TheWindowSaysWhatAHubIs() => SingleThreadedContext.Run(async () =>
    {
        // The sections with their readings placed in session time, as a capture's are, so the timeline can count them.
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplay drawn = workspace.GraphDisplay;

        // writer.exe and reader.exe, related by nothing else, are drawn apart, joined by the section both mapped.
        GraphDisplayNode hub = Assert.Single(drawn.Nodes, node => node.IsHub);
        GraphDisplayNode writer = drawn.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id)!;
        GraphDisplayNode reader = drawn.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 200).Id)!;
        Assert.Equal((GraphNodeKind.Process, GraphNodeKind.Process), (writer.Kind, reader.Kind));
        Assert.Equal(new[] { writer.Key, reader.Key }.Order(StringComparer.Ordinal),
            drawn.Edges.Where(edge => edge.Membership).Select(edge => edge.SourceKey).Order(StringComparer.Ordinal));
        Assert.Equal(("Mapped section 4", "held by 2 processes"), (hub.Label, GraphView.Detail(hub)));

        // The hub's card: what it is, who held it, its capacity, and that holding it is no traffic.
        HoverCard card = workspace.DescribeGraphHover(hub.Key)!;
        Assert.Equal("Mapped section 4", card.Title);
        Assert.Equal(
            [
                "Mapped section",
                "Held by writer.exe · PID 100, reader.exe · PID 200",
                "Capacity: the largest view of it a process mapped, 64 KiB - what it can hold, never bytes moved through it",
                "2 drawn processes held it here; one outside this rung's neighbourhood is left to its own rung",
                "Holding it proves the processes shared it, never what moved through it: no record of it is traffic",
            ],
            card.Lines.Take(5));
        Assert.Matches(@"^Scope: \[.+\) \S+ · whole session$", card.Lines[5]);
        Assert.Equal(
            [
                "Edges: memberships, not relationships, and counted in no total; drawn thin and neutral, with no direction",
                "Size: fixed; a resource, not a process",
            ],
            card.Lines.Skip(6));

        // A membership's card: who held it, and that a membership is no traffic and has no direction.
        GraphDisplayEdge membership = drawn.Edges.Single(edge => edge.Membership && edge.SourceKey == writer.Key);
        HoverCard edgeCard = workspace.DescribeGraphHover(membership.Key)!;
        Assert.Equal("writer.exe · PID 100 — Mapped section 4", edgeCard.Title);
        Assert.Equal(
            [
                "writer.exe · PID 100 held Mapped section 4",
                "A membership: holding a resource, never traffic through it, and with no direction",
                "Thickness: the same for every membership, which carries no traffic value in the graph",
            ],
            edgeCard.Lines);

        // Chosen, it says what it is and who held it, its card counts its own records - its views' - and the timeline
        // highlights exactly them.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        workspace.SelectGraphNode(hub.Key);
        Assert.Equal("Mapped section 4", workspace.SelectionTitle);
        Assert.Equal("Mapped section held by writer.exe · PID 100, reader.exe · PID 200: holding it proves they shared it, never "
            + "what moved through it. E lists its records, whichever process they bind to.", workspace.SelectionSubtitle);
        Assert.Equal(("Selected resource", "4 records of its views · holding a resource is no traffic"),
            (workspace.EvidenceHeading, workspace.EvidenceSummary));
        Assert.Same(workspace.GraphDisplay.Node(hub.Key), workspace.SelectedCluster);
        await workspace.HighlightReady;
        Assert.Equal(4, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));

        // E lists exactly them, its filter naming the resource and why.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(4, workspace.RungRows.Count);
        Assert.StartsWith("Records of Mapped section 4", workspace.EvidenceScopeText, StringComparison.Ordinal);
        FilterRow scope = Assert.Single(workspace.Filters, filter => filter.Field == "scope");
        Assert.EndsWith("with the resource Mapped section 4 chosen: exactly its records, whichever process they bind to.",
            scope.Reason, StringComparison.Ordinal);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }
}
