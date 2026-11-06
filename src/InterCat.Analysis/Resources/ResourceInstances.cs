using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// How one process instance held a resource instance, by the records of it bound to it (`contracts/resources-v1.md` §3):
/// the handles it made, those duplicated into it, those it closed, and those it held at the stop.
/// </summary>
public sealed record ResourceHolder(ProcessInstanceId Process, int HandlesMade, int HandlesDuplicated, int HandlesClosed, int HeldAtStop);

/// <summary>
/// One resource instance (`contracts/resources-v1.md` §1, ADR-097): one gate number of one capture's mechanism - a named
/// synchronization object by its object number, a section by its handles' - from its first record to its last, named as
/// its closes and the stop's rundown name it, held by the process instances its records bind to. Its records are handle
/// records, never traffic: no record of these mechanisms carries a signal, a wait or a byte through shared memory (P27).
/// </summary>
public sealed record ResourceInstance
{
    public required Mechanism Mechanism { get; init; }

    /// <summary>The number the capture's gate gave this incarnation, never a kernel address; never zero.</summary>
    public required long Number { get; init; }

    /// <summary>Its object type's name, as the stop's rundown names the type's number; null where no rundown named it.</summary>
    public string? TypeName { get; init; }

    /// <summary>The names its closes and the stop's rundown carry, each as recorded, in the order first given; empty where none named it.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    public required long FirstNativeTicks { get; init; }

    public required long LastNativeTicks { get; init; }

    /// <summary>Whether its first record is a handle made in the capture, rather than a close or the rundown of one made before it.</summary>
    public required bool FirstHandleMade { get; init; }

    /// <summary>Whether its last record is a close and no handle of it was held at the stop: its end was seen.</summary>
    public required bool LastHandleClosed { get; init; }

    /// <summary>Whether a handle of it was held at the stop, as the stop's rundown lists it.</summary>
    public required bool HeldAtStop { get; init; }

    /// <summary>The process instances its records bind to, by PID and then by which of the PID's holders each was.</summary>
    public required IReadOnlyList<ResourceHolder> Holders { get; init; }

    /// <summary>Its records bound to no process instance, by why (`entities-v1` §4); never a holder guessed.</summary>
    public required IReadOnlyDictionary<ProcessBindingReason, long> Unbound { get; init; }

    public required long Records { get; init; }

    /// <summary>The name a person reads it by: the first its records gave; null where none named it.</summary>
    public string? Name => Names.Count > 0 ? Names[0] : null;
}

/// <summary>
/// A resource's name as browsing reads it (`contracts/resources-v1.md` §2): the object directory it lies in - its namespace,
/// such as <c>\Sessions\1\BaseNamedObjects</c> - and its leaf, the rest. The name itself is kept as recorded beside them.
/// </summary>
public static class ResourceNames
{
    /// <summary>The object directory <paramref name="name"/> lies in: all before its last backslash; null for a name with none.</summary>
    public static string? NamespaceOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int at = name.LastIndexOf('\\');
        return at <= 0 ? at == 0 ? "\\" : null : name[..at];
    }

    /// <summary>The leaf of <paramref name="name"/>: all after its last backslash, or the whole name where it has none.</summary>
    public static string LeafOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name[(name.LastIndexOf('\\') + 1)..];
    }
}

/// <summary>
/// `resource-instances-v1` (`contracts/resources-v1.md`, ADR-097): a generation's resource instances, derived from the
/// numbers its gates wrote over objects' addresses - each named synchronization object's and each section's by its handles
/// - with each one's lifetime, names, type and the process instances its records bind to.
/// </summary>
public sealed class ResourceInstanceIndex
{
    public const string Rule = "resource-instances-v1";

    private ResourceInstanceIndex(IReadOnlyList<ResourceInstance> instances) => Instances = instances;

    /// <summary>Every instance, by mechanism and then by number.</summary>
    public IReadOnlyList<ResourceInstance> Instances { get; }

