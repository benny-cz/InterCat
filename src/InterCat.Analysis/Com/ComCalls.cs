using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>Which end of a COM call a record is (`contracts/com-calls-v1.md` §2).</summary>
public enum ComCallSide
{
    /// <summary>The call as the process that made it records it: COM-Perf's client start and stop.</summary>
    Client = 1,

    /// <summary>The call as the process that served it records it: COM-Perf's server start and stop.</summary>
    Server = 2,
}

/// <summary>How a COM call's records paired under `com-call-link-v1` (§3).</summary>
public enum ComCallState
{
    /// <summary>A start and the next stop of its process, side and trace number.</summary>
    Completed = 1,

    /// <summary>A start no stop of its key followed by the end of the evidence: censored, never failed.</summary>
    OpenAtCaptureEnd = 2,

    /// <summary>A stop no open start of its key preceded: its start is not in the evidence.</summary>
    StartNotObserved = 3,

    /// <summary>A record carrying no trace number, which pairs with nothing.</summary>
    NoTraceNumber = 4,

    /// <summary>A record of a key whose number was reused before its stop: which stop is whose cannot be told.</summary>
    Ambiguous = 5,
}

/// <summary>What carries a COM call: the RPC call it rides, or why none is named (§4, ADR-079's second decision).</summary>
public enum ComCarrierState
{
    /// <summary>One RPC call carries it, on its interface with its method as the procedure.</summary>
    Carried = 1,

    /// <summary>A client call without its start, or a server call without both ends: nothing to join it by.</summary>
    NotCompleted = 2,

    /// <summary>A client call whose start carries no activity id.</summary>
    NoActivityId = 3,

    /// <summary>No RPC call of its activity at the client, or none around it on its thread at the server.</summary>
    NoCarrier = 4,

    /// <summary>More than one such RPC call: which carried it cannot be told.</summary>
    SeveralCarriers = 5,

    /// <summary>The one RPC call names another interface or procedure: conflicting evidence, never a carrier.</summary>
    Conflicting = 6,

    /// <summary>The COM call or its RPC call names no interface or no method, so the join cannot be checked.</summary>
    CannotCheck = 7,
}

/// <summary>Whether a COM call's two ends are linked under `com-call-link-v1` (§5, ADR-079's third decision).</summary>
public enum ComLinkState
{
    /// <summary>The client call and the one server call carrying its activity are one call's two ends.</summary>
    Linked = 1,

    /// <summary>A client call whose start carries no activity id, or which has no start.</summary>
    NoActivityId = 2,

    /// <summary>No server call carries the client call's activity id: its server may not be in the capture.</summary>
    NoServedCall = 3,

    /// <summary>More than one server call carries it: which served this call cannot be told.</summary>
    SeveralServedCalls = 4,

    /// <summary>
    /// The server call carrying it is in the client's own process: a call across an apartment, that process's own work and
    /// never IPC (ADR-077's fifth decision), so never a link.
    /// </summary>
    WithinOneProcess = 5,

    /// <summary>The two do not name each other's processes at their other ends.</summary>
    PeerMismatch = 6,

    /// <summary>The server call names another interface or method: conflicting evidence, never a link.</summary>
    Conflicting = 7,

    /// <summary>The client call or the server call has no start or no stop, so one cannot be placed inside the other.</summary>
    NotCompleted = 8,

    /// <summary>The server call does not lie inside the client's.</summary>
    NotInside = 9,

    /// <summary>Another client call carries the same activity id, so neither is linked.</summary>
    ServerCallShared = 10,

    /// <summary>One of the two names no interface or no method, so the link cannot be checked.</summary>
    CannotCheck = 11,

    /// <summary>A server call no linked client call reached: its client may be remote or not in the capture.</summary>
    NotReached = 12,
}

/// <summary>
/// One COM call as `com-call-link-v1` reads it (`contracts/com-calls-v1.md`): its end, its process and the process it names at
/// its other end, its trace number, interface, method and activity, the thread and readings of its records, its HRESULT,
/// the RPC call carrying it and the call at its other end.
/// </summary>
public sealed record ComCall
{
    public required ComCallSide Side { get; init; }

    public required ComCallState State { get; init; }

    /// <summary>The process its records belong to: the one that raised them (`process-binding-v5`).</summary>
    public required int ProcessId { get; init; }

