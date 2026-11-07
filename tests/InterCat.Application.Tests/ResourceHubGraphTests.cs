using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The graph's resource hubs (§6.3, ADR-099): a resource two or more drawn nodes' processes held is drawn as a node of its
/// own, joined to each drawn node holding it by a membership - undirected, with no traffic value - and never folds a
/// process: holding a resource proves shared access, never what moved through it (P27).
/// </summary>
public sealed class ResourceHubGraphTests
{
    [Fact(DisplayName = "§6.3: a session's resources two or more processes held are its resource links, never an image's section nor one a single process held")]
    public void ASessionsSharedResourcesAreItsResourceLinks()
    {
        // Section 4's views, which both processes mapped: the start-listed sections and section 4 by its handles are one
        // process's each, and the image's section, which the reader maps here too, is a module loaded, not memory shared.
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        ObservationRowV1 image = SectionRecord(13, 37, 200, 200, 99, view: true);
        Publish(session.Store, [.. rows.Append(image).OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)],
            fields: [.. fields, Field(image, SourceField.SectionNumber, 5), Field(image, SourceField.SectionViewNumber, 52),
                Field(image, SourceField.ViewSize, 4096), Field(image, SourceField.ViewInformation, 0x47L << 48)]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ResourceLink shared = Assert.Single(overview.ResourceLinks);
        ProcessInstanceId writer = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId reader = overview.Nodes.Single(node => node.ProcessId == 200).Id;
        Assert.Equal((Mechanism.SharedSection, 4L, true, (string?)null, (long?)65536),
            (shared.Mechanism, shared.Number, shared.ByViews, shared.Name, shared.LargestView));
        Assert.Equal(Sorted([writer, reader]), Sorted(shared.Holders));
        Assert.Equal(("resource:hub:mapped-section:4", "Mapped section 4", "Mapped section"),
            (shared.Key, shared.Label, ResourceLink.KindOf(shared.Key)));
        Assert.True(ResourceLink.IsMembershipKey(shared.MembershipKey(writer)));
        Assert.Same(overview.ResourceLinks, OverviewWorkspace.From(overview).ResourceLinks);

        // A named synchronization object two processes held is one too, named by its name's leaf.
        using var objects = new TemporarySession();
        (rows, fields) = NamedObjects();
        Publish(objects.Store, rows, fields: fields);
        ResourceLink ready = Assert.Single(SessionOverviewProjector.Project(objects.Store).ResourceLinks);
        Assert.Equal(("resource:hub:object:1", "Ready", "Synchronization object", Mechanism.Synchronization),
            (ready.Key, ready.Label, ResourceLink.KindOf(ready.Key), ready.Mechanism));

        // A session whose processes made no record of a mechanism with resources has none.
        using var plain = new TemporarySession();
        Publish(plain.Store, [Lifecycle(1, ObservationKind.Create, 100, 1)]);
        Assert.Empty(SessionOverviewProjector.Project(plain.Store).ResourceLinks);
    }