    /// <summary>
    /// The resource instances of the generation whose observation <paramref name="segments"/> and source-field
    /// <paramref name="fieldSegments"/> are given, their records bound to <paramref name="processes"/>' instances as
    /// <paramref name="policy"/> admits: a binding it does not admit names no holder, and is counted by that reason.
    /// </summary>
    public static ResourceInstanceIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        ProcessInstanceIndex processes,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        ArgumentNullException.ThrowIfNull(processes);
        Dictionary<RecordAddress, Carried> fields = ReadFields(fieldSegments, cancellationToken);
        var drafts = new Dictionary<(Mechanism Mechanism, long Number), Draft>();
        var typeNames = new Dictionary<long, string>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collect(segment, fields, processes, policy, drafts, typeNames);
        }

        return new([.. drafts
            .OrderBy(entry => entry.Key.Mechanism)
            .ThenBy(entry => entry.Key.Number)
            .Select(entry => entry.Value.ToInstance(entry.Key.Mechanism, entry.Key.Number, processes, typeNames))]);
    }

    // The numbers and types the source-field segments carry, by the record they belong to.
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
                if (code is not (SourceField.SynchronizationObjectNumber or SourceField.SectionObjectNumber or SourceField.ObjectTypeNumber))
                {
                    continue;
                }

                // Only a segment holding a resource's fields opens the columns they need.
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
                fields[address] = code == SourceField.ObjectTypeNumber ? carried with { Type = value } : carried with { Number = value };
            }
        }

        return fields;
    }

    // Reads one segment's handle records into their instances' drafts, and the stop's object types into their names.
    private static void Collect(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, Carried> fields,
        ProcessInstanceIndex processes,
        EvidencePolicy policy,
        Dictionary<(Mechanism, long), Draft> drafts,
        Dictionary<long, string> typeNames)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        bool opened = false;
        Locator locator = default;
        SegmentColumnSlice kinds = default;
        SegmentColumnSlice directions = default;
        SegmentColumnSlice ticks = default;
        ProcessBinding[]? owners = null;
        for (int row = 0; row < segment.RowCount; row++)
        {
            var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
            if (mechanism is not (Mechanism.Synchronization or Mechanism.SharedSection))
            {
                continue;
            }

            // Only a segment holding a resource's records opens the columns, and binds its rows.
            if (!opened)
            {
                locator = new Locator(segment);
                kinds = segment.Slice(SegmentColumnId.ObservationKind);
                directions = segment.Slice(SegmentColumnId.Direction);
                ticks = segment.Slice(SegmentColumnId.NativeTicks);
                owners = processes.OwnersOf(segment);
                opened = true;
            }

            RecordAddress address = locator.At(row);
            if (!fields.TryGetValue(address, out Carried carried))
            {
                continue;
            }

            var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;
            string? name = segment.TextValue(SegmentColumnId.ResourceName, row);

            // An object type at the stop carries a type's number and its name, and no object.
            if (carried.Number is not { } number)
            {
                if (kind == ObservationKind.Inventory && carried.Type is { } type && !string.IsNullOrEmpty(name))
                {
                    typeNames.TryAdd(type, name);
                }

                continue;
            }

            // Zero is no number: an address the gate could not number stays no instance (identity-v1).
            if (number == 0)
            {
                continue;
            }

            Draft draft = drafts.TryGetValue((mechanism, number), out Draft? held) ? held : drafts[(mechanism, number)] = new();
            draft.Add(
                new Mark(ticks.SignedAt(row)!.Value, address, kind, (Direction)directions.UnsignedAt(row)!.Value),
                kind is ObservationKind.Close or ObservationKind.Inventory ? name : null,
                carried.Type,
                owners![row] is { IsBound: true } bound && !bound.IsAdmittedUnder(policy)
                    ? ProcessBinding.Unresolved(ProcessBindingReason.NotAdmittedByPolicy)
                    : owners[row]);
        }
    }

    // A record's place and what it says of its instance's handles.
    private readonly record struct Mark(long Ticks, RecordAddress Address, ObservationKind Kind, Direction Direction) : IComparable<Mark>
    {
        public int CompareTo(Mark other) => Ticks != other.Ticks ? Ticks.CompareTo(other.Ticks) : Address.CompareTo(other.Address);

        // A handle's creation, or one duplicated into its process: a handle made in the capture.
        public bool Made => Kind == ObservationKind.Open;
    }

    // One instance while its records are read.
    private sealed class Draft
    {
        private readonly List<string> names = [];
        private readonly Dictionary<int, int[]> holders = [];
        private readonly Dictionary<ProcessBindingReason, long> unbound = [];
        private Mark first;
        private Mark last;
        private long records;
        private long? type;
        private bool heldAtStop;

        public void Add(Mark mark, string? name, long? markType, ProcessBinding owner)
        {
            if (records == 0 || mark.CompareTo(first) < 0)
            {
                first = mark;
            }

            if (records == 0 || mark.CompareTo(last) > 0)
            {
                last = mark;
            }

            records++;
            type ??= markType;
            heldAtStop |= mark.Kind == ObservationKind.Inventory;
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }

            if (!owner.IsBound)
            {
                unbound[owner.Reason] = unbound.GetValueOrDefault(owner.Reason) + 1;
                return;
            }

            int[] counts = holders.TryGetValue(owner.Instance, out int[]? known) ? known : holders[owner.Instance] = new int[4];
            counts[mark.Kind switch
            {
                ObservationKind.Open when mark.Direction == Direction.Inbound => 1,
                ObservationKind.Open => 0,
                ObservationKind.Close => 2,
                _ => 3,
            }]++;
        }

        public ResourceInstance ToInstance(Mechanism mechanism, long number, ProcessInstanceIndex processes, Dictionary<long, string> typeNames) => new()
        {
            Mechanism = mechanism,
            Number = number,
            TypeName = type is { } known && typeNames.TryGetValue(known, out string? named) ? named : null,
            Names = names.AsReadOnly(),
            FirstNativeTicks = first.Ticks,
            LastNativeTicks = last.Ticks,
            FirstHandleMade = first.Made,
            LastHandleClosed = last.Kind == ObservationKind.Close && !heldAtStop,
            HeldAtStop = heldAtStop,
            Holders =
            [
                .. holders
                    .OrderBy(entry => processes.Instances[entry.Key].ProcessId)
                    .ThenBy(entry => processes.Instances[entry.Key].LifecycleEpoch)
                    .Select(entry => new ResourceHolder(processes.Instances[entry.Key].Id, entry.Value[0], entry.Value[1], entry.Value[2], entry.Value[3])),
            ],
            Unbound = unbound,
            Records = records,
        };
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

    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey) : IComparable<RecordAddress>
    {
        public int CompareTo(RecordAddress other) =>
            Stream != other.Stream ? Stream.CompareTo(other.Stream)
            : Epoch != other.Epoch ? Epoch.CompareTo(other.Epoch)
            : Ordinal != other.Ordinal ? Ordinal.CompareTo(other.Ordinal)
            : FactKey.High != other.FactKey.High ? FactKey.High.CompareTo(other.FactKey.High)
            : FactKey.Low.CompareTo(other.FactKey.Low);
    }

    // The object's number and its type's number a record carries; either is null where it carries none.
    private readonly record struct Carried(long? Number, long? Type);
}
