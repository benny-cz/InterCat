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

    [Fact(DisplayName = "§6.7: the search finds a hub by its resource, marks it and rings it with its holders, and going to it from another rung chooses it where its holders are drawn apart")]
    public void TheSearchGoesToAHub() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        string hub = Assert.Single(workspace.GraphDisplay.Nodes, node => node.IsHub).Key;
        string writer = workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id)!.Key;
        string reader = workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 200).Id)!.Key;

        // Typed, the search finds the section by what it is, marks its hub, counts it and says so on the hub's card.
        workspace.SearchText = "mapped section 4";
        SearchRow found = Assert.Single(workspace.SearchResults);
        Assert.Equal(("RESOURCE", "Mapped section 4", "Mapped section held by 2 processes", "4 records"),
            (found.Kind, found.Label, found.Detail, found.Observations));
        Assert.Equal("Resource, Mapped section 4, Mapped section held by 2 processes, 4 records. Press Enter to open.", found.AccessibleName);
        Assert.Equal([hub], workspace.SearchedGraphNodeKeys);
        Assert.StartsWith("1 found by the search · ", workspace.GraphSummary, StringComparison.Ordinal);
        Assert.Equal("Found by the search: its resource", workspace.DescribeGraphHover(hub)!.Lines[^1]);

        // Chosen among the hits, it is ringed with the nodes of the processes holding it.
        workspace.SelectedSearchResult = found;
        Assert.Equal(new[] { hub, reader, writer }.Order(StringComparer.Ordinal),
            workspace.ChosenHitGraphNodeKeys.Order(StringComparer.Ordinal));

        // Gone to from writer.exe's own rung, it is chosen on the machine rung, where both programs' processes are drawn
        // apart, and the search is cleared.
        Assert.True(workspace.OpenGraphNode(writer));
        Assert.Equal(3, workspace.Crumbs.Count);
        workspace.SearchText = "mapped section 4";
        Assert.True(workspace.OpenSearchResult(Assert.Single(workspace.SearchResults)));
        Assert.Single(workspace.Crumbs);
        Assert.Equal((string.Empty, hub), (workspace.SearchText, workspace.SelectedCluster?.Key));
        Assert.Equal("Mapped section 4", workspace.SelectionTitle);
    });

    [Fact(DisplayName = "§6.7: going to a resource whose hub the graph's budget left undrawn draws it first and chooses it")]
    public void GoingToAnUndrawnHubDrawsIt() => SingleThreadedContext.Run(async () =>
    {
        // Nine more sections both processes mapped, ordered before section 4 among hubs of as many holders, so the budget's
        // eight hubs are theirs.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        var more = new List<ObservationRowV1>();
        var numbered = new List<SourceFieldRowV1>();
        ulong ordinal = 1_000;
        for (int section = 101; section <= 109; section++)
        {
            for (int pid = 100; pid <= 200; pid += 100)
            {
                ObservationRowV1 mapped = SectionRecord(section - 80, 37, pid, pid, ordinal++, view: true);
                more.Add(mapped);
                numbered.AddRange([Field(mapped, SourceField.SectionNumber, section), Field(mapped, SourceField.SectionViewNumber, (section * 10L) + (pid / 100)),
                    Field(mapped, SourceField.ViewSize, 4096), Field(mapped, SourceField.ViewInformation, 0)]);
            }
        }

        using var session = new TemporarySession();
        Publish(session.Store, [.. rows.Concat(more).OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)
            .Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: [.. fields, .. numbered]);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        const string Four = "resource:hub:mapped-section:4";
        Assert.Equal(8, workspace.GraphDisplay.Nodes.Count(node => node.IsHub));
        Assert.Null(workspace.GraphDisplay.Node(Four));

        // Gone to, section 4's hub is drawn among the eight, first, and chosen.
        workspace.SearchText = "mapped section 4";
        Assert.True(workspace.OpenSearchResult(Assert.Single(workspace.SearchResults)));
        Assert.Equal(Four, workspace.SelectedCluster?.Key);
        Assert.Equal(Four, workspace.GraphDisplay.Nodes.First(node => node.IsHub).Key);
        Assert.Equal(8, workspace.GraphDisplay.Nodes.Count(node => node.IsHub));

        // Left for another rung, it is let go: back on the machine rung, the budget's eight are drawn again.
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id)!.Key));
        workspace.ReturnTo(0);
        Assert.Null(workspace.GraphDisplay.Node(Four));
    });

    [Fact(DisplayName = "§6.7: going to a resource one program's processes held chooses its hub on that program's rung, which draws them apart")]
    public void GoingToOneProgramsHubOpensItsRung() => SingleThreadedContext.Run(async () =>
    {
        // Both processes run writer.exe.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        using var session = new TemporarySession();
        Publish(session.Store, [.. rows.Select(row => row with
        {
            SessionRelativeTicks = row.NativeTicks * 100,
            ResourceName = row.ResourceName == @"C:\Tools\reader.exe" ? @"C:\Tools\writer.exe" : row.ResourceName,
        })], fields: fields);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        workspace.SearchText = "mapped section 4";
        SearchRow found = Assert.Single(workspace.SearchResults);
        Assert.Equal("Mapped section held by 2 processes of writer.exe", found.Detail);
        Assert.True(workspace.OpenSearchResult(found));
        Assert.Equal(2, workspace.Crumbs.Count);
        Assert.Equal("resource:hub:mapped-section:4", workspace.SelectedCluster?.Key);
    });

    [Fact(DisplayName = "§6.3: with nothing chosen on the machine rung, the inspector lists the session's resources by namespace, idle sockets last, and one chosen there is its own records, which the timeline highlights, the card counts and E lists")]
    public void TheInspectorListsResourcesByNamespace() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        Assert.True(workspace.ShowsResources);
        Assert.Equal("5 resources by namespace, 1 idle listed last", workspace.ResourcesHeading);
        Assert.Equal(
            [
                ("Ready", @"Synchronization object · \Sessions\1\BaseNamedObjects · held by server.exe · PID 100 · 2 records"),
                ("Unix socket 6", "unnamed · held by server.exe · PID 100 · 5 records · 1 send, 1 receive"),
                ("Unix socket 7", "unnamed · held by client.exe · PID 200 · 5 records · 1 send, 1 receive"),
                ("Unix socket 8", "unnamed · held by client.exe · PID 200 · 1 record · 1 send, 0 receives"),
                ("Unix socket 5", "unnamed · held by server.exe · PID 100 · 3 records · idle"),
            ],
            workspace.ResourceRows.Select(row => (row.Label, row.Detail)));
        Assert.Equal("Unix socket 5, unnamed · held by server.exe · PID 100 · 3 records · idle. Press E to list its records, or Enter to choose the process holding it.",
            workspace.ResourceRows[^1].AccessibleName);
        Assert.False(workspace.OffersResourcesToggle);
        Assert.Equal("Records E lists", workspace.EvidenceHeading);

        // Chosen, socket 6 is its own records: the card counts them and what it carried, and the timeline highlights them.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        workspace.ChosenResourceRow = workspace.ResourceRows[1];
        Assert.Equal("resource:hub:socket:6", workspace.ChosenResourceRow?.Key);
        Assert.Equal(("Selected resource", "5 records of its socket · 1 send, 1 receive"), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        await workspace.HighlightReady;
        Assert.Equal(5, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));

        // A process chosen in the graph hides the list and lets its choice go.
        workspace.SelectGraphNode(workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id)!.Key);
        Assert.Equal((false, 0, (ResourceRow?)null), (workspace.ShowsResources, workspace.ResourceRows.Count, workspace.ChosenResourceRow));
        workspace.ClearSelection();
        Assert.Equal((true, (ResourceRow?)null), (workspace.ShowsResources, workspace.ChosenResourceRow));

        // Chosen again, E lists exactly its records, its filter naming the resource and why.
        workspace.ChosenResourceRow = workspace.ResourceRows[^1];
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(3, workspace.RungRows.Count);
        Assert.StartsWith("Records of Unix socket 5", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.False(workspace.ShowsResources);
        FilterRow scope = Assert.Single(workspace.Filters, filter => filter.Field == "scope");
        Assert.EndsWith("with the resource Unix socket 5 chosen in its list: exactly its records, whichever process they bind to.",
            scope.Reason, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.3: the inspector names the first resources and lists every one when asked, saying where the rest are read")]
    public void TheInspectorListsEveryResourceWhenAsked() => SingleThreadedContext.Run(async () =>
    {
        // Section 4's views, then nine more sections both processes mapped: thirteen resources but the image's.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        var more = new List<ObservationRowV1>();
        var numbered = new List<SourceFieldRowV1>();
        ulong ordinal = 1_000;
        for (int section = 101; section <= 109; section++)
        {
            ObservationRowV1 mapped = SectionRecord(section - 80, 37, 100, 100, ordinal++, view: true);
            more.Add(mapped);
            numbered.AddRange([Field(mapped, SourceField.SectionNumber, section), Field(mapped, SourceField.SectionViewNumber, section * 10L),
                Field(mapped, SourceField.ViewSize, 4096), Field(mapped, SourceField.ViewInformation, 0)]);
        }

        using var session = new TemporarySession();
        Publish(session.Store, [.. rows.Concat(more).OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)
            .Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: [.. fields, .. numbered]);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        Assert.Equal(("13 resources by namespace", 8, true, "Show all 13 resources"),
            (workspace.ResourcesHeading, workspace.ResourceRows.Count, workspace.OffersResourcesToggle, workspace.ResourcesToggleLabel));
        Assert.True(workspace.ToggleResources());
        Assert.Equal((13, "Show the first 8", string.Empty), (workspace.ResourceRows.Count, workspace.ResourcesToggleLabel, workspace.ResourcesMore));
        Assert.False(workspace.ToggleResources());
        Assert.Equal(8, workspace.ResourceRows.Count);

        // Every one asked for on the machine's list is the machine's alone: writer.exe's eleven are named eight at first.
        Assert.True(workspace.ToggleResources());
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "writer.exe");
        Assert.Equal((8, "Show all 11 resources"), (workspace.ResourceRows.Count, workspace.ResourcesToggleLabel));
    });

    [Fact(DisplayName = "§6.3: Enter on a listed resource chooses where it is held - the process holding it, or its hub, on the machine rung or its holders' program's where the machine rung draws them as one node - and Esc comes back to its row")]
    public void EnterOnAListedResourceChoosesWhereItIsHeld() => SingleThreadedContext.Run(async () =>
    {
        // A socket one process held: Enter chooses that process; back, nothing is chosen and the socket is chosen in the list.
        using var sockets = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(sockets.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using (WorkspaceViewModel workspace = Open(sockets))
        {
            await workspace.LayoutReady;
            ResourceRow socket = workspace.ResourceRows.Single(row => row.Label == "Unix socket 7");
            Assert.Equal("the process holding it", socket.EnterChooses);
            Assert.True(workspace.ChooseResource(socket));
            Assert.Equal((200, false), (workspace.SelectedProcess?.ProcessId, workspace.ShowsResources));
            Assert.False(workspace.ChooseResource(socket));
            Assert.Equal(socket.Key, workspace.ComeBackFromResource()?.Key);
            Assert.Equal((true, (int?)null, socket.Key), (workspace.ShowsResources, workspace.SelectedProcess?.ProcessId, workspace.ChosenResourceRow?.Key));

            // A way back the selection has since left is let go, and so is one the ladder has left.
            Assert.True(workspace.ChooseResource(socket));
            workspace.ClearSelection();
            Assert.Null(workspace.ComeBackFromResource());
            Assert.True(workspace.ChooseResource(socket));
            Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 200).Id)!.Key));
            Assert.Null(workspace.ComeBackFromResource());
        }

        // E from the process Enter chose keeps it chosen on the evidence rung, a rung the way back was not made at.
        using (WorkspaceViewModel workspace = Open(sockets))
        {
            await workspace.LayoutReady;
            Assert.True(workspace.ChooseResource(workspace.ResourceRows.Single(row => row.Label == "Unix socket 7")));
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Assert.Null(workspace.ComeBackFromResource());
        }

        // A section two processes of one program mapped, which the machine rung draws apart: its hub, chosen there.
        using var one = new TemporarySession();
        (rows, fields) = MappedSections();
        Publish(one.Store, [.. rows.Select(row => row with
        {
            SessionRelativeTicks = row.NativeTicks * 100,
            ResourceName = row.ResourceName == @"C:\Tools\reader.exe" ? @"C:\Tools\writer.exe" : row.ResourceName,
        })], fields: fields);
        using (WorkspaceViewModel workspace = Open(one))
        {
            await workspace.LayoutReady;
            ResourceRow hub = workspace.ResourceRows.Single(row => row.Key == "resource:hub:mapped-section:4");
            Assert.True(workspace.ChooseResource(hub));
            Assert.Equal((hub.Key, 1), (workspace.SelectedCluster?.Key, workspace.Crumbs.Count));
        }

        // A section two programs' processes mapped: its hub, chosen on the machine rung.
        using var mapped = new TemporarySession();
        (rows, fields) = MappedSections();
        Publish(mapped.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using (WorkspaceViewModel workspace = Open(mapped))
        {
            await workspace.LayoutReady;
            ResourceRow hub = workspace.ResourceRows.Single(row => row.Key == "resource:hub:mapped-section:4");
            Assert.Equal("its hub", hub.EnterChooses);
            Assert.True(workspace.ChooseResource(hub));
            Assert.Equal((hub.Key, 1), (workspace.SelectedCluster?.Key, workspace.Crumbs.Count));
            Assert.Equal(hub.Key, workspace.ComeBackFromResource()?.Key);
            Assert.Equal((hub.Key, (string?)null), (workspace.ChosenResourceRow?.Key, workspace.SelectedCluster?.Key));
        }

        // A section 26 processes of one program mapped, which the machine rung draws as that program's one node: its hub is
        // chosen on the program's rung, and the way back is the machine rung's again.
        var workers = new List<ObservationRowV1>();
        var views = new List<SourceFieldRowV1>();
        for (int index = 0; index < 26; index++)
        {
            int pid = 1_000 + index;
            workers.Add(Lifecycle(1, ObservationKind.Create, pid, (ulong)(10 + index)) with { ResourceName = @"C:\Tools\worker.exe" });
            ObservationRowV1 view = SectionRecord(10 + index, 37, pid, pid, (ulong)(100 + index), view: true);
            workers.Add(view);
            views.AddRange([Field(view, SourceField.SectionNumber, 77), Field(view, SourceField.SectionViewNumber, pid),
                Field(view, SourceField.ViewSize, 4096), Field(view, SourceField.ViewInformation, 0)]);
        }

        using var crowd = new TemporarySession();
        Publish(crowd.Store, [.. workers.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)
            .Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: [.. views]);
        using (WorkspaceViewModel workspace = Open(crowd))
        {
            await workspace.LayoutReady;
            Assert.DoesNotContain(workspace.GraphDisplay.Nodes, node => node.IsHub);
            ResourceRow shared = Assert.Single(workspace.ResourceRows);
            Assert.True(workspace.ChooseResource(shared));
            Assert.Equal((shared.Key, 2), (workspace.SelectedCluster?.Key, workspace.Crumbs.Count));
            Assert.Equal(shared.Key, workspace.ComeBackFromResource()?.Key);
            Assert.Equal((1, shared.Key, (string?)null), (workspace.Crumbs.Count, workspace.ChosenResourceRow?.Key, workspace.SelectedCluster?.Key));
        }
    });

    [Fact(DisplayName = "§6.3: a program chosen or opened lists the resources its processes held, one chosen there its own records rather than the program's, and Esc from Enter on one comes back to the program")]
    public void AProgramListsItsProcessesResources() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // server.exe chosen: its object and its two sockets, the idle one last, under the program's own heading.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.True(workspace.ShowsResources);
        Assert.Equal("3 resources its processes held, by namespace, 1 idle listed last", workspace.ResourcesHeading);
        Assert.Equal(["Ready", "Unix socket 6", "Unix socket 5"], workspace.ResourceRows.Select(row => row.Label));
        Assert.StartsWith("3 resources its processes held, by namespace, 1 idle listed last: the resources its processes held, by",
            workspace.ResourcesListName, StringComparison.Ordinal);

        // A socket chosen there is its own records rather than the program's: the card counts them, the timeline highlights them.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        workspace.ChosenResourceRow = workspace.ResourceRows[1];
        Assert.Equal(("Selected resource", "5 records of its socket · 1 send, 1 receive"), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        await workspace.HighlightReady;
        Assert.Equal(5, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));

        // Enter chooses server.exe's process; Esc comes back to the program, the socket chosen in its list again.
        Assert.True(workspace.ChooseResource(workspace.ResourceRows[1]));
        Assert.Equal((100, false), (workspace.SelectedProcess?.ProcessId, workspace.ShowsResources));
        ResourceHop? back = workspace.ComeBackFromResource();
        Assert.Equal("resource:hub:socket:6", back?.Key);
        Assert.NotNull(back?.Program);
        Assert.Equal(("3 resources its processes held, by namespace, 1 idle listed last", "resource:hub:socket:6", (int?)null),
            (workspace.ResourcesHeading, workspace.ChosenResourceRow?.Key, workspace.SelectedProcess?.ProcessId));

        // client.exe chosen instead lets the choice go and lists its own; server.exe chosen again has none chosen.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.Equal(["Unix socket 7", "Unix socket 8"], workspace.ResourceRows.Select(row => row.Label));
        Assert.Null(workspace.ChosenResourceRow);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Assert.Null(workspace.ChosenResourceRow);

        // server.exe opened: its rung describes it, and lists its processes' resources too.
        Assert.True(workspace.Descend());
        Assert.Equal("3 resources its processes held, by namespace, 1 idle listed last", workspace.ResourcesHeading);

        // writer.exe opened, whose section's other holder its rung leaves to its own: Enter on the section, which that rung
        // draws no hub for, chooses the processes holding it together.
        using var mapped = new TemporarySession();
        (rows, fields) = MappedSections();
        Publish(mapped.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        using WorkspaceViewModel opened = Open(mapped);
        await opened.LayoutReady;
        opened.SelectedRung = opened.RungRows.Single(row => row.Label == "writer.exe");
        Assert.True(opened.Descend());
        Assert.True(opened.ChooseResource(opened.ResourceRows.Single(row => row.Key == "resource:hub:mapped-section:4")));
        Assert.Equal((true, (string?)null), (opened.HasMultiSelection, opened.SelectedCluster?.Key));
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }
}
