using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A process's, or a group's, distinct peers over one scope (`metrics-v1` §6.1): the process instances at the other end of
/// its records under `relations-v1` and the evidence policy, each counted once however many records it shares. Known are
/// the records that named a peer; unknown those whose other end is unresolved or bound more weakly than the policy admits,
/// beside which the count is a lower bound. A row whose records named no peer is unmeasured, never zero (R21).
/// </summary>
public sealed record PeerCount(long? Peers, long Known, long Unknown)
{
    /// <summary>A process or group with no record in scope that has another end.</summary>
    public static PeerCount None { get; } = new(null, 0, 0);

    /// <summary>The value a peer ranking reads, with the records behind it and those that named no peer.</summary>
    public RankedValue Of(RankingMetric metric) => metric == RankingMetric.ActivePeers
        ? new(metric, Peers, Known, Unknown)
        : throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only a peer ranking reads peers.");
}

/// <summary>
/// Every process instance's and group's distinct peers over one scope of one generation, its groups keyed as the ladder
/// keys them under every grouping the window offers (<see cref="WorkspaceGrouping"/>). Distinct counts overlap - a record
/// between two processes makes each the other's peer - so the rows never add up to a total.
/// </summary>
public sealed record SessionPeerMeasures(
    Guid SessionId,
    long Generation,
    TimeRange? Interval,
    IReadOnlyDictionary<ProcessInstanceId, PeerCount> ByProcess,
    IReadOnlyDictionary<string, PeerCount> ByGroup) : IRankingMeasures
{
    /// <summary>How many distinct processes have at least one resolved peer: the grouped metric's total.</summary>
    public long WithPeers { get; init; }

    /// <summary>Records in scope with another end whose own process and other end are both unknown to the policy.</summary>
    public long Unattributed { get; init; }
}

