using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>What a client call's other end is under `rpc-call-peer-v1` (`contracts/operations-v1.md` §5c, ADR-034).</summary>
public enum RpcPeerState
{
    /// <summary>Its one send's one receive began a server call of the same interface and procedure.</summary>
    Served = 1,

    /// <summary>The generation holds no ALPC record at all: its capture did not collect ALPC, so nothing is resolved.</summary>
    NoAlpcEvidence = 2,

    /// <summary>The call has no start or no stop, so it has no window to find its send in.</summary>
    NotCompleted = 3,

    /// <summary>No ALPC send on the call's thread between its start and stop: another transport, or a send not delivered.</summary>
    NoSend = 4,

    /// <summary>More than one send on its thread in its window: which one carried the call cannot be told.</summary>
    SeveralSends = 5,

    /// <summary>Its one send carried no message id.</summary>
    NoMessageId = 6,

    /// <summary>No other process received the message after it was sent and before the call stopped.</summary>
    NoReceive = 7,

    /// <summary>More than one receive of the message in another process before the call stopped.</summary>
    SeveralReceives = 8,

    /// <summary>No server call began on the receiving thread within 5 ms of the receive and before the call stopped.</summary>
    NoServerCall = 9,

    /// <summary>One of the two calls carries no interface or no procedure, so the link cannot be checked.</summary>
    CannotCheck = 10,

    /// <summary>The server call it reached carries another interface or procedure: conflicting evidence, not a link.</summary>
    Conflicting = 11,

    /// <summary>Another client call reached the same server call, so neither is linked.</summary>
    ServerCallShared = 12,
}

/// <summary>One client group's calls by what their other end is.</summary>
public sealed record RpcPeerCounts
{
    public required long Calls { get; init; }

    public required long Served { get; init; }

    /// <summary>The calls whose other end is unresolved, by reason; a reason no call has is absent.</summary>
    public required IReadOnlyDictionary<RpcPeerState, long> Unresolved { get; init; }
}

/// <summary>The calls at the other end of one group's calls, by their process binding and interface, with how many.</summary>
public sealed record RpcPeerTally(int ProcessId, ProcessBinding Process, Guid? Interface, long Calls);

/// <summary>
/// The other ends of one generation's RPC calls: each client call linked to the server call that served it through the
/// call's one ALPC send, that message's one receive and the call the receiving thread began (ADR-034), or the reason it
/// is not. It is a derivation over published segments and the calls paired from them, so it is rebuildable (R20).
/// </summary>
public sealed class RpcPeerIndex
{
    /// <summary>The rule's identity. A change to what links, how, or when a link is refused is a new rule (§24).</summary>
    public const string PeerRule = "rpc-call-peer-v1";

    /// <summary>How long after the receive a server call may begin on the receiving thread (ADR-034).</summary>
    public const long ServerStartWindowNanoseconds = 5_000_000;

    private readonly RpcCallIndex calls;
    private readonly RpcPeerState[] states;
    private readonly int[] peers;

    private RpcPeerIndex(RpcCallIndex calls, RpcPeerState[] states, int[] peers, long sends, long receives)
    {
        this.calls = calls;
        this.states = states;
        this.peers = peers;
        AlpcSends = sends;
        AlpcReceives = receives;
    }

    /// <summary>The calls whose other ends these are.</summary>
    public RpcCallIndex Calls => calls;

    public long AlpcSends { get; }

    public long AlpcReceives { get; }

    /// <summary>Whether the generation holds any ALPC send or receive: without one, no call's other end is resolved.</summary>
    public bool CollectedAlpc => AlpcSends + AlpcReceives > 0;

    /// <summary>
    /// Links every completed client call of <paramref name="calls"/> to the server call that served it, from the ALPC
    /// records of <paramref name="segments"/> - the segments the calls were paired from, in the same order - and the
    /// message ids <paramref name="fieldSegments"/> carry for them.
    /// </summary>
    public static RpcPeerIndex Derive(
        RpcCallIndex calls,
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        calls.RequireDerivedFrom(segments);
        Dictionary<RecordAddress, long> messageIds = ReadMessageIds(fieldSegments, cancellationToken);
        var sends = new List<Message>();
        var receives = new List<Message>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectAlpc(segment, messageIds, sends, receives);
        }

