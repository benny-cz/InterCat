using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One caller's completed calls to one interface, by the process whose server calls served them, or by none
/// (`contracts/waits-v1.md` §2): how many, how many failed, the time inside them and its spread, and, over the calls
/// linked to a server call that completed, the time inside them beside the time the server's calls took.
/// </summary>
public sealed record CallWait(ProcessInstanceId Caller, Guid? Interface, ProcessInstanceId? Server)
{
    /// <summary>Its calls whose span is validated: started and stopped, paired by activity id on the caller.</summary>
    public required long Calls { get; init; }

    /// <summary>Of them, those whose stop reported a status other than 0.</summary>
    public required long Failed { get; init; }

    /// <summary>The time inside them: the sum of their spans, in session nanoseconds.</summary>
    public required long InCalls { get; init; }

    /// <summary>How their spans spread, by nearest rank.</summary>
    public required RpcCallDurations Durations { get; init; }

    /// <summary>Of them, those linked to a server call that also completed, whose two spans are both measured.</summary>
    public long Linked { get; init; }

    /// <summary>The time inside the linked calls, on the caller.</summary>
    public long LinkedInCalls { get; init; }

    /// <summary>The time the linked calls' server calls took, on the server.</summary>
    public long ServerRan { get; init; }

    /// <summary>Of calls no server is named for, those whose other end is unresolved, by why.</summary>
    public IReadOnlyDictionary<RpcPeerState, long> Unresolved { get; init; } = new Dictionary<RpcPeerState, long>();

    /// <summary>Of calls no server is named for, those served by a process the evidence policy does not admit.</summary>
    public long ServerNotAdmitted { get; init; }
}

/// <summary>One server's calls served, over every caller: how many callers, calls and how long their calls took on both sides.</summary>
public sealed record ServerWait(ProcessInstanceId Server)
{
    public required int Callers { get; init; }

    public required long Calls { get; init; }

    public required long InCalls { get; init; }

    public required long Linked { get; init; }

    public required long LinkedInCalls { get; init; }

    public required long ServerRan { get; init; }
}

/// <summary>
/// One generation's waits (`contracts/waits-v1.md`): every validated client call's span, grouped by caller, interface and
/// server, the most time first, and what no time is said for.
/// </summary>
public sealed record WaitReading(
    Guid SessionId,
    long Generation,
    EvidencePolicy Policy,
    IReadOnlyList<MechanismCoverage> Coverage,
    IReadOnlyList<CallWait> Waits)
{
    /// <summary>Every process of the generation, by its instance, as the overview names it.</summary>
    public required IReadOnlyDictionary<ProcessInstanceId, ProcessNode> Processes { get; init; }

    /// <summary>Client calls whose span is not validated, by their state: no time is said for them.</summary>
    public required IReadOnlyDictionary<RpcCallState, long> NotValidated { get; init; }

    /// <summary>Client calls whose caller the evidence policy does not admit, or binds to no process.</summary>
    public required long NotAdmitted { get; init; }

    /// <summary>The interval the waits were read over, in presentation ticks; none for the whole generation.</summary>
    public TimeRange? Interval { get; init; }

    /// <summary>Client calls that began outside <see cref="Interval"/>: no time is said for them there.</summary>
    public long OutsideInterval { get; init; }
}

/// <summary>
/// Bottleneck views (plan M12, `contracts/waits-v1.md`): the time each process spent inside the calls it made, from each
/// call's start to its stop on the caller, paired by activity id (`operations-v1` §3), by interface and by the process
/// that served them through ALPC (§5c), with that server's own time where its call completed too. A wait is an
/// observation: calls a process made at once overlap, and a call can be asynchronous, so no wait is said to have blocked
/// or delayed anything, and no path through them is called critical.
/// </summary>
public static class CallWaits
{
    /// <summary>How many times a reading starts again when the session publishes a newer generation while it reads.</summary>
    private const int Attempts = 3;