    /// <summary>The process its start names at its other end, never its own owner; null without a start or where it named none.</summary>
    public int? PeerProcessId { get; init; }

    public long? TraceNumber { get; init; }

    public Guid? Interface { get; init; }

    public long? Method { get; init; }

    /// <summary>Its start's activity id, or its stop's when it has no start.</summary>
    public Guid? ActivityId { get; init; }

    /// <summary>The thread that raised its start, or its stop when it has no start.</summary>
    public int Thread { get; init; }

    /// <summary>The native reading of its start; null without one.</summary>
    public long? Start { get; init; }

    /// <summary>The native reading of its stop; null without one.</summary>
    public long? Stop { get; init; }

    /// <summary>The HRESULT its stop reported: the method's answer; null without a stop.</summary>
    public long? Hresult { get; init; }

    public ComCarrierState Carrier { get; init; }

    /// <summary>The RPC call carrying it, as its position in the RPC call index; -1 where none.</summary>
    public int CarrierCall { get; init; } = -1;

    public ComLinkState Link { get; init; }

    /// <summary>The call at its other end, as its position in <see cref="ComCallIndex.Calls"/>; -1 where none.</summary>
    public int OtherEnd { get; init; } = -1;
}

/// <summary>
/// A client RPC call and the RPC call that served it, related through the COM call each carries (ADR-079's third decision):
/// the four calls' positions, the COM calls' in <see cref="ComCallIndex.Calls"/> and the RPC calls' in the RPC call index.
/// </summary>
public readonly record struct ComRpcLink(int ClientCall, int ServerCall, int ClientRpcCall, int ServerRpcCall);

/// <summary>
/// One generation's COM calls (`contracts/com-calls-v1.md`, ADR-079): COM-Perf's records paired into calls by process, side and
/// trace number; each joined to the RPC call that carries it - at the client the RPC call of its activity id, at the server the
/// one around it on its thread; and each client call linked to the server call carrying its activity id, which relates the
/// RPC call that carried it to the RPC call that served it. A COM call is never an operation of its own (ADR-079's first
/// decision): it annotates the RPC call that carries it. It is a derivation over published segments, so it is rebuildable
/// (R20).
/// </summary>
public sealed class ComCallIndex
{
    /// <summary>The rule's identity. A change to what pairs, what carries, what links, or when either is refused is a new rule (§24).</summary>
    public const string LinkRule = "com-call-link-v1";

    private readonly ComCall[] calls;
    private readonly ComRpcLink[] links;

    private ComCallIndex(RpcCallIndex rpcCalls, ComCall[] calls, ComRpcLink[] links, long otherRecords)
    {
        RpcCalls = rpcCalls;
        this.calls = calls;
        this.links = links;
        OtherComRecords = otherRecords;
    }

    /// <summary>The RPC calls these COM calls are joined to.</summary>
    public RpcCallIndex RpcCalls { get; }

    /// <summary>The generation's COM calls, in the canonical order of their first records.</summary>
    public IReadOnlyList<ComCall> Calls => calls;

    /// <summary>The client RPC calls related to the RPC calls that served them through the COM calls they carry.</summary>
    public IReadOnlyList<ComRpcLink> RpcLinks => links;

    /// <summary>The generation's COM call records that are no call record: neither a start nor a stop, or of no process.</summary>
    public long OtherComRecords { get; }

    /// <summary>
    /// The RPC call carrying the COM call at <paramref name="call"/>, described from <paramref name="segments"/> - the segments
    /// the calls were paired from; null where none carries it.
    /// </summary>
    public RpcCall? CarrierOf(int call, IReadOnlyList<SegmentReaderV1> segments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(call);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(call, calls.Length);
        return calls[call].CarrierCall < 0 ? null : RpcCalls.DescribeAt(calls[call].CarrierCall, segments);
    }

    /// <summary>
    /// Pairs the COM call records of <paramref name="segments"/> - the segments <paramref name="rpcCalls"/> were paired from, in
    /// the same order - with the trace numbers, other ends and methods <paramref name="fieldSegments"/> carry for them, joins
    /// each to the RPC call carrying it, and links each client call to the server call that served it.
    /// </summary>
    public static ComCallIndex Derive(
        RpcCallIndex rpcCalls,
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rpcCalls);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        rpcCalls.RequireDerivedFrom(segments);

