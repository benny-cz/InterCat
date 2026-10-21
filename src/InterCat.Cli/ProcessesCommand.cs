using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Every process instance a session's evidence supports, with what each did.</summary>
internal sealed record ProcessesDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string BindingRule { get; init; }
    public required string EvidencePolicy { get; init; }

    /// <summary>
    /// What <see cref="Instances"/> are ranked by, the most first, as `--rank-by` names it: bytes-sent-and-received unless
    /// it named records, bytes-sent, bytes-received, connections-made or connections-accepted.
    /// </summary>
    public required string RankedBy { get; init; }

    /// <summary>
    /// The port of their own the instances listed accepted connections at, as `--port` named it, whoever connected; null
    /// where every instance is listed.
    /// </summary>
    public int? Port { get; init; }

    public required ProcessesSummaryDocument Summary { get; init; }

    /// <summary>
    /// What the capture covered over the session, mechanism by mechanism, as <c>icat metric --group-by process</c> states
    /// it of the same answer (R18, R21): a process can make any record the capture collects, so its counts are only as
    /// complete as the capture was.
    /// </summary>
    public required MetricCoverageDocument? Coverage { get; init; }

    public required IReadOnlyList<ProcessActivityDocument> Instances { get; init; }
    public required IReadOnlyList<ProcessUnattributedDocument> Unattributed { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record ProcessesSummaryDocument
{
    public required int Instances { get; init; }
    public required int ProcessIds { get; init; }
    public required int ReusedProcessIds { get; init; }
    public required IReadOnlyDictionary<string, int> ByWitness { get; init; }
    public required long RecordsAttributed { get; init; }
    public required long RecordsUnattributed { get; init; }
}

/// <summary>
/// One instance and what it did. The byte totals are sender-accounted sent bytes and receiver-accounted received bytes
/// in the transport-observed domain: each is a record the instance itself made, never the other end's measurement.
/// </summary>
internal sealed record ProcessActivityDocument
{
    public required ProcessInstanceDocument Process { get; init; }
    public required long Records { get; init; }
    public required IReadOnlyDictionary<string, long> Bindings { get; init; }
    public required long? TransportBytesSent { get; init; }
    public required long? TransportBytesReceived { get; init; }

    /// <summary>
    /// The instance's own send records behind <see cref="TransportBytesSent"/>: those that measured a size and those that
    /// recorded none. A null total of an instance that made sends is unmeasured; of one that made none, there are none to
    /// measure. Neither is a zero (R21).
    /// </summary>
    public required ProcessTransportRecordsDocument Sends { get; init; }

    /// <summary>The instance's own receive records behind <see cref="TransportBytesReceived"/>, as <see cref="Sends"/> counts them.</summary>
    public required ProcessTransportRecordsDocument Receives { get; init; }

    /// <summary>
    /// The TCP connections the instance made over the session, as the capture saw each open at either end - by its own
    /// connect or the other end's accept - as the window's card and a connection ranking count them (`relations-v1` §5);
    /// null where the session records no connection's open, so how many it made is unknown rather than none (R21).
    /// </summary>
    public required long? ConnectionsMade { get; init; }

    /// <summary>The TCP connections it accepted, by its own accept or the other end's connect, as <see cref="ConnectionsMade"/> counts them.</summary>
    public required long? ConnectionsAccepted { get; init; }

    /// <summary>
    /// Its own ports the connections it accepted arrived at, each once, the lowest first: the ports it listened at, as its
    /// card in the window names them; null where the session records no connection's open, as <see cref="ConnectionsAccepted"/>
    /// is.
    /// </summary>
    public IReadOnlyList<ushort>? AcceptedPorts { get; init; }

    /// <summary>
    /// Who the instance exchanged data with, through proven relations, when one PID was asked about; null otherwise,
    /// because deriving every instance's peers is a query per instance.
    /// </summary>
    public IReadOnlyList<ProcessPeerDocument>? Peers { get; init; }

    /// <summary>
    /// The other hosts the instance's connections ran to with no process of the capture at their other end, made or
    /// accepted, the most bytes sent and received first, when one PID was asked about; null otherwise, as
    /// <see cref="Peers"/> is. Who is at an address is not observed (P7).
    /// </summary>
    public IReadOnlyList<ProcessHostDocument>? OtherHosts { get; init; }

    /// <summary>
    /// Its own RPC call records the evidence policy admits, whose calls `icat operations` lists with the process at each
    /// one's other end, when one PID was asked about; null otherwise, as <see cref="Peers"/> is. A call is no transport
    /// record, so <see cref="Peers"/> holds none of them.
    /// </summary>
    public long? RpcRecords { get; init; }

    /// <summary>How many instances held its PID in the capture; its <c>lifecycleEpoch</c> says which of them it was.</summary>
    public required int PidHolders { get; init; }

    /// <summary>
    /// The records bound to it over the session only as candidates - a later holder's, which could be late records of an
    /// earlier one - whether or not the evidence policy counts them.
    /// </summary>
    public required long CandidateRecords { get; init; }

    /// <summary>
    /// Of those, the records the evidence policy left out of <see cref="Records"/>: a later holder that counts only its
    /// lifecycle records is not a quiet process (R21).
    /// </summary>
    public required long WithheldRecords { get; init; }

    /// <summary>How its records were bound to it, what the policy left out, and the coverage, in the inspector's words (R18).</summary>
    public required string Binding { get; init; }

    /// <summary>
    /// The part the instance played in collecting the capture - Broker, Client or Recorder - when the capture's collectors
    /// name it (`collector-binding-v1`); null for every other process.
    /// </summary>
    public CollectorRole? Collector { get; init; }
}

/// <summary>An instance's transport records of one side: how many measured a size, and how many recorded none.</summary>
internal sealed record ProcessTransportRecordsDocument
{
    public required long Measured { get; init; }
    public required long Unmeasured { get; init; }
}

/// <summary>
/// One process at the other end from an instance, or one reason the other end is unresolved. Sent bytes are measured
/// where the instance sent them and received bytes where it received them, so each is a record it made.
/// </summary>
internal sealed record ProcessPeerDocument
{
    public required ProcessInstanceDocument? Peer { get; init; }
    public required string? Unresolved { get; init; }
    public required long? SentTo { get; init; }
    public required long? ReceivedFrom { get; init; }

    /// <summary>The instance's send records to this end: measured, and those that recorded no size.</summary>
    public required ProcessTransportRecordsDocument Sends { get; init; }

    /// <summary>The instance's receive records from this end, as <see cref="Sends"/> counts them.</summary>
    public required ProcessTransportRecordsDocument Receives { get; init; }

    /// <summary>
    /// How many TCP connections the instance made to this end, as the capture saw each open at either end: its own
    /// <see cref="ProcessActivityDocument.ConnectionsMade"/>, end by end, so the ends' add up to it - one it made to itself
    /// counted at its own row, and those whose other end no record holds at the row of an end not observed. Null where the
    /// session records no connection's open (R21).
    /// </summary>
    public long? ConnectionsMade { get; init; }

    /// <summary>How many this end made to the instance, which it accepted, as <see cref="ConnectionsMade"/> counts them.</summary>
    public long? ConnectionsAccepted { get; init; }

    /// <summary>
    /// The ports at this end the connections the instance made to it arrived at, each once, the lowest first - for the end
    /// not observed, other hosts' ports. Null where the session records no connection's open.
    /// </summary>
    public IReadOnlyList<ushort>? MadeToPorts { get; init; }

    /// <summary>
    /// The instance's own ports the connections it accepted from this end arrived at, as <see cref="MadeToPorts"/> holds
    /// them.
    /// </summary>
    public IReadOnlyList<ushort>? AcceptedPorts { get; init; }
}

/// <summary>
/// One other host an instance's connections ran to, as `icat hosts` lists a host, over this instance's connections alone:
/// its TCP connections and UDP flows, how many of them the capture saw the instance open by a connect or accept from the
/// host, the ports they ran to there and those of its own the accepted ones arrived at, their records, and what the
/// instance's own transfers there measured each way.
/// </summary>
internal sealed record ProcessHostDocument
{
    public required string Address { get; init; }
    public required string? Scope { get; init; }
    public required int TcpConnections { get; init; }
    public required int UdpFlows { get; init; }
    public required int Connected { get; init; }
    public required int Accepted { get; init; }
    public required IReadOnlyList<ushort> Ports { get; init; }
    public required IReadOnlyList<ushort> AcceptedPorts { get; init; }
    public required long Records { get; init; }
    public required long Sends { get; init; }
    public required long SentBytes { get; init; }
    public required long UnmeasuredSends { get; init; }
    public required long Receives { get; init; }
    public required long ReceivedBytes { get; init; }
    public required long UnmeasuredReceives { get; init; }
}

internal sealed record ProcessUnattributedDocument
{
    public required string Reason { get; init; }
    public required long Records { get; init; }
}

/// <summary>
/// Lists the process instances a published session's evidence supports: how each is witnessed, its lifetime, and how
/// many records and bytes bind to it. It answers from the same grouped metrics `icat metric --group-by process`
/// does, so the two never disagree about a number.
/// </summary>
internal static class ProcessesCommand
{
    /// <summary>The other hosts one instance's detail lists, the most bytes first; its document holds every one.</summary>
    private const int HostsListed = 20;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? topOption = command.TakeOption("--top");
        string? pidOption = command.TakeOption("--pid");
        string? portOption = command.TakeOption("--port");
        string? rankOption = command.TakeOption("--rank-by");
        string? policyOption = command.TakeOption("--evidence-policy");
        string? outputOption = command.TakeOption("--output");
        string? sessionPath = command.TakePositional();
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat processes <directory>");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        int top = 20;
        if (topOption is not null
            && (!int.TryParse(topOption, NumberStyles.None, CultureInfo.InvariantCulture, out top) || top > 100_000))
        {
            ConsoleUi.Failure($"--top expects a number of instances up to 100,000, or 0 for all; '{topOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        int? pid = null;
        if (pidOption is not null)
        {
            if (!int.TryParse(pidOption, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                ConsoleUi.Failure($"--pid expects a process id; '{pidOption}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            pid = parsed;
        }

        // One port's listeners, as the window's search for a port alone finds them: a port, after a colon or not.
        ushort? port = null;
        if (portOption is not null)
        {
            if (!ushort.TryParse(portOption.StartsWith(':') ? portOption[1..] : portOption, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ushort parsed) || parsed == 0)
            {
                ConsoleUi.Failure($"--port takes a port, a number from 1 to 65535 - 135 or :135 - and '{portOption}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (pid is not null)
            {
                ConsoleUi.Failure("--port lists the instances that accepted connections at a port; --pid shows each instance of one "
                    + "PID in full.");
                return InterCatExitCode.InvalidInvocation;
            }

            port = parsed;
        }

        // The list is ranked by what --rank-by names, in icat export's and icat hosts' words; one PID's detail is not a list.
        // One port's listeners are ranked by the connections each accepted unless it names another.
        RankingMetric? rankBy = rankOption switch
        {
            null when port is not null => RankingMetric.ConnectionsAccepted,
            null or "bytes-sent-and-received" => RankingMetric.EndpointBytes,
            "records" => RankingMetric.Records,
            "bytes-sent" => RankingMetric.BytesSent,
            "bytes-received" => RankingMetric.BytesReceived,
            "connections-made" => RankingMetric.ConnectionsMade,
            "connections-accepted" => RankingMetric.ConnectionsAccepted,
            _ => null,
        };
        if (rankBy is null || (rankOption is not null && pid is not null))
        {
            ConsoleUi.Failure(rankBy is null
                ? "--rank-by ranks the instances by records, bytes-sent, bytes-received, bytes-sent-and-received, "
                    + $"connections-made or connections-accepted; '{rankOption}' is none of them."
                : "--rank-by ranks the list of instances; --pid shows each instance of one PID in full.");
            return InterCatExitCode.InvalidInvocation;
        }

        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated;
        if (policyOption is not null
            && (!Enum.TryParse(policyOption.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out policy)
                || !Enum.IsDefined(policy)))
        {
            ConsoleUi.Failure(
                $"--evidence-policy expects one of: {string.Join(", ", Enum.GetNames<EvidencePolicy>())}. '{policyOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            return Icat.NoSession();
        }

        ConsoleUi.Progress("Deriving process instances and binding every record to one.");
        MetricResult records = Grouped(store, Metric.Observations, null, null, policy, cancellationToken);
        if (!records.IsAvailable)
        {
            ConsoleUi.Failure(records.UnavailableExplanation ?? records.Unavailable.ToString());
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        MetricResult sent = Grouped(store, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide, policy, cancellationToken);
        MetricResult received = Grouped(store, Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.ReceiveSide, policy, cancellationToken);

        // Each instance as the inspector explains it: the overview's own projection under the same policy (R18).
        SessionOverviewBundle projected = SessionOverviewProjector.Project(store, policy, cancellationToken: cancellationToken);
        if (port is { } asked && !projected.ConnectionOpensRecorded)
        {
            ConsoleUi.Failure($"This session records no connection's open, so which instances accepted connections at port {asked} "
                + "is unknown rather than none.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (rankBy is RankingMetric.ConnectionsMade or RankingMetric.ConnectionsAccepted && !projected.ConnectionOpensRecorded)
        {
            ConsoleUi.Failure($"This session records no connection's open, so how many connections each instance made or accepted "
                + $"is unknown rather than none, and there is nothing to rank by {rankOption}.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        Dictionary<ProcessInstanceId, ProcessNode> nodes = projected.Nodes.ToDictionary(node => node.Id);
        ProcessesDocument document = Describe(full, records, sent, received, policy, nodes, projected.Collectors.Unfound,
            projected.ConnectionOpensRecorded, rankBy.Value);
        if (port is { } listened)
        {
            // Those whose accepted connections arrived at the port, whoever connected: the ports each process's card names.
            document = document with
            {
                Port = listened,
                Instances = [.. document.Instances.Where(item => item.AcceptedPorts?.Contains(listened) == true)],
            };
        }
        if (pid is { } selected)
        {
            ConsoleUi.Progress($"Finding what each instance of PID {selected} exchanged data with, and who connected to whom.");

            // Who opened their connections, end by end, by the rule its card's counts take (relations-v1 §5): only where the
            // session records an open, since otherwise how many any end made is unknown rather than none.
            TransportRelationIndex? relations = projected.ConnectionOpensRecorded
                ? SessionMetricDerivations.For(store, cancellationToken)?.Relations(cancellationToken)
                : null;
            document = document with
            {
                Instances =
                [
                    .. document.Instances.Select(item => item.Process.ProcessId != selected
                        ? item
                        : item with
                        {
                            Peers = Peers(store, new ProcessInstanceId(Guid.Parse(item.Process.InstanceId)), policy, records.Clock,
                                relations is null ? null
                                    : ConnectionOpens.With(relations, policy, new ProcessInstanceId(Guid.Parse(item.Process.InstanceId))),
                                cancellationToken),
                            OtherHosts = OtherHosts(store, projected.HostLinks, new ProcessInstanceId(Guid.Parse(item.Process.InstanceId)),
                                policy, cancellationToken),
                            RpcRecords = nodes[new ProcessInstanceId(Guid.Parse(item.Process.InstanceId))].Activity
                                .Where(count => count.Mechanism == Mechanism.Rpc).Sum(count => count.Records),
                        }),
                ],
            };
        }

        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, records.Coverage is { } coverage ? MetricCommand.CoverageWords(coverage) : null, records.Clock, top,
                pid, full);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Process instances written to {outputPath}.");
        }

        return store.Recovery.RolledBackToLastKnownGood ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    private static MetricResult Grouped(
        SessionStore store,
        Metric metric,
        ByteDomain? domain,
        AccountingSide? side,
        EvidencePolicy policy,
        CancellationToken cancellationToken) =>
        SessionMetrics.Evaluate(
            store,
            new()
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = metric,
                ByteDomain = domain,
                AccountingSide = side,
                Grouping = LaneGrouping.InstanceOnly,
                EvidencePolicy = policy,
            },
            Checkpointed(store, cancellationToken),
            cancellationToken);

    /// <summary>The answer's options: the session's checkpointed derivation, so each question derives no instance again.</summary>
    private static MetricEvaluationOptions Checkpointed(SessionStore store, CancellationToken cancellationToken) =>
        new() { Derivations = SessionMetricDerivations.For(store, cancellationToken) };

    private static ProcessesDocument Describe(
        string path,
        MetricResult records,
        MetricResult sent,
        MetricResult received,
        EvidencePolicy policy,
        Dictionary<ProcessInstanceId, ProcessNode> nodes,
        IReadOnlyList<CollectorProcessV1> unfound,
        bool opensRecorded,
        RankingMetric rankBy)
    {
        Dictionary<ProcessInstanceId, MetricGroup> sentBy = ByInstance(sent);
        Dictionary<ProcessInstanceId, MetricGroup> receivedBy = ByInstance(received);
        (Func<ProcessActivityDocument, long> first, Func<ProcessActivityDocument, long> then) = Ranking(rankBy);
        List<ProcessActivityDocument> instances =
        [
            .. records.Groups
                .Where(group => group.Process is not null)
                .Select(group => new ProcessActivityDocument
                {
                    Process = ProcessInstanceDocument.From(group.Process!, records.Clock),
                    Records = group.Value ?? 0,
                    Bindings = group.Bindings.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
                    TransportBytesSent = sentBy.GetValueOrDefault(group.Process!.Id)?.Value,
                    TransportBytesReceived = receivedBy.GetValueOrDefault(group.Process!.Id)?.Value,
                    Sends = Records(sentBy.GetValueOrDefault(group.Process!.Id)),
                    Receives = Records(receivedBy.GetValueOrDefault(group.Process!.Id)),
                    ConnectionsMade = opensRecorded ? nodes[group.Process!.Id].ConnectionsMade : null,
                    ConnectionsAccepted = opensRecorded ? nodes[group.Process!.Id].ConnectionsAccepted : null,
                    AcceptedPorts = opensRecorded ? nodes[group.Process!.Id].AcceptedPorts : null,
                    PidHolders = nodes[group.Process!.Id].PidHolders,
                    CandidateRecords = nodes[group.Process!.Id].CandidateRecords,
                    WithheldRecords = nodes[group.Process!.Id].WithheldRecords,
                    Binding = ProcessBindingText.Explain(nodes[group.Process!.Id], unfound),
                    Collector = nodes[group.Process!.Id].Collector,
                })
                .OrderByDescending(first)
                .ThenByDescending(then)
                .ThenBy(item => item.Process.ProcessId)
                .ThenBy(item => item.Process.LifecycleEpoch),
        ];

        var pids = instances.GroupBy(item => item.Process.ProcessId).ToList();
        long unattributed = records.Unattributed.Sum(group => group.Value ?? 0);
        var caveats = new List<string>
        {
            "An instance is identified by host, boot, PID and its witnessed start key when supplied; otherwise by "
            + "its observed creation or first witness, never by the PID alone (R22). One seen only in its own "
            + "records is provisional: the capture holds no lifecycle record of it, so its start is unknown.",
            "Sent and received bytes are each instance's own transport records: sender-accounted for what it sent and "
            + "receiver-accounted for what it received. Added across instances they count a local transfer at both "
            + "ends.",
            opensRecorded
                ? "Connections made and accepted are each instance's TCP connections as the capture saw them open at either "
                    + "end, by its own connect or accept or the other end's: one between two instances is one each counts, "
                    + "and one an instance made to itself it both made and accepted. Those it accepted are at the ports of "
                    + "its own they arrived at, which it listened at."
                : "This session records no connection's open, so how many connections an instance made or accepted is "
                    + "unknown rather than none.",
        };
        caveats.AddRange(records.Caveats.Where(caveat => caveat.Contains("process-binding", StringComparison.Ordinal)
            || caveat.Contains("reused", StringComparison.Ordinal)
            || caveat.Contains("no witnessed instance", StringComparison.Ordinal)));

        return new()
        {
            Contract = "entities-v1",
            Path = path,
            Generation = records.Generation,
            BindingRule = records.BindingRule ?? ProcessInstanceIndex.BindingRule,
            EvidencePolicy = policy.ToString(),
            RankedBy = RankName(rankBy),
            Coverage = MetricCommand.CoverageDocument(records.Coverage),
            Summary = new()
            {
                Instances = instances.Count,
                ProcessIds = pids.Count,
                ReusedProcessIds = pids.Count(group => group.Count() > 1),
                ByWitness = instances
                    .GroupBy(item => item.Process.Witness)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count()),
                RecordsAttributed = instances.Sum(item => item.Records),
                RecordsUnattributed = unattributed,
            },
            Instances = instances,
            Unattributed =
            [
                .. records.Unattributed.Select(group => new ProcessUnattributedDocument
                {
                    Reason = group.Reason!.Value.ToString(),
                    Records = group.Value ?? 0,
                }),
            ],
            Caveats = caveats,
        };
    }

    /// <summary>
    /// What one instance sent to and received from each process at the other end, through the relation rule, with the
    /// traffic whose other end is unresolved kept as its own rows. Both totals are the instance's own records, so
    /// nothing here is measured at a peer. With <paramref name="openings"/>, each row also counts the connections the
    /// instance made to that end and accepted from it, and an end it opened connections with and exchanged no data with is
    /// a row of its own, so the rows' connections add up to its own.
    /// </summary>
    private static List<ProcessPeerDocument> Peers(
        SessionStore store,
        ProcessInstanceId instance,
        EvidencePolicy policy,
        SourceClockDescriptor? clock,
        IReadOnlyList<PeerConnections>? openings,
        CancellationToken cancellationToken)
    {
        MetricResult sentTo = SessionMetrics.Evaluate(store, PeerRequest(Metric.BytesSent, AccountingSide.SendSide, policy) with
        {
            Sender = instance,
        }, Checkpointed(store, cancellationToken), cancellationToken);
        MetricResult receivedFrom = SessionMetrics.Evaluate(store, PeerRequest(Metric.BytesReceived, AccountingSide.ReceiveSide, policy) with
        {
            Receiver = instance,
        }, Checkpointed(store, cancellationToken), cancellationToken);

        // An answer with nothing measured is unavailable, and still holds each end's records, whose values are unknown: the
        // ends are listed with their records unmeasured rather than left out, as if the instance had exchanged nothing.
        var peers = new Dictionary<string, (ProcessInstance? Peer, ProcessBindingReason? Reason, MetricGroup? Sent, MetricGroup? Received)>(
            StringComparer.Ordinal);
        foreach ((MetricResult result, bool sent) in new[] { (sentTo, true), (receivedFrom, false) })
        {
            foreach (MetricGroup group in result.Groups.Concat(result.Unattributed))
            {
                string key = group.Process?.Id.ToString() ?? $"reason/{group.Reason}";
                (ProcessInstance? Peer, ProcessBindingReason? Reason, MetricGroup? Sent, MetricGroup? Received) entry =
                    peers.GetValueOrDefault(key, (group.Process, group.Reason, null, null));
                peers[key] = sent ? entry with { Sent = group } : entry with { Received = group };
            }
        }

        // The connections each end opened, by the same keys: those whose other end no record holds are the end not
        // observed, as their traffic is.
        var opened = new Dictionary<string, PeerConnections>(StringComparer.Ordinal);
        foreach (PeerConnections connections in openings ?? [])
        {
            string key = connections.Peer?.Id.ToString() ?? $"reason/{ProcessBindingReason.PeerNotObserved}";
            opened[key] = connections;
            _ = peers.TryAdd(key, (connections.Peer, connections.Peer is null ? ProcessBindingReason.PeerNotObserved : null, null, null));
        }

        return
        [
            .. peers
                .OrderBy(entry => entry.Value.Peer is null)
                .ThenByDescending(entry => (entry.Value.Sent?.Value ?? 0) + (entry.Value.Received?.Value ?? 0))
                .ThenBy(entry => entry.Value.Peer?.Id.ToString() ?? entry.Value.Reason.ToString(), StringComparer.Ordinal)
                .Select(entry => new ProcessPeerDocument
                {
                    Peer = entry.Value.Peer is { } peer ? ProcessInstanceDocument.From(peer, clock) : null,
                    Unresolved = entry.Value.Reason?.ToString(),
                    SentTo = entry.Value.Sent?.Value,
                    ReceivedFrom = entry.Value.Received?.Value,
                    Sends = Records(entry.Value.Sent),
                    Receives = Records(entry.Value.Received),
                    ConnectionsMade = openings is null ? null : opened.GetValueOrDefault(entry.Key)?.Made ?? 0,
                    ConnectionsAccepted = openings is null ? null : opened.GetValueOrDefault(entry.Key)?.Accepted ?? 0,
                    MadeToPorts = openings is null ? null : opened.GetValueOrDefault(entry.Key)?.MadePorts ?? [],
                    AcceptedPorts = openings is null ? null : opened.GetValueOrDefault(entry.Key)?.AcceptedPorts ?? [],
                }),
        ];
    }

    /// <summary>
    /// The other hosts one instance's connections ran to, read as `icat hosts --rank-by bytes-sent-and-received` reads every
    /// process's: the census's connections to each, and what the instance's transfers there measured, the most first.
    /// </summary>
    private static List<ProcessHostDocument> OtherHosts(
        SessionStore store,
        IReadOnlyList<HostLink> links,
        ProcessInstanceId instance,
        EvidencePolicy policy,
        CancellationToken cancellationToken) =>
    [
        .. SessionHostTraffic.Of(store, [.. links.Where(link => link.Process == instance)], RankingMetric.EndpointBytes,
                policy: policy, cancellationToken: cancellationToken)
            .Select(traffic => new ProcessHostDocument
            {
                Address = traffic.Host.Address,
                Scope = AddressScope.Of(traffic.Host.Address),
                TcpConnections = traffic.Host.TcpConnections,
                UdpFlows = traffic.Host.UdpFlows,
                Connected = traffic.Host.Connected,
                Accepted = traffic.Host.Accepted,
                Ports = traffic.Host.Ports,
                AcceptedPorts = traffic.Host.AcceptedPorts,
                Records = traffic.Host.Records,
                Sends = traffic.Sends,
                SentBytes = traffic.SentBytes,
                UnmeasuredSends = traffic.UnmeasuredSends,
                Receives = traffic.Receives,
                ReceivedBytes = traffic.ReceivedBytes,
                UnmeasuredReceives = traffic.UnmeasuredReceives,
            }),
    ];

    private static MetricRequest PeerRequest(Metric metric, AccountingSide side, EvidencePolicy policy) => new()
    {
        Basis = AnalysisBasis.SourceObservations,
        Metric = metric,
        ByteDomain = ByteDomain.TransportObserved,
        AccountingSide = side,
        Grouping = LaneGrouping.Peer,
        EvidencePolicy = policy,
    };

    /// <summary>
    /// Each instance's group in one grouped answer: its value and the records behind it. An answer with nothing measured
    /// in the domain is unavailable, and still holds each instance's records, whose values are all unknown: every instance
    /// then has no byte value rather than zero bytes, and one that made records says they are unmeasured.
    /// </summary>
    private static Dictionary<ProcessInstanceId, MetricGroup> ByInstance(MetricResult result)
    {
        var groups = new Dictionary<ProcessInstanceId, MetricGroup>();
        foreach (MetricGroup group in result.Groups)
        {
            if (group.Process is { } process)
            {
                groups[process.Id] = group;
            }
        }

        return groups;
    }

    private static ProcessTransportRecordsDocument Records(MetricGroup? group) => new()
    {
        Measured = group?.KnownContributions ?? 0,
        Unmeasured = group?.UnknownContributions ?? 0,
    };

    /// <summary>
    /// One side's bytes as the window's ranked rows say them (R18, R21): the measured sum, "unmeasured" when its records
    /// recorded no size, or "no sends" or "no receives" when it made none.
    /// </summary>
    private static string Bytes(long? total, ProcessTransportRecordsDocument records, string none) =>
        total is { } bytes ? ConsoleUi.Bytes(bytes)
        : records.Measured + records.Unmeasured > 0 ? "unmeasured"
        : none;

    private static void Render(ProcessesDocument document, string? coverage, SourceClockDescriptor? clock, int top, int? pid,
        string path)
    {
        ConsoleUi.Heading("Process instances");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Binding rule", $"{document.BindingRule}, evidence policy {document.EvidencePolicy}");
        ConsoleUi.Field(
            "Instances",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{document.Summary.Instances:N0} across {CountText.Of(document.Summary.ProcessIds, "PID")}, ")
                + (document.Summary.ProcessIds == 1
                    ? document.Summary.ReusedProcessIds == 1 ? "reused" : "never reused"
                    : string.Create(CultureInfo.CurrentCulture, $"{document.Summary.ReusedProcessIds:N0} of them reused")));
        ConsoleUi.Field(
            "Witnessed",
            string.Join(
                ", ",
                document.Summary.ByWitness.Select(entry => string.Create(
                    CultureInfo.CurrentCulture,
                    $"{entry.Value:N0} {WitnessWords(entry.Key)}"))));
        long allRecords = document.Summary.RecordsAttributed + document.Summary.RecordsUnattributed;
        ConsoleUi.Field(
            "Records attributed",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{document.Summary.RecordsAttributed:N0} of {allRecords:N0}"));

        // The list's order, which --rank-by chooses; one PID's instances are shown in full, not ranked.
        if (pid is null)
        {
            ConsoleUi.Field("Ranked by", RankedWords(document.RankedBy));
        }

        // One port's listeners say which port they are listed for.
        if (document.Port is { } port)
        {
            ConsoleUi.Field("Accepted at", string.Create(CultureInfo.InvariantCulture, $"port {port}, whoever connected"));
        }

        // What the capture covered, in the words icat metric uses of the same answer: a count is only as complete as the
        // capture was, and an instance's count of none for a mechanism it did not collect is no proof of inactivity (R21).
        if (coverage is { Length: > 0 })
        {
            ConsoleUi.Note(coverage);
        }

        IReadOnlyList<ProcessActivityDocument> shown = pid is { } only
            ? [.. document.Instances.Where(item => item.Process.ProcessId == only)]
            : top == 0 ? document.Instances : [.. document.Instances.Take(top)];
        var reusedPids = document.Instances
            .GroupBy(item => item.Process.ProcessId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        ConsoleUi.Line();
        if (shown.Count == 0)
        {
            ConsoleUi.Line(pid is { } missing ? $"  No instance of PID {missing} is in this session's evidence."
                : document.Port is { } unheld
                    ? string.Create(CultureInfo.InvariantCulture, $"  No instance accepted a connection at port {unheld}. icat hosts ")
                        + string.Create(CultureInfo.InvariantCulture, $"<directory> --address :{unheld} lists the processes whose ")
                        + "connections ran to that port at another host, and icat ports <directory> the ports its processes "
                        + "accepted connections at."
                : "  No process instance is in this session's evidence.");
        }
        else
        {
            ConsoleUi.Table(
                ["PID", "Image", "Instance", "Records", "Connections", "Sent (transport)", "Received (transport)"],
                [
                    .. shown.Select(item => new[]
                    {
                        reusedPids.Contains(item.Process.ProcessId)
                            ? string.Create(CultureInfo.InvariantCulture, $"{item.Process.ProcessId} #{item.Process.LifecycleEpoch}")
                            : item.Process.ProcessId.ToString(CultureInfo.InvariantCulture),
                        // One of InterCat's own says which, as the window's row does (§19.5).
                        (item.Process.ImageName ?? "not witnessed")
                            + (item.Collector is { } collector ? $" ({CollectorText.Label(collector)})" : string.Empty),
                        Lifetime(item.Process, clock),
                        ConsoleUi.Count(item.Records),
                        Connections(item, "none") + AcceptedAt(item),
                        Bytes(item.TransportBytesSent, item.Sends, "no sends"),
                        Bytes(item.TransportBytesReceived, item.Receives, "no receives"),
                    }),
                ]);
        }

        // A later holder of a reused PID counts only its lifecycle records while the policy counts no candidate: it says so,
        // and how to count them, so its row is not read as a quiet process (R21).
        ProcessActivityDocument[] withheld = [.. shown.Where(item => item.WithheldRecords > 0)];
        foreach (ProcessActivityDocument item in pid is null ? withheld : [])
        {
            ConsoleUi.Note(item.Binding);
        }

        if (pid is null && withheld.Length > 0)
        {
            ConsoleUi.Note("--evidence-policy IncludeCandidates counts them.");
        }

        foreach (ProcessActivityDocument item in pid is null ? [] : shown)
        {
            RenderDetail(item, path);
        }

        if (pid is null && top != 0 && document.Instances.Count > shown.Count)
        {
            ConsoleUi.Line(string.Create(
                CultureInfo.CurrentCulture,
                $"  ... and {document.Instances.Count - shown.Count:N0} more. --top 0 lists every instance; --json always does."));
        }

        if (document.Unattributed.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Line("  Records not attributed to an instance:");
            ConsoleUi.Table(
                ["Reason", "Records"],
                [
                    .. document.Unattributed.Select(item => new[]
                    {
                        BindingText.Reason(Enum.Parse<ProcessBindingReason>(item.Reason)),
                        ConsoleUi.Count(item.Records),
                    }),
                ]);
        }

        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    /// <summary>
    /// One instance in full: the identity to use in a process filter, the evidence it rests on, and who it exchanged
    /// data with, with the command that answers more.
    /// </summary>
    private static void RenderDetail(ProcessActivityDocument item, string path)
    {
        ProcessInstanceDocument process = item.Process;
        string epoch = process.LifecycleEpoch > 1
            ? string.Create(CultureInfo.InvariantCulture, $" #{process.LifecycleEpoch}")
            : string.Empty;
        ConsoleUi.Line();
        ConsoleUi.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"  PID {process.ProcessId}{epoch} {process.ImageName ?? "(image not witnessed)"}"));
        ConsoleUi.Field("Instance id", process.InstanceId);
        ConsoleUi.Field("Counted", item.Binding + (item.WithheldRecords > 0 ? " --evidence-policy IncludeCandidates counts them." : string.Empty));
        ConsoleUi.Field("Identity from", Words(process.IdentityEvidence) + (process.StartSequence is { } sequence
            ? string.Create(CultureInfo.InvariantCulture, $", start sequence {sequence}")
            : string.Empty));
        if (process.ImagePath is { } image)
        {
            ConsoleUi.Field("Image path", image);
        }

        if (process.ParentProcessId is { } parent)
        {
            ConsoleUi.Field(
                "Parent",
                process.ParentInstanceId is { } parentInstance
                    ? string.Create(CultureInfo.InvariantCulture, $"PID {parent}, instance {parentInstance} ({process.ParentBinding?.ToLowerInvariant() ?? "linked"})")
                    : string.Create(CultureInfo.InvariantCulture, $"PID {parent}, named by the source; no instance of it is in the capture"));
        }

        if (process.SessionId is { } session)
        {
            ConsoleUi.Field("Session", session.ToString(CultureInfo.InvariantCulture));
        }

        if (process.Gaps.Count > 0)
        {
            ConsoleUi.Field("Evidence gaps", string.Join(", ", process.Gaps.Select(Words)));
        }

        // Who opened its connections, as its card in the window counts them, and the ports of its own it accepted them at.
        ConsoleUi.Field("Connections", (item.ConnectionsMade, item.ConnectionsAccepted) switch
        {
            (null, _) or (_, null) => "not recorded: this session records no connection's open",
            (0, 0) => "none the capture saw open",
            _ when item.AcceptedPorts is { Count: > 0 } ports =>
                Connections(item, string.Empty) + " at " + HostLinkText.Ports(ports) + ", as the capture saw them open",
            _ => Connections(item, string.Empty) + ", as the capture saw them open",
        });

        // Its RPC calls, which no transport record holds: icat operations lists each, with the process at its other end.
        if (item.RpcRecords is > 0 and var calls)
        {
            ConsoleUi.Field("RPC", CountText.Of(calls, "call record") + string.Create(CultureInfo.InvariantCulture,
                $"; icat operations <directory> --pid {process.ProcessId} lists its calls, each with the process at its other end"));
        }

        if (item.Peers is not { } peers)
        {
            return;
        }

        if (peers.Count == 0)
        {
            ConsoleUi.Note("It made no transport send or receive record in this session, which is not proof it sent or "
                + "received nothing.");
        }
        else
        {
            // Who connected to whom, end by end, where the capture saw any of their connections open: its own count above.
            bool opened = peers.Any(peer => peer.ConnectionsMade + peer.ConnectionsAccepted > 0);
            ConsoleUi.Line($"  Exchanged with ({TransportRelationIndex.RelationRule}; each total is a record this instance made):");
            ConsoleUi.Table(
                ["Other end", .. opened ? ["Connections"] : Array.Empty<string>(), "Sent to", "Received from"],
                [
                    .. peers.Select(peer => (IReadOnlyList<string>)
                    [
                        peer.Peer is { } other
                            ? string.Create(CultureInfo.InvariantCulture, $"PID {other.ProcessId} {other.ImageName ?? "(image not witnessed)"}")
                                + (other.InstanceId == process.InstanceId ? " (itself)" : string.Empty)
                            : peer.Unresolved == nameof(ProcessBindingReason.PeerNotObserved)
                                ? "not in the capture (remote or unobserved)"
                                : "unresolved: " + BindingText.Reason(Enum.Parse<ProcessBindingReason>(peer.Unresolved!)),
                        .. opened
                            ? [Opened(peer.ConnectionsMade ?? 0, peer.ConnectionsAccepted ?? 0, peer.MadeToPorts ?? [],
                                peer.AcceptedPorts ?? [])]
                            : Array.Empty<string>(),
                        Bytes(peer.SentTo, peer.Sends, "none"),
                        Bytes(peer.ReceivedFrom, peer.Receives, "none"),
                    ]),
                ]);
            if (opened)
            {
                ConsoleUi.Note("Connections are those it made to each end and those it accepted from each, as the capture saw them "
                    + "open, and add up to its own above" + (peers.Any(peer => peer.ConnectionsMade + peer.ConnectionsAccepted == 0)
                        ? "; - where it saw none of theirs open, as of a UDP flow, which nothing opens."
                        : ".")
                    + " Their ports are where they arrived: the end's for those it made, its own for those it accepted.");
            }
        }

        // The other hosts it reached, by address, which the table above counts as one unresolved end: as icat hosts lists
        // them, its own connections alone.
        if (item.OtherHosts is { Count: > 0 } hosts)
        {
            ConsoleUi.Line("  " + HostLinkText.HostsHeading("it", hosts.Sum(host => (long)host.Accepted),
                    hosts.Sum(host => (long)host.TcpConnections + host.UdpFlows))
                + ", the most bytes sent and received first; who is at an address is not observed:");
            bool accepting = hosts.Take(HostsListed).Any(host => host.Accepted > 0);
            ConsoleUi.Table(
                ["Host", "Scope", "Connections", "Ports", .. accepting ? ["Accepted here"] : Array.Empty<string>(), "Records", "Sent",
                    "Received"],
                [.. hosts.Take(HostsListed).Select(host => (IReadOnlyList<string>)
                [
                    host.Address, host.Scope ?? "-",
                    HostLinkText.Reached(new OtherHost(host.Address, host.TcpConnections, host.UdpFlows, 1, host.Records), countsProcesses: false),
                    host.Ports.Count == 0 ? "-" : HostLinkText.PortNumbers(host.Ports),
                    .. accepting ? [HostLinkText.AcceptedCell(host.Accepted, host.AcceptedPorts)] : Array.Empty<string>(),
                    ConsoleUi.Count(host.Records),
                    HostsCommand.Bytes(host.SentBytes, host.Sends, host.UnmeasuredSends, "no sends"),
                    HostsCommand.Bytes(host.ReceivedBytes, host.Receives, host.UnmeasuredReceives, "no receives"),
                ])]);
            if (hosts.Count > HostsListed)
            {
                ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture, $"{hosts.Count - HostsListed:N0} more, quieter; --json lists every one, and ")
                    + $"icat channels <directory> --process {process.InstanceId} --one-sided each of its connections.");
            }
        }

        ConsoleUi.Note("To rank every record it took part in by the process at the other end:");
        ConsoleUi.Line($"    icat metric \"{path}\" --metric observations --participant {process.InstanceId} --group-by peer");
    }

    /// <summary>
    /// The TCP connections an instance made and accepted, "3 made · 12 accepted"; <paramref name="none"/> where it made and
    /// accepted none, and "not recorded" where the session records no connection's open (R21).
    /// </summary>
    private static string Connections(ProcessActivityDocument item, string none) =>
        (item.ConnectionsMade, item.ConnectionsAccepted) switch
        {
            ({ } made, { } accepted) when made + accepted > 0 =>
                $"{ConsoleUi.Count(made)} made · {ConsoleUi.Count(accepted)} accepted",
            ({ }, { }) => none,
            _ => "not recorded",
        };

    /// <summary>
    /// The ports of its own an instance accepted connections at, as a table's cell states them after its connections:
    /// " at 445 and 3389"; empty where it accepted none, or the session records no connection's open.
    /// </summary>
    private static string AcceptedAt(ProcessActivityDocument item) =>
        item.AcceptedPorts is { Count: > 0 } ports ? " at " + HostLinkText.PortNumbers(ports) : string.Empty;

    /// <summary>
    /// What a ranking orders instances by, the most first, and what breaks its ties before the PID: an instance's other
    /// direction of connections under a connection ranking, its bytes under a ranking by records, and its records otherwise.
    /// </summary>
    private static (Func<ProcessActivityDocument, long> First, Func<ProcessActivityDocument, long> Then) Ranking(RankingMetric rankBy)
    {
        static long Bytes(ProcessActivityDocument item) => (item.TransportBytesSent ?? 0) + (item.TransportBytesReceived ?? 0);
        return rankBy switch
        {
            RankingMetric.Records => (item => item.Records, Bytes),
            RankingMetric.BytesSent => (item => item.TransportBytesSent ?? 0, item => item.Records),
            RankingMetric.BytesReceived => (item => item.TransportBytesReceived ?? 0, item => item.Records),
            RankingMetric.ConnectionsMade => (item => item.ConnectionsMade ?? 0, item => item.ConnectionsAccepted ?? 0),
            RankingMetric.ConnectionsAccepted => (item => item.ConnectionsAccepted ?? 0, item => item.ConnectionsMade ?? 0),
            _ => (Bytes, item => item.Records),
        };
    }

    /// <summary>A ranking as --rank-by names it.</summary>
    private static string RankName(RankingMetric rankBy) => rankBy switch
    {
        RankingMetric.Records => "records",
        RankingMetric.BytesSent => "bytes-sent",
        RankingMetric.BytesReceived => "bytes-received",
        RankingMetric.ConnectionsMade => "connections-made",
        RankingMetric.ConnectionsAccepted => "connections-accepted",
        _ => "bytes-sent-and-received",
    };

    /// <summary>What a list's "Ranked by" says: "connections made, the most first, then connections accepted".</summary>
    private static string RankedWords(string rankedBy) => rankedBy switch
    {
        "records" => "records, the most first, then transport bytes sent and received",
        "bytes-sent" => "transport bytes sent, the most first, then records",
        "bytes-received" => "transport bytes received, the most first, then records",
        "connections-made" => "connections made, the most first, then connections accepted",
        "connections-accepted" => "connections accepted, the most first, then connections made",
        _ => "transport bytes sent and received, the most first, then records",
    };

    /// <summary>
    /// The connections an instance made to one end and accepted from it, each with the ports it arrived at where known: "2
    /// made to 8080", "1 accepted at 52001", "1 made to 52001 · 1 accepted at 52001", or "-" where the capture saw none of
    /// theirs open.
    /// </summary>
    private static string Opened(long made, long accepted, IReadOnlyList<ushort> madeTo, IReadOnlyList<ushort> acceptedAt) =>
        string.Join(" · ", new[]
        {
            made == 0 ? null : ConsoleUi.Count(made) + " made" + Ports(" to ", madeTo),
            accepted == 0 ? null : ConsoleUi.Count(accepted) + " accepted" + Ports(" at ", acceptedAt),
        }.OfType<string>()) is { Length: > 0 } said ? said : "-";

    /// <summary>Ports after the words leading to them, numbers alone: " to 80 and 443"; empty where there are none.</summary>
    private static string Ports(string words, IReadOnlyList<ushort> ports) =>
        ports.Count == 0 ? string.Empty : words + HostLinkText.PortNumbers(ports);

    private static string Words(string pascal) =>
        string.Concat(pascal.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : character.ToString())).Trim();

    private static string Lifetime(ProcessInstanceDocument process, SourceClockDescriptor? clock)
    {
        string start = process.CreatedNativeTicks is { } created
            ? $"created {SessionText.Instant(clock, created)}"
            : process.Witness switch
            {
                nameof(ProcessWitness.Rundown) => "running at capture start",
                nameof(ProcessWitness.ActivityOnly) => "seen only in its own records",
                _ => "running before its first record",
            };
        string end = process.ExitedNativeTicks is { } exited
            ? $", exited {SessionText.Instant(clock, exited)}"
                + (process.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $" (code {code})") : string.Empty)
            : process.Gaps.Contains(nameof(ProcessEvidenceGaps.ExitNotWitnessed)) && process.LifetimeEndNativeTicks is { } end2
                ? $", replaced {SessionText.Instant(clock, end2)} with no exit seen"
                : string.Empty;
        return start + end;
    }

    private static string WitnessWords(string witness) => witness switch
    {
        nameof(ProcessWitness.Created) => "created in the capture",
        nameof(ProcessWitness.Rundown) => "running at capture start",
        nameof(ProcessWitness.ExitOnly) => "running before their first record",
        nameof(ProcessWitness.ActivityOnly) => "seen only in their own records",
        _ => witness,
    };

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat processes <directory> [--top <n>] [--pid <id>] [--port <n>] [--evidence-policy <name>]");
        ConsoleUi.Line("                 [--rank-by records|bytes-sent|bytes-received|bytes-sent-and-received");
        ConsoleUi.Line("                            |connections-made|connections-accepted]");
        ConsoleUi.Line("                 [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Lists the process instances the session's evidence supports - created, exited,");
        ConsoleUi.Line("      running at capture start, or seen only in their own records - with each one's");
        ConsoleUi.Line("      lifetime, how many records bind to it, the TCP connections it made and accepted as");
        ConsoleUi.Line("      the capture saw them open, and the transport bytes it sent and received. A PID is");
        ConsoleUi.Line("      never an identity on its own: a reused PID is two instances.");
        ConsoleUi.Line("      The list is ranked by the transport bytes each sent and received, the most first,");
        ConsoleUi.Line("      unless --rank-by names another measure: connections-made lists first the instances");
        ConsoleUi.Line("      that connected to the most others, and connections-accepted those the most connected to.");
        ConsoleUi.Line("      What the capture covered, mechanism by mechanism, is said as icat metric says it, so a");
        ConsoleUi.Line("      count of none where the capture saw nothing is not read as no activity.");
        ConsoleUi.Line("      --pid shows each instance of one PID in full: the instance id a process filter");
        ConsoleUi.Line("      takes, its image path and parent, what it sent to and received from each process");
        ConsoleUi.Line($"      at the other end ({TransportRelationIndex.RelationRule}) and the connections it");
        ConsoleUi.Line("      made to each and accepted from each, each other host its connections ran to, by");
        ConsoleUi.Line("      address, the most bytes first, and its RPC call records, whose calls icat operations");
        ConsoleUi.Line("      lists with the process at each one's other end.");
        ConsoleUi.Line("      --port lists the instances that accepted connections at that port of their own, whoever");
        ConsoleUi.Line("      connected - its own machine's processes or other hosts - the most connections first;");
        ConsoleUi.Line("      icat ports lists every port they accepted at.");
    }
}
