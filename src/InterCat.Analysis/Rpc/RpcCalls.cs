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
public sealed partial class RpcCallIndex
{
    /// <summary>The pairing rule's identity. A change to what pairs, how, or what a call holds is a new rule (§24).</summary>
    public const string OperationRule = "rpc-call-operation-v1";

    /// <summary>
    /// How far behind the latest call record read a later generation's record may be read and still extend the calls
    /// (<see cref="Extend"/>): records arrive out of order across a live capture's chunks by about a delivery flush, far
    /// less than this. A record read earlier than the window, of a key not kept, is paired again in full.
    /// </summary>
    public const long LateWindowNanoseconds = 15_000_000_000;

    private readonly Entry[] entries;
    private readonly Guid[] interfaces;
    private readonly string?[] segmentNames;
    private readonly SourceClockDescriptor clock;

    /// <summary>
    /// The published identities of the segments and source-field segments the calls were paired from, for an extension
    /// to tell they still begin a later generation's; null where nothing extends.
    /// </summary>
    private readonly (StoreDependency?[] Segments, StoreDependency?[] Fields)? read;

    /// <summary>What a later generation's records can still change, for <see cref="Extend"/>; null where nothing extends.</summary>
    private readonly CallTail? tail;

    /// <summary>Where the calls of the index these were extended from went, for their other ends; null for a derivation.</summary>
    private readonly CallLineage? lineage;

    /// <summary>Stands for this index in the lineage of one extended from it, which so keeps none of this one.</summary>
    private readonly object stamp = new();

    private RpcCallIndex(
        Entry[] entries,
        Guid[] interfaces,
        string?[] segmentNames,
        SourceClockDescriptor clock,
        ProcessInstanceIndex processes,
        long callRecords,
        long otherRecords,
        RpcCallGroup[] groups,
        RpcCallCounts totals,
        (StoreDependency?[] Segments, StoreDependency?[] Fields)? read = null,
        CallTail? tail = null,
        CallLineage? lineage = null)
    {
        this.entries = entries;
        this.interfaces = interfaces;
        this.segmentNames = segmentNames;
        this.read = read;
        this.tail = tail;
        this.lineage = lineage;
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
        RequireDerivedFrom(segments);
    }

    /// <summary>
    /// Refuses segments other than the ones the calls were paired from, in the same order: by the names segments opened on
    /// demand declare, without opening one, or else by each open segment's own.
    /// </summary>
    internal void RequireDerivedFrom(IReadOnlyList<SegmentReaderV1> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        IEnumerable<string?> names = segments is IPublishedSegments published
            ? published.Names
            : segments.Select(segment => segment.Published?.Name);
        if (segments.Count != segmentNames.Length || names.Where((name, position) => name != segmentNames[position]).Any())
        {
            throw new ArgumentException("These are not the segments the calls were derived from.", nameof(segments));
        }
    }

    /// <summary>The clock the calls were read on.</summary>
    internal SourceClockDescriptor Clock => clock;

    /// <summary>How many calls the index holds: every group's, each group's together and in its reading order.</summary>
    internal int Count => entries.Length;

    /// <summary>Where <paramref name="group"/>'s calls are among the index's, as <see cref="FactsOf"/> numbers them.</summary>
    internal (int First, int Count) RangeOf(RpcCallGroup group)
    {
        RequireGroup(group);
        return (group.First, (int)group.Counts.Calls);
    }

    /// <summary>Where the calls of <paramref name="earlier"/> went in these, when these were extended from it; null otherwise.</summary>
    internal CallLineage? LineageFrom(RpcCallIndex earlier) => lineage is { } known && ReferenceEquals(known.From, earlier.stamp) ? known : null;

    /// <summary>The reading of a call's stop; that of no record where it has none.</summary>
    internal long StopTicksAt(int call) => entries[call].StopTicks;

    /// <summary>What the peer rule reads of one call (`contracts/operations-v1.md` §5c).</summary>
    internal PeerFacts FactsOf(int call)
    {
        Entry entry = entries[call];
        return new(
            entry.Side,
            entry.State,
            entry.ProcessId,
            entry.Process,
            entry.Interface,
            entry.HasProcedure ? entry.Procedure : null,
            entry.StartTicks,
            entry.StartSegment,
            entry.StartRow,
            entry.StopTicks,
            entry.StopSegment >= 0,
            entry.StartThread);
    }

    /// <summary>The interface one call's start named; null without a start or where it named none.</summary>
    internal Guid? InterfaceOf(int call) => entries[call].Interface < 0 ? null : interfaces[entries[call].Interface];

