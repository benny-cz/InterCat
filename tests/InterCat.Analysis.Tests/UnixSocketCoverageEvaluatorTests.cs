using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// AF_UNIX coverage against a truth log (ADR-074, ADR-075): each truth message is matched to its owner's row of its kind
/// nearest it, bound to its socket with the bytes it moved; a row of another process is scope, a message read in two
/// calls leaves one row beyond the truth, and since AF_UNIX names no peer the capture attributes none, so the peer
/// ceiling is not measured and the mechanism stays below traffic visualization.
/// </summary>
public sealed class UnixSocketCoverageEvaluatorTests
{
    private const int Client = 20, Server = 10, Other = 99;
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "P27: an AF_UNIX evaluation matches each truth message to its owner's nearest row of its kind, bound to its socket with its bytes, and names no peer")]
    public void EachMessageIsItsOwnersRow()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.ConnectionEstablished, 0, callId: 0),
            Truth("client", Client, 2, TruthEventKind.MessageSent, 10, callId: 0, bytes: 100),
            Truth("client", Client, 3, TruthEventKind.MessageReceived, 30, callId: 0, bytes: 8),
            Truth("client", Client, 4, TruthEventKind.ConnectionClosed, 40, callId: 0),
            Truth("server", Server, 1, TruthEventKind.ConnectionAccepted, 1, callId: 0),
            Truth("server", Server, 2, TruthEventKind.MessageReceived, 20, callId: 0, bytes: 100),
            Truth("server", Server, 3, TruthEventKind.MessageSent, 25, callId: 0, bytes: 8),
            Truth("server", Server, 4, TruthEventKind.ConnectionClosed, 41, callId: 0),
        ];
        UnixSocketObservation[] rows =
        [
            Row(1, 0, Client, ObservationKind.Open, socket: 1),
            Row(2, 1, Client, ObservationKind.Connect, socket: 1),
            Row(3, 10, Client, ObservationKind.Send, socket: 1, bytes: 100),
            Row(4, 29, Client, ObservationKind.Receive, socket: 1, bytes: 8),
            Row(5, 39, Client, ObservationKind.Close, socket: 1),
            Row(6, 0, Server, ObservationKind.Open, socket: 2),
            Row(7, 0, Server, ObservationKind.Bind, socket: 2),
            Row(8, 0, Server, ObservationKind.Open, socket: 3),
            Row(9, 1, Server, ObservationKind.Accept, socket: 2),

            // The server reads the message as its length and then its body, two calls the truth counts once.
            Row(10, 12, Server, ObservationKind.Receive, socket: 3, bytes: 4),
            Row(11, 19, Server, ObservationKind.Receive, socket: 3, bytes: 96),
            Row(12, 25, Server, ObservationKind.Send, socket: 3, bytes: 8),
            Row(13, 40, Server, ObservationKind.Close, socket: 3),
            Row(14, 15, Other, ObservationKind.Send, socket: 4, bytes: 50),
        ];

        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings());

        Assert.Equal(
            [("client", ObservationKind.Send, 1L, 100L), ("server", ObservationKind.Receive, 3L, 96L), ("server", ObservationKind.Send, 3L, 8L),
                ("client", ObservationKind.Receive, 1L, 8L)],
            result.Operations.Select(operation => (operation.Role, operation.ExpectedKind, operation.Socket!.Value, operation.ObservedBytes!.Value)));
        Assert.All(result.Operations, operation => Assert.Null(operation.Gap));
        MechanismMeasurement measurement = result.Measurement;
        Assert.Equal((4L, 4L, 4L, 4L, 4L), (measurement.TruthOperations, measurement.TruthOperationsWithAdmittedObservation,
            measurement.TruthOperationsBoundToResourceInstance, measurement.ByteEligibleOperations, measurement.ByteMeasuredOperations));

        // Every byte of the workload's rows is counted beside the truth's, the length read beyond it too; another
        // process's row is scope.
        Assert.Equal((108L, 108L, 108L, 108L), (result.TruthBytesSent, result.TruthBytesReceived, result.ObservedBytesSent, result.ObservedBytesReceived));
        Assert.Equal((1, 1), (result.TransfersBeyondTruth, result.ObservationsOutsideScope));

        // Each connection is a socket its process created and closed; no socket's peer is named.
        Assert.Equal([("client", 1, 1, 1), ("server", 1, 1, 1)],
            result.Processes.Select(process => (process.Role, process.TruthConnections, process.SocketsWithMatchedTransfers, process.SocketsCreatedAndClosed)));
        Assert.Equal((2L, 2L, 2L, 0L, 0L, 0L), (measurement.TruthResources, measurement.DiscoveredResourcesWithLifetime,
            measurement.ExpectedMemberships, measurement.ResolvedMemberships, measurement.PeerAttributions, measurement.FalsePeerAttributions));
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Contains("False peer attribution: not measured; the capture attributed no peer, so none could be checked.", result.Assessment.Gaps);
    }

    [Fact(DisplayName = "P27: a truth message with no row of its owner and kind inside the window is a gap, never matched by another process's row or another kind")]
    public void AMessageWithoutItsRowIsAGap()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.MessageSent, 10, callId: 0, bytes: 100),
            Truth("server", Server, 1, TruthEventKind.ConnectionAccepted, 0, callId: 0),
        ];
        UnixSocketObservation[] rows =
        [
            Row(1, 10, Other, ObservationKind.Send, socket: 4, bytes: 100),
            Row(2, 10, Client, ObservationKind.Receive, socket: 1, bytes: 100),
            Row(3, 2_000, Client, ObservationKind.Send, socket: 1, bytes: 100),

            // A send of the truth's other process at the same moment is its own, never the client's.
            Row(4, 10, Server, ObservationKind.Send, socket: 2, bytes: 100),
        ];

        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings());

        UnixSocketOperationCoverage operation = Assert.Single(result.Operations);
        Assert.Equal((0, (long?)null), (operation.MatchedObservations, operation.Socket));
        Assert.Equal("No admitted Unix-domain socket row of this kind and owner was found inside the match window.", operation.Gap);
        Assert.Equal((0L, CapabilityTier.Unsupported), (result.Measurement.TruthOperationsWithAdmittedObservation, result.Assessment.Tier));
        Assert.Equal((3, 1), (result.TransfersBeyondTruth, result.ObservationsOutsideScope));
    }

    [Fact(DisplayName = "R22: a truth connection is discovered with its lifetime only as a socket its process both created and closed in the capture")]
    public void AConnectionIsASocketCreatedAndClosed()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.ConnectionEstablished, 0, callId: 0),
            Truth("client", Client, 2, TruthEventKind.MessageSent, 10, callId: 0, bytes: 100),
            Truth("client", Client, 3, TruthEventKind.ConnectionEstablished, 20, callId: 1),
            Truth("client", Client, 4, TruthEventKind.MessageSent, 30, callId: 1, bytes: 100),
        ];
        UnixSocketObservation[] rows =
        [
            // One socket made before the capture began, which it never saw created; one created and never seen closed.
            Row(1, 10, Client, ObservationKind.Send, socket: 1, bytes: 100),
            Row(2, 20, Client, ObservationKind.Open, socket: 2),
            Row(3, 30, Client, ObservationKind.Send, socket: 2, bytes: 100),
        ];

        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings());

        Assert.Equal((2L, 2L), (result.Measurement.TruthOperations, result.Measurement.TruthOperationsWithAdmittedObservation));
        Assert.Equal([("client", 2, 2, 0)],
            result.Processes.Select(process => (process.Role, process.TruthConnections, process.SocketsWithMatchedTransfers, process.SocketsCreatedAndClosed)));
        Assert.Equal((2L, 0L), (result.Measurement.TruthResources, result.Measurement.DiscoveredResourcesWithLifetime));
    }

    [Fact(DisplayName = "R22: when a process's connections never overlap, a connection is its own socket only if all its operations landed on one socket no other connection's did")]
    public void SequentialConnectionsAreTheirOwnSockets()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.ConnectionEstablished, 0, callId: 0),
            Truth("client", Client, 2, TruthEventKind.MessageSent, 5, callId: 0, bytes: 10),
            Truth("client", Client, 3, TruthEventKind.ConnectionClosed, 9, callId: 0),
            Truth("client", Client, 4, TruthEventKind.ConnectionEstablished, 20, callId: 1),
            Truth("client", Client, 5, TruthEventKind.MessageSent, 25, callId: 1, bytes: 10),
            Truth("client", Client, 6, TruthEventKind.ConnectionClosed, 29, callId: 1),
            Truth("client", Client, 7, TruthEventKind.ConnectionEstablished, 40, callId: 2),
            Truth("client", Client, 8, TruthEventKind.MessageSent, 45, callId: 2, bytes: 10),
            Truth("client", Client, 9, TruthEventKind.ConnectionClosed, 49, callId: 2),
            Truth("client", Client, 10, TruthEventKind.ConnectionEstablished, 60, callId: 3),
            Truth("client", Client, 11, TruthEventKind.MessageSent, 62, callId: 3, bytes: 10),
            Truth("client", Client, 12, TruthEventKind.MessageSent, 65, callId: 3, bytes: 10),
            Truth("client", Client, 13, TruthEventKind.ConnectionClosed, 69, callId: 3),
        ];

        // The first two connections each their own socket; the third's row names the first's socket again, and the
        // fourth's two rows name two sockets of no other connection.
        UnixSocketObservation[] rows =
        [
            Row(1, 5, Client, ObservationKind.Send, socket: 1, bytes: 10),
            Row(2, 25, Client, ObservationKind.Send, socket: 2, bytes: 10),
            Row(3, 45, Client, ObservationKind.Send, socket: 1, bytes: 10),
            Row(4, 62, Client, ObservationKind.Send, socket: 3, bytes: 10),
            Row(5, 65, Client, ObservationKind.Send, socket: 4, bytes: 10),
        ];
        UnixSocketProcessCoverage process = Assert.Single(UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings()).Processes);
        Assert.Equal((4, 4, 1), (process.TruthConnections, process.SocketsWithMatchedTransfers, process.ConnectionsOnTheirOwnSocket));

        // Connections overlapping in time do not name an operation's connection, so the count is not known, never a guess.
        TruthRecord[] overlapping =
        [
            Truth("client", Client, 1, TruthEventKind.ConnectionEstablished, 0, callId: 0),
            Truth("client", Client, 2, TruthEventKind.ConnectionEstablished, 10, callId: 1),
            Truth("client", Client, 3, TruthEventKind.MessageSent, 15, callId: 0, bytes: 10),
            Truth("client", Client, 4, TruthEventKind.ConnectionClosed, 30, callId: 0),
            Truth("client", Client, 5, TruthEventKind.ConnectionClosed, 40, callId: 1),
        ];
        Assert.Null(Assert.Single(UnixSocketCoverageEvaluator.Evaluate(overlapping, rows, Settings()).Processes).ConnectionsOnTheirOwnSocket);
    }

    [Fact(DisplayName = "R22: a kind's operations are paired with its rows by rank only when every pair's bytes agree; otherwise each is its nearest row on the truth's time moved by the ranked pairs' median offset")]
    public void RanksPairOnlyWhereTheBytesAgree()
    {
        // Two sends the truth logged a fraction of a millisecond apart, and two rows: by rank the first would be the earlier
        // row, whose bytes are the second's, so each is matched to its nearest row instead.
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.MessageSent, 10, callId: 0, bytes: 100),
            Truth("client", Client, 2, TruthEventKind.MessageSent, 11, callId: 1, bytes: 200),
        ];
        UnixSocketObservation[] rows =
        [
            Row(1, 0, Client, ObservationKind.Send, socket: 1, bytes: 200),
            Row(2, 10, Client, ObservationKind.Send, socket: 1, bytes: 100),
        ];
        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings());
        Assert.Equal([(100L, 100L, false), (200L, 200L, false)],
            result.Operations.Select(operation => (operation.TruthCompletedBytes!.Value, operation.ObservedBytes!.Value, operation.PairedByRank)));
        Assert.Equal(0, result.OperationsPairedByRank);

        // In the same order with the same bytes they are paired by rank, whichever row is nearer.
        UnixSocketObservation[] ordered =
        [
            Row(1, 11, Client, ObservationKind.Send, socket: 1, bytes: 100),
            Row(2, 12, Client, ObservationKind.Send, socket: 1, bytes: 200),
        ];
        UnixSocketCoverageResult ranked = UnixSocketCoverageEvaluator.Evaluate(truth, ordered, Settings());
        Assert.Equal([(100L, 100L, true), (200L, 200L, true)],
            ranked.Operations.Select(operation => (operation.TruthCompletedBytes!.Value, operation.ObservedBytes!.Value, operation.PairedByRank)));
        Assert.Equal((2, TimeSpan.FromMilliseconds(1)), (ranked.OperationsPairedByRank, ranked.ClockOffset));

        // A capture's clock reading 290, 300 and 400 ms ahead at three sends paired by rank reads 300 ms ahead, their median.
        // A receive read in two calls is not paired by rank: it is its nearest row on its time moved by that offset, never
        // the row nearest its own time, which on the capture's clock came long before it.
        TruthRecord[] offset =
        [
            Truth("client", Client, 1, TruthEventKind.MessageSent, 10, callId: 0, bytes: 100),
            Truth("client", Client, 2, TruthEventKind.MessageSent, 11, callId: 0, bytes: 200),
            Truth("client", Client, 3, TruthEventKind.MessageSent, 12, callId: 0, bytes: 300),
            Truth("client", Client, 4, TruthEventKind.MessageReceived, 50, callId: 0, bytes: 8),
        ];
        UnixSocketObservation[] ahead =
        [
            Row(1, 60, Client, ObservationKind.Receive, socket: 2, bytes: 3),
            Row(2, 300, Client, ObservationKind.Send, socket: 1, bytes: 100),
            Row(3, 311, Client, ObservationKind.Send, socket: 1, bytes: 200),
            Row(4, 350, Client, ObservationKind.Receive, socket: 1, bytes: 8),
            Row(5, 412, Client, ObservationKind.Send, socket: 1, bytes: 300),
        ];
        UnixSocketCoverageResult moved = UnixSocketCoverageEvaluator.Evaluate(offset, ahead, Settings());
        Assert.Equal((3, TimeSpan.FromMilliseconds(300)), (moved.OperationsPairedByRank, moved.ClockOffset));
        UnixSocketOperationCoverage receive = Assert.Single(moved.Operations, operation => operation.ExpectedKind == ObservationKind.Receive);
        Assert.Equal((1L, 8L, false), (receive.Socket!.Value, receive.ObservedBytes!.Value, receive.PairedByRank));
        Assert.Equal(1, moved.TransfersBeyondTruth);
    }

    [Fact(DisplayName = "P27: FX-UNIX-001's rows through the product reproduce its measured tier - every truth send and receive its owner's row, bound to its socket with its bytes, and no peer named")]
    public void CommittedEvidenceReproducesItsTier()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-UNIX-001", "evidence");
        string product = Path.Combine(evidence, "product");
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(product, "truth-server.jsonl")), .. ReadTruth(Path.Combine(product, "truth-client.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);

        // The committed ledger holds the workload's two processes' rows alone, by role, never a PID (P16).
        UnixSocketObservation[] rows = [.. ReadLedger(Path.Combine(product, "rows.csv"), processes)];
        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings());

        using System.Text.Json.JsonDocument measurement = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        System.Text.Json.JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs").EnumerateArray()
            .Single(run => run.GetProperty("run").GetString() == "eight-at-once").GetProperty("evaluation");
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal(
            (stated.GetProperty("truthOperations").GetInt64(), stated.GetProperty("observed").GetInt64(), stated.GetProperty("boundToTheirSocket").GetInt64(),
                stated.GetProperty("withBytes").GetInt64(), stated.GetProperty("truthConnections").GetInt64(), stated.GetProperty("socketsCreatedAndClosed").GetInt64()),
            (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation, measured.TruthOperationsBoundToResourceInstance,
                measured.ByteMeasuredOperations, measured.TruthResources, measured.DiscoveredResourcesWithLifetime));
        Assert.Equal(
            (stated.GetProperty("truthBytesSent").GetInt64(), stated.GetProperty("observedBytesSent").GetInt64(), stated.GetProperty("truthBytesReceived").GetInt64(),
                stated.GetProperty("observedBytesReceived").GetInt64(), stated.GetProperty("transfersBeyondTruth").GetInt32()),
            (result.TruthBytesSent, result.ObservedBytesSent, result.TruthBytesReceived, result.ObservedBytesReceived, result.TransfersBeyondTruth));
        Assert.Equal(stated.GetProperty("gaps").EnumerateArray().Select(gap => gap.GetString()), result.Assessment.Gaps);
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Every one of them, exactly: each send and receive, with each byte it moved, and each connection a socket created and
        // closed; AF_UNIX names no peer, so the tier is experimental evidence.
        Assert.Equal((1024L, 1024L, 1024L, 1024L, 16L, 16L), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthOperationsBoundToResourceInstance, measured.ByteMeasuredOperations, measured.TruthResources, measured.DiscoveredResourcesWithLifetime));
        Assert.Equal((result.TruthBytesSent, result.TruthBytesReceived), (result.ObservedBytesSent, result.ObservedBytesReceived));
        Assert.Equal((0L, 0L, CapabilityTier.ExperimentalEvidence), (measured.PeerAttributions, measured.ResolvedMemberships, result.Assessment.Tier));
    }

    [Fact(DisplayName = "R22: FX-UNIX-002's rows through the product keep each of sixty-four connections made one at a time on its own socket at both ends, while the kernel reused their addresses")]
    public void ChurnedConnectionsAreTheirOwnSockets()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-UNIX-002", "evidence");
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(evidence, "truth-server.jsonl")), .. ReadTruth(Path.Combine(evidence, "truth-client.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        UnixSocketObservation[] rows = [.. ReadLedger(Path.Combine(evidence, "rows.csv"), processes)];
        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(truth, rows, Settings() with { FixtureId = "FX-UNIX-002" });

        using System.Text.Json.JsonDocument measurement = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        System.Text.Json.JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs").EnumerateArray()
            .Single(run => run.GetProperty("run").GetString() == "churn-1").GetProperty("evaluation");
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal(
            (stated.GetProperty("truthOperations").GetInt64(), stated.GetProperty("observed").GetInt64(), stated.GetProperty("withBytes").GetInt64(),
                stated.GetProperty("truthConnections").GetInt64(), stated.GetProperty("socketsCreatedAndClosed").GetInt64(), stated.GetProperty("transfersBeyondTruth").GetInt32()),
            (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation, measured.ByteMeasuredOperations,
                measured.TruthResources, measured.DiscoveredResourcesWithLifetime, result.TransfersBeyondTruth));
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Every operation its owner's row, and each connection at each end a socket of its own, created and closed, none of
        // its operations on another connection's socket.
        Assert.Equal((512L, 512L, 128L, 128L), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthResources, measured.DiscoveredResourcesWithLifetime));
        Assert.Equal([("client", 64, 64, 64), ("server", 64, 64, 64)],
            result.Processes.Select(process => (process.Role, process.TruthConnections, process.SocketsCreatedAndClosed, process.ConnectionsOnTheirOwnSocket!.Value)));
        Assert.Equal([64, 64], result.Processes.Select(process =>
            stated.GetProperty("connectionsOnTheirOwnSocket").GetProperty(process.Role).GetInt32()));

        // While the kernel created most of the churn's sockets at an address an earlier socket held, which the lab probe
        // counted: the product names a socket by its number alone.
        System.Text.Json.JsonElement reuse = measurement.RootElement.GetProperty("addressReuse");
        int sockets = reuse.GetProperty("socketsWithACreate").GetInt32();
        int reused = reuse.GetProperty("socketsCreatedAtAnAddressAnEarlierSocketHad").GetInt32();
        Assert.True(reused > sockets / 2 && reused < sockets, $"{reused} of {sockets} sockets were created at a reused address.");
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), reuse.GetProperty("bench").GetString()!, "report.json")));
    }

    private static IEnumerable<TruthRecord> ReadTruth(string path)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return File.ReadLines(path).Where(line => line.Length > 0).Select(line => System.Text.Json.JsonSerializer.Deserialize<TruthRecord>(line, options)!);
    }

    private static IEnumerable<UnixSocketObservation> ReadLedger(string path, Dictionary<string, int> processes) =>
        File.ReadLines(path).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new UnixSocketObservation
        {
            RecordOrdinal = long.Parse(field[0], System.Globalization.CultureInfo.InvariantCulture),
            TimestampUtcTicks = long.Parse(field[1], System.Globalization.CultureInfo.InvariantCulture),
            OwnerProcessId = processes[field[2]],
            Kind = Enum.Parse<ObservationKind>(field[3]),
            Socket = long.Parse(field[4], System.Globalization.CultureInfo.InvariantCulture),
            Bytes = field[5].Length == 0 ? null : long.Parse(field[5], System.Globalization.CultureInfo.InvariantCulture),
            Status = field[6].Length == 0 ? null : long.Parse(field[6], System.Globalization.CultureInfo.InvariantCulture),
        });

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    private static UnixSocketCoverageSettings Settings() => new()
    {
        FixtureId = "FX-UNIX-001",
        BuildId = "10.0.26220.0-x64",
        BuildIsSupported = true,
        Reproduced = true,
    };

    private static TruthRecord Truth(string role, int process, long sequence, TruthEventKind kind, int milliseconds,
        long? callId = null, long? bytes = null) => new()
    {
        ScenarioId = "FX-UNIX-001",
        Role = role,
        Sequence = sequence,
        Kind = kind,
        ProcessId = process,
        ProcessStartFileTime = 1,
        MonotonicTicks = milliseconds,
        RecordedUtc = Start.AddMilliseconds(milliseconds),
        CallId = callId,
        DeclaredBytes = bytes,
        CompletedBytes = bytes,
    };

    private static UnixSocketObservation Row(long ordinal, int milliseconds, int owner, ObservationKind kind, long socket, long? bytes = null) => new()
    {
        RecordOrdinal = ordinal,
        TimestampUtcTicks = Start.AddMilliseconds(milliseconds).UtcTicks,
        OwnerProcessId = owner,
        Kind = kind,
        Socket = socket,
        Bytes = bytes,
        Status = 0,
    };
}
