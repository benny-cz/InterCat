using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// An other host's candidate other ends in an investigation (§8.3, P6): the processes of another member's capture whose
/// connections mirror a member's to the host, never an established join; a rejected join names none, and an accepted one
/// says a person accepted it.
/// </summary>
public sealed class InvestigationHostsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Application.Tests.InvestigationHosts",
        Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "§8.3: an other host's candidate other ends are the processes of another member's capture whose connections mirror the member's to it, a rejected join naming none")]
    public void AnOtherHostsCandidateOtherEndsAreAnotherMembersProcesses()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(demo.WorkspacePath);
        Guid client = Member(workspace, DemoInvestigation.ClientFolder);
        Guid server = Member(workspace, DemoInvestigation.ServerFolder);
        WorkspaceCorrelationResult correlation = WorkspaceCorrelation.Candidates(demo.WorkspacePath);

        // The client's connections to the server's address mirror two of the server's processes', one each; the client's
        // resolver, which no member captured, has none.
        CandidateHost serverAddress = Assert.Single(InvestigationHosts.Of(workspace, correlation, client));
        Assert.Equal("10.0.0.20", serverAddress.Address);
        Assert.Equal([("demo server", "api.exe", 5100, 1, false), ("demo server", "files.exe", 5300, 1, false)],
            serverAddress.Ends.Select(end => (end.Host, end.Process!, end.ProcessId, end.Connections, end.Accepted)));
        Assert.All(serverAddress.Ends, end => Assert.Equal(server, end.SessionId));
        Assert.Equal("demo server: api.exe · PID 5100 and files.exe · PID 5300", serverAddress.Words);

        // From the server, the client's address names the client's processes.
        CandidateHost clientAddress = Assert.Single(InvestigationHosts.Of(workspace, correlation, server));
        Assert.Equal(("10.0.0.10", "demo client: browser.exe · PID 4100 and sync.exe · PID 4200"), (clientAddress.Address, clientAddress.Words));

        // A join a person rejected names no end; one they accepted says so; a session no candidate holds has none.
        ConnectionCandidate api = correlation.Candidates.Single(candidate => candidate.Second.Connection.Holder.ProcessId == 5100
            || candidate.First.Connection.Holder.ProcessId == 5100);
        ConnectionCandidate files = correlation.Candidates.Single(candidate => candidate != api);
        InvestigationWorkspace.Decide(demo.WorkspacePath, api.Ends.First, api.Ends.Second, WorkspaceJoinDecision.Rejected, null,
            DateTimeOffset.UtcNow);
        InvestigationWorkspace.Decide(demo.WorkspacePath, files.Ends.First, files.Ends.Second, WorkspaceJoinDecision.Accepted, null,
            DateTimeOffset.UtcNow);
        workspace = InvestigationWorkspace.Read(demo.WorkspacePath);
        CandidateHost decided = Assert.Single(InvestigationHosts.Of(workspace, WorkspaceCorrelation.Candidates(demo.WorkspacePath), client));
        Assert.Equal("demo server: files.exe · PID 5300, accepted", decided.Words);
        Assert.Empty(InvestigationHosts.Of(workspace, correlation, Guid.NewGuid()));

        // A host no one named is said by its capture.
        Assert.Equal("capture " + server.ToString("N")[..8], (decided.Ends[0] with { HostAlias = null }).Host);
    }

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

    private static Guid Member(InvestigationWorkspaceFile workspace, string sessionFolder) =>
        workspace.Members.Single(member => member.Path.EndsWith(sessionFolder, StringComparison.Ordinal)).SessionId;
}
