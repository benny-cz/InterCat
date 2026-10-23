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

    [Theory(DisplayName = "I14: RPC calls and their other ends extended through processes that exit, return and appear, server calls held open for chunks and server calls two clients reach equal a derivation of every chunk at once, and write the same operation index")]
    [InlineData(5, false)]
    [InlineData(20_261_023, false)]
    [InlineData(17, true)]
    public void ExtendingThroughChurnEqualsPairingAgain(int seed, bool sparse)
    {
        var random = new Random(seed);
        int extended = 0;
        int shared = 0;
        int rebound = 0;
        for (int trial = 0; trial < 12; trial++)
        {
            // Every chunk is published into one session, as a live capture's generations are, so an operation index can
            // name the segments.
            using var session = new TemporarySession();
            RpcCallIndex? calls = null;
            RpcPeerIndex? peers = null;
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in RandomRpcCaptures.Chunks(random, sparse, churn: true))
            {
                Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fieldRows);
                (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(session.Store);
                ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                RpcCallIndex fullCalls = RpcCallIndex.Derive(observations, fields, processes, TestClock);
                RpcPeerIndex fullPeers = RpcPeerIndex.Derive(fullCalls, observations, fields);
                RpcCallIndex? next = calls?.Extend(observations, fields, processes);
                RpcPeerIndex? nextPeers = next is null ? null : peers!.Extend(next, observations, fields);
                if (next is null || nextPeers is null)
                {
                    (calls, peers) = (fullCalls, fullPeers);
                    continue;
                }

                extended++;
                rebound += calls!.Groups.Select(group => group.Process).Except(next.Groups.Select(group => group.Process)).Any() ? 1 : 0;
                AssertSameCalls(fullCalls, next, observations);
                AssertSamePeers(fullPeers, fullCalls, nextPeers, next, observations);
                Assert.Equal(Written(session.Store, fullCalls, fullPeers, processes), Written(session.Store, next, nextPeers, processes));
                shared += (int)next.Groups.Sum(group => nextPeers.CountsOf(group).Unresolved.GetValueOrDefault(RpcPeerState.ServerCallShared));
                (calls, peers) = (next, nextPeers);
            }
        }

        // Most chunks extend; processes come and go between them, so groups are bound to other instances; and two clients
        // reach one server call.
        Assert.True(extended > 20, $"{extended} extensions.");
        Assert.True(rebound > 0 && shared > 0, $"{rebound} extensions bound groups otherwise, {shared} calls shared a server call.");
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

    [Fact(DisplayName = "I14: a call made after its process exited is grouped by why it binds to none, and an extension groups it again once its PID is used again, though the call stands")]
    public void AnExtensionGroupsAgainWhatBindsForAnotherReason()
    {
        // A client's call, its exit, and a call of its PID after it; twenty seconds later the PID is used again.
        ObservationRowV1[] first =
        [
            Lifecycle(2, ObservationKind.Create, 410, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 410, 2, Activity(1), ServiceControlInterface),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 410, 3, Activity(1), status: 0),
            Lifecycle(200, ObservationKind.Exit, 410, 4, exitCode: 0),
            RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 410, 5, Activity(2), ServiceControlInterface),
            RpcCall(320, ObservationKind.RequestEnd, Direction.Outbound, 410, 6, Activity(2), status: 0),
        ];
        ObservationRowV1[] later =
        [
            Lifecycle(200_000_400, ObservationKind.Create, 410, 7),
            RpcCall(200_000_500, ObservationKind.RequestStart, Direction.Outbound, 410, 8, Activity(3), ServiceControlInterface),
        ];
        (RpcCallIndex? extended, _) = Extended(first, [], later, []);

        // The call after the exit stands - it is long behind the window - yet it now falls between two instances, so it
        // is grouped as a full derivation groups it, not where it was.
        using var all = new TemporarySession();
        Publish(all.Store, [.. first, .. later]);
        (SegmentReaderV1[] segments, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(all.Store);
        RpcCallIndex derived = RpcCallIndex.Derive(segments, fields, ProcessInstanceIndex.Derive(segments, TestClock, fields), TestClock);
        Assert.NotNull(extended);
        Assert.Equal(derived.Groups, extended.Groups);
        Assert.Contains(extended.Groups, group => group.Process.Reason == ProcessBindingReason.BetweenInstances && group.Counts.Calls == 1);
        Assert.DoesNotContain(extended.Groups, group => group.Process.Reason == ProcessBindingReason.AfterExit);
    }

    [Fact(DisplayName = "I14: an exit and a process start read late split a group whose calls stand between the two instances they bound to")]
    public void LifecycleReadLateSplitsAStandingGroup()
    {
        // Two calls of one client, bound to its one instance, and a call twenty seconds later.
        ObservationRowV1[] first =
        [
            Lifecycle(2, ObservationKind.Create, 410, 1),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 410, 2, Activity(1), ServiceControlInterface),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 410, 3, Activity(1), status: 0),
            RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 410, 4, Activity(2), ServiceControlInterface),
            RpcCall(320, ObservationKind.RequestEnd, Direction.Outbound, 410, 5, Activity(2), status: 0),
            RpcCall(200_000_000, ObservationKind.RequestStart, Direction.Outbound, 400, 6, Activity(3), ServiceControlInterface),
        ];

        // The client's exit between them and its PID's next process, read a chunk late: the first call binds as it did, the
        // second to the new instance, though both stand.
        ObservationRowV1[] later = [Lifecycle(200, ObservationKind.Exit, 410, 7, exitCode: 0), Lifecycle(250, ObservationKind.Create, 410, 8)];
        (RpcCallIndex? extended, _) = Extended(first, [], later, []);
        using var all = new TemporarySession();
        Publish(all.Store, [.. first, .. later]);
        (SegmentReaderV1[] segments, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(all.Store);
        RpcCallIndex derived = RpcCallIndex.Derive(segments, fields, ProcessInstanceIndex.Derive(segments, TestClock, fields), TestClock);
        Assert.NotNull(extended);
        Assert.Equal(derived.Groups, extended.Groups);
        Assert.Equal([RelationStrength.Correlated, RelationStrength.Candidate],
            extended.Groups.Where(group => group.ProcessId == 410).Select(group => group.Process.Strength).Order());
    }

    [Fact(DisplayName = "I14: a server call paired again from a key kept open, read late, links a client call that stands, whose message reached its thread before it")]
    public void AServerCallPairedAgainLinksAStandingClient()
    {
        // A server call open since before a client's call, whose message the server's thread receives; nothing begins
        // there after it, so the call reaches no server call. A call twenty seconds later puts both behind the window.
        Guid served = Activity(9);
        ObservationRowV1 client = RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 1, Activity(1), ServiceControlInterface);
        ObservationRowV1 send = Alpc(102, ObservationKind.Send, 400, 401, 2);
        ObservationRowV1 receive = Alpc(104, ObservationKind.Receive, 1_960, 1_961, 3);
        ObservationRowV1 open = RpcCall(50, ObservationKind.RequestStart, Direction.Inbound, 1_960, 4, served, ServiceControlInterface);
        ObservationRowV1[] first =
        [
            Lifecycle(1, ObservationKind.Inventory, 400, 20),
            Lifecycle(1, ObservationKind.Inventory, 1_960, 21),
            open,
            client,
            send,
            receive,
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 5, Activity(1), status: 0),
            RpcCall(200_000_000, ObservationKind.RequestStart, Direction.Outbound, 420, 6, Activity(2), ServiceControlInterface),
        ];
        SourceFieldRowV1[] fields =
        [
            Field(client, SourceField.RpcProcedureNumber, 7),
            Field(open, SourceField.RpcProcedureNumber, 7),
            Field(send, SourceField.AlpcMessageId, 51),
            Field(receive, SourceField.AlpcMessageId, 51),
        ];

        // A start of the open call's key on that thread, read late: the key is kept, so its calls are paired again - an
        // ambiguous run - and the new one begins after the message was received, within the window, before the client's
        // call stopped.
        ObservationRowV1 begun = RpcCall(106, ObservationKind.RequestStart, Direction.Inbound, 1_960, 7, served, ServiceControlInterface);
        (RpcCallIndex? calls, RpcPeerIndex? peers) = Extended(first, fields, [begun], [Field(begun, SourceField.RpcProcedureNumber, 7)]);
        Assert.NotNull(calls);
        Assert.NotNull(peers);
        Assert.Equal((2L, 1L), (calls.Totals.Ambiguous, peers.CountsOf(calls.Groups.Single(group => group.ProcessId == 400)).Served));
        Assert.Equal(0L, Unresolved(calls, peers, RpcPeerState.NoServerCall));
    }

    [Fact(DisplayName = "I14: a server call two clients reached is the other's again when a send read late undoes one client's link, though the other client's call stands")]
    public void ALinkUndoneLateFreesItsServerCall()
    {
        // Two clients' messages are received on the thread that then begins one server call, so it is neither's; a call
        // twenty seconds later puts both long behind the window.
        ObservationRowV1 first = RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 1, Activity(1), ServiceControlInterface);
        ObservationRowV1 second = RpcCall(101, ObservationKind.RequestStart, Direction.Outbound, 410, 2, Activity(2), ServiceControlInterface);
        ObservationRowV1 firstSend = Alpc(102, ObservationKind.Send, 400, 401, 3);
        ObservationRowV1 secondSend = Alpc(103, ObservationKind.Send, 410, 411, 4);
        ObservationRowV1 firstReceive = Alpc(104, ObservationKind.Receive, 1_960, 1_961, 5);
        ObservationRowV1 secondReceive = Alpc(105, ObservationKind.Receive, 1_960, 1_961, 6);
        ObservationRowV1 served = RpcCall(106, ObservationKind.RequestStart, Direction.Inbound, 1_960, 7, Activity(3), ServiceControlInterface);
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Inventory, 400, 20),
            Lifecycle(1, ObservationKind.Inventory, 410, 21),
            Lifecycle(1, ObservationKind.Inventory, 1_960, 22),
            first,
            second,
            firstSend,
            secondSend,
            firstReceive,
            secondReceive,
            served,
            RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 8, Activity(3), status: 0),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 9, Activity(1), status: 0),
            RpcCall(124, ObservationKind.RequestEnd, Direction.Outbound, 410, 10, Activity(2), status: 0),
            RpcCall(200_000_000, ObservationKind.RequestStart, Direction.Outbound, 420, 11, Activity(4), ServiceControlInterface),
        ];
        SourceFieldRowV1[] fields =
        [
            Field(first, SourceField.RpcProcedureNumber, 7),
            Field(second, SourceField.RpcProcedureNumber, 7),
            Field(served, SourceField.RpcProcedureNumber, 7),
            Field(firstSend, SourceField.AlpcMessageId, 31),
            Field(secondSend, SourceField.AlpcMessageId, 32),
            Field(firstReceive, SourceField.AlpcMessageId, 31),
            Field(secondReceive, SourceField.AlpcMessageId, 32),
        ];
        using var before = new TemporarySession();
        Publish(before.Store, rows, fields: fields);
        (SegmentReaderV1[] earlier, SegmentReaderV1[] earlierFields) = IncrementalDerivationTests.SegmentsOf(before.Store);
        RpcCallIndex earlierCalls = RpcCallIndex.Derive(earlier, earlierFields, ProcessInstanceIndex.Derive(earlier, TestClock, earlierFields), TestClock);
        RpcPeerIndex earlierPeers = RpcPeerIndex.Derive(earlierCalls, earlier, earlierFields);
        Assert.Equal(2L, Unresolved(earlierCalls, earlierPeers, RpcPeerState.ServerCallShared));

        // A second send on the second client's thread within its call, read a chunk late: that call no longer links, so
        // the server call served the first, whose call stands, its every record read before the late send.
        ObservationRowV1 late = Alpc(123, ObservationKind.Send, 410, 411, 12);
        (RpcCallIndex? calls, RpcPeerIndex? peers) = Extended(rows, fields, [late], [Field(late, SourceField.AlpcMessageId, 33)]);
        Assert.NotNull(calls);
        Assert.NotNull(peers);
        Assert.Equal((0L, 1L), (Unresolved(calls, peers, RpcPeerState.ServerCallShared), Unresolved(calls, peers, RpcPeerState.SeveralSends)));
        Assert.Equal(1L, peers.CountsOf(calls.Groups.Single(group => group.ProcessId == 400)).Served);
        Assert.Equal([(400, 1_960)], peers.Links().Select(link => (link.ClientProcessId, link.ServerProcessId)));
    }

    [Fact(DisplayName = "I14: other ends followed while no ALPC was read are all linked again once a generation reads some")]
    public void OtherEndsFollowedWithoutAlpcAreLinkedAgain()
    {
        // A completed call, then one twenty seconds later; no ALPC record in either.
        ObservationRowV1 calling = RpcCall(200_000_000, ObservationKind.RequestStart, Direction.Outbound, 400, 3, Activity(2), ServiceControlInterface);
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Inventory, 400, 20),
            Lifecycle(1, ObservationKind.Inventory, 1_960, 21),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 1, Activity(1), ServiceControlInterface),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 2, Activity(1), status: 0),
            calling,
        ];

        // The later call's message, and the server call it reached: now that ALPC was read, the first call sent nothing.
        ObservationRowV1 send = Alpc(200_000_002, ObservationKind.Send, 400, 401, 4);
        ObservationRowV1 receive = Alpc(200_000_003, ObservationKind.Receive, 1_960, 1_961, 5);
        ObservationRowV1 start = RpcCall(200_000_006, ObservationKind.RequestStart, Direction.Inbound, 1_960, 6, Activity(3), ServiceControlInterface);
        ObservationRowV1[] added =
        [
            send,
            receive,
            start,
            RpcCall(200_000_010, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 7, Activity(3), status: 0),
            RpcCall(200_000_020, ObservationKind.RequestEnd, Direction.Outbound, 400, 8, Activity(2), status: 0),
        ];
        (RpcCallIndex? calls, RpcPeerIndex? peers) = Extended(rows, [Field(calling, SourceField.RpcProcedureNumber, 7)], added,
            [Field(send, SourceField.AlpcMessageId, 41), Field(receive, SourceField.AlpcMessageId, 41), Field(start, SourceField.RpcProcedureNumber, 7)]);
        Assert.NotNull(calls);
        Assert.NotNull(peers);
        Assert.Equal((0L, 1L, 1), (Unresolved(calls, peers, RpcPeerState.NoAlpcEvidence), Unresolved(calls, peers, RpcPeerState.NoSend),
            peers.Links().Count()));
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

    /// <summary>How many client calls the other ends say <paramref name="state"/> of.</summary>
    private static long Unresolved(RpcCallIndex calls, RpcPeerIndex peers, RpcPeerState state) =>
        calls.Groups.Where(group => group.Side == RpcCallSide.Client).Sum(group => peers.CountsOf(group).Unresolved.GetValueOrDefault(state));

    /// <summary>The operation index the calls, their other ends and the session's exchanges write, for its current generation.</summary>
    private static byte[] Written(SessionStore store, RpcCallIndex calls, RpcPeerIndex peers, ProcessInstanceIndex processes)
    {
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(store);
        using var written = new MemoryStream();
        _ = OperationIndex.Write(written, store.SessionId, store.Current!.Generation, [.. observations.Select(segment => segment.Published!)],
            [.. fields.Select(segment => segment.Published!)], calls, peers, HttpExchangeIndex.Derive(observations, fields, processes));
        return written.ToArray();
    }

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
/// delivered after the late window behind the latest record (<see cref="RpcCallIndex.LateWindowNanoseconds"/>). A
/// churning capture also has a client exit and its PID used again, a client that first calls halfway through, server
/// calls held open for chunks, and server calls two clients reach.
/// </summary>
internal static class RandomRpcCaptures
{
    private const int Server = 1_960;