    [Fact(DisplayName = "§6.3: the graph draws a resource two drawn nodes' processes held as a hub, joined to each by a membership with no traffic value and no direction")]
    public void TheGraphDrawsAHubJoinedByMemberships()
    {
        // The first two processes are related; the third, related to none, holds a section with the first, so it is drawn on
        // its own rather than counted in No relationships, as the fourth and the fifth are.
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 5).Select(index => Node(index, group.Key))];
        CommunicationEdge pair = Edge("pair", processes[0], processes[1], 10);
        ResourceLink section = new(Mechanism.SharedSection, 4, true, null, [processes[0].Id, processes[2].Id]) { LargestView = 65536 };
        WorkspaceSnapshot snapshot = Snapshot([group], processes, [pair], [section]);
        GraphDisplay display = GraphProjection.Project(snapshot);
        Assert.Equal((GraphNodeKind.Process, GraphNodeKind.Quiet), (display.NodeOf(processes[2].Id)!.Kind, display.NodeOf(processes[3].Id)!.Kind));

        GraphDisplayNode hub = Assert.Single(display.Nodes, node => node.IsHub);
        Assert.Equal((section.Key, "Mapped section 4", GraphNodeKind.SharedResource, 2, 0L, 0),
            (hub.Key, hub.Label, hub.Kind, hub.ConnectedProcesses, hub.Observations, hub.Relationships));
        Assert.Empty(hub.Members);
        Assert.Equal((true, false), (hub.IsResource, hub.IsHost));

        // A membership from each holder's node, naming its membership: none is a relationship, and none says who made it.
        string first = display.NodeOf(processes[0].Id)!.Key;
        string third = display.NodeOf(processes[2].Id)!.Key;
        GraphDisplayEdge[] memberships = [.. display.Edges.Where(edge => edge.Membership)];
        Assert.Equal(new[] { first, third }.Order(StringComparer.Ordinal), memberships.Select(edge => edge.SourceKey).Order(StringComparer.Ordinal));
        Assert.All(memberships, edge => Assert.Equal((hub.Key, 0, 0, 0L, false, 1.25),
            (edge.TargetKey, edge.MadeFromSource, edge.MadeFromTarget, edge.ObservationCount, edge.ToHosts, GraphEncoding.EdgeThickness(edge, 1_000))));
        Assert.Same(display.EdgeOfMembership(section.MembershipKey(processes[0].Id)), memberships.Single(edge => edge.SourceKey == first));
        Assert.Null(display.EdgeOf(section.MembershipKey(processes[0].Id)));
        Assert.Equal((1, 1), (display.TotalRelationships, display.Edges.Count(edge => !edge.Membership)));

        // It sets no scale and is drawn at a fixed size, as a host is; the layout places it among its holders.
        Assert.Equal(9, GraphEncoding.NodeRadius(hub, GraphEncoding.NodeScale(display)));
        Assert.Equal(10L, GraphEncoding.EdgeScale(display));
        Assert.True(GraphLayout.ComputeDisplay("hubs", display).Positions[hub.Key].IsValid);

        // Re-counted over an interval, it is drawn as it was; a brush never re-counts a membership.
        GraphDisplay rescoped = GraphProjection.Rescope(display, snapshot, snapshot with { Edges = [pair with { ObservationCount = 3 }] });
        Assert.Equal(display.Nodes.Select(node => node.Key), rescoped.Nodes.Select(node => node.Key));
        Assert.Equal(display.Edges.Where(edge => edge.Membership).Select(edge => edge.Key), rescoped.Edges.Where(edge => edge.Membership).Select(edge => edge.Key));

        // With InterCat's own process set aside, a resource it held stays a hub while two others hold it, and goes when one does.
        ResourceLink three = new(Mechanism.Synchronization, 1, false, null, [processes[0].Id, processes[1].Id, processes[3].Id]);
        ResourceLink lone = new(Mechanism.SharedSection, 6, true, null, [processes[2].Id, processes[3].Id]);
        WorkspaceSnapshot own = Snapshot([group], [processes[0], processes[1], processes[2], processes[3] with { Collector = CollectorRole.Client }],
            [pair], [lone, three]);
        WorkspaceSnapshot aside = WorkspaceCollectors.SetAside(own);
        Assert.Equal([three.Key], aside.ResourceLinks.Select(link => link.Key));
        Assert.Equal(Sorted([processes[0].Id, processes[1].Id]), Sorted(aside.ResourceLinks[0].Holders));
    }

    [Fact(DisplayName = "§6.3: hubs are drawn the most holders first, as many as the budget allows once every process is placed, and none whose holders one drawn node or the rest of the machine holds")]
    public void HubsNeverFoldAProcess()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 4).Select(index => Node(index, group.Key))];
        CommunicationEdge[] edges = [Edge("a", processes[0], processes[1], 10), Edge("b", processes[2], processes[3], 10)];
        ResourceLink widest = new(Mechanism.Synchronization, 1, false, @"\BaseNamedObjects\Ready",
            [processes[0].Id, processes[1].Id, processes[2].Id]);
        ResourceLink pair = new(Mechanism.SharedSection, 2, true, null, [processes[0].Id, processes[3].Id]);
        WorkspaceSnapshot snapshot = Snapshot([group], processes, edges, [widest, pair]);
        static string[] Hubs(GraphDisplay display) => [.. display.Nodes.Where(node => node.IsHub).Select(node => node.Key)];

        // The four processes and both hubs fit eight nodes; five hold the processes and the widest hub alone.
        GraphDisplay both = GraphProjection.Project(snapshot, budget: new(8, 50, 25));
        Assert.Equal([widest.Key, pair.Key], Hubs(both));
        Assert.Equal("Ready", both.Node(widest.Key)!.Label);
        GraphDisplay one = GraphProjection.Project(snapshot, budget: new(5, 50, 25));
        Assert.Equal([widest.Key], Hubs(one));
        Assert.Equal(4, one.Nodes.Count(node => node.Kind == GraphNodeKind.Process));
        Assert.Empty(Hubs(GraphProjection.Project(snapshot, budget: new(8, 50, 25, HubsDrawn: 0))));

        // A collapsed group holds every holder in one drawn node: no hub joins anything apart.
        Assert.Empty(Hubs(GraphProjection.Project(snapshot, budget: new(8, 50, 2))));

        // A focused rung leaves holders outside its neighbourhood to their own rung.
        GraphDisplay focused = GraphProjection.Project(snapshot, neighborhood: new HashSet<ProcessInstanceId> { processes[0].Id, processes[1].Id });
        Assert.Equal([widest.Key], Hubs(focused));
        Assert.Equal(2, focused.Node(widest.Key)!.ConnectedProcesses);
    }

    [Fact(DisplayName = "§6.7: a hub a search went to is drawn first, whatever the budget would draw before it, its holders kept apart, and a re-count of the drawing draws the same hubs")]
    public void ASearchedHubIsDrawnFirst()
    {
        // The budget draws one hub: the widest, unless the search went to the pair's section, whose other holder is related
        // to nothing else and would otherwise be counted in No relationships with the last process.
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 6).Select(index => Node(index, group.Key))];
        CommunicationEdge[] edges = [Edge("a", processes[0], processes[1], 10), Edge("b", processes[2], processes[3], 10)];
        ResourceLink widest = new(Mechanism.Synchronization, 1, false, @"\BaseNamedObjects\Ready",
            [processes[0].Id, processes[1].Id, processes[2].Id]);
        ResourceLink pair = new(Mechanism.SharedSection, 2, true, null, [processes[0].Id, processes[4].Id]);
        WorkspaceSnapshot snapshot = Snapshot([group], processes, edges, [widest, pair]);
        static string[] Hubs(GraphDisplay display) => [.. display.Nodes.Where(node => node.IsHub).Select(node => node.Key)];
        GraphDisplayBudget one = new(8, 50, 25, HubsDrawn: 1);

        GraphDisplay usual = GraphProjection.Project(snapshot, budget: one);
        Assert.Equal([widest.Key], Hubs(usual));
        Assert.Equal(GraphNodeKind.Quiet, usual.NodeOf(processes[4].Id)!.Kind);
        GraphDisplay kept = GraphProjection.Project(snapshot, budget: one, keptHub: pair.Key);
        Assert.Equal([pair.Key], Hubs(kept));
        Assert.Equal(GraphNodeKind.Process, kept.NodeOf(processes[4].Id)!.Kind);
        Assert.Equal([pair.Key, widest.Key], Hubs(GraphProjection.Project(snapshot, keptHub: pair.Key)));

        // A key no resource of the session has keeps nothing; a re-count keeps the hub the search went to.
        Assert.Equal([widest.Key], Hubs(GraphProjection.Project(snapshot, budget: one, keptHub: "resource:hub:object:99")));
        Assert.Equal([pair.Key], Hubs(GraphProjection.Rescope(kept, snapshot, snapshot)));
    }

    [Fact(DisplayName = "§6.7: the search finds a resource two or more processes held by its name, the object directory it lies in or what it is, and its hit leads where its holders are drawn apart")]
    public void TheSearchFindsAResource()
    {
        ProcessGroup g = new("g", "g.exe", LaneGrouping.Executable);
        ProcessGroup h = new("h", "h.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [Node(1, g.Key), Node(2, g.Key), Node(3, h.Key)];
        ResourceLink ready = new(Mechanism.Synchronization, 1, false, @"\Sessions\1\BaseNamedObjects\Ready",
            [processes[0].Id, processes[1].Id]) { Records = 4 };
        ResourceLink shared = new(Mechanism.SharedSection, 4, true, null, [processes[0].Id, processes[2].Id]) { Records = 6 };
        WorkspaceSnapshot snapshot = Snapshot([g, h], processes, [], [ready, shared]);

        // By its name's leaf: one program's processes held it, so its hit leads to that program's rung, which draws them apart.
        SearchHit hit = Assert.Single(WorkspaceSearch.Find(snapshot, "ready").Hits);
        Assert.Equal((SearchHitKind.Resource, ready.Key, "Ready", @"Synchronization object held by 2 processes of g.exe · \Sessions\1\BaseNamedObjects", 4L),
            (hit.Kind, hit.Key, hit.Label, hit.Detail, hit.ObservationCount));
        Assert.Equal([g.Key], hit.Path);

        // By the object directory its name lies in.
        Assert.Equal([ready.Key], WorkspaceSearch.Find(snapshot, "BaseNamedObjects").Hits.Select(found => found.Key));

        // By what it is, where no record named it: several programs' processes held it, so its hit leads to the machine rung.
        SearchHit section = Assert.Single(WorkspaceSearch.Find(snapshot, "mapped section").Hits);
        Assert.Equal((shared.Key, "Mapped section 4", "Mapped section held by 2 processes", 6L),
            (section.Key, section.Label, section.Detail, section.ObservationCount));
        Assert.Empty(section.Path);
        Assert.True(WorkspaceSearch.FindsResource(shared, " Mapped section 4 "));
        Assert.False(WorkspaceSearch.FindsResource(shared, "Ready"));
        Assert.False(WorkspaceSearch.FindsResource(ready, " "));
    }

    [Fact(DisplayName = "§6.4: a hub's key names its resource, and an evidence page read by it lists exactly the records its gate numbered it in")]
    public void AHubsKeyReadsItsRecords()
    {
        Assert.True(ResourceLink.TryParseKey("resource:hub:mapped-section:4", out Mechanism mechanism, out bool byViews, out long number));
        Assert.Equal((Mechanism.SharedSection, true, 4L), (mechanism, byViews, number));
        Assert.True(ResourceLink.TryParseKey("resource:hub:object:12", out mechanism, out byViews, out number));
        Assert.Equal((Mechanism.Synchronization, false, 12L), (mechanism, byViews, number));
        Assert.All(NoHubKeys, key => Assert.False(ResourceLink.TryParseKey(key, out _, out _, out _)));

        // Section 4's views by their key, its handles by theirs: never joined, each its own records.
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        SessionEvidencePage views = SessionEvidenceQuery.Read(session.Store, operationKey: "resource:hub:mapped-section:4");
        Assert.Equal([ObservationKind.Map, ObservationKind.Map, ObservationKind.Unmap, ObservationKind.Unmap],
            views.Records.Select(record => record.Observation.Kind));
        Assert.Equal(2, SessionEvidenceQuery.Read(session.Store, operationKey: "resource:hub:section:4").Records.Count);
        Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store, operationKey: "resource:hub:object:9"));
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, operationKey: "resource:hub:socket:9"));
    }

    [Fact(DisplayName = "§6.3: a hub holds no process, only memberships run to it and none from it, and each membership is drawn once")]
    public void ImpossibleHubsAreRefused()
    {
        ProcessInstanceId first = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        ProcessInstanceId second = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        ProcessInstanceId third = new(Guid.Parse("00000000-0000-0000-0000-000000000003"));
        GraphDisplayNode a = new("a", "a", GraphNodeKind.Process, null, first, 1, [first], 0, 0, 0);
        GraphDisplayNode b = new("b", "b", GraphNodeKind.Process, null, second, 2, [second], 0, 0, 0);
        GraphDisplayNode hub = new("resource:hub:object:1", "Ready", GraphNodeKind.SharedResource, null, null, null, [], 0, 0, 0);
        static GraphDisplayEdge Membership(string key, string source, string target, params string[] memberships) =>
            new(key, source, target, Mechanism.Synchronization, 0, RelationStrength.Direct, memberships) { Membership = true };

        GraphDisplay valid = new([a, b, hub], [Membership("m1", "a", hub.Key, "membership:x:1"), Membership("m2", "b", hub.Key, "membership:x:2")],
            2, null, null);
        Assert.Equal("m1", valid.EdgeOfMembership("membership:x:1")!.Key);

        Assert.Throws<ArgumentException>(() => new GraphDisplay(
            [a, b, hub with { Members = [third] }], [], 3, null, null));
        Assert.Throws<ArgumentException>(() => new GraphDisplay([a, b, hub], [Membership("m", "a", "b", "membership:x:1")], 2, null, null));
        Assert.Throws<ArgumentException>(() => new GraphDisplay([a, b, hub],
            [new("r", "a", hub.Key, Mechanism.Tcp, 1, RelationStrength.Direct, ["pair"])], 2, null, null));
        Assert.Throws<ArgumentException>(() => new GraphDisplay([a, b, hub], [Membership("m", hub.Key, "a", "membership:x:1")], 2, null, null));
        Assert.Throws<ArgumentException>(() => new GraphDisplay([a, b, hub],
            [Membership("m1", "a", hub.Key, "membership:x:1"), Membership("m2", "b", hub.Key, "membership:x:1")], 2, null, null));
        Assert.Throws<ArgumentException>(() => new GraphDisplay([a, b, hub], [Membership("m", "a", hub.Key, "pair")], 2, null, null));

        // A snapshot's resource link is held by two of its processes or more, each once.
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 2).Select(index => Node(index, group.Key))];
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Snapshot([group], processes, [],
            [new ResourceLink(Mechanism.Synchronization, 1, false, null, [processes[0].Id, processes[0].Id])])));
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Snapshot([group], processes, [],
            [new ResourceLink(Mechanism.Synchronization, 1, false, null, [processes[0].Id, third])])));
    }

    /// <summary>Keys that name no hub: a socket's, a number zero or none, and a host's.</summary>
    private static readonly string[] NoHubKeys = ["resource:hub:socket:4", "resource:hub:section:0", "resource:hub:section:x", "resource:host:10.0.0.1"];

    private static string[] Sorted(IEnumerable<ProcessInstanceId> ids) =>
        [.. ids.Select(id => id.ToString()).Order(StringComparer.Ordinal)];

    private static WorkspaceSnapshot Snapshot(
        IReadOnlyList<ProcessGroup> groups,
        IReadOnlyList<ProcessNode> processes,
        IReadOnlyList<CommunicationEdge> edges,
        IReadOnlyList<ResourceLink>? resources = null) =>
        new("Resource hubs test", new TimeRange(0, 10), groups, processes, edges, [], [], [], []) { ResourceLinks = resources ?? [] };

    private static ProcessNode Node(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        1_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target, long observations) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, observations, null, RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [],
        };
}
