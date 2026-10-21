using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The relationships between two programs (§6.3, L0's aggregate edges): every relationship whose two processes ran these
/// two executables, summed, with the connections each program's processes made to the other's where the capture saw them
/// open (`contracts/relations-v1.md` §5). One program at both ends is its own processes connecting to each other.
/// </summary>
/// <param name="First">The program named first: the one whose processes made more of the connections, else the first by
/// name.</param>
/// <param name="Second">The other program, or <paramref name="First"/> again where its own processes connected to each
/// other.</param>
/// <param name="Relationships">How many relationships join them: one for each two processes and mechanism.</param>
/// <param name="Processes">How many processes are at their ends, the two programs' together.</param>
/// <param name="Records">Their records at both ends, as each relationship counts them.</param>
/// <param name="Mechanisms">What they ran over, in the mechanisms' order.</param>
/// <param name="FirstConnected">How many of their connections the first program's processes made.</param>
/// <param name="SecondConnected">How many the second's made to the first's; none where one program is at both ends.</param>
public sealed record ProgramPair(
    ProcessGroup First,
    ProcessGroup Second,
    int Relationships,
    int Processes,
    long Records,
    IReadOnlyList<Mechanism> Mechanisms,
    long FirstConnected,
    long SecondConnected)
{
    /// <summary>
    /// The two programs as a relationship joins its processes: "client.exe → server.exe" where the first's processes made
    /// the connections, "a.exe ⇄ b.exe" where each made some, and "a.exe and b.exe" where the capture saw none open.
    /// </summary>
    public string Joined => (FirstConnected > 0, SecondConnected > 0) switch
    {
        (true, false) => $"{First.Name} → {Second.Name}",
        (true, true) => $"{First.Name} ⇄ {Second.Name}",
        _ => $"{First.Name} and {Second.Name}",
    };

    /// <summary>
    /// The same as a screen reader says it, who connected in words: "client.exe connected to server.exe", "a.exe and b.exe,
    /// each connected to the other", "a.exe and b.exe".
    /// </summary>
    public string Spoken => (FirstConnected > 0, SecondConnected > 0) switch
    {
        (true, false) => $"{First.Name} connected to {Second.Name}",
        (true, true) => $"{First.Name} and {Second.Name}, each connected to the other",
        _ => $"{First.Name} and {Second.Name}",
    };

    /// <summary>
    /// The ports their connections arrived at, at the end that accepted each, each once, the lowest first: their
    /// relationships' (<see cref="CommunicationEdge.AcceptedPorts"/>) together.
    /// </summary>
    public IReadOnlyList<ushort> AcceptedPorts { get; init; } = [];

    /// <summary>
    /// What they ran over, in words, TCP with the ports its connections arrived at: "TCP at 135", "UDP", "TCP at 135 and
    /// UDP". A list of ports says "and" itself, so the last mechanism after one is set apart by a comma: "TCP at 80 and
    /// 443, and UDP".
    /// </summary>
    public string Over
    {
        get
        {
            string[] parts = [.. Mechanisms.Select(mechanism => mechanism == Mechanism.Tcp
                ? DirectionText.Over(mechanism, AcceptedPorts)
                : MechanismText.Name(mechanism))];
            return parts.Length > 1 && parts[0].Contains(" and ", StringComparison.Ordinal)
                ? string.Join(", ", parts[..^1]) + ", and " + parts[^1]
                : WorkspaceLayout.Series(parts);
        }
    }
}

/// <summary>
/// The pairs of programs a session's relationships join (§6.3): what the graph's aggregate edges between executable groups
/// sum, read whole, so a busy machine's programs are seen talking to each other however many processes each runs.
/// </summary>
public static class ProgramPairs
{
    /// <summary>
    /// The pairs of programs <paramref name="edges"/> join, the busiest first: by their records, then their relationships,
    /// then the programs' names. Each process is of the group <paramref name="processes"/> names for it.
    /// </summary>
    /// <exception cref="ArgumentException">A relationship names a process no group holds.</exception>
    public static IReadOnlyList<ProgramPair> Of(
        IReadOnlyCollection<CommunicationEdge> edges,
        IReadOnlyCollection<ProcessNode> processes,
        IReadOnlyCollection<ProcessGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(groups);
        Dictionary<string, ProcessGroup> named = [];
        foreach (ProcessGroup group in groups) named.TryAdd(group.Key, group);
        Dictionary<ProcessInstanceId, ProcessGroup> groupOf = [];
        foreach (ProcessNode process in processes)
        {
            if (named.TryGetValue(process.GroupKey, out ProcessGroup? group)) groupOf.TryAdd(process.Id, group);
        }

        Dictionary<(string, string), Tally> pairs = [];
        foreach (CommunicationEdge edge in edges)
        {
            ProcessGroup source = Of(edge.SourceId, edge), target = Of(edge.TargetId, edge);

            // Each pair once, whichever end a relationship draws first: its programs in key order.
            bool ordered = string.CompareOrdinal(source.Key, target.Key) <= 0;
            (ProcessGroup one, ProcessGroup other) = ordered ? (source, target) : (target, source);
            if (!pairs.TryGetValue((one.Key, other.Key), out Tally? tally))
            {
                pairs[(one.Key, other.Key)] = tally = new Tally(one, other);
            }

            tally.Relationships++;
            tally.Records += edge.ObservationCount;
            tally.Mechanisms.Add(edge.Mechanism);
            tally.Processes.Add(edge.SourceId);
            tally.Processes.Add(edge.TargetId);
            tally.OneConnected += ordered ? edge.SourceConnected : edge.TargetConnected;
            tally.OtherConnected += ordered ? edge.TargetConnected : edge.SourceConnected;
            tally.Ports.UnionWith(edge.AcceptedPorts);
        }

        return [.. pairs.Values
            .Select(tally => tally.Pair())
            .OrderByDescending(pair => pair.Records)
            .ThenByDescending(pair => pair.Relationships)
            .ThenBy(pair => pair.First.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Second.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.First.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Second.Key, StringComparer.Ordinal)];

        ProcessGroup Of(ProcessInstanceId process, CommunicationEdge edge) => groupOf.TryGetValue(process, out ProcessGroup? group)
            ? group
            : throw new ArgumentException($"Relationship {edge.Key} names process {process}, which no group holds.", nameof(edges));
    }

    private sealed class Tally(ProcessGroup one, ProcessGroup other)
    {
        public int Relationships { get; set; }

        public long Records { get; set; }

        public SortedSet<Mechanism> Mechanisms { get; } = [];

        public HashSet<ProcessInstanceId> Processes { get; } = [];

        public SortedSet<ushort> Ports { get; } = [];

        public long OneConnected { get; set; }

        public long OtherConnected { get; set; }

        /// <summary>
        /// The pair, the program that made more of its connections first, else the first by name: where one program is at
        /// both ends, every connection is its own.
        /// </summary>
        public ProgramPair Pair()
        {
            if (one.Key == other.Key)
            {
                return new(one, one, Relationships, Processes.Count, Records, [.. Mechanisms], OneConnected + OtherConnected, 0)
                {
                    AcceptedPorts = [.. Ports],
                };
            }

            bool swap = OtherConnected > OneConnected
                || (OtherConnected == OneConnected && string.Compare(other.Name, one.Name, StringComparison.OrdinalIgnoreCase) < 0);
            return (swap
                ? new ProgramPair(other, one, Relationships, Processes.Count, Records, [.. Mechanisms], OtherConnected, OneConnected)
                : new ProgramPair(one, other, Relationships, Processes.Count, Records, [.. Mechanisms], OneConnected, OtherConnected))
                with { AcceptedPorts = [.. Ports] };
        }
    }
}
