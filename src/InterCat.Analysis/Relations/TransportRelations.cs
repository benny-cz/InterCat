using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// One connection incarnation whose two ends are each held by exactly one process instance: the relation §7.4's
/// network correlator proves between them (`contracts/relations-v1.md`). Endpoints are as the source names them, never
/// normalized into an identity of their own (R22).
/// </summary>
public sealed record TransportRelation
{
    public required Mechanism Mechanism { get; init; }

    /// <summary>
    /// The channel number this paired incarnation and every record at either end share within this derivation.
    /// It is a join key for one relation index, not a stable cross-generation identity.
    /// </summary>
    public required int Channel { get; init; }

    /// <summary>
    /// A generation-independent display key for the witnessed incarnation. It is anchored to the earliest raw fact
    /// of either end, not the relation index's channel number (which can be renumbered by later publications).
    /// An earlier late-arriving fact deliberately changes the key, so a stale selection is dropped, never merged.
    /// </summary>
    public required string StableKey { get; init; }

    /// <summary>The instance holding the end whose endpoint sorts first.</summary>
    public required ProcessInstance First { get; init; }

    /// <summary>That end's own endpoint, as the source names it.</summary>
    public required string FirstEndpoint { get; init; }

    /// <summary>The instance holding the other end. Equal to <see cref="First"/> for a process connected to itself.</summary>
    public required ProcessInstance Second { get; init; }

    public required string SecondEndpoint { get; init; }

    /// <summary>`Correlated`, or `Candidate` when either end's records bind to its instance only as a candidate.</summary>
    public required RelationStrength Strength { get; init; }

    /// <summary>The earliest and latest native reading of any record of the incarnation, at either end.</summary>
    public required long FirstNativeTicks { get; init; }

    public required long LastNativeTicks { get; init; }

    /// <summary>How many records the incarnation's two ends hold together.</summary>
    public required long Records { get; init; }

    /// <summary>Whether a connect or an accept opened it at each end, and a disconnect closed it at each end.</summary>
    public required bool OpenWitnessed { get; init; }

    public required bool CloseWitnessed { get; init; }
}

/// <summary>
/// The connection incarnation - the channel of §7.1 - one record belongs to, or why the record names none. A paired
/// incarnation and its partner are one channel; an incarnation whose other end no record holds is a one-sided channel.
/// </summary>
public readonly record struct ChannelBinding(int Channel, ProcessBindingReason Reason)
{
    public bool IsKnown => Channel >= 0;

    public static ChannelBinding Unknown(ProcessBindingReason reason) => new(-1, reason);
}

/// <summary>
/// Derives the other end of every TCP and UDP record of one capture (`transport-endpoint-relation-v3`). A record's own
/// end is read through its descriptor's measured orientation - every TCP descriptor and a UDP send name the owner's own
/// endpoint first, a UDP receive names the datagram's sender first (ADR-019) - so the records holding the mirrored pair
/// of the same protocol are the other end of the same connection or datagram flow. A TCP end's records are divided into
/// **incarnations** by the connection lifecycle the capture witnessed - a connect or an accept opens one, a disconnect
/// closes one - because a port reused by a later connection is another connection. A UDP end has no lifecycle, so it is
/// one incarnation for the capture. Where every record of the paired incarnation at the other end binds to one process
/// instance, that instance is the record's peer. Nothing is paired by time proximity, by address alone or by the nearest
/// holder (§7.4, P6).
/// </summary>
/// <remarks>
/// It reads every segment twice: once for the lifecycle boundaries of each end, once to learn who holds each
/// incarnation. A segment's rows are then resolved on demand by locating each in its end's incarnations. Its state is one
/// entry per connection end and incarnation rather than per row, so a long capture's millions of transfers over a few
/// thousand connections cost a few thousand entries.
/// </remarks>
public sealed class TransportRelationIndex
{
    /// <summary>The rule identity a result names when it used these relations (§24 `correlationRevision`).</summary>
    public const string RelationRule = "transport-endpoint-relation-v3";

    private readonly Dictionary<EndKey, EndTimeline> ends;

    private TransportRelationIndex(ProcessInstanceIndex processes, Dictionary<EndKey, EndTimeline> ends)
    {
        this.ends = ends;
        Channels = NumberChannels(ends);
        Relations = BuildRelations(processes, ends);
    }

    /// <summary>How many distinct connection incarnations and datagram flows the capture's transport records establish.</summary>
    public int Channels { get; }

