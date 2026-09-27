using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>The side of an RPC call a record describes (`contracts/operations-v1.md` §2).</summary>
public enum RpcCallSide
{
    /// <summary>The calling process's side: descriptors 5 and 7, direction <see cref="Direction.Outbound"/>.</summary>
    Client = 1,

    /// <summary>The serving process's side: descriptors 6 and 8, direction <see cref="Direction.Inbound"/>.</summary>
    Server = 2,
}

/// <summary>What a call's records establish about it (`contracts/operations-v1.md` §4).</summary>
public enum RpcCallState
{
    /// <summary>A start and its stop, paired by activity id.</summary>
    Completed = 1,

    /// <summary>A start with no stop by the end of the derived evidence: censored, not failed.</summary>
    OpenAtCaptureEnd = 2,

    /// <summary>A stop whose start is not in the evidence: it began before the capture or was not delivered.</summary>
    StartNotObserved = 3,

    /// <summary>A record that carried no activity id, so it pairs with nothing.</summary>
    NoActivityId = 4,

    /// <summary>A record of a key whose activity id was reused before its stop: which stop is whose cannot be told.</summary>
    Ambiguous = 5,
}

/// <summary>One record of a call: its identity, where it is in its generation, and when it was read.</summary>
public sealed record RpcCallRecord(
    ObservationId Observation,
    string? Segment,
    int Row,
    long NativeTicks,
    long? SessionRelativeTicks);

/// <summary>One RPC call (`contracts/operations-v1.md` §4).</summary>
public sealed record RpcCall
{
    public required RpcCallSide Side { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>The call's process, bound by its first record's reading.</summary>
    public required ProcessBinding Process { get; init; }

    public required RpcCallState State { get; init; }

    /// <summary>The activity id that paired it; null for a record that carried none.</summary>
    public Guid? ActivityId { get; init; }

    public Guid? Interface { get; init; }

    public long? Procedure { get; init; }

    public long? Protocol { get; init; }

    public RpcCallRecord? Start { get; init; }

    public RpcCallRecord? Stop { get; init; }

    /// <summary>The stop's status: 0 is success, any other value the call's failure code.</summary>
    public long? Status { get; init; }

    /// <summary>Session nanoseconds from the start's reading to the stop's; only for a completed call.</summary>
    public long? DurationNanoseconds { get; init; }

    /// <summary>The call's identity: its first record's observation identity.</summary>
    public ObservationId Identity => (Start ?? Stop)!.Observation;
}

/// <summary>Calls counted by what their records establish (`contracts/operations-v1.md` §4).</summary>
public sealed record RpcCallCounts
{
    /// <summary>Every call: each call record belongs to exactly one.</summary>
    public required long Calls { get; init; }

    /// <summary>Calls with a start.</summary>
    public required long Started { get; init; }

    public required long Completed { get; init; }

    /// <summary>Completed calls whose status is not 0.</summary>
    public required long Failed { get; init; }

    public required long OpenAtCaptureEnd { get; init; }

    public required long StartNotObserved { get; init; }

    public required long NoActivityId { get; init; }

    public required long Ambiguous { get; init; }
}

/// <summary>
/// The durations of a group's completed calls: each is a duration one of them took, by nearest rank, never an
/// interpolation (`contracts/operations-v1.md` §5).
/// </summary>
public sealed record RpcCallDurations(long Count, long Minimum, long Median, long Percentile95, long Maximum);

/// <summary>The calls of one process binding, side and interface (`contracts/operations-v1.md` §5).</summary>
public sealed record RpcCallGroup
{
    public required int ProcessId { get; init; }

    public required ProcessBinding Process { get; init; }

    public required RpcCallSide Side { get; init; }

    public required Guid? Interface { get; init; }

    public required RpcCallCounts Counts { get; init; }

    /// <summary>The completed calls' durations; null when none completed.</summary>
    public RpcCallDurations? Durations { get; init; }

    /// <summary>Where the group's calls begin in the index, in reading order.</summary>
    internal int First { get; init; }
}

/// <summary>
/// The RPC calls of one generation: each call record's start paired with its stop through the activity id, on one side
/// of one process (`contracts/operations-v1.md`, ADR-031). It is a derivation over published segments alone, so it is
/// rebuildable from retained evidence (R20) and changes no observation (R1).
/// </summary>
/// <remarks>
/// A call is held as a compact entry: its records by segment position and row, its readings, and what it carried. A
/// call's observation identities and activity id are read again from its segments when it is described, so the index
/// holds no more per call than a summary needs.
/// </remarks>
public sealed class RpcCallIndex
{
    /// <summary>The pairing rule's identity. A change to what pairs, how, or what a call holds is a new rule (§24).</summary>
    public const string OperationRule = "rpc-call-operation-v1";

