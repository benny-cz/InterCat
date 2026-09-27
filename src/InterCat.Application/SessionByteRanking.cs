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

    /// <summary>The value a ranking reads, with the measured and unmeasured contributions behind it.</summary>
    public RankedValue Of(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => new(metric, SentMeasured > 0 ? SentBytes : null, SentMeasured, SentUnmeasured),
        RankingMetric.BytesReceived => new(metric, ReceivedMeasured > 0 ? ReceivedBytes : null, ReceivedMeasured, ReceivedUnmeasured),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only a byte ranking reads a process's bytes."),
    };

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
            ReceivedUnmeasured + other.ReceivedUnmeasured);
    }
}

/// <summary>Every process instance's transport bytes over one scope of one generation.</summary>
public sealed record SessionByteMeasures(
    Guid SessionId,
    long Generation,
    TimeRange? Interval,
    IReadOnlyDictionary<ProcessInstanceId, ProcessBytes> ByProcess,
    ProcessBytes Unattributed) : IRankingMeasures;

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
        ProcessInstanceIndex processes = SessionDerivationCache.For(manifest).Processes(store.Root, segments, clock, fields, cancellationToken);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;

        // One slot per instance, then one for every row no instance takes under the policy. An interval no reading can
        // fall in holds nothing, and no segment is read for it.
        int instances = processes.Instances.Count;
        var total = new ByteTally(instances + 1);
        if (interval is null || native is not null)
        {
            SegmentPasses.Run(
                segments,
                () => new ByteTally(instances + 1),
                (segment, tally) => MeasureSegment(segment, native, policy, processes, instances, tally),
                total.Add,
                cancellationToken);
        }

        var byProcess = new Dictionary<ProcessInstanceId, ProcessBytes>();
        for (int instance = 0; instance < instances; instance++)
        {
            ProcessBytes measured = total.Of(instance);
            if (measured != ProcessBytes.None)
            {
                byProcess[processes.Instances[instance].Id] = measured;
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess, total.Of(instances));
    }

    private static void MeasureSegment(
        SegmentReaderV1 segment,
        TimeRange? native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        int instances,
        ByteTally tally)
    {
        PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
        int[] groups = new int[segment.RowCount];
        for (int row = 0; row < groups.Length; row++)
        {
            ProcessBinding owner = owners[row];
            groups[row] = owner.IsAdmittedUnder(policy) ? owner.Instance : instances;
        }

        GroupedDomainMeasurement measured = SegmentMeasurement.MeasureDomainByGroup(
            segment,
            new() { Domain = ByteDomain.TransportObserved, Interval = native },
            groups,
            instances + 1);
        for (int group = 0; group <= instances; group++)
        {
            foreach (SideMeasurement side in measured.Groups[group])
            {
                tally.Add(group, side);
            }
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
            }
        }

        public ProcessBytes Of(int slot) => new(
            sent[slot], sentMeasured[slot], sentUnmeasured[slot], received[slot], receivedMeasured[slot], receivedUnmeasured[slot]);
    }
}
