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
/// Words a search finds nothing by, a letter or two from a program of the session, offer that program's name (R18), as a
/// bare number offers its port (§6.7): going to it searches the name in the words' place.
/// </summary>
public sealed class ProgramSearchTests
{
    [Fact(DisplayName = "R18: a search that finds nothing offers each program a letter or two from it, the busiest first, and going to one searches it")]
    public void ASearchNearAProgramOffersIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // "browsr" names nothing and is a letter from browser.exe, which it offers, chosen, the summary saying how to search it.
        workspace.SearchText = "browsr";
        SearchRow offered = Assert.Single(workspace.SearchResults);
        Assert.Equal((SearchHitKind.NearProgram, "PROGRAM", "browser.exe", string.Empty),
            (offered.Hit.Kind, offered.Kind, offered.Label, offered.Observations));
        Assert.StartsWith("Search it instead: ", offered.Detail, StringComparison.Ordinal);
        Assert.Equal($"Program, browser.exe, {offered.Detail}. Press Enter to search it.", offered.AccessibleName);
        Assert.Same(offered, workspace.SelectedSearchResult);
        Assert.Equal("No matches · Enter searches browser.exe, Esc clears", workspace.SearchSummary);

        // Going to it searches the program in the words' place, and opens no rung.
        int depth = workspace.Crumbs.Count;
        Assert.True(workspace.OpenSearchResult());
        Assert.Equal("browser.exe", workspace.SearchText);
        Assert.Contains(workspace.SearchResults, row => row.Hit.Kind == SearchHitKind.Process && row.Label == "browser.exe · PID 300");
        Assert.Equal($"Search it instead: {CountText.Of(workspace.SearchResults.Count, "match", "matches")}", offered.Detail);
        Assert.Equal(depth, workspace.Crumbs.Count);

        // Two programs as near are both offered, the busier first.
        workspace.SearchText = "clientz";
        Assert.Equal([(SearchHitKind.NearProgram, "clients.exe"), (SearchHitKind.NearProgram, "client.exe")],
            workspace.SearchResults.Select(row => (row.Hit.Kind, row.Label)));
        Assert.Equal("No matches · Enter searches clients.exe, Esc clears", workspace.SearchSummary);

        // Words that find something, words near no program, and words near only a process whose executable no record named,
        // which is named by its PID, offer none.
        foreach (string text in NoPrograms)
        {
            workspace.SearchText = text;
            Assert.DoesNotContain(workspace.SearchResults, row => row.Hit.Kind == SearchHitKind.NearProgram);
        }

        Assert.Equal("No matches · names, PIDs, channel endpoints and the addresses processes reached are searched",
            workspace.SearchSummary);
    });

    /// <summary>Words that offer no program: two that find one, one near none, a number, one near PID 777's name alone.</summary>
    private static readonly string[] NoPrograms = ["brows", "client", "teams", "9999", "PIX 777"];

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// client.exe sending once, clients.exe three times, browser.exe once and PID 777, whose executable no record names,
    /// once, each to another host's 443.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Apps\clients.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10)
            .Between("192.168.1.10:50000", "10.0.0.1:443") with { SessionRelativeTicks = 200 },
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 11)
            .Between("192.168.1.10:50001", "10.0.0.2:443") with { SessionRelativeTicks = 210 },
        Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 12)
            .Between("192.168.1.10:50001", "10.0.0.2:443") with { SessionRelativeTicks = 220 },
        Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 13)
            .Between("192.168.1.10:50001", "10.0.0.2:443") with { SessionRelativeTicks = 230 },
        Transfer(14, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 14)
            .Between("192.168.1.10:50002", "10.0.0.3:443") with { SessionRelativeTicks = 240 },
        Transfer(15, ObservationKind.Send, AccountingSide.SendSide, 8, 777, 15)
            .Between("192.168.1.10:50003", "10.0.0.4:443") with { SessionRelativeTicks = 250 },
    ];
}
