using System.Runtime.InteropServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>What became of one cooperative operation, as its records say (`contracts/sdk-v1.md` §3, ADR-109).</summary>
public enum CooperativeOperationState
{
    /// <summary>Started once and completed once, both in one process.</summary>
    Completed = 1,

    /// <summary>Started once, its completion recorded nowhere: running when the capture ended, or its record lost.</summary>
    Open = 2,

    /// <summary>Completed, with no start recorded: started before the capture, or its start's record lost.</summary>
    StartUnseen = 3,

    /// <summary>
    /// Started or completed more than once, or started and completed in two processes: what the application said breaks the
    /// schema's word that an operation starts once and completes once, in the process that holds it, so none of its records
    /// is chosen over another.
    /// </summary>
    Conflicting = 4,
}

/// <summary>One record of a cooperative operation: the process that raised it, when, and that process's own clock there.</summary>
public sealed record CooperativeOperationRecord
{
    public required int ProcessId { get; init; }

    public required ProcessBinding Process { get; init; }

    /// <summary>The record's source reading.</summary>
    public required long Ticks { get; init; }

    /// <summary>Its session time, in nanoseconds; null when the record has none.</summary>
    public long? SessionNanoseconds { get; init; }

    /// <summary>The raising process's own clock reading at it, as the record states it; null where it carries none.</summary>
    public long? SourceTicks { get; init; }

    /// <summary>The record, which names it in every generation that holds it.</summary>
    public required (uint Stream, uint Epoch, ulong Ordinal) Address { get; init; }
}

/// <summary>
/// One cooperative operation (ADR-109): its identity, the operation its start names as its parent, its name, and its start
/// and completion as the application said them through the SDK - each the application's own assertion, never an
/// observation of the work beneath it.
/// </summary>
public sealed record CooperativeOperation
{
    public required Guid Operation { get; init; }

    /// <summary>The operation its first recorded start names as its parent; null for one started under none, or never recorded starting.</summary>
    public Guid? Parent { get; init; }

    /// <summary>The name its first recorded start gives it; null where no start was recorded.</summary>
    public string? Name { get; init; }

    /// <summary>Its first recorded start; null where none was.</summary>
    public CooperativeOperationRecord? Start { get; init; }

    /// <summary>How many starts of it were recorded: one, or none, or - conflicting - more.</summary>
    public required int Starts { get; init; }

    /// <summary>Its first recorded completion; null where none was.</summary>
    public CooperativeOperationRecord? Completion { get; init; }

    /// <summary>
    /// The status its first recorded completion states: 0 its success, any other value a failure in the application's own
    /// code; null where no completion was recorded, or it kept none.
    /// </summary>
    public long? Status { get; init; }

    /// <summary>How many completions of it were recorded: one, or none, or - conflicting - more.</summary>
    public required int Completions { get; init; }

    public required CooperativeOperationState State { get; init; }

    /// <summary>
    /// How long it ran: from its start's record to its completion's, in session time, for a completed operation whose two
    /// records both have one; null otherwise.
    /// </summary>
    public long? DurationNanoseconds => State == CooperativeOperationState.Completed
        && Start?.SessionNanoseconds is { } started && Completion?.SessionNanoseconds is { } completed
        ? completed - started
        : null;

    /// <summary>Where it was first recorded among the generation's records: its place in a listing.</summary>
    internal long First { get; init; }
}

/// <summary>Where an operation stands in its tree as a listing walks it.</summary>
public enum CooperativeOperationPlace
{
    /// <summary>Started under no operation, or never recorded starting: a tree's root.</summary>
    Root = 1,

    /// <summary>Under an operation this generation recorded.</summary>
    Child = 2,

    /// <summary>Started under an operation this generation never recorded: another capture's, or one begun before it.</summary>
    UnderUnrecorded = 3,

    /// <summary>
    /// Under operations whose parents name each other round, which no start can - each started under one made before it - so
    /// no root reaches it; or under one of those.
    /// </summary>
    Circular = 4,
}

/// <summary>One operation as a listing walks the tree: how deep it lies, and where it stands.</summary>
public sealed record CooperativeOperationNode(CooperativeOperation Operation, int Depth, CooperativeOperationPlace Place);

/// <summary>
/// The cooperative SDK's operations of one generation (ADR-109, M7): each start and completion the SDK's provider raised,
/// keyed by its operation's identity, and each operation placed under the parent its start names - another process's, where
/// the application carried its identity there. A record names an operation only by the identity it carries: one whose
/// identity or process the capture did not keep names none, and is counted apart rather than keyed by a value never
/// observed (R3). It is a derivation over published segments alone, rebuildable from retained evidence (R20), and it
/// changes no observation (R1).
/// </summary>
public sealed class CooperativeOperationIndex
{
    /// <summary>The rule that keys records into operations and places them under their parents, named in every query identity that reads by it (R20).</summary>
    public const string Rule = "cooperative-operation-v1";

    private readonly CooperativeOperation[] operations;
    private readonly Dictionary<Guid, CooperativeOperation> byIdentity;
    private readonly Dictionary<Guid, List<CooperativeOperation>> children;