    /// <summary>
    /// Reads the waits of <paramref name="store"/>'s current generation under <paramref name="policy"/>: its overview naming its
    /// processes, then, under one lease of that generation, its calls and their other ends, read again where a newer
    /// generation was published between them. Within <paramref name="interval"/>, in presentation ticks, only the calls that
    /// began in it are read, each timed to its stop; those that began outside it are counted apart.
    /// </summary>
    public static WaitReading Read(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        TimeRange? interval = null,
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
                throw new InvalidOperationException("This directory began holding another session while its waits were read.");
            }

            if (manifest.Generation != overview.Generation)
            {
                if (attempt + 1 < Attempts) continue;
                throw new InvalidOperationException(
                    "The session published a newer generation each time its waits were read; read them again.");
            }

            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidDataException("This generation names no source clock, so no call has a time.");
            RpcPeerIndex peers = new GenerationSegments(store, manifest, clock).RpcPeers(cancellationToken);
            IReadOnlyList<MechanismCoverage> coverage = SessionCoverage.ForMechanisms(
                SessionSegments.CoverageLedger(store.Root, manifest), [Mechanism.Rpc, Mechanism.Alpc]);
            return Of(overview, peers, coverage, interval, cancellationToken);
        }
    }

    /// <summary>One generation's waits from its overview and the other ends of its calls, over an interval of it or all of it.</summary>
    internal static WaitReading Of(
        SessionOverviewBundle overview,
        RpcPeerIndex peers,
        IReadOnlyList<MechanismCoverage> coverage,
        TimeRange? interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(peers);
        EvidencePolicy policy = overview.Policy;
        RpcCallIndex calls = peers.Calls;
        IReadOnlyList<ProcessInstance> instances = calls.Processes.Instances;
        var spans = new Dictionary<RpcCallGroup, RpcCallSpan[]>(ReferenceEqualityComparer.Instance);
        var tallies = new Dictionary<(ProcessInstanceId Caller, Guid? Interface, ProcessInstanceId? Server), Tally>();
        var notValidated = new Dictionary<RpcCallState, long>();
        long notAdmitted = 0, outside = 0;
        foreach (RpcCallGroup group in calls.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Side != RpcCallSide.Client) continue;
            bool admitted = group.Process.IsBound && group.Process.IsAdmittedUnder(policy);
            if (!admitted && interval is null)
            {
                notAdmitted += group.Counts.Calls;
                continue;
            }

            // Within an interval, a call is read where it began: its start, or its stop where no start was recorded. One that
            // began outside it is counted apart, and nothing else of it.
            ProcessInstanceId? caller = admitted ? instances[group.Process.Instance].Id : null;
            RpcCallSpan[] own = SpansOf(group);
            var unlinked = new List<int>();
            for (int position = 0; position < own.Length; position++)
            {
                RpcCallSpan span = own[position];
                if (interval is { } within && (span.StartNanoseconds ?? span.StopNanoseconds) is { } placed
                    && !within.Contains(placed / 100))
                {
                    outside++;
                    continue;
                }

                if (caller is not { } made)
                {
                    notAdmitted++;
                    continue;
                }

                if (span.State != RpcCallState.Completed || span.StartNanoseconds is not { } start || span.StopNanoseconds is not { } stop)
                {
                    notValidated[span.State] = notValidated.GetValueOrDefault(span.State) + 1;
                    continue;
                }

                long took = stop - start;
                bool failed = span.Status is { } status && status != 0;
                (RpcCallGroup Group, int Position)? other = peers.OtherCallOf(group, position);
                if (other is { } served && served.Group.Process.IsBound && served.Group.Process.IsAdmittedUnder(policy))
                {
                    Tally tally = TallyOf(made, group.Interface, instances[served.Group.Process.Instance].Id);
                    tally.Add(took, failed);
                    RpcCallSpan ran = SpansOf(served.Group)[served.Position];
                    if (ran.State == RpcCallState.Completed && ran.StartNanoseconds is { } began && ran.StopNanoseconds is { } ended)
                    {
                        tally.Link(took, ended - began);
                    }

                    continue;
                }

                Tally none = TallyOf(made, group.Interface, null);
                none.Add(took, failed);
                if (other is not null)
                {
                    none.ServerNotAdmitted++;
                }
                else
                {
                    unlinked.Add(position);
                }
            }

            if (unlinked.Count > 0 && caller is { } unserved)
            {
                Tally none = TallyOf(unserved, group.Interface, null);
                foreach ((RpcPeerState state, long count) in peers.CountsOf(group, unlinked).Unresolved)
                {
                    none.Unresolved[state] = none.Unresolved.GetValueOrDefault(state) + count;
                }
            }
        }

        Dictionary<ProcessInstanceId, ProcessNode> processes = overview.Nodes.ToDictionary(node => node.Id);
        return new(overview.SessionId, overview.Generation, policy, coverage,
            [
                .. tallies
                    .Select(entry => entry.Value.Wait(entry.Key.Caller, entry.Key.Interface, entry.Key.Server))
                    .OrderByDescending(wait => wait.InCalls)
                    .ThenByDescending(wait => wait.Calls)
                    .ThenBy(wait => Name(processes, wait.Caller), StringComparer.Ordinal)
                    .ThenBy(wait => wait.Interface?.ToString() ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(wait => wait.Server is { } server ? Name(processes, server) : "￿", StringComparer.Ordinal),
            ])
        {
            Processes = processes,
            NotValidated = notValidated,
            NotAdmitted = notAdmitted,
            Interval = interval,
            OutsideInterval = outside,
        };

        RpcCallSpan[] SpansOf(RpcCallGroup group)
        {
            if (!spans.TryGetValue(group, out RpcCallSpan[]? held))
            {
                spans[group] = held = [.. calls.SpansOf(group)];
            }

            return held;
        }

        Tally TallyOf(ProcessInstanceId caller, Guid? rpcInterface, ProcessInstanceId? server)
        {
            var key = (caller, rpcInterface, server);
            if (!tallies.TryGetValue(key, out Tally? tally))
            {
                tallies[key] = tally = new Tally();
            }

            return tally;
        }
    }

    /// <summary>
    /// The servers callers waited on, each over every caller: the most time inside their calls first, then the most calls,
    /// then by name.
    /// </summary>
    public static IReadOnlyList<ServerWait> Servers(WaitReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return
        [
            .. reading.Waits
                .Where(wait => wait.Server is not null)
                .GroupBy(wait => wait.Server!.Value)
                .Select(served => new ServerWait(served.Key)
                {
                    Callers = served.Select(wait => wait.Caller).Distinct().Count(),
                    Calls = served.Sum(wait => wait.Calls),
                    InCalls = served.Sum(wait => wait.InCalls),
                    Linked = served.Sum(wait => wait.Linked),
                    LinkedInCalls = served.Sum(wait => wait.LinkedInCalls),
                    ServerRan = served.Sum(wait => wait.ServerRan),
                })
                .OrderByDescending(server => server.InCalls)
                .ThenByDescending(server => server.Calls)
                .ThenBy(server => Name(reading.Processes, server.Server), StringComparer.Ordinal),
        ];
    }

    private static string Name(IReadOnlyDictionary<ProcessInstanceId, ProcessNode> processes, ProcessInstanceId process) =>
        (processes.TryGetValue(process, out ProcessNode? node) ? node.NameWithPid : string.Empty) + "\n" + process;

    /// <summary>One caller's calls to one interface and server as a reading counts them.</summary>
    private sealed class Tally
    {
        private readonly List<long> durations = [];
        private long failed;
        private long linked;
        private long linkedInCalls;
        private long serverRan;

        public Dictionary<RpcPeerState, long> Unresolved { get; } = [];

        public long ServerNotAdmitted { get; set; }

        public void Add(long took, bool fails)
        {
            durations.Add(took);
            if (fails) failed++;
        }

        public void Link(long took, long ran)
        {
            linked++;
            linkedInCalls += took;
            serverRan += ran;
        }

        public CallWait Wait(ProcessInstanceId caller, Guid? rpcInterface, ProcessInstanceId? server) => new(caller, rpcInterface, server)
        {
            Calls = durations.Count,
            Failed = failed,
            InCalls = durations.Sum(),
            Durations = RpcCallDurations.Of(durations)!,
            Linked = linked,
            LinkedInCalls = linkedInCalls,
            ServerRan = serverRan,
            Unresolved = Unresolved,
            ServerNotAdmitted = ServerNotAdmitted,
        };
    }
}
