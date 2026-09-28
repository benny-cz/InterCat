using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One RPC edge of the graph: two process instances a served call joins, and the call records at its two ends that an
/// interval holds, or all of them.
/// </summary>
internal sealed record RpcPeerEdge(string Key, ProcessInstanceId First, ProcessInstanceId Second, long Records, RelationStrength Strength);

/// <summary>
/// The graph's RPC edges: the process instances a served RPC call joins, drawn when the capture collected ALPC to follow
/// calls through (`contracts/operations-v1.md` §5c). ALPC is the evidence that links two calls, never a second count of
/// them: an edge counts the call records at its two ends, as a TCP edge counts the records at its two ends (§5.1).
/// </summary>
internal static class RpcPeerEdges
{
    public const string KeyPrefix = "rpc:";

    /// <summary>
    /// Whether a generation's capture collected ALPC, the only evidence an RPC edge rests on. A capture that did not reads
    /// nothing for them, so its first view opens no segment on their account.
    /// </summary>
    public static bool Collected(CoverageLedgerV1? ledger) =>
        ledger is not null && ledger.Epochs.Any(epoch => epoch.Collected.Any(descriptor => descriptor.Mechanism == Mechanism.Alpc));

    /// <summary>
    /// The edges between process instances whose two bindings <paramref name="policy"/> admits, each keyed by its instance
    /// pair in a stable display order, with its links' records <paramref name="native"/> holds, or every one of them.
    /// A link is correlated evidence at best, and as weak as the weaker binding of its two calls.
    /// </summary>
    public static IReadOnlyList<RpcPeerEdge> Of(RpcPeerIndex peers, EvidencePolicy policy, TimeRange? native = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        IReadOnlyList<ProcessInstance> instances = peers.Calls.Processes.Instances;
        var edges = new Dictionary<(ProcessInstanceId First, ProcessInstanceId Second), (long Records, RelationStrength Strength)>();
        foreach (RpcCallLink link in peers.Links())
        {
            if (!link.Client.IsAdmittedUnder(policy) || !link.Server.IsAdmittedUnder(policy))
            {
                continue;
            }

            var strength = (RelationStrength)Math.Max(
                (int)RelationStrength.Correlated,
                Math.Max((int)link.Client.Strength, (int)link.Server.Strength));
            if (!SessionOverviewProjector.Admitted(strength, policy))
            {
                continue;
            }

            ProcessInstanceId client = instances[link.Client.Instance].Id;
            ProcessInstanceId server = instances[link.Server.Instance].Id;
            (ProcessInstanceId, ProcessInstanceId) pair = string.CompareOrdinal(client.ToString(), server.ToString()) <= 0
                ? (client, server)
                : (server, client);
            (long records, RelationStrength weakest) = edges.GetValueOrDefault(pair, (0, RelationStrength.Direct));
            edges[pair] = (records + link.Records(native), (RelationStrength)Math.Max((int)weakest, (int)strength));
        }

        return
        [
            .. edges
                .OrderBy(edge => edge.Key.First.ToString(), StringComparer.Ordinal)
                .ThenBy(edge => edge.Key.Second.ToString(), StringComparer.Ordinal)
                .Select(edge => new RpcPeerEdge(
                    KeyOf(edge.Key.First, edge.Key.Second), edge.Key.First, edge.Key.Second, edge.Value.Records, edge.Value.Strength)),
        ];
    }

    public static string KeyOf(ProcessInstanceId first, ProcessInstanceId second) => $"{KeyPrefix}{first}:{second}";
}
