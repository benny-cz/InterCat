using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class RpcPeerIndex
{
    /// <summary>
    /// What an interval release owes the links between the calls of <paramref name="calls"/> (ADR-043,
    /// `operations-v1` §5c). A completed client call that stays keeps what its other end is read from: every ALPC send on
    /// its thread in its window, every receive of its one send's message id read after the send and before the call
    /// stopped, and the first server call the receiving thread began after the receive, whole. A server call that stays
    /// keeps every client call that reached it, so one that two calls reached stays neither's. Each link, and each reason a
    /// call has none, is then read from what it was read from before.
    /// </summary>
    internal static ReleaseDependencies ReleaseDependenciesOf(
        RpcCallIndex calls,
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        Func<ulong, bool> released,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        calls.RequireDerivedFrom(segments);
        var dependencies = new ReleaseDependencies(released);
        Evidence evidence = Read(calls, segments, fieldSegments, cancellationToken);
        if (evidence.Sends + evidence.Receives == 0)
        {
            return dependencies;
        }

        var reachedBy = new Dictionary<int, List<int>>();
        for (int call = 0; call < calls.Count; call++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RpcCallIndex.PeerFacts client = calls.FactsOf(call);
            if (client.Side != RpcCallSide.Client || client.State != RpcCallState.Completed)
            {
                continue;
            }

            var read = new List<RowAddress>();
            int thread = ThreadOf(segments, client);
            int from = LowerBound(evidence.Sent, client.ProcessId, thread, client.StartTicks + 1);
            int to = LowerBound(evidence.Sent, client.ProcessId, thread, client.StopTicks);
            for (int at = from; at < to; at++)
            {
                read.Add(RowOf(segments, evidence.Sent[at]));
            }

            if (to - from == 1 && evidence.Sent[from] is { HasId: true } send)
            {
                Message receive = default;
                int receipts = 0;
                for (int at = LowerBound(evidence.Received, send.Id, send.Ticks + 1);
                    at < evidence.Received.Length && evidence.Received[at].Id == send.Id && evidence.Received[at].Ticks < client.StopTicks;
                    at++)
                {
                    read.Add(RowOf(segments, evidence.Received[at]));
                    if (evidence.Received[at].ProcessId != client.ProcessId)
                    {
                        receive = evidence.Received[at];
                        receipts++;
                    }
                }

                int first = receipts == 1 ? LowerBound(evidence.Started, receive.ProcessId, receive.ThreadId, receive.Ticks) : -1;
                if (first >= 0 && first < evidence.Started.Length
                    && evidence.Started[first].ProcessId == receive.ProcessId
                    && evidence.Started[first].ThreadId == receive.ThreadId)
                {
                    read.AddRange(RowsOf(calls, segments, evidence.Started[first].Call));
                }
            }

            (RpcPeerState state, int server) = Resolve(calls, segments, client, evidence.Sent, evidence.Received, evidence.Started, evidence.Window);
            if (state == RpcPeerState.Served)
            {
                if (!reachedBy.TryGetValue(server, out List<int>? clients))
                {
                    reachedBy[server] = clients = [];
                }

                clients.Add(call);
            }

            dependencies.Add([.. RowsOf(calls, segments, call)], read);
        }

        foreach ((int server, List<int> clients) in reachedBy)
        {
            dependencies.Add([.. RowsOf(calls, segments, server)], [.. clients.SelectMany(client => RowsOf(calls, segments, client))]);
        }

        return dependencies;
    }

    private static IEnumerable<RowAddress> RowsOf(RpcCallIndex calls, IReadOnlyList<SegmentReaderV1> segments, int call) =>
        calls.RowsAt(call).Select(record => RowAddress.At(segments[record.Segment], record.Row));

    private static RowAddress RowOf(IReadOnlyList<SegmentReaderV1> segments, Message message) =>
        RowAddress.At(segments[message.Segment], message.Row);
}
