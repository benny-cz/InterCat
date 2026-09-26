using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A live capture publishes a generation per chunk. Its process instances and relations are extended from the previous
/// generation's rather than derived again, and an extension must equal a full derivation of the same segments exactly,
/// or decline so a full derivation answers (IC-015, I14).
/// </summary>
public sealed class IncrementalDerivationTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Theory(DisplayName = "I14: instances and relations extended chunk by chunk equal a derivation of every chunk at once")]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(20_260_927)]
    public void ExtendingEqualsDerivingAgain(int seed)
    {
        var random = new Random(seed);
        int extended = 0;
        int declined = 0;
        for (int trial = 0; trial < 10; trial++)
        {
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomChunks(random);
            var sessions = new List<TemporarySession>();
            try
            {
                var observations = new List<SegmentReaderV1>();
                var fields = new List<SegmentReaderV1>();
                ProcessInstanceIndex? processes = null;
                TransportRelationIndex? relations = null;
                foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in chunks)
                {
                    var session = new TemporarySession();
                    sessions.Add(session);
                    Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fieldRows);
                    (SegmentReaderV1[] chunkObservations, SegmentReaderV1[] chunkFields) = SegmentsOf(session.Store);
                    observations.AddRange(chunkObservations);
                    fields.AddRange(chunkFields);

                    ProcessInstanceIndex fullProcesses = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                    TransportRelationIndex fullRelations = TransportRelationIndex.Derive(observations, fullProcesses);
                    if (processes is null)
                    {
                        (processes, relations) = (fullProcesses, fullRelations);
                        continue;
                    }

                    ProcessInstanceIndex next = processes.Extend(observations, TestClock, fields)
                        ?? throw new Xunit.Sdk.XunitException("Every segment was carried, so the instances must extend.");
                    AssertSameProcesses(fullProcesses, next, observations);
                    TransportRelationIndex? nextRelations = relations!.Extend(observations, next);
                    if (nextRelations is null)
                    {
                        declined++;
                        nextRelations = TransportRelationIndex.Derive(observations, next);
                    }
                    else
                    {
                        extended++;
                    }

                    AssertSameRelations(fullRelations, nextRelations, observations);
                    (processes, relations) = (next, nextRelations);
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

        // Most chunks extend; a chunk whose late records would move an earlier record's incarnation declines.
        Assert.True(extended > declined, $"{extended} extensions and {declined} declined.");
    }

    [Fact(DisplayName = "I14: an extension declines, for a full derivation, whatever would change a decision already made")]
    public void ExtensionDeclinesWhatItCannotAdd()
    {
        ObservationRowV1[] connected =
        [
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 3).Between(ClientEnd, ServerEnd),
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 200, 4).Between(ServerEnd, ClientEnd),
        ];

        // A disconnect delivered late, among records already read, would move the records after it to another
        // incarnation: the relations decline and a full derivation answers.
        Assert.Null(Extended(connected,
            [Transfer(15, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 5).Between(ClientEnd, ServerEnd)]).Relations);

        // A creation of the client's PID, witnessed late at a reading before the records already read, splits the
        // instance those records bound to.
        Assert.Null(Extended(connected, [Lifecycle(15, ObservationKind.Create, 100, 6)]).Relations);

        // After the records already read, a disconnect or another process's creation changes nothing earlier: both extend.
        Assert.NotNull(Extended(connected,
            [Transfer(30, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 7).Between(ClientEnd, ServerEnd)]).Relations);
        Assert.NotNull(Extended(connected, [Lifecycle(30, ObservationKind.Create, 300, 8)]).Relations);

        // A capture-end rundown re-identifies the activity-only server as a rundown-witnessed instance: every record binds
        // to it alike, under a new identity, so the holders move to it and the relation names it.
        (ProcessInstanceIndex? processes, TransportRelationIndex? relations) =
            Extended(connected, [Lifecycle(40, ObservationKind.Inventory, 200, 9)]);
        ProcessInstance server = processes!.Instances.Single(instance => instance.ProcessId == 200);
        Assert.Equal(ProcessWitness.Rundown, server.Witness);
        TransportRelation relation = Assert.Single(relations!.Relations);
        Assert.Contains(server, new[] { relation.First, relation.Second });

        // A segment already read that a later generation no longer names - compacted into another, or released - leaves
        // nothing to extend from.
        using var first = new TemporarySession();
        Publish(first.Store, connected);
        using var second = new TemporarySession();
        Publish(second.Store, [Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 10).Between(ClientEnd, ServerEnd)]);
        (SegmentReaderV1[] before, SegmentReaderV1[] beforeFields) = SegmentsOf(first.Store);
        (SegmentReaderV1[] later, SegmentReaderV1[] laterFields) = SegmentsOf(second.Store);
        ProcessInstanceIndex derived = ProcessInstanceIndex.Derive(before, TestClock, beforeFields);
        Assert.Null(derived.Extend(later, TestClock, laterFields));
        Assert.Null(TransportRelationIndex.Derive(before, derived).Extend(later, ProcessInstanceIndex.Derive(later, TestClock, laterFields)));
    }

    /// <summary>The relations and instances of <paramref name="before"/> extended by a chunk holding <paramref name="added"/>.</summary>
    private static (ProcessInstanceIndex? Processes, TransportRelationIndex? Relations) Extended(
        ObservationRowV1[] before, ObservationRowV1[] added)
    {
        using var first = new TemporarySession();
        Publish(first.Store, before);
        using var second = new TemporarySession();
        Publish(second.Store, added);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(first.Store);
        (SegmentReaderV1[] addedObservations, SegmentReaderV1[] addedFields) = SegmentsOf(second.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        SegmentReaderV1[] all = [.. observations, .. addedObservations];
        SegmentReaderV1[] allFields = [.. fields, .. addedFields];
        ProcessInstanceIndex extended = processes.Extend(all, TestClock, allFields)!;
        AssertSameProcesses(ProcessInstanceIndex.Derive(all, TestClock, allFields), extended, all);
        TransportRelationIndex? extendedRelations = relations.Extend(all, extended);
        if (extendedRelations is not null)
        {
            AssertSameRelations(TransportRelationIndex.Derive(all, extended), extendedRelations, all);
        }

        return (extended, extendedRelations);
    }

    private static void AssertSameProcesses(
        ProcessInstanceIndex expected, ProcessInstanceIndex actual, IReadOnlyList<SegmentReaderV1> segments)
    {
        Assert.Equal(expected.Instances, actual.Instances);
        Assert.Equal(
            (expected.LifecycleRecords, expected.LifecycleRecordsWithoutOwner, expected.StartKeysAvailable),
            (actual.LifecycleRecords, actual.LifecycleRecordsWithoutOwner, actual.StartKeysAvailable));
        foreach (SegmentReaderV1 segment in segments)
        {
            Assert.Equal(expected.OwnersOf(segment), actual.OwnersOf(segment));
        }
    }

    private static void AssertSameRelations(
        TransportRelationIndex expected, TransportRelationIndex actual, IReadOnlyList<SegmentReaderV1> segments)
    {
        Assert.Equal(expected.Relations, actual.Relations);
        Assert.Equal(expected.Channels, actual.Channels);
        foreach (SegmentReaderV1 segment in segments)
        {
            (ProcessBinding[] expectedPeers, ChannelBinding[] expectedChannels) = expected.BindingsOf(segment);
            (ProcessBinding[] actualPeers, ChannelBinding[] actualChannels) = actual.BindingsOf(segment);
            Assert.Equal(expectedPeers, actualPeers);
            Assert.Equal(expectedChannels, actualChannels);
        }

        foreach (Mechanism mechanism in new[] { Mechanism.Tcp, Mechanism.Udp })
        {
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                Assert.Equal(
                    expected.RecordsWithoutAdmittedPeer(mechanism, policy),
                    actual.RecordsWithoutAdmittedPeer(mechanism, policy));
            }
        }
    }

    private static (SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) SegmentsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        return (
            [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))],
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))]);
    }

    /// <summary>
    /// A random capture over a few endpoints and PIDs, cut into chunks as a live capture publishes them. Records mostly
    /// arrive in time order, but some are delivered a chunk or two late, as ETW delivers a busy processor's buffer late,
    /// so a chunk can hold records older than the chunk before it. Processes are created, exit, are reused and are
    /// confirmed by rundowns, some with the start key a source field carries, and a field can arrive after its record.
    /// </summary>
    private static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> RandomChunks(Random random)
    {
        string[] clients = ["127.0.0.1:50000", "127.0.0.1:50001", "10.0.0.5:50002"];
        string[] servers = ["127.0.0.1:8080", "127.0.0.1:9090"];
        int[] pids = [100, 200, 300, 400];
        ObservationKind[] kinds =
        [
            ObservationKind.Send, ObservationKind.Receive, ObservationKind.Send, ObservationKind.Receive,
            ObservationKind.Send, ObservationKind.Receive, ObservationKind.Connect, ObservationKind.Accept,
            ObservationKind.Disconnect,
        ];
        ObservationKind[] lifecycle = [ObservationKind.Create, ObservationKind.Exit, ObservationKind.Inventory];
        int chunkCount = random.Next(2, 7);
        int count = random.Next(12, 90);
        var rows = new List<(ObservationRowV1 Row, int Chunk)>();
        var fields = new List<(SourceFieldRowV1 Field, int Chunk)>();
        ulong ordinal = 0;
        ulong sequence = 1_000;
        long ticks = 0;
        for (int index = 0; index < count; index++)
        {
            ticks += random.Next(0, 4);
            int pid = pids[random.Next(pids.Length)];
            int chunk = index * chunkCount / count;
            if (random.Next(7) == 0)
            {
                chunk = Math.Min(chunkCount - 1, chunk + random.Next(1, 3));
            }

            ObservationRowV1 row;
            if (random.Next(10) == 0)
            {
                row = Lifecycle(ticks, lifecycle[random.Next(lifecycle.Length)], pid, ++ordinal);
                if (random.Next(2) == 0)
                {
                    int fieldChunk = random.Next(4) == 0 ? Math.Min(chunkCount - 1, chunk + 1) : chunk;
                    fields.Add((Field(row, SourceField.ProcessStartSequence, (long)++sequence), fieldChunk));
                }
            }
            else
            {
                ObservationKind kind = kinds[random.Next(kinds.Length)];
                row = Transfer(ticks, kind, kind is ObservationKind.Send ? AccountingSide.SendSide
                    : kind is ObservationKind.Receive ? AccountingSide.ReceiveSide : AccountingSide.EndpointActivity,
                    kind is ObservationKind.Send or ObservationKind.Receive ? 16 : 0,
                    random.Next(15) == 0 ? null : pid, ++ordinal);
                string client = clients[random.Next(clients.Length)];
                string server = servers[random.Next(servers.Length)];
                row = random.Next(20) == 0 ? row : random.Next(2) == 0 ? row.Between(client, server) : row.Between(server, client);
                if (random.Next(5) == 0)
                {
                    row = row with { Mechanism = Mechanism.Udp };
                }
            }

            rows.Add((random.Next(3) == 0 ? row : row with { SessionRelativeTicks = ticks * 100 }, chunk));
        }

        // A chunk with no records publishes nothing, so its fields move to the last chunk that has records before it.
        var chunks = new List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)>();
        int[] kept = [.. Enumerable.Range(0, chunkCount).Where(chunk => rows.Any(entry => entry.Chunk == chunk))];
        foreach (int chunk in kept)
        {
            int next = Array.IndexOf(kept, chunk) + 1 < kept.Length ? kept[Array.IndexOf(kept, chunk) + 1] : chunkCount;
            chunks.Add((
                [.. rows.Where(entry => entry.Chunk == chunk).Select(entry => entry.Row)],
                [.. fields.Where(entry => entry.Chunk >= chunk && entry.Chunk < next).Select(entry => entry.Field)]));
        }

        return chunks;
    }
}
