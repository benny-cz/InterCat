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
