using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// An operation index written as its calls are paired (`contracts/operation-index-v1.md` §2, revision 882): the bytes of
/// one written from the calls a derivation pairs whole, whatever the segments' cut and overlap and however few paired
/// calls it holds before it spills them, so a finished session's checkpoint holds the calls in flight rather than every
/// call (§12).
/// </summary>
public sealed class StreamedOperationIndexTests
{
    [Theory(DisplayName = "§12: an operation index written as its calls are paired holds the bytes of one written from the calls paired whole, however its segments are cut and overlap and however few calls it holds before spilling them")]
    [InlineData(3, false)]
    [InlineData(20_261_103, false)]
    [InlineData(7, true)]
    [InlineData(882, true)]
    public void PairingAsItWritesWritesTheSameBytes(int seed, bool churn)
    {
        var random = new Random(seed);
        int spilled = 0;
        for (int trial = 0; trial < 6; trial++)
        {
            // A live capture's chunks, published as its generations are, so later segments hold records read earlier; in
            // some trials the rows come in no order at all, so every segment's readings overlap every other's.
            using var session = new TemporarySession();
            bool shuffled = trial % 3 == 2;
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in RandomRpcCaptures.Chunks(random, sparse: false, churn))
            {
                ObservationRowV1[] kept = [.. rows.Where(row => row.Mechanism != Mechanism.Alpc)];
                if (shuffled)
                {
                    random.Shuffle(kept);
                }

                Publish(session.Store, kept, rowsPerSegment: random.Next(1, kept.Length + 1),
                    fields: [.. fields.Where(field => field.Field != SourceField.AlpcMessageId)]);
            }

            Paired paired = PairedWhole(session.Store);
            byte[] whole = WrittenWhole(session.Store, paired);
            string spill = Path.Combine(session.Path, "spill");
            foreach (int heldBytes in new[] { RpcCallIndex.StreamedCallBytes, 1, 88 * random.Next(2, 9) })
            {
                Assert.Equal(whole, WrittenFromSegments(session.Store, paired, heldBytes: heldBytes, spillDirectory: spill));

                // The calls spilled are read back and the file holding them is gone with them.
                Assert.Empty(Directory.Exists(spill) ? Directory.GetFiles(spill) : []);
                spilled += Directory.Exists(spill) ? 1 : 0;
            }

            // Calls that would not fit are not kept, and the exchanges after them are weighed alike: the smallest index
            // holds its covered files, two flags and the exchanges' rule.
            StoreDependency last = (paired.Fields.Length > 0 ? paired.Fields[^1] : paired.Observations[^1]).Published!;
            int smallest = whole.AsSpan().IndexOf(Encoding.UTF8.GetBytes(last.Digest)) + 71 + 19;
            foreach (long bound in new[] { whole.Length - 1L, smallest, random.Next(smallest, whole.Length) })
            {
                Assert.Equal(
                    WrittenWhole(session.Store, paired, bound),
                    WrittenFromSegments(session.Store, paired, bound, heldBytes: 1, spillDirectory: spill));
            }
        }

