using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>The semantic kind of a node that the communication graph actually draws.</summary>
public enum GraphNodeKind
{
    /// <summary>One process instance, drawn on its own.</summary>
    Process = 1,

    /// <summary>A collapsed executable group: its members that would otherwise be drawn one by one.</summary>
    Group = 2,

    /// <summary>The opened group's members not drawn on their own: its quiet members and, over budget, its low end.</summary>
    OtherMembers = 3,

    /// <summary>The least active drawn nodes, folded across groups when collapsing groups alone cannot fit the budget.</summary>
    Remainder = 4,

    /// <summary>Processes with no relationship in the published scope, outside the explicit focus. It has no edges.</summary>
    Quiet = 5,

    /// <summary>
    /// Every process outside a focused rung's neighbourhood (§3.2 L1-L4), counted rather than drawn. Its edges are the
    /// relationships that lead out of the neighbourhood; it is context, not a participant, and adds to no total.
    /// </summary>
    Context = 6,

    /// <summary>
    /// The other hosts the drawn processes' connections run to (§6.3's unresolved resources): a resource, not a process,
    /// so it has no members. Its edges are processes' connections whose other end no record of the capture holds, drawn
    /// with no traffic value; who is at their other ends is not observed (P7). It is drawn only when one of them is.
    /// </summary>
    OtherHosts = 7,

    /// <summary>
    /// Processes whose only drawn activity is their connections to other hosts - an end of no relationship - outside the
    /// explicit focus, past the budget's count of them: the less busy of them, counted in one node edged to Other hosts once
    /// per transport, so a busy machine's dozens do not bury the relationships between processes in a star (§6.3).
    /// </summary>
    OtherHostsOnly = 8,
}

/// <summary>
/// Workspace-owned graph compaction settings (§6.3). The defaults are deliberately below the layout engine's hard
/// safety caps: projection keeps a graph legible; layout caps protect the worker from malformed or un-compacted input.
/// </summary>
/// <param name="HostOnlyDrawn">
/// How many processes whose only drawn activity is their connections to other hosts are drawn on their own, the busiest
/// by their connections' records; past it the rest fold into one Other hosts only node.
/// </param>
public sealed record GraphDisplayBudget(int Nodes, int Edges, int CollapseThreshold, int HostOnlyDrawn = 8)
{
    public static GraphDisplayBudget Default { get; } = new(200, 500, 25, 8);

    public void Validate()
    {
        if (Nodes is < 1 or > GraphLayout.MaximumNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(Nodes),
                $"The graph display node budget must be between 1 and {GraphLayout.MaximumNodes:N0}.");
        }

        if (Edges is < 1 or > GraphLayout.MaximumEdges)
        {
            throw new ArgumentOutOfRangeException(nameof(Edges),
                $"The graph display edge budget must be between 1 and {GraphLayout.MaximumEdges:N0}.");
        }

        if (CollapseThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CollapseThreshold),
                "The graph cluster threshold must be at least one member.");
        }

        if (HostOnlyDrawn < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HostOnlyDrawn),
                "The count of processes drawn only for their connections to other hosts cannot be below zero.");
        }
    }
}

/// <summary>One node in the bounded graph projection. <see cref="Members"/> is never empty.</summary>
public sealed record GraphDisplayNode(
    string Key,
    string Label,
    GraphNodeKind Kind,
    string? GroupKey,
    ProcessInstanceId? Process,
    int? ProcessId,
    IReadOnlyList<ProcessInstanceId> Members,
    long Observations,
    int Relationships,
    int InternalRelationships)
{
    /// <summary>
    /// What the node's size reads in place of its relationships' records when the ranking's metric is another (§6.3): the
    /// bytes its relationships carry under a byte ranking. Null when the records are the metric.
    /// </summary>
    public long? Magnitude { get; init; }

    /// <summary>
    /// Whether the ranking's metric applies and none of its relationships measured a value for it - their sends recorded
    /// no size - so its magnitude is unknown, never zero (R3). It is drawn with §6.6's unmeasured encoding and adds
    /// nothing to the scale.
    /// </summary>
    public bool Unmeasured { get; init; }

    /// <summary>The value the node's size is read from: its magnitude when it has one, else its relationships' records.</summary>
    public long Weight => Magnitude ?? Observations;

    /// <summary>Which of the processes that held its PID a process node is, from 1 (<see cref="ProcessNode.PidHolder"/>).</summary>
    public int PidHolder { get; init; } = 1;

    /// <summary>How many processes held a process node's PID in the capture (<see cref="ProcessNode.PidHolders"/>).</summary>
    public int PidHolders { get; init; } = 1;

    /// <summary>
    /// For the Other hosts node, how many drawn processes its edges come from: those whose connections to other hosts it
    /// stands for. Zero for every other node.
    /// </summary>
    public int ConnectedProcesses { get; init; }

    /// <summary>
    /// A process node's PID as its process's <see cref="ProcessNode.PidLabel"/> reads it, a reused PID's holder numbered;
    /// null for an aggregate.
    /// </summary>
    public string? PidLabel => ProcessId is { } pid ? ProcessNode.PidLabelOf(pid, PidHolder, PidHolders) : null;

    /// <summary>
    /// The node as a card names it: a process by its name and PID, as <see cref="ProcessNode.NameWithPid"/> does, so one
    /// executable's instances read apart, and an aggregate by its label.
    /// </summary>
    public string Title => ProcessId is { } pid && PidLabel is { } pidLabel
        ? string.Equals(Label, ProcessNode.PidName(pid), StringComparison.Ordinal) ? pidLabel : $"{Label} · {pidLabel}"
        : Label;
}