    private CooperativeOperationIndex(CooperativeOperation[] operations, ProcessInstanceIndex processes, long withoutOperation)
    {
        this.operations = operations;
        Processes = processes;
        WithoutOperation = withoutOperation;
        byIdentity = operations.ToDictionary(operation => operation.Operation);

        // Each operation under the parent its start names, in the order first recorded.
        children = [];
        foreach (CooperativeOperation operation in operations)
        {
            if (operation.Parent is { } parent)
            {
                ref List<CooperativeOperation>? under = ref CollectionsMarshal.GetValueRefOrAddDefault(children, parent, out _);
                (under ??= []).Add(operation);
            }
        }

        UnrecordedParents = [.. operations.Where(operation => operation.Parent is { } parent && !byIdentity.ContainsKey(parent))
            .Select(operation => operation.Parent!.Value).Distinct()];
        Tree = Walk();
    }

    /// <summary>Every operation, in the order first recorded.</summary>
    public IReadOnlyList<CooperativeOperation> Operations => operations;

    public ProcessInstanceIndex Processes { get; }

    /// <summary>Operation records of the SDK that name no operation: its identity or its process was not kept.</summary>
    public long WithoutOperation { get; }

    /// <summary>Each operation a start names as its parent that this generation never recorded, in the order first named.</summary>
    public IReadOnlyList<Guid> UnrecordedParents { get; }

    /// <summary>
    /// Every operation as a listing walks the tree: each root, in the order first recorded, then everything under it, depth
    /// first, each operation's children in the order first recorded; then, for each parent never recorded, the operations
    /// started under it and everything under them; then whatever no root reaches - parents naming each other round.
    /// </summary>
    public IReadOnlyList<CooperativeOperationNode> Tree { get; }

    /// <summary>The operation of this identity; null where the generation recorded none.</summary>
    public CooperativeOperation? Find(Guid operation) => byIdentity.GetValueOrDefault(operation);

    /// <summary>The operations started under <paramref name="operation"/>, in the order first recorded.</summary>
    public IReadOnlyList<CooperativeOperation> ChildrenOf(Guid operation) =>
        children.TryGetValue(operation, out List<CooperativeOperation>? under) ? under : [];