    /// <summary>The group a call belongs to, and its place in the group's reading order.</summary>
    internal (RpcCallGroup Group, int Position) GroupAt(int call)
    {
        foreach (RpcCallGroup group in Groups)
        {
            if (call >= group.First && call < group.First + group.Counts.Calls)
            {
                return (group, call - group.First);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(call), call, "No group of the index holds this call.");
    }

    /// <summary>One call described from the segments it was paired from.</summary>
    internal RpcCall DescribeAt(int call, IReadOnlyList<SegmentReaderV1> segments)
    {
        RequireDerivedFrom(segments);
        return Describe(entries[call], segments);
    }

    /// <summary>
    /// One call as the peer rule reads it: its side, state and process, the interface position and procedure its start
    /// carried, where its start is, and the thread that raised it. A call without a start has a start segment below zero;
    /// a thread below zero is one the index does not hold, as calls read from an operation index do not, and is read from
    /// the start's row.
    /// </summary>
    internal readonly record struct PeerFacts(
        RpcCallSide Side,
        RpcCallState State,
        int ProcessId,
        ProcessBinding Process,
        int Interface,
        long? Procedure,
        long StartTicks,
        int StartSegment,
        int StartRow,
        long StopTicks,
        bool HasStop,
        int Thread);

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
        // records' delivery order change no call (§3, I14). The keys a later generation's records can still change are
        // kept with their records, for that generation to extend these calls rather than pair them all again.
        int[] order = CanonicalOrder(collected, cancellationToken);
        long? latest = order.Length == 0 ? null : collected[order[^1]].Ticks;
        var tails = new Dictionary<CallKey, List<int>>();
        List<Entry> drafts = Walk(
            CollectionsMarshal.AsSpan(collected), order, cancellationToken, boundary: BoundaryOf(collected, order, Watermark(latest, clock)),
            tails: tails);
        return Assemble(
            CollectionsMarshal.AsSpan(drafts),
            processes,
            [.. interfaces],
            [.. segments.Select(segment => segment.Published?.Name)],
            clock,
            collected.Count,
            otherRecords,
            cancellationToken,
            read: (IdentitiesOf(segments), IdentitiesOf(fieldSegments)),
            tail: new(latest, Kept(collected, order, tails), [], [], []),
            kept: KeptRanks(tails, order.Length));
    }

    /// <summary>
    /// The calls of <paramref name="segments"/>, extended from these rather than paired again, when the segments and the
    /// source-field segments begin with every one these were paired from, in the same order, as a live capture's next
    /// generation's do (I14). Only the added segments are read, and only the keys these kept - those with a call open,
    /// or a record within <see cref="LateWindowNanoseconds"/> of the latest read - and the keys the added records name are
    /// paired again, each from a record after which it had no call open; every other call stands, bound again to
    /// <paramref name="processes"/>. The result is exactly <see cref="Derive"/>'s over the same segments. Null, for a full
    /// derivation to answer, when a segment read here is missing or another (after a compaction or a retention), when an
    /// added record with an activity id is read before that window and before the records kept of its key (it could change
    /// a call decided before them), or when an added source field names no added call start: a record's fields are
    /// published with it, so such a field could change a call these hold.
    /// </summary>
    public RpcCallIndex? Extend(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        if (tail is null || read is not { } before || processes.Clock != clock.Id)
        {
            return null;
        }

        StoreDependency?[] offered = IdentitiesOf(segments);
        StoreDependency?[] offeredFields = IdentitiesOf(fieldSegments);
        if (!Begins(offered, before.Segments) || !Begins(offeredFields, before.Fields))
        {
            return null;
        }

        Dictionary<RecordAddress, FieldSlot> fields = ReadFields(
            [.. Enumerable.Range(before.Fields.Length, fieldSegments.Count - before.Fields.Length).Select(position => fieldSegments[position])],
            cancellationToken);
        var known = new List<Guid>(interfaces);
        var knownIndex = new Dictionary<Guid, int>(interfaces.Length);
        for (int position = 0; position < interfaces.Length; position++)
        {
            knownIndex[interfaces[position]] = position;
        }

        var added = new List<CallRecord>();
        long otherRecords = OtherRpcRecords;
        for (int position = segmentNames.Length; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            otherRecords += Collect(segments[position], position, fields, known, knownIndex, added);
        }

        if (fields.Count > 0)
        {
            var starts = new HashSet<RecordAddress>(added.Where(record => record.IsStart).Select(record => record.Address));
            if (fields.Keys.Any(address => !starts.Contains(address)))
            {
                return null;
            }
        }

        // An added record of a key read within the window, or after the records kept of it, can change only calls these
        // kept; one read earlier than both could change a call decided before them.
        long watermark = Watermark(tail.LatestTicks, clock);
        foreach (CallRecord record in added)
        {
            if (record.HasActivity
                && record.Ticks < watermark
                && (!tail.Keys.TryGetValue(KeyOf(record), out CallRecord[]? kept) || CompareRecords(record, kept[0]) < 0))
            {
                return null;
            }
        }

        // Every kept key is paired again with the added records, from its kept records on, each of which begins where the
        // key had no call open; the calls those records made are withdrawn and replaced.
        var tracked = new List<CallRecord>(added.Count);
        foreach (CallRecord[] kept in tail.Keys.Values)
        {
            tracked.AddRange(kept);
        }

        tracked.AddRange(added);
        int[] order = CanonicalOrder(tracked, cancellationToken);
        long? latest = tail.LatestTicks;
        if (added.Count > 0)
        {
            long newest = added.Max(record => record.Ticks);
            latest = latest is { } previous ? Math.Max(previous, newest) : newest;
        }

        var tails = new Dictionary<CallKey, List<int>>();
        List<Entry> walked = Walk(
            CollectionsMarshal.AsSpan(tracked), order, cancellationToken, boundary: BoundaryOf(tracked, order, Watermark(latest, clock)),
            tails: tails);
        walked.Sort(static (left, right) => left.FirstRank.CompareTo(right.FirstRank));
        return Extended(
            walked,
            KeptRanks(tails, order.Length),
            processes,
            [.. known],
            [.. offered.Select(identity => identity!.Name)],
            CallRecords + added.Count,
            otherRecords,
            (offered, offeredFields),
            new(latest, Kept(tracked, order, tails), [], [], []),
            cancellationToken);
    }