/// <summary>
/// One drawn edge. Several source relationships may collapse into it; <see cref="Relationships"/> preserves the exact
/// source keys so highlighting and interval re-counting can resolve through the presentation aggregate (R13).
/// </summary>
public sealed record GraphDisplayEdge(
    string Key,
    string SourceKey,
    string TargetKey,
    Mechanism Mechanism,
    long ObservationCount,
    RelationStrength Strength,
    IReadOnlyList<string> Relationships)
{
    /// <summary>
    /// What the edge's thickness reads in place of its records when the ranking's metric is another (§6.3): the bytes sent
    /// across it under a byte ranking. Null when the records are the metric.
    /// </summary>
    public long? Magnitude { get; init; }

    /// <summary>
    /// Whether the ranking's metric applies and none of the relationships drawn here measured a value for it - their sends
    /// recorded no size - so its magnitude is unknown, never zero (R3). It is drawn with §6.6's unmeasured encoding, not a
    /// thickness, and adds nothing to the scale.
    /// </summary>
    public bool Unmeasured { get; init; }

    /// <summary>The value the edge's thickness is read from: its magnitude when it has one, else its records.</summary>
    public long Weight => Magnitude ?? ObservationCount;

    /// <summary>
    /// Whether the edge runs to the Other hosts node: its source's connections to other hosts over its mechanism, named by
    /// their host links' keys. It is no relationship - no total counts it - and it carries no traffic value: it is drawn
    /// in its mechanism's hue and its evidence's pattern at the thinnest constant stroke (§6.3), so neither a brush nor a
    /// ranking's metric changes it.
    /// </summary>
    public bool ToHosts { get; init; }

    /// <summary>
    /// Where an edge runs beside the others between the same two nodes, in lanes from the line between them: 0 alone,
    /// -0.5 and 0.5 for two - a node's TCP and UDP to the other hosts, or two processes' TCP and UDP - and -1, 0 and 1 for
    /// three, so none is drawn over another. A lane is taken across the line from the ordinally first of the two nodes,
    /// whichever the edge's source is.
    /// </summary>
    public double Lane { get; init; }
}

/// <summary>
/// The complete process membership of a bounded communication graph. Processes may share a drawn node, but none are
/// omitted; source relationships that become internal to a cluster stay represented by that node's counts.
/// </summary>
public sealed class GraphDisplay
{
    private readonly IReadOnlyDictionary<string, GraphDisplayNode> nodesByKey;
    private readonly IReadOnlyDictionary<ProcessInstanceId, GraphDisplayNode> nodesByProcess;
    private readonly IReadOnlyDictionary<string, GraphDisplayEdge> edgesByRelationship;
    private readonly IReadOnlyDictionary<string, GraphDisplayEdge> edgesByHostLink;
    private readonly IReadOnlyDictionary<string, GraphDisplayNode> nodesByRelationship;

    public GraphDisplay(
        IReadOnlyList<GraphDisplayNode> nodes,
        IReadOnlyList<GraphDisplayEdge> edges,
        int totalProcesses,
        string? expandedGroup,
        ProcessInstanceId? kept,
        int totalRelationships = 0,
        IReadOnlyList<string>? relationshipKeys = null,
        IReadOnlyDictionary<string, string>? internalRelationshipNodes = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentOutOfRangeException.ThrowIfNegative(totalProcesses);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRelationships);

        var byKey = new Dictionary<string, GraphDisplayNode>(nodes.Count, StringComparer.Ordinal);
        var byProcess = new Dictionary<ProcessInstanceId, GraphDisplayNode>(Math.Max(nodes.Count, totalProcesses));
        foreach (GraphDisplayNode node in nodes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Label);
            ArgumentNullException.ThrowIfNull(node.Members);
            if ((node.Members.Count == 0) != (node.Kind == GraphNodeKind.OtherHosts))
            {
                throw new ArgumentException(node.Kind == GraphNodeKind.OtherHosts
                    ? $"Graph node {node.Key} stands for other hosts and holds no process."
                    : $"Graph node {node.Key} has no process members.", nameof(nodes));
            }

            if (node.Kind == GraphNodeKind.OtherHosts && byKey.Values.Any(drawn => drawn.Kind == GraphNodeKind.OtherHosts))
            {
                throw new ArgumentException($"Graph node {node.Key} is a second node for the other hosts.", nameof(nodes));
            }

            if (!byKey.TryAdd(node.Key, node))
            {
                throw new ArgumentException($"Graph node key {node.Key} occurs twice.", nameof(nodes));
            }

