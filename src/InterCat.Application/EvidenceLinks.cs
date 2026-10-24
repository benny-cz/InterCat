using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How one end led to another, by one documented rule (`contracts/links-v1.md` §2).</summary>
public enum LinkKind
{
    /// <summary>A process started another: the child's creation or rundown record names it (`entities-v1` §3).</summary>
    Started = 1,

    /// <summary>
    /// A process's client calls were served by another's server calls, each linked through the call's one ALPC message
    /// (`operations-v1` §5c).
    /// </summary>
    Called = 2,

    /// <summary>An end connected to another, which accepted: the capture saw the TCP connection open (`relations-v1` §5).</summary>
    Connected = 3,
}

/// <summary>
/// An end of a link: one of the capture's processes, another host by its address, or an end on this machine no record
/// holds.
/// </summary>
public sealed record LinkEnd(ProcessInstanceId? Process, string? Address)
{
    public static LinkEnd UnseenEnd { get; } = new(null, null);

    public static LinkEnd Of(ProcessInstanceId process) => new(process, null);

    public static LinkEnd Host(string address) => new(null, address);

    public bool Unseen => Process is null && Address is null;
}

/// <summary>
/// One link: <see cref="From"/> led to <see cref="To"/> by its kind's rule, as strong as the weakest evidence it rests on,
/// held <see cref="Occurrences"/> times - calls served, or connections opened - an RPC link by one interface and a
/// connection by the port it arrived at.
/// </summary>
public sealed record EvidenceLink(LinkKind Kind, LinkEnd From, LinkEnd To, RelationStrength Strength)
{
    public long Occurrences { get; init; } = 1;

    public Guid? Interface { get; init; }

    public ushort? Port { get; init; }
}

/// <summary>What of one process's activity links it to nothing, each counted by why (`contracts/links-v1.md` §4).</summary>
public sealed record UnlinkedActivity
{
    public static UnlinkedActivity None { get; } = new();

    /// <summary>Its TCP connections whose opening no record shows: neither end is known to have made them.</summary>
    public long UnopenedConnections { get; init; }

    /// <summary>Its UDP flows, which nothing opens.</summary>
    public long Flows { get; init; }

    /// <summary>Its client calls whose other end is unresolved, by reason.</summary>
    public IReadOnlyDictionary<RpcPeerState, long> UnresolvedCalls { get; init; } = new Dictionary<RpcPeerState, long>();

    /// <summary>The calls it served that no linked client call reached.</summary>
    public long CallsNotReached { get; init; }

    /// <summary>The parent PID its creation names where no process of the capture is linked as that parent.</summary>
    public int? UnheldParent { get; init; }

    public bool IsEmpty => UnopenedConnections == 0 && Flows == 0 && UnresolvedCalls.Count == 0 && CallsNotReached == 0
        && UnheldParent is null;
}

/// <summary>
/// One generation's links (`contracts/links-v1.md`): every process it started, every call it made that another served and
/// every connection the capture saw it make or accept, whatever their strength, and what of each process links to
/// nothing; an evidence policy then says which links a view follows.
/// </summary>
public sealed record LinkReading(
    Guid SessionId,
    long Generation,
    EvidencePolicy Policy,
    IReadOnlyList<MechanismCoverage> Coverage,
    IReadOnlyList<EvidenceLink> Links)
{
    /// <summary>Every process of the generation, by its instance, as the overview names it.</summary>
    public required IReadOnlyDictionary<ProcessInstanceId, ProcessNode> Processes { get; init; }

    /// <summary>When each process's creation was witnessed, in presentation ticks; absent where none was.</summary>
    public required IReadOnlyDictionary<ProcessInstanceId, long> Created { get; init; }

    /// <summary>What of each process's activity links it to nothing; absent where nothing of it is unlinked.</summary>
    public required IReadOnlyDictionary<ProcessInstanceId, UnlinkedActivity> Unlinked { get; init; }

    /// <summary>Whether the evidence policy follows <paramref name="link"/>: whether it admits its strength.</summary>
    public bool Follows(EvidenceLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return SessionOverviewProjector.Admitted(link.Strength, Policy);
    }
}

