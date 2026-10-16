using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A process's, or a group's, TCP connections the capture saw it open over one scope (`relations-v1` §3, §5): those it made
/// by a connect and those it accepted, each one record at its own end, beside the TCP records it holds in scope. Where the
/// session records no connection's open at all - a capture that collected no connect or accept, or an import whose source
/// recorded none - how many it made or accepted is unknown, never zero (R21).
/// </summary>
/// <param name="Made">The connects in scope at its end: the connections it made.</param>
/// <param name="Accepted">The accepts in scope at its end: the connections it accepted.</param>
/// <param name="TcpRecords">Its own TCP records in scope, opens among them.</param>
/// <param name="OpensRecorded">Whether the session records any connection's open, so a count of none is none.</param>
public sealed record ConnectionCount(long Made, long Accepted, long TcpRecords, bool OpensRecorded)
{
    /// <summary>Nothing in scope, in a session that records connections' opens: what a sum starts from.</summary>
    public static ConnectionCount None { get; } = new(0, 0, 0, true);

    /// <summary>Two counts together, as a group's are its members'; they partition it.</summary>
    public ConnectionCount Plus(ConnectionCount other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(checked(Made + other.Made), checked(Accepted + other.Accepted), checked(TcpRecords + other.TcpRecords),
            OpensRecorded && other.OpensRecorded);
    }

    /// <summary>
    /// The value a connection ranking reads: the connections made or accepted, over the TCP records behind them. A row with
    /// no TCP record in scope holds nothing the ranking takes; one whose session records no open is unmeasured.
    /// </summary>
    public RankedValue Of(RankingMetric metric)
    {
        long count = metric switch
        {
            RankingMetric.ConnectionsMade => Made,
            RankingMetric.ConnectionsAccepted => Accepted,
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Only a connection ranking reads connections."),
        };
        return OpensRecorded
            ? new(metric, TcpRecords > 0 ? count : null, TcpRecords, 0)
            : new(metric, null, 0, TcpRecords);
    }
}

/// <summary>
/// Every process instance's TCP connections opened over one scope of one generation, made and accepted. They add: a
/// group's are its members' (<see cref="ConnectionCount.Plus"/>).
/// </summary>
public sealed record SessionConnectionMeasures(
    Guid SessionId,
    long Generation,
    TimeRange? Interval,
    IReadOnlyDictionary<ProcessInstanceId, ConnectionCount> ByProcess) : IRankingMeasures
{
    /// <summary>Whether the session records any connection's open, so that a process with none made or accepted none.</summary>
    public required bool OpensRecorded { get; init; }

    /// <summary>A process's count, or none in scope.</summary>
    public ConnectionCount Of(ProcessInstanceId process) =>
        ByProcess.GetValueOrDefault(process) ?? ConnectionCount.None with { OpensRecorded = OpensRecorded };
}

/// <summary>
/// Reads each process's TCP connections made and accepted for the ranked table (§5.2, `relations-v1` §5): its own connect
/// and accept records in scope, each the open of one connection as the capture saw it at that process's end, from the
/// derivation the overview already holds. A record counts for the process instance it binds to under the evidence policy.
/// </summary>
public static class SessionConnectionRanking
{
    /// <summary>
    /// Counts the whole retained capture, or <paramref name="interval"/> in 100-nanosecond presentation ticks, whose bounds
    /// are placed on the source clock as the command line places a time. A record is in scope when its reading is.
    /// </summary>
    public static SessionConnectionMeasures Measure(
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

        // A time scope reads only the segments holding a reading in it; the whole session reads every segment. Whether the
        // session records any open at all is the whole derivation's to say, whatever the scope reads.
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TransportRelationIndex relations = generation.Relations(cancellationToken);
        bool opensRecorded = relations.Relations.Any(relation => relation.Opening != RelationOpening.NotWitnessed || relation.OpenWitnessed)
            || relations.OneSided.Any(connection => connection.Opening != ConnectionOpening.NotWitnessed);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;
        SegmentReaderV1[] segments = native is { } readings
            ? SessionNativeInterval.Segments(store, manifest, (readings.StartTicks, readings.EndTicks))
            : interval is null ? generation.All : [];

        int instances = processes.Instances.Count;
        var total = new ConnectionTally(instances);
        if (interval is null || native is not null)
        {
            SegmentPasses.Run(
                segments,
                () => new ConnectionTally(instances),
                (segment, tally) => MeasureSegment(segment, native, policy, processes, tally),
                total.Add,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var byProcess = new Dictionary<ProcessInstanceId, ConnectionCount>();
        for (int instance = 0; instance < instances; instance++)
        {
            if (total.Tcp[instance] > 0)
            {
                byProcess[processes.Instances[instance].Id] =
                    new(total.Made[instance], total.Accepted[instance], total.Tcp[instance], opensRecorded);
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess) { OpensRecorded = opensRecorded };
    }

    /// <summary>Counts one segment's TCP records in scope by the process each binds to, and its connects and accepts apart.</summary>
    private static void MeasureSegment(
        SegmentReaderV1 segment,
        TimeRange? native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        ConnectionTally tally)
    {
        bool[] inScope = SegmentMeasurement.RowsInScope(segment, null, null, native);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
        PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
        for (int row = 0; row < inScope.Length; row++)
        {
            if (!inScope[row] || (Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.Tcp)
            {
                continue;
            }

            ProcessBinding owner = owners[row];
            if (!owner.IsAdmittedUnder(policy))
            {
                continue;
            }

            int instance = owner.Instance;
            tally.Tcp[instance]++;
            switch ((ObservationKind)kinds.UnsignedAt(row)!.Value)
            {
                case ObservationKind.Connect:
                    tally.Made[instance]++;
                    break;
                case ObservationKind.Accept:
                    tally.Accepted[instance]++;
                    break;
            }
        }
    }

    /// <summary>One worker's counts by process, merged once it has no segment left.</summary>
    private sealed class ConnectionTally(int instances)
    {
        public long[] Tcp { get; } = new long[instances];

        public long[] Made { get; } = new long[instances];

        public long[] Accepted { get; } = new long[instances];

        public void Add(ConnectionTally other)
        {
            for (int instance = 0; instance < Tcp.Length; instance++)
            {
                Tcp[instance] += other.Tcp[instance];
                Made[instance] += other.Made[instance];
                Accepted[instance] += other.Accepted[instance];
            }
        }
    }
}
