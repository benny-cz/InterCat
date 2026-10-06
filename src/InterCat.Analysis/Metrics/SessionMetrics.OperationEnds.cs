using System.Globalization;
using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>
/// A call's other end on the logical-operations basis (`contracts/metrics-v1.md` §8a, revision 228): the process at the
/// other end of each RPC call under `rpc-call-peer-v1`, read the way a process filter reads a record's (§5). A client
/// call's other end is the process of the server call that served it, and a server call's the process of the client call
/// it served; a call no link reached has an unresolved one, and is disclosed with the reason its own state gives.
/// </summary>
public static partial class SessionMetrics
{
    /// <summary>Whether a logical-operations request reads a call's other end: a filter, grouping or count of peers.</summary>
    private static bool NeedsOtherEnds(MetricRequest request) =>
        request.Focus is { Role: not ProcessRole.Owner }
        || request.Peer is not null
        || request.Between is not null
        || request.Grouping == LaneGrouping.Peer
        || (request.Metric == Metric.Rate ? request.RateNumerator : request.Metric) == Metric.ActivePeers;

    /// <summary>What a request that reads a call's other end asks for, as its refusal names it.</summary>
    private static string OtherEndsAskedFor(MetricRequest request) =>
        request.Focus is { Role: ProcessRole.Participant } ? "participant(P)"
        : request.Peer is not null ? "peer(P,Q)"
        : request.Between is not null ? "between(A,B)"
        : request.Grouping == LaneGrouping.Peer ? "Grouping by peer"
        : "Counting peers";

    /// <summary>
    /// Every call as a process filter reads a row: the process that made it, the process at its other end, and no data
    /// direction, since a call is made at a client and served at a server rather than sent or received.
    /// </summary>
    private sealed class CallEnds
    {
        private CallEnds(RpcPeerIndex peers, SegmentRoles roles, ProcessFilter? focus, IRowFilter? filter)
        {
            Peers = peers;
            Roles = roles;
            Focus = focus;
            Filter = filter;
            Kept = filter?.Mask(roles);
            Undecided = filter is null ? null : filter.Undecided(roles, Kept!);
        }

        public RpcPeerIndex Peers { get; }

        public SegmentRoles Roles { get; }

        /// <summary>The focus a peer grouping or a peer count reads the other end from; null for between(A,B).</summary>
        public ProcessFilter? Focus { get; }

        public IRowFilter? Filter { get; }

        /// <summary>Which calls the process filter keeps, by the index's call order; null without a filter.</summary>
        public bool[]? Kept { get; }

        /// <summary>
        /// Calls the filter leaves out only because their other end is not linked, or linked to a binding the evidence
        /// policy does not admit: the filtered processes could be that end. Disclosed, never added (P6).
        /// </summary>
        public bool[]? Undecided { get; }

        /// <summary>The process at the other end of the call at <paramref name="call"/> from the focus.</summary>
        public ProcessBinding Counterpart(int call) => Focus is { } focus ? focus.Counterpart(Roles, call) : Roles.Peer(call);

        /// <summary>Why a call's other end names no admitted process: its own state when no link reached it.</summary>
        public RpcPeerState? Unlinked(int call) => Peers.StateAt(call) is var state && state != RpcPeerState.Served ? state : null;

        public static CallEnds Of(RpcCallIndex calls, RpcPeerIndex peers, MetricRequest request, ProcessInstanceIndex processes)
        {
            int count = calls.Count;
            var owners = new ProcessBinding[count];
            var others = new ProcessBinding[count];
            for (int call = 0; call < count; call++)
            {
                owners[call] = calls.FactsOf(call).Process;
                int other = peers.OtherAt(call);
                others[call] = other >= 0
                    ? calls.FactsOf(other).Process
                    : ProcessBinding.Unresolved(ProcessBindingReason.CallNotLinked);
            }

            bool[] communicates = new bool[count];
            Array.Fill(communicates, true);
            var roles = new SegmentRoles(owners, others, new sbyte[count], communicates);
            ProcessFilter? focus = request.Focus is { } named
                ? new ProcessFilter(
                    named.Role,
                    IndexOf(processes, named.Instance),
                    request.Peer is { } peer ? IndexOf(processes, peer) : null,
                    request.EvidencePolicy)
                : null;
            IRowFilter? filter = (IRowFilter?)focus ?? (request.Between is { } between
                ? new BetweenFilter(
                    between.First.Select(instance => IndexOf(processes, instance)).ToHashSet(),
                    between.Second.Select(instance => IndexOf(processes, instance)).ToHashSet(),
                    between.Direction,
                    request.EvidencePolicy)
                : null);
            return new(peers, roles, focus, filter);
        }
    }

