using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A session's relationships summed by the two programs at their ends (§6.3): which programs talk to which on a busy
/// machine, however many processes each runs, each pair named first by the program whose processes made its connections.
/// </summary>
public sealed class ProgramPairsTests
{
    private static readonly ProcessGroup Browser = new(@"executable:C:\APPS\BROWSER.EXE", "browser.exe", LaneGrouping.Executable);
    private static readonly ProcessGroup Server = new(@"executable:C:\APPS\SERVER.EXE", "server.exe", LaneGrouping.Executable);
    private static readonly ProcessGroup Agent = new(@"executable:C:\APPS\AGENT.EXE", "agent.exe", LaneGrouping.Executable);

    // Its key sorts before the agent's, its name after it.
    private static readonly ProcessGroup Zeta = new(@"executable:C:\0\ZETA.EXE", "zeta.exe", LaneGrouping.Executable);

    [Fact(DisplayName = "§6.3: a session's relationships are summed by the two programs at their ends, the busiest first, each named by who connected to whom")]
    public void RelationshipsAreSummedByTheirPrograms()
    {
        ProcessNode browser = Node(1, 100, Browser), tab = Node(2, 101, Browser), worker = Node(3, 102, Browser);
        ProcessNode server = Node(4, 200, Server), agent = Node(5, 300, Agent), zeta = Node(6, 400, Zeta);
        CommunicationEdge[] edges =
        [
            // Two of the browser's processes connected to the server, one drawn from each end.
            Edge(browser, server, Mechanism.Tcp, 10, fromSource: 2, fromTarget: 0),
            Edge(server, tab, Mechanism.Tcp, 6, fromSource: 0, fromTarget: 1),

            // Its tab and its worker connected to its main process, each drawn from a different end.
            Edge(tab, browser, Mechanism.Tcp, 4, fromSource: 1, fromTarget: 0),
            Edge(browser, worker, Mechanism.Tcp, 4, fromSource: 0, fromTarget: 1),

            // The server connected to the agent twice and the agent to the server once, and a UDP flow joins them too.
            Edge(agent, server, Mechanism.Tcp, 3, fromSource: 1, fromTarget: 2),
            Edge(server, agent, Mechanism.Udp, 2, fromSource: 0, fromTarget: 0),

            // A flow nothing opens, as many records as the agent and the server share, over one relationship.
            Edge(zeta, agent, Mechanism.Udp, 5, fromSource: 0, fromTarget: 0),
        ];

        IReadOnlyList<ProgramPair> pairs = ProgramPairs.Of(edges, [browser, tab, worker, server, agent, zeta], [Browser, Server, Agent, Zeta]);

        // The busiest by records, then by relationships: the agent and the server tie the agent and zeta at 5 records.
        Assert.Equal(
            ["browser.exe → server.exe", "browser.exe → browser.exe", "server.exe ⇄ agent.exe", "agent.exe and zeta.exe"],
            pairs.Select(pair => pair.Joined));
        Assert.Equal([16L, 8L, 5L, 5L], pairs.Select(pair => pair.Records));
        Assert.Equal([2, 2, 2, 1], pairs.Select(pair => pair.Relationships));

        // The processes at either end: the browser's two and the server; the browser's three; one each.
        Assert.Equal([3, 3, 2, 2], pairs.Select(pair => pair.Processes));
        Assert.Equal(["TCP", "TCP", "TCP and UDP", "UDP"], pairs.Select(pair => pair.Over));

        // The connections each program's processes made, the maker's first: a program's own all its own.
        Assert.Equal([(3L, 0L), (2L, 0L), (2L, 1L), (0L, 0L)], pairs.Select(pair => (pair.FirstConnected, pair.SecondConnected)));
        Assert.Same(Browser, pairs[1].First);
        Assert.Same(Browser, pairs[1].Second);
        Assert.Equal([Mechanism.Tcp, Mechanism.Udp], pairs[2].Mechanisms);
    }

    [Fact(DisplayName = "§6.3: a relationship whose process no group holds is refused, never summed under a guessed program")]
    public void AProcessNoGroupHoldsIsRefused()
    {
        ProcessNode browser = Node(1, 100, Browser), server = Node(2, 200, Server);
        CommunicationEdge edge = Edge(browser, server, Mechanism.Tcp, 2, fromSource: 1, fromTarget: 0);

        Assert.Throws<ArgumentException>(() => ProgramPairs.Of([edge], [browser], [Browser, Server]));
        Assert.Throws<ArgumentException>(() => ProgramPairs.Of([edge], [browser, server], [Browser]));
        Assert.Empty(ProgramPairs.Of([], [browser, server], [Browser, Server]));
    }

    private static ProcessNode Node(int index, int pid, ProcessGroup group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), pid, group.Name, "Created", group.Key, 0.5, 0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(ProcessNode source, ProcessNode target, Mechanism mechanism, long records, int fromSource,
        int fromTarget) => new($"{mechanism}:{source.ProcessId}:{target.ProcessId}", source.Id, target.Id, mechanism, records, null,
            RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [$"{mechanism}:{source.ProcessId}:{target.ProcessId}:0"],
            SourceConnected = fromSource,
            TargetConnected = fromTarget,
        };
}
