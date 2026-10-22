using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A process's, or a group's, TCP connections made and accepted over one scope, as the capture saw each open at either end
/// (`relations-v1` §3, §5, <see cref="ConnectionOpens"/>), beside the TCP records it holds in scope. Where the session
/// records no connection's open at all - a capture that collected no connect or accept, or an import whose source recorded
/// none - how many it made or accepted is unknown, never zero (R21).
/// </summary>
/// <param name="Made">The connections it made: by its own connect, or one the other end accepted.</param>
/// <param name="Accepted">The connections it accepted: by its own accept, or one the other end made.</param>
/// <param name="TcpRecords">Its own TCP records in scope.</param>
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
/// Reads each process's TCP connections made and accepted for the ranked table (§5.2, `relations-v1` §5): each connection
/// counts for the process that made it and the one that accepted it, as the capture saw it open at either end, from the
/// derivation the overview already holds (<see cref="ConnectionOpens"/>), so a ranking of the whole session reads what each
/// process's card says. Its TCP records are its own records in scope that bind to it under the evidence policy.
/// </summary>
public static class SessionConnectionRanking
{
    /// <summary>
    /// Counts the whole retained capture, or <paramref name="interval"/> in 100-nanosecond presentation ticks, whose bounds
    /// are placed on the source clock as the command line places a time. A record is in scope when its reading is, and a
    /// connection when its first record, at either end, is: its open, where the capture saw one.
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

        // A time scope reads only the segments holding a reading in it. The whole session's TCP records are each instance's
        // own records of TCP, which the derivation counts by the same binding a pass over the rows would make: kept with a
        // finished session's checkpoint, and extended from one live generation to the next, so the whole session opens no
        // segment it has not already read (P25). Whether the session records any open at all is the whole derivation's to
        // say, whatever the scope reads.
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TransportRelationIndex relations = generation.Relations(cancellationToken);
        bool opensRecorded = ConnectionOpens.Recorded(relations);
        TimeRange? native = interval is { } presentation ? RankingScope.NativeInterval(clock, presentation) : null;

        int instances = processes.Instances.Count;
        var total = new ConnectionTally(instances);
        if (interval is null && generation.Activity(processes, cancellationToken) is { } activity)
        {
            for (int instance = 0; instance < instances; instance++)
            {
                total.Tcp[instance] = activity.RecordsOf(instance, policy, Mechanism.Tcp);
            }
        }
        else if (interval is null || native is not null)
        {
            SegmentReaderV1[] segments = native is { } readings
                ? SessionNativeInterval.Segments(store, manifest, (readings.StartTicks, readings.EndTicks))
                : generation.All;
            SegmentPasses.Run(
                segments,
                () => new ConnectionTally(instances),
                (segment, tally) => MeasureSegment(segment, native, policy, processes, tally),
                total.Add,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A process with no TCP record in scope holds no count there, as a ranked row with none ranks after every one with
        // some; over the whole session every process a connection counts for holds that connection's records.
        Dictionary<ProcessInstanceId, (long Made, long Accepted)> opens = interval is null || native is not null
            ? ConnectionOpens.Count(relations, policy, native)
            : [];
        var byProcess = new Dictionary<ProcessInstanceId, ConnectionCount>();
        for (int instance = 0; instance < instances; instance++)
        {
            if (total.Tcp[instance] > 0)
            {
                ProcessInstanceId id = processes.Instances[instance].Id;
                (long made, long accepted) = opens.GetValueOrDefault(id);
                byProcess[id] = new(made, accepted, total.Tcp[instance], opensRecorded);
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, byProcess) { OpensRecorded = opensRecorded };
    }

    /// <summary>Counts one segment's TCP records in scope by the process each binds to.</summary>
    private static void MeasureSegment(
        SegmentReaderV1 segment,
        TimeRange? native,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        ConnectionTally tally)
    {
        bool[] inScope = SegmentMeasurement.RowsInScope(segment, null, null, native);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
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

            tally.Tcp[owner.Instance]++;
        }
    }

    /// <summary>One worker's TCP records by process, merged once it has no segment left.</summary>
    private sealed class ConnectionTally(int instances)
    {
        public long[] Tcp { get; } = new long[instances];

        public void Add(ConnectionTally other)
        {
            for (int instance = 0; instance < Tcp.Length; instance++)
            {
                Tcp[instance] += other.Tcp[instance];
            }
        }
    }
}
