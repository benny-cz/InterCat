using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// One connection whose two ends are each held by exactly one process instance: the relation §7.4's network
/// correlator proves between them (`contracts/relations-v1.md`). Endpoints are as the source names them, never
/// normalized into an identity of their own (R22).
/// </summary>
public sealed record TransportRelation
{
    public required Mechanism Mechanism { get; init; }

    /// <summary>The instance holding the end whose endpoint sorts first.</summary>
    public required ProcessInstance First { get; init; }

    /// <summary>That end's own endpoint, as the source names it.</summary>
    public required string FirstEndpoint { get; init; }

    /// <summary>The instance holding the other end. Equal to <see cref="First"/> for a process connected to itself.</summary>
    public required ProcessInstance Second { get; init; }

    public required string SecondEndpoint { get; init; }

    /// <summary>`Correlated`, or `Candidate` when either end's records bind to its instance only as a candidate.</summary>
    public required RelationStrength Strength { get; init; }

    /// <summary>The earliest and latest native reading of any record at either end.</summary>
    public required long FirstNativeTicks { get; init; }

    public required long LastNativeTicks { get; init; }

    /// <summary>How many records the two ends hold together.</summary>
    public required long Records { get; init; }
}

/// <summary>
/// Derives the other end of every transport record of one capture (`tcp-endpoint-relation-v1`). A record names its own
/// endpoint first and the remote one second, on every admitted TCP descriptor; the record holding the mirrored pair is
/// the other end of the same connection. Where every record at that other end binds to one process instance, that
/// instance is the record's peer. Nothing is paired by time, by address alone or by the nearest holder (§7.4, P6).
/// </summary>
/// <remarks>
/// It reads every segment once to learn who holds each end, then resolves a segment's rows on demand. Its state is
/// one entry per connection end rather than per row, so a long capture's millions of transfers over a few thousand
/// connections cost a few thousand entries.
/// </remarks>
public sealed class TransportRelationIndex
{
    /// <summary>The rule identity a result names when it used these relations (§24 `entityRevision`).</summary>
    public const string RelationRule = "tcp-endpoint-relation-v1";

    private readonly ProcessInstanceIndex processes;
    private readonly Dictionary<EndKey, EndState> ends;

    private TransportRelationIndex(ProcessInstanceIndex processes, Dictionary<EndKey, EndState> ends)
    {
        this.processes = processes;
        this.ends = ends;
        Relations = BuildRelations(processes, ends);
    }

    /// <summary>Every connection whose two ends each have one holder, in a stable order.</summary>
    public IReadOnlyList<TransportRelation> Relations { get; }

