using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// How one process instance held a resource instance, by the records of it bound to it (`contracts/resources-v1.md` §3):
/// the handles it made, those duplicated into it, those it closed, and those it held at the stop; and for a section by its
/// views, the views it mapped, those it unmapped, and those a rundown listed it holding at the capture's start or stop.
/// </summary>
public sealed record ResourceHolder(
    ProcessInstanceId Process,
    int HandlesMade,
    int HandlesDuplicated,
    int HandlesClosed,
    int HeldAtStop,
    int ViewsMapped = 0,
    int ViewsUnmapped = 0,
    int ViewsListed = 0);

/// <summary>
/// One mapping of a section's view by one process, as its view's records pair it (`contracts/resources-v1.md` §1, ADR-092):
/// the readings of its mapping and its unmapping where the capture saw them, whether the start's rundown listed it - mapped
/// before the capture - and whether the stop's held it. A reading the capture did not see is null; a mapping whose two
/// readings were both seen is the one whose lifetime is measured, never one with an end unseen.
/// </summary>
/// <param name="View">The number the view gate gave the view's address.</param>
/// <param name="Holder">The process instance its records bind to under the policy; null where they bind to none.</param>
public sealed record ResourceView(
    long View,
    ProcessInstanceId? Holder,
    long? MappedNativeTicks,
    long? UnmappedNativeTicks,
    bool MappedBefore,
    bool HeldAtStop);

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
/// synchronization object by its object number, a section by its handles' or by its views', never joined, an AF_UNIX socket
/// by its socket's - from its first record to its last, named as its closes and the stop's rundown name it, held by the
/// process instances its records bind to. A synchronization object's and a section's records are handles and views, never
/// traffic: none carries a signal, a wait or a byte through shared memory (P27); a socket's sends and receives are its
/// traffic, and one with none was idle.
/// </summary>
public sealed record ResourceInstance
{
    public required Mechanism Mechanism { get; init; }

    /// <summary>
    /// The layer its records are of, as its source admits them: `Resource` for a synchronization object's and a section's,
    /// `Transport` for an AF_UNIX socket's. A layer projection keeps or drops a resource by it.
    /// </summary>
    public ObservationLayer Layer { get; init; } = ObservationLayer.Resource;

    /// <summary>The number the capture's gate gave this incarnation, never a kernel address; never zero.</summary>
    public required long Number { get; init; }

    /// <summary>
    /// Whether it is a section found by its views - the number the view gate gives the value its views carry - rather than by
    /// its handles; the two numbers are never joined, so one section can be two instances (ADR-092).
    /// </summary>
    public bool ByViews { get; init; }

    /// <summary>For a section by its views, whether they are an image's - their extra information's high byte 0x47 - a module loaded, not memory a process shared.</summary>
    public bool Image { get; init; }

    /// <summary>For a section by its views, how many views of it the capture saw; zero for any other.</summary>
    public int Views { get; init; }

    /// <summary>
    /// For a section by its views, each mapping of one of its views by one process, in the order of its first reading: what
    /// a mapping lifetime measures; empty for any other.
    /// </summary>
    public IReadOnlyList<ResourceView> Mappings { get; init; } = [];

    /// <summary>For a section by its views, the largest extent one of its views mapped, in bytes; null where no view's size was recorded.</summary>
    public long? LargestView { get; init; }

    /// <summary>Its object type's name, as the stop's rundown names the type's number; null where no rundown named it.</summary>
    public string? TypeName { get; init; }

    /// <summary>The names its closes and the stop's rundown carry, each as recorded, in the order first given; empty where none named it.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    public required long FirstNativeTicks { get; init; }

    public required long LastNativeTicks { get; init; }

    /// <summary>
    /// Whether its first record is a handle made, a socket created or a view mapped in the capture, rather than a close, a
    /// transfer, an unmapping or the rundown of one made before it.
    /// </summary>
    public required bool FirstHandleMade { get; init; }

    /// <summary>Whether its last record is a close or an unmapping: its end was seen.</summary>
    public required bool LastHandleClosed { get; init; }

    /// <summary>Whether its last record is the stop's rundown: a handle or a view of it was held at the stop.</summary>
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
/// `resource-instances-v3` (`contracts/resources-v1.md`, ADR-097): a generation's resource instances, derived from the
/// numbers its gates wrote over objects' addresses - each named synchronization object's, each section's by its handles and
/// by its views, never joined, and each AF_UNIX socket's - with each one's lifetime, names, type, traffic and the process
/// instances its records bind to. Version 1 derived no socket, and version 2 no section by its views.
/// </summary>
public sealed class ResourceInstanceIndex
{
    public const string Rule = "resource-instances-v3";

