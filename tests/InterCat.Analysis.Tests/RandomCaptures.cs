using InterCat.Domain;
using InterCat.Storage;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>Random captures cut into chunks, shared by the tests of derivations and of the queries that read them.</summary>
internal static class RandomCaptures
{
    /// <summary>
    /// A random capture over a few endpoints and PIDs, cut into chunks as a live capture publishes them. Records mostly
    /// arrive in time order, but some are delivered a chunk or two late, as ETW delivers a busy processor's buffer late,
    /// so a chunk can hold records older than the chunk before it. Processes are created, exit, are reused and are
    /// confirmed by rundowns, some with the start key a source field carries, and a field can arrive after its record.
    /// </summary>
    internal static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> Chunks(Random random)
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