        int count = calls.Count;
        var states = new RpcPeerState[count];
        int[] peers = new int[count];
        Array.Fill(peers, -1);
        if (sends.Count + receives.Count == 0)
        {
            for (int call = 0; call < count; call++)
            {
                states[call] = calls.FactsOf(call).Side == RpcCallSide.Client ? RpcPeerState.NoAlpcEvidence : default;
            }

            return new(calls, states, peers, 0, 0);
        }

        // Sends by their thread and reading; receives by message id and reading; server starts by thread and reading.
        Span<Message> sent = CollectionsMarshal.AsSpan(sends);
        sent.Sort(static (left, right) => (left.ProcessId, left.ThreadId, left.Ticks).CompareTo((right.ProcessId, right.ThreadId, right.Ticks)));
        List<Message> identified = [.. receives.Where(receive => receive.HasId)];
        Span<Message> received = CollectionsMarshal.AsSpan(identified);
        received.Sort(static (left, right) => (left.Id, left.Ticks).CompareTo((right.Id, right.Ticks)));
        List<ServerStart> servers = [];
        for (int call = 0; call < count; call++)
        {
            RpcCallIndex.PeerFacts facts = calls.FactsOf(call);
            if (facts.Side == RpcCallSide.Server && facts.StartSegment >= 0)
            {
                servers.Add(new(facts.ProcessId, ThreadOf(segments, facts), facts.StartTicks, call));
            }
        }

        Span<ServerStart> started = CollectionsMarshal.AsSpan(servers);
        started.Sort(static (left, right) => (left.ProcessId, left.ThreadId, left.Ticks, left.Call).CompareTo((right.ProcessId, right.ThreadId, right.Ticks, right.Call)));
        long window = (long)((Int128)ServerStartWindowNanoseconds * calls.Clock.TicksPerSecond / 1_000_000_000);
        int[] claims = new int[count];
        for (int call = 0; call < count; call++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RpcCallIndex.PeerFacts client = calls.FactsOf(call);
            if (client.Side != RpcCallSide.Client)
            {
                continue;
            }

            (states[call], peers[call]) = Resolve(calls, segments, client, sent, received, started, window);
            if (peers[call] >= 0)
            {
                claims[peers[call]]++;
            }
        }

        // A server call two client calls reached is neither's: which one it served cannot be told.
        for (int call = 0; call < count; call++)
        {
            if (peers[call] >= 0 && calls.FactsOf(call).Side == RpcCallSide.Client && claims[peers[call]] > 1)
            {
                states[call] = RpcPeerState.ServerCallShared;
                peers[call] = -1;
            }
        }

        for (int call = 0; call < count; call++)
        {
            if (peers[call] >= 0 && calls.FactsOf(call).Side == RpcCallSide.Client && states[call] == RpcPeerState.Served)
            {
                peers[peers[call]] = call;
            }
        }

