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
/// A bare number searched as a port (§6.7): typed bare, a number is a PID or a PID's first digits, and a number whose port
/// search finds something offers that search among its hits - after a process whose PID the number is, else first - which
/// going to searches in the number's place.
/// </summary>
public sealed class PortSearchTests
{
    [Fact(DisplayName = "§6.7: a bare number whose port search finds something offers it, after the process whose PID it is and else first, and going to it searches the port")]
    public void ABareNumberOffersItsPortSearch() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // 3389 is no PID nor part of any name, so its port is all it offers: first and chosen, the summary saying how to
        // search it.
        workspace.SearchText = "3389";
        SearchRow port = Assert.Single(workspace.SearchResults);
        Assert.Equal((SearchHitKind.Port, "PORT", ":3389", "Search the port instead: 1 match"),
            (port.Hit.Kind, port.Kind, port.Label, port.Detail));
        Assert.Equal(string.Empty, port.Observations);
        Assert.Equal("Port, :3389, Search the port instead: 1 match. Press Enter to search it.", port.AccessibleName);
        Assert.Same(port, workspace.SelectedSearchResult);
        Assert.Equal("No PID or name matches · Enter searches :3389 as a port, Esc clears", workspace.SearchSummary);

        // Going to it searches the port in the number's place, its first hit chosen; it opens no rung.
        int depth = workspace.Crumbs.Count;
        Assert.True(workspace.OpenSearchResult());
        Assert.Equal(":3389", workspace.SearchText);
        Assert.Equal(["mstsc.exe · PID 500"], workspace.SearchResults.Select(row => row.Label));
        Assert.Same(workspace.SearchResults[0], workspace.SelectedSearchResult);
        Assert.Equal(depth, workspace.Crumbs.Count);

        // 8080 begins agent.exe's PID and is part of the loopback channel's endpoints; its port finds that channel and the
        // server that accepted another host's connection at it. A PID it only begins is the less likely meaning, so the
        // port comes first.
        workspace.SearchText = "8080";
        Assert.Equal([(SearchHitKind.Port, ":8080"), (SearchHitKind.Process, "agent.exe · PID 80800"), (SearchHitKind.Channel, "Channel")],
            workspace.SearchResults.Select(row => (row.Hit.Kind, row.Label)));
        Assert.Equal("Search the port instead: 2 matches", workspace.SearchResults[0].Detail);
        Assert.Equal("2 matches · Enter opens the selected one, Esc clears", workspace.SearchSummary);

        // 443 is daemon.exe's PID, the likelier meaning, which comes first and is chosen; browser.exe reached another host's
        // 443, which the port after it offers.
        workspace.SearchText = "443";
        Assert.Equal([(SearchHitKind.Process, "daemon.exe · PID 443"), (SearchHitKind.Port, ":443")],
            workspace.SearchResults.Select(row => (row.Hit.Kind, row.Label)));
        Assert.Same(workspace.SearchResults[0], workspace.SelectedSearchResult);

        // A number no connection used as a port, one past the last port, six digits, or a number with a letter offers none.
        foreach (string text in NoPorts)
        {
            workspace.SearchText = text;
            Assert.DoesNotContain(workspace.SearchResults, row => row.Hit.Kind == SearchHitKind.Port);
        }

        Assert.Equal("No matches · names, PIDs, channel endpoints and the addresses processes reached are searched",
            workspace.SearchSummary);
    });

    /// <summary>Words that offer no port: a number no connection used as one, one past the last, six digits, a letter.</summary>
    private static readonly string[] NoPorts = ["9999", "65536", "123456", "443a", " 3389x"];

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// client.exe and server.exe related over loopback at the server's 8080, where server.exe also accepted another host's
    /// connection; browser.exe reaching another host's 443 and mstsc.exe its 3389; daemon.exe, whose PID is 443; and
    /// agent.exe, whose PID begins 8080.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Lifecycle(4, ObservationKind.Create, 443, 4) with { ResourceName = @"C:\Tools\daemon.exe", SessionRelativeTicks = 130 },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Windows\System32\mstsc.exe", SessionRelativeTicks = 140 },
        Lifecycle(6, ObservationKind.Create, 80800, 6) with { ResourceName = @"C:\Tools\agent.exe", SessionRelativeTicks = 150 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 11)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 200 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
        Transfer(23, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 13)
            .Between("192.168.1.10:8080", "192.168.1.30:52001") with { SessionRelativeTicks = 220 },
        Transfer(24, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 500, 14)
            .Between("192.168.1.10:52002", "10.0.0.7:3389") with { SessionRelativeTicks = 230 },
        Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 40, 500, 15)
            .Between("192.168.1.10:52002", "10.0.0.7:3389") with { SessionRelativeTicks = 240 },
    ];
}
