using System.Collections.ObjectModel;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The transport-observed bytes of a set of records - a process instance's own, one end of a channel's, or one interval's:
/// those its send records measured, taken under sender accounting, and those its receive records measured, under
/// receiver accounting (`metrics-v1` §4, §6). A declared measurement with no value is unmeasured, counted apart and never
/// summed as zero (R3).
/// </summary>
public sealed record TransportBytes(
    long SentBytes,
    long SentMeasured,
    long SentUnmeasured,
    long ReceivedBytes,
    long ReceivedMeasured,
    long ReceivedUnmeasured)
{
    /// <summary>Records with no send or receive record among them.</summary>
    public static TransportBytes None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Bytes on these records that state neither side, which only endpoint activity takes: every contribution
    /// at the endpoint that recorded it (`metrics-v1` §4).
    /// </summary>
    public long OtherBytes { get; init; }

    public long OtherMeasured { get; init; }

    public long OtherUnmeasured { get; init; }

    /// <summary>The value a ranking reads, with the measured and unmeasured contributions behind it.</summary>
    public RankedValue Of(RankingMetric metric)
    {
        (long? value, long measured, long unmeasured) = ValueOf(metric);
        return new(metric, value, measured, unmeasured);
    }

    /// <summary>
    /// What <see cref="Of"/> reads, without allocating, for a drawing that reads it per column: the value, null when no
    /// record measured it, and the records that measured it and that declared a size they did not record.
    /// </summary>
    public (long? Value, long Measured, long Unmeasured) ValueOf(RankingMetric metric)
    {
        switch (metric)
        {
            case RankingMetric.BytesSent:
                return (SentMeasured > 0 ? SentBytes : null, SentMeasured, SentUnmeasured);
            case RankingMetric.BytesReceived:
                return (ReceivedMeasured > 0 ? ReceivedBytes : null, ReceivedMeasured, ReceivedUnmeasured);
            case RankingMetric.EndpointBytes:
                long measured = SentMeasured + ReceivedMeasured + OtherMeasured;
                return (measured > 0 ? Total : null, measured,
                    SentUnmeasured + ReceivedUnmeasured + OtherUnmeasured);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only a byte ranking reads a process's bytes.");
        }
    }

    /// <summary>Every byte these records measured, sent, received or neither.</summary>
    internal long Total => SegmentMeasurement.AddBytes(SegmentMeasurement.AddBytes(SentBytes, ReceivedBytes), OtherBytes);

    /// <summary>
    /// Two sets of records' bytes together: a group's, which its members partition, or several columns'. Bytes past what a
    /// sum holds are refused (<see cref="SegmentMeasurement.PastASum"/>), though a query's answer never holds any: every
    /// sum of its parts fits, since their total does (<see cref="Fit"/>).
    /// </summary>
    public TransportBytes Plus(TransportBytes other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            SegmentMeasurement.AddBytes(SentBytes, other.SentBytes),
            SentMeasured + other.SentMeasured,
            SentUnmeasured + other.SentUnmeasured,
            SegmentMeasurement.AddBytes(ReceivedBytes, other.ReceivedBytes),
            ReceivedMeasured + other.ReceivedMeasured,
            ReceivedUnmeasured + other.ReceivedUnmeasured)
        {
            OtherBytes = SegmentMeasurement.AddBytes(OtherBytes, other.OtherBytes),
            OtherMeasured = OtherMeasured + other.OtherMeasured,
            OtherUnmeasured = OtherUnmeasured + other.OtherUnmeasured,
        };
    }

    /// <summary>
    /// Refuses, as their rows' measurement would, parts of an answer whose bytes add up past what a sum holds
    /// (<see cref="SegmentMeasurement.PastASum"/>), so every sum a view takes of them fits.
    /// </summary>
    internal static void Fit(IEnumerable<TransportBytes> parts)
    {
        long all = 0;
        foreach (TransportBytes part in parts)
        {
            all = SegmentMeasurement.AddBytes(all, part.Total);
        }
    }

    /// <summary><paramref name="parts"/>, read only, once their bytes are found to fit what a sum holds (<see cref="Fit"/>).</summary>
    internal static ReadOnlyCollection<TransportBytes> Fitted(TransportBytes[] parts)
    {
        Fit(parts);
        return Array.AsReadOnly(parts);
    }
}

/// <summary>One end of a drawn channel: the channel's key and the process instance holding that end.</summary>
public readonly record struct ChannelEnd(string ChannelKey, ProcessInstanceId Process);

