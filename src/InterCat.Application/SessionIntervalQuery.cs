using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Observed records per graph edge and per paired channel inside one analysis interval of a leased generation. The
/// admission and channel rules are exactly the overview's, so a ranking scoped to a brushed interval counts the same
/// kind of record the whole-session ranking counts, only fewer of them (plan §3.4, §6.4).
/// </summary>
public sealed record SessionIntervalCounts(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyDictionary<string, long> EdgeRecords,
    IReadOnlyDictionary<string, long> ChannelRecords,
    long ObservedRows,
    long GraphRows);

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
            .Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest)
            .Select(name => SessionSegments.Open(store.Root, manifest, name))];
        TransportRelationIndex relations = SessionDerivationCache.For(manifest)
            .Relations(segments, clock, fields, cancellationToken);

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
        long observed = 0;
        long graph = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
            ChannelBinding[]? bindings = null;
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (times.SignedAt(row) is not { } nanoseconds || !interval.Contains(nanoseconds / 100)) continue;
                observed++;
                bindings ??= relations.ChannelsOf(segment);
                if (!bindings[row].IsKnown || !channels.TryGetValue(bindings[row].Channel, out (string Edge, string Channel) keys))
                {
                    continue;
                }

                graph++;
                edgeRecords[keys.Edge] = edgeRecords.GetValueOrDefault(keys.Edge) + 1;
                channelRecords[keys.Channel] = channelRecords.GetValueOrDefault(keys.Channel) + 1;
            }
        }

        return new(manifest.SessionId, manifest.Generation, interval, edgeRecords, channelRecords, observed, graph);
    }
}
