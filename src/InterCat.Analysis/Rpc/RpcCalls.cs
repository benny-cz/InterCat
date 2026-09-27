using System.Runtime.CompilerServices;
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
public sealed record RpcCallDurations(long Count, long Minimum, long Median, long Percentile95, long Maximum)
{
    /// <summary>The distribution of these durations by nearest rank, as a group's is taken; null when there are none.</summary>
    public static RpcCallDurations? Of(IEnumerable<long> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);
        return RpcCallIndex.Distribution([.. durations]);
    }
}

/// <summary>
/// One call as a timeline draws it: when its records were read, in session nanoseconds, how it ended, and the first
/// record's raw locator and fact key, which name it.
/// </summary>
public readonly record struct RpcCallSpan(
    long? StartNanoseconds,
    long? StopNanoseconds,
    RpcCallState State,
    long? Status,
    long? Procedure,
    uint Stream,
    uint Epoch,
    ulong Ordinal,
    FactKey FactKey);

/// <summary>Where one record of a call is: its native reading and its place in the index's segments.</summary>
public readonly record struct RpcCallMark(long NativeTicks, int Segment, int Row);

/// <summary>
/// One call as a count reads it (`contracts/metrics-v1.md` §8a): its process binding, what its records establish, its
/// stop's status, and where each of its records is. A record the call does not have is null.
/// </summary>
public readonly record struct RpcCallOutcome(
    ProcessBinding Process,
    RpcCallSide Side,
    RpcCallState State,
    long? Status,
    RpcCallMark? Start,
    RpcCallMark? Stop);

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
    /// The published names of the segments the calls were derived from, by the positions <see cref="RecordsOf"/> gives;
    /// null for a segment with no published identity.
    /// </summary>
    public IReadOnlyList<string?> SegmentNames => segmentNames;

    /// <summary>
    /// The group of one process instance's calls on one side to one interface, or null when it made none. Calls bound to
    /// no instance belong to no instance's group.
    /// </summary>
    public RpcCallGroup? GroupOf(ProcessInstanceId instance, RpcCallSide side, Guid? rpcInterface)
    {
        foreach (RpcCallGroup group in Groups)
        {
            if (group.Process.IsBound
                && group.Side == side
                && group.Interface == rpcInterface
                && Processes.Instances[group.Process.Instance].Id == instance)
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>
    /// Where in <paramref name="group"/>'s reading order the call is whose first record has this raw locator and fact key:
    /// the call's identity, without the capture every record here shares. Null when no call of the group has it.
    /// </summary>
    public int? PositionOf(RpcCallGroup group, uint stream, uint epoch, ulong ordinal, FactKey factKey)
    {
        RequireGroup(group);
        var address = new RecordAddress(stream, epoch, ordinal, factKey);
        for (int index = 0; index < group.Counts.Calls; index++)
        {
            if (entries[group.First + index].FirstAddress == address)
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// The records of <paramref name="group"/>'s calls, or of its one call at <paramref name="position"/>, by segment
    /// position (<see cref="SegmentNames"/>) and row: a call's start, then its stop.
    /// </summary>
    public IEnumerable<(int Segment, int Row)> RecordsOf(RpcCallGroup group, int? position = null)
    {
        RequireGroup(group);
        if (position is { } one)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(one);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(one, group.Counts.Calls);
        }

        return Records(group.First + (position ?? 0), position is null ? group.First + (int)group.Counts.Calls : group.First + position.Value + 1);
    }

    /// <summary>
    /// Every call of <paramref name="group"/> in reading order, placed in session time on the index's clock: what a
    /// timeline draws of a channel, with no segment read.
    /// </summary>
    public IEnumerable<RpcCallSpan> SpansOf(RpcCallGroup group)
    {
        RequireGroup(group);
        return Spans(group.First, group.First + (int)group.Counts.Calls);
    }

    /// <summary>
    /// The calls of <paramref name="group"/> in reading order, as a count reads them: what <see cref="Outcomes"/> yields
    /// for this group alone, position by position.
    /// </summary>
    public IEnumerable<RpcCallOutcome> OutcomesOf(RpcCallGroup group)
    {
        RequireGroup(group);
        for (int index = group.First; index < group.First + group.Counts.Calls; index++)
        {
            Entry entry = entries[index];
            yield return new(
                entry.Process,
                entry.Side,
                entry.State,
                entry.HasStatus ? entry.Status : null,
                entry.StartSegment >= 0 ? new RpcCallMark(entry.StartTicks, entry.StartSegment, entry.StartRow) : null,
                entry.StopSegment >= 0 ? new RpcCallMark(entry.StopTicks, entry.StopSegment, entry.StopRow) : null);
        }
    }

    /// <summary>
    /// The calls of <paramref name="group"/> at these positions in its reading order, described from
    /// <paramref name="segments"/> as <see cref="CallsOf"/> describes them: a page of the calls a scope selects.
    /// </summary>
    public IReadOnlyList<RpcCall> CallsAt(RpcCallGroup group, IReadOnlyList<SegmentReaderV1> segments, IReadOnlyList<int> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        RequireSegments(group, segments);
        var calls = new List<RpcCall>(positions.Count);
        foreach (int position in positions)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(position);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, group.Counts.Calls);
            calls.Add(Describe(entries[group.First + position], segments));
        }

        return calls;
    }

    /// <summary>
    /// Every call, group by group, each group's in reading order: what a count of operations reads, with no segment
    /// read. A record's segment is a position in <see cref="SegmentNames"/>.
    /// </summary>
    public IEnumerable<RpcCallOutcome> Outcomes()
    {
        foreach (Entry entry in entries)
        {
            yield return new(
                entry.Process,
                entry.Side,
                entry.State,
                entry.HasStatus ? entry.Status : null,
                entry.StartSegment >= 0 ? new RpcCallMark(entry.StartTicks, entry.StartSegment, entry.StartRow) : null,
                entry.StopSegment >= 0 ? new RpcCallMark(entry.StopTicks, entry.StopSegment, entry.StopRow) : null);
        }
    }

    private IEnumerable<RpcCallSpan> Spans(int from, int to)
    {
        for (int index = from; index < to; index++)
        {
            Entry entry = entries[index];
            yield return new(
                entry.StartSegment >= 0 ? (long)SourceClockMath.SessionNanoseconds(clock, entry.StartTicks) : null,
                entry.StopSegment >= 0 ? (long)SourceClockMath.SessionNanoseconds(clock, entry.StopTicks) : null,
                entry.State,
                entry.HasStatus ? entry.Status : null,
                entry.HasProcedure ? entry.Procedure : null,
                entry.FirstAddress.Stream,
                entry.FirstAddress.Epoch,
                entry.FirstAddress.Ordinal,
                entry.FirstAddress.FactKey);
        }
    }

    private IEnumerable<(int Segment, int Row)> Records(int from, int to)
    {
        for (int index = from; index < to; index++)
        {
            Entry entry = entries[index];
            if (entry.StartSegment >= 0)
            {
                yield return (entry.StartSegment, entry.StartRow);
            }

            if (entry.StopSegment >= 0)
            {
                yield return (entry.StopSegment, entry.StopRow);
            }
        }
    }

    private void RequireGroup(RpcCallGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!Groups.Contains(group))
        {
            throw new ArgumentException("The group is not one of this index's.", nameof(group));
        }
    }

    /// <summary>A call is described from the segments it was paired from, in the same order, and no others.</summary>
    private void RequireSegments(RpcCallGroup group, IReadOnlyList<SegmentReaderV1> segments)
    {
        RequireGroup(group);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count != segmentNames.Length
            || segments.Select(segment => segment.Published?.Name).Where((name, position) => name != segmentNames[position]).Any())
        {
            throw new ArgumentException("These are not the segments the calls were derived from.", nameof(segments));
        }
    }

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

        Dictionary<RecordAddress, FieldSlot> fields = ReadFields(fieldSegments, cancellationToken);
        var interfaces = new List<Guid>();
        var interfaceIndex = new Dictionary<Guid, int>();
        // Sized from the mechanism column alone, so a session of many calls fills its buffers once.
        var collected = new List<CallRecord>(CountRpcRecords(segments, cancellationToken));
        long otherRecords = 0;
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            otherRecords += Collect(segments[position], position, fields, interfaces, interfaceIndex, collected);
        }

        // Every call record is read before any is paired, and then in canonical order, so the segments' cut and the
        // records' delivery order change no call (§3, I14).
        int[] order = CanonicalOrder(collected, cancellationToken);
        List<Entry> drafts = Walk(CollectionsMarshal.AsSpan(collected), order, cancellationToken);
        Span<Entry> drafted = CollectionsMarshal.AsSpan(drafts);
        for (int index = 0; index < drafted.Length; index++)
        {
            drafted[index] = drafted[index] with
            {
                Process = processes.Bind(drafted[index].ProcessId, drafted[index].FirstTicks, isLifecycleRecord: false),
            };
        }

        Entry[] entries = InGroupsInReadingOrder(drafted, cancellationToken);
        (RpcCallGroup[] groups, RpcCallCounts totals) = Group(entries, [.. interfaces], clock);
        return new(
            entries,
            [.. interfaces],
            [.. segments.Select(segment => segment.Published?.Name)],
            clock,
            processes,
            collected.Count,
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
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        RequireSegments(group, segments);
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
    /// Reads the procedure number and protocol sequence each call start carried as a source field, a column at a time. A
    /// field repeated across source-field segments refuses the derivation, as it refuses a process identity (§23), and so
    /// does a field row whose value and availability disagree, as reading the row whole would (`source-fields-v1`).
    /// </summary>
    private static Dictionary<RecordAddress, FieldSlot> ReadFields(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<RecordAddress, FieldSlot>();
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
            SegmentColumnSlice availabilities = default;
            SegmentColumnPresence texts = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.RpcProcedureNumber or SourceField.RpcProtocolSequence))
                {
                    continue;
                }

                // Only a segment holding RPC fields opens the columns they need.
                if (!opened)
                {
                    streams = segment.Slice(SegmentColumnId.RawStreamId);
                    epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                    factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    availabilities = segment.Slice(SegmentColumnId.FieldAvailability);
                    texts = segment.Presence(SegmentColumnId.FieldText);
                    opened = true;
                }

                long? value = values.SignedAt(row);
                bool text = texts.HasValue(row);
                var availability = (FieldAvailability)availabilities.UnsignedAt(row)!.Value;
                if (!Enum.IsDefined(availability)
                    || availability == FieldAvailability.NotApplicable
                    || (value is not null && text)
                    || (value is not null || text) != (availability == FieldAvailability.Present))
                {
                    throw new InvalidDataException(
                        $"Source-field row {row} is invalid: its {code} value and its availability ({availability}) "
                        + "disagree (source-fields-v1).");
                }

                var address = new RecordAddress(
                    (uint)streams.UnsignedAt(row)!.Value,
                    (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
                ref FieldSlot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(fields, address, out _);
                if (!slot.Name(code, value))
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries {code} in more than one source-field segment; a "
                        + "duplicated field could change what its call is.");
                }
            }
        }

        return fields;
    }

    /// <summary>Reads one segment's call records; returns how many of its RPC records are no call record.</summary>
    private static long Collect(
        SegmentReaderV1 segment,
        int position,
        Dictionary<RecordAddress, FieldSlot> fields,
        List<Guid> interfaces,
        Dictionary<Guid, int> interfaceIndex,
        List<CallRecord> records)
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
        SegmentColumnSlice activities = default;
        SegmentColumnSlice identifiers = default;
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
                activities = segment.Slice(SegmentColumnId.ActivityId);
                identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
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
            var address = new RecordAddress(
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
            int interfaceAt = -1;
            if (isStart && identifiers.GuidAt(row) is { } uuid)
            {
                if (!interfaceIndex.TryGetValue(uuid, out interfaceAt))
                {
                    interfaceAt = interfaces.Count;
                    interfaceIndex[uuid] = interfaceAt;
                    interfaces.Add(uuid);
                }
            }

            FieldSlot carried = isStart ? fields.GetValueOrDefault(address) : default;
            long? status = isStart ? null : statuses.SignedAt(row);
            Guid? activity = activities.GuidAt(row);
            CallFlags flags = (isStart ? CallFlags.Start : CallFlags.None)
                | (activity is null ? CallFlags.None : CallFlags.Activity)
                | (status is null ? CallFlags.None : CallFlags.Status)
                | (carried.Procedure is null ? CallFlags.None : CallFlags.Procedure)
                | (carried.Protocol is null ? CallFlags.None : CallFlags.Protocol);
            records.Add(new(
                address,
                activity ?? Guid.Empty,
                ticks.SignedAt(row)!.Value,
                status ?? 0,
                carried.Procedure ?? 0,
                carried.Protocol ?? 0,
                processId,
                position,
                row,
                interfaceAt,
                callSide,
                flags));
        }

        return other;
    }

    /// <summary>
    /// The records' canonical order (§3): native reading, then raw locator, then fact key. The readings are sorted as plain
    /// integers, and only records read at one instant are compared by their locators.
    /// </summary>
    private static int[] CanonicalOrder(List<CallRecord> collected, CancellationToken cancellationToken)
    {
        ReadOnlySpan<CallRecord> records = CollectionsMarshal.AsSpan(collected);
        long[] readings = new long[records.Length];
        int[] order = new int[records.Length];
        bool ordered = true;
        for (int index = 0; index < records.Length; index++)
        {
            readings[index] = records[index].Ticks;
            order[index] = index;
            ordered &= index == 0 || readings[index - 1] <= readings[index];
        }

        if (!ordered)
        {
            Array.Sort(readings, order);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Comparer<int>? byLocator = null;
        for (int start = 0; start < order.Length;)
        {
            int end = start + 1;
            while (end < order.Length && readings[end] == readings[start])
            {
                end++;
            }

            if (end - start > 1)
            {
                byLocator ??= Comparer<int>.Create((left, right) => CompareLocators(collected, left, right));
                Array.Sort(order, start, end - start, byLocator);
            }

            start = end;
        }

        return order;
    }

    private static int CompareLocators(List<CallRecord> collected, int left, int right)
    {
        ReadOnlySpan<CallRecord> records = CollectionsMarshal.AsSpan(collected);
        RecordAddress one = records[left].Address;
        RecordAddress other = records[right].Address;
        int order = one.Stream.CompareTo(other.Stream);
        order = order != 0 ? order : one.Epoch.CompareTo(other.Epoch);
        order = order != 0 ? order : one.Ordinal.CompareTo(other.Ordinal);
        order = order != 0 ? order : one.FactKey.High.CompareTo(other.FactKey.High);
        return order != 0 ? order : one.FactKey.Low.CompareTo(other.FactKey.Low);
    }

    /// <summary>
    /// Pairs every key's records at once, in canonical order (§3): a start opens a call and the next stop of its key
    /// closes it. A start while its key has a call open makes every record of the key, from the earlier start until as
    /// many stops as starts have been read, ambiguous. Only the keys with a call open are held, so the walk holds the calls
    /// in flight rather than every key the capture has seen.
    /// </summary>
    private static List<Entry> Walk(ReadOnlySpan<CallRecord> records, int[] order, CancellationToken cancellationToken)
    {
        var calls = new List<Entry>((records.Length / 2) + 1);
        var open = new Dictionary<CallKey, OpenCall>();
        for (int rank = 0; rank < order.Length; rank++)
        {
            if ((rank & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ref readonly CallRecord record = ref records[order[rank]];
            if (!record.HasActivity)
            {
                calls.Add(Single(record, RpcCallState.NoActivityId, rank));
                continue;
            }

            var key = new CallKey(record.ProcessId, record.Side, record.Activity);
            ref OpenCall state = ref CollectionsMarshal.GetValueRefOrNullRef(open, key);
            if (Unsafe.IsNullRef(ref state))
            {
                if (record.IsStart)
                {
                    open.Add(key, new OpenCall(rank));
                }
                else
                {
                    calls.Add(Single(record, RpcCallState.StartNotObserved, rank));
                }

                continue;
            }

            // A start while a call is open means the id was reused before its stop. From that start on, every record of the
            // key belongs to the ambiguous run, until the key has no call open.
            if (record.IsStart || state.Run is not null)
            {
                (state.Run ??= [state.First]).Add(rank);
            }

            state.Open += record.IsStart ? 1 : -1;
            if (state.Open > 0)
            {
                continue;
            }

            OpenCall closed = state;
            _ = open.Remove(key);
            if (closed.Run is { } run)
            {
                AddAmbiguous(records, order, run, calls);
            }
            else
            {
                calls.Add(Paired(records[order[closed.First]], record, closed.First));
            }
        }

        foreach (OpenCall still in open.Values)
        {
            if (still.Run is { } run)
            {
                AddAmbiguous(records, order, run, calls);
            }
            else
            {
                calls.Add(Single(records[order[still.First]], RpcCallState.OpenAtCaptureEnd, still.First));
            }
        }

        return calls;
    }

    private static void AddAmbiguous(ReadOnlySpan<CallRecord> records, int[] order, List<int> run, List<Entry> calls)
    {
        foreach (int rank in run)
        {
            calls.Add(Single(records[order[rank]], RpcCallState.Ambiguous, rank));
        }
    }

    private static Entry Paired(in CallRecord start, in CallRecord stop, int rank) => new()
    {
        FirstAddress = start.Address,
        FirstRank = rank,
        ProcessId = start.ProcessId,
        Side = start.Side,
        State = RpcCallState.Completed,
        Interface = start.Interface,
        Procedure = start.Procedure,
        HasProcedure = start.HasProcedure,
        Protocol = start.Protocol,
        HasProtocol = start.HasProtocol,
        StartTicks = start.Ticks,
        StartSegment = start.Segment,
        StartRow = start.Row,
        StopTicks = stop.Ticks,
        StopSegment = stop.Segment,
        StopRow = stop.Row,
        Status = stop.Status,
        HasStatus = stop.HasStatus,
    };

    private static Entry Single(in CallRecord record, RpcCallState state, int rank) => record.IsStart
        ? new()
        {
            FirstAddress = record.Address,
            FirstRank = rank,
            ProcessId = record.ProcessId,
            Side = record.Side,
            State = state,
            Interface = record.Interface,
            Procedure = record.Procedure,
            HasProcedure = record.HasProcedure,
            Protocol = record.Protocol,
            HasProtocol = record.HasProtocol,
            StartTicks = record.Ticks,
            StartSegment = record.Segment,
            StartRow = record.Row,
            StopSegment = -1,
            StopRow = -1,
        }
        : new()
        {
            FirstAddress = record.Address,
            FirstRank = rank,
            ProcessId = record.ProcessId,
            Side = record.Side,
            State = state,
            Interface = -1,
            StartSegment = -1,
            StartRow = -1,
            StopTicks = record.Ticks,
            StopSegment = record.Segment,
            StopRow = record.Row,
            Status = record.Status,
            HasStatus = record.HasStatus,
        };

    /// <summary>
    /// The calls grouped by process binding, side and interface, the groups in the order of those identities and each
    /// group's calls in the canonical order of their first records (§3), which is what a channel lists and a page reads.
    /// A group is numbered where its identity sorts, so one sort of plain integers orders the groups and their calls.
    /// </summary>
    private static Entry[] InGroupsInReadingOrder(ReadOnlySpan<Entry> drafted, CancellationToken cancellationToken)
    {
        var numbered = new Dictionary<GroupKey, int>();
        int[] provisional = new int[drafted.Length];
        for (int index = 0; index < drafted.Length; index++)
        {
            ref int number = ref CollectionsMarshal.GetValueRefOrAddDefault(numbered, GroupKey.Of(drafted[index]), out bool known);
            if (!known)
            {
                number = numbered.Count - 1;
            }

            provisional[index] = number;
        }

        var identities = new GroupKey[numbered.Count];
        foreach ((GroupKey identity, int number) in numbered)
        {
            identities[number] = identity;
        }

        int[] byIdentity = [.. Enumerable.Range(0, identities.Length)];
        Array.Sort(identities, byIdentity, GroupKeyOrder.Instance);
        int[] final = new int[byIdentity.Length];
        for (int place = 0; place < byIdentity.Length; place++)
        {
            final[byIdentity[place]] = place;
        }

        cancellationToken.ThrowIfCancellationRequested();
        long[] keys = new long[drafted.Length];
        int[] order = new int[drafted.Length];
        for (int index = 0; index < drafted.Length; index++)
        {
            keys[index] = ((long)final[provisional[index]] << 32) | (uint)drafted[index].FirstRank;
            order[index] = index;
        }

        Array.Sort(keys, order);
        var entries = new Entry[drafted.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            entries[index] = drafted[order[index]];
        }

        return entries;
    }

    /// <summary>Counts each group of consecutive entries, and every entry, and orders the groups by calls.</summary>
    private static (RpcCallGroup[] Groups, RpcCallCounts Totals) Group(Entry[] entries, Guid[] interfaces, SourceClockDescriptor clock)
    {
        var groups = new List<RpcCallGroup>();
        var totals = new Tally();
        int first = 0;
        while (first < entries.Length)
        {
            int end = first + 1;
            GroupKey identity = GroupKey.Of(entries[first]);
            while (end < entries.Length && GroupKey.Of(entries[end]) == identity)
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

    internal static RpcCallDurations? Distribution(List<long> durations)
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

    /// <summary>
    /// What a call start carried as source fields while they are read, and which fields a row has named, so a field named
    /// twice is refused even when a row of it carried no value.
    /// </summary>
    private struct FieldSlot
    {
        private bool procedureNamed;
        private bool protocolNamed;

        public long? Procedure { get; private set; }

        public long? Protocol { get; private set; }

        /// <summary>Records one row's field; false when a row named the field already.</summary>
        public bool Name(SourceField field, long? value)
        {
            if (field == SourceField.RpcProcedureNumber)
            {
                if (procedureNamed)
                {
                    return false;
                }

                procedureNamed = true;
                Procedure = value;
                return true;
            }

            if (protocolNamed)
            {
                return false;
            }

            protocolNamed = true;
            Protocol = value;
            return true;
        }
    }

    [Flags]
    private enum CallFlags : byte
    {
        None = 0,
        Start = 1,
        Activity = 2,
        Status = 4,
        Procedure = 8,
        Protocol = 16,
    }

    /// <summary>One call record as it is paired: its canonical place, its key, and what it carried.</summary>
    private readonly record struct CallRecord(
        RecordAddress Address,
        Guid Activity,
        long Ticks,
        long Status,
        long Procedure,
        long Protocol,
        int ProcessId,
        int Segment,
        int Row,
        int Interface,
        RpcCallSide Side,
        CallFlags Flags)
    {
        public bool IsStart => (Flags & CallFlags.Start) != 0;

        public bool HasActivity => (Flags & CallFlags.Activity) != 0;

        public bool HasStatus => (Flags & CallFlags.Status) != 0;

        public bool HasProcedure => (Flags & CallFlags.Procedure) != 0;

        public bool HasProtocol => (Flags & CallFlags.Protocol) != 0;
    }

    /// <summary>What pairs a call's records: the process they belong to, their side and their activity id (§3).</summary>
    private readonly record struct CallKey(int ProcessId, RpcCallSide Side, Guid Activity);

    /// <summary>
    /// A key with a call open: the rank of its run's first start, how many starts await their stop, and, once the id has
    /// been reused, the rank of every record of the run.
    /// </summary>
    private struct OpenCall(int first)
    {
        public int First = first;
        public int Open = 1;
        public List<int>? Run;
    }

    /// <summary>One call as the index holds it: a summary's worth, with its records by segment position and row.</summary>
    private readonly record struct Entry
    {
        /// <summary>The call's identity: its first record's raw locator and fact key.</summary>
        public RecordAddress FirstAddress { get; init; }

        /// <summary>Where its first record is in the canonical order of every call record (§3).</summary>
        public int FirstRank { get; init; }

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

    /// <summary>What makes a call's group: its PID, its binding, its side and its interface.</summary>
    private readonly record struct GroupKey(int ProcessId, ProcessBinding Process, RpcCallSide Side, int Interface)
    {
        public static GroupKey Of(in Entry entry) => new(entry.ProcessId, entry.Process, entry.Side, entry.Interface);
    }

    /// <summary>Groups in the order of their identities: PID, instance, binding reason and strength, side, interface.</summary>
    private sealed class GroupKeyOrder : IComparer<GroupKey>
    {
        public static readonly GroupKeyOrder Instance = new();

        public int Compare(GroupKey left, GroupKey right)
        {
            int order = left.ProcessId.CompareTo(right.ProcessId);
            order = order != 0 ? order : left.Process.Instance.CompareTo(right.Process.Instance);
            order = order != 0 ? order : ((int)left.Process.Reason).CompareTo((int)right.Process.Reason);
            order = order != 0 ? order : ((int)left.Process.Strength).CompareTo((int)right.Process.Strength);
            order = order != 0 ? order : ((int)left.Side).CompareTo((int)right.Side);
            return order != 0 ? order : left.Interface.CompareTo(right.Interface);
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