    /// <summary>
    /// These calls with the calls paired again, <paramref name="walked"/>, in place of those the kept records made: the
    /// result of <see cref="Extend"/>. A call's rank in the canonical order of first records (§3) is its rank here, less
    /// the withdrawn calls before it, more the calls paired again before it, so first records are compared only where a
    /// call paired again falls among those that stand. Where every standing call binds to <paramref name="processes"/> as
    /// it binds here, each group's standing calls are carried in their order and the calls paired again for it dealt in by
    /// rank; otherwise every call is bound and grouped again, as a derivation does.
    /// </summary>
    /// <param name="walked">The calls paired again, in canonical order, each ranked by its first record in their walk.</param>
    /// <param name="kept">By rank in that walk, whether a later generation pairs the record again.</param>
    private RpcCallIndex Extended(
        List<Entry> walked,
        bool[] kept,
        ProcessInstanceIndex processes,
        Guid[] known,
        string?[] names,
        long callRecords,
        long otherRecords,
        (StoreDependency?[] Segments, StoreDependency?[] Fields) read,
        CallTail next,
        CancellationToken cancellationToken)
    {
        CallTail before = tail!;
        int[] withdrawn = before.KeptCalls;
        int[] follows = new int[walked.Count];
        int lower = 0;
        int below = 0;
        for (int index = 0; index < walked.Count; index++)
        {
            lower = FirstNotBefore(walked[index], lower, before.Canonical);
            while (below < withdrawn.Length && withdrawn[below] < lower)
            {
                below++;
            }

            follows[index] = lower - below;
        }

        int[] ranks = new int[entries.Length];
        int standing = 0;
        int dealt = 0;
        int skipped = 0;
        for (int rank = 0; rank < entries.Length; rank++)
        {
            if (skipped < withdrawn.Length && withdrawn[skipped] == rank)
            {
                ranks[rank] = -1;
                skipped++;
                continue;
            }

            while (dealt < walked.Count && follows[dealt] <= standing)
            {
                dealt++;
            }

            ranks[rank] = standing++ + dealt;
        }

        cancellationToken.ThrowIfCancellationRequested();
        for (int index = 0; index < walked.Count; index++)
        {
            walked[index] = walked[index] with
            {
                Process = processes.Bind(walked[index].ProcessId, walked[index].FirstTicks, isLifecycleRecord: false),
            };
        }

        int[] withdrawnAt = [.. withdrawn.Select(rank => before.Canonical[rank])];
        Array.Sort(withdrawnAt);
        int[] moved = new int[entries.Length];
        int[] walkedAt = new int[walked.Count];
        int[] canonical = new int[standing + walked.Count];
        Placed placed = Rebound(processes) is { } mapped
            && Carried(walked, ranks, follows, mapped, known, withdrawnAt, moved, walkedAt, canonical, cancellationToken) is { } carried
            ? carried
            : Regrouped(walked, ranks, follows, processes, known, moved, walkedAt, canonical, cancellationToken);

        // The calls paired again that a later generation pairs again in turn, by their ranks.
        var keptCalls = new List<int>();
        for (int index = 0; index < walked.Count; index++)
        {
            if (kept[walked[index].FirstRank])
            {
                keptCalls.Add(follows[index] + index);
            }
        }

        return new(placed.Entries, known, names, clock, processes, callRecords, otherRecords, placed.Groups, placed.Totals, read,
            next with { Canonical = canonical, KeptCalls = [.. keptCalls], Durations = placed.Durations },
            Lineage(walked, withdrawnAt, moved, walkedAt));
    }

    /// <summary>The first rank, from <paramref name="from"/> on, whose call's first record is not before <paramref name="call"/>'s.</summary>
    private int FirstNotBefore(in Entry call, int from, int[] canonical)
    {
        // Most calls paired again follow every call that stands, so the search gallops from where the last one fell.
        int low = from;
        int high = from;
        int step = 1;
        while (high < canonical.Length && CompareFirstRecords(entries[canonical[high]], call) < 0)
        {
            low = high + 1;
            high = (int)Math.Min(canonical.Length, (long)high + step);
            step *= 2;
        }

        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareFirstRecords(entries[canonical[middle]], call) < 0)
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

    /// <summary>
    /// How each instance a standing call binds to here is numbered in <paramref name="processes"/>, when every reading a
    /// group's calls bind by binds there as here: to the same instance, as strongly, or to none for the same reason. Null
    /// when one could bind otherwise, which moves its call to another group.
    /// </summary>
    private Dictionary<int, int>? Rebound(ProcessInstanceIndex processes)
    {
        // A group's calls are in reading order, so its first and last call bound its readings.
        var spans = new Dictionary<int, (long First, long Last)>();
        foreach (RpcCallGroup group in Groups)
        {
            long first = entries[group.First].FirstTicks;
            long last = entries[group.First + (int)group.Counts.Calls - 1].FirstTicks;
            spans[group.ProcessId] = spans.TryGetValue(group.ProcessId, out (long First, long Last) seen)
                ? (Math.Min(seen.First, first), Math.Max(seen.Last, last))
                : (first, last);
        }

        var mapped = new Dictionary<int, int>();
        foreach ((int processId, (long first, long last)) in spans)
        {
            if (!Processes.BindsAlike(processes, processId, first, last, mapped, sameReasons: true))
            {
                return null;
            }
        }

        return mapped;
    }

