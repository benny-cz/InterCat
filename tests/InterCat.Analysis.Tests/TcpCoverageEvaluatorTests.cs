using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

public sealed class TcpCoverageEvaluatorTests
{
    private const uint Loopback = 0x7F00_0001;

    [Fact(DisplayName = "I14: one admitted record cannot satisfy two truth operations")]
    public void ObservationMatchingIsOneToOne()
    {
        TruthRecord[] truth =
        [
            Truth(1, recordedTicks: 10_000, callId: 1),
            Truth(2, recordedTicks: 10_100, callId: 2),
        ];
        NetworkTransferObservation[] observations = [Observation(1, timestampTicks: 10_050)];

        TcpCoverageResult result = TcpCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(2, result.Measurement.TruthOperations);
        Assert.Equal(1, result.Measurement.TruthOperationsWithAdmittedObservation);
        Assert.Single(result.Operations, operation => operation.MatchedObservations == 1);
        Assert.Single(result.Operations, operation => operation.MatchedObservations == 0);
    }

    [Fact(DisplayName = "P16: persisted fixture evidence excludes unrelated whole-machine endpoints")]
    public void FixtureEvidenceContainsOnlyTruthFlows()
    {
        TruthRecord[] truth = [Truth(1, recordedTicks: 10_000, callId: 1)];
        NetworkTransferObservation relevant = Observation(1, timestampTicks: 10_000);
        NetworkTransferObservation unrelated = Observation(
            2,
            timestampTicks: 10_000,
            flow: new(0xC0A8_0102, 55_000, 0x0808_0808, 443));

        IReadOnlyList<NetworkTransferObservation> scoped =
            TcpCoverageEvaluator.SelectFixtureEvidence(truth, [relevant, unrelated], Settings());

        Assert.Equal(relevant, Assert.Single(scoped));
    }

    [Fact(DisplayName = "R3: a matched observation without bytes stays unknown")]
    public void UnknownByteMeasurementIsNotConvertedToZero()
    {
        TruthRecord[] truth = [Truth(1, recordedTicks: 10_000, callId: 1)];
        NetworkTransferObservation observation = Observation(1, timestampTicks: 10_000) with
        {
            ByteCount = null,
            ByteDomain = null,
        };

        TcpCoverageResult result = TcpCoverageEvaluator.Evaluate(truth, [observation], Settings());

        OperationCoverage operation = Assert.Single(result.Operations);
        Assert.Equal(1, operation.MatchedObservations);
        Assert.Null(operation.ObservedBytes);
        Assert.Equal(0, result.Measurement.ByteMeasuredOperations);
    }

    [Fact(DisplayName = "P27: an operation seen only with its endpoints mirrored is an orientation gap, not a miss")]
    public void MirroredObservationsNameTheOrientationAsTheGap()
    {
        TruthRecord[] truth = [Truth(1, recordedTicks: 10_000, callId: 1)];
        NetworkTransferObservation mirrored = Observation(1, timestampTicks: 10_000, flow: new(Loopback, 40_000, Loopback, 50_000));

        TcpCoverageResult result = TcpCoverageEvaluator.Evaluate(truth, [mirrored], Settings());

        OperationCoverage operation = Assert.Single(result.Operations);
        Assert.Equal(0, operation.MatchedObservations);
        Assert.Contains("mirrored", operation.Gap, StringComparison.Ordinal);
    }

    /// <summary>
    /// The committed, fixture-scoped evidence of each measured transport re-evaluates to the counters it was published
    /// with: a change to matching or orientation that would move a tier fails here rather than in the next capture.
    /// </summary>
    [Theory(DisplayName = "P27: committed transport fixture evidence reproduces its measured tier")]
    [InlineData("FX-TCP-001", 48, 4)]
    [InlineData("FX-UDP-001", 64, 4)]
    public void CommittedEvidenceReproducesItsTier(string fixture, int operations, int peers)
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", fixture, "evidence");
        TruthRecord[] truth = ReadLines<TruthRecord>(Path.Combine(evidence, "truth.jsonl"));
        NetworkTransferObservation[] observations = ReadLines<NetworkTransferObservation>(Path.Combine(evidence, "observations.jsonl"));

        TcpCoverageResult result = TcpCoverageEvaluator.Evaluate(truth, observations, new()
        {
            FixtureId = fixture,
            BuildId = "10.0.26220.0-x64",
            BuildIsSupported = true,
            Reproduced = true,
        });

        Assert.Equal(operations, result.Measurement.TruthOperations);
        Assert.Equal(operations, result.Measurement.TruthOperationsWithAdmittedObservation);
        Assert.Equal(operations, result.Measurement.TruthOperationsBoundToResourceInstance);
        Assert.Equal(operations, result.Measurement.ByteMeasuredOperations);
        Assert.Equal((peers, 0L), (result.Measurement.PeerAttributions, result.Measurement.FalsePeerAttributions));
        Assert.Equal(result.TruthBytesSent, result.ObservedBytesSent);
        Assert.Equal(CapabilityTier.TrafficVisualization, result.Assessment.Tier);
    }

    private static T[] ReadLines<T>(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return
        [
            .. File.ReadAllLines(path)
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<T>(line, options)!),
        ];
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static TcpCoverageSettings Settings() => new()
    {
        FixtureId = "FX-TCP-TEST",
        BuildId = "test-build",
        BuildIsSupported = false,
        MatchWindow = TimeSpan.FromTicks(500),
    };

    private static TruthRecord Truth(long sequence, long recordedTicks, long callId) => new()
    {
        ScenarioId = "FX-TCP-TEST",
        Role = "client",
        Sequence = sequence,
        Kind = TruthEventKind.MessageSent,
        ProcessId = 42,
        ProcessStartFileTime = 123,
        MonotonicTicks = recordedTicks,
        RecordedUtc = new(recordedTicks, TimeSpan.Zero),
        CallId = callId,
        LocalPort = 50_000,
        RemotePort = 40_000,
        DeclaredBytes = 100,
        CompletedBytes = 100,
    };

    private static NetworkTransferObservation Observation(
        ulong ordinal,
        long timestampTicks,
        FlowKey? flow = null) => new()
    {
        Id = new(new(new(new Guid("08ccf60c-4c45-4793-b646-b1cf26fe691a")), 1, 1, ordinal), 0),
        Mechanism = Mechanism.Tcp,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        TimestampUtcTicks = timestampTicks,
        SourceTicks = timestampTicks,
        OwnerProcessId = 42,
        HeaderProcessId = 4,
        SourceAddress = Loopback,
        SourcePort = 50_000,
        DestinationAddress = Loopback,
        DestinationPort = 40_000,
        Flow = flow ?? new(Loopback, 50_000, Loopback, 40_000),
        ByteCount = 100,
        ByteDomain = ByteDomain.TransportObserved,
        AttributionQuality = QualityLevel.Proven,
    };
}
