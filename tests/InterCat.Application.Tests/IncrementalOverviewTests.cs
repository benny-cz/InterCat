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
    private static ObservationRowV1[][] LiveChunks()
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
