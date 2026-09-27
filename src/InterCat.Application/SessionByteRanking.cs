using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One process instance's transport-observed bytes on its own records: those its send records measured, taken under
/// sender accounting, and those its receive records measured, under receiver accounting (`metrics-v1` §4, §6). A declared
/// measurement with no value is unmeasured, counted apart and never summed as zero (R3).
/// </summary>
public sealed record ProcessBytes(
    long SentBytes,
    long SentMeasured,
    long SentUnmeasured,
    long ReceivedBytes,
    long ReceivedMeasured,
    long ReceivedUnmeasured)
{
    /// <summary>A process with no send or receive record in scope.</summary>
    public static ProcessBytes None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Bytes on the process's own records that state neither side, which only endpoint activity takes: every contribution
    /// at the endpoint that recorded it (`metrics-v1` §4).
    /// </summary>
    public long OtherBytes { get; init; }

    public long OtherMeasured { get; init; }

    public long OtherUnmeasured { get; init; }

    /// <summary>The value a ranking reads, with the measured and unmeasured contributions behind it.</summary>
    public RankedValue Of(RankingMetric metric)
    {
        switch (metric)
        {
            case RankingMetric.BytesSent:
                return new(metric, SentMeasured > 0 ? SentBytes : null, SentMeasured, SentUnmeasured);
            case RankingMetric.BytesReceived:
                return new(metric, ReceivedMeasured > 0 ? ReceivedBytes : null, ReceivedMeasured, ReceivedUnmeasured);
            case RankingMetric.EndpointBytes:
                long measured = SentMeasured + ReceivedMeasured + OtherMeasured;
                return new(metric, measured > 0 ? checked(SentBytes + ReceivedBytes + OtherBytes) : null, measured,
                    SentUnmeasured + ReceivedUnmeasured + OtherUnmeasured);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only a byte ranking reads a process's bytes.");
        }
    }

    /// <summary>Two processes' bytes together: a group's, which its members partition.</summary>
    public ProcessBytes Plus(ProcessBytes other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            checked(SentBytes + other.SentBytes),
            SentMeasured + other.SentMeasured,
            SentUnmeasured + other.SentUnmeasured,
            checked(ReceivedBytes + other.ReceivedBytes),
            ReceivedMeasured + other.ReceivedMeasured,
            ReceivedUnmeasured + other.ReceivedUnmeasured)
        {
            OtherBytes = checked(OtherBytes + other.OtherBytes),
            OtherMeasured = OtherMeasured + other.OtherMeasured,
            OtherUnmeasured = OtherUnmeasured + other.OtherUnmeasured,
        };
    }
}

/// <summary>One end of a drawn channel: the channel's key and the process instance holding that end.</summary>
public readonly record struct ChannelEnd(string ChannelKey, ProcessInstanceId Process);

/// <summary>
/// Every process instance's transport bytes over one scope of one generation, and each end's bytes on every channel the
/// overview draws: an admitted paired TCP incarnation, whose two ends are held by the two processes it relates.
/// </summary>
public sealed record SessionByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange? Interval,
    IReadOnlyDictionary<ProcessInstanceId, ProcessBytes> ByProcess,
    ProcessBytes Unattributed) : IRankingMeasures
{
    /// <summary>
    /// The bytes each end's own records measured on each drawn channel; an end with no send or receive record in scope is
    /// absent. A record belongs to the end its owner holds, under the evidence policy, as it belongs to that process.
    /// </summary>
    public IReadOnlyDictionary<ChannelEnd, ProcessBytes> ByChannelEnd { get; init; } = new Dictionary<ChannelEnd, ProcessBytes>();
}