    /// <summary>Every connection incarnation whose two ends each have one holder, in a stable order.</summary>
    public IReadOnlyList<TransportRelation> Relations { get; }

    /// <summary>Whether this rule reads a mechanism's records: the transports whose orientation was measured.</summary>
    public static bool Relates(Mechanism mechanism) => mechanism is Mechanism.Tcp or Mechanism.Udp;

    /// <summary>Derives the relations of the segments <paramref name="processes"/> was derived from.</summary>
    public static TransportRelationIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);
        var ends = new Dictionary<EndKey, EndTimeline>();

        // First the lifecycle each end witnessed, which is all an incarnation boundary needs.
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EndKey?[] keys = KeysOf(segment);
            SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
            Position[] positions = PositionsOf(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (keys[row] is not { } key)
                {
                    continue;
                }

                if (!ends.TryGetValue(key, out EndTimeline? timeline))
                {
                    timeline = new();
                    ends[key] = timeline;
                }

                switch ((ObservationKind)kinds.UnsignedAt(row)!.Value)
                {
                    case ObservationKind.Connect or ObservationKind.Accept:
                        timeline.Cut(positions[row], afterClose: false);
                        break;
                    case ObservationKind.Disconnect:
                        timeline.Cut(positions[row], afterClose: true);
                        break;
                }
            }
        }

        foreach (EndTimeline timeline in ends.Values)
        {
            timeline.Seal();
        }

        // Then who holds each incarnation.
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EndKey?[] keys = KeysOf(segment);
            Position[] positions = PositionsOf(segment);
            SegmentColumnSlice owners = segment.Slice(SegmentColumnId.OwnerProcessId);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (keys[row] is not { } key)
                {
                    continue;
                }

                long? owner = owners.SignedAt(row);
                long reading = positions[row].Ticks;
                ends[key].At(positions[row]).Observe(
                    positions[row],
                    owner is { } pid ? (int)pid : null,
                    processes.Bind(owner is { } bound ? (int)bound : null, reading, isLifecycleRecord: false));
            }
        }

        foreach ((EndKey key, EndTimeline timeline) in ends)
        {
            if (key.CompareTo(key.Mirror()) <= 0 && ends.TryGetValue(key.Mirror(), out EndTimeline? mirror))
            {
                EndTimeline.Pair(timeline, mirror);
            }
        }

        return new(processes, ends);
    }

    /// <summary>
    /// The other end of every row of a segment: the instance holding the paired incarnation, with the relation's
    /// strength, or why there is none. A row outside every relation rule's mechanisms says so rather than looking
    /// unresolved for want of evidence.
    /// </summary>
    public ProcessBinding[] PeersOf(SegmentReaderV1 segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        EndKey?[] keys = KeysOf(segment);
        Position[] positions = PositionsOf(segment);
        var peers = new ProcessBinding[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            if (!Relates((Mechanism)mechanisms.UnsignedAt(row)!.Value))
            {
                peers[row] = ProcessBinding.Unresolved(ProcessBindingReason.NoRelationRule);
                continue;
            }

            peers[row] = keys[row] is { } key
                ? PeerOf(ends[key].At(positions[row]))
                : ProcessBinding.Unresolved(ProcessBindingReason.PeerEndpointIncomplete);
        }

        return peers;
    }

    /// <summary>
    /// The channel every row of a segment belongs to. A row whose incarnation's partner is undecided could belong to
    /// either of two connections at the other end, so its channel is unknown rather than counted twice or guessed.
    /// </summary>
    public ChannelBinding[] ChannelsOf(SegmentReaderV1 segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        EndKey?[] keys = KeysOf(segment);
        Position[] positions = PositionsOf(segment);
        var channels = new ChannelBinding[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            if (!Relates((Mechanism)mechanisms.UnsignedAt(row)!.Value))
            {
                channels[row] = ChannelBinding.Unknown(ProcessBindingReason.NoRelationRule);
                continue;
            }

            channels[row] = keys[row] is { } key && ends[key].At(positions[row]) is { } incarnation
                ? incarnation.Channel >= 0
                    ? new(incarnation.Channel, ProcessBindingReason.Bound)
                    : ChannelBinding.Unknown(ProcessBindingReason.PeerAmbiguous)
                : ChannelBinding.Unknown(ProcessBindingReason.PeerEndpointIncomplete);
        }

        return channels;
    }

    /// <summary>
    /// Which end of its connection or flow each row of a segment was made at: <c>0</c> at the end whose own endpoint
    /// sorts first, which is a <see cref="TransportRelation"/>'s <see cref="TransportRelation.First"/>, <c>1</c> at the
    /// other, and <c>-1</c> for a row with no end of its own - another mechanism, or an incomplete endpoint. The two ends
    /// of a channel therefore partition its records without reading who holds them, so a process connected to itself
    /// still shows two ends.
    /// </summary>
    public static sbyte[] EndsOf(SegmentReaderV1 segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        EndKey?[] keys = KeysOf(segment);
        var sides = new sbyte[keys.Length];
        for (int row = 0; row < keys.Length; row++)
        {
            sides[row] = keys[row] is { } key ? (sbyte)(key.CompareTo(key.Mirror()) <= 0 ? 0 : 1) : (sbyte)-1;
        }

        return sides;
    }

    /// <summary>
    /// Numbers the channels in a stable order: by end, then by incarnation. A paired incarnation shares its partner's
    /// number, one whose other end is not observed has its own, and an undecided one has its own only on the side with
    /// more incarnations; the other side's undecided records have none.
    /// </summary>
    private static int NumberChannels(Dictionary<EndKey, EndTimeline> ends)
    {
        int next = 0;
        foreach ((EndKey _, EndTimeline timeline) in ends.OrderBy(entry => entry.Key))
        {
            foreach (Incarnation incarnation in timeline.Incarnations.Where(incarnation => incarnation.Records > 0))
            {
                if (incarnation.Channel >= 0 || (incarnation.Pairing == Pairing.Ambiguous && !incarnation.NumberedAlone))
                {
                    continue;
                }

                incarnation.Channel = next;
                if (incarnation is { Pairing: Pairing.Paired, Partner: { } partner })
                {
                    partner.Channel = next;
                }

                next++;
            }
        }

        return next;
    }

    private static ProcessBinding PeerOf(Incarnation incarnation) => incarnation.Pairing switch
    {
        Pairing.Paired when incarnation.Partner is { } other => other.Ambiguous
            ? ProcessBinding.Unresolved(ProcessBindingReason.PeerAmbiguous)
            : other.Holder < 0
                ? ProcessBinding.Unresolved(ProcessBindingReason.PeerUnbound)
                : new(other.Holder, Weaker(RelationStrength.Correlated, other.Weakest), ProcessBindingReason.Bound),
        Pairing.Ambiguous => AgreedPeer(incarnation.Candidates),
        _ => ProcessBinding.Unresolved(ProcessBindingReason.PeerNotObserved),
    };

    /// <summary>
    /// When which incarnation is the partner is undecided but every candidate is held by the same one instance, that
    /// instance is the other end whichever the partner is. Any disagreement, or any candidate without one holder, leaves
    /// the other end ambiguous.
    /// </summary>
    private static ProcessBinding AgreedPeer(IReadOnlyList<Incarnation> candidates)
    {
        if (candidates.Count == 0
            || candidates.Any(candidate => candidate.Ambiguous || candidate.Holder < 0)
            || candidates.Select(candidate => candidate.Holder).Distinct().Count() != 1)
        {
            return ProcessBinding.Unresolved(ProcessBindingReason.PeerAmbiguous);
        }

        RelationStrength strength = candidates.Aggregate(RelationStrength.Correlated, (weakest, candidate) => Weaker(weakest, candidate.Weakest));
        return new(candidates[0].Holder, strength, ProcessBindingReason.Bound);
    }

    /// <summary>
    /// The end each row describes: its protocol, its own endpoint and the remote one. The endpoints are read through the
    /// descriptor's measured orientation, because a UDP receive names the datagram's sender first where every other
    /// admitted transport descriptor names the owner's own endpoint first. A row of another mechanism, or with a missing
    /// or zero address or port, has none.
    /// </summary>
    private static EndKey?[] KeysOf(SegmentReaderV1 segment)
    {
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
        SegmentColumnSlice families = segment.Slice(SegmentColumnId.EndpointAddressFamily);
        SegmentColumnSlice localAddresses = segment.Slice(SegmentColumnId.SourceEndpointAddress);
        SegmentColumnSlice localPorts = segment.Slice(SegmentColumnId.SourceEndpointPort);
        SegmentColumnSlice remoteAddresses = segment.Slice(SegmentColumnId.DestinationEndpointAddress);
        SegmentColumnSlice remotePorts = segment.Slice(SegmentColumnId.DestinationEndpointPort);
        var keys = new EndKey?[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            // An unavailable address is not zero, and a zero one names no endpoint: neither can find the other end.
            var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
            if (!Relates(mechanism)
                || families.UnsignedAt(row) is not { } family
                || localAddresses.UnsignedAt(row) is not ({ } firstAddress and not 0UL)
                || localPorts.UnsignedAt(row) is not ({ } firstPort and not 0UL)
                || remoteAddresses.UnsignedAt(row) is not ({ } secondAddress and not 0UL)
                || remotePorts.UnsignedAt(row) is not ({ } secondPort and not 0UL))
            {
                continue;
            }

            var named = new EndKey((byte)mechanism, (byte)family, (uint)firstAddress, (ushort)firstPort, (uint)secondAddress, (ushort)secondPort);
            keys[row] = TransportEndpoints.OrientationOf(mechanism, (ObservationKind)kinds.UnsignedAt(row)!.Value) == EndpointOrientation.OwnerFirst
                ? named
                : named.Mirror();
        }

        return keys;
    }

    /// <summary>Each row's place in the canonical order: native reading, raw locator, fact key (`entities-v1` §3).</summary>
    private static Position[] PositionsOf(SegmentReaderV1 segment)
    {
        SegmentColumnSlice ticks = segment.Slice(SegmentColumnId.NativeTicks);
        SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
        SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
        SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
        SegmentColumnSlice factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
        SegmentColumnSlice factLow = segment.Slice(SegmentColumnId.FactKeyLow);
        var positions = new Position[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            positions[row] = new(
                ticks.SignedAt(row)!.Value,
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                factHigh.UnsignedAt(row)!.Value,
                factLow.UnsignedAt(row)!.Value);
        }

        return positions;
    }

    private static List<TransportRelation> BuildRelations(ProcessInstanceIndex processes, Dictionary<EndKey, EndTimeline> ends)
    {
        var relations = new List<TransportRelation>();
        foreach ((EndKey key, EndTimeline timeline) in ends.OrderBy(entry => entry.Key))
        {
            if (key.CompareTo(key.Mirror()) > 0)
            {
                continue;
            }

            foreach (Incarnation incarnation in timeline.Incarnations)
            {
                if (incarnation is not { Pairing: Pairing.Paired, Partner: { } other, Holder: >= 0, Ambiguous: false }
                    || other.Holder < 0
                    || other.Ambiguous)
                {
                    continue;
                }

                relations.Add(new()
                {
                    Mechanism = (Mechanism)key.Protocol,
                    Channel = incarnation.Channel,
                    StableKey = StableChannelKey(incarnation, other, processes.Instances[incarnation.Holder],
                        processes.Instances[other.Holder]),
                    First = processes.Instances[incarnation.Holder],
                    FirstEndpoint = key.LocalEndpoint,
                    Second = processes.Instances[other.Holder],
                    SecondEndpoint = key.Mirror().LocalEndpoint,
                    Strength = Weaker(Weaker(RelationStrength.Correlated, incarnation.Weakest), other.Weakest),
                    FirstNativeTicks = Math.Min(incarnation.First, other.First),
                    LastNativeTicks = Math.Max(incarnation.Last, other.Last),
                    Records = incarnation.Records + other.Records,
                    OpenWitnessed = incarnation.OpenWitnessed && other.OpenWitnessed,
                    CloseWitnessed = incarnation.CloseWitnessed && other.CloseWitnessed,
                });
            }
        }

        return relations;
    }

    /// <summary>The weaker of two strengths: a relation is only as strong as the weakest binding it rests on.</summary>
    private static RelationStrength Weaker(RelationStrength left, RelationStrength right) =>
        (RelationStrength)Math.Max((int)left, (int)right);

    private static string StableChannelKey(
        Incarnation first, Incarnation second, ProcessInstance firstProcess, ProcessInstance secondProcess)
    {
        Position near = first.FirstPosition
            ?? throw new InvalidDataException("A paired incarnation has no first raw fact.");
        Position far = second.FirstPosition
            ?? throw new InvalidDataException("A paired incarnation's peer has no first raw fact.");
        Position anchor = near.CompareTo(far) <= 0 ? near : far;
        return string.Create(CultureInfo.InvariantCulture,
            $"transport:{firstProcess.Id}:{secondProcess.Id}:{anchor.Stream:x8}:{anchor.Epoch:x8}:"
            + $"{anchor.Ordinal:x16}:{anchor.FactHigh:x16}:{anchor.FactLow:x16}");
    }

    private enum Pairing
    {
        /// <summary>No incarnation at the other end could be this one's partner.</summary>
        NotObserved = 0,

        /// <summary>One incarnation at the other end is this one's partner, and it has no other.</summary>
        Paired = 1,

        /// <summary>More than one incarnation at the other end could be this one's partner.</summary>
        Ambiguous = 2,
    }

    /// <summary>A row's place in the canonical order.</summary>
    private readonly record struct Position(long Ticks, uint Stream, uint Epoch, ulong Ordinal, ulong FactHigh, ulong FactLow)
        : IComparable<Position>
    {
        public int CompareTo(Position other)
        {
            int compared = Ticks.CompareTo(other.Ticks);
            compared = compared != 0 ? compared : Stream.CompareTo(other.Stream);
            compared = compared != 0 ? compared : Epoch.CompareTo(other.Epoch);
            compared = compared != 0 ? compared : Ordinal.CompareTo(other.Ordinal);
            compared = compared != 0 ? compared : FactHigh.CompareTo(other.FactHigh);
            return compared != 0 ? compared : FactLow.CompareTo(other.FactLow);
        }
    }

    /// <summary>
    /// Where one incarnation ends and the next begins: at an open, which starts the next one with itself, or just after
    /// a disconnect, which is the last record of the one it closes.
    /// </summary>
    private readonly record struct Cut(Position At, bool AfterClose)
    {
        /// <summary>Whether a row at this position belongs to the incarnation after the cut.</summary>
        public bool IsAtOrBefore(Position row)
        {
            int compared = At.CompareTo(row);
            return compared < 0 || (compared == 0 && !AfterClose);
        }
    }

    /// <summary>
    /// One connection or datagram-flow end: its protocol, an address family, the end's own endpoint and the remote endpoint
    /// it names. TCP port 5000 and UDP port 5000 are different ends, so the protocol is part of the key.
    /// </summary>
    private readonly record struct EndKey(byte Protocol, byte Family, uint LocalAddress, ushort LocalPort, uint RemoteAddress, ushort RemotePort)
        : IComparable<EndKey>
    {
        public string LocalEndpoint => Format(LocalAddress, LocalPort);

        public EndKey Mirror() => new(Protocol, Family, RemoteAddress, RemotePort, LocalAddress, LocalPort);

        public int CompareTo(EndKey other)
        {
            int compared = Protocol.CompareTo(other.Protocol);
            compared = compared != 0 ? compared : Family.CompareTo(other.Family);
            compared = compared != 0 ? compared : LocalAddress.CompareTo(other.LocalAddress);
            compared = compared != 0 ? compared : LocalPort.CompareTo(other.LocalPort);
            compared = compared != 0 ? compared : RemoteAddress.CompareTo(other.RemoteAddress);
            return compared != 0 ? compared : RemotePort.CompareTo(other.RemotePort);
        }

        private static string Format(uint address, ushort port) => string.Create(
            CultureInfo.InvariantCulture,
            $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}:{port}");
    }

    /// <summary>One end's incarnations, divided by the lifecycle records the capture holds for it.</summary>
    private sealed class EndTimeline
    {
        private readonly List<Cut> cuts = [];

        public Incarnation[] Incarnations { get; private set; } = [];

        public void Cut(Position at, bool afterClose) => cuts.Add(new(at, afterClose));

        public void Seal()
        {
            cuts.Sort((left, right) => left.At.CompareTo(right.At));
            Incarnations = new Incarnation[cuts.Count + 1];
            for (int index = 0; index < Incarnations.Length; index++)
            {
                Cut? opening = index > 0 ? cuts[index - 1] : null;
                Cut? closing = index < cuts.Count ? cuts[index] : null;
                Incarnations[index] = new(
                    Start: opening?.At.Ticks ?? long.MinValue,
                    End: closing is { } close ? (close.AfterClose ? close.At.Ticks + 1 : close.At.Ticks) : long.MaxValue,
                    OpenWitnessed: opening is { AfterClose: false },
                    CloseWitnessed: closing is { AfterClose: true });
            }
        }

        /// <summary>The incarnation a row at this position belongs to: the one after the last cut at or before it.</summary>
        public Incarnation At(Position row)
        {
            int low = 0;
            int high = cuts.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (cuts[middle].IsAtOrBefore(row))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return Incarnations[low];
        }

        /// <summary>
        /// Pairs the incarnations of an end with those of its mirror. The same number of incarnations with records at both
        /// ends pair in order - one 4-tuple carries one connection at a time - when every such pair's lifetimes overlap, or
        /// when each end has one. Otherwise an incarnation pairs only with the one mirror incarnation whose lifetime overlaps
        /// it and no other of this end's. Empty incarnations, between a disconnect and the next open, take no part.
        /// </summary>
        public static void Pair(EndTimeline end, EndTimeline mirror)
        {
            Incarnation[] near = [.. end.Incarnations.Where(incarnation => incarnation.Records > 0)];
            Incarnation[] far = [.. mirror.Incarnations.Where(incarnation => incarnation.Records > 0)];
            if (near.Length == far.Length
                && (near.Length == 1 || near.Zip(far).All(pair => pair.First.Overlaps(pair.Second))))
            {
                foreach ((Incarnation first, Incarnation second) in near.Zip(far))
                {
                    first.PairWith(second);
                    second.PairWith(first);
                }

                return;
            }

            foreach (Incarnation incarnation in near)
            {
                incarnation.Resolve(far, near);
            }

            foreach (Incarnation incarnation in far)
            {
                incarnation.Resolve(near, far);
            }

            // Undecided incarnations are still connections: each one bounded by its own lifecycle is distinct, so the
            // side with more of them counts that many channels, and the other side's records belong to one of them,
            // undecided which. Numbering both sides would count one connection twice.
            Incarnation[] counted = far.Length > near.Length ? far : near;
            foreach (Incarnation incarnation in counted.Where(incarnation => incarnation.Pairing == Pairing.Ambiguous))
            {
                incarnation.NumberedAlone = true;
            }
        }
    }

    /// <summary>
    /// One connection incarnation at one end: its lifetime in native ticks, whether its lifecycle was witnessed, and who
    /// holds it - the single instance every bound record of it binds to, or ambiguity once two instances, or two PIDs,
    /// appear. A record whose PID binds to no instance still names its PID, so a second process is noticed even when it
    /// cannot be bound.
    /// </summary>
    private sealed class Incarnation(long Start, long End, bool OpenWitnessed, bool CloseWitnessed)
    {
        private int? processId;

        /// <summary>The first native tick of the incarnation's lifetime, or unbounded below when no boundary precedes it.</summary>
        public long Start { get; } = Start;

        /// <summary>The first native tick after the lifetime, or unbounded above when no boundary follows it.</summary>
        public long End { get; } = End;

        public bool OpenWitnessed { get; } = OpenWitnessed;

        public bool CloseWitnessed { get; } = CloseWitnessed;

        public int Holder { get; private set; } = -1;

        public bool Ambiguous { get; private set; }

        public RelationStrength Weakest { get; private set; } = RelationStrength.Direct;

        public long First { get; private set; } = long.MaxValue;

        public Position? FirstPosition { get; private set; }

        public long Last { get; private set; } = long.MinValue;

        public long Records { get; private set; }

        public Pairing Pairing { get; private set; }

        public Incarnation? Partner { get; private set; }

        /// <summary>
        /// The channel this incarnation belongs to, or -1 when its partner is undecided and the other side's
        /// incarnations are the ones counted.
        /// </summary>
        public int Channel { get; set; } = -1;

        /// <summary>Whether this undecided incarnation is counted as a channel of its own.</summary>
        public bool NumberedAlone { get; set; }

        /// <summary>The mirror incarnations that could be this one's partner, when more than one could.</summary>
        public IReadOnlyList<Incarnation> Candidates { get; private set; } = [];

        public bool Overlaps(Incarnation other) => Start < other.End && other.Start < End;

        public void PairWith(Incarnation other)
        {
            Pairing = Pairing.Paired;
            Partner = other;
        }

        /// <summary>Pairs with the one candidate whose lifetime overlaps this one's, if this is its only overlap too.</summary>
        public void Resolve(IReadOnlyList<Incarnation> candidates, IReadOnlyList<Incarnation> rivals)
        {
            Incarnation[] overlapping = [.. candidates.Where(Overlaps)];
            if (overlapping.Length == 1 && rivals.Count(overlapping[0].Overlaps) == 1)
            {
                PairWith(overlapping[0]);
                return;
            }

            Pairing = overlapping.Length == 0 ? Pairing.NotObserved : Pairing.Ambiguous;
            Candidates = overlapping;
        }

        public void Observe(Position position, int? owner, ProcessBinding binding)
        {
            long reading = position.Ticks;
            Records++;
            First = Math.Min(First, reading);
            Last = Math.Max(Last, reading);
            if (FirstPosition is not { } prior || position.CompareTo(prior) < 0)
            {
                FirstPosition = position;
            }
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
