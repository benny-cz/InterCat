using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>The role a process filter selects a record by (§19.1).</summary>
public enum ProcessRole
{
    /// <summary>`owner(P)`: the record's own payload names P.</summary>
    Owner = 1,

    /// <summary>`participant(P)`: P made the record, or is the other end of it through a proven relation.</summary>
    Participant = 2,

    /// <summary>`sender(P)`: data flowed out of P - its own send record, or a receive record whose other end is P.</summary>
    Sender = 3,

    /// <summary>`receiver(P)`: data flowed into P - its own receive record, or a send record whose other end is P.</summary>
    Receiver = 4,
}

/// <summary>A focused process instance and the role a filter selects its records by.</summary>
public readonly record struct ProcessFocus(ProcessRole Role, ProcessInstanceId Instance);

/// <summary>
/// Who each record of a segment belongs to in every role a filter or grouping can ask about: the process that made it,
/// the process at its other end, and which way its data flowed. Computed once per segment and shared.
/// </summary>
/// <param name="Owners">Each row's own binding under `process-binding-v2`.</param>
/// <param name="Peers">Each row's other end under the relation rule; empty when relations were not derived.</param>
/// <param name="Directions">+1 when data left the row's owner, -1 when it arrived, 0 for a record with no data direction.</param>
/// <param name="Communicates">Whether the record could have another end at all. A thread or process lifecycle record has none.</param>
internal sealed record SegmentRoles(ProcessBinding[] Owners, ProcessBinding[] Peers, sbyte[] Directions, bool[] Communicates)
{
    public ProcessBinding Owner(int row) => Owners[row];

    public ProcessBinding Peer(int row) =>
        Peers.Length == 0 ? ProcessBinding.Unresolved(ProcessBindingReason.NoRelationRule) : Peers[row];
}

/// <summary>The roles of every segment of one answer, derived once per segment.</summary>
internal sealed class ProcessRoles(ProcessInstanceIndex processes, TransportRelationIndex? relations)
{
    private readonly Dictionary<SegmentReaderV1, SegmentRoles> cache = [];

    public ProcessInstanceIndex Processes { get; } = processes;

    public TransportRelationIndex? Relations { get; } = relations;

    public SegmentRoles For(SegmentReaderV1 segment)
    {
        if (cache.TryGetValue(segment, out SegmentRoles? known))
        {
            return known;
        }

        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        var directions = new sbyte[segment.RowCount];
        var communicates = new bool[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            communicates[row] = (Mechanism)mechanisms.UnsignedAt(row)!.Value is not (Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle);
            directions[row] = (ObservationKind)kinds.UnsignedAt(row)!.Value switch
            {
                ObservationKind.Send => 1,
                ObservationKind.Receive => -1,
                _ => 0,
            };
        }

        var roles = new SegmentRoles(OwnersOf(Processes, segment), Relations?.PeersOf(segment) ?? [], directions, communicates);
        cache[segment] = roles;
        return roles;
    }

    /// <summary>
    /// The binding of the process each row names as its owner. A lifecycle record binds to the exact instance it
    /// created, ended or confirmed; any other record binds by its reading (`contracts/entities-v1.md` §4).
    /// </summary>
    public static ProcessBinding[] OwnersOf(ProcessInstanceIndex index, SegmentReaderV1 segment)
    {
        SegmentColumnSlice owners = segment.Slice(SegmentColumnId.OwnerProcessId);
        SegmentColumnSlice ticks = segment.Slice(SegmentColumnId.NativeTicks);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
        SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
        SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
        SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
        SegmentColumnSlice factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
        SegmentColumnSlice factLow = segment.Slice(SegmentColumnId.FactKeyLow);
        var bindings = new ProcessBinding[segment.RowCount];
        for (int row = 0; row < segment.RowCount; row++)
        {
            bool lifecycle = ProcessInstanceIndex.IsLifecycleRecord(
                (Mechanism)mechanisms.UnsignedAt(row)!.Value,
                (ObservationKind)kinds.UnsignedAt(row)!.Value);
            long? owner = owners.SignedAt(row);
            ObservationId? exact = lifecycle && owner is not null
                ? new ObservationId(
                    new RawRecordId(segment.CaptureId,
                        (uint)streams.UnsignedAt(row)!.Value,
                        (uint)epochs.UnsignedAt(row)!.Value,
                        ordinals.UnsignedAt(row)!.Value),
                    segment.Derivation,
                    new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value))
                : null;
            bindings[row] = index.Bind(owner is { } pid ? (int)pid : null, ticks.SignedAt(row)!.Value, lifecycle, exact);
        }