/// <summary>
/// Every process instance's transport bytes over one scope of one generation, and each end's bytes on every channel the
/// overview draws: an admitted paired TCP incarnation or UDP flow, whose two ends are held by the two processes it relates.
/// </summary>
public sealed record SessionByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange? Interval,
    IReadOnlyDictionary<ProcessInstanceId, TransportBytes> ByProcess,
    TransportBytes Unattributed) : IRankingMeasures
{
    /// <summary>
    /// The bytes each end's own records measured on each drawn channel; an end with no send or receive record in scope is
    /// absent. A record belongs to the end its owner holds, under the evidence policy, as it belongs to that process.
    /// </summary>
    public IReadOnlyDictionary<ChannelEnd, TransportBytes> ByChannelEnd { get; init; } = new Dictionary<ChannelEnd, TransportBytes>();

    /// <summary>
    /// Whether they were answered from the bytes a finished session's persisted overview keeps (overview-index-v1 minor
    /// 3), opening no segment, rather than read from the segments; both give the same measures.
    /// </summary>
    public bool FromPersistedOverview { get; init; }

    /// <summary>
    /// Whether they were answered from the bytes the derivation counted as it read every record (revision 737), opening no
    /// segment it had not read, rather than read from the segments again; both give the same measures.
    /// </summary>
    public bool FromDerivation { get; init; }
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
        SessionDerivation derivation = SessionDerivationCache.For(manifest);

        // A finished session's persisted overview keeps the whole session's bytes before any policy, and its checkpoint the
        // instances and channels they name: the first byte view then opens no segment (overview-index-v1 minor 3).
        if (interval is null
            && derivation.PersistedOverview(store.Root) is { ProcessBytes: { } kept }
            && derivation.FromCheckpoint(store.Root, clock) is { } derived
            && FromKept(kept, derived.Processes, derived.Relations, policy, manifest) is { } answered)
        {
            return answered;
        }

        // A time scope reads only the segments holding a reading in it, with the instances and channels the derivation or
        // the checkpoint holds (P25).
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TransportRelationIndex relations = generation.Relations(cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;

        // The whole session's bytes are otherwise what the derivation counted as it read every record - extended from one
        // live generation to the next - so a live view opens no segment for them that its derivation has not read (P25,
        // revision 737). A refusal is the rows' to state, which say why.
        if (interval is null
            && generation.Activity(processes, cancellationToken) is { } activity
            && DerivedBytes(processes, relations, activity) is { } derivedBytes)
        {
            try
            {
                if (FromKept(derivedBytes, processes, relations, policy, manifest) is { } counted)
                {
                    return counted with { FromPersistedOverview = false, FromDerivation = true };
                }
            }
            catch (InvalidDataException refused) when (SegmentMeasurement.IsUnsummable(refused))
            {
                // Read from the rows below, to the reason they give.
            }
        }

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
            if (TransportRelationIndex.Relates(relation.Mechanism) && SessionOverviewProjector.Admitted(relation.Strength, policy)
                && relation.Channel >= 0 && relation.Channel < slotOfChannel.Length)
            {
                slotOfChannel[relation.Channel] = drawn.Count;
                drawn.Add((relation.StableKey, indexOf[relation.First.Id], indexOf[relation.Second.Id]));
            }
        }

        // One slot per instance, then one for every row no instance takes under the policy; one per channel end, then
        // one for every row at no drawn end. An interval no reading can fall in holds nothing, and no segment is read. A
        // finished session's interval is measured from its tile index's byte tallies, opening no segment (tile-index-v1
        // minor 6), and a live one's from its chunks' tiles where they bind as this derivation does (§6).
        var total = new Tallies(instances + 1, (drawn.Count * 2) + 1);
        if (interval is { } brush && native is { } within
            && (PersistedMeasures(store, manifest, clock, brush, within, policy, processes, relations, slotOfChannel, drawn,
                    cancellationToken)
                ?? ChunkMeasures(store, manifest, clock, brush, within, policy, processes, relations, slotOfChannel, drawn,
                    cancellationToken))
                is { } tiled)
        {
            total = tiled;
        }
        else if (interval is null || native is not null)
        {
            SegmentReaderV1[] segments = native is { } readings
                ? SessionNativeInterval.Segments(store, manifest, (readings.StartTicks, readings.EndTicks))
                : generation.All;
            SegmentPasses.Run(
                segments,
                () => new Tallies(instances + 1, (drawn.Count * 2) + 1),
                (segment, tally) => MeasureSegment(segment, native, policy, processes, relations, slotOfChannel, drawn, tally),
                total.Add,
                cancellationToken);
        }

        var byProcess = new Dictionary<ProcessInstanceId, TransportBytes>();
        for (int instance = 0; instance < instances; instance++)
        {
            TransportBytes measured = total.Processes.Of(instance);
            if (measured != TransportBytes.None)
            {
                byProcess[processes.Instances[instance].Id] = measured;
            }
        }

        var byChannelEnd = new Dictionary<ChannelEnd, TransportBytes>();
        for (int slot = 0; slot < drawn.Count; slot++)
        {
            (string key, int first, int second) = drawn[slot];
            foreach ((int end, int instance) in new[] { (0, first), (1, second) })
            {
                TransportBytes measured = total.Ends.Of((slot * 2) + end);
                if (measured != TransportBytes.None)
                {
                    byChannelEnd[new(key, processes.Instances[instance].Id)] = measured;
                }
            }
        }

        TransportBytes unattributed = total.Processes.Of(instances);
        TransportBytes.Fit(byProcess.Values.Append(unattributed));
        TransportBytes.Fit(byChannelEnd.Values);
        return new(manifest.SessionId, manifest.Generation, interval, byProcess, unattributed)
        {
            ByChannelEnd = byChannelEnd,
        };
    }

    /// <summary>
    /// The whole session's measures under <paramref name="policy"/> from the bytes a persisted overview keeps: an instance's
    /// at each strength the policy admits, the rest unattributed, and each end of each channel the policy draws. Null when
    /// they name an instance or a channel end the derivation does not hold, and the segments are read instead.
    /// </summary>
    private static SessionByteMeasures? FromKept(
        OverviewProcessBytes kept,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        EvidencePolicy policy,
        SessionManifestV1 manifest)
    {
        var positionOf = new Dictionary<ProcessInstanceId, int>(processes.Instances.Count);
        for (int position = 0; position < processes.Instances.Count; position++)
        {
            positionOf[processes.Instances[position].Id] = position;
        }

        var byProcess = new Dictionary<ProcessInstanceId, TransportBytes>();
        TransportBytes unattributed = kept.Unbound;
        foreach ((ProcessInstanceId instance, RelationStrength strength, TransportBytes bytes) in kept.Processes)
        {
            if (!positionOf.TryGetValue(instance, out int position))
            {
                return null;
            }

            if (new ProcessBinding(position, strength, ProcessBindingReason.Bound).IsAdmittedUnder(policy))
            {
                byProcess[instance] = byProcess.TryGetValue(instance, out TransportBytes? sum) ? sum.Plus(bytes) : bytes;
            }
            else
            {
                unattributed = unattributed.Plus(bytes);
            }
        }

        // The channels kept are every paired channel, whatever its own strength; the policy draws some of them. An overview
        // kept before the graph drew UDP flows kept TCP's alone, so a session with a flow measures its ends again.
        var channels = new Dictionary<string, TransportRelation>(StringComparer.Ordinal);
        foreach (TransportRelation relation in relations.Relations)
        {
            if (TransportRelationIndex.Relates(relation.Mechanism) && relation.Channel >= 0 && relation.Channel < relations.Channels)
            {
                if (relation.Mechanism == Mechanism.Udp && !kept.CoversUdp)
                {
                    return null;
                }

                channels[relation.StableKey] = relation;
            }
        }

        TransportBytes.Fit(byProcess.Values.Append(unattributed));
        var byChannelEnd = new Dictionary<ChannelEnd, TransportBytes>();
        foreach ((string channel, ProcessInstanceId holder, RelationStrength strength, TransportBytes bytes) in kept.Ends)
        {
            if (!channels.TryGetValue(channel, out TransportRelation? relation)
                || (holder != relation.First.Id && holder != relation.Second.Id))
            {
                return null;
            }

            if (SessionOverviewProjector.Admitted(relation.Strength, policy)
                && new ProcessBinding(positionOf[holder], strength, ProcessBindingReason.Bound).IsAdmittedUnder(policy))
            {
                byChannelEnd[new(channel, holder)] = bytes;
            }
        }

        TransportBytes.Fit(byChannelEnd.Values);
        return new(manifest.SessionId, manifest.Generation, null, byProcess, unattributed)
        {
            ByChannelEnd = byChannelEnd,
            FromPersistedOverview = true,
        };
    }

    /// <summary>
    /// The whole session's bytes before any evidence policy, as a persisted overview keeps them, from the counts the
    /// derivation keeps (revision 737): each instance's at each strength from its activity, and each end of each paired
    /// channel from the channel's two incarnations, its holder's own records alone. Exactly what <see cref="KeptPlan"/>
    /// counts over every segment; null when the activity or the relations keep no such counts, a byte view refuses a
    /// record, a sum passes what one holds, or an end's records bind at two strengths, and the segments are read.
    /// </summary>
    internal static OverviewProcessBytes? DerivedBytes(
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        ProcessActivityIndex activity)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(activity);
        if (!activity.Counts(processes) || !relations.Binds(processes) || activity.BytesRefused
            || activity.Owners() is not { } owners)
        {
            return null;
        }

        // Each instance's bytes at each strength a policy can admit; any other record's, unbound.
        TransportBytes unbound = TransportBytes.None;
        var kept = new Dictionary<(int Instance, RelationStrength Strength), TransportBytes>();
        foreach (RecordOwners owner in owners)
        {
            if (owner.Bytes.Refused)
            {
                return null;
            }

            if (owner.Bytes.IsEmpty)
            {
                continue;
            }

            if (owner.Instance < 0 || !KeptPlan.Keeps(owner.Strength))
            {
                if (Summed(unbound, Bytes(owner.Bytes)) is not { } sum)
                {
                    return null;
                }

                unbound = sum;
            }
            else
            {
                if (Summed(kept.GetValueOrDefault((owner.Instance, owner.Strength)), Bytes(owner.Bytes)) is not { } sum)
                {
                    return null;
                }

                kept[(owner.Instance, owner.Strength)] = sum;
            }
        }

        // Each end of every paired channel, whatever its own strength: the records its holder binds, at the one strength
        // they all bind at; a process connected to itself holds both ends, and its records are counted at the first.
        var positionOf = new Dictionary<ProcessInstanceId, int>(processes.Instances.Count);
        for (int position = 0; position < processes.Instances.Count; position++)
        {
            positionOf[processes.Instances[position].Id] = position;
        }

        var ends = new List<(string, ProcessInstanceId, RelationStrength, TransportBytes)>();
        foreach (TransportRelation relation in relations.Relations)
        {
            if (!TransportRelationIndex.Relates(relation.Mechanism) || relation.Channel < 0 || relation.Channel >= relations.Channels)
            {
                continue;
            }

            if (relations.OwnersOf(relation) is not { } records)
            {
                return null;
            }

            int first = positionOf[relation.First.Id];
            int second = positionOf[relation.Second.Id];
            var atEnd = new TransportBytes?[2];
            var strengthAt = new RelationStrength?[2];
            foreach (RecordOwners owner in records)
            {
                int end = owner.Instance < 0 || !KeptPlan.Keeps(owner.Strength) ? -1
                    : owner.Instance == first ? 0
                    : owner.Instance == second ? 1
                    : -1;
                if (end < 0 || owner.Records == 0)
                {
                    continue;
                }

                if (owner.Bytes.Refused || (strengthAt[end] is { } noted && noted != owner.Strength))
                {
                    return null;
                }

                strengthAt[end] = owner.Strength;
                if (!owner.Bytes.IsEmpty)
                {
                    if (Summed(atEnd[end], Bytes(owner.Bytes)) is not { } sum)
                    {
                        return null;
                    }

                    atEnd[end] = sum;
                }
            }

            for (int end = 0; end < 2; end++)
            {
                if (atEnd[end] is { } bytes && bytes != TransportBytes.None)
                {
                    ends.Add((relation.StableKey, end == 0 ? relation.First.Id : relation.Second.Id, strengthAt[end]!.Value, bytes));
                }
            }
        }

        return new(unbound, kept.Where(entry => entry.Value != TransportBytes.None)
            .Select(entry => (processes.Instances[entry.Key.Instance].Id, entry.Key.Strength, entry.Value)), ends);

        static TransportBytes Bytes(TransportByteSums sums) => new(
            sums.SentBytes, sums.SentMeasured, sums.SentUnmeasured, sums.ReceivedBytes, sums.ReceivedMeasured, sums.ReceivedUnmeasured)
        {
            OtherBytes = sums.OtherBytes,
            OtherMeasured = sums.OtherMeasured,
            OtherUnmeasured = sums.OtherUnmeasured,
        };
    }

    /// <summary>Two sets of records' bytes together, the first none when null; null where a sum passes what one holds.</summary>
    private static TransportBytes? Summed(TransportBytes? left, TransportBytes right)
    {
        if (left is null)
        {
            return right;
        }

        try
        {
            return left.Plus(right);
        }
        catch (InvalidDataException refused) when (SegmentMeasurement.IsUnsummable(refused))
        {
            return null;
        }
    }

    /// <summary>
    /// What a pass keeping the whole session's bytes before any evidence policy needs (overview-index-v1 minor 3): every
    /// paired channel - TCP connection or UDP flow - whatever its own strength, with the instances holding its two ends, and
    /// a slot for each instance at each strength a policy can admit. Null when the session has more paired channels than an
    /// overview keeps the ends of.
    /// </summary>
    internal sealed class KeptPlan
    {
        /// <summary>The most paired channels whose ends an overview keeps; a session with more reads them when asked.</summary>
        public const int MaximumChannels = 20_000;

        /// <summary>The most bytes the kept bytes take in an overview, of the 16 MiB it may take in all.</summary>
        public const long MaximumBytes = 12L * 1024 * 1024;

        private static readonly RelationStrength[] Strengths =
            [RelationStrength.Direct, RelationStrength.Correlated, RelationStrength.Candidate, RelationStrength.Conflicting];

        private readonly ProcessInstanceIndex processes;
        private readonly TransportRelationIndex relations;
        private readonly int[] slotOfChannel;
        private readonly List<(string Key, int First, int Second)> channels;

        private KeptPlan(
            ProcessInstanceIndex processes,
            TransportRelationIndex relations,
            int[] slotOfChannel,
            List<(string Key, int First, int Second)> channels)
        {
            this.processes = processes;
            this.relations = relations;
            this.slotOfChannel = slotOfChannel;
            this.channels = channels;
        }

        private int Unbound => processes.Instances.Count * Strengths.Length;

        private int NoEnd => channels.Count * 2;

        public static KeptPlan? For(ProcessInstanceIndex processes, TransportRelationIndex relations)
        {
            ArgumentNullException.ThrowIfNull(processes);
            ArgumentNullException.ThrowIfNull(relations);
            var indexOf = new Dictionary<ProcessInstanceId, int>(processes.Instances.Count);
            for (int instance = 0; instance < processes.Instances.Count; instance++)
            {
                indexOf[processes.Instances[instance].Id] = instance;
            }

            int[] slotOfChannel = new int[relations.Channels];
            Array.Fill(slotOfChannel, -1);
            var channels = new List<(string Key, int First, int Second)>();
            foreach (TransportRelation relation in relations.Relations)
            {
                if (TransportRelationIndex.Relates(relation.Mechanism) && relation.Channel >= 0 && relation.Channel < slotOfChannel.Length)
                {
                    slotOfChannel[relation.Channel] = channels.Count;
                    channels.Add((relation.StableKey, indexOf[relation.First.Id], indexOf[relation.Second.Id]));
                }
            }

            return channels.Count > MaximumChannels ? null : new(processes, relations, slotOfChannel, channels);
        }

        /// <summary>Whether a record binding at <paramref name="strength"/> keeps a slot of its instance's, as a policy can admit it.</summary>
        public static bool Keeps(RelationStrength strength) => SlotOf(strength) >= 0;

        /// <summary>A binding strength's slot among an instance's, or -1 for one no evidence policy admits.</summary>
        private static int SlotOf(RelationStrength strength) => strength switch
        {
            RelationStrength.Direct => 0,
            RelationStrength.Correlated => 1,
            RelationStrength.Candidate => 2,
            RelationStrength.Conflicting => 3,
            _ => -1,
        };

        /// <summary>An empty tally for one worker.</summary>
        public KeptTally Start() => new(Unbound + 1, NoEnd + 1, NoEnd);

        /// <summary>
        /// Places each of one segment's rows: at its owner's slot for the strength it binds at, or with the unbound; and at
        /// the end its owner holds of its channel, as <see cref="Measure"/> places it, noting the strength it binds at.
        /// </summary>
        public void Measure(SegmentReaderV1 segment, KeptTally tally, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(segment);
            ArgumentNullException.ThrowIfNull(tally);
            PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
            PackedChannels bound = SegmentBindings.ChannelsOf(segment, relations);
            using RentedRows<int> groupRows = RentedRows<int>.For(segment);
            using RentedRows<int> endRows = RentedRows<int>.For(segment);
            Span<int> groups = groupRows.Span;
            Span<int> ends = endRows.Span;
            for (int row = 0; row < groups.Length; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                ProcessBinding owner = owners[row];
                int strength = owner.IsBound ? SlotOf(owner.Strength) : -1;
                groups[row] = strength < 0 ? Unbound : (owner.Instance * Strengths.Length) + strength;

                // A row is at the end its own owner holds of the channel it belongs to; a process connected to itself holds
                // both, and its records are counted at the first.
                ChannelBinding binding = bound[row];
                int slot = strength >= 0 && binding.IsKnown && binding.Channel < slotOfChannel.Length ? slotOfChannel[binding.Channel] : -1;
                int end = slot < 0 ? NoEnd
                    : owner.Instance == channels[slot].First ? slot * 2
                    : owner.Instance == channels[slot].Second ? (slot * 2) + 1
                    : NoEnd;
                ends[row] = end;
                if (end != NoEnd)
                {
                    tally.Note(end, owner.Strength);
                }
            }

            var domain = new DomainMeasurementSpec { Domain = ByteDomain.TransportObserved };
            tally.Processes.Add(SegmentMeasurement.MeasureDomainByGroup(segment, domain, groups, Unbound + 1));
            if (channels.Count > 0)
            {
                tally.Ends.Add(SegmentMeasurement.MeasureDomainByGroup(segment, domain, ends, NoEnd + 1));
            }
        }

        /// <summary>
        /// What the tallies kept, for an overview to keep: null when an end's records bound at two strengths, which no
        /// binding rule gives today, or when they would take more than <see cref="MaximumBytes"/>.
        /// </summary>
        public OverviewProcessBytes? Finish(KeptTally total)
        {
            ArgumentNullException.ThrowIfNull(total);
            if (total.Mixed)
            {
                return null;
            }

            var kept = new List<(ProcessInstanceId, RelationStrength, TransportBytes)>();
            for (int instance = 0; instance < processes.Instances.Count; instance++)
            {
                for (int strength = 0; strength < Strengths.Length; strength++)
                {
                    TransportBytes bytes = total.Processes.Of((instance * Strengths.Length) + strength);
                    if (bytes != TransportBytes.None)
                    {
                        kept.Add((processes.Instances[instance].Id, Strengths[strength], bytes));
                    }
                }
            }

            var ends = new List<(string, ProcessInstanceId, RelationStrength, TransportBytes)>();
            for (int slot = 0; slot < channels.Count; slot++)
            {
                (string key, int first, int second) = channels[slot];
                for (int end = 0; end < 2; end++)
                {
                    TransportBytes bytes = total.Ends.Of((slot * 2) + end);
                    if (bytes != TransportBytes.None)
                    {
                        ends.Add((key, processes.Instances[end == 0 ? first : second].Id, total.StrengthAt((slot * 2) + end), bytes));
                    }
                }
            }

            var result = new OverviewProcessBytes(total.Processes.Of(Unbound), kept, ends);
            return result.EncodedLength <= MaximumBytes ? result : null;
        }
    }

    /// <summary>One worker's kept bytes by instance and strength, and by channel end, with the strength each end's records bind at.</summary>
    internal sealed class KeptTally(int processSlots, int endSlots, int ends)
    {
        private readonly RelationStrength[] strengths = new RelationStrength[ends];

        public TransportByteTally Processes { get; } = new(processSlots);

        public TransportByteTally Ends { get; } = new(endSlots);

        /// <summary>Whether some end's records bound at two strengths, so the ends' bytes cannot be kept by one.</summary>
        public bool Mixed { get; private set; }

        public RelationStrength StrengthAt(int end) => strengths[end];

        public void Note(int end, RelationStrength strength)
        {
            if (strengths[end] == default)
            {
                strengths[end] = strength;
            }
            else if (strengths[end] != strength)
            {
                Mixed = true;
            }
        }

        public void Add(KeptTally other)
        {
            ArgumentNullException.ThrowIfNull(other);
            Processes.Add(other.Processes);
            Ends.Add(other.Ends);
            Mixed |= other.Mixed;
            for (int end = 0; end < strengths.Length; end++)
            {
                if (other.strengths[end] != default)
                {
                    Note(end, other.strengths[end]);
                }
            }
        }
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
        tally.Processes.Add(SegmentMeasurement.MeasureDomainByGroup(segment, domain, groups, instances + 1));
        if (drawn.Count > 0)
        {
            tally.Ends.Add(SegmentMeasurement.MeasureDomainByGroup(segment, domain, ends, noEnd + 1));
        }
    }

    /// <summary>
    /// A brush's measures from the tile index the generation's checkpoint published (`contracts/tile-index-v1.md` minor 6):
    /// each tile its interval holds whole from its byte tallies, and the records of the tiles its ends fall among, from the
    /// level spanning the segments first where it keeps them. A segment whose section keeps no byte tallies, or whose
    /// readings go backwards, has its rows measured, and so does one a tile could place a record of otherwise than its
    /// row (<see cref="MeasuredByRows"/>). Null when the generation names no tile index of minor 6, or one without a
    /// section of each of its segments, or one whose bindings are not this derivation's; and when a part of it could not be
    /// read, which is said once, and the index is not read again for the generation. The brush is then measured from its
    /// segments, and measures the same.
    /// </summary>
    private static Tallies? PersistedMeasures(
        SessionStore store,
        SessionManifestV1 manifest,
        SourceClockDescriptor clock,
        TimeRange interval,
        TimeRange native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        int[] slotOfChannel,
        List<(string Key, int First, int Second)> drawn,
        CancellationToken cancellationToken)
    {
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        if (derivation.PersistedTiles(store.Root) is not { Minor: >= SessionTileIndex.ByteTalliedMinor } tiles)
        {
            return null;
        }

        StoreDependency[] segments = SessionOverviewIndex.ObservationSegments(manifest);
        if (!segments.All(tiles.Describes) || !tiles.Derivation.Matches(processes, relations))
        {
            return null;
        }

        var total = new Tallies(processes.Instances.Count + 1, (drawn.Count * 2) + 1);
        var measuring = new ByteMeasuring(SessionTileIndex.AdmittedStrengths(policy), slotOfChannel,
            [.. drawn.Select(channel => channel.First)], [.. drawn.Select(channel => channel.Second)], total.Processes, total.Ends);
        try
        {
            using TileReading reading = tiles.Read(store.Root);
            bool[] byRows = [.. segments.Select(segment =>
                MeasuredByRows(store, manifest, clock, reading.Section(segment.Name), interval))];
            if (!byRows.Contains(true) && tiles.SpansExactly(segments) && reading.Spanning().KeepsByteTallies)
            {
                TileRemainders left = reading.SpanMeasure(interval, measuring, cancellationToken);
                foreach ((TileSection section, List<TimeRange> ranges) in reading.Beneath(left))
                {
                    foreach (TimeRange range in ranges)
                    {
                        // A level spanning the segments tallies their bytes only where every section beneath it does.
                        if (!reading.MeasureInterval(section, range, measuring, cancellationToken))
                        {
                            throw SessionTileIndex.Invalid(
                                $"the level spanning its segments tallies bytes {section.Entry.Describe} does not keep.");
                        }
                    }
                }

                return total;
            }

            for (int at = 0; at < segments.Length; at++)
            {
                // A segment measured from its rows is opened only where its readings meet the brush's.
                cancellationToken.ThrowIfCancellationRequested();
                string name = segments[at].Name;
                if (byRows[at]
                    ? SessionNativeInterval.Meets(SessionSegments.NativeRange(store, manifest, name), (native.StartTicks, native.EndTicks))
                    : !reading.MeasureInterval(reading.Section(name), interval, measuring, cancellationToken))
                {
                    MeasureSegment(SessionSegments.Open(store, manifest, name), native, policy, processes, relations, slotOfChannel,
                        drawn, total);
                }
            }

            return total;
        }
        catch (Exception exception) when ((exception is InvalidDataException && !SegmentMeasurement.IsUnsummable(exception))
            || exception is IOException or UnauthorizedAccessException)
        {
            derivation.RefuseTiles(exception.Message);
            return null;
        }
    }

    /// <summary>
    /// A live generation's brush measures, for which it persists no tiles of its own, from the tiles of its chunks
    /// (`contracts/tile-index-v1.md` §6): each segment the brush meets measured from the section its chunk's tiles keep,
    /// where this derivation binds that chunk's owners as the derivation that tiled it did and finds each span of its ends'
    /// records within one incarnation of its own, its owners and channels read as this derivation's; and from its rows
    /// otherwise, and wherever a tile could place a record otherwise than its row (<see cref="MeasuredByRows"/>). Null
    /// where no chunk's tiles measure, or one could not be read, and every segment's rows are measured instead, as
    /// without them.
    /// </summary>
    private static Tallies? ChunkMeasures(
        SessionStore store,
        SessionManifestV1 manifest,
        SourceClockDescriptor clock,
        TimeRange interval,
        TimeRange native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        int[] slotOfChannel,
        List<(string Key, int First, int Second)> drawn,
        CancellationToken cancellationToken)
    {
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        IReadOnlyList<TileIndexFile> chunks = derivation.TiledChunks(store.Root);
        if (chunks.Count == 0)
        {
            return null;
        }

        var met = new List<(StoreDependency Segment, TileIndexFile? Tiles, int[]? Owners, int[]? Channels)>();
        foreach (StoreDependency segment in SessionOverviewIndex.ObservationSegments(manifest))
        {
            if (SessionNativeInterval.Meets(SessionSegments.NativeRange(store, manifest, segment.Name), (native.StartTicks, native.EndTicks)))
            {
                TileIndexFile? tiles = chunks.FirstOrDefault(chunk => chunk.Describes(segment));
                int[]? owners = tiles is null ? null : derivation.ChunkOwners(tiles, processes);
                int[]? channels = owners is null ? null : derivation.ChunkChannels(tiles!, relations);
                met.Add((segment, channels is null ? null : tiles, owners, channels));
            }
        }

        if (met.All(segment => segment.Tiles is null))
        {
            return null;
        }

        var total = new Tallies(processes.Instances.Count + 1, (drawn.Count * 2) + 1);
        var measuring = new ByteMeasuring(SessionTileIndex.AdmittedStrengths(policy), slotOfChannel,
            [.. drawn.Select(channel => channel.First)], [.. drawn.Select(channel => channel.Second)], total.Processes, total.Ends);
        try
        {
            foreach ((StoreDependency segment, TileIndexFile? tiles, int[]? owners, int[]? channels) in met)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool measured = false;
                if (tiles is not null)
                {
                    using TileReading reading = tiles.Read(store.Root);
                    reading.Rebound = owners;
                    reading.ChannelsRebound = channels;
                    TileSection section = reading.Section(segment.Name);
                    measured = !MeasuredByRows(store, manifest, clock, section, interval)
                        && reading.MeasureInterval(section, interval, measuring, cancellationToken);
                }

                if (!measured)
                {
                    MeasureSegment(SessionSegments.Open(store, manifest, segment.Name), native, policy, processes, relations,
                        slotOfChannel, drawn, total);
                }
            }

            return total;
        }
        catch (Exception exception) when ((exception is InvalidDataException && !SegmentMeasurement.IsUnsummable(exception))
            || exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a brush measures <paramref name="section"/>'s segment from its rows rather than its tiles. A brush's bounds
    /// place a record by the first native reading at or after each (metrics-v1 §5), and a tile by its tick, its
    /// nanoseconds over 100 truncated toward zero: the two place a record alike but before the capture's epoch, in the tick
    /// a bound falls in. So a segment is, for a brush starting at or before the epoch, where its section holds a reading
    /// there and its rows one before it; and wherever it holds an untimed row, which no tile holds and whose native reading
    /// can still lie in the interval.
    /// </summary>
    private static bool MeasuredByRows(
        SessionStore store, SessionManifestV1 manifest, SourceClockDescriptor clock, TileSection section, TimeRange interval) =>
        section.Untimed > 0
        || (interval.StartTicks <= 0 && section.First <= 0
            && SessionSegments.NativeRange(store, manifest, section.Entry.Name).Min < clock.CaptureEpochNativeTicks);

    /// <summary>One worker's sums by process and by channel end, merged once it has no segment left.</summary>
    private sealed class Tallies(int processSlots, int endSlots)
    {
        public TransportByteTally Processes { get; } = new(processSlots);

        public TransportByteTally Ends { get; } = new(endSlots);

        public void Add(Tallies other)
        {
            Processes.Add(other.Processes);
            Ends.Add(other.Ends);
        }
    }
}

