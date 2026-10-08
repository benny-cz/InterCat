using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class TransportRelationIndex
{
    /// <summary>
    /// Adds to <paramref name="kept"/> the released rows an interval release keeps so that every connection a row that stays
    /// names - a retained row, or one already kept - keeps the incarnations, holders, pairing and anchors the whole capture
    /// gave it (ADR-043), and adds each kept row's owner to <paramref name="owners"/>, whose process identity is then kept as
    /// well. At each end such a row names, and at its mirror, which its pairing reads: every connect, accept and disconnect,
    /// which divide the end into incarnations and bound their lifetimes (`relations-v1` §3); and in each incarnation its
    /// first record, which anchors its key and keeps it among the incarnations with records, and the first record of each
    /// owner, instance and strength that no staying row of it has, which together decide its holder, whether it is
    /// ambiguous and how strongly it is held. Its count of records and its first and last readings are what stays of it.
    /// </summary>
    /// <param name="released">Whether the record with this ordinal is released; a released row stays only when kept.</param>
    internal void KeepAcross(
        IReadOnlyList<SegmentReaderV1> segments,
        Func<ulong, bool> released,
        HashSet<RowAddress> kept,
        HashSet<int> owners,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(released);
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(owners);
        var named = new HashSet<EndKey>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = new EndColumns(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (columns.KeyAt(row) is { } key
                    && (!released(columns.PositionAt(row).Ordinal) || kept.Contains(AddressOf(columns.PositionAt(row)))))
                {
                    _ = named.Add(key);
                    _ = named.Add(key.Mirror());
                }
            }
        }

        var incarnations = new Dictionary<Incarnation, IncarnationEvidence>();
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = new EndColumns(segment);
            var rowOwners = new RecordOwnerColumns(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (columns.KeyAt(row) is not { } key
                    || !named.Contains(key)
                    || !ends.TryGetValue(key, out EndTimeline? timeline))
                {
                    continue;
                }

                Position position = columns.PositionAt(row);
                int? owner = rowOwners.At(row, (Mechanism)key.Protocol);
                bool gone = released(position.Ordinal) && !kept.Contains(AddressOf(position));
                if (gone && columns.KindAt(row) is ObservationKind.Connect or ObservationKind.Accept or ObservationKind.Disconnect)
                {
                    Keep(position, owner, kept, owners);
                    gone = false;
                }

                Incarnation incarnation = timeline.Whole ?? timeline.At(position);
                if (!incarnations.TryGetValue(incarnation, out IncarnationEvidence? evidence))
                {
                    evidence = new();
                    incarnations[incarnation] = evidence;
                }

                evidence.Observe(position, owner, processes.Bind(owner, position.Ticks, isLifecycleRecord: false), gone);
            }
        }

        foreach (IncarnationEvidence evidence in incarnations.Values)
        {
            evidence.KeepInto(kept, owners);
        }
    }

    private static void Keep(Position position, int? owner, HashSet<RowAddress> kept, HashSet<int> owners)
    {
        _ = kept.Add(AddressOf(position));
        if (owner is { } pid)
        {
            _ = owners.Add(pid);
        }
    }

    private static RowAddress AddressOf(Position position) =>
        new(position.Stream, position.Epoch, position.Ordinal, new FactKey(position.FactHigh, position.FactLow));

    /// <summary>
    /// What one incarnation's records decide that an interval release must keep: its first record, and for each owner,
    /// bound instance and strength among its records, whether a row that stays has it and else its first one that goes.
    /// </summary>
    private sealed class IncarnationEvidence
    {
        private readonly Dictionary<(int? Owner, int Instance, RelationStrength Strength), Facet> facets = [];
        private Position? first;
        private int? firstOwner;
        private bool firstGone;

        public void Observe(Position position, int? owner, ProcessBinding binding, bool gone)
        {
            if (first is not { } known || position.CompareTo(known) < 0)
            {
                (first, firstOwner, firstGone) = (position, owner, gone);
            }

            (int? Owner, int Instance, RelationStrength Strength) key = binding.IsBound
                ? (owner, binding.Instance, binding.Strength)
                : (owner, -1, RelationStrength.Unresolved);
            Facet facet = facets.GetValueOrDefault(key);
            facets[key] = gone
                ? facet.Earliest is { } earliest && earliest.CompareTo(position) <= 0 ? facet : facet with { Earliest = position }
                : facet with { Stays = true };
        }

        public void KeepInto(HashSet<RowAddress> kept, HashSet<int> owners)
        {
            if (firstGone && first is { } anchor)
            {
                Keep(anchor, firstOwner, kept, owners);
            }

            foreach (((int? owner, int _, RelationStrength _), Facet facet) in facets)
            {
                if (!facet.Stays && facet.Earliest is { } earliest)
                {
                    Keep(earliest, owner, kept, owners);
                }
            }
        }

        private readonly record struct Facet(bool Stays, Position? Earliest);
    }
}
