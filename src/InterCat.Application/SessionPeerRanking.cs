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
/// Every process instance's and executable group's distinct peers over one scope of one generation. Distinct counts
/// overlap - a record between two processes makes each the other's peer - so the rows never add up to a total.
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
/// Reads each process's and each executable group's distinct peers for the ranked table: what `icat metric --metric
/// active-peers --group-by process` answers for every instance, and `--group-by executable` for every group whose
/// executable is known (R18), from the derivation the overview already holds rather than a derivation of its own.
/// </summary>
public static class SessionPeerRanking
{
    /// <summary>
    /// Counts the whole retained capture, or <paramref name="interval"/> in 100-nanosecond presentation ticks, whose bounds
    /// are placed on the source clock as the command line places a time. A record is in scope when its reading is.
    /// </summary>
    public static SessionPeerMeasures Measure(
        SessionStore store,
        TimeRange? interval,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its processes cannot be identified.");

        // A time scope reads only the segments holding a reading in it, with the instances and channels the derivation or
        // the checkpoint holds (P25); the whole session reads every segment.
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TransportRelationIndex relations = generation.Relations(cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;
        SegmentReaderV1[] segments = native is { } readings
            ? SessionNativeInterval.Segments(store, manifest, (readings.StartTicks, readings.EndTicks))
            : interval is null ? generation.All : [];

        // Each instance's group as the ladder keys it, as a dense index.
        int instances = processes.Instances.Count;
        var groupKeys = new List<string>();
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] groupOf = new int[instances];
        for (int instance = 0; instance < instances; instance++)
        {
            string key = SessionOverviewProjector.GroupKey(processes.Instances[instance]);
            if (!groupIndex.TryGetValue(key, out int index))
            {
                groupIndex[key] = index = groupKeys.Count;
                groupKeys.Add(key);
            }

            groupOf[instance] = index;
        }

        // An interval no reading can fall in holds nothing, and no segment is read.
        var total = new PeerTally(instances, groupKeys.Count);
        if (interval is null || native is not null)
        {
            SegmentPasses.Run(
                segments,
                () => new PeerTally(instances, groupKeys.Count),
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

        var byGroup = new Dictionary<string, PeerCount>(StringComparer.Ordinal);
        for (int group = 0; group < groupKeys.Count; group++)
        {
            if (total.Groups.CountOf(group) is { } counted)
            {
                byGroup[groupKeys[group]] = counted;
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess, byGroup)
        {
            WithPeers = total.Processes.Distinct(),
            Unattributed = total.Unattributed,
        };
    }

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
        int[] groupOf,
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

            ProcessBinding owner = owners[row];
            ProcessBinding peer = peers[row];
            int self = owner.IsAdmittedUnder(policy) ? owner.Instance : -1;
            int other = peer.IsAdmittedUnder(policy) ? peer.Instance : -1;
            tally.Processes.Add(self, other, self, other);
            tally.Groups.Add(self < 0 ? -1 : groupOf[self], other < 0 ? -1 : groupOf[other], self, other);
            if (self < 0 && other < 0)
            {
                tally.Unattributed++;
            }
        }
    }

    /// <summary>One worker's peers by process and by group, merged once it has no segment left.</summary>
    private sealed class PeerTally(int instances, int groups)
    {
        public Grouping Processes { get; } = new(instances);

        public Grouping Groups { get; } = new(groups);

        public long Unattributed { get; set; }

        public void Add(PeerTally other)
        {
            Processes.Add(other.Processes);
            Groups.Add(other.Groups);
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
        /// One record: its owner's group and its other end's, -1 where the policy does not admit that end, with the two
        /// instances each adds to the other's peers.
        /// </summary>
        public void Add(int ownerGroup, int peerGroup, int owner, int peer)
        {
            if (ownerGroup >= 0 && peerGroup >= 0 && ownerGroup == peerGroup)
            {
                known[ownerGroup]++;
                PeersOf(ownerGroup).Add(owner);
                PeersOf(ownerGroup).Add(peer);
                return;
            }

            if (ownerGroup >= 0)
            {
                if (peerGroup >= 0)
                {
                    known[ownerGroup]++;
                    PeersOf(ownerGroup).Add(peer);
                }
                else
                {
                    unknown[ownerGroup]++;
                }
            }

            if (peerGroup >= 0)
            {
                if (ownerGroup >= 0)
                {
                    known[peerGroup]++;
                    PeersOf(peerGroup).Add(owner);
                }
                else
                {
                    unknown[peerGroup]++;
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

        /// <summary>How many distinct instances are some group's peer: each of them has a resolved peer in turn.</summary>
        public long Distinct() => peers.Where(set => set is not null).SelectMany(set => set!).Distinct().LongCount();

        private HashSet<int> PeersOf(int slot) => peers[slot] ??= [];
    }
}
