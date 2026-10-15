using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A live capture publishes a generation per chunk. Its RPC calls and their other ends are extended from the previous
/// generation's rather than paired and followed again from every segment, and an extension must equal a full derivation
/// of the same segments exactly, or decline so a full derivation answers (I14, `operations-v1` §4 and §5c).
/// </summary>
public sealed class IncrementalRpcTests
{
    [Theory(DisplayName = "I14: RPC calls and their other ends extended chunk by chunk equal a derivation of every chunk at once")]
    [InlineData(3, false)]
    [InlineData(29, false)]
    [InlineData(20_261_015, false)]
    [InlineData(11, true)]
    [InlineData(20_261_016, true)]
    public void ExtendingCallsEqualsPairingAgain(int seed, bool sparse)
    {
        var random = new Random(seed);
        int extended = 0;
        int declined = 0;
        int followed = 0;
        for (int trial = 0; trial < 12; trial++)
        {
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomRpcCaptures.Chunks(random, sparse);
            var sessions = new List<TemporarySession>();
            try
            {
                var observations = new List<SegmentReaderV1>();
                var fields = new List<SegmentReaderV1>();
                RpcCallIndex? calls = null;
                RpcPeerIndex? peers = null;
                foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in chunks)
                {
                    var session = new TemporarySession();
                    sessions.Add(session);
                    Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fieldRows);
                    (SegmentReaderV1[] chunkObservations, SegmentReaderV1[] chunkFields) = IncrementalDerivationTests.SegmentsOf(session.Store);
                    observations.AddRange(chunkObservations);
                    fields.AddRange(chunkFields);

                    ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                    RpcCallIndex fullCalls = RpcCallIndex.Derive(observations, fields, processes, TestClock);
                    RpcPeerIndex fullPeers = RpcPeerIndex.Derive(fullCalls, observations, fields);
                    if (calls is null)
                    {
                        (calls, peers) = (fullCalls, fullPeers);
                        continue;
                    }

                    RpcCallIndex? next = calls.Extend(observations, fields, processes);
                    if (next is null)
                    {
                        declined++;
                        next = RpcCallIndex.Derive(observations, fields, processes, TestClock);
                    }
                    else
                    {
                        extended++;
                    }

                    AssertSameCalls(fullCalls, next, observations);
                    RpcPeerIndex nextPeers = peers!.Extend(next, observations, fields) ?? RpcPeerIndex.Derive(next, observations, fields);
                    followed += nextPeers.Calls == next ? 1 : 0;
                    AssertSamePeers(fullPeers, fullCalls, nextPeers, next, observations);
                    (calls, peers) = (next, nextPeers);
                }
            }
            finally
            {
                foreach (TemporarySession session in sessions)
                {
                    session.Dispose();
                }
            }
        }

        // Most chunks extend; in a sparse capture a record delivered after the late window declines, for a full pairing.
        Assert.True(extended > 3 * declined, $"{extended} extensions and {declined} declined.");
        Assert.True(sparse ? declined > 0 : declined == 0, $"{declined} declined.");
        Assert.True(followed > 0);
    }

    [Fact(DisplayName = "I14: an RPC extension declines, for a full derivation, whatever could change a call or a link already made")]
    public void RpcExtensionDeclinesWhatItCannotAdd()
    {
        // A client call and the call that served it, closed, then a call that reads twenty seconds later.
        ObservationRowV1 start = RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 1, Activity(1), ServiceControlInterface);
        ObservationRowV1 send = Alpc(102, ObservationKind.Send, 400, 401, 2);
        ObservationRowV1 receive = Alpc(103, ObservationKind.Receive, 1_960, 1_961, 3);
        ObservationRowV1 served = RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 4, Activity(2), ServiceControlInterface);
        ObservationRowV1[] first =
        [
            Lifecycle(1, ObservationKind.Inventory, 400, 10),
            Lifecycle(2, ObservationKind.Inventory, 1_960, 11),
            start,
            send,
            receive,
            served,
            RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 5, Activity(2), status: 0),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 6, Activity(1), status: 0),
            RpcCall(200_000_000, ObservationKind.RequestStart, Direction.Outbound, 400, 7, Activity(3), ServiceControlInterface),
        ];
        SourceFieldRowV1[] firstFields =
        [
            Field(start, SourceField.RpcProcedureNumber, 7),
            Field(served, SourceField.RpcProcedureNumber, 7),
            Field(send, SourceField.AlpcMessageId, 21),
            Field(receive, SourceField.AlpcMessageId, 21),
        ];

        // Another stop of the first call's key, read before the window behind the latest record and before anything kept of
        // the key: it could make the call ambiguous, so the calls decline.
        Assert.Null(Extended(first, firstFields,
            [RpcCall(115, ObservationKind.RequestEnd, Direction.Outbound, 400, 8, Activity(1), status: 0)], []).Calls);

        // A key kept from its open call on, whose earlier call closed long before: a stop read between that call's records
        // could close it rather than stand alone, so it declines too, though the key is kept.
        Guid reused = Activity(5);
        ObservationRowV1[] closedThenOpen =
        [
            Lifecycle(1, ObservationKind.Inventory, 400, 20),
            RpcCall(10, ObservationKind.RequestStart, Direction.Outbound, 400, 21, reused, ServiceControlInterface),
            RpcCall(20, ObservationKind.RequestEnd, Direction.Outbound, 400, 22, reused, status: 0),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 23, reused, ServiceControlInterface),
            RpcCall(200_000_000, ObservationKind.RequestStart, Direction.Outbound, 400, 24, Activity(6), ServiceControlInterface),
        ];
        Assert.Null(Extended(closedThenOpen, [],
            [RpcCall(15, ObservationKind.RequestEnd, Direction.Outbound, 400, 25, reused, status: 0)], []).Calls);

        // After the records kept of it, it extends: the open call's stop closes it.
        (RpcCallIndex? reopened, _) = Extended(closedThenOpen, [],
            [RpcCall(150, ObservationKind.RequestEnd, Direction.Outbound, 400, 26, reused, status: 0)], []);
        Assert.Equal((2L, 0L), (reopened!.Totals.Completed, reopened.Totals.OpenAtCaptureEnd - 1));

        // Within the window, or of a key kept, it extends: the open call's stop closes it, and a call with no activity id
        // read long before stands on its own.
        (RpcCallIndex? calls, _) = Extended(first, firstFields,
            [
                RpcCall(200_000_050, ObservationKind.RequestEnd, Direction.Outbound, 400, 9, Activity(3), status: 0),
                RpcCall(50, ObservationKind.RequestStart, Direction.Outbound, 400, 12),
            ], []);
        Assert.NotNull(calls);
        Assert.Equal((4L, 3L, 1L, 0L), (calls.Totals.Calls, calls.Totals.Completed, calls.Totals.NoActivityId, calls.Totals.OpenAtCaptureEnd));

        // A source field of a call already read, published in a later chunk, is no field of an added record: a full
        // derivation reads it.
        Assert.Null(Extended(first, firstFields, [Lifecycle(300, ObservationKind.Create, 500, 13)],
            [Field(start, SourceField.RpcProtocolSequence, 3)]).Calls);

        // A message id of an ALPC record already read declines the other ends alike.
        ObservationRowV1 later = Alpc(200_000_010, ObservationKind.Send, 400, 401, 14);
        Assert.Null(Extended(first, firstFields, [later], [Field(later, SourceField.AlpcMessageId, 22), Field(send, SourceField.AlpcMessageId, 23)]).Peers);

        // Segments a later generation no longer names - compacted into another, or released - leave nothing to extend from.
        using var before = new TemporarySession();
        Publish(before.Store, first, fields: firstFields);
        using var other = new TemporarySession();
        Publish(other.Store, [RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 15, Activity(4), ServiceControlInterface)]);
        (SegmentReaderV1[] earlier, SegmentReaderV1[] earlierFields) = IncrementalDerivationTests.SegmentsOf(before.Store);
        (SegmentReaderV1[] replaced, SegmentReaderV1[] replacedFields) = IncrementalDerivationTests.SegmentsOf(other.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(earlier, TestClock, earlierFields);
        RpcCallIndex derived = RpcCallIndex.Derive(earlier, earlierFields, processes, TestClock);
        Assert.Null(derived.Extend(replaced, replacedFields, ProcessInstanceIndex.Derive(replaced, TestClock, replacedFields)));

        // Another session's segments, named as these were, are other files: a name alone tells nothing apart, so neither
        // the calls nor their other ends extend over them.
        using var twin = new TemporarySession();
        Publish(twin.Store, [.. first, Lifecycle(3, ObservationKind.Inventory, 500, 16)], fields: firstFields);
        (SegmentReaderV1[] twinSegments, SegmentReaderV1[] twinFields) = IncrementalDerivationTests.SegmentsOf(twin.Store);
        Assert.Equal(earlier.Select(segment => segment.Published!.Name), twinSegments.Select(segment => segment.Published!.Name));
        Assert.Equal(earlierFields.Select(segment => segment.Published!.Name), twinFields.Select(segment => segment.Published!.Name));
        ProcessInstanceIndex twinProcesses = ProcessInstanceIndex.Derive(twinSegments, TestClock, twinFields);
        Assert.Null(derived.Extend(twinSegments, twinFields, twinProcesses));
        Assert.Null(RpcPeerIndex.Derive(derived, earlier, earlierFields)
            .Extend(RpcCallIndex.Derive(twinSegments, twinFields, twinProcesses, TestClock), twinSegments, twinFields));

        // So too where neither session carries a source field: the segments alone tell them apart.
        using var bare = new TemporarySession();
        Publish(bare.Store, first);
        using var bareTwin = new TemporarySession();
        Publish(bareTwin.Store, [.. first, Lifecycle(3, ObservationKind.Inventory, 500, 16)]);
        (SegmentReaderV1[] bareSegments, _) = IncrementalDerivationTests.SegmentsOf(bare.Store);
        (SegmentReaderV1[] bareTwinSegments, _) = IncrementalDerivationTests.SegmentsOf(bareTwin.Store);
        RpcCallIndex bareCalls = RpcCallIndex.Derive(bareSegments, [], ProcessInstanceIndex.Derive(bareSegments, TestClock, []), TestClock);
        RpcCallIndex bareTwinCalls = RpcCallIndex.Derive(
            bareTwinSegments, [], ProcessInstanceIndex.Derive(bareTwinSegments, TestClock, []), TestClock);
        Assert.Null(RpcPeerIndex.Derive(bareCalls, bareSegments, []).Extend(bareTwinCalls, bareTwinSegments, []));
    }

    [Fact(DisplayName = "I14: an RPC peer index gives its ALPC records to the first extension made from it, and a second extends from nothing")]
    public void APeerIndexIsExtendedOnce()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        using var first = new TemporarySession();
        Publish(first.Store, rows, fields: fields);
        using var second = new TemporarySession();
        Publish(second.Store, [Alpc(400, ObservationKind.Send, 400, 401, 90)]);
        (SegmentReaderV1[] earlier, SegmentReaderV1[] earlierFields) = IncrementalDerivationTests.SegmentsOf(first.Store);
        (SegmentReaderV1[] added, SegmentReaderV1[] addedFields) = IncrementalDerivationTests.SegmentsOf(second.Store);
        SegmentReaderV1[] all = [.. earlier, .. added];
        SegmentReaderV1[] allFields = [.. earlierFields, .. addedFields];

        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(all, TestClock, allFields);
        RpcCallIndex calls = RpcCallIndex.Derive(earlier, earlierFields, ProcessInstanceIndex.Derive(earlier, TestClock, earlierFields), TestClock);
        RpcPeerIndex peers = RpcPeerIndex.Derive(calls, earlier, earlierFields);
        RpcCallIndex next = calls.Extend(all, allFields, processes)!;
        RpcPeerIndex extended = peers.Extend(next, all, allFields)!;
        Assert.Equal((3L, 2L, 2), (extended.AlpcSends, extended.AlpcReceives, extended.Links().Count()));

        // The records it kept now live in the extension; another extension from it reads them in full instead.
        Assert.Null(peers.Extend(next, all, allFields));

        // An extension that declines - here for a message id of a record already read - hands the records back, so the
        // next one extends.
        ObservationRowV1 late = Alpc(500, ObservationKind.Send, 400, 401, 91);
        ObservationRowV1 earlierSend = rows.First(row => row.Mechanism == Mechanism.Alpc);
        using var orphaned = new TemporarySession();
        Publish(orphaned.Store, [late], fields: [Field(late, SourceField.AlpcMessageId, 41), Field(earlierSend, SourceField.AlpcMessageId, 42)]);
        using var sound = new TemporarySession();
        Publish(sound.Store, [late], fields: [Field(late, SourceField.AlpcMessageId, 41)]);
        Assert.Null(ExtendedBy(extended, next, all, allFields, orphaned.Store));
        Assert.NotNull(ExtendedBy(extended, next, all, allFields, sound.Store));
    }

    /// <summary>Other ends extended, with their calls, by the segments a further chunk published.</summary>
    private static RpcPeerIndex? ExtendedBy(
        RpcPeerIndex peers, RpcCallIndex calls, SegmentReaderV1[] segments, SegmentReaderV1[] fields, SessionStore added)
    {
        (SegmentReaderV1[] more, SegmentReaderV1[] moreFields) = IncrementalDerivationTests.SegmentsOf(added);
        SegmentReaderV1[] all = [.. segments, .. more];
        SegmentReaderV1[] allFields = [.. fields, .. moreFields];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(all, TestClock, allFields);
        RpcCallIndex extended = calls.Extend(all, allFields, processes) ?? RpcCallIndex.Derive(all, allFields, processes, TestClock);
        return peers.Extend(extended, all, allFields);
    }

    /// <summary>The calls and their other ends of a first chunk, extended by a second.</summary>
    private static (RpcCallIndex? Calls, RpcPeerIndex? Peers) Extended(
        ObservationRowV1[] first, SourceFieldRowV1[] firstFields, ObservationRowV1[] added, SourceFieldRowV1[] addedFields)
    {
        using var before = new TemporarySession();
        Publish(before.Store, first, fields: firstFields);
        using var after = new TemporarySession();
        Publish(after.Store, added, fields: addedFields);
        (SegmentReaderV1[] earlier, SegmentReaderV1[] earlierFields) = IncrementalDerivationTests.SegmentsOf(before.Store);
        (SegmentReaderV1[] later, SegmentReaderV1[] laterFields) = IncrementalDerivationTests.SegmentsOf(after.Store);
        SegmentReaderV1[] all = [.. earlier, .. later];
        SegmentReaderV1[] allFields = [.. earlierFields, .. laterFields];
        RpcCallIndex calls = RpcCallIndex.Derive(earlier, earlierFields, ProcessInstanceIndex.Derive(earlier, TestClock, earlierFields), TestClock);
        RpcPeerIndex peers = RpcPeerIndex.Derive(calls, earlier, earlierFields);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(all, TestClock, allFields);
        RpcCallIndex? next = calls.Extend(all, allFields, processes);
        RpcPeerIndex? followed = peers.Extend(next ?? RpcCallIndex.Derive(all, allFields, processes, TestClock), all, allFields);
        return (next, followed);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 7);

    private static void AssertSameCalls(RpcCallIndex expected, RpcCallIndex actual, IReadOnlyList<SegmentReaderV1> segments)
    {
        Assert.Equal((expected.CallRecords, expected.OtherRpcRecords), (actual.CallRecords, actual.OtherRpcRecords));
        Assert.Equal(expected.SegmentNames, actual.SegmentNames);
        Assert.Equal(expected.Totals, actual.Totals);
        Assert.Equal(expected.Groups, actual.Groups);
        Assert.Equal(expected.Outcomes(), actual.Outcomes());
        for (int group = 0; group < expected.Groups.Count; group++)
        {
            Assert.Equal(
                expected.CallsOf(expected.Groups[group], segments, 0, int.MaxValue),
                actual.CallsOf(actual.Groups[group], segments, 0, int.MaxValue));
            Assert.Equal(expected.SpansOf(expected.Groups[group]), actual.SpansOf(actual.Groups[group]));
        }
    }

    private static void AssertSamePeers(
        RpcPeerIndex expected, RpcCallIndex expectedCalls, RpcPeerIndex actual, RpcCallIndex actualCalls, IReadOnlyList<SegmentReaderV1> segments)
    {
        Assert.Equal((expected.AlpcSends, expected.AlpcReceives), (actual.AlpcSends, actual.AlpcReceives));
        Assert.Equal(expected.Links(), actual.Links());
        for (int group = 0; group < expectedCalls.Groups.Count; group++)
        {
            RpcCallGroup one = expectedCalls.Groups[group];
            RpcCallGroup other = actualCalls.Groups[group];
            RpcPeerCounts counts = expected.CountsOf(one);
            RpcPeerCounts actualCounts = actual.CountsOf(other);
            Assert.Equal((counts.Calls, counts.Served), (actualCounts.Calls, actualCounts.Served));
            Assert.Equal(counts.Unresolved.OrderBy(entry => entry.Key), actualCounts.Unresolved.OrderBy(entry => entry.Key));
            Assert.Equal(expected.PeersOf(one), actual.PeersOf(other));
            for (int position = 0; position < one.Counts.Calls; position++)
            {
                Assert.Equal(expected.PeerOf(one, position, segments), actual.PeerOf(other, position, segments));
            }
        }
    }
}