/// <summary>
/// Reads each process's and each group's distinct peers for the ranked table: what `icat metric --metric active-peers
/// --group-by process` answers for every instance, `--group-by executable` for every group whose executable is known and
/// `--group-by session` for every terminal session named (R18), from the derivation the overview already holds rather
/// than a derivation of its own.
/// </summary>
public static class SessionPeerRanking
{
    /// <summary>
    /// Counts the whole retained capture, or <paramref name="interval"/> in 100-nanosecond presentation ticks, whose bounds
    /// are placed on the source clock as the command line places a time. A record is in scope when its reading is. The
    /// instances a view sets aside (<paramref name="setAside"/>, §19.5) keep their own peers, and are still the peers of
    /// those they shared records with, but no group counts them as a member and the processes with a peer leave them out.
    /// </summary>
    public static SessionPeerMeasures Measure(
        SessionStore store,
        TimeRange? interval,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        IReadOnlySet<ProcessInstanceId>? setAside = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its processes cannot be identified.");

        // A time scope reads only the segments holding a reading in it, with the instances and channels the derivation or
        // the checkpoint holds (P25). The whole session is counted from how its records bind at their two ends, where a
        // finished session's persisted overview keeps them, and otherwise reads every segment.
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TransportRelationIndex relations = generation.Relations(cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;

        // Each instance's group as the ladder keys it under every grouping the window offers - by executable and by terminal
        // session - as a dense index into one list of keys, which never collide, so a change of grouping reads nothing again.
        // An instance the view sets aside (§19.5) is no group's member, though it is still a peer of those it shared records
        // with: its own records count toward no group, and it is not among the processes with a peer the rows shown hold.
        int instances = processes.Instances.Count;
        IReadOnlyList<LaneGrouping> groupings = WorkspaceGrouping.All;
        var groupKeys = new List<string>();
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int[][] groupOf = [.. groupings.Select(_ => new int[instances])];
        bool[] aside = new bool[instances];
        for (int instance = 0; instance < instances; instance++)
        {
            aside[instance] = setAside?.Contains(processes.Instances[instance].Id) == true;
            for (int grouping = 0; grouping < groupings.Count; grouping++)
            {
                string key = aside[instance]
                    ? SetAsideKey
                    : WorkspaceGrouping.KeyOf(processes.Instances[instance], groupings[grouping]);
                if (!groupIndex.TryGetValue(key, out int index))
                {
                    groupIndex[key] = index = groupKeys.Count;
                    groupKeys.Add(key);
                }

                groupOf[grouping][instance] = index;
            }
        }

        // An interval no reading can fall in holds nothing, and no segment is read.
        var total = new PeerTally(instances, groupKeys.Count, groupings.Count);
        if (interval is null
            && generation.Derivation.PersistedOverview(store.Root) is { PeerBindings: { } kept }
            && Resolve(kept, processes) is { } pairs)
        {
            foreach ((ProcessBinding owner, ProcessBinding peer, long records) in pairs)
            {
                Count(owner, peer, records, policy, groupOf, total);
            }
        }
        else if (interval is null || native is not null)
        {
            SegmentReaderV1[] segments = native is { } readings
                ? SessionNativeInterval.Segments(store, manifest, (readings.StartTicks, readings.EndTicks))
                : generation.All;
            SegmentPasses.Run(
                segments,
                () => new PeerTally(instances, groupKeys.Count, groupings.Count),
                (segment, tally) => MeasureSegment(segment, native, policy, processes, relations, groupOf, tally),
                total.Add,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var byProcess = new Dictionary<ProcessInstanceId, PeerCount>();
        for (int instance = 0; instance < instances; instance++)
        {
            if (total.Processes.CountOf(instance) is { } counted)
            {
                byProcess[processes.Instances[instance].Id] = counted;
            }
        }

        // A key is one grouping's, so only that grouping's tally counts anything under it. The set-aside instances' key is
        // every grouping's, and no group's.
        var byGroup = new Dictionary<string, PeerCount>(StringComparer.Ordinal);
        foreach (Grouping grouping in total.Groups)
        {
            for (int group = 0; group < groupKeys.Count; group++)
            {
                if (!string.Equals(groupKeys[group], SetAsideKey, StringComparison.Ordinal)
                    && grouping.CountOf(group) is { } counted)
                {
                    byGroup[groupKeys[group]] = counted;
                }
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess, byGroup)
        {
            WithPeers = total.Processes.Distinct(aside),
            Unattributed = total.Unattributed,
        };
    }

    /// <summary>The group the instances a view sets aside fall in under every grouping, which no ladder row is.</summary>
    private const string SetAsideKey = "collectors:aside";

    /// <summary>
    /// Counts one segment's records in scope that have another end, by process and by group, as the grouped distinct
    /// count does: a record between two known processes adds each to the other's peers, and one with both ends in one
    /// group - a process connected to itself, or two instances of one executable - is one contribution to it.
    /// </summary>
    private static void MeasureSegment(
        SegmentReaderV1 segment,
        TimeRange? native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        int[][] groupOf,
        PeerTally tally)
    {
        bool[] inScope = SegmentMeasurement.RowsInScope(segment, null, null, native);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
        PackedOwners peers = SegmentBindings.PeersOf(segment, relations);
        for (int row = 0; row < inScope.Length; row++)
        {
            if (!inScope[row]
                || (Mechanism)mechanisms.UnsignedAt(row)!.Value is Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle)
            {
                continue;
            }

            Count(owners[row], peers[row], 1, policy, groupOf, tally);
        }
    }

    /// <summary>
    /// Counts <paramref name="records"/> records that bind at their own end to <paramref name="owner"/> and at their other
    /// end to <paramref name="peer"/>, by process and by group, each end only where the policy admits it.
    /// </summary>
    private static void Count(
        ProcessBinding owner,
        ProcessBinding peer,
        long records,
        EvidencePolicy policy,
        int[][] groupOf,
        PeerTally tally)
    {
        int self = owner.IsAdmittedUnder(policy) ? owner.Instance : -1;
        int other = peer.IsAdmittedUnder(policy) ? peer.Instance : -1;
        tally.Processes.Add(self, other, self, other, records);
        for (int grouping = 0; grouping < groupOf.Length; grouping++)
        {
            tally.Groups[grouping].Add(
                self < 0 ? -1 : groupOf[grouping][self], other < 0 ? -1 : groupOf[grouping][other], self, other, records);
        }

        if (self < 0 && other < 0)
        {
            tally.Unattributed += records;
        }
    }

    /// <summary>
    /// The pairs of bindings a persisted overview keeps, bound to <paramref name="processes"/>' instances; null when one
    /// names an instance they do not hold, and the segments are read instead.
    /// </summary>
    private static List<(ProcessBinding Owner, ProcessBinding Peer, long Records)>? Resolve(
        OverviewPeerBindings kept,
        ProcessInstanceIndex processes)
    {
        var positionOf = new Dictionary<ProcessInstanceId, int>(processes.Instances.Count);
        for (int position = 0; position < processes.Instances.Count; position++)
        {
            positionOf[processes.Instances[position].Id] = position;
        }

        var resolved = new List<(ProcessBinding Owner, ProcessBinding Peer, long Records)>(kept.Pairs.Count);
        foreach (PeerBindingPair pair in kept.Pairs)
        {
            if (Binding(pair.Owner, pair.OwnerStrength) is not { } owner || Binding(pair.Peer, pair.PeerStrength) is not { } peer)
            {
                return null;
            }

            resolved.Add((owner, peer, pair.Records));
        }

        return resolved;

        ProcessBinding? Binding(ProcessInstanceId instance, RelationStrength strength) =>
            instance.Value == Guid.Empty ? ProcessBinding.Unresolved(ProcessBindingReason.NoOwner)
            : positionOf.TryGetValue(instance, out int position) ? new ProcessBinding(position, strength, ProcessBindingReason.Bound)
            : null;
    }

    /// <summary>
    /// How the whole session's records bind at their two ends, before any evidence policy (overview-index-v1 minor 7), counted
    /// in one pass over <paramref name="segments"/>, the generation's every segment; null when there are more pairs than
    /// <paramref name="maximumPairs"/>, as a persisted overview keeps, and the segments are read when a view asks.
    /// </summary>
    internal static OverviewPeerBindings? KeptBindings(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        CancellationToken cancellationToken,
        int maximumPairs = OverviewPeerBindings.MaximumPairs)
    {
        var total = new BindingTally();
        SegmentPasses.Run(
            segments,
            () => new BindingTally(),
            (segment, tally) => tally.Count(segment, processes, relations),
            total.Add,
            cancellationToken);
        return total.Pairs.Count > maximumPairs
            ? null
            : new([.. total.Pairs.Select(entry => new PeerBindingPair(
                entry.Key.Owner < 0 ? default : processes.Instances[entry.Key.Owner].Id,
                entry.Key.OwnerStrength,
                entry.Key.Peer < 0 ? default : processes.Instances[entry.Key.Peer].Id,
                entry.Key.PeerStrength,
                entry.Value))]);
    }

    /// <summary>
    /// One worker's records by the pair of bindings at their two ends: an end bound at a strength some policy admits by its
    /// instance's position, and any other end as none, at no strength.
    /// </summary>
    private sealed class BindingTally
    {
        public Dictionary<(int Owner, RelationStrength OwnerStrength, int Peer, RelationStrength PeerStrength), long> Pairs { get; } = [];

        public void Count(SegmentReaderV1 segment, ProcessInstanceIndex processes, TransportRelationIndex relations)
        {
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
            PackedOwners peers = SegmentBindings.PeersOf(segment, relations);

            // Records of one connection lie together, so a run of one pair is counted once it ends.
            (int, RelationStrength, int, RelationStrength) run = default;
            long length = 0;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value is Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle)
                {
                    continue;
                }

                (int Instance, RelationStrength Strength) owner = Kept(owners[row]);
                (int Instance, RelationStrength Strength) peer = Kept(peers[row]);
                (int, RelationStrength, int, RelationStrength) pair = (owner.Instance, owner.Strength, peer.Instance, peer.Strength);
                if (length > 0 && pair != run)
                {
                    Pairs[run] = Pairs.GetValueOrDefault(run) + length;
                    length = 0;
                }

                run = pair;
                length++;
            }

            if (length > 0)
            {
                Pairs[run] = Pairs.GetValueOrDefault(run) + length;
            }
        }

        public void Add(BindingTally other)
        {
            foreach ((var pair, long records) in other.Pairs)
            {
                Pairs[pair] = Pairs.GetValueOrDefault(pair) + records;
            }
        }

        private static (int Instance, RelationStrength Strength) Kept(ProcessBinding binding) =>
            binding.IsBound && binding.Strength is RelationStrength.Direct or RelationStrength.Correlated
                or RelationStrength.Candidate or RelationStrength.Conflicting
                ? (binding.Instance, binding.Strength)
                : (-1, 0);
    }

    /// <summary>One worker's peers by process and by each grouping's groups, merged once it has no segment left.</summary>
    private sealed class PeerTally(int instances, int groups, int groupings)
    {
        public Grouping Processes { get; } = new(instances);

        public Grouping[] Groups { get; } = [.. Enumerable.Range(0, groupings).Select(_ => new Grouping(groups))];

        public long Unattributed { get; set; }

        public void Add(PeerTally other)
        {
            Processes.Add(other.Processes);
            for (int grouping = 0; grouping < Groups.Length; grouping++)
            {
                Groups[grouping].Add(other.Groups[grouping]);
            }

            Unattributed += other.Unattributed;
        }
    }

    /// <summary>One grouping's distinct peers, known records and unknown records, per group.</summary>
    private sealed class Grouping(int slots)
    {
        private readonly HashSet<int>?[] peers = new HashSet<int>?[slots];
        private readonly long[] known = new long[slots];
        private readonly long[] unknown = new long[slots];

        /// <summary>
        /// <paramref name="records"/> records alike: their owner's group and their other end's, -1 where the policy does not
        /// admit that end, with the two instances each adds to the other's peers.
        /// </summary>
        public void Add(int ownerGroup, int peerGroup, int owner, int peer, long records)
        {
            if (ownerGroup >= 0 && peerGroup >= 0 && ownerGroup == peerGroup)
            {
                known[ownerGroup] += records;
                PeersOf(ownerGroup).Add(owner);
                PeersOf(ownerGroup).Add(peer);
                return;
            }

            if (ownerGroup >= 0)
            {
                if (peerGroup >= 0)
                {
                    known[ownerGroup] += records;
                    PeersOf(ownerGroup).Add(peer);
                }
                else
                {
                    unknown[ownerGroup] += records;
                }
            }

            if (peerGroup >= 0)
            {
                if (ownerGroup >= 0)
                {
                    known[peerGroup] += records;
                    PeersOf(peerGroup).Add(owner);
                }
                else
                {
                    unknown[peerGroup] += records;
                }
            }
        }

        public void Add(Grouping other)
        {
            for (int slot = 0; slot < peers.Length; slot++)
            {
                known[slot] += other.known[slot];
                unknown[slot] += other.unknown[slot];
                if (other.peers[slot] is { } theirs)
                {
                    PeersOf(slot).UnionWith(theirs);
                }
            }
        }

        /// <summary>A group's count, unmeasured when its records named no peer; null when it had no such record.</summary>
        public PeerCount? CountOf(int slot) => known[slot] + unknown[slot] == 0
            ? null
            : new(peers[slot] is { Count: > 0 } counted ? counted.Count : null, known[slot], unknown[slot]);

        /// <summary>
        /// How many distinct instances are some group's peer, each of them having a resolved peer in turn, but those
        /// <paramref name="aside"/> marks.
        /// </summary>
        public long Distinct(bool[] aside) =>
            peers.Where(set => set is not null).SelectMany(set => set!).Distinct().LongCount(instance => !aside[instance]);

        private HashSet<int> PeersOf(int slot) => peers[slot] ??= [];
    }
}