/// <summary>
/// One worker's transport bytes per slot and side - a slot being whatever a grouping of rows gives it: a process, a channel
/// end, a column - merged once it has no segment left (<see cref="SegmentPasses"/>). Bytes are summed in bytes only, and a
/// slot's total past what a sum holds is refused (<see cref="SegmentMeasurement.PastASum"/>); the answer made of its slots
/// is fitted where it is made (<see cref="TransportBytes.Fit"/>), a slot for rows outside it never part of it.
/// </summary>
internal sealed class TransportByteTally(int slots)
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

    /// <summary>How many slots it keeps.</summary>
    public int Slots => sent.Length;

    /// <summary>
    /// Adds one segment's grouped measurement, whose groups are these slots. A segment measuring them in another unit is
    /// refused, as two units are: a byte view sums bytes, and converts nothing into them.
    /// </summary>
    public void Add(GroupedDomainMeasurement measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        if (measured.Unit is { } unit && unit != MeasurementUnit.Bytes)
        {
            throw SegmentMeasurement.Unsummable(
                $"A segment measures its transport bytes in {unit}. A byte view sums bytes, and does not convert another "
                + "unit into them.");
        }

        for (int group = 0; group < measured.Groups.Count; group++)
        {
            foreach (SideMeasurement side in measured.Groups[group])
            {
                Add(group, side);
            }
        }
    }

    public void Add(int slot, SideMeasurement side)
    {
        ArgumentNullException.ThrowIfNull(side);
        Add(slot, (int)side.Side, side.TotalBytes, side.KnownContributions, side.UnknownContributions);
    }

    /// <summary>
    /// Adds the bytes of one side's contributions to a slot, by its <c>EN-AccountingSide</c> code: a send side's as sent, a
    /// receive side's as received, any other's as other - a segment's measurement of a side, or what a tile index keeps of a
    /// tile's records (`contracts/tile-index-v1.md` minor 5).
    /// </summary>
    public void Add(int slot, int side, long total, long measured, long unmeasured)
    {
        switch ((AccountingSide)side)
        {
            case AccountingSide.SendSide:
                sent[slot] = SegmentMeasurement.AddBytes(sent[slot], total);
                sentMeasured[slot] += measured;
                sentUnmeasured[slot] += unmeasured;
                break;
            case AccountingSide.ReceiveSide:
                received[slot] = SegmentMeasurement.AddBytes(received[slot], total);
                receivedMeasured[slot] += measured;
                receivedUnmeasured[slot] += unmeasured;
                break;
            default:
                other[slot] = SegmentMeasurement.AddBytes(other[slot], total);
                otherMeasured[slot] += measured;
                otherUnmeasured[slot] += unmeasured;
                break;
        }
    }

    /// <summary>Adds one record's contribution to a slot: the bytes it measured, or -1 for a size it declared and not recorded.</summary>
    public void Add(int slot, int side, long value)
    {
        if (value < 0)
        {
            Add(slot, side, 0, 0, 1);
        }
        else
        {
            Add(slot, side, value, 1, 0);
        }
    }

    public void Add(TransportByteTally other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (int slot = 0; slot < sent.Length; slot++)
        {
            sent[slot] = SegmentMeasurement.AddBytes(sent[slot], other.sent[slot]);
            sentMeasured[slot] += other.sentMeasured[slot];
            sentUnmeasured[slot] += other.sentUnmeasured[slot];
            received[slot] = SegmentMeasurement.AddBytes(received[slot], other.received[slot]);
            receivedMeasured[slot] += other.receivedMeasured[slot];
            receivedUnmeasured[slot] += other.receivedUnmeasured[slot];
            this.other[slot] = SegmentMeasurement.AddBytes(this.other[slot], other.other[slot]);
            otherMeasured[slot] += other.otherMeasured[slot];
            otherUnmeasured[slot] += other.otherUnmeasured[slot];
        }
    }

    public TransportBytes Of(int slot) => new(
        sent[slot], sentMeasured[slot], sentUnmeasured[slot], received[slot], receivedMeasured[slot], receivedUnmeasured[slot])
    {
        OtherBytes = other[slot],
        OtherMeasured = otherMeasured[slot],
        OtherUnmeasured = otherUnmeasured[slot],
    };
}