/// <summary>
/// Reads each process's transport bytes for the ranked table: what `icat metric --metric bytes-sent --byte-domain
/// transport-observed --side send --group-by process` answers for every instance, and the same for received bytes under
/// receiver accounting, in one pass (R18). A row belongs to the instance its own owner binds to under the evidence policy;
/// one the policy does not admit, or that binds to none, is counted apart as unattributed and never given to a process.
/// </summary>
public static class SessionByteRanking
{
    /// <summary>
    /// Measures the whole retained capture, or <paramref name="interval"/> in 100-nanosecond presentation ticks, which is
    /// converted to the first native reading at or after each bound, as the command line converts a time (metrics-v1 §5).
    /// </summary>
    public static SessionByteMeasures Measure(
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
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
        TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;

        // The channels the overview draws, each with a dense slot and the instance holding each of its ends.
        int instances = processes.Instances.Count;
        var indexOf = new Dictionary<ProcessInstanceId, int>(instances);
        for (int instance = 0; instance < instances; instance++)
        {
            indexOf[processes.Instances[instance].Id] = instance;
        }

        int[] slotOfChannel = new int[relations.Channels];
        Array.Fill(slotOfChannel, -1);
        var drawn = new List<(string Key, int First, int Second)>();
        foreach (TransportRelation relation in relations.Relations)
        {
            if (relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy)
                && relation.Channel >= 0 && relation.Channel < slotOfChannel.Length)
            {
                slotOfChannel[relation.Channel] = drawn.Count;
                drawn.Add((relation.StableKey, indexOf[relation.First.Id], indexOf[relation.Second.Id]));
            }
        }

        // One slot per instance, then one for every row no instance takes under the policy; one per channel end, then
        // one for every row at no drawn end. An interval no reading can fall in holds nothing, and no segment is read.
        var total = new Tallies(instances + 1, (drawn.Count * 2) + 1);
        if (interval is null || native is not null)
        {
            SegmentPasses.Run(
                segments,
                () => new Tallies(instances + 1, (drawn.Count * 2) + 1),
                (segment, tally) => MeasureSegment(segment, native, policy, processes, relations, slotOfChannel, drawn, tally),
                total.Add,
                cancellationToken);
        }

        var byProcess = new Dictionary<ProcessInstanceId, ProcessBytes>();
        for (int instance = 0; instance < instances; instance++)
        {
            ProcessBytes measured = total.Processes.Of(instance);
            if (measured != ProcessBytes.None)
            {
                byProcess[processes.Instances[instance].Id] = measured;
            }
        }

        var byChannelEnd = new Dictionary<ChannelEnd, ProcessBytes>();
        for (int slot = 0; slot < drawn.Count; slot++)
        {
            (string key, int first, int second) = drawn[slot];
            foreach ((int end, int instance) in new[] { (0, first), (1, second) })
            {
                ProcessBytes measured = total.Ends.Of((slot * 2) + end);
                if (measured != ProcessBytes.None)
                {
                    byChannelEnd[new(key, processes.Instances[instance].Id)] = measured;
                }
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess, total.Processes.Of(instances))
        {
            ByChannelEnd = byChannelEnd,
        };
    }

    private static void MeasureSegment(
        SegmentReaderV1 segment,
        TimeRange? native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        int[] slotOfChannel,
        List<(string Key, int First, int Second)> drawn,
        Tallies tally)
    {
        int instances = processes.Instances.Count;
        int noEnd = drawn.Count * 2;
        PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
        PackedChannels channels = SegmentBindings.ChannelsOf(segment, relations);
        int[] groups = new int[segment.RowCount];
        int[] ends = new int[segment.RowCount];
        for (int row = 0; row < groups.Length; row++)
        {
            ProcessBinding owner = owners[row];
            bool admitted = owner.IsAdmittedUnder(policy);
            groups[row] = admitted ? owner.Instance : instances;

            // A row is at the end its own owner holds of the channel it belongs to; a process connected to itself holds
            // both, and its records are counted at the first.
            ChannelBinding binding = channels[row];
            int slot = admitted && binding.IsKnown && binding.Channel < slotOfChannel.Length ? slotOfChannel[binding.Channel] : -1;
            ends[row] = slot < 0 ? noEnd
                : owner.Instance == drawn[slot].First ? slot * 2
                : owner.Instance == drawn[slot].Second ? (slot * 2) + 1
                : noEnd;
        }

        var domain = new DomainMeasurementSpec { Domain = ByteDomain.TransportObserved, Interval = native };
        Add(tally.Processes, SegmentMeasurement.MeasureDomainByGroup(segment, domain, groups, instances + 1));
        if (drawn.Count > 0)
        {
            Add(tally.Ends, SegmentMeasurement.MeasureDomainByGroup(segment, domain, ends, noEnd + 1));
        }
    }

