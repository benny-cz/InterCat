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
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest)
            .Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest)
            .Select(name => SessionSegments.Open(store, manifest, name))];
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        ProcessInstanceIndex processes = derivation.Processes(store.Root, segments, clock, fields, cancellationToken);
        TransportRelationIndex relations = derivation.Relations(store.Root, segments, clock, fields, cancellationToken);

        // Only the relations the overview draws: paired TCP incarnations whose two instances the policy admits.
        var channels = new Dictionary<int, (string Edge, string Channel)>();
        foreach (TransportRelation relation in relations.Relations)
        {
            if (relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy))
            {
                channels[relation.Channel] = (SessionOverviewProjector.EdgeKeyOf(relation), relation.StableKey);
            }
        }

        var edgeRecords = new Dictionary<string, long>(StringComparer.Ordinal);
        var channelRecords = new Dictionary<string, long>(StringComparer.Ordinal);
        var processRecords = new Dictionary<int, Dictionary<Mechanism, long>>();
        long observed = 0;
        long graph = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            RentedRows<ChannelBinding>? rented = null;
            RentedRows<ProcessBinding>? owners = null;
            try
            {
                for (int row = 0; row < segment.RowCount; row++)
                {
                    if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (times.SignedAt(row) is not { } nanoseconds || !interval.Contains(nanoseconds / 100)) continue;
                    observed++;

                    // Bindings are derived only for a segment that has a row inside the interval. A row's owner binds as
                    // the whole-session count binds it, so the policy admits the same rows here as there.
                    if (owners is null)
                    {
                        owners = RentedRows<ProcessBinding>.For(segment);
                        processes.OwnersOf(segment, owners.Value.Span);
                    }

                    ProcessBinding owner = owners.Value.Buffer[row];
                    if (owner.IsAdmittedUnder(policy))
                    {
                        if (!processRecords.TryGetValue(owner.Instance, out Dictionary<Mechanism, long>? tally))
                        {
                            tally = [];
                            processRecords[owner.Instance] = tally;
                        }

                        var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
                        tally[mechanism] = tally.GetValueOrDefault(mechanism) + 1;
                    }

                    if (rented is null)
                    {
                        rented = RentedRows<ChannelBinding>.For(segment);
                        relations.ChannelsOf(segment, rented.Value.Span);
                    }

                    ChannelBinding binding = rented.Value.Buffer[row];
                    if (!binding.IsKnown || !channels.TryGetValue(binding.Channel, out (string Edge, string Channel) keys))
                    {
                        continue;
                    }

                    graph++;
                    edgeRecords[keys.Edge] = edgeRecords.GetValueOrDefault(keys.Edge) + 1;
                    channelRecords[keys.Channel] = channelRecords.GetValueOrDefault(keys.Channel) + 1;
                }
            }
            finally
            {
                rented?.Dispose();
                owners?.Dispose();
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, edgeRecords, channelRecords, observed, graph)
        {
            ProcessRecords = processRecords.ToDictionary(
                entry => processes.Instances[entry.Key].Id,
                entry => (IReadOnlyList<MechanismCount>)Array.AsReadOnly([.. entry.Value
                    .OrderByDescending(count => count.Value)
                    .ThenBy(count => count.Key)
                    .Select(count => new MechanismCount(count.Key, count.Value))])),
        };
    }
}