        Dictionary<RecordAddress, Carried> fields = ReadFields(fieldSegments, cancellationToken);
        var records = new List<Record>();
        long other = 0;
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            other += Collect(segments[position], fields, records);
        }

        records.Sort(static (left, right) => left.Ticks != right.Ticks ? left.Ticks.CompareTo(right.Ticks) : left.Address.CompareTo(right.Address));
        List<ComCall> paired = Pair(records);
        Carry(paired, rpcCalls, segments);
        ComRpcLink[] links = LinkEnds(paired);
        return new(rpcCalls, [.. paired], links, other);
    }

    // Pairs records in canonical order by their key - process, side and trace number - as an RPC call's are by activity id
    // (contracts/operations-v1.md §3): a start opens a call and the next stop closes it; a start while its key is open makes
    // every record of the key ambiguous until as many stops as starts have been read; a stop of no open key is a call whose
    // start is not in the evidence; a record with no trace number is a call of its own.
    private static List<ComCall> Pair(List<Record> records)
    {
        var calls = new List<ComCall>(records.Count / 2 + 1);
        var open = new Dictionary<(int Process, ComCallSide Side, long Trace), KeyRun>();
        foreach (Record record in records)
        {
            if (record.Trace is not { } trace)
            {
                calls.Add(Describe(record, record.IsStart ? record : null, record.IsStart ? null : record, ComCallState.NoTraceNumber));
                continue;
            }

            var key = (record.ProcessId, record.Side, trace);
            open.TryGetValue(key, out KeyRun run);
            if (record.IsStart)
            {
                if (run.Starts == 0)
                {
                    open[key] = new(calls.Count, 1, false);
                    calls.Add(Describe(record, record, null, ComCallState.OpenAtCaptureEnd));
                }
                else
                {
                    // The number was used again before its stop: the open start, this one and as many stops as starts are
                    // ambiguous.
                    if (!run.Ambiguous)
                    {
                        calls[run.Call] = calls[run.Call] with { State = ComCallState.Ambiguous };
                    }

                    calls.Add(Describe(record, record, null, ComCallState.Ambiguous));
                    open[key] = new(run.Call, run.Starts + 1, true);
                }

                continue;
            }

            if (run.Starts == 0)
            {
                calls.Add(Describe(record, null, record, ComCallState.StartNotObserved));
            }
            else if (!run.Ambiguous)
            {
                ComCall started = calls[run.Call];
                calls[run.Call] = started with { State = ComCallState.Completed, Stop = record.Ticks, Hresult = record.Status };
                open.Remove(key);
            }
            else
            {
                calls.Add(Describe(record, null, record, ComCallState.Ambiguous));
                if (run.Starts == 1)
                {
                    open.Remove(key);
                }
                else
                {
                    open[key] = run with { Starts = run.Starts - 1 };
                }
            }
        }

        return calls;
    }

    private static ComCall Describe(Record first, Record? start, Record? stop, ComCallState state) => new()
    {
        Side = first.Side,
        State = state,
        ProcessId = first.ProcessId,
        PeerProcessId = start?.Peer,
        TraceNumber = first.Trace,
        Interface = start?.Interface,
        Method = start?.Method,
        ActivityId = first.Activity,
        Thread = first.Thread,
        Start = start?.Ticks,
        Stop = stop?.Ticks,
        Hresult = stop?.Status,
    };

    // Joins each COM call to the RPC call carrying it (§4): at the client the one RPC client call of its process sharing its
    // start's activity id; at the server the one RPC server call of its process on its thread whose span holds it.
    private static void Carry(List<ComCall> calls, RpcCallIndex rpcCalls, IReadOnlyList<SegmentReaderV1> segments)
    {
        var clientCarriers = new Dictionary<(int Process, Guid Activity), List<int>>();
        var serverCarriers = new Dictionary<(int Process, int Thread), List<int>>();

        // The RPC calls' starts by the segment each was read in, so a segment's columns are opened once.
        var starts = new List<(int Call, int Row)>?[segments.Count];
        for (int call = 0; call < rpcCalls.Count; call++)
        {
            RpcCallIndex.PeerFacts facts = rpcCalls.FactsOf(call);
            if (facts.StartSegment >= 0)
            {
                (starts[facts.StartSegment] ??= []).Add((call, facts.StartRow));
            }
        }

        for (int position = 0; position < segments.Count; position++)
        {
            if (starts[position] is not { } read)
            {
                continue;
            }

            SegmentColumnSlice activities = segments[position].Slice(SegmentColumnId.ActivityId);
            SegmentColumnSlice threads = segments[position].Slice(SegmentColumnId.HeaderThreadId);
            foreach ((int call, int row) in read)
            {
                RpcCallIndex.PeerFacts facts = rpcCalls.FactsOf(call);
                if (facts.Side == RpcCallSide.Client)
                {
                    if (activities.GuidAt(row) is { } activity)
                    {
                        Add(clientCarriers, (facts.ProcessId, activity), call);
                    }
                }
                else if (facts.HasStop)
                {
                    int thread = facts.Thread >= 0 ? facts.Thread : (int)(threads.UnsignedAt(row) ?? 0);
                    Add(serverCarriers, (facts.ProcessId, thread), call);
                }
            }
        }

        for (int index = 0; index < calls.Count; index++)
        {
            ComCall call = calls[index];
            (ComCarrierState state, int carrier) = call.Side == ComCallSide.Client
                ? ClientCarrier(call, clientCarriers, rpcCalls)
                : ServerCarrier(call, serverCarriers, rpcCalls);
            calls[index] = call with { Carrier = state, CarrierCall = carrier };
        }
    }

    private static (ComCarrierState State, int Carrier) ClientCarrier(
        ComCall call,
        Dictionary<(int Process, Guid Activity), List<int>> carriers,
        RpcCallIndex rpcCalls)
    {
        if (call.Start is null)
        {
            return (ComCarrierState.NotCompleted, -1);
        }

        if (call.ActivityId is not { } activity)
        {
            return (ComCarrierState.NoActivityId, -1);
        }

        return carriers.TryGetValue((call.ProcessId, activity), out List<int>? found) ? Checked(call, found, rpcCalls) : (ComCarrierState.NoCarrier, -1);
    }

    private static (ComCarrierState State, int Carrier) ServerCarrier(
        ComCall call,
        Dictionary<(int Process, int Thread), List<int>> carriers,
        RpcCallIndex rpcCalls)
    {
        if (call.State != ComCallState.Completed || call.Start is not { } start || call.Stop is not { } stop)
        {
            return (ComCarrierState.NotCompleted, -1);
        }

        List<int> around = carriers.TryGetValue((call.ProcessId, call.Thread), out List<int>? onThread)
            ? [.. onThread.Where(carrier => rpcCalls.FactsOf(carrier).StartTicks <= start && rpcCalls.FactsOf(carrier).StopTicks >= stop)]
            : [];
        return around.Count == 0 ? (ComCarrierState.NoCarrier, -1) : Checked(call, around, rpcCalls);
    }

    // The one candidate carries the call only on its interface with its method as the procedure (ADR-079's second decision).
    private static (ComCarrierState State, int Carrier) Checked(ComCall call, List<int> candidates, RpcCallIndex rpcCalls)
    {
        if (candidates.Count > 1)
        {
            return (ComCarrierState.SeveralCarriers, -1);
        }

        int carrier = candidates[0];
        Guid? rpcInterface = rpcCalls.InterfaceOf(carrier);
        long? procedure = rpcCalls.FactsOf(carrier).Procedure;
        if (call.Interface is null || call.Method is null || rpcInterface is null || procedure is null)
        {
            return (ComCarrierState.CannotCheck, -1);
        }

        return rpcInterface == call.Interface && procedure == call.Method ? (ComCarrierState.Carried, carrier) : (ComCarrierState.Conflicting, -1);
    }

    // Links each client call to the server call carrying its activity id (§5), and through their carriers the client's RPC
    // call to the RPC call that served it.
    private static ComRpcLink[] LinkEnds(List<ComCall> calls)
    {
        var served = new Dictionary<Guid, List<int>>();
        var made = new Dictionary<Guid, int>();
        for (int index = 0; index < calls.Count; index++)
        {
            ComCall call = calls[index];
            if (call.Start is not null && call.ActivityId is { } activity)
            {
                if (call.Side == ComCallSide.Server)
                {
                    Add(served, activity, index);
                }
                else
                {
                    made[activity] = made.GetValueOrDefault(activity) + 1;
                }
            }
        }

        var links = new List<ComRpcLink>();
        var reached = new Dictionary<int, int>();
        for (int index = 0; index < calls.Count; index++)
        {
            ComCall call = calls[index];
            if (call.Side != ComCallSide.Client)
            {
                continue;
            }

            (ComLinkState state, int other) = LinkOf(call, calls, served, made);
            calls[index] = call with { Link = state, OtherEnd = other };
            if (state == ComLinkState.Linked)
            {
                calls[other] = calls[other] with { Link = ComLinkState.Linked, OtherEnd = index };
                if (call.Carrier == ComCarrierState.Carried && calls[other].Carrier == ComCarrierState.Carried)
                {
                    links.Add(new(index, other, call.CarrierCall, calls[other].CarrierCall));
                    reached[calls[other].CarrierCall] = reached.GetValueOrDefault(calls[other].CarrierCall) + 1;
                }
            }
        }

        for (int index = 0; index < calls.Count; index++)
        {
            if (calls[index] is { Side: ComCallSide.Server, Link: not ComLinkState.Linked })
            {
                calls[index] = calls[index] with { Link = ComLinkState.NotReached, OtherEnd = -1 };
            }
        }

        // A server RPC call two links reach is related to neither: which one it served cannot be told.
        return [.. links.Where(link => reached[link.ServerRpcCall] == 1)];
    }

    private static (ComLinkState State, int Other) LinkOf(ComCall call, List<ComCall> calls, Dictionary<Guid, List<int>> served, Dictionary<Guid, int> made)
    {
        if (call.Start is null || call.ActivityId is not { } activity)
        {
            return (ComLinkState.NoActivityId, -1);
        }

        if (!served.TryGetValue(activity, out List<int>? servers))
        {
            return (ComLinkState.NoServedCall, -1);
        }

        if (servers.Count > 1)
        {
            return (ComLinkState.SeveralServedCalls, -1);
        }

        int other = servers[0];
        ComCall server = calls[other];
        if (server.ProcessId == call.ProcessId)
        {
            return (ComLinkState.WithinOneProcess, -1);
        }

        if (made[activity] > 1)
        {
            return (ComLinkState.ServerCallShared, -1);
        }

        if (call.PeerProcessId != server.ProcessId || server.PeerProcessId != call.ProcessId)
        {
            return (ComLinkState.PeerMismatch, -1);
        }

        if (call.Interface is null || call.Method is null || server.Interface is null || server.Method is null)
        {
            return (ComLinkState.CannotCheck, -1);
        }

        if (call.Interface != server.Interface || call.Method != server.Method)
        {
            return (ComLinkState.Conflicting, -1);
        }

        if (call.State != ComCallState.Completed || server.State != ComCallState.Completed)
        {
            return (ComLinkState.NotCompleted, -1);
        }

        return server.Start >= call.Start && server.Stop <= call.Stop ? (ComLinkState.Linked, other) : (ComLinkState.NotInside, -1);
    }

    private static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, int value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<int>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(value);
    }

    // The trace numbers, other ends and methods source-field segments carry, by the record they belong to.
    private static Dictionary<RecordAddress, Carried> ReadFields(IReadOnlyList<SegmentReaderV1> fieldSegments, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<RecordAddress, Carried>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            Locator locator = default;
            SegmentColumnSlice values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.ComCallTraceId or SourceField.PeerProcessId or SourceField.ComMethodNumber))
                {
                    continue;
                }

                // Only a segment holding COM fields opens the columns they need.
                if (!opened)
                {
                    locator = new Locator(segment);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                if (values.SignedAt(row) is not { } value)
                {
                    continue;
                }

                RecordAddress address = locator.At(row);
                Carried carried = fields.GetValueOrDefault(address);
                fields[address] = code switch
                {
                    SourceField.ComCallTraceId => carried with { Trace = value },
                    SourceField.PeerProcessId => carried with { Peer = (int)value },
                    _ => carried with { Method = value },
                };
            }
        }

        return fields;
    }

    // Reads one segment's COM call records; returns how many of its COM records are no call record.
    private static long Collect(SegmentReaderV1 segment, Dictionary<RecordAddress, Carried> fields, List<Record> records)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        long other = 0;
        bool opened = false;
        CallColumns read = default;
        RecordOwnerColumns owners = default;
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ComCall)
            {
                continue;
            }

            // Only a segment holding COM records opens the columns a call needs.
            if (!opened)
            {
                read = new CallColumns(segment);
                owners = new RecordOwnerColumns(segment);
                opened = true;
            }

            var kind = (ObservationKind)read.Kinds.UnsignedAt(row)!.Value;
            ComCallSide? side = (Direction)read.Directions.UnsignedAt(row)!.Value switch
            {
                Direction.Outbound => ComCallSide.Client,
                Direction.Inbound => ComCallSide.Server,
                _ => null,
            };
            if (side is not { } callSide || kind is not (ObservationKind.RequestStart or ObservationKind.RequestEnd)
                || owners.At(row, Mechanism.ComCall) is not { } processId)
            {
                other++;
                continue;
            }

            bool isStart = kind == ObservationKind.RequestStart;
            RecordAddress address = read.Locator.At(row);
            Carried carried = fields.GetValueOrDefault(address);
            records.Add(new(
                address,
                read.Ticks.SignedAt(row)!.Value,
                processId,
                callSide,
                isStart,
                carried.Trace,
                isStart ? carried.Peer : null,
                isStart ? carried.Method : null,
                isStart ? read.Identifiers.GuidAt(row) : null,
                read.Activities.GuidAt(row),
                (int)(read.Threads.UnsignedAt(row) ?? 0),
                isStart ? null : read.Statuses.SignedAt(row)));
        }

        return other;
    }

    // A segment's raw locator columns, opened once: the address of each of its rows.
    private readonly ref struct Locator
    {
        private readonly SegmentColumnSlice streams;
        private readonly SegmentColumnSlice epochs;
        private readonly SegmentColumnSlice ordinals;
        private readonly SegmentColumnSlice factHigh;
        private readonly SegmentColumnSlice factLow;

        public Locator(SegmentReaderV1 segment)
        {
            streams = segment.Slice(SegmentColumnId.RawStreamId);
            epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
            ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
            factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
            factLow = segment.Slice(SegmentColumnId.FactKeyLow);
        }

        public RecordAddress At(int row) => new(
            (uint)streams.UnsignedAt(row)!.Value,
            (uint)epochs.UnsignedAt(row)!.Value,
            ordinals.UnsignedAt(row)!.Value,
            new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
    }

    // The columns a COM call record is read from, opened once for a segment holding one.
    private readonly ref struct CallColumns
    {
        public CallColumns(SegmentReaderV1 segment)
        {
            Locator = new(segment);
            Kinds = segment.Slice(SegmentColumnId.ObservationKind);
            Directions = segment.Slice(SegmentColumnId.Direction);
            Ticks = segment.Slice(SegmentColumnId.NativeTicks);
            Identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
            Activities = segment.Slice(SegmentColumnId.ActivityId);
            Threads = segment.Slice(SegmentColumnId.HeaderThreadId);
            Statuses = segment.Slice(SegmentColumnId.StatusCode);
        }

        public Locator Locator { get; }

        public SegmentColumnSlice Kinds { get; }

        public SegmentColumnSlice Directions { get; }

        public SegmentColumnSlice Ticks { get; }

        public SegmentColumnSlice Identifiers { get; }

        public SegmentColumnSlice Activities { get; }

        public SegmentColumnSlice Threads { get; }

        public SegmentColumnSlice Statuses { get; }
    }

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey) : IComparable<RecordAddress>
    {
        public int CompareTo(RecordAddress other) =>
            Stream != other.Stream ? Stream.CompareTo(other.Stream)
            : Epoch != other.Epoch ? Epoch.CompareTo(other.Epoch)
            : Ordinal != other.Ordinal ? Ordinal.CompareTo(other.Ordinal)
            : FactKey.High != other.FactKey.High ? FactKey.High.CompareTo(other.FactKey.High)
            : FactKey.Low.CompareTo(other.FactKey.Low);
    }

    private readonly record struct Carried(long? Trace, int? Peer, long? Method);

    private readonly record struct KeyRun(int Call, int Starts, bool Ambiguous);

    private readonly record struct Record(
        RecordAddress Address,
        long Ticks,
        int ProcessId,
        ComCallSide Side,
        bool IsStart,
        long? Trace,
        int? Peer,
        long? Method,
        Guid? Interface,
        Guid? Activity,
        int Thread,
        long? Status);
}