/// <summary>A process created near another and linked to it by nothing: <see cref="OffsetTicks"/> after it, before where negative.</summary>
public sealed record NearProcess(ProcessInstanceId Process, long OffsetTicks);

/// <summary>
/// One process's links (`contracts/links-v1.md` §3): those the policy follows that led to it and that it led to, those
/// it does not follow, the processes created near it that nothing links to it, and what of it links to nothing.
/// </summary>
public sealed record StepLinks(ProcessInstanceId Step)
{
    public required IReadOnlyList<EvidenceLink> LedToIt { get; init; }

    public required IReadOnlyList<EvidenceLink> ItLedTo { get; init; }

    public required IReadOnlyList<EvidenceLink> NotFollowed { get; init; }

    /// <summary>Whether its creation was witnessed, which nearness in time is measured from.</summary>
    public required bool CreationWitnessed { get; init; }

    public required IReadOnlyList<NearProcess> Near { get; init; }

    public required UnlinkedActivity Unlinked { get; init; }
}

/// <summary>
/// Causal navigation (plan M12, `contracts/links-v1.md`): the links a generation's evidence records between its processes,
/// other hosts and ends no record holds - a process started another, a process's calls were served by another, an end
/// connected to another - each by its documented rule and strength. A view follows only the links its evidence policy
/// admits, step by step or as a chain; processes created near one another in time are listed apart and never followed,
/// since nearness is never a link (P8).
/// </summary>
public static class EvidenceLinks
{
    /// <summary>The rule a reading's links are made by.</summary>
    public const string Rule = "evidence-links-v1";

    /// <summary>How near in time two creations are listed as near: a second, in presentation ticks.</summary>
    public const long NearTicks = 10_000_000;

    /// <summary>How many times a reading starts again when the session publishes a newer generation while it reads.</summary>
    private const int Attempts = 3;

    /// <summary>
    /// Reads the links of <paramref name="store"/>'s current generation under <paramref name="policy"/>: its overview naming
    /// its processes, then, under one lease of that generation, its processes, relations, calls and their other ends,
    /// read again where a newer generation was published between them.
    /// </summary>
    public static LinkReading Read(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        for (int attempt = 0; ; attempt++)
        {
            SessionOverviewBundle overview = SessionOverviewProjector.Project(store, policy, cancellationToken: cancellationToken);
            using EvidenceLease lease = store.AcquireLease();
            SessionManifestV1 manifest = lease.Manifest;
            if (manifest.SessionId != overview.SessionId)
            {
                throw new InvalidOperationException("This directory began holding another session while its links were read.");
            }

            if (manifest.Generation != overview.Generation)
            {
                if (attempt + 1 < Attempts) continue;
                throw new InvalidOperationException(
                    "The session published a newer generation each time its links were read; read them again.");
            }

            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidDataException("This generation names no source clock, so no record has a time.");
            var generation = new GenerationSegments(store, manifest, clock);
            ProcessInstanceIndex processes = generation.Processes(cancellationToken);
            TransportRelationIndex relations = generation.Relations(cancellationToken);
            RpcPeerIndex peers = generation.RpcPeers(cancellationToken);
            IReadOnlyList<MechanismCoverage> coverage = SessionCoverage.ForMechanisms(
                SessionSegments.CoverageLedger(store.Root, manifest),
                [Mechanism.ProcessLifecycle, Mechanism.Tcp, Mechanism.Rpc, Mechanism.Alpc]);
            return Of(overview, processes, relations, peers, clock, coverage, cancellationToken);
        }
    }