    /// <summary>
    /// Reads each SDK record's clock reading from <paramref name="fieldSegments"/>, keys the operation records of
    /// <paramref name="segments"/> into operations, binds each record's process, and places each operation under its parent.
    /// </summary>
    public static CooperativeOperationIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        Dictionary<RecordAddress, long> sourceTicks = ReadSourceTicks(fieldSegments, cancellationToken);
        var gathered = new Dictionary<Guid, Gathered>();
        long withoutOperation = 0;
        long ordinal = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutOperation += Collect(segment, sourceTicks, processes, gathered, ref ordinal);
        }

        return new([.. gathered.Select(pair => Operation(pair.Key, pair.Value)).OrderBy(operation => operation.First)], processes,
            withoutOperation);
    }

    /// <summary>
    /// One operation from its records: its first start's parent and name, its first completion's status, and what became of
    /// it - conflicting where it was started or completed twice, or in two processes, none of its records chosen.
    /// </summary>
    private static CooperativeOperation Operation(Guid identity, Gathered gathered)
    {
        Started? start = gathered.Starts.Count > 0 ? gathered.Starts[0] : null;
        Completed? completion = gathered.Completions.Count > 0 ? gathered.Completions[0] : null;
        bool twoProcesses = gathered.Starts.Select(started => started.Record.ProcessId)
            .Concat(gathered.Completions.Select(completed => completed.Record.ProcessId)).Distinct().Count() > 1;
        return new()
        {
            Operation = identity,
            Parent = start?.Parent,
            Name = start?.Name,
            Start = start?.Record,
            Starts = gathered.Starts.Count,
            Completion = completion?.Record,
            Status = completion?.Status,
            Completions = gathered.Completions.Count,
            State = gathered.Starts.Count > 1 || gathered.Completions.Count > 1 || twoProcesses ? CooperativeOperationState.Conflicting
                : start is null ? CooperativeOperationState.StartUnseen
                : completion is null ? CooperativeOperationState.Open
                : CooperativeOperationState.Completed,
            First = gathered.First,
        };
    }

    /// <summary>
    /// Walks the tree depth first, an explicit stack in place of recursion so a deep chain of operations cannot exhaust the
    /// thread's: roots, then each unrecorded parent's operations, then whatever neither reaches.
    /// </summary>
    private CooperativeOperationNode[] Walk()
    {
        var walked = new List<CooperativeOperationNode>(operations.Length);
        var visited = new HashSet<Guid>();
        void From(CooperativeOperation top, CooperativeOperationPlace place)
        {
            var pending = new Stack<(CooperativeOperation Operation, int Depth, CooperativeOperationPlace Place)>();
            pending.Push((top, 0, place));
            while (pending.TryPop(out (CooperativeOperation Operation, int Depth, CooperativeOperationPlace Place) next))
            {
                if (!visited.Add(next.Operation.Operation))
                {
                    continue;
                }

                walked.Add(new(next.Operation, next.Depth, next.Place));
                IReadOnlyList<CooperativeOperation> under = ChildrenOf(next.Operation.Operation);
                CooperativeOperationPlace below = place == CooperativeOperationPlace.Circular ? CooperativeOperationPlace.Circular
                    : CooperativeOperationPlace.Child;
                for (int at = under.Count - 1; at >= 0; at--)
                {
                    pending.Push((under[at], next.Depth + 1, below));
                }
            }
        }

        foreach (CooperativeOperation operation in operations.Where(operation => operation.Parent is null))
        {
            From(operation, CooperativeOperationPlace.Root);
        }

        foreach (Guid parent in UnrecordedParents)
        {
            foreach (CooperativeOperation operation in ChildrenOf(parent))
            {
                From(operation, CooperativeOperationPlace.UnderUnrecorded);
            }
        }

        foreach (CooperativeOperation operation in operations)
        {
            From(operation, CooperativeOperationPlace.Circular);
        }

        return [.. walked];
    }

    private static Dictionary<RecordAddress, long> ReadSourceTicks(IReadOnlyList<SegmentReaderV1> fieldSegments, CancellationToken cancellationToken)
    {
        var read = new Dictionary<RecordAddress, long>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            SegmentColumnSlice streams = default, epochs = default, ordinals = default, factHigh = default, factLow = default,
                values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((SourceField)(ushort)codes.UnsignedAt(row)!.Value != SourceField.SourceTicks)
                {
                    continue;
                }

                // Only a segment holding a clock reading opens the columns it needs.
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
                if (!read.TryAdd(address, value))
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries its clock reading in more than one source-field segment; a duplicated field "
                        + "could move its operation's time.");
                }
            }
        }

        return read;
    }

    private static long Collect(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, long> sourceTicks,
        ProcessInstanceIndex processes,
        Dictionary<Guid, Gathered> gathered,
        ref long ordinal)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        long without = 0;
        bool opened = false;
        SegmentColumnSlice ticks = default, streams = default, epochs = default, ordinals = default, factHigh = default,
            factLow = default, sessionTimes = default, identifiers = default, related = default, statuses = default;
        var owners = new RecordOwnerColumns(segment);
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ApplicationSdk)
            {
                continue;
            }

            // A record is the SDK's by its provider: the content fixture's records share the mechanism and number from 1 too.
            (Guid provider, ushort eventId, _, _) = segment.SchemaOf(row);
            if (provider != CooperativeSdk.ProviderId || eventId is not (CooperativeSdk.OperationStarted or CooperativeSdk.OperationCompleted))
            {
                continue;
            }

            // Only a segment holding the SDK's operations opens the columns an operation needs.
            if (!opened)
            {
                ticks = segment.Slice(SegmentColumnId.NativeTicks);
                streams = segment.Slice(SegmentColumnId.RawStreamId);
                epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
                factLow = segment.Slice(SegmentColumnId.FactKeyLow);
                sessionTimes = segment.Slice(SegmentColumnId.SessionRelativeTicks);
                identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
                related = segment.Slice(SegmentColumnId.RelatedActivityId);
                statuses = segment.Slice(SegmentColumnId.StatusCode);
                opened = true;
            }

            // A record whose operation or process was not kept names no operation.
            if (identifiers.GuidAt(row) is not { } operation || operation == Guid.Empty
                || owners.At(row, Mechanism.ApplicationSdk) is not { } processId)
            {
                without++;
                continue;
            }

            var address = new RecordAddress(
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value));
            long at = ticks.SignedAt(row)!.Value;
            var record = new CooperativeOperationRecord
            {
                ProcessId = processId,
                Process = processes.Bind(processId, at, isLifecycleRecord: false),
                Ticks = at,
                SessionNanoseconds = sessionTimes.SignedAt(row),
                SourceTicks = sourceTicks.TryGetValue(address, out long reading) ? reading : null,
                Address = (address.Stream, address.Epoch, address.Ordinal),
            };
            ref Gathered? held = ref CollectionsMarshal.GetValueRefOrAddDefault(gathered, operation, out _);
            held ??= new() { First = ordinal };
            ordinal++;
            if (eventId == CooperativeSdk.OperationStarted)
            {
                // A start's parent is its related activity, ETW's own link; the empty identity names none.
                Guid? parent = related.GuidAt(row) is { } named && named != Guid.Empty ? named : null;
                held.Starts.Add(new(record, parent, segment.TextValue(SegmentColumnId.ResourceName, row)));
            }
            else
            {
                held.Completions.Add(new(record, statuses.SignedAt(row)));
            }
        }

        return without;
    }

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    private sealed record Started(CooperativeOperationRecord Record, Guid? Parent, string? Name);

    private sealed record Completed(CooperativeOperationRecord Record, long? Status);

    /// <summary>An operation's starts and completions, gathered as read, and where it was first recorded.</summary>
    private sealed class Gathered
    {
        public required long First { get; init; }

        public List<Started> Starts { get; } = [];

        public List<Completed> Completions { get; } = [];
    }
}