            foreach (ProcessInstanceId member in node.Members)
            {
                if (!byProcess.TryAdd(member, node))
                {
                    throw new ArgumentException($"Process instance {member} occurs in more than one drawn node.", nameof(nodes));
                }
            }
        }

        if (byProcess.Count != 0 && byProcess.Count != totalProcesses)
        {
            throw new ArgumentException(
                $"The graph draws {byProcess.Count:N0} process instances but declares {totalProcesses:N0}.", nameof(totalProcesses));
        }

        var byRelationship = new Dictionary<string, GraphDisplayEdge>(StringComparer.Ordinal);
        var byHostLink = new Dictionary<string, GraphDisplayEdge>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphDisplayEdge edge in edges)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.SourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.TargetKey);
            ArgumentNullException.ThrowIfNull(edge.Relationships);
            if (!edgeKeys.Add(edge.Key))
            {
                throw new ArgumentException($"Drawn edge key {edge.Key} occurs twice.", nameof(edges));
            }

            if (!byKey.TryGetValue(edge.SourceKey, out GraphDisplayNode? from)
                || !byKey.TryGetValue(edge.TargetKey, out GraphDisplayNode? to))
            {
                throw new ArgumentException($"Graph edge {edge.Key} names a node that is not drawn.", nameof(edges));
            }

            if (edge.Relationships.Count == 0)
            {
                throw new ArgumentException($"Graph edge {edge.Key} has no source relationships.", nameof(edges));
            }

            // An edge to the other hosts names host links, which are no relationships and are kept apart from them.
            if (edge.ToHosts != (to.Kind == GraphNodeKind.OtherHosts) || from.Kind == GraphNodeKind.OtherHosts)
            {
                throw new ArgumentException($"Graph edge {edge.Key} runs to or from the other hosts unlike a host edge.", nameof(edges));
            }

            if (edge.ToHosts)
            {
                foreach (string link in edge.Relationships)
                {
                    if (!HostLink.IsKey(link) || !byHostLink.TryAdd(link, edge))
                    {
                        throw new ArgumentException($"Host link {link} is not drawn by exactly one edge.", nameof(edges));
                    }
                }

                continue;
            }

            foreach (string relationship in edge.Relationships)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(relationship);
                if (!byRelationship.TryAdd(relationship, edge))
                {
                    throw new ArgumentException(
                        $"Source relationship {relationship} occurs in more than one drawn edge.", nameof(edges));
                }
            }
        }

        internalRelationshipNodes ??= new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allRelationshipKeys = relationshipKeys is null
            ? [.. byRelationship.Keys.Concat(internalRelationshipNodes.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            : [.. relationshipKeys.Order(StringComparer.Ordinal)];
        var relationshipSet = allRelationshipKeys.ToHashSet(StringComparer.Ordinal);
        int resolvedTotalRelationships = relationshipKeys is null && totalRelationships == 0
            ? allRelationshipKeys.Length
            : totalRelationships;
        var nodeByRelationship = new Dictionary<string, GraphDisplayNode>(internalRelationshipNodes.Count, StringComparer.Ordinal);
        foreach ((string relationship, string nodeKey) in internalRelationshipNodes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relationship);
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeKey);
            if (byRelationship.ContainsKey(relationship)
                || !relationshipSet.Contains(relationship)
                || !byKey.TryGetValue(nodeKey, out GraphDisplayNode? node)
                || !nodeByRelationship.TryAdd(relationship, node))
            {
                throw new ArgumentException(
                    $"Internal relationship {relationship} does not resolve to exactly one drawn node.",
                    nameof(internalRelationshipNodes));
            }
        }

        if (relationshipSet.Count != allRelationshipKeys.Length
            || allRelationshipKeys.Length != resolvedTotalRelationships
            || byRelationship.Keys.Any(key => !relationshipSet.Contains(key))
            || byRelationship.Count + nodeByRelationship.Count != resolvedTotalRelationships)
        {
            throw new ArgumentException("The graph source relationship set is inconsistent.", nameof(relationshipKeys));
        }

        Nodes = nodes;
        Edges = edges;
        TotalProcesses = totalProcesses;
        TotalRelationships = resolvedTotalRelationships;
        ExpandedGroup = expandedGroup;
        Kept = kept;
        RelationshipKeys = Array.AsReadOnly(allRelationshipKeys);
        nodesByKey = byKey;
        nodesByProcess = byProcess;
        edgesByRelationship = byRelationship;
        edgesByHostLink = byHostLink;
        nodesByRelationship = nodeByRelationship;
    }

    public IReadOnlyList<GraphDisplayNode> Nodes { get; }
    public IReadOnlyList<GraphDisplayEdge> Edges { get; }
    public int TotalProcesses { get; }
    public int TotalRelationships { get; }
    public string? ExpandedGroup { get; }
    public ProcessInstanceId? Kept { get; }
    public IReadOnlyList<string> RelationshipKeys { get; }
    public bool IsClustered => Nodes.Any(node => node.Kind is not (GraphNodeKind.Process or GraphNodeKind.OtherHosts));

    /// <summary>
    /// The same graph with each drawn edge and node carrying the magnitude its drawing reads in place of its records - the
    /// ranking's metric, so the ranked table and the graph never disagree about magnitude (§6.3). A null value is one the
    /// metric could not measure: that mark is <see cref="GraphDisplayEdge.Unmeasured"/>, never drawn as zero (R3).
    /// Structure, membership and every count stay as they are.
    /// </summary>
    public GraphDisplay WithMagnitudes(Func<GraphDisplayEdge, long?> edge, Func<GraphDisplayNode, long?> node)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(node);
        GraphDisplayNode[] nodes = [.. Nodes.Select(drawn => drawn.Kind == GraphNodeKind.OtherHosts ? drawn
            : node(drawn) is { } value
                ? drawn with { Magnitude = value, Unmeasured = false }
                : drawn with { Magnitude = 0, Unmeasured = true })];
        GraphDisplayEdge[] edges = [.. Edges.Select(drawn => drawn.ToHosts ? drawn
            : edge(drawn) is { } value
                ? drawn with { Magnitude = value, Unmeasured = false }
                : drawn with { Magnitude = 0, Unmeasured = true })];
        return new GraphDisplay(nodes, edges, TotalProcesses, ExpandedGroup, Kept, TotalRelationships, RelationshipKeys,
            nodesByRelationship.ToDictionary(pair => pair.Key, pair => pair.Value.Key, StringComparer.Ordinal));
    }

    public GraphDisplayNode? Node(string key) => nodesByKey.GetValueOrDefault(key);
    public GraphDisplayNode? NodeOf(ProcessInstanceId process) => nodesByProcess.GetValueOrDefault(process);
    public GraphDisplayEdge? EdgeOf(string relationship) => edgesByRelationship.GetValueOrDefault(relationship);

    /// <summary>The drawn edge to the other hosts that a host link is drawn in; null when it is not drawn.</summary>
    public GraphDisplayEdge? EdgeOfHostLink(string link) => edgesByHostLink.GetValueOrDefault(link);

    /// <summary>The Other hosts node, when a process's connections to other hosts are drawn; null otherwise.</summary>
    public GraphDisplayNode? OtherHosts => Nodes.FirstOrDefault(node => node.Kind == GraphNodeKind.OtherHosts);
    public GraphDisplayNode? NodeOfRelationship(string relationship) => nodesByRelationship.GetValueOrDefault(relationship);
}

/// <summary>
/// Deterministic §6.3 presentation compaction. Its membership is derived from the whole published graph and therefore
/// does not churn while a time brush merely re-counts observations (§6.4). The ranking/table remain un-compacted.
/// </summary>
public static class GraphProjection
{
    private const string QuietKey = "cluster:quiet";
    private const string HostOnlyKey = "cluster:other-hosts-only";
    private const string RemainderKey = "cluster:remainder";
    private const string ContextKey = "cluster:context";

    /// <summary>The Other hosts node's key.</summary>
    public const string OtherHostsKey = "resource:other-hosts";

    private sealed record Descriptor(
        GraphNodeKind Kind,
        string Label,
        string? GroupKey,
        ProcessInstanceId? Process,
        int? ProcessId,
        int PidHolder = 1,
        int PidHolders = 1);

    /// <summary>One step of the fallback order: these members join the drawn node <paramref name="Key"/>.</summary>
    private sealed record Fold(string Key, IReadOnlyList<ProcessInstanceId> Members);

    private readonly record struct AggregateKey(string Source, string Target, Mechanism Mechanism);

    private sealed class EdgeAggregate
    {
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required Mechanism Mechanism { get; init; }
        public long Observations { get; set; }
        public RelationStrength Strength { get; set; } = RelationStrength.Direct;
        public List<string> Relationships { get; } = [];
    }

    private sealed class NodeTotals
    {
        public long Observations { get; set; }
        public int Relationships { get; set; }
        public int InternalRelationships { get; set; }
    }

