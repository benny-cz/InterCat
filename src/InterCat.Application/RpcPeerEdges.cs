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
/// The links between one pair of process instances at one strength, whatever the evidence policy: what a persisted
/// overview keeps so a first view reads no segment (`contracts/overview-index-v1.md` §3), and what an evidence policy then
/// admits or leaves out. The pair is in the overview's stable display order.
/// </summary>
internal sealed record RpcPeerLinkTotal(ProcessInstanceId First, ProcessInstanceId Second, RelationStrength Strength, long Records);

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
    /// </summary>
    public static IReadOnlyList<RpcPeerEdge> Of(RpcPeerIndex peers, EvidencePolicy policy, TimeRange? native = null) =>
        Of(Totals(peers, native), policy);

    /// <summary>
    /// The edges <paramref name="policy"/> admits from link totals: a total at a strength the policy leaves out is not
    /// drawn, and an edge is as weak as the weakest total it admits.
    /// </summary>
    public static IReadOnlyList<RpcPeerEdge> Of(IReadOnlyList<RpcPeerLinkTotal> totals, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(totals);
        return
        [
            .. totals
                .Where(total => SessionOverviewProjector.Admitted(total.Strength, policy))
                .GroupBy(total => (total.First, total.Second))
                .OrderBy(pair => pair.Key.First.ToString(), StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Second.ToString(), StringComparer.Ordinal)
                .Select(pair => new RpcPeerEdge(
                    KeyOf(pair.Key.First, pair.Key.Second),
                    pair.Key.First,
                    pair.Key.Second,
                    pair.Sum(total => total.Records),
                    pair.Max(total => total.Strength))),
        ];
    }

    /// <summary>
    /// Every link's records by instance pair and strength, before any evidence policy: a link is correlated evidence at
    /// best, and as weak as the weaker binding of its two calls; a link one of whose calls binds to no instance joins no
    /// pair. Only links holding a record <paramref name="native"/> holds count, when it is given.
    /// </summary>
    public static IReadOnlyList<RpcPeerLinkTotal> Totals(RpcPeerIndex peers, TimeRange? native = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        IReadOnlyList<ProcessInstance> instances = peers.Calls.Processes.Instances;
        var totals = new Dictionary<(ProcessInstanceId First, ProcessInstanceId Second, RelationStrength Strength), long>();
        foreach (RpcCallLink link in peers.Links())
        {
            if (!link.Client.IsBound || !link.Server.IsBound)
            {
                continue;
            }

            int records = link.Records(native);
            if (records == 0)
            {
                continue;
            }

            var strength = (RelationStrength)Math.Max(
                (int)RelationStrength.Correlated,
                Math.Max((int)link.Client.Strength, (int)link.Server.Strength));
            ProcessInstanceId client = instances[link.Client.Instance].Id;
            ProcessInstanceId server = instances[link.Server.Instance].Id;
            (ProcessInstanceId, ProcessInstanceId) pair = string.CompareOrdinal(client.ToString(), server.ToString()) <= 0
                ? (client, server)
                : (server, client);
            (ProcessInstanceId, ProcessInstanceId, RelationStrength) key = (pair.Item1, pair.Item2, strength);
            totals[key] = totals.GetValueOrDefault(key) + records;
        }

        return
        [
            .. totals
                .OrderBy(total => total.Key.First.ToString(), StringComparer.Ordinal)
                .ThenBy(total => total.Key.Second.ToString(), StringComparer.Ordinal)
                .ThenBy(total => total.Key.Strength)
                .Select(total => new RpcPeerLinkTotal(total.Key.First, total.Key.Second, total.Key.Strength, total.Value)),
        ];
    }

    public static string KeyOf(ProcessInstanceId first, ProcessInstanceId second) => $"{KeyPrefix}{first}:{second}";

    /// <summary>
    /// The pair an RPC relationship's key names: two distinct instances, the first before the second as their "N" forms
    /// order, as <see cref="KeyOf"/> writes them. False for any other key, an RPC channel's or call's among them.
    /// </summary>
    public static bool TryParseKey(string? key, out ProcessInstanceId first, out ProcessInstanceId second)
    {
        first = second = default;
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = key[KeyPrefix.Length..].Split(':');
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out Guid one) || !Guid.TryParseExact(parts[1], "N", out Guid other)
            || one == Guid.Empty || other == Guid.Empty || string.CompareOrdinal(parts[0], parts[1]) >= 0
            || parts[0] != one.ToString("N") || parts[1] != other.ToString("N"))
        {
            return false;
        }

        (first, second) = (new ProcessInstanceId(one), new ProcessInstanceId(other));
        return true;
    }

    /// <summary>
    /// Whether <paramref name="link"/> is one the relationship between <paramref name="first"/> and
    /// <paramref name="second"/> rests on under <paramref name="policy"/>, in either direction: both its calls bind to an
    /// instance, those instances are the pair, and the link's strength - correlated at best, as weak as its weaker
    /// binding - is one the policy admits. It is the same reading <see cref="Totals"/> and <see cref="Of(IReadOnlyList{RpcPeerLinkTotal}, EvidencePolicy)"/>
    /// give the relationship's count.
    /// </summary>
    public static bool Joins(RpcCallLink link, IReadOnlyList<ProcessInstance> instances, ProcessInstanceId first,
        ProcessInstanceId second, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (!link.Client.IsBound || !link.Server.IsBound)
        {
            return false;
        }

        var strength = (RelationStrength)Math.Max(
            (int)RelationStrength.Correlated,
            Math.Max((int)link.Client.Strength, (int)link.Server.Strength));
        ProcessInstanceId client = instances[link.Client.Instance].Id;
        ProcessInstanceId server = instances[link.Server.Instance].Id;
        return SessionOverviewProjector.Admitted(strength, policy)
            && ((client == first && server == second) || (client == second && server == first));
    }
}
