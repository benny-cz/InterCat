using System.Globalization;
using System.Net;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>Where a pattern's server end is (`contracts/patterns-v1.md` §2): a process of this machine, or another host.</summary>
public enum PatternPlace
{
    /// <summary>On this machine: a process of the capture, or an end on it no record holds.</summary>
    Here = 1,

    /// <summary>At another host, named by its address; who is at it is not observed (P7).</summary>
    Elsewhere = 2,
}

/// <summary>A participant's part in a pattern's occurrences: the end that made them, or the end that served them.</summary>
public enum PatternRole
{
    /// <summary>The end that connected, sent to a service port, or made an RPC call.</summary>
    Client = 1,

    /// <summary>The end that accepted, sat at the service port, or served the call.</summary>
    Server = 2,
}

/// <summary>
/// One participant of a pattern in one role: a process of the capture, another host by its address, or - both null - the
/// ends on this machine no record holds, counted together; with how many of the pattern's occurrences it took part in.
/// </summary>
public sealed record PatternParticipant(PatternRole Role, ProcessInstanceId? Process, string? Address, long Occurrences)
{
    /// <summary>Whether it stands for the ends on this machine no record of the capture holds.</summary>
    public bool Unseen => Process is null && Address is null;
}

/// <summary>
/// One recurring communication pattern (`contracts/patterns-v1.md`, plan M12): a structural signature - a mechanism, the
/// service port or the RPC interface and procedure, and where the server end is - with the occurrences that share it,
/// who took part in them in each role, and when they occurred.
/// </summary>
/// <param name="Key">The signature as a key: "tcp:here:8080", "udp:elsewhere:53", "rpc:{interface}:{procedure}".</param>
/// <param name="Mechanism">TCP, UDP or RPC.</param>
/// <param name="Place">Where the server end is; for RPC, here, the calls being this machine's.</param>
/// <param name="Port">The service port, of TCP and UDP.</param>
/// <param name="Interface">The interface the calls' starts named, of RPC.</param>
/// <param name="Procedure">The procedure number the calls' starts recorded, of RPC; null where none was recorded.</param>
public sealed record CommunicationPattern(
    string Key,
    Mechanism Mechanism,
    PatternPlace Place,
    ushort? Port,
    Guid? Interface,
    long? Procedure)
{
    /// <summary>
    /// How many times it occurred: TCP connections or UDP flows, or the RPC calls its clients made.
    /// </summary>
    public required long Occurrences { get; init; }

    /// <summary>Of RPC, how many of its calls the capture recorded served; none of TCP and UDP.</summary>
    public long Served { get; init; }

    /// <summary>What it is ranked by: its occurrences, or of RPC the more of its calls made and served.</summary>
    public long Weight => Math.Max(Occurrences, Served);

    /// <summary>The ends that made its occurrences, the most first.</summary>
    public required IReadOnlyList<PatternParticipant> Clients { get; init; }

    /// <summary>The ends that served them, the most first.</summary>
    public required IReadOnlyList<PatternParticipant> Servers { get; init; }

    /// <summary>When its first occurrence began, in 100-nanosecond presentation ticks; null where none had a session time.</summary>
    public long? FirstTicks { get; init; }

    /// <summary>When its last occurrence began, in presentation ticks; null where none had a session time.</summary>
    public long? LastTicks { get; init; }

    /// <summary>
    /// In how many of the session's ten equal tenths an occurrence began: how far through the session it recurred.
    /// </summary>
    public int Tenths { get; init; }

    /// <summary>Whether it occurred more than once: a recurring pattern.</summary>
    public bool Recurring => Weight > 1;
}

