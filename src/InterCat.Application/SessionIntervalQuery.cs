using System.Runtime.CompilerServices;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Observed records per graph edge, per paired channel and per process instance inside one analysis interval of a
/// leased generation. The admission, channel and binding rules are exactly the overview's, so a ranking scoped to a
/// brushed interval counts the same kind of record the whole-session ranking counts, only fewer of them (plan §3.4, §6.4).
/// </summary>
public sealed record SessionIntervalCounts(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyDictionary<string, long> EdgeRecords,
    IReadOnlyDictionary<string, long> ChannelRecords,
    long ObservedRows,
    long GraphRows)
{
    /// <summary>
    /// Each process instance's own records inside the interval that the evidence policy admits, by mechanism, most
    /// first: what the ranked table counts over the whole session (`process-activity-v1`), only fewer. An instance with
    /// none in the interval is absent.
    /// </summary>
    public IReadOnlyDictionary<ProcessInstanceId, IReadOnlyList<MechanismCount>> ProcessRecords { get; init; } =
        new Dictionary<ProcessInstanceId, IReadOnlyList<MechanismCount>>();

    /// <summary>
    /// The capture's own coverage over the interval, whatever was observed in it: what a process's count there is judged
    /// by, since it could have made any record the capture collects (§10.3). Null when not judged.
    /// </summary>
    public CoverageState? CaptureCoverage { get; init; }

    /// <summary>TCP's coverage over the interval, which a paired channel's count there is judged by. Null when not judged.</summary>
    public CoverageState? TcpCoverage { get; init; }

    /// <summary>
    /// Each mechanism's coverage over the interval and the fact that decided it (`coverage-v2` §4): what a count of its
    /// records there could have seen, stated apart from the count and never rolled into one state (metrics-v1 §7, R21).
    /// Empty when not judged.
    /// </summary>
    public IReadOnlyList<MechanismCoverage> MechanismCoverage { get; init; } = [];
}

public static class SessionIntervalQuery
{
    /// <summary>
    /// Counts the records inside a half-open interval of 100-nanosecond session-relative presentation ticks. A row
    /// without a usable session time cannot be placed in an interval and is not counted, exactly as the timeline omits it.
    /// </summary>
    public static SessionIntervalCounts Count(
        SessionStore store,
        TimeRange interval,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so an interval cannot be placed.");

        // A brush binds with the instances and channels the derivation or the checkpoint holds: a reopened session's first
        // brush opened every segment, wherever it was (P25). Only a derivation neither holds opens the rest.
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        TransportRelationIndex relations = generation.Relations(cancellationToken);

        // Only the relations the overview draws: paired TCP incarnations whose two instances the policy admits. Each
        // gets a dense slot, so a row's count is an array index rather than a key hashed per row (R11).
        int[] slotOfChannel = new int[relations.Channels];
        Array.Fill(slotOfChannel, -1);
        var drawn = new List<(string Edge, string Channel)>();
        foreach (TransportRelation relation in relations.Relations)
        {
            if (relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy)
                && relation.Channel >= 0 && relation.Channel < slotOfChannel.Length)
            {
                slotOfChannel[relation.Channel] = drawn.Count;
                drawn.Add((SessionOverviewProjector.EdgeKeyOf(relation), relation.StableKey));
            }
        }

        // A finished session's brush counts from the tile index its checkpoint published, opening no segment (S3).
        // Otherwise it counts only the segments its interval meets, each by one worker into tallies of its own, and the
        // workers' tallies are summed: the same counts a serial pass makes, side by side (SegmentPasses).
        int instances = processes.Instances.Count;
        int slots = TimelineColumns.SlotCount;
        IntervalTally? total = PersistedTally(store, manifest, interval, policy, processes, relations, slotOfChannel, drawn.Count,
            cancellationToken);
        if (total is null)
        {
            total = new IntervalTally(instances * slots, drawn.Count);
            SegmentPasses.Run(
                SessionNativeInterval.Segments(store, manifest, interval, clock),
                () => new IntervalTally(instances * slots, drawn.Count),
                (segment, tally) => CountSegment(segment, interval, policy, processes, relations, slotOfChannel, slots, tally,
                    cancellationToken),
                total.Add,
                cancellationToken);
        }