    /// <summary>
    /// The extended calls where every standing call binds as here: each group's standing calls carried in their order,
    /// its instance numbered as <paramref name="mapped"/> says, and the calls paired again for it dealt in by rank. Null
    /// where two groups would come to one, which only binding again sorts out.
    /// </summary>
    private Placed? Carried(
        List<Entry> walked,
        int[] ranks,
        int[] follows,
        Dictionary<int, int> mapped,
        Guid[] known,
        int[] withdrawnAt,
        int[] moved,
        int[] walkedAt,
        int[] canonical,
        CancellationToken cancellationToken)
    {
        var slots = new Dictionary<GroupKey, (int Group, List<int> Paired)>();
        for (int group = 0; group < Groups.Count; group++)
        {
            RpcCallGroup standing = Groups[group];
            ProcessBinding binding = standing.Process;
            if (binding.IsBound)
            {
                if (!mapped.TryGetValue(binding.Instance, out int instance))
                {
                    return null;
                }

                binding = binding with { Instance = instance };
            }

            if (!slots.TryAdd(new(standing.ProcessId, binding, standing.Side, entries[standing.First].Interface), (group, [])))
            {
                return null;
            }
        }

        for (int index = 0; index < walked.Count; index++)
        {
            ref (int Group, List<int> Paired) slot = ref CollectionsMarshal.GetValueRefOrAddDefault(slots, GroupKey.Of(walked[index]), out bool joins);
            if (!joins)
            {
                slot = (-1, []);
            }

            slot.Paired.Add(index);
        }

        GroupKey[] keys = [.. slots.Keys];
        Array.Sort(keys, GroupKeyOrder.Instance);
        Entry[] carried = GC.AllocateUninitializedArray<Entry>(canonical.Length);
        var groups = new List<(RpcCallGroup Group, long[] Durations)>(keys.Length);
        var totals = new Tally();
        int at = 0;
        foreach (GroupKey key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int group, List<int> paired) = slots[key];
            int from = group < 0 ? 0 : Groups[group].First;
            int to = group < 0 ? 0 : from + (int)Groups[group].Counts.Calls;
            int skip = LowerBound(withdrawnAt, from);
            int first = at;
            var tally = new Tally();
            var removed = new List<long>();
            var added = new List<long>();
            int dealt = 0;
            int position = from;
            while (true)
            {
                while (position < to && skip < withdrawnAt.Length && withdrawnAt[skip] == position)
                {
                    if (entries[position].State == RpcCallState.Completed)
                    {
                        removed.Add(Duration(clock, entries[position]));
                    }

                    moved[position++] = -1;
                    skip++;
                }

                int standingRank = position < to ? ranks[entries[position].FirstRank] : int.MaxValue;
                int pairedRank = dealt < paired.Count ? follows[paired[dealt]] + paired[dealt] : int.MaxValue;
                if (standingRank == int.MaxValue && pairedRank == int.MaxValue)
                {
                    break;
                }

                if (standingRank < pairedRank)
                {
                    carried[at] = entries[position] with { FirstRank = standingRank, Process = key.Process };
                    moved[position++] = at;
                    canonical[standingRank] = at;
                }
                else
                {
                    int index = paired[dealt++];
                    carried[at] = walked[index] with { FirstRank = pairedRank };
                    walkedAt[index] = at;
                    canonical[pairedRank] = at;
                    if (carried[at].State == RpcCallState.Completed)
                    {
                        added.Add(Duration(clock, carried[at]));
                    }
                }

                tally.Add(carried[at]);
                totals.Add(carried[at]);
                at++;
            }

            // A group whose every call was withdrawn, and none paired again for it, is gone.
            if (at > first)
            {
                long[] durations = Merged(group < 0 ? [] : tail!.Durations[group], removed, added);
                groups.Add((Grouped(carried[first], known, tally, durations, first), durations));
            }
        }