        return new(calls, states, peers, sends.Count, receives.Count);
    }

    /// <summary>
    /// The group's client calls by what their other end is, or only those at <paramref name="positions"/> in its reading
    /// order. A server group's calls have none of their own.
    /// </summary>
    public RpcPeerCounts CountsOf(RpcCallGroup group, IEnumerable<int>? positions = null)
    {
        long served = 0;
        long counted = 0;
        var unresolved = new Dictionary<RpcPeerState, long>();
        foreach (int call in CallsOf(group, positions))
        {
            counted++;
            if (states[call] == RpcPeerState.Served)
            {
                served++;
            }
            else if (states[call] != default)
            {
                unresolved[states[call]] = unresolved.GetValueOrDefault(states[call]) + 1;
            }
        }

        return new() { Calls = counted, Served = served, Unresolved = unresolved };
    }

    /// <summary>
    /// The calls at the other end of <paramref name="group"/>'s calls, or of those at <paramref name="positions"/>, by
    /// their process and interface, the most first: for a client group, who served it; for a server group, who called it.
    /// </summary>
    public IReadOnlyList<RpcPeerTally> PeersOf(RpcCallGroup group, IEnumerable<int>? positions = null)
    {
        var tally = new Dictionary<(int ProcessId, ProcessBinding Process, Guid? Interface), long>();
        foreach (int call in CallsOf(group, positions))
        {
            // Only a served client call and the server call it reached hold each other.
            if (peers[call] >= 0)
            {
                RpcCallIndex.PeerFacts other = calls.FactsOf(peers[call]);
                (int, ProcessBinding, Guid?) key = (other.ProcessId, other.Process, calls.InterfaceOf(peers[call]));
                tally[key] = tally.GetValueOrDefault(key) + 1;
            }
        }

        return
        [
            .. tally
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.ProcessId)
                .Select(entry => new RpcPeerTally(entry.Key.ProcessId, entry.Key.Process, entry.Key.Interface, entry.Value)),
        ];
    }

    /// <summary>
    /// What the call at <paramref name="position"/> in <paramref name="group"/> reached, and its other call. A server call
    /// has no state of its own; its other call is the client call that reached it, when one did.
    /// </summary>
    public (RpcPeerState State, RpcCall? Other) PeerOf(RpcCallGroup group, int position, IReadOnlyList<SegmentReaderV1> segments)
    {
        int call = At(group, position);
        return (states[call], peers[call] < 0 ? null : calls.DescribeAt(peers[call], segments));
    }

    /// <summary>Where the other call of the call at <paramref name="position"/> is: its group and place; null when unlinked.</summary>
    public (RpcCallGroup Group, int Position)? OtherCallOf(RpcCallGroup group, int position)
    {
        int call = At(group, position);
        return peers[call] < 0 ? null : calls.GroupAt(peers[call]);
    }

    private int At(RpcCallGroup group, int position)
    {
        (int first, int count) = calls.RangeOf(group);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, count);
        return first + position;
    }

    private IEnumerable<int> CallsOf(RpcCallGroup group, IEnumerable<int>? positions)
    {
        (int first, int count) = calls.RangeOf(group);
        if (positions is null)
        {
            return Enumerable.Range(first, count);
        }

        return positions.Select(position => position >= 0 && position < count
            ? first + position
            : throw new ArgumentOutOfRangeException(nameof(positions), position, "No call of the group is at this position."));
    }

    private static (RpcPeerState State, int Server) Resolve(
        RpcCallIndex calls,
        IReadOnlyList<SegmentReaderV1> segments,
        RpcCallIndex.PeerFacts client,
        ReadOnlySpan<Message> sent,
        ReadOnlySpan<Message> received,
        ReadOnlySpan<ServerStart> started,
        long window)
    {
        if (client.State != RpcCallState.Completed)
        {
            return (RpcPeerState.NotCompleted, -1);
        }

        // Exactly one send on the call's own thread, read after its start and before its stop.
        int thread = ThreadOf(segments, client);
        int from = LowerBound(sent, client.ProcessId, thread, client.StartTicks + 1);
        int to = LowerBound(sent, client.ProcessId, thread, client.StopTicks);
        if (to - from != 1)
        {
            return (to == from ? RpcPeerState.NoSend : RpcPeerState.SeveralSends, -1);
        }

        Message send = sent[from];
        if (!send.HasId)
        {
            return (RpcPeerState.NoMessageId, -1);
        }

        // Exactly one receive of that id in another process, read after the send and before the call's stop.
        Message receive = default;
        int receipts = 0;
        for (int at = LowerBound(received, send.Id, send.Ticks + 1); at < received.Length && received[at].Id == send.Id && received[at].Ticks < client.StopTicks; at++)
        {
            if (received[at].ProcessId != client.ProcessId)
            {
                receive = received[at];
                receipts++;
            }
        }

        if (receipts != 1)
        {
            return (receipts == 0 ? RpcPeerState.NoReceive : RpcPeerState.SeveralReceives, -1);
        }

        // The first server call the receiving thread began within the window, before the client call stopped.
        int first = LowerBound(started, receive.ProcessId, receive.ThreadId, receive.Ticks);
        if (first >= started.Length
            || started[first].ProcessId != receive.ProcessId
            || started[first].ThreadId != receive.ThreadId
            || started[first].Ticks - receive.Ticks > window
            || started[first].Ticks >= client.StopTicks)
        {
            return (RpcPeerState.NoServerCall, -1);
        }

        int server = started[first].Call;
        RpcCallIndex.PeerFacts facts = calls.FactsOf(server);
        if (client.Interface < 0 || facts.Interface < 0 || client.Procedure is null || facts.Procedure is null)
        {
            return (RpcPeerState.CannotCheck, -1);
        }

        return client.Interface == facts.Interface && client.Procedure == facts.Procedure
            ? (RpcPeerState.Served, server)
            : (RpcPeerState.Conflicting, -1);
    }

    private static int ThreadOf(IReadOnlyList<SegmentReaderV1> segments, RpcCallIndex.PeerFacts facts) =>
        (int)(segments[facts.StartSegment].Slice(SegmentColumnId.HeaderThreadId).UnsignedAt(facts.StartRow) ?? 0);

    private static int LowerBound(ReadOnlySpan<Message> sorted, int processId, int threadId, long ticks)
    {
        int low = 0;
        int high = sorted.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if ((sorted[middle].ProcessId, sorted[middle].ThreadId, sorted[middle].Ticks).CompareTo((processId, threadId, ticks)) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int LowerBound(ReadOnlySpan<Message> sorted, long id, long ticks)
    {
        int low = 0;
        int high = sorted.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if ((sorted[middle].Id, sorted[middle].Ticks).CompareTo((id, ticks)) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int LowerBound(ReadOnlySpan<ServerStart> sorted, int processId, int threadId, long ticks)
    {
        int low = 0;
        int high = sorted.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if ((sorted[middle].ProcessId, sorted[middle].ThreadId, sorted[middle].Ticks).CompareTo((processId, threadId, ticks)) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void CollectAlpc(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, long> messageIds,
        List<Message> sends,
        List<Message> receives)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        bool opened = false;
        SegmentColumnSlice kinds = default;
        SegmentColumnSlice ticks = default;
        SegmentColumnSlice processes = default;
        SegmentColumnSlice threads = default;
        SegmentColumnSlice streams = default;
        SegmentColumnSlice epochs = default;
        SegmentColumnSlice ordinals = default;
        SegmentColumnSlice factHigh = default;
        SegmentColumnSlice factLow = default;
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Alpc)
            {
                continue;
            }

            if (!opened)
            {
                kinds = segment.Slice(SegmentColumnId.ObservationKind);
                ticks = segment.Slice(SegmentColumnId.NativeTicks);
                processes = segment.Slice(SegmentColumnId.HeaderProcessId);
                threads = segment.Slice(SegmentColumnId.HeaderThreadId);
                streams = segment.Slice(SegmentColumnId.RawStreamId);
                epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                opened = true;
            }

            var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;
            if (kind is not (ObservationKind.Send or ObservationKind.Receive))
            {
                continue;
            }

            var address = new RecordAddress(
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
            bool hasId = messageIds.TryGetValue(address, out long id);
            var message = new Message(
                (int)(processes.UnsignedAt(row) ?? 0),
                (int)(threads.UnsignedAt(row) ?? 0),
                ticks.SignedAt(row)!.Value,
                id,
                hasId);
            (kind == ObservationKind.Send ? sends : receives).Add(message);
        }
    }

    /// <summary>
    /// Every ALPC record's message id, by the record it belongs to. A field repeated across source-field segments refuses
    /// the derivation, as it refuses a call's procedure (`source-fields-v1`).
    /// </summary>
    private static Dictionary<RecordAddress, long> ReadMessageIds(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken)
    {
        var ids = new Dictionary<RecordAddress, long>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            SegmentColumnSlice streams = default;
            SegmentColumnSlice epochs = default;
            SegmentColumnSlice ordinals = default;
            SegmentColumnSlice factHigh = default;
            SegmentColumnSlice factLow = default;
            SegmentColumnSlice values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((SourceField)(ushort)codes.UnsignedAt(row)!.Value != SourceField.AlpcMessageId)
                {
                    continue;
                }

                if (!opened)
                {
                    streams = segment.Slice(SegmentColumnId.RawStreamId);
                    epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                    factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                if (values.SignedAt(row) is not { } value)
                {
                    continue;
                }

                var address = new RecordAddress(
                    (uint)streams.UnsignedAt(row)!.Value,
                    (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
                if (!ids.TryAdd(address, value))
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries its ALPC message id in more than one source-field segment; a "
                        + "duplicated field could change which call it links.");
                }
            }
        }

        return ids;
    }

    /// <summary>A record's raw locator and fact key: what a source field names its observation by.</summary>
    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    /// <summary>One ALPC send or receive: the thread that made it, its reading and its message id.</summary>
    private readonly record struct Message(int ProcessId, int ThreadId, long Ticks, long Id, bool HasId);

    /// <summary>One server call's start: the thread that began it, its reading, and the call.</summary>
    private readonly record struct ServerStart(int ProcessId, int ThreadId, long Ticks, int Call);
}