    /// <summary>
    /// Projects a snapshot for drawing. <paramref name="neighborhood"/>, when given, is the set of processes a focused rung
    /// draws (§3.2: a group's members and their peers, an instance and its peers, a channel's participants); every other
    /// process is counted in one context node rather than drawn, and no process is omitted.
    /// </summary>
    public static GraphDisplay Project(
        WorkspaceSnapshot snapshot,
        string? expandedGroup = null,
        ProcessInstanceId? kept = null,
        GraphDisplayBudget? budget = null,
        IReadOnlySet<ProcessInstanceId>? neighborhood = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        budget ??= GraphDisplayBudget.Default;
        budget.Validate();

        Dictionary<string, ProcessGroup> groups = ValidateGroups(snapshot);
        Dictionary<ProcessInstanceId, ProcessNode> processes = ValidateProcesses(snapshot, groups);
        ValidateEdges(snapshot, processes);
        ValidateHostLinks(snapshot, processes);
        if (neighborhood is not null)
        {
            if (neighborhood.Any(id => !processes.ContainsKey(id)))
            {
                throw new ArgumentException("The graph neighbourhood names a process instance absent from this snapshot.",
                    nameof(neighborhood));
            }

            if (kept is { } focus && !neighborhood.Contains(focus))
            {
                throw new ArgumentException("The kept process lies outside the graph neighbourhood.", nameof(neighborhood));
            }
        }

        if (expandedGroup is not null && !groups.ContainsKey(expandedGroup))
        {
            throw new ArgumentException($"The graph focus names unknown process group {expandedGroup}.", nameof(expandedGroup));
        }

        if (kept is { } keptId)
        {
            if (!processes.TryGetValue(keptId, out ProcessNode? keptProcess))
            {
                throw new ArgumentException($"The graph focus names unknown process instance {keptId}.", nameof(kept));
            }

            if (expandedGroup is not null
                && !string.Equals(keptProcess.GroupKey, expandedGroup, StringComparison.Ordinal))
            {
                throw new ArgumentException("The kept process is not a member of the expanded graph group.", nameof(kept));
            }
        }

        var assignment = new Dictionary<ProcessInstanceId, string>(processes.Count);
        var descriptors = new Dictionary<string, Descriptor>(processes.Count + 3, StringComparer.Ordinal)
        {
            [QuietKey] = new(GraphNodeKind.Quiet, "No relationships", null, null, null),
            [HostOnlyKey] = new(GraphNodeKind.OtherHostsOnly, "Other hosts only", null, null, null),
            [RemainderKey] = new(GraphNodeKind.Remainder, "Other processes", null, null, null),
            [ContextKey] = new(GraphNodeKind.Context, "Rest of the machine", null, null, null),
        };
        foreach (ProcessNode process in processes.Values)
        {
            string key = process.Id.ToString();
            assignment[process.Id] = key;
            descriptors[key] = new(
                GraphNodeKind.Process, process.Name, process.GroupKey,
                process.Id, process.ProcessId, process.PidHolder, process.PidHolders);
        }

        // A descriptor names what a node would be; only a node that receives members is drawn.
        foreach (ProcessGroup group in groups.Values)
        {
            descriptors[GroupNodeKey(group.Key)] = new(GraphNodeKind.Group, group.Name, group.Key, null, null);
        }

        if (expandedGroup is not null)
        {
            descriptors[OtherMembersKey(expandedGroup)] = new(
                GraphNodeKind.OtherMembers, "Other members", expandedGroup, null, null);
        }

        Dictionary<ProcessInstanceId, long> processMetric = ProcessMetrics(snapshot, processes.Keys);
        Dictionary<string, long> groupMetric = GroupMetrics(snapshot, processes.Values);
        Dictionary<string, ProcessInstanceId[]> membersByGroup = processes.Values
            .GroupBy(process => process.GroupKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(process => process.Id)
                    .OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        // A process with no relationship in the published scope has nothing to draw: scattered over the pane, hundreds of
        // them would be the unreadable cloud §6.3 forbids. Outside the explicit focus they are counted in one node - the
        // opened group's own in its Other members - and every one stays individual in the ranked table.
        int nodeCount = processes.Count;

        // A focused rung draws its neighbourhood (§3.2). Everything else is context, counted in one node whose edges are
        // the relationships leading out - even a single process outside, so the node always means "not in this focus".
        if (neighborhood is not null)
        {
            ProcessInstanceId[] outside = [.. processes.Keys.Where(id => !neighborhood.Contains(id))];
            foreach (ProcessInstanceId id in outside)
            {
                assignment[id] = ContextKey;
            }

            nodeCount -= Math.Max(0, outside.Length - 1);
        }

        // A process whose connections run to other hosts is drawn beside the processes it relates, edged to them.
        HashSet<ProcessInstanceId> related =
        [
            .. snapshot.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }),
            .. snapshot.HostLinks.Select(link => link.Process),
        ];
        ProcessInstanceId[] quiet = [.. processes.Keys
            .Where(id => !related.Contains(id) && kept != id && IsIndividual(id, assignment))
            .OrderBy(id => id.ToString(), StringComparer.Ordinal)];
        nodeCount -= FoldTogether(QuietKey,
            [.. quiet.Where(id => !string.Equals(processes[id].GroupKey, expandedGroup, StringComparison.Ordinal))], assignment);
        if (expandedGroup is not null)
        {
            nodeCount -= FoldTogether(OtherMembersKey(expandedGroup),
                [.. quiet.Where(id => string.Equals(processes[id].GroupKey, expandedGroup, StringComparison.Ordinal))],
                assignment);
        }

        // Honour the size threshold on what would be drawn: a group with more than the threshold of individually drawn
        // members collapses, except the group the user deliberately opened. A kept process stays addressable on its own.
        foreach ((string groupKey, ProcessInstanceId[] members) in membersByGroup
            .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ProcessInstanceId[] drawn = Individually(members, kept, assignment);
            if (string.Equals(groupKey, expandedGroup, StringComparison.Ordinal) || drawn.Length <= budget.CollapseThreshold)
            {
                continue;
            }

            nodeCount -= FoldTogether(GroupNodeKey(groupKey), drawn, assignment);
        }