    /// <summary>
    /// One generation's links from its overview, processes, relations and the other ends of its calls: each link once, by
    /// its kind, ends, interface or port and strength, with how many times the evidence holds it.
    /// </summary>
    internal static LinkReading Of(
        SessionOverviewBundle overview,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        RpcPeerIndex peers,
        SourceClockDescriptor clock,
        IReadOnlyList<MechanismCoverage> coverage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(peers);
        var links = new Dictionary<(LinkKind Kind, LinkEnd From, LinkEnd To, Guid? Interface, ushort? Port, RelationStrength Strength), long>();
        var unlinked = new Dictionary<ProcessInstanceId, Unlinked>();

        // A process started another where the child's record names it: by its start key, directly, or by its PID alone.
        foreach (ProcessNode node in overview.Nodes)
        {
            if (node.Parent is { } parent && node.ParentBinding is { } binding)
            {
                Add(LinkKind.Started, LinkEnd.Of(parent), LinkEnd.Of(node.Id), null, null, binding, 1);
            }
            else if (node.ParentProcessId is { } named)
            {
                UnlinkedOf(node.Id).UnheldParent = named;
            }
        }

        // A client call served by a server call, as strong as the weaker of the two calls' bindings and correlated at best.
        IReadOnlyList<ProcessInstance> instances = peers.Calls.Processes.Instances;
        foreach (RpcCallGroup group in peers.Calls.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!group.Process.IsBound) continue;
            ProcessInstanceId process = instances[group.Process.Instance].Id;
            RpcPeerCounts counts = peers.CountsOf(group);
            if (group.Side == RpcCallSide.Server)
            {
                if (counts.Calls > counts.Served) UnlinkedOf(process).CallsNotReached += counts.Calls - counts.Served;
                continue;
            }

            foreach ((RpcPeerState state, long calls) in counts.Unresolved)
            {
                Dictionary<RpcPeerState, long> unresolved = UnlinkedOf(process).UnresolvedCalls;
                unresolved[state] = unresolved.GetValueOrDefault(state) + calls;
            }

            foreach (RpcPeerTally served in peers.PeersOf(group))
            {
                if (!served.Process.IsBound) continue;
                Add(LinkKind.Called, LinkEnd.Of(process), LinkEnd.Of(instances[served.Process.Instance].Id), served.Interface,
                    null, RpcPeerEdges.StrengthOf(group.Process, served.Process), served.Calls);
            }
        }

        // A TCP connection between two of the capture's processes, from the end the capture saw connect to the end that
        // accepted; one seen open at neither end has no direction, and a UDP flow is opened by neither.
        foreach (TransportRelation relation in relations.Relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relation.Mechanism == Mechanism.Udp)
            {
                UnlinkedOf(relation.First.Id).Flows++;
                if (relation.Second.Id != relation.First.Id) UnlinkedOf(relation.Second.Id).Flows++;
                continue;
            }

            if (relation.Mechanism != Mechanism.Tcp) continue;
            if (ConnectionOpens.AcceptingPort(relation) is not { } port)
            {
                UnlinkedOf(relation.First.Id).UnopenedConnections++;
                if (relation.Second.Id != relation.First.Id) UnlinkedOf(relation.Second.Id).UnopenedConnections++;
                continue;
            }

