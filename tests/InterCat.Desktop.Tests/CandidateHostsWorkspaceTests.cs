using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static System.StringComparison;

namespace InterCat.Desktop.Tests;

/// <summary>
/// An other host's candidate other ends in the window (§8.3, P6): a session read with an investigation it is a member of
/// names, where a host is listed, carded and described, the processes of another member's capture whose connections
/// mirror its own to the host - never as an established join - and a later publication of the session takes over that
/// read instead of comparing every member's connections again.
/// </summary>
public sealed class CandidateHostsWorkspaceTests : IDisposable
{
    private const string ServerEnds = "demo server: api.exe · PID 5100 and files.exe · PID 5300";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Desktop.Tests.CandidateHosts",
        Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "§8.3: a session read with its investigation names its hosts' candidate other ends where a host is listed, carded and described, never as an established join")]
    public void ASessionReadWithItsInvestigationNamesItsHostsCandidateOtherEnds() => SingleThreadedContext.Run(async () =>
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        using WorkspaceViewModel workspace = Open(demo.ClientSession);
        await workspace.LayoutReady;
        GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
        workspace.SelectGraphNode(hub.Key);
        string unread = Row(workspace, "10.0.0.20").Detail;
        Assert.DoesNotContain("candidate", unread, Ordinal);
        string described = workspace.SelectionSubtitle;
        Assert.DoesNotContain("andidate", described, Ordinal);
        Assert.DoesNotContain("andidate", workspace.HostsNote, Ordinal);
        Assert.DoesNotContain(workspace.DescribeGraphHover(hub.Key)!.Lines, line => line.Contains("andidate", Ordinal));

        // Read with its investigation, the server's address names the server's two processes whose connections mirror the
        // client's to it; the resolver's, which no member captured, names none. The list restates its rows and keeps the
        // host chosen in it.
        workspace.ChosenHostRow = Row(workspace, "10.0.0.20");
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);
        workspace.ReadCandidateHosts(demo.WorkspacePath);
        await workspace.CandidateHostsReady;
        Assert.Contains(nameof(WorkspaceViewModel.HostRows), changed);
        Assert.Equal(unread + " · candidate other ends: " + ServerEnds, Row(workspace, "10.0.0.20").Detail);
        Assert.Contains(ServerEnds + ". Press E", Row(workspace, "10.0.0.20").AccessibleName, Ordinal);
        Assert.DoesNotContain("candidate", Row(workspace, "10.0.0.53").Detail, Ordinal);
        Assert.Equal("10.0.0.20", workspace.ChosenHostRow?.Address);

        // Beneath the rows that name them, the list says once what they are; Other hosts' description, above the list,
        // stays as long as it was.
        Assert.Contains(nameof(WorkspaceViewModel.HostsNote), changed);
        Assert.True(workspace.HasHostsNote);
        Assert.Equal("The candidate other ends named for 1 of its 2 hosts are processes of its investigation's other sessions "
            + "whose connections mirror these, never an established join.", workspace.HostsNote);
        Assert.Equal(described, workspace.SelectionSubtitle);
        Assert.Contains("Its investigation's other sessions propose candidate other ends for 1 of its 2 hosts: processes whose "
            + "connections mirror these, never an established join; chosen, it lists them", workspace.DescribeGraphHover(hub.Key)!.Lines);

        // Drawn on its own, its card and its description name them, never as an established join; the resolver's name none.
        Assert.True(workspace.OpenGraphNode(hub.Key));
        await workspace.LayoutReady;
        string server = GraphProjection.HostKey("10.0.0.20");
        Assert.Contains("Candidate other ends: " + ServerEnds + ", never an established join", workspace.DescribeGraphHover(server)!.Lines);
        Assert.DoesNotContain(workspace.DescribeGraphHover(GraphProjection.HostKey("10.0.0.53"))!.Lines,
            line => line.Contains("andidate", Ordinal));
        changed.Clear();
        workspace.SelectGraphNode(server);
        Assert.Contains(". Its candidate other ends in its investigation, never an established join: " + ServerEnds
            + ". Who is at it is not observed.", workspace.SelectionSubtitle, Ordinal);
        workspace.SelectGraphNode(GraphProjection.HostKey("10.0.0.53"));
        Assert.DoesNotContain("andidate", workspace.SelectionSubtitle, Ordinal);

        // Read again, its description follows what is read.
        workspace.SelectGraphNode(server);
        changed.Clear();
        workspace.ReadCandidateHosts(demo.WorkspacePath);
        await workspace.CandidateHostsReady;
        Assert.Contains(nameof(WorkspaceViewModel.SelectionSubtitle), changed);
    });

    [Fact(DisplayName = "§8.3: an investigation that cannot be read, or that never captured a host's other end, names no candidate for it")]
    public void AnUnreadableInvestigationNamesNoCandidate() => SingleThreadedContext.Run(async () =>
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        using WorkspaceViewModel workspace = Open(demo.ClientSession);
        await workspace.LayoutReady;
        workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);
        workspace.ReadCandidateHosts(demo.WorkspacePath);
        await workspace.CandidateHostsReady;
        Assert.Contains("candidate other ends", Row(workspace, "10.0.0.20").Detail, Ordinal);

        // A file that is no investigation proposes none, and what an earlier read named is no longer said.
        string notOne = Path.Combine(folder, "not-an-investigation.json");
        File.WriteAllText(notOne, "{}");
        workspace.ReadCandidateHosts(notOne);
        await workspace.CandidateHostsReady;
        Assert.All(workspace.HostRows, row => Assert.DoesNotContain("candidate", row.Detail, Ordinal));

        // Nor does one with no file at all.
        workspace.ReadCandidateHosts(Path.Combine(folder, "missing.icat-workspace"));
        await workspace.CandidateHostsReady;
        Assert.All(workspace.HostRows, row => Assert.DoesNotContain("candidate", row.Detail, Ordinal));
    });

    [Fact(DisplayName = "§8.3: a later publication of the session takes over the read of its candidate other ends, still running or done, and reads them again only under another evidence policy")]
    public void ALaterPublicationTakesOverTheCandidateRead() => SingleThreadedContext.Run(async () =>
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));

        // Given up while it still runs, the read outlives the workspace that began it, and the next shows what it finds.
        using WorkspaceViewModel second = Open(demo.ClientSession);
        CandidateHostsRead running;
        using (WorkspaceViewModel first = Open(demo.ClientSession))
        {
            first.ReadCandidateHosts(demo.WorkspacePath);
            running = first.CarryCandidateHosts()!;
            Assert.Null(first.CarryCandidateHosts());
        }

        Assert.Equal((demo.WorkspacePath, EvidencePolicy.IncludeCorrelated), (running.Investigation, running.Policy));
        second.AdoptCandidateHosts(running);
        await second.CandidateHostsReady;
        await second.LayoutReady;
        second.SelectGraphNode(second.GraphDisplay.OtherHosts!.Key);
        Assert.EndsWith(" · candidate other ends: " + ServerEnds, Row(second, "10.0.0.20").Detail, Ordinal);

        // A workspace that gave its read up shows nothing of what it finds.
        using (WorkspaceViewModel giver = Open(demo.ClientSession))
        {
            await giver.LayoutReady;
            giver.SelectGraphNode(giver.GraphDisplay.OtherHosts!.Key);
            giver.ReadCandidateHosts(demo.WorkspacePath);
            Task giverReady = giver.CandidateHostsReady;
            using CandidateHostsRead given = giver.CarryCandidateHosts()!;
            await giverReady;
            Assert.DoesNotContain("candidate", Row(giver, "10.0.0.20").Detail, Ordinal);
        }

        // Done, it is shown with the next one's first rows, read nowhere again.
        using WorkspaceViewModel third = Open(demo.ClientSession);
        await third.LayoutReady;
        third.SelectGraphNode(third.GraphDisplay.OtherHosts!.Key);
        CandidateHostsRead done = second.CarryCandidateHosts()!;
        Assert.Same(running, done);
        third.AdoptCandidateHosts(done);
        Assert.True(third.CandidateHostsReady.IsCompleted);
        Assert.EndsWith(" · candidate other ends: " + ServerEnds, Row(third, "10.0.0.20").Detail, Ordinal);

        // Counted under another policy, the session's hosts are read again under it.
        using WorkspaceViewModel strict = Open(demo.ClientSession, EvidencePolicy.DirectOnly);
        strict.AdoptCandidateHosts(third.CarryCandidateHosts()!);
        await strict.CandidateHostsReady;
        CandidateHostsRead reread = strict.CarryCandidateHosts()!;
        Assert.NotSame(done, reread);
        Assert.Equal((demo.WorkspacePath, EvidencePolicy.DirectOnly), (reread.Investigation, reread.Policy));

        // The demo's connections bind to their processes by correlation, so direct evidence alone holds none of them,
        // and none has a candidate other end, nor any listening port.
        Assert.Empty((await reread.Reading).Hosts);
        Assert.Empty((await reread.Reading).Listeners);
        Assert.True(done.Cancelled);
        done.Dispose();

        // Another session's workspace lets the read go and names nothing from it.
        using WorkspaceViewModel other = Open(demo.ServerSession);
        other.AdoptCandidateHosts(reread);
        Assert.True(reread.Cancelled);
        Assert.Null(other.CarryCandidateHosts());
    });

    [Fact(DisplayName = "§8.3: a workspace's end lets its read of candidate other ends go, and a read for an ended workspace begins none")]
    public void AWorkspacesEndLetsItsCandidateReadGo() => SingleThreadedContext.Run(async () =>
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        WorkspaceViewModel workspace = Open(demo.ClientSession);
        workspace.ReadCandidateHosts(demo.WorkspacePath);
        Task reading = workspace.CandidateHostsReady;
        workspace.Dispose();
        await reading;
        workspace.ReadCandidateHosts(demo.WorkspacePath);
        Assert.Null(workspace.CarryCandidateHosts());

        // A workspace with no session to read names none, and reads none.
        using var drawn = new WorkspaceViewModel(OverviewWorkspace.From(SessionOverviewProjector.Project(
            SessionStore.OpenForViewing(LocalOwnedDirectory.Open(demo.ClientSession)))), "no-evidence-source");
        drawn.ReadCandidateHosts(demo.WorkspacePath);
        Assert.Null(drawn.CarryCandidateHosts());
        Assert.Throws<ArgumentException>(() => drawn.ReadCandidateHosts(" "));
    });

    [Fact(DisplayName = "§8.3: the other end's session names the candidate other ends of its one host, the processes of the capture that reached it")]
    public void TheOtherEndsSessionNamesItsOneHostsCandidates() => SingleThreadedContext.Run(async () =>
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        using WorkspaceViewModel workspace = Open(demo.ServerSession);
        await workspace.LayoutReady;
        workspace.ReadCandidateHosts(demo.WorkspacePath);
        await workspace.CandidateHostsReady;
        workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);
        Assert.Equal(["10.0.0.10"], workspace.HostRows.Select(row => row.Address));
        Assert.EndsWith(" · candidate other ends: demo client: browser.exe · PID 4100 and sync.exe · PID 4200",
            Row(workspace, "10.0.0.10").Detail, Ordinal);
        Assert.Equal("The candidate other ends named for its host are processes of its investigation's other sessions whose "
            + "connections mirror these, never an established join.", workspace.HostsNote);
    });

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WorkspaceViewModel Open(string session, EvidencePolicy policy = EvidencePolicy.IncludeCorrelated)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(
            SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session)), policy);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session, overview.SessionId, overview.Generation, overview.Policy));
    }

    private static HostRow Row(WorkspaceViewModel workspace, string address) =>
        workspace.HostRows.Single(row => string.Equals(row.Address, address, Ordinal));
}
