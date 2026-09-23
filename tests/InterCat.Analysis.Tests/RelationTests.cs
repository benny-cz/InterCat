using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// `tcp-endpoint-relation-v1`: a record's other end is the process holding the mirrored endpoint pair, when every
/// record at that end binds to one instance. The filters and groupings built on it - participant, sender, receiver,
/// peer and cross-side totals - keep what they cannot resolve visible rather than guessing a peer (§7.4, §19.1, P6).
/// </summary>
public sealed class RelationTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "R22: a connection whose two ends each have one holder relates them, and nothing else is paired")]
    public void MirroredEndsWithOneHolderEachAreRelated()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derive(session.Store);
        ProcessInstance client = processes.Instances.Single(instance => instance.ProcessId == 100);
        ProcessInstance server = processes.Instances.Single(instance => instance.ProcessId == 200);

        // The end whose endpoint sorts first is listed first: 127.0.0.1:8080 before 127.0.0.1:50000.
        TransportRelation relation = Assert.Single(relations.Relations);
        Assert.Equal((server.Id, ServerEnd, client.Id, ClientEnd), (relation.First.Id, relation.FirstEndpoint, relation.Second.Id, relation.SecondEndpoint));
        Assert.Equal(RelationStrength.Correlated, relation.Strength);
        Assert.Equal((6L, 20L, 41L), (relation.Records, relation.FirstNativeTicks, relation.LastNativeTicks));

        SegmentReaderV1 segment = Assert.Single(Segments(session.Store));
        Dictionary<long, ProcessBinding> peers = PeersByReading(segment, relations);
        Assert.Equal(server.Id, processes.Instances[peers[30].Instance].Id);
        Assert.Equal(client.Id, processes.Instances[peers[31].Instance].Id);
        Assert.Equal(client.Id, processes.Instances[peers[21].Instance].Id);
        Assert.Equal(RelationStrength.Correlated, peers[30].Strength);
        Assert.Equal(ProcessBindingReason.PeerNotObserved, peers[50].Reason);
        Assert.Equal(ProcessBindingReason.PeerNotObserved, peers[60].Reason);
        Assert.Equal(ProcessBindingReason.PeerEndpointIncomplete, peers[70].Reason);
        Assert.Equal(ProcessBindingReason.NoRelationRule, peers[10].Reason);
    }

    [Fact(DisplayName = "P6: an end held by two processes, or by records that bind to none, leaves its peers unresolved")]
    public void AmbiguousOrUnboundEndsAreNeverGuessed()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 5, 201, 3).Between(ServerEnd, ClientEnd),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 7, 300, 4).Between("127.0.0.1:50001", "127.0.0.1:9090"),
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 7, null, 5).Between("127.0.0.1:9090", "127.0.0.1:50001"),
        ]);
        (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derive(session.Store);
        Dictionary<long, ProcessBinding> peers = PeersByReading(Assert.Single(Segments(session.Store)), relations);

        // Two processes hold the server end, so which one the client talked to is not decided; the client itself is
        // the one holder of its end, so both receive records still know their other end.
        Assert.Equal(ProcessBindingReason.PeerAmbiguous, peers[10].Reason);
        Assert.Equal(100, processes.Instances[peers[11].Instance].ProcessId);
        Assert.Equal(100, processes.Instances[peers[12].Instance].ProcessId);

        // The other end's only record names no process, so nothing binds there.
        Assert.Equal(ProcessBindingReason.PeerUnbound, peers[20].Reason);
        Assert.Equal(300, processes.Instances[peers[21].Instance].ProcessId);
        Assert.Empty(relations.Relations);
    }

    [Fact(DisplayName = "R22: a peer bound only as a candidate makes a candidate relation the default policy leaves out")]
    public void ACandidatePeerIsAdmittedOnlyWhenAskedFor()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 200, 1),
            Lifecycle(20, ObservationKind.Exit, 200, 2),
            Lifecycle(30, ObservationKind.Create, 200, 3),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4).Between(ClientEnd, ServerEnd),
            Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 5).Between(ServerEnd, ClientEnd),
        ]);
        (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derive(session.Store);
        Assert.Equal(RelationStrength.Candidate, Assert.Single(relations.Relations).Strength);
        ProcessInstanceId laterServer = processes.Instances
            .Where(instance => instance.ProcessId == 200)
            .OrderBy(instance => instance.CreatedNativeTicks)
            .Last().Id;

        // The client's send reached the server end, whose only record lies in a later instance of a reused PID, so
        // the send's other end is that instance only as a candidate.
        MetricRequest request = Request(Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Receiver = laterServer,
        };
        MetricResult byDefault = SessionMetrics.Evaluate(session.Store, request);
        Assert.Equal(MetricUnavailableReason.NothingMeasured, byDefault.Unavailable);
        Assert.Equal(1, byDefault.UnresolvedCounterparts[ProcessBindingReason.NotAdmittedByPolicy]);

        MetricResult withCandidates = SessionMetrics.Evaluate(session.Store, request with
        {
            EvidencePolicy = EvidencePolicy.IncludeCandidates,
        });
        Assert.Equal(64, withCandidates.Value);
        Assert.Empty(withCandidates.UnresolvedCounterparts);
    }

    [Fact(DisplayName = "R22: participant(P) takes P's records and those whose other end is P, and discloses what it cannot decide")]
    public void ParticipantTakesOwnAndRelatedRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceId client, ProcessInstanceId server, _) = Instances(session.Store);

        MetricResult count = SessionMetrics.Evaluate(
            session.Store, Request(Metric.Observations) with { Participant = server }, new() { EvidenceLimit = 20 });
        Assert.Equal(7, count.Value);
        Assert.Equal(5, count.ExcludedByProcessFilter);
        Assert.Equal([5L, 20, 21, 30, 31, 40, 41], count.Evidence.Select(item => item.Observation.NativeTicks).Order());
        Assert.Equal(TransportRelationIndex.RelationRule, count.RelationRule);
        Assert.Equal(ProcessInstanceIndex.BindingRule, count.BindingRule);

        // The records outside the filter that might still involve the server: two whose other end no record in the
        // capture holds, and one with no endpoint pair. Lifecycle records have no other end and are not counted.
        Assert.Equal(
            [(ProcessBindingReason.PeerEndpointIncomplete, 1L), (ProcessBindingReason.PeerNotObserved, 2L)],
            count.UnresolvedCounterparts.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)));
        Assert.Contains(count.Caveats, caveat => caveat.Contains("could be that end", StringComparison.Ordinal));

        // A sender-accounted total over the server's conversations takes each transfer once, at its sender.
        MetricResult sent = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Participant = server });
        Assert.Equal(140, sent.Value);
        MetricResult clientSent = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Participant = client });
        Assert.Equal(148, clientSent.Value);
    }

    [Fact(DisplayName = "R22: sender(P) and receiver(P) follow the data direction, measured at either end")]
    public void SenderAndReceiverFollowTheData()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceId client, _, _) = Instances(session.Store);
        MetricRequest sent = Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);
        MetricRequest received = Request(Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.ReceiveSide);

        // Everything the client sent, measured where it sent it, whoever the other end was.
        Assert.Equal(108, SessionMetrics.Evaluate(session.Store, sent with { Sender = client }).Value);

        // The client's data measured where it arrived: only the server's receive record proves the client sent it.
        MetricResult atReceivers = SessionMetrics.Evaluate(session.Store,
            sent with { AccountingSide = AccountingSide.ReceiveSide, Sender = client });
        Assert.Equal(100, atReceivers.Value);
        Assert.Empty(atReceivers.UnresolvedCounterparts);

        Assert.Equal(40, SessionMetrics.Evaluate(session.Store, received with { Receiver = client }).Value);
        MetricResult atSenders = SessionMetrics.Evaluate(session.Store,
            received with { AccountingSide = AccountingSide.SendSide, Receiver = client });
        Assert.Equal(40, atSenders.Value);

        // A send the client could have received is one whose other end is unresolved and that it did not make.
        Assert.Equal(1, Assert.Single(atSenders.UnresolvedCounterparts).Value);

        // A connect or accept carries no data direction, so it has no sender and no receiver.
        MetricResult sends = SessionMetrics.Evaluate(session.Store, Request(Metric.Observations) with { Sender = client });
        Assert.Equal(4, sends.Value);
    }

    [Fact(DisplayName = "R22: peer(P,Q) narrows a focus to one counterpart and is never a filter on its own")]
    public void PeerNarrowsAFocus()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceId client, ProcessInstanceId server, ProcessInstanceId other) = Instances(session.Store);
        MetricRequest count = Request(Metric.Observations);

        Assert.Equal(6, SessionMetrics.Evaluate(session.Store, count with { Participant = client, Peer = server }).Value);
        Assert.Equal(3, SessionMetrics.Evaluate(session.Store, count with { Owner = client, Peer = server }).Value);
        Assert.Equal(100, SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Sender = client, Peer = server }).Value);
        Assert.Equal(0, SessionMetrics.Evaluate(session.Store, count with { Participant = client, Peer = other }).Value);

        MetricResult absent = SessionMetrics.Evaluate(session.Store, count with { Participant = client, Peer = ProcessInstanceId.New() });
        Assert.Equal(MetricUnavailableReason.ProcessInstanceNotFound, absent.Unavailable);
        Assert.Contains("peer(P,Q)", (count with { Peer = server }).Check()!.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I5: grouped by peer, a focus's counterparts and its unresolved ends partition its total")]
    public void PeerGroupingPartitionsTheFocusTotal()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceId client, ProcessInstanceId server, _) = Instances(session.Store);

        MetricResult conversation = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
            {
                Participant = client,
                Grouping = LaneGrouping.Peer,
            });
        Assert.True(conversation.GroupsPartitionTotal);
        Assert.Equal(148, conversation.Value);
        MetricGroup peer = Assert.Single(conversation.Groups);
        Assert.Equal((server, 140L), (peer.Process!.Id, peer.Value!.Value));
        Assert.Equal(
            [(ProcessBindingReason.PeerEndpointIncomplete, 3L), (ProcessBindingReason.PeerNotObserved, 5L)],
            conversation.Unattributed.OrderBy(group => group.Reason).Select(group => (group.Reason!.Value, group.Value!.Value)));

        MetricResult toServer = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
            {
                Sender = client,
                Grouping = LaneGrouping.Peer,
            });
        Assert.Equal(100, Assert.Single(toServer.Groups).Value);
        Assert.Equal(108, toServer.Value);

        MetricRejection unfocused = (Request(Metric.Observations) with { Grouping = LaneGrouping.Peer }).Check()!;
        Assert.Contains("one focused process", unfocused.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I5: a cross-side total groups each record under the process at its other end, or its reason")]
    public void CrossSideTotalsGroupThroughTheRelation()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceId client, ProcessInstanceId server, _) = Instances(session.Store);

        MetricResult sentAtReceivers = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.True(sentAtReceivers.IsAvailable);
        Assert.Equal(TransportRelationIndex.RelationRule, sentAtReceivers.RelationRule);
        Assert.Equal(
            [(client, 100L), (server, 40L)],
            sentAtReceivers.Groups.Select(group => (group.Process!.Id, group.Value!.Value)));
        Assert.Empty(sentAtReceivers.Unattributed);

        MetricResult receivedAtSenders = SessionMetrics.Evaluate(session.Store,
            Request(Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.SendSide) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal(155, receivedAtSenders.Value);
        Assert.Equal(
            [(server, 100L), (client, 40L)],
            receivedAtSenders.Groups.Select(group => (group.Process!.Id, group.Value!.Value)));
        Assert.Equal(15, receivedAtSenders.Unattributed.Sum(group => group.Value));
        Assert.Equal(
            receivedAtSenders.Value,
            receivedAtSenders.Groups.Sum(group => group.Value) + receivedAtSenders.Unattributed.Sum(group => group.Value));
    }

    [Fact(DisplayName = "R2: an owner never takes a cross-side total, and one request names one process focus")]
    public void FocusRequestsThatMeanNothingAreRefused()
    {
        ProcessInstanceId one = ProcessInstanceId.New();
        ProcessInstanceId two = ProcessInstanceId.New();
        MetricRequest crossSide = Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide);

        Assert.Contains("sender or receiver focus", (crossSide with { Owner = one }).Check()!.Reason, StringComparison.Ordinal);
        Assert.Null((crossSide with { Sender = one }).Check());
        Assert.Contains("not several", (Request(Metric.Observations) with { Sender = one, Receiver = two }).Check()!.Reason, StringComparison.Ordinal);
        Assert.NotNull((Request(Metric.Observations) with { Participant = one, Peer = new ProcessInstanceId(Guid.Empty) }).Check());
    }

    [Fact(DisplayName = "I14: relations and peers do not depend on how records were split into segments or delivered")]
    public void RelationsIgnoreSegmentationAndOrder()
    {
        using var whole = new TemporarySession();
        Publish(whole.Store, ConnectedSession());
        using var split = new TemporarySession();
        Publish(split.Store, [.. ConnectedSession().Reverse()], rowsPerSegment: 2);

        (ProcessInstanceIndex wholeProcesses, TransportRelationIndex wholeRelations) = Derive(whole.Store);
        (ProcessInstanceIndex splitProcesses, TransportRelationIndex splitRelations) = Derive(split.Store);
        Assert.Equal(
            wholeRelations.Relations.Select(relation => (relation.First.Id, relation.Second.Id, relation.Strength, relation.Records)),
            splitRelations.Relations.Select(relation => (relation.First.Id, relation.Second.Id, relation.Strength, relation.Records)));
        Assert.Equal(
            PeerIdentities(whole.Store, wholeProcesses, wholeRelations),
            PeerIdentities(split.Store, splitProcesses, splitRelations));
    }

    [Fact(DisplayName = "R22: a peer count counts process instances at the other end, as a lower bound beside what it cannot resolve")]
    public void APeerCountIsALowerBoundOnProcessInstances()
    {
        using var session = new TemporarySession();
        Publish(session.Store, ConnectedSession());
        (ProcessInstanceId client, ProcessInstanceId server, ProcessInstanceId other) = Instances(session.Store);
        MetricRequest peers = Request(Metric.ActivePeers);

        MetricResult serverPeers = SessionMetrics.Evaluate(session.Store, peers with { Participant = server }, new() { EvidenceLimit = 3 });
        Assert.Equal(1, serverPeers.Value);
        Assert.Equal((6L, 0L), (serverPeers.KnownContributions, serverPeers.UnknownContributions));
        Assert.Equal(3, serverPeers.Evidence.Count);

        // The client also sent to an endpoint nothing in the capture holds, and wrote one record with no endpoint
        // pair: its one resolved peer is a lower bound, and the two ends it cannot name are counted by reason.
        MetricResult clientPeers = SessionMetrics.Evaluate(session.Store, peers with { Participant = client });
        Assert.Equal(1, clientPeers.Value);
        Assert.Equal(
            [(ProcessBindingReason.PeerEndpointIncomplete, 1L), (ProcessBindingReason.PeerNotObserved, 1L)],
            clientPeers.UnknownCounterparts.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)));
        Assert.Contains(clientPeers.Caveats, caveat => caveat.StartsWith("At least 1:", StringComparison.Ordinal));
        Assert.Equal(1, SessionMetrics.Evaluate(session.Store, peers with { Sender = client }).Value);
        Assert.Equal(1, SessionMetrics.Evaluate(session.Store, peers with { Receiver = client }).Value);

        // A process none of whose records resolves its other end has no count at all, not zero peers (R21).
        MetricResult unresolved = SessionMetrics.Evaluate(session.Store, peers with { Participant = other });
        Assert.Equal(MetricUnavailableReason.NothingMeasured, unresolved.Unavailable);
        Assert.Null(unresolved.Value);

        MetricResult channels = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels));
        Assert.Equal(MetricUnavailableReason.NoEntityBindings, channels.Unavailable);
        Assert.Contains("time-scoped", channels.UnavailableExplanation!, StringComparison.Ordinal);
        Assert.Contains("is one count", (peers with { Participant = client, Grouping = LaneGrouping.InstanceOnly }).Check()!.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R2: ranked by peers, each process counts its own, the rows overlap, and an unresolved-only process is unmeasured")]
    public void RankingByPeersSaysItsRowsOverlap()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. ConnectedSession(),
            Transfer(90, ObservationKind.Send, AccountingSide.SendSide, 1, 500, 13).Between("127.0.0.1:60000", "127.0.0.1:60001"),
            Transfer(91, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 500, 14).Between("127.0.0.1:60001", "127.0.0.1:60000"),
        ]);
        (ProcessInstanceId client, ProcessInstanceId server, ProcessInstanceId other) = Instances(session.Store);
        ProcessInstanceId self = ProcessInstanceIndex.Derive(Segments(session.Store), TestClock)
            .Instances.Single(instance => instance.ProcessId == 500).Id;

        MetricResult ranked = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.True(ranked.IsAvailable);
        Assert.False(ranked.GroupsPartitionTotal);
        Assert.Equal(3, ranked.Value);
        Assert.Equal(
            [(client, 1L, 6L, 2L), (server, 1L, 6L, 0L), (self, 1L, 2L, 0L), (other, (long?)null, 0L, 1L)],
            ranked.Groups
                .Select(group => (group.Process!.Id, group.Value, group.KnownContributions, group.UnknownContributions))
                .OrderBy(entry => entry.Value is null)
                .ThenByDescending(entry => entry.KnownContributions)
                .ThenBy(entry => entry.Id == client ? 0 : 1)
                .Select(entry => (entry.Id, entry.Value, entry.KnownContributions, entry.UnknownContributions)));
        Assert.Null(ranked.Groups[^1].Rank);
        Assert.Contains(ranked.Caveats, caveat => caveat.Contains("do not add up to the total", StringComparison.Ordinal));

        // A process connected to itself is its own peer, counted once however many of its records name it.
        Assert.Equal(1, SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Participant = self }).Value);

        MetricResult topTwo = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with
        {
            Grouping = LaneGrouping.InstanceOnly,
            RequestedRows = 2,
        });
        Assert.Equal(2, topTwo.Groups.Count);
        Assert.Equal((1L, 2), (topTwo.Remainder!.Value, topTwo.Remainder.GroupsMerged));
    }

    /// <summary>
    /// A client (PID 100, created and exited in the capture) connects to a server (PID 200, running at capture start)
    /// over loopback and they exchange 100 and 40 bytes. A third process sends to an endpoint nothing in the capture
    /// holds, the client sends to a remote address, and one client record carries no endpoint pair at all.
    /// </summary>
    private static ObservationRowV1[] ConnectedSession() =>
    [
        Lifecycle(5, ObservationKind.Inventory, 200, 1),
        Lifecycle(10, ObservationKind.Create, 100, 2),
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between(ClientEnd, ServerEnd),
        Transfer(21, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4).Between(ServerEnd, ClientEnd),
        Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 5).Between(ClientEnd, ServerEnd),
        Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 6).Between(ServerEnd, ClientEnd),
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 40, 200, 7).Between(ServerEnd, ClientEnd),
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 100, 8).Between(ClientEnd, ServerEnd),
        Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 7, 300, 9).Between("127.0.0.1:50001", "127.0.0.1:9090"),
        Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 5, 100, 10).Between("192.168.1.5:50002", "10.0.0.1:443"),
        Transfer(70, ObservationKind.Send, AccountingSide.SendSide, 3, 100, 11),
        Lifecycle(80, ObservationKind.Exit, 100, 12, exitCode: 0),
    ];

    private static (ProcessInstanceIndex Processes, TransportRelationIndex Relations) Derive(SessionStore store)
    {
        IReadOnlyList<SegmentReaderV1> segments = Segments(store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock);
        return (processes, TransportRelationIndex.Derive(segments, processes));
    }

    private static (ProcessInstanceId Client, ProcessInstanceId Server, ProcessInstanceId Other) Instances(SessionStore store)
    {
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(Segments(store), TestClock);
        return (
            processes.Instances.Single(instance => instance.ProcessId == 100).Id,
            processes.Instances.Single(instance => instance.ProcessId == 200).Id,
            processes.Instances.Single(instance => instance.ProcessId == 300).Id);
    }

    private static Dictionary<long, ProcessBinding> PeersByReading(SegmentReaderV1 segment, TransportRelationIndex relations)
    {
        ProcessBinding[] peers = relations.PeersOf(segment);
        return Enumerable.Range(0, segment.RowCount).ToDictionary(row => segment.Row(row).NativeTicks, row => peers[row]);
    }

    private static List<(long Reading, ProcessInstanceId? Peer, ProcessBindingReason Reason)> PeerIdentities(
        SessionStore store,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations) =>
    [
        .. Segments(store)
            .SelectMany(segment =>
            {
                ProcessBinding[] peers = relations.PeersOf(segment);
                return Enumerable.Range(0, segment.RowCount).Select(row => (
                    segment.Row(row).NativeTicks,
                    peers[row].IsBound ? processes.Instances[peers[row].Instance].Id : (ProcessInstanceId?)null,
                    peers[row].Reason));
            })
            .OrderBy(entry => entry.NativeTicks),
    ];

    private static MetricRequest Request(
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null) => new()
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = metric,
            ByteDomain = byteDomain,
            AccountingSide = accountingSide,
        };
}