    private static void Add(ByteTally tally, GroupedDomainMeasurement measured)
    {
        for (int group = 0; group < measured.Groups.Count; group++)
        {
            foreach (SideMeasurement side in measured.Groups[group])
            {
                tally.Add(group, side);
            }
        }
    }

    /// <summary>One worker's sums by process and by channel end, merged once it has no segment left.</summary>
    private sealed class Tallies(int processSlots, int endSlots)
    {
        public ByteTally Processes { get; } = new(processSlots);

        public ByteTally Ends { get; } = new(endSlots);

        public void Add(Tallies other)
        {
            Processes.Add(other.Processes);
            Ends.Add(other.Ends);
        }
    }

    /// <summary>One worker's sums per slot and side, merged once it has no segment left (<see cref="SegmentPasses"/>).</summary>
    private sealed class ByteTally(int slots)
    {
        private readonly long[] sent = new long[slots];
        private readonly long[] sentMeasured = new long[slots];
        private readonly long[] sentUnmeasured = new long[slots];
        private readonly long[] received = new long[slots];
        private readonly long[] receivedMeasured = new long[slots];
        private readonly long[] receivedUnmeasured = new long[slots];
        private readonly long[] other = new long[slots];
        private readonly long[] otherMeasured = new long[slots];
        private readonly long[] otherUnmeasured = new long[slots];

        public void Add(int slot, SideMeasurement side)
        {
            switch (side.Side)
            {
                case AccountingSide.SendSide:
                    sent[slot] = checked(sent[slot] + side.TotalBytes);
                    sentMeasured[slot] += side.KnownContributions;
                    sentUnmeasured[slot] += side.UnknownContributions;
                    break;
                case AccountingSide.ReceiveSide:
                    received[slot] = checked(received[slot] + side.TotalBytes);
                    receivedMeasured[slot] += side.KnownContributions;
                    receivedUnmeasured[slot] += side.UnknownContributions;
                    break;
                default:
                    other[slot] = checked(other[slot] + side.TotalBytes);
                    otherMeasured[slot] += side.KnownContributions;
                    otherUnmeasured[slot] += side.UnknownContributions;
                    break;
            }
        }

        public void Add(ByteTally other)
        {
            for (int slot = 0; slot < sent.Length; slot++)
            {
                sent[slot] = checked(sent[slot] + other.sent[slot]);
                sentMeasured[slot] += other.sentMeasured[slot];
                sentUnmeasured[slot] += other.sentUnmeasured[slot];
                received[slot] = checked(received[slot] + other.received[slot]);
                receivedMeasured[slot] += other.receivedMeasured[slot];
                receivedUnmeasured[slot] += other.receivedUnmeasured[slot];
                this.other[slot] = checked(this.other[slot] + other.other[slot]);
                otherMeasured[slot] += other.otherMeasured[slot];
                otherUnmeasured[slot] += other.otherUnmeasured[slot];
            }
        }

        public ProcessBytes Of(int slot) => new(
            sent[slot], sentMeasured[slot], sentUnmeasured[slot], received[slot], receivedMeasured[slot], receivedUnmeasured[slot])
        {
            OtherBytes = other[slot],
            OtherMeasured = otherMeasured[slot],
            OtherUnmeasured = otherUnmeasured[slot],
        };
    }
}