    private readonly Entry[] entries;
    private readonly Guid[] interfaces;
    private readonly string?[] segmentNames;
    private readonly SourceClockDescriptor clock;

    private RpcCallIndex(
        Entry[] entries,
        Guid[] interfaces,
        string?[] segmentNames,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        long callRecords,
        long otherRecords,
        RpcCallGroup[] groups,
        RpcCallCounts totals)
    {
        this.entries = entries;
        this.interfaces = interfaces;
        this.segmentNames = segmentNames;
        this.clock = clock;
        Processes = processes;
        CallRecords = callRecords;
        OtherRpcRecords = otherRecords;
        Groups = groups;
        Totals = totals;
    }

    /// <summary>The process index the calls' bindings name positions in.</summary>
    public ProcessInstanceIndex Processes { get; }

    /// <summary>The call records read: every RPC start and stop of a client or server side.</summary>
    public long CallRecords { get; }

    /// <summary>RPC records that are no call record (§2): another kind or direction.</summary>
    public long OtherRpcRecords { get; }

    /// <summary>Every group, the one with the most calls first, then by PID, side and interface.</summary>
    public IReadOnlyList<RpcCallGroup> Groups { get; }

    /// <summary>Every call, counted by state.</summary>
    public RpcCallCounts Totals { get; }

    /// <summary>
    /// Pairs the call records of <paramref name="segments"/>, whose process instances are <paramref name="processes"/>,
    /// with the procedure and protocol <paramref name="fieldSegments"/> carry for their starts.
    /// </summary>
    public static RpcCallIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        SourceClockDescriptor clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        if (clock.Id != processes.Clock)
        {
            throw new ArgumentException("The calls are timed on another clock than the process instances.", nameof(clock));
        }

        Dictionary<RecordAddress, StartFields> fields = ReadFields(fieldSegments, cancellationToken);
        var interfaces = new List<Guid>();
        var interfaceIndex = new Dictionary<Guid, int>();
        // Sized from the mechanism column alone, so a session of many calls fills its buffers once.
        var keyed = new List<CallRecord>(CountRpcRecords(segments, cancellationToken));
        var unkeyed = new List<CallRecord>();
        long otherRecords = 0;
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            otherRecords += Collect(segments[position], position, fields, interfaces, interfaceIndex, keyed, unkeyed);
        }

        Span<CallRecord> records = CollectionsMarshal.AsSpan(keyed);
        records.Sort(CanonicalByKey.Instance);
        var drafts = new List<Entry>((records.Length / 2) + unkeyed.Count);
        int first = 0;
        while (first < records.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = first + 1;
            while (end < records.Length && CanonicalByKey.SameKey(records[first], records[end]))
            {
                end++;
            }

            Walk(records.Slice(first, end - first), drafts);
            first = end;
        }

        foreach (CallRecord record in unkeyed)
        {
            drafts.Add(Single(record, RpcCallState.NoActivityId));
        }

        Entry[] entries = drafts.ToArray();
        for (int index = 0; index < entries.Length; index++)
        {
            entries[index] = entries[index] with
            {
                Process = processes.Bind(entries[index].ProcessId, entries[index].FirstTicks, isLifecycleRecord: false),
            };
        }