            bool firstConnected = relation.Opening == RelationOpening.FirstConnected;
            Add(LinkKind.Connected, LinkEnd.Of((firstConnected ? relation.First : relation.Second).Id),
                LinkEnd.Of((firstConnected ? relation.Second : relation.First).Id), null, port, relation.Strength, 1);
        }

        // A connection whose other end no record holds: to or from another host, by its address, or an end on this machine.
        foreach (TransportConnection connection in relations.OneSided)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connection.Mechanism == Mechanism.Udp)
            {
                UnlinkedOf(connection.Holder.Id).Flows++;
                continue;
            }

            if (connection.Mechanism != Mechanism.Tcp) continue;
            string local = HostScopeKeys.EndpointAddress(connection.LocalEndpoint);
            string remote = HostScopeKeys.EndpointAddress(connection.RemoteEndpoint);
            LinkEnd other = CommunicationPatterns.OnThisMachine(local, remote) ? LinkEnd.UnseenEnd : LinkEnd.Host(remote);
            LinkEnd holder = LinkEnd.Of(connection.Holder.Id);
            if (connection.Opening == ConnectionOpening.Connected && ConnectionOpens.PortOf(connection.RemoteEndpoint) is { } reached)
            {
                Add(LinkKind.Connected, holder, other, null, reached, connection.Strength, 1);
            }
            else if (connection.Opening == ConnectionOpening.Accepted && ConnectionOpens.PortOf(connection.LocalEndpoint) is { } accepted)
            {
                Add(LinkKind.Connected, other, holder, null, accepted, connection.Strength, 1);
            }
            else
            {
                UnlinkedOf(connection.Holder.Id).UnopenedConnections++;
            }
        }

        var created = new Dictionary<ProcessInstanceId, long>();
        foreach (ProcessInstance instance in processes.Instances)
        {
            if (instance.CreatedNativeTicks is { } native)
            {
                created[instance.Id] = (long)(SourceClockMath.SessionNanoseconds(clock, native) / 100);
            }
        }

        return new(overview.SessionId, overview.Generation, overview.Policy, coverage,
            [.. links.Select(entry => new EvidenceLink(entry.Key.Kind, entry.Key.From, entry.Key.To, entry.Key.Strength)
            {
                Occurrences = entry.Value,
                Interface = entry.Key.Interface,
                Port = entry.Key.Port,
            })])
        {
            Processes = overview.Nodes.ToDictionary(node => node.Id),
            Created = created,
            Unlinked = unlinked.ToDictionary(entry => entry.Key, entry => entry.Value.Activity()),
        };

        void Add(LinkKind kind, LinkEnd from, LinkEnd to, Guid? rpcInterface, ushort? port, RelationStrength strength, long times)
        {
            var key = (kind, from, to, rpcInterface, port, strength);
            links[key] = links.GetValueOrDefault(key) + times;
        }

        Unlinked UnlinkedOf(ProcessInstanceId process)
        {
            if (!unlinked.TryGetValue(process, out Unlinked? tally))
            {
                unlinked[process] = tally = new Unlinked();
            }

            return tally;
        }
    }

    /// <summary>
    /// One process's links under the reading's policy (`contracts/links-v1.md` §3): those followed that led to it - its
    /// parent, its callers, what connected to it - and that it led to, each kind in turn, the most held first, then
    /// processes before hosts and the end no record holds, each by name; those not followed; and the processes whose
    /// creation was witnessed within <see cref="NearTicks"/> of its own and that no link, followed or not, joins to it, the
    /// nearest first.
    /// </summary>
    public static StepLinks Of(LinkReading reading, ProcessInstanceId step)
    {
        ArgumentNullException.ThrowIfNull(reading);
        EvidenceLink[] touching = [.. reading.Links.Where(link => link.From.Process == step || link.To.Process == step)
            .OrderBy(link => link.Kind)
            .ThenByDescending(link => link.Occurrences)
            .ThenBy(link => Key(reading, link.From.Process == step ? link.To : link.From), StringComparer.Ordinal)
            .ThenBy(link => link.Interface?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(link => link.Port)];
        var joined = new HashSet<ProcessInstanceId>(touching
            .Select(link => link.From.Process == step ? link.To.Process : link.From.Process)
            .OfType<ProcessInstanceId>());
        bool witnessed = reading.Created.TryGetValue(step, out long createdAt);
        return new StepLinks(step)
        {
            LedToIt = [.. touching.Where(link => reading.Follows(link) && link.To.Process == step && link.From.Process != step)],
            ItLedTo = [.. touching.Where(link => reading.Follows(link) && link.From.Process == step)],
            NotFollowed = [.. touching.Where(link => !reading.Follows(link))],
            CreationWitnessed = witnessed,
            Near = !witnessed ? [] :
            [
                .. reading.Created
                    .Where(other => other.Key != step && !joined.Contains(other.Key) && Math.Abs(other.Value - createdAt) <= NearTicks)
                    .Select(other => new NearProcess(other.Key, other.Value - createdAt))
                    .OrderBy(near => Math.Abs(near.OffsetTicks))
                    .ThenBy(near => near.Process.ToString(), StringComparer.Ordinal),
            ],
            Unlinked = reading.Unlinked.GetValueOrDefault(step) ?? UnlinkedActivity.None,
        };
    }

    /// <summary>
    /// The shortest chain of links from one process to another that the reading's policy follows - or, with
    /// <paramref name="everyLink"/>, that any link makes - each link from the process the previous one led to, through
    /// processes alone: another host or an end no record holds is an address, never a step, so nothing is chained through
    /// it. Null where none leads there. Among chains as short, the one whose links come first by kind and then by the
    /// processes they lead to.
    /// </summary>
    public static IReadOnlyList<EvidenceLink>? Chain(LinkReading reading, ProcessInstanceId from, ProcessInstanceId to, bool everyLink = false)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ILookup<ProcessInstanceId, EvidenceLink> onward = reading.Links
            .Where(link => link.From.Process is not null && link.To.Process is not null && link.From.Process != link.To.Process
                && (everyLink || reading.Follows(link)))
            .OrderBy(link => link.Kind)
            .ThenBy(link => link.To.Process!.Value.ToString(), StringComparer.Ordinal)
            .ThenBy(link => link.Strength)
            .ToLookup(link => link.From.Process!.Value);
        var reached = new Dictionary<ProcessInstanceId, EvidenceLink?> { [from] = null };
        var frontier = new Queue<ProcessInstanceId>([from]);
        while (frontier.Count > 0)
        {
            ProcessInstanceId at = frontier.Dequeue();
            if (at == to)
            {
                var chain = new List<EvidenceLink>();
                for (ProcessInstanceId back = to; reached[back] is { } link; back = link.From.Process!.Value)
                {
                    chain.Add(link);
                }

                chain.Reverse();
                return chain;
            }

            foreach (EvidenceLink link in onward[at])
            {
                ProcessInstanceId next = link.To.Process!.Value;
                if (reached.TryAdd(next, link)) frontier.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>The strengths <paramref name="policy"/> follows, the strongest first: those it admits.</summary>
    public static IReadOnlyList<RelationStrength> Followed(EvidencePolicy policy) =>
        [.. Enum.GetValues<RelationStrength>().Where(strength => SessionOverviewProjector.Admitted(strength, policy))];

    /// <summary>The narrowest evidence policy that follows a link of <paramref name="strength"/>.</summary>
    public static EvidencePolicy Following(RelationStrength strength) =>
        Enum.GetValues<EvidencePolicy>().First(policy => SessionOverviewProjector.Admitted(strength, policy));

    /// <summary>The weakest strength of a chain's links: what the chain as a whole rests on (ADR-055).</summary>
    public static RelationStrength Weakest(IReadOnlyList<EvidenceLink> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return chain.Count == 0 ? RelationStrength.Direct : chain.Max(link => link.Strength);
    }

    /// <summary>An end as links are ordered by it: a process by its name and then its instance, then hosts by address, then the unseen end.</summary>
    private static string Key(LinkReading reading, LinkEnd end) => end.Process is { } process
        ? "0" + (reading.Processes.TryGetValue(process, out ProcessNode? node) ? node.NameWithPid : string.Empty) + "\n" + process
        : end.Address is { } address ? "1" + address : "2";

    /// <summary>What of one process links it to nothing, as a reading counts it.</summary>
    private sealed class Unlinked
    {
        public long UnopenedConnections { get; set; }

        public long Flows { get; set; }

        public Dictionary<RpcPeerState, long> UnresolvedCalls { get; } = [];

        public long CallsNotReached { get; set; }

        public int? UnheldParent { get; set; }

        public UnlinkedActivity Activity() => new()
        {
            UnopenedConnections = UnopenedConnections,
            Flows = Flows,
            UnresolvedCalls = UnresolvedCalls,
            CallsNotReached = CallsNotReached,
            UnheldParent = UnheldParent,
        };
    }
}