/// <summary>
/// A session's communication patterns (`contracts/patterns-v1.md`): every pattern of its current generation, the heaviest
/// first, with what no pattern holds counted apart - so a connection or call missing from them is never read as one that
/// did not happen.
/// </summary>
public sealed record PatternReading(
    Guid SessionId,
    long Generation,
    EvidencePolicy Policy,
    TimeRange? Span,
    IReadOnlyList<MechanismCoverage> Coverage,
    IReadOnlyList<CommunicationPattern> Patterns)
{
    /// <summary>The process instances the overview identified, by identity, which name each participant.</summary>
    public required IReadOnlyDictionary<ProcessInstanceId, ProcessNode> Processes { get; init; }

    /// <summary>The executables the overview groups processes by, by key, which count each role's programs.</summary>
    public required IReadOnlyDictionary<string, ProcessGroup> Groups { get; init; }

    /// <summary>TCP connections the capture saw open at neither end, or at both: no end of them has a known role.</summary>
    public long UnopenedConnections { get; init; }

    /// <summary>UDP flows between two dynamic ports, which have no service port to group them by.</summary>
    public long DynamicFlows { get; init; }

    /// <summary>RPC calls no recorded start named an interface for, so no pattern holds them.</summary>
    public long CallsWithoutInterface { get; init; }

    /// <summary>Connections, flows and calls the evidence policy did not admit.</summary>
    public long NotAdmitted { get; init; }
}

/// <summary>
/// Recurring communication patterns (`contracts/patterns-v1.md`, plan M12's explainable pattern groups): each TCP connection
/// the capture saw open, each UDP flow and each RPC call of a generation, grouped by a structural signature - its
/// mechanism, the service port or the interface and procedure, and where the server end is - each end in its role. A
/// signature names no process, address or time, so a pattern groups what recurs and its participants say who took part:
/// a grouping, never an identity (P6).
/// </summary>
public static class CommunicationPatterns
{
    /// <summary>The first port of the dynamic range (RFC 6335), which a client takes for itself and a service never listens at.</summary>
    public const ushort FirstDynamicPort = 49152;

    /// <summary>How many equal parts of the session a pattern's spread counts.</summary>
    public const int Parts = 10;

    private const int Attempts = 3;

    /// <summary>
    /// Reads every pattern of <paramref name="store"/>'s current generation under <paramref name="policy"/>: its overview,
    /// its connections and its calls, all of one generation, read again where a newer one was published meanwhile.
    /// </summary>
    public static PatternReading Read(
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
                throw new InvalidOperationException("This directory began holding another session while its patterns were read.");
            }

