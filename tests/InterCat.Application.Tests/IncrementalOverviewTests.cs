using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The derivation cache is shared by every query in the process and keeps only the latest few generations, so a test
/// of what it extends runs alone rather than beside tests that would push its generations out.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedDerivationCache
{
    public const string Name = "Derivation cache";
}

/// <summary>
/// A live session publishes a generation per chunk, and each generation's instances and relations extend the previous
/// generation's. What the window then shows must be what a fresh derivation of the same generation shows (IC-015, I14).
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class IncrementalOverviewTests
{
    private const string Server = "127.0.0.1:8080";

    [Fact(DisplayName = "I14: each generation of a live session extends the one before it and projects as a fresh derivation would")]
    public void GenerationsExtendAndProjectAsFresh()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[][] chunks = LiveChunks();
        var how = new List<(bool Processes, bool Relations)>();
        for (int index = 0; index < chunks.Length; index++)
        {
            Publish(session.Store, chunks[index]);
            if (index == 6)
            {
                // A compaction rewrites every small publication into one segment: nothing read before is named any more.
                Assert.NotNull(SegmentCompaction.Compact(session.Store, Committed));
            }

            _ = SessionOverviewProjector.Project(session.Store);
            SessionDerivation derivation = SessionDerivationCache.For(session.Store.Current!);
            how.Add((derivation.ProcessesExtended, derivation.RelationsExtended));
            (ProcessInstanceIndex processes, TransportRelationIndex relations) = Fresh(session.Store);
            Assert.Equal(processes.Instances, derivation.DerivedProcesses!.Instances);
            Assert.Equal(relations.Relations, derivation.DerivedRelations!.Relations);
            Assert.Equal(relations.Channels, derivation.DerivedRelations.Channels);
        }

        // The first generation has nothing to extend. A server's disconnect delivered a chunk late, among records already
        // read, and the compaction are each derived in full; every other generation extends, the last one through a
        // capture-end rundown that re-identifies the server.
        Assert.Equal(
            [(false, false), (true, true), (true, true), (true, true), (true, false), (true, true), (false, false), (true, true)],
            how);
        Assert.Equal(ProcessWitness.Rundown, Fresh(session.Store).Processes.Instances.Single(instance => instance.ProcessId == 200).Witness);

        string extended = JsonSerializer.Serialize(SessionOverviewProjector.Project(session.Store));
        SessionDerivationCache.Clear();
        Assert.Equal(extended, JsonSerializer.Serialize(SessionOverviewProjector.Project(session.Store)));
    }

    [Fact(DisplayName = "I14: each generation of a live RPC peers capture extends its calls and their other ends, and projects as a fresh derivation would")]
    public void RpcPeersGenerationsExtendAndProjectAsFresh()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        byte[] plan = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = "normalizer-plan-v1",
            descriptors = new[] { new { mechanism = (int)Mechanism.Rpc }, new { mechanism = (int)Mechanism.Alpc } },
        });
        var how = new List<(bool Calls, bool Peers)>();
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)[] chunks = LiveCalls();
        for (int index = 0; index < chunks.Length; index++)
        {
            Publish(session.Store, chunks[index].Rows, fields: chunks[index].Fields, plan: index == 0 ? plan : null);
            string extended = JsonSerializer.Serialize(SessionOverviewProjector.Project(session.Store));
            SessionDerivation derivation = SessionDerivationCache.For(session.Store.Current!);
            how.Add((derivation.RpcCallsExtended, derivation.RpcPeersExtended));
            SessionDerivationCache.Clear();
            Assert.Equal(extended, JsonSerializer.Serialize(SessionOverviewProjector.Project(session.Store)));
            _ = SessionOverviewProjector.Project(session.Store);
            if (index + 1 < chunks.Length)
            {
                // The fresh projection leaves the derivation a later generation extends, as the live follow's would.
                Assert.NotNull(SessionDerivationCache.For(session.Store.Current!).DerivedRpcPeers);
            }
        }

        // The first generation has nothing to extend; every later one extends both, the call open across the second
        // chunk's cut and the message received in the next chunk included.
        Assert.Equal([(false, false), (true, true), (true, true)], how);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(12, Assert.Single(overview.Edges, edge => edge.Mechanism == Mechanism.Rpc).ObservationCount);
    }

    [Fact(DisplayName = "I14: each generation of a live WinINet capture extends its HTTP exchanges, and lists them as a fresh derivation would")]
    public void HttpGenerationsExtendAndListAsFresh()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = HttpExchangeRows();

        // Three chunks of the capture, cut in time between buffers of open exchanges, each with its records' fields; the
        // second use of each of two numbers begins in the last.
        int[] cuts = [0, 7, 13, rows.Length];
        var how = new List<bool>();
        for (int index = 0; index + 1 < cuts.Length; index++)
        {
            ObservationRowV1[] chunk = rows[cuts[index]..cuts[index + 1]];
            var ordinals = new HashSet<ulong>(chunk.Select(row => row.RawRecordOrdinal));
            Publish(session.Store, chunk, fields: [.. fields.Where(field => ordinals.Contains(field.RawRecordOrdinal))]);
            ProcessInstanceId client = Fresh(session.Store).Processes.Instances.Single(instance => instance.ProcessId == 4_242).Id;
            string extended = Listed(session.Store, client);
            how.Add(SessionDerivationCache.For(session.Store.Current!).HttpExchangesExtended);
            SessionDerivationCache.Clear();
            Assert.Equal(extended, Listed(session.Store, client));
        }

        // The first generation has nothing to extend; every later one extends from the one before it, which the fresh
        // listing left in the cache, as the live follow's would.
        Assert.Equal([false, true, true], how);
        Assert.Equal(5, SessionHttpExchanges.Channels(session.Store,
            Fresh(session.Store).Processes.Instances.Single(instance => instance.ProcessId == 4_242).Id).Channels.Single().Exchanges);

        static string Listed(SessionStore store, ProcessInstanceId client) =>
            JsonSerializer.Serialize(SessionHttpExchanges.Channels(store, client))
            + JsonSerializer.Serialize(SessionHttpExchanges.Exchanges(store, HttpExchangeKeys.Channel(client)));
    }

    /// <summary>
    /// Three chunks of a live RPC peers capture: the service control manager's three calls, linked through ALPC, and a
    /// fourth whose client stop and whose message's receive arrive in the third chunk.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)[] LiveCalls()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Guid client = new(4, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
        Guid server = new(14, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
        ObservationRowV1 start = RpcCall(400, ObservationKind.RequestStart, Direction.Outbound, 400, 70, client, ServiceControlInterface);
        ObservationRowV1 send = Alpc(402, ObservationKind.Send, 400, 401, 71);
        ObservationRowV1 receive = Alpc(403, ObservationKind.Receive, 1_960, 1_961, 72);
        ObservationRowV1 served = RpcCall(405, ObservationKind.RequestStart, Direction.Inbound, 1_960, 73, server, ServiceControlInterface);
        ObservationRowV1[] second =
        [
            start,
            send,
            served,
            RpcCall(410, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 74, server, status: 0),
        ];
        ObservationRowV1[] third = [receive, RpcCall(420, ObservationKind.RequestEnd, Direction.Outbound, 400, 75, client, status: 0)];
        return
        [
            (rows, fields),
            (second, [Field(start, SourceField.RpcProcedureNumber, 7), Field(served, SourceField.RpcProcedureNumber, 7),
                Field(send, SourceField.AlpcMessageId, 31)]),
            (third, [Field(receive, SourceField.AlpcMessageId, 31)]),
        ];
    }

    private static (ProcessInstanceIndex Processes, TransportRelationIndex Relations) Fresh(SessionStore store)
    {
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, SessionSegments.SourceClock(store.Root, manifest)!.Value, fields);
        return (processes, TransportRelationIndex.Derive(segments, processes));
    }

    /// <summary>
    /// Eight chunks of a capture in which a client, created in the first, opens a connection to a server in each chunk,
    /// exchanges five transfers and closes it, beside a datagram flow that lasts the capture. One record arrives a chunk
    /// late inside its connection, the server's close of the fourth connection arrives a chunk late at a reading before
    /// its own last records, and the last chunk holds the server's capture-end rundown.
    /// </summary>
    internal static ObservationRowV1[][] LiveChunks()
    {
        ulong ordinal = 0;
        List<ObservationRowV1>[] chunks = [.. Enumerable.Range(0, 8).Select(_ => new List<ObservationRowV1>())];
        chunks[0].Add(Lifecycle(5, ObservationKind.Create, 100, ++ordinal));
        for (int chunk = 0; chunk < chunks.Length; chunk++)
        {
            long at = chunk * 100L;
            string client = $"127.0.0.1:{50_000 + chunk}";
            chunks[chunk].Add(Transfer(at + 10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, ++ordinal).Between(client, Server));
            chunks[chunk].Add(Transfer(at + 11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, ++ordinal).Between(Server, client));
            for (int transfer = 0; transfer < 5; transfer++)
            {
                chunks[chunk].Add(Transfer(at + 20 + (10 * transfer), ObservationKind.Send, AccountingSide.SendSide, 64, 100, ++ordinal)
                    .Between(client, Server));
                chunks[chunk].Add(Transfer(at + 21 + (10 * transfer), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, ++ordinal)
                    .Between(Server, client));
            }

            chunks[chunk].Add(Transfer(at + 90, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, ++ordinal).Between(client, Server));
            ObservationRowV1 serverClose = chunk == 3
                ? Transfer(at + 45, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 200, ++ordinal).Between(Server, client)
                : Transfer(at + 91, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 200, ++ordinal).Between(Server, client);
            chunks[chunk == 3 ? 4 : chunk].Add(serverClose);
            chunks[chunk].Add(Transfer(at + 70, ObservationKind.Send, AccountingSide.SendSide, 32, 100, ++ordinal)
                .Between("127.0.0.1:5353", "127.0.0.1:5354") with { Mechanism = Mechanism.Udp });
            chunks[chunk].Add(Transfer(at + 71, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 200, ++ordinal)
                .Between("127.0.0.1:5353", "127.0.0.1:5354") with { Mechanism = Mechanism.Udp });
        }

        chunks[3].Add(Transfer(265, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, ++ordinal).Between(Server, "127.0.0.1:50002"));
        chunks[7].Add(Lifecycle(795, ObservationKind.Inventory, 200, ++ordinal));
        return [.. chunks.Select(rows => rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 }).ToArray())];
    }
}