        return bindings;
    }
}

/// <summary>
/// One process filter resolved against a derivation: the focused instance, the role it is selected in, the optional
/// peer it is narrowed to, and the evidence policy every binding it relies on must satisfy.
/// </summary>
internal sealed record ProcessFilter(ProcessRole Role, int Focus, int? Peer, EvidencePolicy Policy)
{
    /// <summary>Which rows of a segment the filter keeps.</summary>
    public bool[] Mask(SegmentRoles roles)
    {
        var mask = new bool[roles.Owners.Length];
        for (int row = 0; row < mask.Length; row++)
        {
            mask[row] = Includes(roles, row, openPeers: false);
        }

        return mask;
    }

    /// <summary>
    /// Rows the filter leaves out only because the record's other end is unresolved, or resolved under a strength this
    /// policy does not admit: the focus could be that end. They are disclosed, never added (P6).
    /// </summary>
    public bool[] Undecided(SegmentRoles roles, bool[] mask)
    {
        var undecided = new bool[mask.Length];
        for (int row = 0; row < mask.Length; row++)
        {
            undecided[row] = !mask[row] && Includes(roles, row, openPeers: true);
        }

        return undecided;
    }

    /// <summary>
    /// The process at the other end from the focus in a row the filter keeps: what a peer grouping ranks. A process
    /// connected to itself is its own counterpart.
    /// </summary>
    public ProcessBinding Counterpart(SegmentRoles roles, int row) => Role switch
    {
        ProcessRole.Owner => roles.Peer(row),
        ProcessRole.Participant => Is(roles.Owner(row), Focus) ? roles.Peer(row) : roles.Owner(row),
        ProcessRole.Sender => roles.Directions[row] > 0 ? roles.Peer(row) : roles.Owner(row),
        ProcessRole.Receiver => roles.Directions[row] < 0 ? roles.Peer(row) : roles.Owner(row),
        _ => throw new InvalidOperationException($"Unknown process role {Role}."),
    };

    private bool Includes(SegmentRoles roles, int row, bool openPeers)
    {
        ProcessBinding owner = roles.Owner(row);
        ProcessBinding peer = roles.Peer(row);
        sbyte direction = roles.Directions[row];

        // The focus as the record's maker, with its peer narrowed to Q; or the focus as the record's other end, with
        // the maker narrowed to Q. With open peers, an unresolved other end of a record that has one may be anyone -
        // except the focus itself on a record it made: a process connected to itself holds both ends, and both would
        // be observed.
        openPeers &= roles.Communicates[row];
        bool asOwner = Is(owner, Focus) && (Peer is not { } q || Matches(peer, q, openPeers));
        bool asPeer = (Is(peer, Focus) || (openPeers && !Is(owner, Focus) && !peer.IsAdmittedUnder(Policy)))
            && (Peer is not { } p || Is(owner, p));
        return Role switch
        {
            ProcessRole.Owner => asOwner,
            ProcessRole.Participant => asOwner || asPeer,
            ProcessRole.Sender => (direction > 0 && asOwner) || (direction < 0 && asPeer),
            ProcessRole.Receiver => (direction < 0 && asOwner) || (direction > 0 && asPeer),
            _ => false,
        };
    }

    private bool Matches(ProcessBinding binding, int instance, bool openPeers) =>
        Is(binding, instance) || (openPeers && !binding.IsAdmittedUnder(Policy));

    private bool Is(ProcessBinding binding, int instance) =>
        binding.IsBound && binding.Instance == instance && binding.IsAdmittedUnder(Policy);
}