/// <summary>
/// Random live captures of RPC calls and the ALPC messages that link them, cut into chunks as a live capture publishes
/// them: most records in the chunk of their reading, some delivered a chunk or two late, and in a sparse capture some
/// delivered after the late window behind the latest record (<see cref="RpcCallIndex.LateWindowNanoseconds"/>).
/// </summary>
internal static class RandomRpcCaptures
{
    private const int Server = 1_960;
    private static readonly int[] Clients = [400, 410, 420];
    private static readonly Guid[] Interfaces =
    [
        ServiceControlInterface,
        Guid.Parse("12345778-1234-abcd-ef00-0123456789ac"),
        Guid.Parse("e1af8308-5d1f-11c9-91a4-08002b14a0fa"),
    ];

    public static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> Chunks(Random random, bool sparse)
    {
        ulong ordinal = 0;
        long message = 1;
        int activity = 0;
        var records = new List<(ObservationRowV1 Row, List<SourceFieldRowV1> Fields)>
        {
            (Lifecycle(1, ObservationKind.Inventory, Server, ++ordinal), []),
        };
        foreach (int client in Clients)
        {
            records.Add((Lifecycle(2, ObservationKind.Create, client, ++ordinal), []));
        }

        // A dense capture places calls tens of ticks apart; a sparse one three seconds apart, so a late window holds a few.
        long spacing = sparse ? 30_000_000 : 40;
        var lastActivity = new Dictionary<int, Guid>();
        int calls = random.Next(8, 30);
        for (int call = 0; call < calls; call++)
        {
            long at = 100 + (call * spacing) + random.Next(0, 30);
            int client = Clients[random.Next(Clients.Length)];
            Guid rpcInterface = Interfaces[random.Next(Interfaces.Length)];
            long procedure = random.Next(0, 3);

            // An activity id is mostly new; sometimes the client's last one again, which pairs a new call or makes a run
            // ambiguous when the last one is still open; sometimes absent.
            Guid clientActivity = random.NextDouble() < 0.1 && lastActivity.TryGetValue(client, out Guid reused)
                ? reused
                : NewActivity(++activity);
            lastActivity[client] = clientActivity;
            Guid? clientId = random.NextDouble() < 0.05 ? null : clientActivity;
            Guid serverActivity = NewActivity(++activity);

            if (random.NextDouble() > 0.08)
            {
                ObservationRowV1 start = RpcCall(at, ObservationKind.RequestStart, Direction.Outbound, client, ++ordinal, clientId, rpcInterface);
                records.Add((start, random.NextDouble() < 0.95 ? [Field(start, SourceField.RpcProcedureNumber, procedure)] : []));
            }

            // Mostly one send on the call's thread, sometimes none or two; mostly one receive of its id in the server.
            int sends = random.NextDouble() switch { < 0.1 => 0, < 0.2 => 2, _ => 1 };
            long id = message++;
            for (int each = 0; each < sends; each++)
            {
                ObservationRowV1 send = Alpc(at + 2 + each, ObservationKind.Send, client, client + 1, ++ordinal);
                records.Add((send, random.NextDouble() < 0.95 ? [Field(send, SourceField.AlpcMessageId, each == 0 ? id : message++)] : []));
            }

            int receives = random.NextDouble() switch { < 0.1 => 0, < 0.15 => 2, _ => 1 };
            for (int each = 0; each < receives; each++)
            {
                ObservationRowV1 receive = Alpc(at + 3 + each, ObservationKind.Receive, Server, Server + 1, ++ordinal);
                records.Add((receive, [Field(receive, SourceField.AlpcMessageId, id)]));
            }

            if (random.NextDouble() > 0.08)
            {
                ObservationRowV1 served = RpcCall(at + 6, ObservationKind.RequestStart, Direction.Inbound, Server, ++ordinal, serverActivity, rpcInterface);
                long servedProcedure = random.NextDouble() < 0.05 ? procedure + 1 : procedure;
                records.Add((served, random.NextDouble() < 0.95 ? [Field(served, SourceField.RpcProcedureNumber, servedProcedure)] : []));
            }

            if (random.NextDouble() > 0.05)
            {
                records.Add((RpcCall(at + 10, ObservationKind.RequestEnd, Direction.Inbound, Server, ++ordinal, serverActivity, status: 0), []));
            }

            if (random.NextDouble() > 0.08)
            {
                records.Add((RpcCall(at + 20, ObservationKind.RequestEnd, Direction.Outbound, client, ++ordinal, clientId,
                    status: random.NextDouble() < 0.2 ? 5 : 0), []));
            }

            // Noise: ALPC between two other processes, as a busy machine's holds.
            if (random.NextDouble() < 0.4)
            {
                ObservationRowV1 noise = Alpc(at + 25, ObservationKind.Send, 500, 501, ++ordinal);
                ObservationRowV1 noiseReceive = Alpc(at + 26, ObservationKind.Receive, 600, 601, ++ordinal);
                records.Add((noise, [Field(noise, SourceField.AlpcMessageId, message)]));
                records.Add((noiseReceive, [Field(noiseReceive, SourceField.AlpcMessageId, message++)]));
            }
        }

        // Each record is delivered in the chunk of its reading, or a chunk or two late; in a sparse capture, some long
        // after the late window.
        long span = Math.Max(1, ((calls * spacing) + 200) / random.Next(3, 9));
        var chunks = new SortedDictionary<long, (List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields)>();
        foreach ((ObservationRowV1 row, List<SourceFieldRowV1> fields) in records)
        {
            double draw = random.NextDouble();
            long delivered = row.NativeTicks
                + (draw < 0.15 ? span * random.Next(1, 3) : 0)
                + (sparse && draw > 0.97 ? 200_000_000 : 0);
            long chunk = delivered / span;
            if (!chunks.TryGetValue(chunk, out (List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields) held))
            {
                held = ([], []);
                chunks[chunk] = held;
            }

            held.Rows.Add(row);
            held.Fields.AddRange(fields);
        }

        return [.. chunks.Values.Select(chunk => (chunk.Rows.ToArray(), chunk.Fields.ToArray()))];
    }

    private static Guid NewActivity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 9);
}
