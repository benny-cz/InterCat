using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The Other hosts only node in the window (§6.3): a busy machine's processes that only reach other hosts, past the eight
/// busiest, counted in one node so the relationships between processes are not buried in a star of them; it says what it
/// folds wherever it is read, and chosen, the card counts its processes' records as any aggregate's.
/// </summary>
public sealed class OtherHostsOnlyWorkspaceTests
{
    [Fact(DisplayName = "§6.3: the less busy processes that only reach other hosts are one node that says what it folds, chosen as an aggregate is")]
    public void TheLessBusyAreOneNode() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplay drawn = workspace.GraphDisplay;
        GraphDisplayNode folded = Assert.Single(drawn.Nodes, node => node.Kind == GraphNodeKind.OtherHostsOnly);

        // The four least busy of the twelve are folded; the summary says the graph is drawn as fewer nodes.
        Assert.Equal(["app0.exe", "app1.exe", "app2.exe", "app3.exe"],
            workspace.Snapshot.Processes.Where(process => folded.Members.Contains(process.Id)).Select(process => process.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal("14 processes · 1 relationship among 2 of them · drawn as 11 nodes · paired TCP and UDP only · "
            + "12 with connections to other hosts", workspace.GraphSummary);

        // Its card says what it folds and where their connections ran; its edge's says what a click does.
        HoverCard card = workspace.DescribeGraphHover(folded.Key)!;
        Assert.Equal("4 processes with no admitted relationship, connected only to other hosts, the less busy folded together",
            card.Lines[0]);
        Assert.Equal("Other hosts: 4 TCP connections from 4 processes to 4 hosts", card.Lines[1]);
        GraphDisplayEdge toHosts = Assert.Single(drawn.Edges, edge => edge.SourceKey == folded.Key);
        Assert.Equal("Click chooses its processes, which the inspector lists; double-click takes the keyboard there",
            workspace.DescribeGraphHover(toHosts.Key)!.Lines[^1]);

        // Chosen, by a click on it or its edge, it says what it holds, and the card counts its processes' own records.
        Assert.True(workspace.SelectGraphEdge(toHosts.Key));
        Assert.Equal(GraphNodeKind.OtherHostsOnly, workspace.SelectedCluster?.Kind);
        Assert.Equal("Other hosts only", workspace.SelectionTitle);
        Assert.StartsWith("4 processes whose only drawn activity is their connections to other hosts, the less busy of those "
            + "that have no relationship, counted in one node", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal("Selected aggregate", workspace.EvidenceHeading);
        Assert.StartsWith("14 own records", workspace.EvidenceSummary, StringComparison.Ordinal);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// Twelve applications, app0.exe to app11.exe, each sending to a host of its own once more than the one before it, from
    /// one send; and client.exe and server.exe related over loopback.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        var rows = new List<ObservationRowV1>();
        ulong ordinal = 1;
        for (int index = 0; index < 12; index++)
        {
            int pid = 1_000 + (index * 4);
            rows.Add(Lifecycle(index + 1, ObservationKind.Create, pid, ordinal++) with
            {
                ResourceName = $@"C:\Apps\app{index}.exe", SessionRelativeTicks = 100 + index,
            });
            for (int send = 0; send <= index; send++)
            {
                rows.Add(Transfer(1_000 + (index * 20) + send, ObservationKind.Send, AccountingSide.SendSide, 64, pid, ordinal++)
                    .Between($"192.168.1.10:{50_000 + index}", $"142.250.{index}.1:443") with
                {
                    SessionRelativeTicks = 10_000 + (index * 20) + send,
                });
            }
        }

        rows.Add(Lifecycle(100, ObservationKind.Create, 100, ordinal++) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 200 });
        rows.Add(Lifecycle(101, ObservationKind.Create, 200, ordinal++) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 210 });
        rows.Add(Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, ordinal++)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 50_000 });
        rows.Add(Transfer(5_001, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, ordinal)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 50_100 });
        return [.. rows];
    }
}