    /// <summary>Derives the relations of the segments <paramref name="processes"/> was derived from.</summary>
    public static TransportRelationIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);
        var ends = new Dictionary<EndKey, EndState>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EndKey?[] keys = KeysOf(segment);
            SegmentColumnSlice owners = segment.Slice(SegmentColumnId.OwnerProcessId);
            SegmentColumnSlice ticks = segment.Slice(SegmentColumnId.NativeTicks);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (keys[row] is not { } key)
                {
                    continue;
                }

                if (!ends.TryGetValue(key, out EndState? state))
                {
                    state = new();
                    ends[key] = state;
                }

                long reading = ticks.SignedAt(row)!.Value;
                long? owner = owners.SignedAt(row);
                state.Observe(reading, owner is { } pid ? (int)pid : null, processes.Bind(
                    owner is { } bound ? (int)bound : null, reading, isLifecycleRecord: false));
            }
        }

        return new(processes, ends);
    }

    /// <summary>
    /// The other end of every row of a segment: the instance holding it, with the relation's strength, or why there
    /// is none. A row outside every relation rule's mechanisms says so rather than looking unresolved for want of
    /// evidence.
    /// </summary>
    public ProcessBinding[] PeersOf(SegmentReaderV1 segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        EndKey?[] keys = KeysOf(segment);
        var peers = new ProcessBinding[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Tcp)
            {
                peers[row] = ProcessBinding.Unresolved(ProcessBindingReason.NoRelationRule);
                continue;
            }

            peers[row] = keys[row] is { } key
                ? PeerOf(key)
                : ProcessBinding.Unresolved(ProcessBindingReason.PeerEndpointIncomplete);
        }

        return peers;
    }

    private ProcessBinding PeerOf(EndKey key)
    {
        if (!ends.TryGetValue(key.Mirror(), out EndState? other))
        {
            return ProcessBinding.Unresolved(ProcessBindingReason.PeerNotObserved);
        }

        if (other.Ambiguous)
        {
            return ProcessBinding.Unresolved(ProcessBindingReason.PeerAmbiguous);
        }

        return other.Holder < 0
            ? ProcessBinding.Unresolved(ProcessBindingReason.PeerUnbound)
            : new(other.Holder, Weaker(RelationStrength.Correlated, other.Weakest), ProcessBindingReason.Bound);
    }

    /// <summary>
    /// The end each row describes: its own endpoint, then the remote one, as every admitted TCP descriptor names them.
    /// A row of another mechanism, or with a missing or zero address or port, has none.
    /// </summary>
    private static EndKey?[] KeysOf(SegmentReaderV1 segment)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice families = segment.Slice(SegmentColumnId.EndpointAddressFamily);
        SegmentColumnSlice localAddresses = segment.Slice(SegmentColumnId.SourceEndpointAddress);
        SegmentColumnSlice localPorts = segment.Slice(SegmentColumnId.SourceEndpointPort);
        SegmentColumnSlice remoteAddresses = segment.Slice(SegmentColumnId.DestinationEndpointAddress);
        SegmentColumnSlice remotePorts = segment.Slice(SegmentColumnId.DestinationEndpointPort);
        var keys = new EndKey?[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            // An unavailable address is not zero, and a zero one names no endpoint: neither can find the other end.
            if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Tcp
                || families.UnsignedAt(row) is not { } family
                || localAddresses.UnsignedAt(row) is not ({ } localAddress and not 0UL)
                || localPorts.UnsignedAt(row) is not ({ } localPort and not 0UL)
                || remoteAddresses.UnsignedAt(row) is not ({ } remoteAddress and not 0UL)
                || remotePorts.UnsignedAt(row) is not ({ } remotePort and not 0UL))
            {
                continue;
            }

            keys[row] = new((byte)family, (uint)localAddress, (ushort)localPort, (uint)remoteAddress, (ushort)remotePort);
        }

        return keys;
    }

    private static List<TransportRelation> BuildRelations(ProcessInstanceIndex processes, Dictionary<EndKey, EndState> ends)
    {
        var relations = new List<TransportRelation>();
        foreach ((EndKey key, EndState state) in ends.OrderBy(entry => entry.Key))
        {
            EndKey mirror = key.Mirror();
            if (mirror.CompareTo(key) < 0
                || state.Holder < 0
                || state.Ambiguous
                || !ends.TryGetValue(mirror, out EndState? other)
                || other.Holder < 0
                || other.Ambiguous)
            {
                continue;
            }

            bool self = mirror.Equals(key);
            relations.Add(new()
            {
                Mechanism = Mechanism.Tcp,
                First = processes.Instances[state.Holder],
                FirstEndpoint = key.LocalEndpoint,
                Second = processes.Instances[other.Holder],
                SecondEndpoint = mirror.LocalEndpoint,
                Strength = Weaker(Weaker(RelationStrength.Correlated, state.Weakest), other.Weakest),
                FirstNativeTicks = Math.Min(state.First, other.First),
                LastNativeTicks = Math.Max(state.Last, other.Last),
                Records = self ? state.Records : state.Records + other.Records,
            });
        }

        return relations;
    }

    /// <summary>The weaker of two strengths: a relation is only as strong as the weakest binding it rests on.</summary>
    private static RelationStrength Weaker(RelationStrength left, RelationStrength right) =>
        (RelationStrength)Math.Max((int)left, (int)right);

    /// <summary>One connection end: an address family, the end's own endpoint and the remote endpoint it names.</summary>
    private readonly record struct EndKey(byte Family, uint LocalAddress, ushort LocalPort, uint RemoteAddress, ushort RemotePort)
        : IComparable<EndKey>
    {
        public string LocalEndpoint => Format(LocalAddress, LocalPort);

        public EndKey Mirror() => new(Family, RemoteAddress, RemotePort, LocalAddress, LocalPort);

        public int CompareTo(EndKey other)
        {
            int compared = Family.CompareTo(other.Family);
            compared = compared != 0 ? compared : LocalAddress.CompareTo(other.LocalAddress);
            compared = compared != 0 ? compared : LocalPort.CompareTo(other.LocalPort);
            compared = compared != 0 ? compared : RemoteAddress.CompareTo(other.RemoteAddress);
            return compared != 0 ? compared : RemotePort.CompareTo(other.RemotePort);
        }

        private static string Format(uint address, ushort port) => string.Create(
            CultureInfo.InvariantCulture,
            $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}:{port}");
    }

    /// <summary>
    /// Who holds one end: the single instance every bound record of it binds to, or ambiguity once two instances, or
    /// two PIDs, appear there. A record whose PID binds to no instance still names its PID, so a second process at the
    /// same end is noticed even when it cannot be bound.
    /// </summary>
    private sealed class EndState
    {
        private int? processId;

        public int Holder { get; private set; } = -1;

        public bool Ambiguous { get; private set; }

        public RelationStrength Weakest { get; private set; } = RelationStrength.Direct;

        public long First { get; private set; } = long.MaxValue;

        public long Last { get; private set; } = long.MinValue;

        public long Records { get; private set; }

        public void Observe(long reading, int? owner, ProcessBinding binding)
        {
            Records++;
            First = Math.Min(First, reading);
            Last = Math.Max(Last, reading);
            if (owner is { } pid)
            {
                if (processId is { } seen && seen != pid)
                {
                    Ambiguous = true;
                }

                processId ??= pid;
            }

            if (!binding.IsBound)
            {
                return;
            }

            if (Holder >= 0 && Holder != binding.Instance)
            {
                Ambiguous = true;
            }

            if (Holder < 0)
            {
                Holder = binding.Instance;
            }

            Weakest = Weaker(Weakest, binding.Strength);
        }
    }
}
