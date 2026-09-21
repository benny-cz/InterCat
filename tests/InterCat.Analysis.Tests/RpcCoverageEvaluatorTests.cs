using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

public sealed class RpcCoverageEvaluatorTests
{
    private static readonly Guid Interface = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
    private static readonly Guid OtherInterface = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Origin = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private const int ClientProcessId = 4001;
    private const int ServerProcessId = 1960;

    private static RpcCoverageSettings Settings(bool reproduced = true) => new()
    {
        ExpectedInterface = Interface,
        ClientProcessId = ClientProcessId,
        ExpectedServerProcessId = ServerProcessId,
        FixtureId = "FX-RPC-001",
        BuildId = "10.0.26100.0-x64",
        BuildIsSupported = true,
        Reproduced = reproduced,
    };

    private static TruthRecord Call(long callId, int offsetMs) => new()
    {
        ScenarioId = "FX-RPC-001",
        Role = "client",
        Sequence = callId,
        Kind = TruthEventKind.CallIssued,
        ProcessId = ClientProcessId,
        ProcessStartFileTime = 1,
        MonotonicTicks = offsetMs,
        RecordedUtc = Origin.AddMilliseconds(offsetMs),
        CallId = callId,
        ResourceName = Interface.ToString(),
    };

    private static RpcCallObservation Observation(
        ObservationKind kind,
        ulong ordinal,
        int offsetMs,
        Guid activityId,
        Guid? interfaceUuid = null,
        long? status = null,
        int processId = ClientProcessId,
        Direction direction = Direction.Outbound) => new()
    {
        Id = new(new(new(Guid.Empty), 1, 1, ordinal), 0),
        Kind = kind,
        Direction = direction,
        TimestampUtcTicks = Origin.AddMilliseconds(offsetMs).UtcTicks,
        SourceTicks = offsetMs,
        ProcessId = processId,
        ActivityId = activityId,
        InterfaceUuid = interfaceUuid,
        Status = status,
        AttributionQuality = QualityLevel.Qualified,
    };

    [Fact(DisplayName = "P2: an RPC call never reports a byte value, and the byte criterion stays not measured")]
    public void RpcNeverReportsBytes()
    {
        Guid activity = Guid.NewGuid();
        List<TruthRecord> truth = [Call(1, 10)];
        List<RpcCallObservation> observations =
        [
            Observation(ObservationKind.RequestStart, 1, 10, activity, Interface),
            Observation(ObservationKind.RequestEnd, 2, 12, activity, status: 0),
        ];

        RpcCoverageResult result = RpcCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(0, result.Measurement.ByteEligibleOperations);
        Assert.Equal(0, result.Measurement.ByteMeasuredOperations);
        TierCriterion bytes = result.Assessment.Criteria.Single(criterion => criterion.Name == "ByteMeasurement");
        Assert.Null(bytes.Measured);
        Assert.False(bytes.Satisfied);
        Assert.NotEqual(CapabilityTier.TrafficVisualization, result.Assessment.Tier);
    }

    [Fact(DisplayName = "P8: a completion is paired by activity id, and an unpaired call keeps its status unknown")]
    public void CompletionPairsByActivity()
    {
        Guid activity = Guid.NewGuid();
        List<TruthRecord> truth = [Call(1, 10), Call(2, 100)];
        List<RpcCallObservation> observations =
        [
            Observation(ObservationKind.RequestStart, 1, 10, activity, Interface),
            Observation(ObservationKind.RequestEnd, 2, 11, Guid.NewGuid(), status: 5),
            Observation(ObservationKind.RequestStart, 3, 100, Guid.NewGuid(), Interface),
        ];

        RpcCoverageResult result = RpcCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(2, result.Measurement.TruthOperationsWithAdmittedObservation);
        Assert.Equal(0, result.CompletionsPaired);
        Assert.All(result.Calls, call => Assert.Null(call.Status));
    }

    [Fact(DisplayName = "R22: a call to another interface is scope, not a match")]
    public void OtherInterfaceIsOutOfScope()
    {
        List<TruthRecord> truth = [Call(1, 10)];
        List<RpcCallObservation> observations =
        [
            Observation(ObservationKind.RequestStart, 1, 10, Guid.NewGuid(), OtherInterface),
        ];

        RpcCoverageResult result = RpcCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(1, result.ObservationsOutsideScope);
        Assert.Equal(0, result.Measurement.TruthOperationsWithAdmittedObservation);
        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);
    }

    [Fact(DisplayName = "P7: a peer is claimed only when the two sides share an activity, and is checked against truth")]
    public void PeerIsOnlyClaimedWhenTheActivityLinksThem()
    {
        Guid shared = Guid.NewGuid();
        List<TruthRecord> truth = [Call(1, 10)];
        List<RpcCallObservation> linked =
        [
            Observation(ObservationKind.RequestStart, 1, 10, shared, Interface),
            Observation(
                ObservationKind.RequestStart,
                2,
                11,
                shared,
                Interface,
                processId: ServerProcessId,
                direction: Direction.Inbound),
        ];

        RpcCoverageResult paired = RpcCoverageEvaluator.Evaluate(truth, linked, Settings());

        RpcPeerAttributionCheck peer = Assert.Single(paired.PeerAttributions);
        Assert.True(peer.AgreesWithTruth);
        Assert.Equal(ServerProcessId, peer.ServerProcessId);

        List<RpcCallObservation> unlinked =
        [
            Observation(ObservationKind.RequestStart, 1, 10, Guid.NewGuid(), Interface),
            Observation(
                ObservationKind.RequestStart,
                2,
                11,
                Guid.NewGuid(),
                Interface,
                processId: ServerProcessId,
                direction: Direction.Inbound),
        ];

        RpcCoverageResult unpaired = RpcCoverageEvaluator.Evaluate(truth, unlinked, Settings());

        Assert.Empty(unpaired.PeerAttributions);
        Assert.Contains(
            unpaired.Notes,
            note => note.Contains("left unresolved rather than inferred", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "P27: observed calls without a byte domain or a lifetime stay at experimental evidence")]
    public void ObservedCallsStayExperimental()
    {
        Guid activity = Guid.NewGuid();
        List<TruthRecord> truth = [Call(1, 10)];
        List<RpcCallObservation> observations =
        [
            Observation(ObservationKind.RequestStart, 1, 10, activity, Interface),
            Observation(ObservationKind.RequestEnd, 2, 12, activity, status: 0),
        ];

        RpcCoverageResult result = RpcCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Contains(result.Assessment.Gaps, gap => gap.Contains("ByteMeasurement", StringComparison.Ordinal));
        Assert.Contains(result.Assessment.Gaps, gap => gap.Contains("ResourceDiscovery", StringComparison.Ordinal));
    }
}
