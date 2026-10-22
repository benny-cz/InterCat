using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// What a one-sided connection's records sent and received is counted as its relations read them (revision 736): its
/// sends and receives, the bytes they state and how many state none. A connection to another host is then listed, and
/// Other hosts ranked by bytes, without a second read of its rows, so the counts must be exactly what a pass over the rows
/// counts, however the relations were made: derived whole, extended chunk by chunk, or read back from a checkpoint.
/// </summary>
public sealed class ConnectionTransfersTests
{
    private static readonly Guid Session = Guid.Parse("7a7e5f3e-0000-4000-8000-000000000736");

    [Theory(DisplayName = "I14: each one-sided connection's sends, receives and bytes are what a pass over its rows counts, whether its relations were derived whole, extended chunk by chunk or read back from a checkpoint")]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(20_261_022)]
    public void TransfersAreWhatTheRowsCount(int seed)
    {
        var random = new Random(seed);
        int compared = 0;
        int unsized = 0;
        int extensions = 0;
        for (int trial = 0; trial < 8; trial++)
        {
            // Each chunk is published into one session as a live capture publishes it, so every generation's segments begin
            // with the ones before it.
            using var session = new TemporarySession();
            ProcessInstanceIndex? processes = null;
            TransportRelationIndex? relations = null;
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in RandomCaptures.Chunks(random))
            {
                Publish(session.Store, [.. rows.Select(row => Sized(row, random))], rowsPerSegment: random.Next(1, rows.Length + 1),
                    fields: fieldRows);
                (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
                ProcessInstanceIndex fullProcesses = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                TransportRelationIndex full = TransportRelationIndex.Derive(observations, fullProcesses);
                ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, fullProcesses);
                Assert.True(full.TransfersKept);
                (int connections, int withoutSize) = AssertTransfersAreTheRows(full, observations);
                compared += connections;
                unsized += withoutSize;

                // Read back from a checkpoint, and extended from the chunk before, they are the same counts.
                using (var written = new MemoryStream())
                {
                    _ = DerivationCheckpoint.Write(written, Session, 2, fullProcesses, full, activity);
                    TransportRelationIndex read = DerivationCheckpoint.Read(written.ToArray(), Session, TestClock).Relations;
                    Assert.True(read.TransfersKept);
                    AssertSameTransfers(full, read);
                }

                if (processes is not null && relations is not null
                    && processes.Extend(observations, TestClock, fields) is { } next
                    && relations.Extend(observations, next) is { } extended)
                {
                    Assert.True(extended.TransfersKept);
                    AssertSameTransfers(TransportRelationIndex.Derive(observations, next), extended);
                    extensions++;
                    (processes, relations) = (next, extended);
                }
                else
                {
                    (processes, relations) = (fullProcesses, full);
                }
            }
        }

        Assert.True(compared > 20, $"Only {compared} connections were compared.");
        Assert.True(unsized > 0, "No connection's transfer stated no size.");
        Assert.True(extensions > 5, $"Only {extensions} generations extended the one before.");
    }

    [Fact(DisplayName = "I14: a connection whose sent or received bytes pass what a sum holds gives no counts, read back from a checkpoint alike, and its rows refuse them")]
    public void SumsPastWhatOneHoldsAreNotGiven()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, long.MaxValue / 2 + 1, 700, 1).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, long.MaxValue / 2 + 1, 700, 2).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 700, 3).Between("192.168.1.10:52001", "203.0.113.2:443"),
        ]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        TransportConnection overflowing = relations.OneSided.Single(connection => connection.RemoteEndpoint.StartsWith("203.0.113.1", StringComparison.Ordinal));
        TransportConnection other = relations.OneSided.Single(connection => connection != overflowing);
        Assert.Null(relations.TransfersOf(overflowing));
        Assert.Equal(new ConnectionTransfers(0, 0, 0, 1, 64, 0), relations.TransfersOf(other));

        using var written = new MemoryStream();
        _ = DerivationCheckpoint.Write(written, Session, 2, processes, relations, ProcessActivityIndex.Derive(observations, processes));
        TransportRelationIndex read = DerivationCheckpoint.Read(written.ToArray(), Session, TestClock).Relations;
        Assert.Null(read.TransfersOf(overflowing));
        Assert.Equal(new ConnectionTransfers(0, 0, 0, 1, 64, 0), read.TransfersOf(other));

        // A connection that is not one of these relations' has none either.
        Assert.Null(relations.TransfersOf(other with { Channel = other.Channel + 100 }));
        Assert.Null(relations.TransfersOf(other with { Records = other.Records + 1 }));
    }

    [Fact(DisplayName = "I14: relations extended over a live generation's added segments go on refusing a sum the relations they extend refused, and count nothing where those counted nothing")]
    public void ExtendedRelationsKeepWhatTheyExtendKept()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, long.MaxValue / 2 + 1, 700, 1).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, long.MaxValue / 2 + 1, 700, 2).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 700, 3).Between("192.168.1.10:52001", "203.0.113.2:443"),
        ]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        using var written = new MemoryStream();
        _ = DerivationCheckpoint.Write(written, Session, 2, processes, relations, ProcessActivityIndex.Derive(observations, processes));
        DerivationCheckpoint earlier = DerivationCheckpoint.Read(EarlierCheckpoints.WithoutTransfers(written.ToArray()), Session, TestClock);
        Assert.False(earlier.Relations.TransfersKept);

        // The next generation adds a send to the connection whose sends passed a sum, and a receive to the other.
        Publish(session.Store,
        [
            Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 1, 700, 4).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(14, ObservationKind.Receive, AccountingSide.ReceiveSide, 36, 700, 5).Between("192.168.1.10:52001", "203.0.113.2:443"),
        ]);
        (SegmentReaderV1[] grown, SegmentReaderV1[] grownFields) = SegmentsOf(session.Store);
        Assert.True(grown.Length > observations.Length);
        ProcessInstanceIndex next = processes.Extend(grown, TestClock, grownFields)
            ?? throw new Xunit.Sdk.XunitException("Every segment read before is still offered, so the instances must extend.");
        TransportRelationIndex extended = relations.Extend(grown, next)
            ?? throw new Xunit.Sdk.XunitException("No record added changes a decision made, so the relations must extend.");
        Assert.True(extended.TransfersKept);
        TransportConnection overflowing = extended.OneSided.Single(connection => connection.RemoteEndpoint.StartsWith("203.0.113.1", StringComparison.Ordinal));
        Assert.Null(extended.TransfersOf(overflowing));
        Assert.Equal(new ConnectionTransfers(0, 0, 0, 2, 100, 0), extended.TransfersOf(extended.OneSided.Single(connection => connection != overflowing)));
        AssertSameTransfers(TransportRelationIndex.Derive(grown, next), extended);

        // Relations read back from format 1.3 know nothing of what the segments before sent, nor do they once extended.
        ProcessInstanceIndex fromEarlier = earlier.Processes.Extend(grown, TestClock, grownFields)
            ?? throw new Xunit.Sdk.XunitException("Every segment the checkpoint covers is still offered, so the instances must extend.");
        TransportRelationIndex extendedFromEarlier = earlier.Relations.Extend(grown, fromEarlier)
            ?? throw new Xunit.Sdk.XunitException("No record added changes a decision made, so the relations must extend.");
        Assert.Equal(extended.OneSided, extendedFromEarlier.OneSided);
        Assert.False(extendedFromEarlier.TransfersKept);
        Assert.All(extendedFromEarlier.OneSided, connection => Assert.Null(extendedFromEarlier.TransfersOf(connection)));
    }

    [Fact(DisplayName = "I14: a checkpoint's transfers that contradict their incarnation's records, or are not one entry per incarnation, are refused")]
    public void ContradictoryTransfersAreRefused()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 700, 1).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 700, 2).Between("192.168.1.10:52000", "203.0.113.1:443"),
        ]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        Assert.Equal(new ConnectionTransfers(1, 100, 0, 1, 0, 1), relations.TransfersOf(Assert.Single(relations.OneSided)));
        using var written = new MemoryStream();
        _ = DerivationCheckpoint.Write(written, Session, 2, processes, relations, ProcessActivityIndex.Derive(observations, processes));
        byte[] bytes = EarlierCheckpoints.WithoutOwners(written.ToArray());

        // In format 1.4 the one incarnation's entry is the last 49 bytes before the count: a refusal flag, then its sends, the
        // bytes they state, how many state none, and the same of its receives.
        int entry = bytes.Length - 4 - 49;
        foreach ((int offset, long value) in new[]
        {
            (1, -1L),     // fewer sends than none
            (1, 2L),      // more sends and receives than the incarnation holds records
            (17, 2L),     // more sends stating no size than sends
            (25, -1L),    // fewer receives than none
            (41, 2L),     // more receives stating no size than receives
        })
        {
            byte[] changed = [.. bytes];
            BitConverter.TryWriteBytes(changed.AsSpan(entry + offset, 8), value);
            Assert.Contains("sends and receives contradict", Assert.Throws<InvalidDataException>(
                () => DerivationCheckpoint.Read(changed, Session, TestClock)).Message, StringComparison.Ordinal);
        }

        // A sum refused while it still states bytes, and a count of entries the relations do not hold, are refused.
        byte[] refused = [.. bytes];
        refused[entry] = 1;
        Assert.Contains("sends and receives contradict", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(refused, Session, TestClock)).Message, StringComparison.Ordinal);
        byte[] counted = [.. bytes];
        BitConverter.TryWriteBytes(counted.AsSpan(bytes.Length - 4, 4), 2u);
        Assert.Contains("not one entry per incarnation", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(counted, Session, TestClock)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A transfer of the random capture with a size of its own, or none at all now and then, so sums differ connection by
    /// connection and some sends and receives state no size.
    /// </summary>
    private static ObservationRowV1 Sized(ObservationRowV1 row, Random random) =>
        row.Kind is not (ObservationKind.Send or ObservationKind.Receive) ? row
        : random.Next(6) == 0 ? row with
        {
            ByteValue = null,
            ByteAvailability = FieldAvailability.NotExposed,
            MeasurementQuality = QualityLevel.UnknownQuality,
        }
        : row with { ByteValue = random.Next(1, 5_000) };

    /// <summary>
    /// Asserts each one-sided connection's counts are its rows': those whose channel is its own. Returns how many connections
    /// it compared, and how many of their transfers stated no size.
    /// </summary>
    private static (int Connections, int Unsized) AssertTransfersAreTheRows(
        TransportRelationIndex relations, IReadOnlyList<SegmentReaderV1> segments)
    {
        var expected = relations.OneSided.ToDictionary(connection => connection.Channel, _ => new long[6]);
        foreach (SegmentReaderV1 segment in segments)
        {
            ChannelBinding[] channels = relations.BindingsOf(segment).Channels;
            SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
            SegmentColumnSlice sizes = segment.Slice(SegmentColumnId.ByteValue);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (!channels[row].IsKnown || !expected.TryGetValue(channels[row].Channel, out long[]? counts))
                {
                    continue;
                }

                var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;
                int at = kind == ObservationKind.Send ? 0 : kind == ObservationKind.Receive ? 3 : -1;
                if (at < 0)
                {
                    continue;
                }

                counts[at]++;
                if (sizes.SignedAt(row) is { } size) counts[at + 1] += size;
                else counts[at + 2]++;
            }
        }

        int unsized = 0;
        foreach (TransportConnection connection in relations.OneSided)
        {
            long[] counts = expected[connection.Channel];
            Assert.Equal(new ConnectionTransfers(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]),
                relations.TransfersOf(connection));
            unsized += (int)(counts[2] + counts[5]);
        }

        return (relations.OneSided.Count, unsized);
    }

    private static void AssertSameTransfers(TransportRelationIndex expected, TransportRelationIndex actual)
    {
        Assert.Equal(expected.OneSided, actual.OneSided);
        Assert.Equal(expected.OneSided.Select(expected.TransfersOf), actual.OneSided.Select(actual.TransfersOf));
    }
}
