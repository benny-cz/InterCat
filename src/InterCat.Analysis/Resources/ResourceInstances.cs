using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// How one process instance held a resource instance, by the records of it bound to it (`contracts/resources-v1.md` §3):
/// the handles it made, those duplicated into it, those it closed, and those it held at the stop.
/// </summary>
public sealed record ResourceHolder(ProcessInstanceId Process, int HandlesMade, int HandlesDuplicated, int HandlesClosed, int HeldAtStop);

/// <summary>
/// The transfers a resource's records carried (`contracts/resources-v1.md` §1): its sends and receives, the bytes of those
/// whose size was recorded, and how many recorded none. A resource that carried none was idle - discovered, its traffic
/// none observed - which a mechanism recording no transfer never says of its resources (R3).
/// </summary>
public sealed record ResourceTraffic(long Sends, long Receives, long BytesSent, long BytesReceived, long UnmeasuredTransfers)
{
    /// <summary>Whether it carried no transfer the capture saw.</summary>
    public bool Idle => Sends == 0 && Receives == 0;
}

/// <summary>
/// One resource instance (`contracts/resources-v1.md` §1, ADR-097): one gate number of one capture's mechanism - a named
/// synchronization object by its object number, a section by its handles', an AF_UNIX socket by its socket's - from its
/// first record to its last, named as its closes and the stop's rundown name it, held by the process instances its records
/// bind to. A synchronization object's and a section's records are handles, never traffic: none carries a signal, a wait or
/// a byte through shared memory (P27); a socket's sends and receives are its traffic, and one with none was idle.
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

    /// <summary>
    /// The transfers its records carried, where its mechanism records any - an AF_UNIX socket's sends and receives - and
    /// null where it records none: a synchronization object's or a section's, never said to be idle (R3).
    /// </summary>
    public ResourceTraffic? Traffic { get; init; }

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
/// `resource-instances-v2` (`contracts/resources-v1.md`, ADR-097): a generation's resource instances, derived from the
/// numbers its gates wrote over objects' addresses - each named synchronization object's, each section's by its handles and
/// each AF_UNIX socket's - with each one's lifetime, names, type, traffic and the process instances its records bind to.
/// Version 1 derived no socket.
/// </summary>
public sealed class ResourceInstanceIndex
{
    public const string Rule = "resource-instances-v2";

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
        var sockets = new List<SocketRow>();
        var socketAt = new Dictionary<RecordAddress, int>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collect(segment, fields, processes, policy, drafts, typeNames, sockets, socketAt);
        }

        // A socket's number is the source field its gate writes over the socket's address, read for its rows alone; a row
        // its gate numbered zero, or not at all, is no socket's (identity-v1).
        NumberSockets(fieldSegments, sockets, socketAt, cancellationToken);
        foreach (SocketRow socket in sockets)
        {
            if (socket.Number is not { } number || number == 0)
            {
                continue;
            }

            Draft draft = drafts.TryGetValue((Mechanism.UnixDomainSocket, number), out Draft? held)
                ? held
                : drafts[(Mechanism.UnixDomainSocket, number)] = new() { CarriesTraffic = true };
            draft.Add(socket.Mark, null, null, socket.Owner);
            draft.Transfer(socket.Mark.Kind, socket.Bytes);
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

    // Reads one segment's handle records into their instances' drafts and the stop's object types into their names, and
    // keeps its AF_UNIX rows for their sockets' numbers, which the source fields give once every segment is read.
    private static void Collect(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, Carried> fields,
        ProcessInstanceIndex processes,
        EvidencePolicy policy,
        Dictionary<(Mechanism, long), Draft> drafts,
        Dictionary<long, string> typeNames,
        List<SocketRow> sockets,
        Dictionary<RecordAddress, int> socketAt)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        bool opened = false;
        Locator locator = default;
        SegmentColumnSlice kinds = default;
        SegmentColumnSlice directions = default;
        SegmentColumnSlice ticks = default;
        SegmentColumnSlice bytes = default;
        SegmentColumnSlice availability = default;
        ProcessBinding[]? owners = null;
        for (int row = 0; row < segment.RowCount; row++)
        {
            var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
            if (mechanism is not (Mechanism.Synchronization or Mechanism.SharedSection or Mechanism.UnixDomainSocket))
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
                bytes = segment.Slice(SegmentColumnId.ByteValue);
                availability = segment.Slice(SegmentColumnId.ByteAvailability);
                owners = processes.OwnersOf(segment);
                opened = true;
            }

            RecordAddress address = locator.At(row);
            ProcessBinding owner = owners![row] is { IsBound: true } admitted && !admitted.IsAdmittedUnder(policy)
                ? ProcessBinding.Unresolved(ProcessBindingReason.NotAdmittedByPolicy)
                : owners[row];
            if (mechanism == Mechanism.UnixDomainSocket)
            {
                // A transfer's bytes are known where its record measured them, and unmeasured where it did not (R3).
                socketAt[address] = sockets.Count;
                sockets.Add(new SocketRow(
                    new Mark(ticks.SignedAt(row)!.Value, address, (ObservationKind)kinds.UnsignedAt(row)!.Value,
                        (Direction)directions.UnsignedAt(row)!.Value),
                    (FieldAvailability)availability.UnsignedAt(row)!.Value == FieldAvailability.Present ? bytes.SignedAt(row) : null,
                    owner));
                continue;
            }

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
                owner);
        }
    }

    // The socket each kept AF_UNIX row is of, from the source field its gate wrote: read for those rows alone.
    private static void NumberSockets(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        List<SocketRow> sockets,
        Dictionary<RecordAddress, int> socketAt,
        CancellationToken cancellationToken)
    {
        if (sockets.Count == 0)
        {
            return;
        }

        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            Locator locator = default;
            SegmentColumnSlice values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((SourceField)(ushort)codes.UnsignedAt(row)!.Value != SourceField.ConnectionId)
                {
                    continue;
                }

                if (!opened)
                {
                    locator = new Locator(segment);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                if (socketAt.TryGetValue(locator.At(row), out int at) && values.SignedAt(row) is { } number)
                {
                    sockets[at].Number = number;
                }
            }
        }
    }

    // An AF_UNIX row while its socket's number is read: its place, its measured bytes, and the process it binds to.
    private sealed class SocketRow(Mark mark, long? bytes, ProcessBinding owner)
    {
        public Mark Mark { get; } = mark;

        public long? Bytes { get; } = bytes;

        public ProcessBinding Owner { get; } = owner;

        public long? Number { get; set; }
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
        private long sends;
        private long receives;
        private long bytesSent;
        private long bytesReceived;
        private long unmeasured;

        /// <summary>Whether its mechanism's records carry transfers: an AF_UNIX socket's.</summary>
        public bool CarriesTraffic { get; init; }

        // A send or a receive, its bytes where its record measured them.
        public void Transfer(ObservationKind kind, long? bytes)
        {
            if (kind is not (ObservationKind.Send or ObservationKind.Receive))
            {
                return;
            }

            if (kind == ObservationKind.Send)
            {
                sends++;
                bytesSent += bytes ?? 0;
            }
            else
            {
                receives++;
                bytesReceived += bytes ?? 0;
            }

            unmeasured += bytes is null ? 1 : 0;
        }

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

            // A handle made, duplicated, closed or held at the stop is counted; a socket's call or transfer names its holder.
            int[] counts = holders.TryGetValue(owner.Instance, out int[]? known) ? known : holders[owner.Instance] = new int[4];
            int? slot = mark.Kind switch
            {
                ObservationKind.Open when mark.Direction == Direction.Inbound => 1,
                ObservationKind.Open => 0,
                ObservationKind.Close => 2,
                ObservationKind.Inventory => 3,
                _ => null,
            };
            if (slot is { } counted)
            {
                counts[counted]++;
            }
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
            Traffic = CarriesTraffic ? new(sends, receives, bytesSent, bytesReceived, unmeasured) : null,
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