        Assert.True(spilled > 0, "No trial spilled its calls.");
    }

    [Fact(DisplayName = "§12: an operation index written as its calls are paired takes records read at one instant by their locators, wherever their segments put them, and keeps every call state, interface and exchange")]
    public void PairingAsItWritesKeepsTheCanonicalOrder()
    {
        // Two calls start at one reading, the later-stored on the earlier stream; a call stops at the reading another
        // starts; a stop without its start, records without an activity id, an id reused before its stop, a call to no
        // interface, a call left open and one carrying its procedure and protocol, cut a record to a segment and stored in
        // order and backwards, so records read at one instant lie in segments either way round. The first call, on a
        // stream of its own, is still open at the end, so it is paired last and its stream is named first.
        Guid service = ServiceControlInterface;
        Guid other = Guid.Parse("12345778-1234-abcd-ef00-0123456789ab");
        ObservationRowV1 carrying = RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, Activity(7), other);
        ObservationRowV1[] rows =
        [
            Lifecycle(10, ObservationKind.Create, 400, 1),
            Lifecycle(11, ObservationKind.Create, 500, 2),
            RpcCall(50, ObservationKind.RequestStart, Direction.Outbound, 400, 4, Activity(9), service) with { RawStreamId = 5 },
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 5, Activity(1), service) with { RawStreamId = 2 },
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 6, Activity(2), other),
            RpcCall(150, ObservationKind.RequestEnd, Direction.Outbound, 400, 7, Activity(2), status: 0),
            RpcCall(150, ObservationKind.RequestStart, Direction.Inbound, 500, 8, Activity(3), service),
            RpcCall(160, ObservationKind.RequestEnd, Direction.Outbound, 400, 9, Activity(4), status: 5),
            RpcCall(170, ObservationKind.RequestStart, Direction.Outbound, 400, 10),
            RpcCall(180, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, status: 0),
            RpcCall(200, ObservationKind.RequestEnd, Direction.Outbound, 400, 12, Activity(1), status: 0) with { RawStreamId = 2 },
            RpcCall(210, ObservationKind.RequestStart, Direction.Outbound, 400, 13, Activity(5)),
            RpcCall(220, ObservationKind.RequestStart, Direction.Outbound, 400, 14, Activity(5), service),
            RpcCall(230, ObservationKind.RequestEnd, Direction.Outbound, 400, 15, Activity(5), status: 0),
            RpcCall(240, ObservationKind.RequestEnd, Direction.Outbound, 400, 16, Activity(5), status: 0),
            RpcCall(250, ObservationKind.RequestEnd, Direction.Inbound, 500, 17, Activity(3), status: 0),
            carrying,
            RpcCall(310, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, Activity(7), status: 0),
            RpcCall(400, ObservationKind.RequestStart, Direction.Outbound, 400, 40, Activity(8), service),
        ];
        (ObservationRowV1[] http, SourceFieldRowV1[] httpFields) = HttpExchangeRows(stream: 3, laterStream: 4);
        foreach (bool backwards in new[] { false, true })
        {
            using var session = new TemporarySession();
            ObservationRowV1[] stored = [.. rows.Concat(http)];
            Publish(session.Store, backwards ? [.. stored.Reverse()] : stored, rowsPerSegment: 1, fields:
            [
                Field(carrying, SourceField.RpcProcedureNumber, 4),
                Field(carrying, SourceField.RpcProtocolSequence, 10),
                .. httpFields,
            ]);

            Paired paired = PairedWhole(session.Store);
            byte[] whole = WrittenWhole(session.Store, paired);
            Assert.Equal(whole, WrittenFromSegments(session.Store, paired));
            Assert.Equal(whole, WrittenFromSegments(session.Store, paired, heldBytes: 1, spillDirectory: Path.Combine(session.Path, "spill")));

            // What was written is what a derivation pairs: every state, both interfaces, the fields and the exchanges.
            OperationIndex read = OperationIndex.Read(WrittenFromSegments(session.Store, paired), session.Store.SessionId, TestClock,
                paired.Calls.Processes);
            Assert.Equal(Enum.GetValues<RpcCallState>().Order(), read.Calls!.Outcomes().Select(call => call.State).Distinct().Order());
            Assert.Equal(2, read.Calls.Groups.Select(group => group.Interface).OfType<Guid>().Distinct().Count());
            Assert.Contains(read.Calls.Groups.SelectMany(group => read.Calls.CallsOf(group, paired.Observations, 0, 100)),
                call => call is { Procedure: 4, Protocol: 10 });
            Assert.Equal((0L, 0L), (read.Peers!.AlpcSends, read.Peers.AlpcReceives));
            Assert.NotEmpty(read.Exchanges!.Groups);
        }
    }

    [Fact(DisplayName = "§12: segments holding an ALPC send or receive are not written as their calls are paired: nothing is written, for calls paired whole to be")]
    public void AlpcIsLeftToCallsPairedWhole()
    {
        (ObservationRowV1[] linked, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        using var session = new TemporarySession();
        Publish(session.Store, linked, rowsPerSegment: 4, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = IncrementalDerivationTests.SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
        using var written = new MemoryStream();
        Assert.Null(OperationIndex.WriteFromSegments(written, session.Store.SessionId, session.Store.Current!.Generation,
            [.. observations.Select(segment => segment.Published!)], [.. fieldSegments.Select(segment => segment.Published!)],
            observations, fieldSegments, TestClock, HttpExchangeIndex.Derive(observations, fieldSegments, processes)));
        Assert.Equal(0, written.Length);
    }

    [Fact(DisplayName = "I2: a call's procedure named twice refuses an operation index written as its calls are paired, as it refuses a derivation, and segments that are not the files named are refused")]
    public void ADuplicatedCallFieldIsRefusedAsItIsPaired()
    {
        ObservationRowV1 start = RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 2, Activity(1), ServiceControlInterface);
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(10, ObservationKind.Create, 400, 1), start], rowsPerSegment: 1, fields:
        [
            Field(start, SourceField.RpcProcedureNumber, 0) with { Value = null, Availability = FieldAvailability.EventLost },
            Field(start, SourceField.RpcProcedureNumber, 7),
        ]);

        (SegmentReaderV1[] duplicated, SegmentReaderV1[] duplicates) = IncrementalDerivationTests.SegmentsOf(session.Store);
        Assert.Contains(
            "more than one source-field segment",
            Assert.Throws<InvalidDataException>(() => WrittenFromSegments(session.Store, new(duplicated, duplicates, null!, null!,
                HttpExchangeIndex.Derive(duplicated, [], ProcessInstanceIndex.Derive(duplicated, TestClock, []))))).Message,
            StringComparison.Ordinal);

        using var plain = new TemporarySession();
        Publish(plain.Store, [Lifecycle(10, ObservationKind.Create, 400, 1), start], rowsPerSegment: 1);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(plain.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        HttpExchangeIndex exchanges = HttpExchangeIndex.Derive(observations, fields, processes);
        StoreDependency[] named = [.. observations.Select(segment => segment.Published!)];
        Assert.Throws<ArgumentException>(() => OperationIndex.WriteFromSegments(new MemoryStream(), plain.Store.SessionId,
            plain.Store.Current!.Generation, [.. named.Reverse()], [], observations, fields, TestClock, exchanges));
        Assert.Throws<ArgumentException>(() => OperationIndex.WriteFromSegments(new MemoryStream(), plain.Store.SessionId,
            plain.Store.Current!.Generation, named, [], [.. observations.Reverse()], fields, TestClock, exchanges));
    }

    /// <summary>The current generation's segments, and its calls, their other ends and its exchanges, derived whole.</summary>
    private static Paired PairedWhole(SessionStore store)
    {
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        RpcCallIndex calls = RpcCallIndex.Derive(observations, fields, processes, TestClock);
        return new(observations, fields, calls, RpcPeerIndex.Derive(calls, observations, fields),
            HttpExchangeIndex.Derive(observations, fields, processes));
    }

    /// <summary>The operation index of the session's current generation, from its calls paired whole.</summary>
    private static byte[] WrittenWhole(SessionStore store, Paired paired, long maximumBytes = OperationIndex.MaximumBytes)
    {
        using var written = new MemoryStream();
        long length = OperationIndex.Write(written, store.SessionId, store.Current!.Generation,
            [.. paired.Observations.Select(segment => segment.Published!)], [.. paired.Fields.Select(segment => segment.Published!)],
            paired.Calls, paired.Peers, paired.Exchanges, maximumBytes);
        Assert.Equal(written.Length, length);
        return written.ToArray();
    }

    /// <summary>The operation index of the session's current generation, its calls paired as it is written.</summary>
    private static byte[] WrittenFromSegments(
        SessionStore store,
        Paired paired,
        long maximumBytes = OperationIndex.MaximumBytes,
        int heldBytes = RpcCallIndex.StreamedCallBytes,
        string? spillDirectory = null)
    {
        using var written = new MemoryStream();
        long? length = OperationIndex.WriteFromSegments(written, store.SessionId, store.Current!.Generation,
            [.. paired.Observations.Select(segment => segment.Published!)], [.. paired.Fields.Select(segment => segment.Published!)],
            paired.Observations, paired.Fields, TestClock, paired.Exchanges, maximumBytes, heldBytes, spillDirectory);
        Assert.Equal(written.Length, length);
        return written.ToArray();
    }

    /// <summary>A generation's segments, and its calls, their other ends and its exchanges, derived whole.</summary>
    private sealed record Paired(
        SegmentReaderV1[] Observations,
        SegmentReaderV1[] Fields,
        RpcCallIndex Calls,
        RpcPeerIndex Peers,
        HttpExchangeIndex Exchanges);

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 7);
}
