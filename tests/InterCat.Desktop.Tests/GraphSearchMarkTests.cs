using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// What a search finds in the graph (§6.7): while a search is typed, each node holding a process it finds is marked -
/// wholly where every process the node holds is found, in part where some are - by the rule its hits and the timeline's
/// lanes are found by, the Other hosts node where an address its connections ran to is found, and the summary and a found
/// node's card say so.
/// </summary>
public sealed class GraphSearchMarkTests
{
    [Fact(DisplayName = "§6.7: a search marks the graph's nodes holding the processes it finds, wholly or in part, and Other hosts for an address or an endpoint, and says so")]
    public void ASearchMarksTheNodesItFinds() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplay drawn = workspace.GraphDisplay;
        GraphDisplayNode browser = drawn.NodeOf(Process(workspace, 300).Id)!;
        GraphDisplayNode tools = drawn.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet);
        GraphDisplayNode hub = drawn.OtherHosts!;
        Assert.Equal(3, tools.Members.Count);
        string summary = workspace.GraphSummary;

        // No search marks nothing.
        Assert.Empty(workspace.SearchedGraphNodeKeys);
        Assert.Empty(workspace.PartlySearchedGraphNodeKeys);

        // A name finds its process, and the graph's summary counts it and its card says so.
        workspace.SearchText = "browser";
        Assert.Equal([browser.Key], workspace.SearchedGraphNodeKeys);
        Assert.Empty(workspace.PartlySearchedGraphNodeKeys);
        Assert.Equal(summary + " · 1 found by the search", workspace.GraphSummary);
        Assert.Contains("Found by the search", workspace.DescribeGraphHover(browser.Key)!.Lines);

        // An aggregate every process of which is found is marked wholly; one some of which are, in part.
        workspace.SearchText = "tool";
        Assert.Equal([tools.Key], workspace.SearchedGraphNodeKeys);
        Assert.Contains("Found by the search: 3 of its 3 processes", workspace.DescribeGraphHover(tools.Key)!.Lines);
        workspace.SearchText = "402";
        Assert.Empty(workspace.SearchedGraphNodeKeys);
        Assert.Equal([tools.Key], workspace.PartlySearchedGraphNodeKeys);
        Assert.Contains("Found by the search: 1 of its 3 processes", workspace.DescribeGraphHover(tools.Key)!.Lines);
        Assert.Equal(summary + " · 1 found by the search", workspace.GraphSummary);

        // An address finds the process that reached it, and the Other hosts node its connections stand for.
        workspace.SearchText = "142.250.1.1";
        Assert.Equal(new[] { browser.Key, hub.Key }.Order(StringComparer.Ordinal),
            workspace.SearchedGraphNodeKeys.Order(StringComparer.Ordinal));
        Assert.Equal("Found by the search: an address these connections reached", workspace.DescribeGraphHover(hub.Key)!.Lines[^1]);
        workspace.SearchText = "20.42.65.1";
        Assert.Empty(workspace.SearchedGraphNodeKeys);

        // An endpoint, the address at a port its connections ran to, or a port alone, finds them as the address does; the
        // address at another port finds nothing.
        workspace.SearchText = "142.250.1.1:443";
        Assert.Equal(new[] { browser.Key, hub.Key }.Order(StringComparer.Ordinal),
            workspace.SearchedGraphNodeKeys.Order(StringComparer.Ordinal));
        workspace.SearchText = ":443";
        Assert.Equal(new[] { browser.Key, hub.Key }.Order(StringComparer.Ordinal),
            workspace.SearchedGraphNodeKeys.Order(StringComparer.Ordinal));

        workspace.SearchText = "142.250.1.1:80";
        Assert.Empty(workspace.SearchedGraphNodeKeys);

        // A search that finds nothing says so; cleared, the graph is as it was.
        workspace.SearchText = "no such name";
        Assert.Empty(workspace.SearchedGraphNodeKeys);
        Assert.Equal(summary + " · none found by the search", workspace.GraphSummary);
        workspace.SearchText = string.Empty;
        Assert.Empty(workspace.SearchedGraphNodeKeys);
        Assert.Equal(summary, workspace.GraphSummary);
        Assert.DoesNotContain(workspace.DescribeGraphHover(browser.Key)!.Lines,
            line => line.StartsWith("Found by the search", StringComparison.Ordinal));

        // The marks follow the drawing. A search kept while the browser's rung opens marks there the one node standing for
        // the rest of the machine, in part, where the machine rung marked the server itself.
        workspace.SearchText = "server";
        Assert.Equal([drawn.NodeOf(Process(workspace, 200).Id)!.Key], workspace.SearchedGraphNodeKeys);
        Assert.True(workspace.OpenGraphNode(browser.Key));
        await workspace.LayoutReady;
        GraphDisplayNode rest = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Context);
        Assert.Equal([rest.Key], workspace.PartlySearchedGraphNodeKeys);
        Assert.Empty(workspace.SearchedGraphNodeKeys);
    });

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// client.exe and server.exe related over loopback; browser.exe's TCP to another host; and three instances of
    /// tool.exe that do nothing the graph draws.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        .. Enumerable.Range(0, 3).Select(index => Lifecycle(4 + index, ObservationKind.Create, 401 + index, (ulong)(4 + index))
            with { ResourceName = @"C:\Tools\tool.exe", SessionRelativeTicks = 130 + index }),
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 11)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 200 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
    ];
}