            if (manifest.Generation != overview.Generation)
            {
                if (attempt + 1 < Attempts) continue;
                throw new InvalidOperationException(
                    "The session published a newer generation each time its patterns were read; read them again.");
            }

            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidDataException("This generation names no source clock, so no occurrence has a time.");
            var generation = new GenerationSegments(store, manifest, clock);
            TransportRelationIndex relations = generation.Relations(cancellationToken);
            RpcCallIndex calls = generation.RpcCalls(cancellationToken);
            IReadOnlyList<MechanismCoverage> coverage = SessionCoverage.ForMechanisms(
                SessionSegments.CoverageLedger(store.Root, manifest), [Mechanism.Tcp, Mechanism.Udp, Mechanism.Rpc]);
            return Of(overview, relations, calls, clock, coverage, cancellationToken);
        }
    }

    /// <summary>A pattern's signature in words: "TCP at port 8080 here", "UDP to port 53 at other hosts", "RPC calls to …".</summary>
    public static string Describe(CommunicationPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Mechanism == Mechanism.Rpc)
        {
            return "RPC calls to " + RpcInterfaceNames.Describe(pattern.Interface) + ", "
                + (pattern.Procedure is { } procedure
                    ? "procedure " + procedure.ToString(CultureInfo.InvariantCulture)
                    : "no procedure recorded");
        }

        string port = pattern.Port!.Value.ToString(CultureInfo.InvariantCulture);
        string transport = MechanismText.Name(pattern.Mechanism);
        return pattern.Place == PatternPlace.Here ? $"{transport} at port {port} here" : $"{transport} to port {port} at other hosts";
    }

    /// <summary>
    /// What a pattern's signature is made of and what it leaves out, in words, so the grouping is never a black box: the
    /// fields that make it, the source-specific normalization, and how each end's role was told.
    /// </summary>
    public static string Explain(CommunicationPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Mechanism switch
        {
            Mechanism.Tcp when pattern.Place == PatternPlace.Here =>
                "TCP connections a process of this machine accepted at this port, whoever connected: grouped by the port they "
                + "arrived at. The connecting end's own port, which it chose itself, and every address are not part of it; the "
                + "end the capture saw accept a connection is its server, the end that connected its client.",
            Mechanism.Tcp =>
                "TCP connections this machine's processes opened to this port at other hosts, whichever host: grouped by the "
                + "port they ran to. Each process's own port, which it chose itself, and the hosts' addresses are not part of "
                + "it; who is at an address is not observed.",
            Mechanism.Udp =>
                "UDP flows whose service port this is: of a flow's two ports, the one outside the dynamic range "
                + $"{FirstDynamicPort.ToString(CultureInfo.InvariantCulture)} to 65535, the lower where both are. The end at it "
                + "is the flow's server and the other its client, by port alone, since nothing opens a UDP flow; the other "
                + "port and the addresses are not part of it.",
            _ =>
                "RPC calls whose starts named this interface and procedure, made and served: who made a call is a client and "
                + "who served one a server. A call's activity id, protocol sequence and process are not part of it.",
        };
    }

    /// <summary>Whether a port is in the dynamic range, which a client takes for itself (RFC 6335).</summary>
    public static bool IsDynamic(ushort port) => port >= FirstDynamicPort;

    /// <summary>
    /// The patterns of one generation's relations and calls, its overview naming their processes and its span placing
    /// their occurrences: the heaviest first, then by key.
    /// </summary>
    internal static PatternReading Of(
        SessionOverviewBundle overview,
        TransportRelationIndex relations,
        RpcCallIndex calls,
        SourceClockDescriptor clock,
        IReadOnlyList<MechanismCoverage> coverage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(calls);
        EvidencePolicy policy = overview.Policy;
        TimeRange? span = overview.Recording ?? overview.Extent;
        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        long unopened = 0, dynamic = 0, unnamed = 0, notAdmitted = 0;
        long Ticks(long nativeTicks) => (long)(SourceClockMath.SessionNanoseconds(clock, nativeTicks) / 100);

        foreach (TransportRelation relation in relations.Relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relation.Mechanism is not (Mechanism.Tcp or Mechanism.Udp)) continue;
            if (!SessionOverviewProjector.Admitted(relation.Strength, policy))
            {
                notAdmitted++;
                continue;
            }

            if (relation.Mechanism == Mechanism.Tcp)
            {
                // Its server is the end the capture saw accept it; one it saw open at neither end, or both, has no roles.
                if (ConnectionOpens.AcceptingPort(relation) is not { } port)
                {
                    unopened++;
                    continue;
                }

                bool firstConnected = relation.Opening == RelationOpening.FirstConnected;
                TallyOf(Mechanism.Tcp, PatternPlace.Here, port)
                    .Occur(Ticks(relation.FirstNativeTicks), Participant.Of(firstConnected ? relation.First : relation.Second),
                        Participant.Of(firstConnected ? relation.Second : relation.First));
                continue;
            }

            // A UDP flow between two of this machine's processes: the end at its service port serves it.
            if (ServicePort(ConnectionOpens.PortOf(relation.FirstEndpoint), ConnectionOpens.PortOf(relation.SecondEndpoint)) is not
                { } flow)
            {
                dynamic++;
                continue;
            }

            TallyOf(Mechanism.Udp, PatternPlace.Here, flow.Port).Occur(Ticks(relation.FirstNativeTicks),
                Participant.Of(flow.FirstServes ? relation.Second : relation.First),
                Participant.Of(flow.FirstServes ? relation.First : relation.Second));
        }

        foreach (TransportConnection connection in relations.OneSided)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connection.Mechanism is not (Mechanism.Tcp or Mechanism.Udp)) continue;
            if (!SessionOverviewProjector.Admitted(connection.Strength, policy))
            {
                notAdmitted++;
                continue;
            }

            // The other end no record holds is another host by its address, or an end on this machine.
            string local = HostScopeKeys.EndpointAddress(connection.LocalEndpoint);
            string remote = HostScopeKeys.EndpointAddress(connection.RemoteEndpoint);
            Participant other = OnThisMachine(local, remote) ? Participant.UnseenEnd : Participant.Host(remote);
            Participant holder = Participant.Of(connection.Holder);
            ushort? localPort = ConnectionOpens.PortOf(connection.LocalEndpoint);
            ushort? remotePort = ConnectionOpens.PortOf(connection.RemoteEndpoint);
            long at = Ticks(connection.FirstNativeTicks);
            if (connection.Mechanism == Mechanism.Tcp)
            {
                if (connection.Opening == ConnectionOpening.Accepted && localPort is { } accepted)
                {
                    TallyOf(Mechanism.Tcp, PatternPlace.Here, accepted).Occur(at, other, holder);
                }
                else if (connection.Opening == ConnectionOpening.Connected && remotePort is { } connected)
                {
                    TallyOf(Mechanism.Tcp, other.Address is null ? PatternPlace.Here : PatternPlace.Elsewhere, connected)
                        .Occur(at, holder, other);
                }
                else
                {
                    unopened++;
                }

                continue;
            }

            if (ServicePort(localPort, remotePort) is not { } served)
            {
                dynamic++;
                continue;
            }

            if (served.FirstServes)
            {
                TallyOf(Mechanism.Udp, PatternPlace.Here, served.Port).Occur(at, other, holder);
            }
            else
            {
                TallyOf(Mechanism.Udp, other.Address is null ? PatternPlace.Here : PatternPlace.Elsewhere, served.Port)
                    .Occur(at, holder, other);
            }
        }

        foreach (RpcCallGroup group in calls.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!group.Process.IsAdmittedUnder(policy))
            {
                notAdmitted += group.Counts.Calls;
                continue;
            }

            // A call is placed where it began: its start, or its stop where no start was recorded, which names no interface.
            Participant process = Participant.Of(calls.Processes.Instances[group.Process.Instance]);
            foreach (RpcCallSpan call in calls.SpansOf(group))
            {
                if (call.StartNanoseconds is not { } started || group.Interface is not { } rpcInterface)
                {
                    unnamed++;
                    continue;
                }

                Tally tally = TallyOf(Mechanism.Rpc, PatternPlace.Here, null, rpcInterface, call.Procedure);
                if (group.Side == RpcCallSide.Client) tally.Occur(started / 100, process, null);
                else tally.Serve(started / 100, process);
            }
        }

        CommunicationPattern[] patterns = [.. tallies.Values
            .Select(tally => tally.Pattern(span))
            .OrderByDescending(pattern => pattern.Weight)
            .ThenBy(pattern => pattern.Key, StringComparer.Ordinal)];
        return new(overview.SessionId, overview.Generation, policy, span, coverage, patterns)
        {
            Processes = overview.Nodes.ToDictionary(node => node.Id),
            Groups = overview.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal),
            UnopenedConnections = unopened,
            DynamicFlows = dynamic,
            CallsWithoutInterface = unnamed,
            NotAdmitted = notAdmitted,
        };

        Tally TallyOf(Mechanism mechanism, PatternPlace place, ushort? port, Guid? rpcInterface = null, long? procedure = null)
        {
            string key = mechanism == Mechanism.Rpc
                ? string.Create(CultureInfo.InvariantCulture,
                    $"rpc:{rpcInterface!.Value:D}:{(procedure is { } number ? number.ToString(CultureInfo.InvariantCulture) : "none")}")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{MechanismText.Name(mechanism).ToLowerInvariant()}:{(place == PatternPlace.Here ? "here" : "elsewhere")}:{port}");
            if (!tallies.TryGetValue(key, out Tally? tally))
            {
                tally = new Tally(new CommunicationPattern(key, mechanism, place, port, rpcInterface, procedure)
                {
                    Occurrences = 0,
                    Clients = [],
                    Servers = [],
                });
                tallies[key] = tally;
            }

            return tally;
        }
    }

    /// <summary>
    /// A flow's service port and whether its first end is at it: the port outside the dynamic range, the lower where both
    /// are - the first end's where they are one; null where both are dynamic or either is unknown.
    /// </summary>
    internal static (ushort Port, bool FirstServes)? ServicePort(ushort? first, ushort? second)
    {
        if (first is not { } one || second is not { } other) return null;
        return (IsDynamic(one), IsDynamic(other)) switch
        {
            (false, true) => (one, true),
            (true, false) => (other, false),
            (false, false) => one <= other ? (one, true) : (other, false),
            _ => null,
        };
    }

    /// <summary>Whether the other end of a one-sided connection is on this machine: at its own address, or at loopback's.</summary>
    private static bool OnThisMachine(string localAddress, string remoteAddress) =>
        string.Equals(localAddress, remoteAddress, StringComparison.Ordinal)
        || (IPAddress.TryParse(remoteAddress, out IPAddress? remote)
            && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote));

    /// <summary>An end as a pattern counts it: a process of the capture, another host by address, or an unseen end here.</summary>
    private readonly record struct Participant(ProcessInstanceId? Process, string? Address)
    {
        public static Participant UnseenEnd => default;

        public static Participant Of(ProcessInstance process) => new(process.Id, null);

        public static Participant Host(string address) => new(null, address);
    }

    /// <summary>One pattern's occurrences as they are read: each end's count in each role, and when they began.</summary>
    private sealed class Tally(CommunicationPattern signature)
    {
        private readonly Dictionary<Participant, long> clients = [];
        private readonly Dictionary<Participant, long> servers = [];
        private readonly List<long> times = [];
        private long occurrences;
        private long served;

        public void Occur(long at, Participant client, Participant? server)
        {
            occurrences++;
            clients[client] = clients.GetValueOrDefault(client) + 1;
            if (server is { } serving) servers[serving] = servers.GetValueOrDefault(serving) + 1;
            times.Add(at);
        }

        public void Serve(long at, Participant server)
        {
            served++;
            servers[server] = servers.GetValueOrDefault(server) + 1;
            times.Add(at);
        }

        public CommunicationPattern Pattern(TimeRange? span) => signature with
        {
            Occurrences = occurrences,
            Served = served,
            Clients = Listed(clients, PatternRole.Client),
            Servers = Listed(servers, PatternRole.Server),
            FirstTicks = times.Count > 0 ? times.Min() : null,
            LastTicks = times.Count > 0 ? times.Max() : null,
            Tenths = span is { } whole ? times.Select(ticks => PartOf(whole, ticks)).Distinct().Count() : 0,
        };

        private static PatternParticipant[] Listed(Dictionary<Participant, long> ends, PatternRole role) => [.. ends
            .Select(entry => new PatternParticipant(role, entry.Key.Process, entry.Key.Address, entry.Value))
            .OrderByDescending(participant => participant.Occurrences)
            .ThenBy(participant => participant.Process is null ? 1 : 0)
            .ThenBy(participant => participant.Process?.ToString() ?? participant.Address ?? string.Empty, StringComparer.Ordinal)];
    }

    /// <summary>Which of <see cref="Parts"/> equal parts of <paramref name="span"/> a moment is in; one outside it, the nearest.</summary>
    internal static int PartOf(TimeRange span, long ticks)
    {
        if (ticks <= span.StartTicks) return 0;
        if (ticks >= span.EndTicks) return Parts - 1;
        return (int)((Int128)(ticks - span.StartTicks) * Parts / span.SpanTicks);
    }
}