    // The opcode of the kernel's rundown of the views mapped at a session's start (ADR-090): a view it lists was mapped
    // before the capture. The stop's, 40, lists a view held at the stop; a handle's rundown, at the stop alone, is 39 too.
    private const ulong ViewStartRundown = 39;

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
        var drafts = new Dictionary<(Mechanism Mechanism, bool ByViews, long Number), Draft>();
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

            Draft draft = drafts.TryGetValue((Mechanism.UnixDomainSocket, false, number), out Draft? held)
                ? held
                : drafts[(Mechanism.UnixDomainSocket, false, number)] = new() { CarriesTraffic = true };
            draft.Add(socket.Mark, null, null, socket.Owner);
            draft.Transfer(socket.Mark.Kind, socket.Bytes);
        }

        return new([.. drafts
            .OrderBy(entry => entry.Key.Mechanism)
            .ThenBy(entry => entry.Key.ByViews)
            .ThenBy(entry => entry.Key.Number)
            .Select(entry => entry.Value.ToInstance(entry.Key.Mechanism, entry.Key.ByViews, entry.Key.Number, processes, typeNames))]);
    }

    /// <summary>
    /// The records of one resource instance - those of <paramref name="mechanism"/> whose source field carries
    /// <paramref name="number"/>, the number its gate gave it - by segment and row, each segment's in its order: what E lists
    /// as exactly a resource's records (ADR-099). A section by its views is read by the number its views carry, one by its
    /// handles by its handles', never the two joined (ADR-092); zero is no number, and names no record.
    /// </summary>
    public static IReadOnlyList<(int Segment, int Row)> RecordsOf(
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        Mechanism mechanism,
        bool byViews,
        long number,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(fieldSegments);
        SourceField field = mechanism switch
        {
            Mechanism.Synchronization => SourceField.SynchronizationObjectNumber,
            Mechanism.SharedSection => byViews ? SourceField.SectionNumber : SourceField.SectionObjectNumber,
            Mechanism.UnixDomainSocket => SourceField.ConnectionId,
            _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "No resource of this mechanism is derived."),
        };
        if (number == 0)
        {
            return [];
        }

        // The records whose field carries the number, by their address; then each one's row in its observation segment,
        // of its mechanism alone, since a TCP record's connection id shares the field with a socket's number.
        var addresses = new HashSet<RecordAddress>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            Locator locator = default;
            SegmentColumnSlice values = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((SourceField)(ushort)codes.UnsignedAt(row)!.Value != field)
                {
                    continue;
                }

                if (!opened)
                {
                    locator = new Locator(segment);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                if (values.SignedAt(row) == number)
                {
                    addresses.Add(locator.At(row));
                }
            }
        }

        var records = new List<(int Segment, int Row)>();
        for (int index = 0; index < segments.Count && addresses.Count > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = segments[index];
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            bool opened = false;
            Locator locator = default;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != mechanism)
                {
                    continue;
                }

                if (!opened)
                {
                    locator = new Locator(segment);
                    opened = true;
                }

                if (addresses.Contains(locator.At(row)))
                {
                    records.Add((index, row));
                }
            }
        }

        return records;
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
                if (code is not (SourceField.SynchronizationObjectNumber or SourceField.SectionObjectNumber or SourceField.ObjectTypeNumber
                    or SourceField.SectionNumber or SourceField.SectionViewNumber or SourceField.ViewSize or SourceField.ViewInformation))
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
                fields[address] = code switch
                {
                    SourceField.ObjectTypeNumber => carried with { Type = value },
                    SourceField.SectionNumber => carried with { ViewSection = value },
                    SourceField.SectionViewNumber => carried with { View = value },
                    SourceField.ViewSize => carried with { Size = value },
                    SourceField.ViewInformation => carried with { Information = value },
                    _ => carried with { Number = value },
                };
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
        Dictionary<(Mechanism, bool, long), Draft> drafts,
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
        SegmentColumnSlice opcodes = default;
        SegmentColumnSlice layers = default;
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
                opcodes = segment.Slice(SegmentColumnId.Opcode);
                layers = segment.Slice(SegmentColumnId.Layer);
                owners = processes.OwnersOf(segment);
                opened = true;
            }

            RecordAddress address = locator.At(row);
            ProcessBinding owner = owners![row] is { IsBound: true } admitted && !admitted.IsAdmittedUnder(policy)
                ? ProcessBinding.Unresolved(ProcessBindingReason.NotAdmittedByPolicy)
                : owners[row];
            var layer = (ObservationLayer)layers.UnsignedAt(row)!.Value;
            if (mechanism == Mechanism.UnixDomainSocket)
            {
                // A transfer's bytes are known where its record measured them, and unmeasured where it did not (R3).
                socketAt[address] = sockets.Count;
                sockets.Add(new SocketRow(
                    new Mark(ticks.SignedAt(row)!.Value, address, (ObservationKind)kinds.UnsignedAt(row)!.Value,
                        (Direction)directions.UnsignedAt(row)!.Value, layer),
                    (FieldAvailability)availability.UnsignedAt(row)!.Value == FieldAvailability.Present ? bytes.SignedAt(row) : null,
                    owner));
                continue;
            }

            if (!fields.TryGetValue(address, out Carried carried))
            {
                continue;
            }

            var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;

            // A view's record carries the number its gate gave the value its section's views carry: that section by its views,
            // never joined to the number its handles carry (ADR-092). Both its rundowns are inventories, and only its opcode
            // tells the start's, which lists a view mapped before the capture, from the stop's, which lists one held at it.
            if (mechanism == Mechanism.SharedSection && carried.ViewSection is { } viewSection)
            {
                if (viewSection != 0)
                {
                    Draft views = drafts.TryGetValue((mechanism, true, viewSection), out Draft? seen)
                        ? seen
                        : drafts[(mechanism, true, viewSection)] = new() { ByViews = true };
                    var mark = new Mark(ticks.SignedAt(row)!.Value, address, kind, (Direction)directions.UnsignedAt(row)!.Value, layer,
                        StartRundown: kind == ObservationKind.Inventory && opcodes.UnsignedAt(row) == ViewStartRundown);
                    views.Add(mark, null, null, owner);
                    views.View(carried.View, carried.Size, carried.Information);
                    views.Mapping(carried.View, mark, owner);
                }

                continue;
            }

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

            Draft draft = drafts.TryGetValue((mechanism, false, number), out Draft? held) ? held : drafts[(mechanism, false, number)] = new();
            draft.Add(
                new Mark(ticks.SignedAt(row)!.Value, address, kind, (Direction)directions.UnsignedAt(row)!.Value, layer),
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

    // A record's place and what it says of its instance's handles or views: a view's rundown at the session's start is an
    // inventory as the stop's is, and says nothing of the stop.
    private readonly record struct Mark(
        long Ticks, RecordAddress Address, ObservationKind Kind, Direction Direction, ObservationLayer Layer, bool StartRundown = false)
        : IComparable<Mark>
    {
        public int CompareTo(Mark other) => Ticks != other.Ticks ? Ticks.CompareTo(other.Ticks) : Address.CompareTo(other.Address);

        // A handle's creation, one duplicated into its process, a socket's create or a view's mapping: made in the capture.
        public bool Made => Kind is ObservationKind.Open or ObservationKind.Map;

        // The stop's rundown of a handle or a view: held at the stop.
        public bool AtStop => Kind == ObservationKind.Inventory && !StartRundown;
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
        private long sends;
        private long receives;
        private long bytesSent;
        private long bytesReceived;
        private long unmeasured;
        private readonly HashSet<long> views = [];
        private long? largestView;
        private bool image;

        // Each view's records by the process they bind to - an unbound one's apart, as -1 - which pair into its mappings.
        private readonly Dictionary<(long View, int Holder), List<Mark>> mappings = [];

        /// <summary>Whether its mechanism's records carry transfers: an AF_UNIX socket's.</summary>
        public bool CarriesTraffic { get; init; }

        /// <summary>Whether its records are views': a section by its views.</summary>
        public bool ByViews { get; init; }

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

        // A view's record: the view's number, its extent and whether it is an image's.
        public void View(long? view, long? size, long? information)
        {
            if (view is { } number)
            {
                views.Add(number);
            }

            if (size is { } extent && (largestView is null || extent > largestView))
            {
                largestView = extent;
            }

            image |= information is { } extra && (ulong)extra >> 48 == 0x47;
        }

        // A view's record kept with the others of its view and its process, to be paired into its mappings.
        public void Mapping(long? view, Mark mark, ProcessBinding owner)
        {
            if (view is not { } number)
            {
                return;
            }

            (long, int) key = (number, owner.IsBound ? owner.Instance : -1);
            (mappings.TryGetValue(key, out List<Mark>? marks) ? marks : mappings[key] = []).Add(mark);
        }

        // A view's records in time, paired: a mapping, or the start's rundown listing one made before the capture, opens one;
        // its unmapping closes it, or the stop's rundown holds it. Another mapping before an unmapping leaves the open one's end
        // unseen, and an unmapping or a stop's rundown with none open is one whose mapping the capture did not see.
        private List<ResourceView> Paired(ProcessInstanceIndex processes)
        {
            var paired = new List<ResourceView>();
            foreach (((long view, int holder), List<Mark> marks) in mappings)
            {
                ProcessInstanceId? process = holder >= 0 ? processes.Instances[holder].Id : null;
                marks.Sort();
                (long? Mapped, bool Before)? open = null;
                foreach (Mark mark in marks)
                {
                    switch (mark.Kind)
                    {
                        case ObservationKind.Map:
                        case ObservationKind.Inventory when mark.StartRundown:
                            if (open is { } unclosed)
                            {
                                paired.Add(new(view, process, unclosed.Mapped, null, unclosed.Before, false));
                            }

                            open = mark.Kind == ObservationKind.Map ? (mark.Ticks, false) : (null, true);
                            break;
                        case ObservationKind.Unmap:
                        case ObservationKind.Inventory:
                            bool held = mark.Kind == ObservationKind.Inventory;
                            paired.Add(open is { } closing
                                ? new(view, process, closing.Mapped, held ? null : mark.Ticks, closing.Before, held)
                                : new(view, process, null, held ? null : mark.Ticks, false, held));
                            open = null;
                            break;
                    }
                }

                if (open is { } left)
                {
                    paired.Add(new(view, process, left.Mapped, null, left.Before, false));
                }
            }

            return [.. paired
                .OrderBy(mapping => mapping.MappedNativeTicks ?? mapping.UnmappedNativeTicks ?? long.MaxValue)
                .ThenBy(mapping => mapping.View)];
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
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }

            if (!owner.IsBound)
            {
                unbound[owner.Reason] = unbound.GetValueOrDefault(owner.Reason) + 1;
                return;
            }

            // A handle made, duplicated, closed or held at the stop, and a view mapped, unmapped or listed by a rundown, is
            // counted; a socket's call or transfer names its holder.
            int[] counts = holders.TryGetValue(owner.Instance, out int[]? known) ? known : holders[owner.Instance] = new int[7];
            int? slot = mark.Kind switch
            {
                ObservationKind.Open when mark.Direction == Direction.Inbound => 1,
                ObservationKind.Open => 0,
                ObservationKind.Close => 2,
                ObservationKind.Inventory when ByViews => 6,
                ObservationKind.Inventory => 3,
                ObservationKind.Map => 4,
                ObservationKind.Unmap => 5,
                _ => null,
            };
            if (slot is { } counted)
            {
                counts[counted]++;
            }
        }

        public ResourceInstance ToInstance(Mechanism mechanism, bool byViews, long number, ProcessInstanceIndex processes, Dictionary<long, string> typeNames) => new()
        {
            Mechanism = mechanism,
            Layer = first.Layer,
            Number = number,
            ByViews = byViews,
            Image = image,
            Views = views.Count,
            Mappings = ByViews ? Paired(processes) : [],
            LargestView = largestView,
            TypeName = type is { } known && typeNames.TryGetValue(known, out string? named) ? named : null,
            Names = names.AsReadOnly(),
            FirstNativeTicks = first.Ticks,
            LastNativeTicks = last.Ticks,
            FirstHandleMade = first.Made,
            LastHandleClosed = last.Kind is ObservationKind.Close or ObservationKind.Unmap,
            HeldAtStop = last.AtStop,
            Holders =
            [
                .. holders
                    .OrderBy(entry => processes.Instances[entry.Key].ProcessId)
                    .ThenBy(entry => processes.Instances[entry.Key].LifecycleEpoch)
                    .Select(entry => new ResourceHolder(processes.Instances[entry.Key].Id, entry.Value[0], entry.Value[1], entry.Value[2],
                        entry.Value[3], entry.Value[4], entry.Value[5], entry.Value[6])),
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

    // What a record's source fields carry: its object's number and its type's, or a view's number, its section's by its
    // views, its extent and its extra information; each null where it carries none.
    private readonly record struct Carried(long? Number, long? Type, long? ViewSection = null, long? View = null, long? Size = null, long? Information = null);
}