    /// <summary>
    /// A count of the distinct processes at the other end of the calls a focus keeps (§6.1 on the logical-operations
    /// basis): each call by its first record, its counterpart from the focus, and a call whose other end names no admitted
    /// process an unknown contribution beside the count, which is then a lower bound.
    /// </summary>
    private static MetricResult CallPeers(
        Context context,
        RpcCallIndex calls,
        CallEnds ends,
        CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        var peers = new HashSet<int>();
        var unknown = new Dictionary<ProcessBindingReason, long>();
        var unlinked = new Dictionary<RpcPeerState, long>();
        var disclosed = new Dictionary<ProcessBindingReason, long>();
        long known = 0;
        long outsideInterval = 0;
        long outsideProcess = 0;
        int index = -1;
        foreach (RpcCallOutcome call in calls.Outcomes())
        {
            index++;
            RpcCallMark first = (call.Start ?? call.Stop)!.Value;
            if (request.Interval is { } interval && !interval.Contains(first.NativeTicks))
            {
                outsideInterval++;
                continue;
            }

            if (!ends.Kept![index])
            {
                outsideProcess++;
                if (ends.Undecided![index])
                {
                    Disclose(ends, index, disclosed, unlinked);
                }

                continue;
            }

            ProcessBinding counterpart = ends.Counterpart(index);
            if (counterpart.IsBound && counterpart.IsAdmittedUnder(request.EvidencePolicy))
            {
                known++;
                peers.Add(counterpart.Instance);
                continue;
            }

            ProcessBindingReason reason = counterpart.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : counterpart.Reason;
            unknown[reason] = unknown.GetValueOrDefault(reason) + 1;
            if (ends.Unlinked(index) is { } state)
            {
                unlinked[state] = unlinked.GetValueOrDefault(state) + 1;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        long unknownCount = unknown.Values.Sum();
        MetricResult answer = context.Answer(request) with
        {
            Unit = MeasurementUnit.Count,
            KnownContributions = known,
            UnknownContributions = unknownCount,
            ExcludedByProcessFilter = outsideProcess,
            ExcludedOutsideInterval = outsideInterval,
            UnknownCounterparts = unknown,
            UnresolvedCounterparts = disclosed,
            UnlinkedCalls = unlinked,
            BindingRule = ProcessInstanceIndex.BindingRule,
            OperationRule = RpcCallIndex.OperationRule,
            RelationRule = RpcPeerIndex.PeerRule,
        };
        var caveats = new List<string>
        {
            "A peer is the process at a call's other end from the focus, linked through the call's one ALPC message "
                + "(rpc-call-peer-v1): the server that served a call the focus made, or the client whose call it served. "
                + "Each call is in scope by its first record's reading.",
        };
        if (unknownCount > 0)
        {
            caveats.Add(unknownCount == 1
                ? "1 call in scope has no admitted process at its other end, by the reason given; it could add a peer, so the "
                    + "count is a lower bound beside it (P7)."
                : $"{unknownCount:N0} calls in scope have no admitted process at their other end, each by the reason given; "
                    + "any of them could add a peer, so the count is a lower bound beside them (P7).");
        }

        if (known == 0 && unknownCount > 0)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation =
                    (unknownCount == 1
                        ? "The one call in scope has no linked, admitted process at its other end"
                        : $"None of the {unknownCount:N0} calls in scope has a linked, admitted process at its other end")
                    + ", so no peer is known. That is not a count of zero peers (R21).",
                Unit = null,
                Caveats = caveats,
            };
        }

        return answer with { Value = peers.Count, Caveats = caveats };
    }

    /// <summary>What an answer that read calls' other ends says about them, with how many it could not decide.</summary>
    private static string OtherEndCaveat(long undecided) =>
        "A call's other end is the process of the call it is linked to through its one ALPC message (rpc-call-peer-v1): "
        + "a client call's server, a server call's client; a message id is never a key by itself, and nothing is linked "
        + "by time alone (P8)."
        + (undecided == 0
            ? string.Empty
            : $" {CountText.Of(undecided, "call")} in scope whose other end is not linked, or not admitted, could involve the "
                + $"filtered processes: {CountText.Agree(undecided, "it is", "they are")} disclosed by reason and never counted (P7).");

    /// <summary>
    /// Records one call the filter left out only because its other end is not linked or not admitted: by the reason its
    /// other end names no admitted process, and, when no link reached it, by the reason its own state gives.
    /// </summary>
    private static void Disclose(
        CallEnds ends,
        int call,
        Dictionary<ProcessBindingReason, long> disclosed,
        Dictionary<RpcPeerState, long> unlinked)
    {
        ProcessBinding other = ends.Roles.Peer(call);
        ProcessBindingReason reason = other.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : other.Reason;
        disclosed[reason] = disclosed.GetValueOrDefault(reason) + 1;
        if (ends.Unlinked(call) is { } state)
        {
            unlinked[state] = unlinked.GetValueOrDefault(state) + 1;
        }
    }
}
