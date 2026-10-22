using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A live generation's whole-session byte and peer rankings from what its derivation counts (revision 737): every record's
/// owner and transport bytes, counted by the activity and by each incarnation as they read the record, and extended with
/// them from one generation to the next. What they give must be exactly what a pass over every segment counts - the bytes
/// and peer bindings a finished session's persisted overview keeps - however the derivation was made, so a live view
/// opens no segment for them that its derivation has not read.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class DerivedRankingTests
{
    [Theory(DisplayName = "I14: the whole session's bytes and peer bindings the derivation counts are what a pass over every segment counts, derived whole or extended chunk by chunk")]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(20_261_022)]
    public void DerivedCountsAreThoseOfEverySegment(int seed)
    {
        var random = new Random(seed);
        int compared = 0;
        int extensions = 0;
        int ends = 0;
        int peers = 0;
        for (int trial = 0; trial < 8; trial++)
        {
            // Each chunk is published into one session as a live capture publishes it, so every generation's segments begin
            // with the ones before it.
            using var session = new TemporarySession();
            (ProcessInstanceIndex Processes, TransportRelationIndex Relations, ProcessActivityIndex Activity)? before = null;
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in RandomCaptures.Chunks(random))
            {
                _ = Publish(session.Store, [.. rows.Select(row => Sized(row, random))], rowsPerSegment: random.Next(1, rows.Length + 1),
                    fields: fieldRows);
                (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
                ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
                ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
                (int endsCounted, int peersCounted) = AssertCountsAreTheSegments(observations, processes, relations, activity);
                compared++;
                ends += endsCounted;
                peers += peersCounted;

                // Extended from the generation before, the derivation counts the same.
                if (before is { } previous
                    && previous.Processes.Extend(observations, TestClock, fields) is { } next
                    && previous.Relations.Extend(observations, next) is { } nextRelations
                    && previous.Activity.Extend(observations, next) is { } nextActivity)
                {
                    Assert.True(nextRelations.OwnersKept && nextActivity.OwnersKept);
                    _ = AssertCountsAreTheSegments(observations, next, nextRelations, nextActivity);
                    extensions++;
                    before = (next, nextRelations, nextActivity);
                }
                else
                {
                    before = (processes, relations, activity);
                }
            }
        }

        Assert.True(compared > 20, $"Only {compared} generations were compared.");
        Assert.True(extensions > 5, $"Only {extensions} generations extended the one before.");
        Assert.True(ends > 10, $"Only {ends} channel ends' bytes were compared.");
        Assert.True(peers > 10, $"Only {peers} records were found to have another end.");
    }

    [Fact(DisplayName = "S3: a live generation's whole-session byte and peer rankings come from its derivation, extended from the generation before, opening no segment it has not read, and are what its rows give")]
    public void ALiveGenerationRanksFromItsDerivation()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Conversation();
        _ = Publish(session.Store, rows[..12], rowsPerSegment: 3);
        SessionByteMeasures first = SessionByteRanking.Measure(session.Store, null);
        Assert.True(first.FromDerivation);
        _ = SessionPeerRanking.Measure(session.Store, null);

        // The next chunk adds to the same connections and opens another; the derivation extends over it alone.
        _ = Publish(session.Store, rows[12..], rowsPerSegment: 3);
        int added = SessionSegments.Names(session.Store.Current!).Count - 4;
        session.Store.ReleaseSegmentReaders();
        foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
        {
            SessionByteMeasures bytes = SessionByteRanking.Measure(session.Store, null, policy);
            SessionPeerMeasures counted = SessionPeerRanking.Measure(session.Store, null, policy);
            Assert.True(bytes.FromDerivation);
            Assert.InRange(session.Store.SegmentReaderCache.Entries, 0, added);

            // A time scope holding every record reads the rows, to the same answer.
            var everything = new TimeRange(0, long.MaxValue / 4);
            SessionByteMeasures read = SessionByteRanking.Measure(session.Store, everything, policy);
            Assert.False(read.FromDerivation);
            Assert.Equal(Text(read), Text(bytes));
            Assert.Equal(Text(SessionPeerRanking.Measure(session.Store, everything, policy)), Text(counted));
            session.Store.ReleaseSegmentReaders();
        }

        // The ranking's bytes are sizable at both ends of the paired connection and at the one to another host.
        SessionByteMeasures whole = SessionByteRanking.Measure(session.Store, null);
        Assert.Equal(2, whole.ByChannelEnd.Count);
        Assert.Equal(3, whole.ByProcess.Count);
    }

    [Fact(DisplayName = "I14: relations and counts read back from a checkpoint keep no owners, nor once extended, so their whole-session bytes and peers are read from the rows")]
    public void CountsReadBackFromACheckpointKeepNoOwners()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Conversation();
        _ = Publish(session.Store, rows[..12], rowsPerSegment: 3);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
        using var written = new MemoryStream();
        _ = DerivationCheckpoint.Write(written, session.Store.Current!.SessionId, 2, processes, relations, activity);
        DerivationCheckpoint read = DerivationCheckpoint.Read(written.ToArray(), session.Store.Current!.SessionId, TestClock);
        Assert.False(read.Relations.OwnersKept);
        Assert.False(read.Activity!.OwnersKept);
        Assert.Null(read.Relations.OwnersByPeer());
        Assert.Null(read.Activity.Owners());
        Assert.Null(SessionByteRanking.DerivedBytes(read.Processes, read.Relations, read.Activity));
        Assert.Null(SessionPeerRanking.DerivedPairs(read.Processes, read.Relations, read.Activity));

        _ = Publish(session.Store, rows[12..], rowsPerSegment: 3);
        (observations, fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex next = read.Processes.Extend(observations, TestClock, fields)
            ?? throw new Xunit.Sdk.XunitException("Every segment the checkpoint covers is still offered, so the instances must extend.");
        TransportRelationIndex nextRelations = read.Relations.Extend(observations, next)
            ?? throw new Xunit.Sdk.XunitException("No record added changes a decision made, so the relations must extend.");
        ProcessActivityIndex nextActivity = read.Activity.Extend(observations, next)
            ?? throw new Xunit.Sdk.XunitException("No record added binds otherwise, so the counts must extend.");
        Assert.False(nextRelations.OwnersKept || nextActivity.OwnersKept);
        Assert.Null(SessionByteRanking.DerivedBytes(next, nextRelations, nextActivity));
        Assert.Null(SessionPeerRanking.DerivedPairs(next, nextRelations, nextActivity));

        // Counts made under other instances than the ones asked about are not used either.
        Assert.Null(SessionByteRanking.DerivedBytes(next, relations, nextActivity));
        Assert.Null(SessionPeerRanking.DerivedPairs(processes, relations, nextActivity));
    }

    [Fact(DisplayName = "§10.2: a record whose bytes a byte view refuses keeps the derivation's bytes from being used, in a channel or apart from one, and the rows refuse them with why; a relation's owners are given only for one of these relations")]
    public void RefusedBytesAreLeftToTheRows()
    {
        // One of the paired connection's sends measures in a unit other than bytes; in the other session, a record naming
        // no end does.
        ObservationRowV1[] rows = Conversation();
        ObservationRowV1[] inChannel = [.. rows.Select(row => row.RawRecordOrdinal == 6 ? row with { MeasurementUnit = MeasurementUnit.Count } : row)];
        ObservationRowV1[] apart = [.. rows, Timed(Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 10, 300, 19)) with
        {
            MeasurementUnit = MeasurementUnit.Count,
        }];
        foreach ((ObservationRowV1[] session, bool refusedInChannel) in new[] { (inChannel, true), (apart, false) })
        {
            SessionDerivationCache.Clear();
            using var store = new TemporarySession();
            _ = Publish(store.Store, session, rowsPerSegment: 4);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(store.Store);
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
            TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
            ProcessActivityIndex activity = ProcessActivityIndex.Derive(observations, processes);
            TransportRelation channel = Assert.Single(relations.Relations);
            Assert.True(activity.BytesRefused);
            Assert.Equal(refusedInChannel, relations.OwnersOf(channel) is null);
            Assert.Null(SessionByteRanking.DerivedBytes(processes, relations, activity));
            Assert.True(SegmentMeasurement.IsUnsummable(Assert.Throws<InvalidDataException>(() => SessionByteRanking.Measure(store.Store, null))));

            // Peers count records, not bytes, and are counted from the derivation all the same.
            Assert.NotNull(SessionPeerRanking.DerivedPairs(processes, relations, activity));

            // A relation not among these relations has no owners here.
            Assert.Null(relations.OwnersOf(channel with { Records = channel.Records + 1 }));
            Assert.Null(relations.OwnersOf(channel with { Channel = relations.Channels }));
        }
    }

    /// <summary>
    /// Asserts the bytes and peer bindings the derivation counted are what a pass over every segment counts under the same
    /// derivation; returns how many channel ends' bytes and how many records with another end that compared.
    /// </summary>
    private static (int Ends, int Peers) AssertCountsAreTheSegments(
        SegmentReaderV1[] segments,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        ProcessActivityIndex activity)
    {
        SessionByteRanking.KeptPlan plan = SessionByteRanking.KeptPlan.For(processes, relations)
            ?? throw new Xunit.Sdk.XunitException("A random capture holds far fewer channels than an overview keeps.");
        SessionByteRanking.KeptTally total = plan.Start();
        foreach (SegmentReaderV1 segment in segments)
        {
            SessionByteRanking.KeptTally tally = plan.Start();
            plan.Measure(segment, tally, CancellationToken.None);
            total.Add(tally);
        }

        OverviewProcessBytes expected = plan.Finish(total)
            ?? throw new Xunit.Sdk.XunitException("No binding rule binds an end's records at two strengths.");
        OverviewProcessBytes derived = SessionByteRanking.DerivedBytes(processes, relations, activity)
            ?? throw new Xunit.Sdk.XunitException("A derivation that read every record keeps its bytes.");
        Assert.Equal(expected.Unbound, derived.Unbound);
        Assert.Equal(expected.Processes, derived.Processes);
        Assert.Equal(expected.Ends, derived.Ends);

        var positionOf = new Dictionary<ProcessInstanceId, int>();
        for (int position = 0; position < processes.Instances.Count; position++)
        {
            positionOf[processes.Instances[position].Id] = position;
        }

        OverviewPeerBindings kept = SessionPeerRanking.KeptBindings(segments, processes, relations, CancellationToken.None, int.MaxValue)!;
        (int, RelationStrength, int, RelationStrength, long)[] pairs = [.. kept.Pairs
            .Select(pair => (Position(pair.Owner), pair.OwnerStrength, Position(pair.Peer), pair.PeerStrength, pair.Records))
            .Order()];
        (int, RelationStrength, int, RelationStrength, long)[] counted = [.. (SessionPeerRanking.DerivedPairs(processes, relations, activity)
            ?? throw new Xunit.Sdk.XunitException("A derivation that read every record keeps how they bind."))
            .Select(pair => (Instance(pair.Owner), Strength(pair.Owner), Instance(pair.Peer), Strength(pair.Peer), pair.Records))
            .Order()];
        Assert.Equal(pairs, counted);
        return (derived.Ends.Count, (int)pairs.Where(pair => pair.Item3 >= 0).Sum(pair => pair.Item5));

        int Position(ProcessInstanceId instance) => instance.Value == Guid.Empty ? -1 : positionOf[instance];

        static int Instance(ProcessBinding binding) => binding.IsBound ? binding.Instance : -1;

        static RelationStrength Strength(ProcessBinding binding) => binding.IsBound ? binding.Strength : 0;
    }

    /// <summary>
    /// A record of the random capture that measures bytes - a send, a receive, or an open or close at its endpoint - with a
    /// size of its own, or none at all now and then, so sums differ owner by owner and side by side, and some records state
    /// no size.
    /// </summary>
    private static ObservationRowV1 Sized(ObservationRowV1 row, Random random) =>
        row.ByteDomain is null ? row
        : random.Next(6) == 0 ? row with
        {
            ByteValue = null,
            ByteAvailability = FieldAvailability.NotExposed,
            MeasurementQuality = QualityLevel.UnknownQuality,
        }
        : row with { ByteValue = random.Next(1, 5_000) };

    /// <summary>
    /// Process 100 connects to process 200 on this machine and sends it records, and connects to another host; process 300
    /// sends datagrams to another host. Its first twelve records are a live capture's first chunk; the rest add to the same
    /// connections and open one more.
    /// </summary>
    internal static ObservationRowV1[] Conversation()
    {
        const string client = "127.0.0.1:50000";
        const string server = "127.0.0.1:8080";
        const string local = "192.168.1.5:51000";
        const string remote = "203.0.113.7:443";
        return
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
            Timed(Lifecycle(3, ObservationKind.Create, 300, 3)),
            Timed(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 4).Between(client, server)),
            Timed(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 5).Between(server, client)),
            Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 120, 100, 6).Between(client, server)),
            Timed(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 120, 200, 7).Between(server, client)),
            Timed(Transfer(14, ObservationKind.Send, AccountingSide.SendSide, 900, 200, 8).Between(server, client)),
            Timed(Transfer(15, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 100, 9).Between(client, server)),
            Timed(Transfer(16, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between(local, remote)),
            Timed(Transfer(17, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 11).Between(local, remote)),
            Timed(Transfer(18, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 12).Between("192.168.1.5:61000", "8.8.8.8:53")
                with { Mechanism = Mechanism.Udp }),
            Timed(Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 300, 100, 13).Between(client, server)),
            Timed(Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 300, 200, 14).Between(server, client)),
            Timed(Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 2_000, 100, 15).Between(local, remote)),
            Timed(Transfer(23, ObservationKind.Send, AccountingSide.SendSide, null, 300, 16).Between("192.168.1.5:61000", "8.8.8.8:53")
                with { Mechanism = Mechanism.Udp }),
            Timed(Transfer(24, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 17).Between("192.168.1.5:52000", remote)),
            Timed(Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 75, 300, 18).Between("192.168.1.5:52000", remote)),
        ];
    }

    /// <summary>The current generation's observation and source-field segments.</summary>
    private static (SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) SegmentsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        return (
            [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))],
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))]);
    }

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>Every process's and channel end's bytes, and the unattributed, in one order.</summary>
    private static string Text(SessionByteMeasures measures) => string.Join(Environment.NewLine,
    [
        .. measures.ByProcess.OrderBy(entry => entry.Key.Value).Select(entry => $"{entry.Key.Value:N} {entry.Value}"),
        .. measures.ByChannelEnd.OrderBy(entry => entry.Key.ChannelKey, StringComparer.Ordinal).ThenBy(entry => entry.Key.Process.Value)
            .Select(entry => $"{entry.Key} {entry.Value}"),
        $"{measures.Unattributed}",
    ]);

    /// <summary>Every process's and group's count of peers, the processes with a peer and the unattributed, in one order.</summary>
    private static string Text(SessionPeerMeasures measures) => string.Join(Environment.NewLine,
    [
        .. measures.ByProcess.OrderBy(entry => entry.Key.Value).Select(entry => $"{entry.Key.Value:N} {entry.Value}"),
        .. measures.ByGroup.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key} {entry.Value}"),
        $"{measures.WithPeers} {measures.Unattributed}",
    ]);
}