        var edgeRecords = new Dictionary<string, long>(StringComparer.Ordinal);
        var channelRecords = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int slot = 0; slot < drawn.Count; slot++)
        {
            if (total.Channels[slot] > 0)
            {
                (string edge, string channel) = drawn[slot];
                edgeRecords[edge] = edgeRecords.GetValueOrDefault(edge) + total.Channels[slot];
                channelRecords[channel] = total.Channels[slot];
            }
        }

        var processRecords = new Dictionary<ProcessInstanceId, IReadOnlyList<MechanismCount>>();
        for (int instance = 0; instance < instances; instance++)
        {
            MechanismCount[] made = [.. Enumerable.Range(0, slots)
                .Where(slot => total.Processes[(instance * slots) + slot] > 0)
                .Select(slot => new MechanismCount(TimelineColumns.MechanismAt(slot), total.Processes[(instance * slots) + slot]))
                .OrderByDescending(count => count.Records)
                .ThenBy(count => count.Mechanism)];
            if (made.Length > 0)
            {
                processRecords[processes.Instances[instance].Id] = Array.AsReadOnly(made);
            }
        }

        // An interval no source reading falls in has no coverage to judge.
        CoverageLedgerV1? ledger = SessionSegments.CoverageLedger(store.Root, manifest);
        TimeRange? native = RankingScope.NativeInterval(clock, interval);

        // An RPC edge counts its links' call records the interval holds, as the overview draws it (operations-v1 §5c).
        long rpcRecords = 0;
        if (native is { } range && RpcPeerEdges.Collected(ledger))
        {
            foreach (RpcPeerEdge edge in RpcPeerEdges.Of(generation.RpcPeers(cancellationToken), policy, range))
            {
                if (edge.Records > 0)
                {
                    edgeRecords[edge.Key] = edge.Records;
                    rpcRecords += edge.Records;
                }
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, edgeRecords, channelRecords, total.Observed, total.Graph + rpcRecords)
        {
            ProcessRecords = processRecords,
            CaptureCoverage = native is null ? CoverageState.UnknownCoverage : SessionCoverage.CaptureStates(ledger, [native])[0],
            TcpCoverage = native is null ? CoverageState.UnknownCoverage : SessionCoverage.Of(ledger, Mechanism.Tcp, native).State,
            MechanismCoverage = CoverageText.Over(ledger, clock, interval),
        };
    }

    /// <summary>
    /// The brush's counts from the tile index the generation's checkpoint published (`contracts/tile-index-v1.md` §4): each
    /// tile its interval holds whole from its tallies, and the records of the tiles its ends fall among, opening no segment
    /// but one whose readings go backwards, which keeps no tiles and has its rows read. Null when the generation names no
    /// tile index, or one without a section of each of its segments, or one whose bindings are not this derivation's, which
    /// a later checkpoint publishes again; and when a part of it could not be read, which is said once, and the index is
    /// not read again for the generation. The brush then counts from its segments, and counts the same.
    /// </summary>
    private static IntervalTally? PersistedTally(
        SessionStore store,
        SessionManifestV1 manifest,
        TimeRange interval,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        int[] slotOfChannel,
        int drawn,
        CancellationToken cancellationToken)
    {
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        if (derivation.PersistedTiles(store.Root) is not { } tiles)
        {
            return null;
        }

        StoreDependency[] segments = SessionOverviewIndex.ObservationSegments(manifest);
        if (!segments.All(tiles.Describes) || !tiles.Derivation.Matches(processes, relations))
        {
            return null;
        }

        int slots = TimelineColumns.SlotCount;
        var tally = new IntervalTally(processes.Instances.Count * slots, drawn);
        try
        {
            using TileReading reading = tiles.Read(store.Root);
            foreach (StoreDependency segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reading.CountInterval(reading.Section(segment.Name), interval, policy, slotOfChannel, tally, cancellationToken))
                {
                    CountSegment(SessionSegments.Open(store, manifest, segment.Name), interval, policy, processes, relations,
                        slotOfChannel, slots, tally, cancellationToken);
                }
            }

            return tally;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            derivation.RefuseTiles(exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Counts one segment's records inside the interval: every one observed, each by the instance its owner binds to as
    /// the policy admits it, and each of a drawn channel by that channel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CountSegment(
        SegmentReaderV1 segment,
        TimeRange interval,
        EvidencePolicy policy,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        int[] slotOfChannel,
        int slots,
        IntervalTally tally,
        CancellationToken cancellationToken)
    {
        // A segment whose timed readings all lie outside the interval has nothing to count, and its tiles say so without
        // a row being read.
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        if (tiles.First is not { } first || tiles.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return;
        }

        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        // The derivation's bindings of every row, kept with the reader, so a later brush reads them again rather than the
        // columns they come from (SegmentBindings).
        PackedOwners ownerRows = SegmentBindings.OwnersOf(segment, processes);
        PackedChannels channelRows = SegmentBindings.ChannelsOf(segment, relations);
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is not { } nanoseconds || !interval.Contains(nanoseconds / 100)) continue;
            tally.Observed++;
            ProcessBinding owner = ownerRows[row];
            if (owner.IsAdmittedUnder(policy))
            {
                tally.Processes[(owner.Instance * slots)
                    + TimelineColumns.SlotOfMechanism((Mechanism)mechanisms.UnsignedAt(row)!.Value)]++;
            }

            ChannelBinding binding = channelRows[row];
            if (binding.IsKnown && binding.Channel < slotOfChannel.Length && slotOfChannel[binding.Channel] is >= 0 and int slot)
            {
                tally.Graph++;
                tally.Channels[slot]++;
            }
        }
    }
}

/// <summary>
/// One brush's counts, or one worker's share of them: the records observed in the interval, those of a drawn channel, and
/// each instance's admitted records per mechanism slot (flat) and each drawn channel's.
/// </summary>
internal sealed class IntervalTally(int processCells, int channels)
{
    public long Observed { get; set; }

    public long Graph { get; set; }

    public long[] Processes { get; } = new long[processCells];

    public long[] Channels { get; } = new long[channels];

    /// <summary>Sums another worker's counts into these.</summary>
    public void Add(IntervalTally other)
    {
        Observed += other.Observed;
        Graph += other.Graph;
        for (int cell = 0; cell < Processes.Length; cell++)
        {
            Processes[cell] += other.Processes[cell];
        }

        for (int slot = 0; slot < Channels.Length; slot++)
        {
            Channels[slot] += other.Channels[slot];
        }
    }
}