        (RpcCallGroup Group, long[] Durations)[] ordered = InDisplayOrder(groups);
        return new(carried, [.. ordered.Select(group => group.Group)], totals.Counts(), [.. ordered.Select(group => group.Durations)]);
    }

    /// <summary>
    /// The extended calls bound and grouped again, every one: where a standing call may bind otherwise than here, as a
    /// rundown or a process start read late can make it.
    /// </summary>
    private Placed Regrouped(
        List<Entry> walked,
        int[] ranks,
        int[] follows,
        ProcessInstanceIndex processes,
        Guid[] known,
        int[] moved,
        int[] walkedAt,
        int[] canonical,
        CancellationToken cancellationToken)
    {
        var drafted = new Entry[canonical.Length];
        int[] origins = new int[canonical.Length];
        int at = 0;
        for (int position = 0; position < entries.Length; position++)
        {
            int rank = ranks[entries[position].FirstRank];
            moved[position] = -1;
            if (rank >= 0)
            {
                origins[at] = position;
                drafted[at++] = entries[position] with { FirstRank = rank };
            }
        }

        int standing = at;
        for (int index = 0; index < walked.Count; index++)
        {
            drafted[at++] = walked[index] with { FirstRank = follows[index] + index };
        }

        int[] placed = new int[drafted.Length];
        Placed regrouped = Place(drafted, processes, known, clock, placed, canonical, kept: null, keptCalls: null, cancellationToken);
        for (int draft = 0; draft < standing; draft++)
        {
            moved[origins[draft]] = placed[draft];
        }

        for (int index = 0; index < walked.Count; index++)
        {
            walkedAt[index] = placed[standing + index];
        }

        return regrouped;
    }

    /// <summary>
    /// Where these calls went in the index extended from them, for its other ends to follow: a standing call to its place,
    /// and a withdrawn server call to the call paired again from its first record when the peer rule reads the two alike -
    /// the same start, on the same thread, of the same interface and procedure - as a server call still open is paired
    /// again, unchanged, generation after generation. The other calls paired again are new to the rule, and the earliest
    /// start of a server call it reads differently bounds which links can change.
    /// </summary>
    private CallLineage Lineage(List<Entry> walked, int[] withdrawnAt, int[] moved, int[] walkedAt)
    {
        var pairedFrom = new Dictionary<RecordAddress, int>(walked.Count);
        for (int index = 0; index < walked.Count; index++)
        {
            pairedFrom[walked[index].FirstAddress] = index;
        }

        bool[] repairs = new bool[walked.Count];
        long restarted = long.MaxValue;
        foreach (int position in withdrawnAt)
        {
            ref readonly Entry withdrawn = ref entries[position];
            if (withdrawn.Side != RpcCallSide.Server || withdrawn.StartSegment < 0)
            {
                continue;
            }

            if (pairedFrom.TryGetValue(withdrawn.FirstAddress, out int index) && ServesAlike(withdrawn, walked[index]))
            {
                moved[position] = walkedAt[index];
                repairs[index] = true;
            }
            else
            {
                restarted = Math.Min(restarted, withdrawn.StartTicks);
            }
        }

        var paired = new List<int>();
        for (int index = 0; index < walked.Count; index++)
        {
            if (repairs[index])
            {
                continue;
            }

            paired.Add(walkedAt[index]);
            if (walked[index].Side == RpcCallSide.Server && walked[index].StartSegment >= 0)
            {
                restarted = Math.Min(restarted, walked[index].StartTicks);
            }
        }

        return new(stamp, moved, [.. paired], restarted);

        static bool ServesAlike(in Entry one, in Entry other) =>
            other.Side == RpcCallSide.Server
            && other.StartSegment >= 0
            && one.ProcessId == other.ProcessId
            && one.StartThread == other.StartThread
            && one.StartTicks == other.StartTicks
            && one.Interface == other.Interface
            && one.HasProcedure == other.HasProcedure
            && one.Procedure == other.Procedure;
    }

    /// <summary>The sorted <paramref name="durations"/> without <paramref name="removed"/> and with <paramref name="added"/>, sorted.</summary>
    private static long[] Merged(long[] durations, List<long> removed, List<long> added)
    {
        removed.Sort();
        added.Sort();
        long[] merged = new long[durations.Length - removed.Count + added.Count];
        int kept = 0;
        int skip = 0;
        int next = 0;
        int at = 0;
        while (kept < durations.Length || next < added.Count)
        {
            if (kept < durations.Length && skip < removed.Count && durations[kept] == removed[skip])
            {
                kept++;
                skip++;
            }
            else if (next < added.Count && (kept >= durations.Length || added[next] < durations[kept]))
            {
                merged[at++] = added[next++];
            }
            else
            {
                merged[at++] = durations[kept++];
            }
        }

        return skip == removed.Count && at == merged.Length
            ? merged
            : throw new InvalidOperationException("A withdrawn call's duration is not among its group's.");
    }

    /// <summary>
    /// The first place in sorted <paramref name="values"/> holding <paramref name="value"/> or more. The places of withdrawn
    /// calls are distinct, each call having a place of its own, so a place holding the value is the only one.
    /// </summary>
    private static int LowerBound(int[] values, int value)
    {
        int found = Array.BinarySearch(values, value);
        return found < 0 ? ~found : found;
    }

    /// <summary>Which ranks of a walk the kept keys hold: the records a later generation pairs again.</summary>
    private static bool[] KeptRanks(Dictionary<CallKey, List<int>> tails, int ranks)
    {
        bool[] kept = new bool[ranks];
        foreach (List<int> held in tails.Values)
        {
            foreach (int rank in held)
            {
                kept[rank] = true;
            }
        }

        return kept;
    }

    /// <summary>
    /// The published identities of segments - name, length and digest - read without opening one where they declare
    /// them; null for a segment with none.
    /// </summary>
    internal static StoreDependency?[] IdentitiesOf(IReadOnlyList<SegmentReaderV1> segments) => segments is IPublishedSegments published
        ? [.. published.Published]
        : [.. segments.Select(segment => segment.Published)];

    /// <summary>
    /// Whether <paramref name="identities"/>, each published, begin with every one of <paramref name="before"/>, in
    /// order: the same files, not only the same names, which another session's segments can share.
    /// </summary>
    internal static bool Begins(StoreDependency?[] identities, StoreDependency?[] before)
    {
        if (identities.Length < before.Length || identities.Any(identity => identity is null))
        {
            return false;
        }

        for (int position = 0; position < before.Length; position++)
        {
            if (identities[position] != before[position])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The published identities of the segments and source-field segments the calls were paired from; null where nothing extends.</summary>
    internal (StoreDependency?[] Segments, StoreDependency?[] Fields)? Read => read;

    /// <summary>The earliest reading a later record may have and still extend calls whose latest record is <paramref name="latest"/>.</summary>
    private static long Watermark(long? latest, SourceClockDescriptor clock)
    {
        if (latest is not { } newest)
        {
            return long.MinValue;
        }

        Int128 ticks = (Int128)LateWindowNanoseconds * clock.TicksPerSecond / 1_000_000_000;
        long window = ticks > long.MaxValue ? long.MaxValue : (long)ticks;
        return newest < long.MinValue + window ? long.MinValue : newest - window;
    }

    /// <summary>The first rank, in canonical order, whose record is read at or after <paramref name="watermark"/>.</summary>
    private static int BoundaryOf(List<CallRecord> records, int[] order, long watermark)
    {
        int low = 0;
        int high = order.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (records[order[middle]].Ticks < watermark)
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

    /// <summary>Each kept key's records, by the ranks the walk kept, in canonical order.</summary>
    private static Dictionary<CallKey, CallRecord[]> Kept(List<CallRecord> records, int[] order, Dictionary<CallKey, List<int>> tails)
    {
        var kept = new Dictionary<CallKey, CallRecord[]>(tails.Count);
        foreach ((CallKey key, List<int> ranks) in tails)
        {
            kept[key] = [.. ranks.Select(rank => records[order[rank]])];
        }

        return kept;
    }

    private static CallKey KeyOf(in CallRecord record) => new(record.ProcessId, record.Side, record.Activity);

    /// <summary>Two records by the canonical order (§3): reading, then raw locator, then fact key.</summary>
    private static int CompareRecords(in CallRecord one, in CallRecord other)
    {
        int order = one.Ticks.CompareTo(other.Ticks);
        order = order != 0 ? order : one.Address.Stream.CompareTo(other.Address.Stream);
        order = order != 0 ? order : one.Address.Epoch.CompareTo(other.Address.Epoch);
        order = order != 0 ? order : one.Address.Ordinal.CompareTo(other.Address.Ordinal);
        order = order != 0 ? order : one.Address.FactKey.High.CompareTo(other.Address.FactKey.High);
        return order != 0 ? order : one.Address.FactKey.Low.CompareTo(other.Address.FactKey.Low);
    }

    /// <summary>
    /// Binds each call to its process by its first record's reading, orders the calls into their groups and counts them:
    /// what a derivation does once it has paired the calls, and a reader of an operation index once it has read them.
    /// </summary>
    /// <param name="placed">When given, receives where each drafted call is in the index made.</param>
    /// <param name="read">The segments and source-field segments read, when the index made may be extended.</param>
    /// <param name="tail">What a later generation can still change, when the index made may be extended.</param>
    /// <param name="kept">With <paramref name="tail"/>, by each call's rank, whether a later generation pairs its first record again.</param>
    private static RpcCallIndex Assemble(
        Span<Entry> drafted,
        ProcessInstanceIndex processes,
        Guid[] interfaces,
        string?[] segmentNames,
        SourceClockDescriptor clock,
        long callRecords,
        long otherRecords,
        CancellationToken cancellationToken,
        int[]? placed = null,
        (StoreDependency?[] Segments, StoreDependency?[] Fields)? read = null,
        CallTail? tail = null,
        bool[]? kept = null)
    {
        int[]? canonical = tail is null ? null : new int[drafted.Length];
        List<int>? keptCalls = tail is null ? null : [];
        Placed assembled = Place(drafted, processes, interfaces, clock, placed, canonical, kept, keptCalls, cancellationToken);
        return new(assembled.Entries, interfaces, segmentNames, clock, processes, callRecords, otherRecords, assembled.Groups,
            assembled.Totals, read,
            tail is null ? null : tail with { Canonical = canonical!, KeptCalls = [.. keptCalls!], Durations = assembled.Durations });
    }

    /// <summary>Binds each call by its first record's reading, deals the calls into their groups in reading order and counts them.</summary>
    private static Placed Place(
        Span<Entry> drafted,
        ProcessInstanceIndex processes,
        Guid[] interfaces,
        SourceClockDescriptor clock,
        int[]? placed,
        int[]? canonical,
        bool[]? kept,
        List<int>? keptCalls,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < drafted.Length; index++)
        {
            drafted[index] = drafted[index] with
            {
                Process = processes.Bind(drafted[index].ProcessId, drafted[index].FirstTicks, isLifecycleRecord: false),
            };
        }

        Entry[] entries = InGroupsInReadingOrder(drafted, cancellationToken, placed, canonical, kept, keptCalls);
        (RpcCallGroup[] groups, RpcCallCounts totals, long[][] durations) = Group(entries, interfaces, clock);
        return new(entries, groups, totals, durations);
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
        SegmentColumnSlice threads = default;
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
                threads = segment.Slice(SegmentColumnId.HeaderThreadId);
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
                flags,
                (int)(threads.UnsignedAt(row) ?? 0)));
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
    /// <param name="groups">When given, receives each call's records, and each ambiguous run's, by their canonical rank.</param>
    /// <param name="boundary">The first rank of the late window, from which <paramref name="tails"/> keeps every key's records.</param>
    /// <param name="tails">
    /// When given, receives the ranks of the records a later record can still re-pair: of every key with a call open at
    /// <paramref name="boundary"/>, from the first record of that call or its ambiguous run, and of every key with a record
    /// at or after it, from that record; each list begins where its key had no call open.
    /// </param>
    private static List<Entry> Walk(
        ReadOnlySpan<CallRecord> records,
        int[] order,
        CancellationToken cancellationToken,
        List<int[]>? groups = null,
        int boundary = int.MaxValue,
        Dictionary<CallKey, List<int>>? tails = null)
    {
        var calls = new List<Entry>((records.Length / 2) + 1);
        var open = new Dictionary<CallKey, OpenCall>();
        for (int rank = 0; rank < order.Length; rank++)
        {
            if ((rank & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (rank == boundary && tails is not null)
            {
                KeepOpen(open, tails);
            }

            ref readonly CallRecord record = ref records[order[rank]];
            if (!record.HasActivity)
            {
                calls.Add(Single(record, RpcCallState.NoActivityId, rank));
                groups?.Add([rank]);
                continue;
            }

            var key = new CallKey(record.ProcessId, record.Side, record.Activity);
            if (tails is not null && rank >= boundary)
            {
                ref List<int>? kept = ref CollectionsMarshal.GetValueRefOrAddDefault(tails, key, out _);
                (kept ??= []).Add(rank);
            }

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
                    groups?.Add([rank]);
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
                groups?.Add([.. run]);
            }
            else
            {
                calls.Add(Paired(records[order[closed.First]], record, closed.First));
                groups?.Add([closed.First, rank]);
            }
        }

        if (tails is not null && boundary >= order.Length)
        {
            KeepOpen(open, tails);
        }

        foreach (OpenCall still in open.Values)
        {
            if (still.Run is { } run)
            {
                AddAmbiguous(records, order, run, calls);
                groups?.Add([.. run]);
            }
            else
            {
                calls.Add(Single(records[order[still.First]], RpcCallState.OpenAtCaptureEnd, still.First));
                groups?.Add([still.First]);
            }
        }

        return calls;
    }

    /// <summary>Keeps every key with a call open, from that call's first record, or its ambiguous run's: its every record so far.</summary>
    private static void KeepOpen(Dictionary<CallKey, OpenCall> open, Dictionary<CallKey, List<int>> tails)
    {
        foreach ((CallKey key, OpenCall state) in open)
        {
            tails[key] = state.Run is { } run ? [.. run] : [state.First];
        }
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
        StartThread = start.Thread,
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
            StartThread = record.Thread,
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
            StartThread = -1,
        };

    /// <summary>
    /// The calls grouped by process binding, side and interface, the groups in the order of those identities and each
    /// group's calls in the canonical order of their first records (§3), which is what a channel lists and a page reads.
    /// Every call's first record has a canonical rank of its own, so the calls are taken in rank order by placing each at
    /// its rank, and dealt into their groups in that order: nothing is compared but the groups' identities.
    /// </summary>
    /// <remarks>
    /// Each call placed is ranked by its place in that order, so <see cref="Entry.FirstRank"/> numbers the index's calls
    /// from 0 without a gap, whatever the drafts were ranked by.
    /// </remarks>
    /// <param name="canonical">When given, receives the index's call positions in the canonical order of their first records.</param>
    /// <param name="kept">When given, by each draft's rank, whether a later generation pairs its first record again.</param>
    /// <param name="keptCalls">With <paramref name="kept"/>, receives the ranks of those calls, in order.</param>
    private static Entry[] InGroupsInReadingOrder(
        ReadOnlySpan<Entry> drafted,
        CancellationToken cancellationToken,
        int[]? placed = null,
        int[]? canonical = null,
        bool[]? kept = null,
        List<int>? keptCalls = null)
    {
        var numbered = new Dictionary<GroupKey, int>();
        int[] provisional = new int[drafted.Length];
        int ranks = 0;
        for (int index = 0; index < drafted.Length; index++)
        {
            ref int number = ref CollectionsMarshal.GetValueRefOrAddDefault(numbered, GroupKey.Of(drafted[index]), out bool known);
            if (!known)
            {
                number = numbered.Count - 1;
            }

            provisional[index] = number;
            ranks = Math.Max(ranks, drafted[index].FirstRank + 1);
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
        int[] next = new int[identities.Length + 1];
        int[] byRank = new int[ranks];
        Array.Fill(byRank, -1);
        for (int index = 0; index < drafted.Length; index++)
        {
            next[final[provisional[index]] + 1]++;
            if (byRank[drafted[index].FirstRank] >= 0)
            {
                throw new InvalidOperationException("Two calls begin with the same record.");
            }

            byRank[drafted[index].FirstRank] = index;
        }

        for (int group = 1; group < next.Length; group++)
        {
            next[group] += next[group - 1];
        }

        var entries = new Entry[drafted.Length];
        int ranked = 0;
        foreach (int index in byRank)
        {
            if (index < 0)
            {
                continue;
            }

            int at = next[final[provisional[index]]]++;
            entries[at] = drafted[index] with { FirstRank = ranked };
            if (placed is not null)
            {
                placed[index] = at;
            }

            if (canonical is not null)
            {
                canonical[ranked] = at;
            }

            if (kept is not null && kept[drafted[index].FirstRank])
            {
                keptCalls!.Add(ranked);
            }

            ranked++;
        }

        return entries;
    }

    /// <summary>
    /// Counts each group of consecutive entries, and every entry, and orders the groups by calls; each group's completed
    /// calls' durations, sorted, come in the groups' order, for an extension to carry.
    /// </summary>
    private static (RpcCallGroup[] Groups, RpcCallCounts Totals, long[][] Durations) Group(
        Entry[] entries, Guid[] interfaces, SourceClockDescriptor clock)
    {
        var groups = new List<(RpcCallGroup Group, long[] Durations)>();
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

            durations.Sort();
            long[] sorted = [.. durations];
            groups.Add((Grouped(entries[first], interfaces, tally, sorted, first), sorted));
            first = end;
        }

        (RpcCallGroup Group, long[] Durations)[] ordered = InDisplayOrder(groups);
        return ([.. ordered.Select(group => group.Group)], totals.Counts(), [.. ordered.Select(group => group.Durations)]);
    }

    /// <summary>The group whose first call is <paramref name="head"/>, at <paramref name="first"/>, counted and timed.</summary>
    private static RpcCallGroup Grouped(in Entry head, Guid[] interfaces, Tally tally, long[] durations, int first) => new()
    {
        ProcessId = head.ProcessId,
        Process = head.Process,
        Side = head.Side,
        Interface = head.Interface < 0 ? null : interfaces[head.Interface],
        Counts = tally.Counts(),
        Durations = Distribution(durations),
        First = first,
    };

    /// <summary>Groups taken in the order of their identities, the one with the most calls first, then by PID, instance, side and interface.</summary>
    private static (RpcCallGroup Group, long[] Durations)[] InDisplayOrder(List<(RpcCallGroup Group, long[] Durations)> groups) =>
        [.. groups
            .OrderByDescending(group => group.Group.Counts.Calls)
            .ThenBy(group => group.Group.ProcessId)
            .ThenBy(group => group.Group.Process.Instance)
            .ThenBy(group => group.Group.Side)
            .ThenBy(group => group.Group.Interface)];

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

    /// <summary>The distribution of durations already sorted.</summary>
    private static RpcCallDurations? Distribution(long[] sorted) => sorted.Length == 0
        ? null
        : new(sorted.Length, sorted[0], NearestRank(sorted, 50), NearestRank(sorted, 95), sorted[^1]);

    /// <summary>The smallest duration at least <paramref name="percent"/>% of the durations do not exceed.</summary>
    private static long NearestRank(IReadOnlyList<long> sorted, int percent) =>
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
        CallFlags Flags,
        int Thread)
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
    /// What a later generation's records can still change (<see cref="Extend"/>): the latest reading of any call record
    /// read; the records of each key with a call open, or with a record within the late window of that reading, from a
    /// record before which it had no call open; the index's calls in the canonical order of their first records; the
    /// ranks of the calls those kept records made, which a later generation withdraws and pairs again; and each group's
    /// completed calls' durations, sorted, by the groups' order.
    /// </summary>
    private sealed record CallTail(
        long? LatestTicks, Dictionary<CallKey, CallRecord[]> Keys, int[] Canonical, int[] KeptCalls, long[][] Durations);

    /// <summary>Calls bound, dealt into their groups in reading order and counted; each group's sorted durations by the groups' order.</summary>
    private readonly record struct Placed(Entry[] Entries, RpcCallGroup[] Groups, RpcCallCounts Totals, long[][] Durations);

    /// <summary>
    /// Where the calls of the index an index was extended from went in it (<see cref="Lineage"/>), for the calls' other
    /// ends to be extended alike.
    /// </summary>
    /// <param name="From">The index extended from, by its <see cref="stamp"/>, which keeps none of it.</param>
    /// <param name="Moved">By that index's position, this one's for the same call, or -1 for a call withdrawn.</param>
    /// <param name="Paired">The positions of the calls paired again that no call moved to.</param>
    /// <param name="Restarted">
    /// The earliest start of a server call withdrawn or paired again that the peer rule reads differently; the latest
    /// reading there is where none was.
    /// </param>
    internal sealed record CallLineage(object From, int[] Moved, int[] Paired, long Restarted);

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

        /// <summary>
        /// Where its first record is in the canonical order (§3): in an index, the call's place among the index's calls,
        /// from 0; in a walk's drafts, the record's place among the records walked.
        /// </summary>
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

        /// <summary>The thread that raised the call's start, which the peer rule reads; -1 without a start, or unknown.</summary>
        public int StartThread { get; init; }

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

        public void Add(in Entry entry)
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
