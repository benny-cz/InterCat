using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since revision 169 a brushed ranking and a focused timeline count their segments side by side, and an evidence page
/// opens a segment only once its earliest reading could come next. Over random captures published in chunks, with late
/// records that make segments overlap in time, each must answer exactly what a count of every row answers.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class SegmentPassTests
{
    [Theory(DisplayName = "R13: a brushed count over many segments equals a count of every row, under every policy")]
    [InlineData(8)]
    [InlineData(21)]
    [InlineData(20_260_927)]
    public void BrushedCountsEqualACountOfEveryRow(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 4; trial++)
        {
            using var session = new TemporarySession();
            (SegmentReaderV1[] segments, ProcessInstanceIndex processes, TransportRelationIndex relations) = Published(session, random);
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                TimeRange interval = Interval(random);
                SessionIntervalCounts counts = SessionIntervalQuery.Count(session.Store, interval, policy);

                Dictionary<int, (string Edge, string Channel)> drawn = relations.Relations
                    .Where(relation => relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy))
                    .ToDictionary(relation => relation.Channel, relation => (SessionOverviewProjector.EdgeKeyOf(relation), relation.StableKey));
                long observed = 0;
                long graph = 0;
                var edges = new SortedDictionary<string, long>(StringComparer.Ordinal);
                var channels = new SortedDictionary<string, long>(StringComparer.Ordinal);
                var made = new SortedDictionary<(string Id, Mechanism Mechanism), long>();
                foreach (SegmentReaderV1 segment in segments)
                {
                    ProcessBinding[] owners = processes.OwnersOf(segment);
                    ChannelBinding[] bindings = relations.ChannelsOf(segment);
                    for (int row = 0; row < segment.RowCount; row++)
                    {
                        if (segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row) is not { } nanoseconds
                            || !interval.Contains(nanoseconds / 100))
                        {
                            continue;
                        }

                        observed++;
                        if (owners[row].IsAdmittedUnder(policy))
                        {
                            var key = (processes.Instances[owners[row].Instance].Id.ToString(),
                                (Mechanism)segment.UnsignedValue(SegmentColumnId.Mechanism, row)!.Value);
                            made[key] = made.GetValueOrDefault(key) + 1;
                        }

                        if (bindings[row].IsKnown && drawn.TryGetValue(bindings[row].Channel, out (string Edge, string Channel) keys))
                        {
                            graph++;
                            edges[keys.Edge] = edges.GetValueOrDefault(keys.Edge) + 1;
                            channels[keys.Channel] = channels.GetValueOrDefault(keys.Channel) + 1;
                        }
                    }
                }

                Assert.Equal((observed, graph), (counts.ObservedRows, counts.GraphRows));
                Assert.Equal(edges, new SortedDictionary<string, long>(counts.EdgeRecords.ToDictionary(), StringComparer.Ordinal));
                Assert.Equal(channels, new SortedDictionary<string, long>(counts.ChannelRecords.ToDictionary(), StringComparer.Ordinal));
                Assert.Equal(made, new SortedDictionary<(string Id, Mechanism Mechanism), long>(counts.ProcessRecords
                    .SelectMany(entry => entry.Value.Select(count => (Key: (entry.Key.ToString(), count.Mechanism), count.Records)))
                    .ToDictionary(entry => entry.Key, entry => entry.Records)));
            }
        }
    }

    [Theory(DisplayName = "R13: a focused timeline over many segments equals a count of every row, lane by lane")]
    [InlineData(9)]
    [InlineData(34)]
    [InlineData(20_260_927)]
    public void FocusedTimelinesEqualACountOfEveryRow(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 4; trial++)
        {
            using var session = new TemporarySession();
            (SegmentReaderV1[] segments, ProcessInstanceIndex processes, TransportRelationIndex relations) = Published(session, random);
            EvidencePolicy policy = Enum.GetValues<EvidencePolicy>()[random.Next(4)];
            TimeRange interval = Interval(random);
            int columns = random.Next(1, 40);

            // A group: every instance, so its lanes partition its focus.
            ProcessInstanceId[] group = [.. processes.Instances.Select(instance => instance.Id)];
            if (group.Length > 1)
            {
                SessionFocusedTimeline focused = SessionTimelineQuery.Focused(session.Store, interval, columns, new TimelineFocus(null, group), policy);
                AssertWholeTimeline(segments, focused);
                Assert.Equal(group.Length, focused.ProcessLanes.Count);
                Assert.Equal(Counts(segments, focused.Focus, (segment, row) => Owner(processes, segment, row, policy) is not null),
                    focused.Focus.Select(bucket => (long)bucket.ObservationCount));
                foreach (ProcessTimelineLane lane in focused.ProcessLanes)
                {
                    Assert.Equal(Counts(segments, lane.Buckets, (segment, row) => Owner(processes, segment, row, policy) == lane.ProcessId),
                        lane.Buckets.Select(bucket => (long)bucket.ObservationCount));
                }
            }

            // One process: its records split by source direction.
            ProcessInstanceId single = group[random.Next(group.Length)];
            SessionFocusedTimeline one = SessionTimelineQuery.Focused(session.Store, interval, columns, new TimelineFocus(null, [single]), policy);
            foreach (DirectionTimelineLane lane in one.DirectionLanes)
            {
                Assert.Equal(Counts(segments, lane.Buckets, (segment, row) => Owner(processes, segment, row, policy) == single
                        && (Direction)segment.UnsignedValue(SegmentColumnId.Direction, row)!.Value == lane.Direction),
                    lane.Buckets.Select(bucket => (long)bucket.ObservationCount));
            }

            // One admitted paired channel: its records at each of its two ends.
            TransportRelation[] admitted = [.. relations.Relations
                .Where(relation => relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy))];
            if (admitted.Length > 0)
            {
                TransportRelation relation = admitted[random.Next(admitted.Length)];
                SessionFocusedTimeline channel = SessionTimelineQuery.Focused(
                    session.Store, interval, columns, new TimelineFocus(relation.StableKey, []), policy);
                Assert.Equal(2, channel.ChannelEndLanes.Count);
                foreach (ChannelEndTimelineLane end in channel.ChannelEndLanes)
                {
                    Assert.Equal(Counts(segments, end.Buckets, (segment, row) =>
                            Memo(segment, relations.ChannelsOf)[row].Channel == relation.Channel && Memo(segment, TransportRelationIndex.EndsOf)[row] == end.End),
                        end.Buckets.Select(bucket => (long)bucket.ObservationCount));
                }
            }
        }
    }

    [Theory(DisplayName = "R13: evidence pages over overlapping segments list a scope's records once each, in canonical order")]
    [InlineData(10)]
    [InlineData(55)]
    [InlineData(20_260_927)]
    public void EvidencePagesListEveryRecordOnceInOrder(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 4; trial++)
        {
            using var session = new TemporarySession();
            (SegmentReaderV1[] segments, ProcessInstanceIndex processes, TransportRelationIndex relations) = Published(session, random);
            EvidencePolicy policy = Enum.GetValues<EvidencePolicy>()[random.Next(4)];
            int pageSize = random.Next(1, 10);

            foreach (ProcessInstance instance in processes.Instances)
            {
                Assert.Equal(
                    Expected(segments, (segment, row) => Owner(processes, segment, row, policy) == instance.Id),
                    Pages(cursor => SessionEvidenceQuery.Read(session.Store, ownerProcessScope: instance.Id, policy: policy,
                        pageSize: pageSize, cursor: cursor)));
            }

            foreach (TransportRelation relation in relations.Relations
                .Where(relation => relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy)))
            {
                Assert.Equal(
                    Expected(segments, (segment, row) => Memo(segment, relations.ChannelsOf)[row].Channel == relation.Channel),
                    Pages(cursor => SessionEvidenceQuery.Read(session.Store, channelKey: relation.StableKey, policy: policy,
                        pageSize: pageSize, cursor: cursor)));
            }

            // Unscoped, every record of the generation, whichever segment holds it.
            Assert.Equal(Expected(segments, (_, _) => true),
                Pages(cursor => SessionEvidenceQuery.Read(session.Store, pageSize: pageSize, cursor: cursor)));
        }
    }

    [Fact(DisplayName = "R22: a binding packed into four bytes reads back as it was, and one that does not fit is refused")]
    public void PackedBindingsReadBackAsTheyWere()
    {
        foreach (RelationStrength strength in Enum.GetValues<RelationStrength>())
        {
            foreach (ProcessBindingReason reason in Enum.GetValues<ProcessBindingReason>())
            {
                foreach (int instance in new[] { -1, 0, 1, 399, (1 << 24) - 2 })
                {
                    var binding = new ProcessBinding(instance, strength, reason);
                    Assert.Equal(binding, SegmentBindings.UnpackOwner(SegmentBindings.Pack(binding)));
                }
            }
        }

        foreach (ProcessBindingReason reason in Enum.GetValues<ProcessBindingReason>())
        {
            foreach (int channel in new[] { -1, 0, 7, (1 << 27) - 2 })
            {
                var binding = new ChannelBinding(channel, reason);
                Assert.Equal(binding, SegmentBindings.UnpackChannel(SegmentBindings.Pack(binding)));
            }
        }

        _ = Assert.Throws<InvalidOperationException>(() =>
            SegmentBindings.Pack(new ProcessBinding((1 << 24) - 1, RelationStrength.Direct, ProcessBindingReason.Bound)));
        _ = Assert.Throws<InvalidOperationException>(() =>
            SegmentBindings.Pack(new ChannelBinding((1 << 27) - 1, ProcessBindingReason.Bound)));
    }

    /// <summary>A random capture published chunk by chunk, with the derivations of its current generation.</summary>
    private static (SegmentReaderV1[] Segments, ProcessInstanceIndex Processes, TransportRelationIndex Relations) Published(
        TemporarySession session, Random random)
    {
        foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in RandomCaptures.Chunks(random))
        {
            _ = Publish(session.Store, rows, rowsPerSegment: random.Next(1, 12), fields: fields);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store.Root, manifest, name))];
        SegmentReaderV1[] fieldSegments = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(session.Store.Root, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock, fieldSegments);
        return (segments, processes, TransportRelationIndex.Derive(segments, processes));
    }

    /// <summary>An interval over the random capture's session times, which lie below 300 presentation ticks.</summary>
    private static TimeRange Interval(Random random)
    {
        long start = random.Next(0, 250);
        return new(start, start + random.Next(1, 320 - (int)start));
    }

    /// <summary>The owner a row counts for under <paramref name="policy"/>, or null when it counts for none.</summary>
    private static ProcessInstanceId? Owner(ProcessInstanceIndex processes, SegmentReaderV1 segment, int row, EvidencePolicy policy)
    {
        ProcessBinding binding = Memo(segment, processes.OwnersOf)[row];
        return binding.IsAdmittedUnder(policy) ? processes.Instances[binding.Instance].Id : null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SegmentReaderV1,
        Dictionary<(object?, System.Reflection.MethodInfo), Array>> Memos = new();

    /// <summary>A per-row binding of a segment, derived once for the segment rather than once for every row asked.</summary>
    private static T[] Memo<T>(SegmentReaderV1 segment, Func<SegmentReaderV1, T[]> derive)
    {
        Dictionary<(object?, System.Reflection.MethodInfo), Array> known = Memos.GetOrCreateValue(segment);
        lock (known)
        {
            if (!known.TryGetValue((derive.Target, derive.Method), out Array? values))
            {
                values = derive(segment);
                known[(derive.Target, derive.Method)] = values;
            }

            return (T[])values;
        }
    }

    /// <summary>The whole timeline of a focused count: every timed row, bucket by bucket.</summary>
    private static void AssertWholeTimeline(SegmentReaderV1[] segments, SessionFocusedTimeline focused) =>
        Assert.Equal(Counts(segments, focused.Whole.Buckets, (_, _) => true),
            focused.Whole.Buckets.Select(bucket => (long)bucket.ObservationCount));

    /// <summary>Per bucket, the timed rows inside it that <paramref name="counts"/> takes, read row by row.</summary>
    private static long[] Counts(
        SegmentReaderV1[] segments, IReadOnlyList<TimelineBucket> buckets, Func<SegmentReaderV1, int, bool> counts)
    {
        long[] expected = new long[buckets.Count];
        foreach (SegmentReaderV1 segment in segments)
        {
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row) is not { } nanoseconds)
                {
                    continue;
                }

                for (int bucket = 0; bucket < buckets.Count; bucket++)
                {
                    if (buckets[bucket].Interval.Contains(nanoseconds / 100) && counts(segment, row))
                    {
                        expected[bucket]++;
                    }
                }
            }
        }

        return expected;
    }

    /// <summary>The rows <paramref name="includes"/> takes, as (segment, row), in canonical row order.</summary>
    private static (string Segment, int Row)[] Expected(SegmentReaderV1[] segments, Func<SegmentReaderV1, int, bool> includes) =>
    [
        .. segments.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Where(row => includes(segment, row))
                .Select(row => (Segment: segment, Row: row)))
            .OrderBy(entry => entry.Segment.SignedValue(SegmentColumnId.NativeTicks, entry.Row))
            .ThenBy(entry => entry.Segment.UnsignedValue(SegmentColumnId.RawStreamId, entry.Row))
            .ThenBy(entry => entry.Segment.UnsignedValue(SegmentColumnId.RawSourceEpoch, entry.Row))
            .ThenBy(entry => entry.Segment.UnsignedValue(SegmentColumnId.RawRecordOrdinal, entry.Row))
            .ThenBy(entry => entry.Segment.UnsignedValue(SegmentColumnId.FactKeyHigh, entry.Row))
            .ThenBy(entry => entry.Segment.UnsignedValue(SegmentColumnId.FactKeyLow, entry.Row))
            .Select(entry => (entry.Segment.Published!.Name, entry.Row)),
    ];

    /// <summary>Every page a scope has, following each page's cursor, as (segment, row).</summary>
    private static (string Segment, int Row)[] Pages(Func<string?, SessionEvidencePage> read)
    {
        var records = new List<(string Segment, int Row)>();
        string? cursor = null;
        do
        {
            SessionEvidencePage page = read(cursor);
            Assert.False(page.RestartRequired, page.RestartReason);
            records.AddRange(page.Records.Select(record => (record.SegmentName, record.SegmentRow)));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return [.. records];
    }
}
