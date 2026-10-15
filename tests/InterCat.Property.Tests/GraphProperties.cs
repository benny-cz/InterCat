using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.PropertyTests;

/// <summary>
/// §6.3's compaction over random machines rather than chosen examples (R10): processes in groups of every size, related by
/// TCP, UDP and RPC between random pairs - several between one pair, and in either order - and reaching other hosts, drawn
/// under random budgets, at the machine rung, in an opened group and around a kept process. Whatever folds, every process
/// is drawn once, every relationship and connection to other hosts is drawn or folded once, no aggregate holds one
/// process, the relationships between two nodes run in distinct lanes, and the Other hosts only node folds only the less
/// busy of the processes that only reach other hosts. Every case comes from a printed seed (§13.6).
/// </summary>
public sealed class GraphProperties
{
    private const int Cases = 150;

    public static TheoryData<int> Seeds => [3, 29, 541, 20_261_015];

    [Theory(DisplayName = "§6.3: a random machine's graph draws every process once, every relationship and connection once, in lanes of their own")]
    [MemberData(nameof(Seeds))]
    public void ARandomMachinesGraphDrawsEverythingOnce(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            WorkspaceSnapshot snapshot = Machine(random);
            GraphDisplayBudget budget = Budget(random);
            ProcessGroup? opened = random.Next(4) == 0 ? snapshot.Groups[random.Next(snapshot.Groups.Count)] : null;
            ProcessInstanceId? kept = random.Next(4) == 0
                ? snapshot.Processes.Where(process => opened is null || process.GroupKey == opened.Key)
                    .Select(process => (ProcessInstanceId?)process.Id).FirstOrDefault()
                : null;

            // A process's rung draws it and its peers, the rest of the machine one context node (§3.2).
            HashSet<ProcessInstanceId>? neighborhood = null;
            if (opened is null && random.Next(4) == 0)
            {
                ProcessInstanceId focus = snapshot.Processes[random.Next(snapshot.Processes.Count)].Id;
                kept = focus;
                neighborhood = [focus, .. snapshot.Edges.Where(edge => edge.SourceId == focus || edge.TargetId == focus)
                    .SelectMany(edge => new[] { edge.SourceId, edge.TargetId })];
            }

            bool openHosts = random.Next(3) == 0;
            string label = Describe(seed, index, budget, opened?.Key, kept) + (neighborhood is null ? string.Empty : ", focused")
                + (openHosts ? ", Other hosts opened" : string.Empty);
            GraphDisplay display;
            try
            {
                display = GraphProjection.Project(snapshot, opened?.Key, kept, budget, neighborhood, openHosts);
            }
            catch (InvalidOperationException refused) when (refused.Message.Contains("could not fit", StringComparison.Ordinal))
            {
                // A budget too small for the focus is refused in words, never answered by hiding a process.
                continue;
            }

            Holds(snapshot, display, budget, kept, neighborhood, label);

            // Opening Other hosts folds no process: the drawing is the folded one's, with the hosts drawn on their own.
            Assert.Equal(openHosts, display.OpenedHosts is not null);
            if (openHosts)
            {
                GraphDisplay folded = GraphProjection.Project(snapshot, opened?.Key, kept, budget, neighborhood);
                Assert.Equal(folded.Nodes.Where(node => !node.IsHost).Select(node => (node.Key, node.Members.Count)),
                    display.Nodes.Where(node => !node.IsHost).Select(node => (node.Key, node.Members.Count)));
                Assert.Equal(folded.Edges.Where(edge => !edge.ToHosts).Select(edge => edge.Key),
                    display.Edges.Where(edge => !edge.ToHosts).Select(edge => edge.Key));
            }
        }
    }

    /// <summary>Every invariant one drawing must hold.</summary>
    private static void Holds(WorkspaceSnapshot snapshot, GraphDisplay display, GraphDisplayBudget budget, ProcessInstanceId? kept,
        HashSet<ProcessInstanceId>? neighborhood, string label)
    {
        // Every process is a member of exactly one drawn node, and the kept process is drawn on its own.
        var drawnIn = new Dictionary<ProcessInstanceId, GraphDisplayNode>();
        foreach (GraphDisplayNode node in display.Nodes)
        {
            foreach (ProcessInstanceId member in node.Members)
            {
                Assert.True(drawnIn.TryAdd(member, node), $"{label}: {member} is drawn twice");
            }
        }

        Assert.True(snapshot.Processes.All(process => drawnIn.ContainsKey(process.Id)), $"{label}: a process is not drawn");
        if (kept is { } focus)
        {
            Assert.True(drawnIn[focus].Kind == GraphNodeKind.Process, $"{label}: the kept process is folded");
        }

        // A focus draws its neighbourhood and counts everything else in the context node, however few.
        GraphDisplayNode? context = display.Nodes.FirstOrDefault(node => node.Kind == GraphNodeKind.Context);
        if (neighborhood is not null)
        {
            Assert.All(snapshot.Processes, process => Assert.True(
                (drawnIn[process.Id].Kind == GraphNodeKind.Context) != neighborhood.Contains(process.Id),
                $"{label}: {process.Id} is drawn on the wrong side of the focus"));
        }
        else
        {
            Assert.Null(context);
        }

        // No aggregate holds one process but the context node, and every node but Other hosts and the hosts it was opened
        // into holds some.
        foreach (GraphDisplayNode node in display.Nodes)
        {
            if (node.Kind == GraphNodeKind.Context)
            {
                Assert.NotEmpty(node.Members);
            }
            else if (node.IsHost)
            {
                Assert.Empty(node.Members);
            }
            else if (node.Kind == GraphNodeKind.Process)
            {
                Assert.True(node.Members.Count == 1, $"{label}: process node {node.Key} holds {node.Members.Count}");
            }
            else
            {
                Assert.True(node.Members.Count >= 2, $"{label}: aggregate {node.Key} ({node.Kind}) holds {node.Members.Count}");
            }
        }

        // Every relationship is drawn by one edge between its ends' nodes, or folded inside the node holding both.
        var drawnRelationships = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphDisplayEdge edge in display.Edges.Where(edge => !edge.ToHosts))
        {
            foreach (string relationship in edge.Relationships)
            {
                Assert.True(drawnRelationships.Add(relationship), $"{label}: relationship {relationship} is drawn twice");
            }
        }

        foreach (CommunicationEdge relationship in snapshot.Edges)
        {
            GraphDisplayNode source = drawnIn[relationship.SourceId];
            GraphDisplayNode target = drawnIn[relationship.TargetId];
            if (source == target)
            {
                Assert.DoesNotContain(relationship.Key, drawnRelationships);
            }
            else
            {
                Assert.True(drawnRelationships.Contains(relationship.Key), $"{label}: relationship {relationship.Key} is lost");
                GraphDisplayEdge edge = display.EdgeOf(relationship.Key)!;
                Assert.Equal(relationship.Mechanism, edge.Mechanism);
                Assert.True(
                    (edge.SourceKey == source.Key && edge.TargetKey == target.Key) || (edge.SourceKey == target.Key && edge.TargetKey == source.Key),
                    $"{label}: relationship {relationship.Key} is drawn between other nodes");
            }
        }

        // Every connection to other hosts is drawn from the node its process is drawn in, once to each host Other hosts was
        // opened into that it reached and once to Other hosts for the rest; a context node's processes' are left to their
        // own rungs.
        HashSet<string> opened = new(display.OpenedHosts ?? [], StringComparer.Ordinal);
        string TargetOf(HostAddress address) =>
            opened.Contains(address.Address) ? GraphProjection.HostKey(address.Address) : GraphProjection.OtherHostsKey;
        foreach (HostLink link in snapshot.HostLinks)
        {
            IReadOnlyList<GraphDisplayEdge> drawnBy = display.EdgesOfHostLink(link.Key);
            if (drawnIn[link.Process].Kind == GraphNodeKind.Context)
            {
                Assert.Empty(drawnBy);
                continue;
            }

            Assert.Equal(link.Addresses.Select(TargetOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                drawnBy.Select(edge => edge.TargetKey).Order(StringComparer.Ordinal));
            Assert.All(drawnBy, edge => Assert.Equal((drawnIn[link.Process].Key, link.Mechanism), (edge.SourceKey, edge.Mechanism)));
        }

        // Each edge to a host counts the records its source's connections over its transport held at the hosts it stands for.
        foreach (GraphDisplayEdge edge in display.Edges.Where(edge => edge.ToHosts))
        {
            Assert.Equal(
                snapshot.HostLinks
                    .Where(link => drawnIn[link.Process].Key == edge.SourceKey && link.Mechanism == edge.Mechanism)
                    .SelectMany(link => link.Addresses)
                    .Where(address => TargetOf(address) == edge.TargetKey)
                    .Sum(address => address.Records),
                edge.ObservationCount);
        }

        // The hosts drawn on their own are the busiest of the drawn connections', at most the budget's count, each with the
        // processes that reached it; Other hosts is drawn exactly when a drawn connection ran to a host not drawn so.
        HostLink[] drawnLinks = [.. snapshot.HostLinks.Where(link => drawnIn[link.Process].Kind != GraphNodeKind.Context)];
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(drawnLinks);
        Assert.True(opened.Count <= budget.HostsDrawn, $"{label}: {opened.Count} hosts opened past {budget.HostsDrawn}");
        Assert.Equal(hosts.Take(opened.Count).Select(host => host.Address), display.OpenedHosts ?? []);
        Assert.Equal(display.OpenedHosts ?? [], display.Nodes.Where(node => node.Kind == GraphNodeKind.Host).Select(node => node.Label));
        Assert.All(display.Nodes.Where(node => node.Kind == GraphNodeKind.Host), host => Assert.Equal(
            drawnLinks.Where(link => link.Addresses.Any(address => address.Address == host.Label)).Select(link => link.Process).Distinct().Count(),
            host.ConnectedProcesses));
        Assert.Equal(drawnLinks.SelectMany(link => link.Addresses).Any(address => !opened.Contains(address.Address)),
            display.OtherHosts is not null);

        // The hosts the drawn connections reached account for every one of their connections and records, each address
        // once, the busiest first.
        Assert.Equal(drawnLinks.Sum(link => link.Records), hosts.Sum(host => host.Records));
        Assert.Equal(drawnLinks.Sum(link => link.Connections), hosts.Sum(host => host.TcpConnections + host.UdpFlows));
        Assert.Equal(drawnLinks.SelectMany(link => link.Addresses).Select(address => address.Address).Distinct(StringComparer.Ordinal).Count(),
            hosts.Count);
        Assert.True(hosts.Zip(hosts.Skip(1)).All(pair => pair.First.Records >= pair.Second.Records), $"{label}: hosts out of order");

        // The relationships between two nodes run in lanes of their own, evenly about the line between them; a node's
        // edges to the other hosts too.
        foreach (IGrouping<(string, string), GraphDisplayEdge> between in display.Edges
            .GroupBy(edge => string.CompareOrdinal(edge.SourceKey, edge.TargetKey) <= 0 ? (edge.SourceKey, edge.TargetKey) : (edge.TargetKey, edge.SourceKey)))
        {
            double[] lanes = [.. between.Select(edge => string.CompareOrdinal(edge.SourceKey, edge.TargetKey) <= 0 ? edge.Lane : -edge.Lane).Order()];
            double[] expected = [.. Enumerable.Range(0, lanes.Length).Select(lane => lane - ((lanes.Length - 1) / 2d))];
            Assert.True(lanes.SequenceEqual(expected), $"{label}: lanes {string.Join(", ", lanes)} between {between.Key}");
        }

        // Other hosts only folds processes that relate to nothing and reach other hosts, past the budget's count, each no
        // busier than every one of them drawn on its own outside an opened group.
        HashSet<ProcessInstanceId> ends = [.. snapshot.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })];
        Dictionary<ProcessInstanceId, long> reach = snapshot.HostLinks.GroupBy(link => link.Process)
            .ToDictionary(group => group.Key, group => group.Sum(link => link.Records));
        if (display.Nodes.FirstOrDefault(node => node.Kind == GraphNodeKind.OtherHostsOnly) is { } folded)
        {
            Assert.All(folded.Members, member => Assert.True(reach.ContainsKey(member) && !ends.Contains(member),
                $"{label}: {member} is folded among those that only reach other hosts"));
            ProcessInstanceId[] alone = [.. display.Nodes
                .Where(node => node.Kind == GraphNodeKind.Process && node.Process is { } process && reach.ContainsKey(process)
                    && !ends.Contains(process) && process != kept && node.GroupKey != display.ExpandedGroup)
                .Select(node => node.Process!.Value)];
            Assert.True(alone.Length <= budget.HostOnlyDrawn, $"{label}: {alone.Length} drawn alone past {budget.HostOnlyDrawn}");
            long quietestAlone = alone.Length == 0 ? long.MaxValue : alone.Min(process => reach[process]);
            Assert.True(folded.Members.All(member => reach[member] <= quietestAlone),
                $"{label}: a folded process is busier than one drawn alone");
        }

        // A re-count against the same snapshot keeps the drawing.
        GraphDisplay again = GraphProjection.Rescope(display, snapshot, snapshot);
        Assert.Equal(display.Nodes.Select(node => (node.Key, node.Members.Count)), again.Nodes.Select(node => (node.Key, node.Members.Count)));
        Assert.Equal(display.Edges.Select(edge => (edge.Key, edge.Lane)), again.Edges.Select(edge => (edge.Key, edge.Lane)));
    }

    /// <summary>A random machine: groups of every size, relationships of three mechanisms, connections to other hosts.</summary>
    private static WorkspaceSnapshot Machine(Random random)
    {
        int groupCount = random.Next(1, 12);
        ProcessGroup[] groups = [.. Enumerable.Range(0, groupCount)
            .Select(index => new ProcessGroup($"g{index}", $"app{index}.exe", LaneGrouping.Executable))];
        int processCount = random.Next(1, 90);
        ProcessNode[] processes = [.. Enumerable.Range(1, processCount).Select(index => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
            1_000 + index,
            $"process{index}.exe",
            "test",
            groups[random.Next(random.Next(4) == 0 ? 1 : groupCount)].Key,
            0.5,
            0.5,
            CoverageState.Covered))];
        Mechanism[] mechanisms = [Mechanism.Tcp, Mechanism.Udp, Mechanism.Rpc];
        var edges = new List<CommunicationEdge>();
        var pairs = new HashSet<(int, int, Mechanism)>();
        int relationships = random.Next(0, processCount);
        for (int index = 0; index < relationships; index++)
        {
            int first = random.Next(processCount);
            int second = random.Next(processCount);
            Mechanism mechanism = mechanisms[random.Next(mechanisms.Length)];
            if (first == second || !pairs.Add((Math.Min(first, second), Math.Max(first, second), mechanism)))
            {
                continue;
            }

            edges.Add(new CommunicationEdge($"edge-{index}", processes[first].Id, processes[second].Id, mechanism,
                random.Next(1, 500), null, random.Next(3) == 0 ? RelationStrength.Correlated : RelationStrength.Direct)
            {
                Rule = RelationRule.TransportEndpoint,
                Evidence = [$"edge-{index}"],
            });
        }

        // A link's connections run to distinct addresses of a pool the machine's processes share, each address reached by a
        // connection or more, each holding a record or more.
        HostLink[] links = [.. processes
            .Where(_ => random.Next(3) > 0)
            .SelectMany(process => new[] { Mechanism.Tcp, Mechanism.Udp }
                .Where(_ => random.Next(2) == 0)
                .Select(mechanism =>
                {
                    HostAddress[] addresses = [.. Enumerable.Range(0, 40).OrderBy(_ => random.Next()).Take(random.Next(1, 12))
                        .Select(host =>
                        {
                            int connections = random.Next(1, 4);
                            return new HostAddress($"203.0.113.{host}", connections, connections + random.Next(0, 30));
                        })];
                    return new HostLink(process.Id, mechanism, addresses,
                        random.Next(4) == 0 ? RelationStrength.Correlated : RelationStrength.Direct);
                }))];
        return new WorkspaceSnapshot("random machine", new TimeRange(0, 10), groups, processes, edges, [], [], [], [])
        {
            HostLinks = links,
        };
    }

    private static GraphDisplayBudget Budget(Random random) => random.Next(3) == 0
        ? GraphDisplayBudget.Default
        : new GraphDisplayBudget(random.Next(4, 60), random.Next(4, 200), random.Next(2, 30), random.Next(0, 12), random.Next(0, 10));

    private static string Describe(int seed, int index, GraphDisplayBudget budget, string? opened, ProcessInstanceId? kept) =>
        string.Create(CultureInfo.InvariantCulture,
            $"seed {seed}, case {index}, budget {budget}, opened {opened ?? "none"}, kept {kept?.ToString() ?? "none"}");
}