        Array.Sort(entries, ByGroupThenReading.Instance);
        (RpcCallGroup[] groups, RpcCallCounts totals) = Group(entries, [.. interfaces], clock);
        return new(
            entries,
            [.. interfaces],
            [.. segments.Select(segment => segment.Published?.Name)],
            clock,
            processes,
            (long)records.Length + unkeyed.Count,
            otherRecords,
            groups,
            totals);
    }

    /// <summary>
    /// The calls of <paramref name="group"/> in reading order, from <paramref name="offset"/>, at most
    /// <paramref name="limit"/> of them, described from <paramref name="segments"/>: the segments the index was derived
    /// from, in the same order.
    /// </summary>
    public IReadOnlyList<RpcCall> CallsOf(RpcCallGroup group, IReadOnlyList<SegmentReaderV1> segments, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (!Groups.Contains(group))
        {
            throw new ArgumentException("The group is not one of this index's.", nameof(group));
        }

        if (segments.Count != segmentNames.Length
            || segments.Select(segment => segment.Published?.Name).Where((name, position) => name != segmentNames[position]).Any())
        {
            throw new ArgumentException("These are not the segments the calls were derived from.", nameof(segments));
        }

        int from = group.First + (int)Math.Min(offset, group.Counts.Calls);
        int to = group.First + (int)Math.Min((long)offset + limit, group.Counts.Calls);
        var calls = new List<RpcCall>(Math.Max(0, to - from));
        for (int index = from; index < to; index++)
        {
            calls.Add(Describe(entries[index], segments));
        }

        return calls;
    }

    private RpcCall Describe(Entry entry, IReadOnlyList<SegmentReaderV1> segments)
    {
        RpcCallRecord? start = entry.StartSegment < 0 ? null : RecordAt(segments, entry.StartSegment, entry.StartRow, entry.StartTicks);
        RpcCallRecord? stop = entry.StopSegment < 0 ? null : RecordAt(segments, entry.StopSegment, entry.StopRow, entry.StopTicks);
        (int segment, int row) = entry.StartSegment >= 0 ? (entry.StartSegment, entry.StartRow) : (entry.StopSegment, entry.StopRow);
        return new()
        {
            Side = entry.Side,
            ProcessId = entry.ProcessId,
            Process = entry.Process,
            State = entry.State,
            ActivityId = segments[segment].IdentifierValue(SegmentColumnId.ActivityId, row),
            Interface = entry.Interface < 0 ? null : interfaces[entry.Interface],
            Procedure = entry.HasProcedure ? entry.Procedure : null,
            Protocol = entry.HasProtocol ? entry.Protocol : null,
            Start = start,
            Stop = stop,
            Status = entry.HasStatus ? entry.Status : null,
            DurationNanoseconds = entry.State == RpcCallState.Completed ? Duration(clock, entry) : null,
        };
    }

    private RpcCallRecord RecordAt(IReadOnlyList<SegmentReaderV1> segments, int segment, int row, long ticks)
    {
        SegmentReaderV1 reader = segments[segment];
        return new(reader.ObservationIdOf(row), segmentNames[segment], row, ticks,
            reader.Slice(SegmentColumnId.SessionRelativeTicks).SignedAt(row));
    }

    private static long Duration(SourceClockDescriptor clock, Entry entry) =>
        (long)(SourceClockMath.SessionNanoseconds(clock, entry.StopTicks) - SourceClockMath.SessionNanoseconds(clock, entry.StartTicks));

    private static int CountRpcRecords(IReadOnlyList<SegmentReaderV1> segments, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            for (int row = 0; row < segment.RowCount; row++)
            {
                count += (Mechanism)mechanisms.UnsignedAt(row)!.Value == Mechanism.Rpc ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>
    /// Reads the procedure number and protocol sequence each call start carried as a source field. A field repeated
    /// across source-field segments refuses the derivation, as it refuses a process identity (§23).
    /// </summary>
    private static Dictionary<RecordAddress, StartFields> ReadFields(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<RecordAddress, StartFields>();
        var seen = new HashSet<(RecordAddress Address, SourceField Field)>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.RpcProcedureNumber or SourceField.RpcProtocolSequence))
                {
                    continue;
                }

                SourceFieldRowV1 source = segment.FieldRow(row);
                var address = new RecordAddress(source.RawStreamId, source.RawSourceEpoch, source.RawRecordOrdinal, source.FactKey);
                if (!seen.Add((address, source.Field)))
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries {source.Field} in more than one source-field segment; a "
                        + "duplicated field could change what its call is.");
                }

                if (source.Value is not { } value)
                {
                    continue;
                }

                StartFields current = fields.GetValueOrDefault(address);
                fields[address] = source.Field == SourceField.RpcProcedureNumber
                    ? current with { Procedure = value }
                    : current with { Protocol = value };
            }
        }

        return fields;
    }

    /// <summary>Reads one segment's call records; returns how many of its RPC records are no call record.</summary>
    private static long Collect(
        SegmentReaderV1 segment,
        int position,
        Dictionary<RecordAddress, StartFields> fields,
        List<Guid> interfaces,
        Dictionary<Guid, int> interfaceIndex,
        List<CallRecord> keyed,
        List<CallRecord> unkeyed)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        long other = 0;
        bool opened = false;
        SegmentColumnSlice kinds = default;
        SegmentColumnSlice directions = default;
        SegmentColumnSlice ticks = default;
        SegmentColumnSlice streams = default;
        SegmentColumnSlice epochs = default;
        SegmentColumnSlice ordinals = default;
        SegmentColumnSlice factHigh = default;
        SegmentColumnSlice factLow = default;
        SegmentColumnSlice statuses = default;
        var owners = new RecordOwnerColumns(segment);
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Rpc)
            {
                continue;
            }

            // Only a segment holding RPC records opens the columns a call needs.
            if (!opened)
            {
                kinds = segment.Slice(SegmentColumnId.ObservationKind);
                directions = segment.Slice(SegmentColumnId.Direction);
                ticks = segment.Slice(SegmentColumnId.NativeTicks);
                streams = segment.Slice(SegmentColumnId.RawStreamId);
                epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                statuses = segment.Slice(SegmentColumnId.StatusCode);
                opened = true;
            }

            var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;
            var direction = (Direction)directions.UnsignedAt(row)!.Value;
            RpcCallSide? side = direction switch
            {
                Direction.Outbound => RpcCallSide.Client,
                Direction.Inbound => RpcCallSide.Server,
                _ => null,
            };
            if (side is not { } callSide || kind is not (ObservationKind.RequestStart or ObservationKind.RequestEnd)
                || owners.At(row, Mechanism.Rpc) is not { } processId)
            {
                other++;
                continue;
            }

            bool isStart = kind == ObservationKind.RequestStart;
            var fact = new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value);
            var address = new RecordAddress(
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                fact);
            int interfaceAt = -1;
            if (isStart && segment.IdentifierValue(SegmentColumnId.SourceIdentifier, row) is { } uuid)
            {
                if (!interfaceIndex.TryGetValue(uuid, out interfaceAt))
                {
                    interfaceAt = interfaces.Count;
                    interfaceIndex[uuid] = interfaceAt;
                    interfaces.Add(uuid);
                }
            }

            StartFields carried = isStart ? fields.GetValueOrDefault(address) : default;
            long? status = isStart ? null : statuses.SignedAt(row);
            Guid? activity = segment.IdentifierValue(SegmentColumnId.ActivityId, row);
            var record = new CallRecord(
                processId,
                callSide,
                isStart,
                activity ?? Guid.Empty,
                ticks.SignedAt(row)!.Value,
                address,
                position,
                row,
                interfaceAt,
                carried,
                status);
            (activity is null ? unkeyed : keyed).Add(record);
        }

        return other;
    }

    /// <summary>
    /// Pairs one key's records, in canonical order (§3): a start opens a call and the next stop closes it. A start while
    /// one is open makes every record from the earlier start until the key has no call open ambiguous.
    /// </summary>
    private static void Walk(ReadOnlySpan<CallRecord> records, List<Entry> calls)
    {
        int run = -1;
        int open = 0;
        bool ambiguous = false;
        for (int index = 0; index < records.Length; index++)
        {
            CallRecord record = records[index];
            if (record.IsStart)
            {
                run = open == 0 ? index : run;
                open++;
                ambiguous |= open > 1;
                continue;
            }

            if (open == 0)
            {
                calls.Add(Single(record, RpcCallState.StartNotObserved));
                continue;
            }

            open--;
            if (open > 0)
            {
                continue;
            }

            if (ambiguous)
            {
                AddAmbiguous(records[run..(index + 1)], calls);
            }
            else
            {
                calls.Add(Paired(records[run], record));
            }

            ambiguous = false;
        }

        if (open == 0)
        {
            return;
        }

        if (ambiguous)
        {
            AddAmbiguous(records[run..], calls);
        }
        else
        {
            calls.Add(Single(records[run], RpcCallState.OpenAtCaptureEnd));
        }
    }

    private static void AddAmbiguous(ReadOnlySpan<CallRecord> records, List<Entry> calls)
    {
        foreach (CallRecord record in records)
        {
            calls.Add(Single(record, RpcCallState.Ambiguous));
        }
    }

    private static Entry Paired(CallRecord start, CallRecord stop) => new()
    {
        ProcessId = start.ProcessId,
        Side = start.Side,
        State = RpcCallState.Completed,
        Interface = start.Interface,
        Procedure = start.Fields.Procedure ?? 0,
        HasProcedure = start.Fields.Procedure.HasValue,
        Protocol = start.Fields.Protocol ?? 0,
        HasProtocol = start.Fields.Protocol.HasValue,
        StartTicks = start.Ticks,
        StartSegment = start.Segment,
        StartRow = start.Row,
        StopTicks = stop.Ticks,
        StopSegment = stop.Segment,
        StopRow = stop.Row,
        Status = stop.Status ?? 0,
        HasStatus = stop.Status.HasValue,
    };

    private static Entry Single(CallRecord record, RpcCallState state) => record.IsStart
        ? new()
        {
            ProcessId = record.ProcessId,
            Side = record.Side,
            State = state,
            Interface = record.Interface,
            Procedure = record.Fields.Procedure ?? 0,
            HasProcedure = record.Fields.Procedure.HasValue,
            Protocol = record.Fields.Protocol ?? 0,
            HasProtocol = record.Fields.Protocol.HasValue,
            StartTicks = record.Ticks,
            StartSegment = record.Segment,
            StartRow = record.Row,
            StopSegment = -1,
            StopRow = -1,
        }
        : new()
        {
            ProcessId = record.ProcessId,
            Side = record.Side,
            State = state,
            Interface = -1,
            StartSegment = -1,
            StartRow = -1,
            StopTicks = record.Ticks,
            StopSegment = record.Segment,
            StopRow = record.Row,
            Status = record.Status ?? 0,
            HasStatus = record.Status.HasValue,
        };

    /// <summary>Counts each group of consecutive entries, and every entry, and orders the groups by calls.</summary>
    private static (RpcCallGroup[] Groups, RpcCallCounts Totals) Group(Entry[] entries, Guid[] interfaces, SourceClockDescriptor clock)
    {
        var groups = new List<RpcCallGroup>();
        var totals = new Tally();
        int first = 0;
        while (first < entries.Length)
        {
            int end = first + 1;
            while (end < entries.Length && ByGroupThenReading.SameGroup(entries[first], entries[end]))
            {
                end++;
            }

            var tally = new Tally();
            var durations = new List<long>();
            for (int index = first; index < end; index++)
            {
                tally.Add(entries[index]);
                totals.Add(entries[index]);
                if (entries[index].State == RpcCallState.Completed)
                {
                    durations.Add(Duration(clock, entries[index]));
                }
            }

            Entry head = entries[first];
            groups.Add(new()
            {
                ProcessId = head.ProcessId,
                Process = head.Process,
                Side = head.Side,
                Interface = head.Interface < 0 ? null : interfaces[head.Interface],
                Counts = tally.Counts(),
                Durations = Distribution(durations),
                First = first,
            });
            first = end;
        }

        return ([.. groups
            .OrderByDescending(group => group.Counts.Calls)
            .ThenBy(group => group.ProcessId)
            .ThenBy(group => group.Process.Instance)
            .ThenBy(group => group.Side)
            .ThenBy(group => group.Interface)], totals.Counts());
    }

    private static RpcCallDurations? Distribution(List<long> durations)
    {
        if (durations.Count == 0)
        {
            return null;
        }

        durations.Sort();
        return new(
            durations.Count,
            durations[0],
            NearestRank(durations, 50),
            NearestRank(durations, 95),
            durations[^1]);
    }

    /// <summary>The smallest duration at least <paramref name="percent"/>% of the durations do not exceed.</summary>
    private static long NearestRank(List<long> sorted, int percent) =>
        sorted[(int)Math.Max(0, ((((long)sorted.Count * percent) + 99) / 100) - 1)];

    /// <summary>Where one record is: its raw locator and fact key, without the capture every record here shares.</summary>
    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    /// <summary>The procedure number and protocol sequence a call start carried, each null when it carried none.</summary>
    private readonly record struct StartFields(long? Procedure, long? Protocol);

    /// <summary>One call record as it is paired: its key, its canonical place, and what it carried.</summary>
    private readonly record struct CallRecord(
        int ProcessId,
        RpcCallSide Side,
        bool IsStart,
        Guid Activity,
        long Ticks,
        RecordAddress Address,
        int Segment,
        int Row,
        int Interface,
        StartFields Fields,
        long? Status);

    /// <summary>One call as the index holds it: a summary's worth, with its records by segment position and row.</summary>
    private readonly record struct Entry
    {
        public int ProcessId { get; init; }

        public ProcessBinding Process { get; init; }

        public RpcCallSide Side { get; init; }

        public RpcCallState State { get; init; }

        public int Interface { get; init; }

        public long Procedure { get; init; }

        public bool HasProcedure { get; init; }

        public long Protocol { get; init; }

        public bool HasProtocol { get; init; }

        public long StartTicks { get; init; }

        public int StartSegment { get; init; }

        public int StartRow { get; init; }

        public long StopTicks { get; init; }

        public int StopSegment { get; init; }

        public int StopRow { get; init; }

        public long Status { get; init; }

        public bool HasStatus { get; init; }

        /// <summary>The reading the call binds by: its first record's.</summary>
        public long FirstTicks => StartSegment >= 0 ? StartTicks : StopTicks;
    }

    /// <summary>A key's records together, each key's in canonical order (§3).</summary>
    private sealed class CanonicalByKey : IComparer<CallRecord>
    {
        public static readonly CanonicalByKey Instance = new();

        public static bool SameKey(CallRecord left, CallRecord right) =>
            left.ProcessId == right.ProcessId && left.Side == right.Side && left.Activity == right.Activity;

        public int Compare(CallRecord left, CallRecord right)
        {
            int order = left.ProcessId.CompareTo(right.ProcessId);
            order = order != 0 ? order : ((int)left.Side).CompareTo((int)right.Side);
            order = order != 0 ? order : left.Activity.CompareTo(right.Activity);
            order = order != 0 ? order : left.Ticks.CompareTo(right.Ticks);
            order = order != 0 ? order : left.Address.Stream.CompareTo(right.Address.Stream);
            order = order != 0 ? order : left.Address.Epoch.CompareTo(right.Address.Epoch);
            order = order != 0 ? order : left.Address.Ordinal.CompareTo(right.Address.Ordinal);
            order = order != 0 ? order : left.Address.FactKey.High.CompareTo(right.Address.FactKey.High);
            return order != 0 ? order : left.Address.FactKey.Low.CompareTo(right.Address.FactKey.Low);
        }
    }

    /// <summary>A group's calls together, each group's in the order their first records were read.</summary>
    private sealed class ByGroupThenReading : IComparer<Entry>
    {
        public static readonly ByGroupThenReading Instance = new();

        public static bool SameGroup(Entry left, Entry right) =>
            left.ProcessId == right.ProcessId
            && left.Process == right.Process
            && left.Side == right.Side
            && left.Interface == right.Interface;

        public int Compare(Entry left, Entry right)
        {
            int order = left.ProcessId.CompareTo(right.ProcessId);
            order = order != 0 ? order : left.Process.Instance.CompareTo(right.Process.Instance);
            order = order != 0 ? order : ((int)left.Process.Reason).CompareTo((int)right.Process.Reason);
            order = order != 0 ? order : ((int)left.Process.Strength).CompareTo((int)right.Process.Strength);
            order = order != 0 ? order : ((int)left.Side).CompareTo((int)right.Side);
            order = order != 0 ? order : left.Interface.CompareTo(right.Interface);
            order = order != 0 ? order : left.FirstTicks.CompareTo(right.FirstTicks);
            order = order != 0 ? order : left.StartSegment.CompareTo(right.StartSegment);
            order = order != 0 ? order : left.StartRow.CompareTo(right.StartRow);
            order = order != 0 ? order : left.StopSegment.CompareTo(right.StopSegment);
            return order != 0 ? order : left.StopRow.CompareTo(right.StopRow);
        }
    }

    /// <summary>Counts calls by state as they are added.</summary>
    private sealed class Tally
    {
        private long calls;
        private long started;
        private long completed;
        private long failed;
        private long open;
        private long startNotObserved;
        private long noActivityId;
        private long ambiguous;

        public void Add(Entry entry)
        {
            calls++;
            started += entry.StartSegment >= 0 ? 1 : 0;
            switch (entry.State)
            {
                case RpcCallState.Completed:
                    completed++;
                    failed += entry.HasStatus && entry.Status != 0 ? 1 : 0;
                    break;
                case RpcCallState.OpenAtCaptureEnd:
                    open++;
                    break;
                case RpcCallState.StartNotObserved:
                    startNotObserved++;
                    break;
                case RpcCallState.NoActivityId:
                    noActivityId++;
                    break;
                default:
                    ambiguous++;
                    break;
            }
        }

        public RpcCallCounts Counts() => new()
        {
            Calls = calls,
            Started = started,
            Completed = completed,
            Failed = failed,
            OpenAtCaptureEnd = open,
            StartNotObserved = startNotObserved,
            NoActivityId = noActivityId,
            Ambiguous = ambiguous,
        };
    }
}