    /// <summary>A churning capture's client whose process exits and whose PID is used again.</summary>
    private const int Reused = 410;

    /// <summary>A churning capture's client that first calls halfway through, numbered between the others.</summary>
    private const int Joining = 405;

    private static readonly int[] Clients = [400, 410, 420];
    private static readonly Guid[] Interfaces =
    [
        ServiceControlInterface,
        Guid.Parse("12345778-1234-abcd-ef00-0123456789ac"),
        Guid.Parse("e1af8308-5d1f-11c9-91a4-08002b14a0fa"),
    ];

    public static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> Chunks(Random random, bool sparse, bool churn = false)
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
        bool joined = false;
        for (int call = 0; call < calls; call++)
        {
            long at = 100 + (call * spacing) + random.Next(0, 30);
            int client = Clients[random.Next(Clients.Length)];
            if (churn)
            {
                if (call == calls / 3)
                {
                    records.Add((Lifecycle(at - 1, ObservationKind.Exit, Reused, ++ordinal, exitCode: 0), []));
                }
                else if (call == 2 * calls / 3)
                {
                    records.Add((Lifecycle(at - 1, ObservationKind.Create, Reused, ++ordinal), []));
                }

                if (call >= calls / 2 && random.NextDouble() < 0.3)
                {
                    if (!joined)
                    {
                        records.Add((Lifecycle(at - 1, ObservationKind.Create, Joining, ++ordinal), []));
                        joined = true;
                    }

                    client = Joining;
                }
            }

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

            // Another client sometimes reaches the same server call, its message received on the server's thread just
            // before the call begins.
            if (churn && random.NextDouble() < 0.12)
            {
                int other = client == Clients[0] ? Clients[1] : Clients[0];
                long otherId = message++;
                ObservationRowV1 otherStart = RpcCall(at + 1, ObservationKind.RequestStart, Direction.Outbound, other, ++ordinal, NewActivity(++activity), rpcInterface);
                ObservationRowV1 otherSend = Alpc(at + 2, ObservationKind.Send, other, other + 1, ++ordinal);
                ObservationRowV1 otherReceive = Alpc(at + 4, ObservationKind.Receive, Server, Server + 1, ++ordinal);
                records.Add((otherStart, [Field(otherStart, SourceField.RpcProcedureNumber, procedure)]));
                records.Add((otherSend, [Field(otherSend, SourceField.AlpcMessageId, otherId)]));
                records.Add((otherReceive, [Field(otherReceive, SourceField.AlpcMessageId, otherId)]));
                records.Add((RpcCall(at + 24, ObservationKind.RequestEnd, Direction.Outbound, other, ++ordinal, otherStart.ActivityId, status: 0), []));
            }

            if (random.NextDouble() > 0.05)
            {
                // A churning capture's server call is sometimes held open for chunks, and paired again each generation.
                long stopped = at + 10 + (churn && random.NextDouble() < 0.15 ? spacing * random.Next(3, 12) : 0);
                records.Add((RpcCall(stopped, ObservationKind.RequestEnd, Direction.Inbound, Server, ++ordinal, serverActivity, status: 0), []));
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