        // A process whose only drawn activity is its connections to other hosts is drawn beside the hub while few are. A
        // busy machine has dozens, a star that buries the relationships between processes: past the budget's count, outside
        // the explicit focus, the busiest by their connections' records stay drawn and the rest are counted in one Other
        // hosts only node, edged to Other hosts once per transport. A collapsed group keeps its members.
        HashSet<ProcessInstanceId> ends = [.. snapshot.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })];
        Dictionary<ProcessInstanceId, long> reach = [];
        foreach (HostLink link in snapshot.HostLinks)
        {
            reach[link.Process] = Add(reach.GetValueOrDefault(link.Process), link.Records);
        }

        ProcessInstanceId[] hostOnly = [.. reach.Keys
            .Where(id => !ends.Contains(id) && kept != id && IsIndividual(id, assignment)
                && !string.Equals(processes[id].GroupKey, expandedGroup, StringComparison.Ordinal))
            .OrderByDescending(id => reach[id])
            .ThenBy(id => id.ToString(), StringComparer.Ordinal)];
        if (hostOnly.Length > budget.HostOnlyDrawn)
        {
            nodeCount -= FoldTogether(HostOnlyKey, hostOnly[budget.HostOnlyDrawn..], assignment);
        }

        // Then collapse further groups, lowest whole-session incident metric first, until the budget fits. The order never
        // uses a brushed interval, so a gesture cannot re-cluster the graph under the hand. The node budget is met by an
        // exact count; the edge budget is found by bisection over the same order.
        Fold[] collapses = [.. membersByGroup.Keys
            .Where(key => !string.Equals(key, expandedGroup, StringComparison.Ordinal))
            .OrderBy(key => groupMetric.GetValueOrDefault(key))
            .ThenBy(key => key, StringComparer.Ordinal)
            .Select(key => new Fold(GroupNodeKey(key), Individually(membersByGroup[key], kept, assignment)))
            .Where(fold => fold.Members.Count >= 2)];
        int length = 0;
        while (nodeCount > budget.Nodes && length < collapses.Length)
        {
            nodeCount -= collapses[length++].Members.Count - 1;
        }

        GraphDisplay display = ShortestFittingPrefix(
            snapshot, assignment, descriptors, collapses, length, budget, expandedGroup, kept);
        if (Fits(display, budget))
        {
            return display;
        }

        // An explicitly opened group may itself be larger or denser than the pane budget. Keep its highest-metric members
        // individually visible and fold the low end into its honest Other members node rather than truncating.
        if (expandedGroup is not null)
        {
            string other = OtherMembersKey(expandedGroup);
            bool exists = display.Node(other) is not null;
            Fold[] lowEnd = [.. Individually(membersByGroup[expandedGroup], kept, assignment)
                .OrderBy(id => processMetric.GetValueOrDefault(id))
                .ThenBy(id => id.ToString(), StringComparer.Ordinal)
                .Select(id => new Fold(other, [id]))];

            // A new Other members node saves nothing until its second member joins it; never draw a one-member aggregate.
            int minimum = Math.Max(exists ? 1 : 2, display.Nodes.Count - budget.Nodes + (exists ? 0 : 1));
            if (lowEnd.Length >= (exists ? 1 : 2))
            {
                display = ShortestFittingPrefix(snapshot, assignment, descriptors, lowEnd,
                    Math.Min(minimum, lowEnd.Length), budget, expandedGroup, kept);
                if (Fits(display, budget))
                {
                    return display;
                }
            }
        }

        // Singleton-heavy captures cannot be brought under budget by collapsing groups. Fold the least active remaining
        // drawn nodes into one explicit cross-group remainder. The focused process and the no-relationship node keep their
        // meaning, and nodes of the opened group go last; no process is lost.
        Fold[] remainder = [.. display.Nodes
            .Where(node => node.Kind is not (GraphNodeKind.Quiet or GraphNodeKind.Remainder or GraphNodeKind.Context
                or GraphNodeKind.OtherHosts or GraphNodeKind.OtherHostsOnly))
            .Where(node => kept is not { } focus || !node.Members.Contains(focus))
            .OrderBy(node => expandedGroup is not null
                && string.Equals(node.GroupKey, expandedGroup, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(node => node.Observations)
            .ThenBy(node => node.Members.Count)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .Select(node => new Fold(RemainderKey, node.Members))];
        if (remainder.Length >= 2)
        {
            display = ShortestFittingPrefix(snapshot, assignment, descriptors, remainder,
                Math.Min(Math.Max(2, display.Nodes.Count - budget.Nodes + 1), remainder.Length), budget, expandedGroup, kept);
        }

        if (!Fits(display, budget))
        {
            throw new InvalidOperationException(
                $"The graph projection could not fit {snapshot.Processes.Count:N0} processes and "
                + $"{snapshot.Edges.Count:N0} relationships inside the {budget.Nodes:N0}-node / {budget.Edges:N0}-edge "
                + "display budget without hiding the focused process.");
        }

        return display;
    }

    private static bool Fits(GraphDisplay display, GraphDisplayBudget budget) =>
        display.Nodes.Count <= budget.Nodes && display.Edges.Count <= budget.Edges;

    /// <summary>
    /// Applies the shortest prefix of <paramref name="folds"/>, at least <paramref name="minimum"/> long, after which the
    /// drawing fits the budget, and returns that drawing; when none fits, applies them all. Merging nodes can only merge
    /// edges, never split them, so the counts cannot rise along the prefix and bisection finds the shortest fitting one.
    /// </summary>
    private static GraphDisplay ShortestFittingPrefix(
        WorkspaceSnapshot snapshot,
        Dictionary<ProcessInstanceId, string> assignment,
        Dictionary<string, Descriptor> descriptors,
        IReadOnlyList<Fold> folds,
        int minimum,
        GraphDisplayBudget budget,
        string? expandedGroup,
        ProcessInstanceId? kept)
    {
        var baseline = new Dictionary<ProcessInstanceId, string>(assignment);
        void Apply(int length)
        {
            foreach ((ProcessInstanceId member, string key) in baseline)
            {
                assignment[member] = key;
            }

            for (int index = 0; index < length; index++)
            {
                foreach (ProcessInstanceId member in folds[index].Members)
                {
                    assignment[member] = folds[index].Key;
                }
            }
        }

        GraphDisplay Attempt(int length)
        {
            Apply(length);
            return Materialize(snapshot, assignment, descriptors, expandedGroup, kept);
        }

        // Usually the shortest admissible prefix already fits, and one drawing settles it.
        int low = Math.Clamp(minimum, 0, folds.Count);
        GraphDisplay shortest = Attempt(low);
        if (Fits(shortest, budget) || low == folds.Count)
        {
            return shortest;
        }

        int high = folds.Count;
        GraphDisplay best = Attempt(high);
        if (!Fits(best, budget))
        {
            return best;
        }

        // Prefix `low` does not fit and prefix `high` does; the shortest fitting prefix lies in (low, high].
        low++;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            GraphDisplay attempt = Attempt(middle);
            if (Fits(attempt, budget))
            {
                high = middle;
                best = attempt;
            }
            else
            {
                low = middle + 1;
            }
        }

        // The last attempt may have been a shorter prefix that did not fit; leave the assignment on the one returned.
        Apply(high);
        return best;
    }

    /// <summary>
    /// Re-counts the same drawing against an interval-scoped snapshot of the same published graph. The unscoped basis
    /// supplies the relationship structure so only magnitudes may change; topology and cluster membership stay fixed (§6.4).
    /// </summary>
    public static GraphDisplay Rescope(GraphDisplay display, WorkspaceSnapshot basis, WorkspaceSnapshot scoped)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(scoped);
        var basisProcesses = basis.Processes.Select(process => process.Id).ToHashSet();
        var scopedProcesses = scoped.Processes.Select(process => process.Id).ToHashSet();
        ProcessInstanceId[] displayedProcesses = [.. display.Nodes.SelectMany(node => node.Members)];
        if (basis.Processes.Count != display.TotalProcesses
            || basisProcesses.Count != basis.Processes.Count
            || scoped.Processes.Count != display.TotalProcesses
            || scopedProcesses.Count != scoped.Processes.Count
            || !basisProcesses.SetEquals(scopedProcesses)
            || displayedProcesses.Length != display.TotalProcesses
            || displayedProcesses.Any(id => !basisProcesses.Contains(id)))
        {
            throw new ArgumentException(
                "A graph can be re-counted only against snapshots with the same process instances.", nameof(scoped));
        }

        var basisEdges = basis.Edges.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        var edges = scoped.Edges.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        if (basisEdges.Count != display.TotalRelationships
            || edges.Count != display.TotalRelationships
            || !display.RelationshipKeys.ToHashSet(StringComparer.Ordinal).SetEquals(basisEdges.Keys)
            || !basisEdges.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(edges.Keys))
        {
            throw new ArgumentException(
                "A graph can be re-counted only against snapshots with the same relationship identities.", nameof(scoped));
        }

        foreach ((string key, CommunicationEdge original) in basisEdges)
        {
            CommunicationEdge current = edges[key];
            if (original.SourceId != current.SourceId
                || original.TargetId != current.TargetId
                || original.Mechanism != current.Mechanism
                || original.Strength != current.Strength)
            {
                throw new ArgumentException(
                    $"Relationship {key} changed endpoint, mechanism, or evidence strength while the graph was re-counted.",
                    nameof(scoped));
            }
        }

        Dictionary<ProcessInstanceId, string> assignment = display.Nodes
            .SelectMany(node => node.Members.Select(member => (member, node.Key)))
            .ToDictionary(entry => entry.member, entry => entry.Key);
        Dictionary<string, Descriptor> descriptors = display.Nodes.ToDictionary(
            node => node.Key,
            node => new Descriptor(node.Kind, node.Label, node.GroupKey, node.Process, node.ProcessId, node.PidHolder, node.PidHolders),
            StringComparer.Ordinal);
        GraphDisplay recounted = Materialize(scoped, assignment, descriptors, display.ExpandedGroup, display.Kept);
        if (!SameStructure(display, recounted))
        {
            throw new ArgumentException(
                "A graph can be re-counted only when relationship endpoints, mechanism and evidence strength are unchanged.",
                nameof(scoped));
        }

        return recounted;
    }

    private static bool SameStructure(GraphDisplay expected, GraphDisplay actual)
    {
        if (expected.Nodes.Count != actual.Nodes.Count || expected.Edges.Count != actual.Edges.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Nodes.Count; index++)
        {
            GraphDisplayNode left = expected.Nodes[index];
            GraphDisplayNode right = actual.Nodes[index];
            if (left.Key != right.Key
                || left.Kind != right.Kind
                || left.GroupKey != right.GroupKey
                || left.Relationships != right.Relationships
                || left.InternalRelationships != right.InternalRelationships
                || !left.Members.SequenceEqual(right.Members))
            {
                return false;
            }
        }

        for (int index = 0; index < expected.Edges.Count; index++)
        {
            GraphDisplayEdge left = expected.Edges[index];
            GraphDisplayEdge right = actual.Edges[index];
            if (left.Key != right.Key
                || left.SourceKey != right.SourceKey
                || left.TargetKey != right.TargetKey
                || left.Mechanism != right.Mechanism
                || left.Strength != right.Strength
                || !left.Relationships.SequenceEqual(right.Relationships))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, ProcessGroup> ValidateGroups(WorkspaceSnapshot snapshot)
    {
        var groups = new Dictionary<string, ProcessGroup>(snapshot.Groups.Count, StringComparer.Ordinal);
        foreach (ProcessGroup group in snapshot.Groups)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group.Key);
            if (!groups.TryAdd(group.Key, group))
            {
                throw new ArgumentException($"Process group key {group.Key} occurs twice.", nameof(snapshot));
            }
        }

        return groups;
    }

    private static Dictionary<ProcessInstanceId, ProcessNode> ValidateProcesses(
        WorkspaceSnapshot snapshot,
        Dictionary<string, ProcessGroup> groups)
    {
        var processes = new Dictionary<ProcessInstanceId, ProcessNode>(snapshot.Processes.Count);
        foreach (ProcessNode process in snapshot.Processes)
        {
            if (!groups.ContainsKey(process.GroupKey))
            {
                throw new ArgumentException(
                    $"Process instance {process.Id} names missing group {process.GroupKey}.", nameof(snapshot));
            }

            if (!processes.TryAdd(process.Id, process))
            {
                throw new ArgumentException($"Process instance {process.Id} occurs twice.", nameof(snapshot));
            }
        }

        return processes;
    }

    private static void ValidateEdges(
        WorkspaceSnapshot snapshot,
        Dictionary<ProcessInstanceId, ProcessNode> processes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.Key);
            if (!keys.Add(edge.Key))
            {
                throw new ArgumentException($"Relationship key {edge.Key} occurs twice.", nameof(snapshot));
            }

            if (!processes.ContainsKey(edge.SourceId) || !processes.ContainsKey(edge.TargetId))
            {
                throw new ArgumentException(
                    $"Relationship {edge.Key} names a process instance absent from this snapshot.", nameof(snapshot));
            }

            if (edge.ObservationCount < 0)
            {
                throw new ArgumentException($"Relationship {edge.Key} has a negative observation count.", nameof(snapshot));
            }
        }
    }

    private static void ValidateHostLinks(
        WorkspaceSnapshot snapshot,
        Dictionary<ProcessInstanceId, ProcessNode> processes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (HostLink link in snapshot.HostLinks)
        {
            if (!keys.Add(link.Key))
            {
                throw new ArgumentException($"Host link {link.Key} occurs twice.", nameof(snapshot));
            }

            if (!processes.ContainsKey(link.Process))
            {
                throw new ArgumentException($"Host link {link.Key} names a process instance absent from this snapshot.",
                    nameof(snapshot));
            }

            // At least one host, and no more hosts than connections: at least one connection.
            if (link.Records < 0 || link.Hosts < 1 || link.Hosts > link.Connections)
            {
                throw new ArgumentException($"Host link {link.Key} has impossible counts.", nameof(snapshot));
            }
        }
    }

    private static Dictionary<ProcessInstanceId, long> ProcessMetrics(
        WorkspaceSnapshot snapshot,
        IEnumerable<ProcessInstanceId> ids)
    {
        Dictionary<ProcessInstanceId, long> result = ids.ToDictionary(id => id, _ => 0L);
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            result[edge.SourceId] = Add(result[edge.SourceId], edge.ObservationCount);
            if (edge.TargetId != edge.SourceId)
            {
                result[edge.TargetId] = Add(result[edge.TargetId], edge.ObservationCount);
            }
        }

        return result;
    }

    private static Dictionary<string, long> GroupMetrics(
        WorkspaceSnapshot snapshot,
        IEnumerable<ProcessNode> processes)
    {
        Dictionary<ProcessInstanceId, string> groupOf = processes.ToDictionary(process => process.Id, process => process.GroupKey);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string group in groupOf.Values.Distinct(StringComparer.Ordinal))
        {
            result[group] = 0;
        }

        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            string source = groupOf[edge.SourceId];
            string target = groupOf[edge.TargetId];
            result[source] = Add(result[source], edge.ObservationCount);
            if (!string.Equals(source, target, StringComparison.Ordinal))
            {
                result[target] = Add(result[target], edge.ObservationCount);
            }
        }

        return result;
    }

    /// <summary>
    /// Draws individually drawn <paramref name="members"/> as the one new node <paramref name="key"/> and returns the
    /// nodes that saves. A single member is left alone: a one-member aggregate saves nothing and misstates what it holds.
    /// </summary>
    private static int FoldTogether(
        string key,
        ProcessInstanceId[] members,
        Dictionary<ProcessInstanceId, string> assignment)
    {
        if (members.Length < 2)
        {
            return 0;
        }

        foreach (ProcessInstanceId member in members)
        {
            assignment[member] = key;
        }

        return members.Length - 1;
    }

    /// <summary>The members still drawn as their own node, other than the process kept on its own.</summary>
    private static ProcessInstanceId[] Individually(
        IEnumerable<ProcessInstanceId> members,
        ProcessInstanceId? kept,
        Dictionary<ProcessInstanceId, string> assignment) =>
        [.. members.Where(id => kept != id && IsIndividual(id, assignment))];

    private static GraphDisplay Materialize(
        WorkspaceSnapshot snapshot,
        Dictionary<ProcessInstanceId, string> assignment,
        Dictionary<string, Descriptor> descriptors,
        string? expandedGroup,
        ProcessInstanceId? kept)
    {
        if (snapshot.Processes.Count != assignment.Count)
        {
            throw new InvalidOperationException("The graph projection assignment does not cover every process instance.");
        }

        var members = new Dictionary<string, List<ProcessInstanceId>>(StringComparer.Ordinal);
        foreach (ProcessNode process in snapshot.Processes)
        {
            if (!assignment.TryGetValue(process.Id, out string? key) || !descriptors.ContainsKey(key))
            {
                throw new InvalidOperationException($"Process instance {process.Id} has no graph presentation node.");
            }

            if (!members.TryGetValue(key, out List<ProcessInstanceId>? list))
            {
                list = [];
                members[key] = list;
            }

            list.Add(process.Id);
        }

        var totals = members.Keys.ToDictionary(key => key, _ => new NodeTotals(), StringComparer.Ordinal);
        var aggregates = new Dictionary<AggregateKey, EdgeAggregate>();
        var internalRelationshipNodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            string source = assignment[edge.SourceId];
            string target = assignment[edge.TargetId];
            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                NodeTotals same = totals[source];
                same.Relationships++;
                same.InternalRelationships++;
                same.Observations = Add(same.Observations, edge.ObservationCount);
                internalRelationshipNodes.Add(edge.Key, source);
                continue;
            }

            NodeTotals sourceTotals = totals[source];
            sourceTotals.Relationships++;
            sourceTotals.Observations = Add(sourceTotals.Observations, edge.ObservationCount);
            NodeTotals targetTotals = totals[target];
            targetTotals.Relationships++;
            targetTotals.Observations = Add(targetTotals.Observations, edge.ObservationCount);

            var key = new AggregateKey(source, target, edge.Mechanism);
            if (!aggregates.TryGetValue(key, out EdgeAggregate? aggregate))
            {
                aggregate = new()
                {
                    Source = source,
                    Target = target,
                    Mechanism = edge.Mechanism,
                    Strength = edge.Strength,
                };
                aggregates[key] = aggregate;
            }

            aggregate.Observations = Add(aggregate.Observations, edge.ObservationCount);
            if (edge.Strength > aggregate.Strength)
            {
                aggregate.Strength = edge.Strength;
            }
            aggregate.Relationships.Add(edge.Key);
        }

        GraphDisplayNode[] nodes = [.. members
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry =>
            {
                Descriptor descriptor = descriptors[entry.Key];
                ProcessInstanceId[] ids = [.. entry.Value.OrderBy(id => id.ToString(), StringComparer.Ordinal)];
                NodeTotals nodeTotals = totals[entry.Key];
                return new GraphDisplayNode(
                    entry.Key,
                    descriptor.Label,
                    descriptor.Kind,
                    descriptor.GroupKey,
                    descriptor.Process,
                    descriptor.ProcessId,
                    Array.AsReadOnly(ids),
                    nodeTotals.Observations,
                    nodeTotals.Relationships,
                    nodeTotals.InternalRelationships)
                {
                    PidHolder = descriptor.PidHolder,
                    PidHolders = descriptor.PidHolders,
                };
            })];

        var builtEdges = new List<GraphDisplayEdge>(aggregates.Count);
        var usedEdgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (EdgeAggregate aggregate in aggregates.Values
            .OrderBy(value => value.Source, StringComparer.Ordinal)
            .ThenBy(value => value.Target, StringComparer.Ordinal)
            .ThenBy(value => value.Mechanism))
        {
            string[] relationships = [.. aggregate.Relationships.Order(StringComparer.Ordinal)];
            string baseKey = relationships.Length == 1 ? relationships[0] : AggregateEdgeKey(aggregate, relationships);
            string key = baseKey;
            for (int suffix = 2; !usedEdgeKeys.Add(key); suffix++)
            {
                key = $"{baseKey}:{suffix}";
            }

            builtEdges.Add(new(
                key,
                aggregate.Source,
                aggregate.Target,
                aggregate.Mechanism,
                aggregate.Observations,
                aggregate.Strength,
                Array.AsReadOnly(relationships)));
        }

        // The relationships between the same two drawn nodes - over two transports, an RPC link beside them, or two whose
        // ends were met in the other order - run side by side, so none is drawn over another.
        foreach (IGrouping<(string, string), int> between in Enumerable.Range(0, builtEdges.Count)
            .GroupBy(index => Unordered(builtEdges[index].SourceKey, builtEdges[index].TargetKey)))
        {
            int[] side = [.. between];
            for (int lane = 0; side.Length > 1 && lane < side.Length; lane++)
            {
                GraphDisplayEdge edge = builtEdges[side[lane]];
                double across = lane - ((side.Length - 1) / 2d);
                builtEdges[side[lane]] = edge with
                {
                    Lane = string.CompareOrdinal(edge.SourceKey, edge.TargetKey) <= 0 ? across : -across,
                };
            }
        }

        // Each drawn node's connections to other hosts, one edge per transport, to the one node that stands for them. A
        // process outside a focused rung's neighbourhood is context, and its connections are left to its own rung.
        var hostEdges = new Dictionary<(string Source, Mechanism Mechanism), EdgeAggregate>();
        var connected = new HashSet<ProcessInstanceId>();
        foreach (HostLink link in snapshot.HostLinks)
        {
            string source = assignment[link.Process];
            if (string.Equals(source, ContextKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!hostEdges.TryGetValue((source, link.Mechanism), out EdgeAggregate? reach))
            {
                reach = new() { Source = source, Target = OtherHostsKey, Mechanism = link.Mechanism };
                hostEdges[(source, link.Mechanism)] = reach;
            }

            reach.Observations = Add(reach.Observations, link.Records);
            if (link.Strength > reach.Strength)
            {
                reach.Strength = link.Strength;
            }

            reach.Relationships.Add(link.Key);
            connected.Add(link.Process);
        }

        // A node's edges over two transports run side by side, so neither is drawn over the other.
        foreach (IGrouping<string, EdgeAggregate> bySource in hostEdges.Values
            .OrderBy(reach => reach.Source, StringComparer.Ordinal)
            .ThenBy(reach => reach.Mechanism)
            .GroupBy(reach => reach.Source, StringComparer.Ordinal))
        {
            EdgeAggregate[] side = [.. bySource];
            for (int lane = 0; lane < side.Length; lane++)
            {
                EdgeAggregate reach = side[lane];
                builtEdges.Add(new(
                    $"{OtherHostsKey}:{reach.Source}:{MechanismText.Name(reach.Mechanism).ToLowerInvariant()}",
                    reach.Source,
                    OtherHostsKey,
                    reach.Mechanism,
                    reach.Observations,
                    reach.Strength,
                    Array.AsReadOnly([.. reach.Relationships.Order(StringComparer.Ordinal)]))
                {
                    ToHosts = true,
                    Lane = lane - ((side.Length - 1) / 2d),
                });
            }
        }

        if (hostEdges.Count > 0)
        {
            nodes = [.. nodes, new GraphDisplayNode(
                OtherHostsKey, "Other hosts", GraphNodeKind.OtherHosts, null, null, null, [],
                hostEdges.Values.Aggregate(0L, (sum, reach) => Add(sum, reach.Observations)), 0, 0)
            {
                ConnectedProcesses = connected.Count,
            }];
        }

        GraphDisplayEdge[] edges = [.. builtEdges.OrderBy(edge => edge.Key, StringComparer.Ordinal)];
        string[] relationshipKeys = [.. snapshot.Edges.Select(edge => edge.Key).Order(StringComparer.Ordinal)];

        var display = new GraphDisplay(
            Array.AsReadOnly(nodes),
            Array.AsReadOnly(edges),
            snapshot.Processes.Count,
            expandedGroup,
            kept,
            snapshot.Edges.Count,
            Array.AsReadOnly(relationshipKeys),
            internalRelationshipNodes);
        if (display.Nodes.Sum(node => node.Members.Count) != snapshot.Processes.Count)
        {
            throw new InvalidOperationException("Graph compaction omitted a process instance.");
        }

        return display;
    }

    /// <summary>Two drawn nodes' keys in ordinal order, which names the pair whichever of them an edge leaves from.</summary>
    private static (string First, string Second) Unordered(string source, string target) =>
        string.CompareOrdinal(source, target) <= 0 ? (source, target) : (target, source);

    private static string AggregateEdgeKey(EdgeAggregate aggregate, IReadOnlyList<string> relationships)
    {
        var canonical = new StringBuilder();
        canonical.Append(aggregate.Source).Append('\n')
            .Append(aggregate.Target).Append('\n')
            .Append((int)aggregate.Mechanism).Append('\n');
        foreach (string relationship in relationships)
        {
            canonical.Append(relationship).Append('\n');
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return $"cluster-edge:{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static long Add(long left, long right)
    {
        if (right < 0 || left > long.MaxValue - right)
        {
            throw new InvalidDataException("Graph observation counts exceed the representable 64-bit range.");
        }

        return left + right;
    }

    private static bool IsIndividual(ProcessInstanceId id, Dictionary<ProcessInstanceId, string> assignment) =>
        assignment.TryGetValue(id, out string? key) && string.Equals(key, id.ToString(), StringComparison.Ordinal);

    private static string GroupNodeKey(string group) => $"cluster:group:{group.Length}:{group}";
    private static string OtherMembersKey(string group) => $"cluster:group-other:{group.Length}:{group}";
}
