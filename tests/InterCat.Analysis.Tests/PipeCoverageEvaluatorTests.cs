using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

public sealed class PipeCoverageEvaluatorTests
{
    private const string PipePath = @"\\.\pipe\intercat-fx-pipe-test";
    private const int ClientProcessId = 1001;
    private static readonly DateTimeOffset Origin = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private static PipeCoverageSettings Settings() => new()
    {
        PipePath = PipePath,
        FixtureId = "FX-PIPE-001",
        BuildId = "10.0.26100.0-x64",
        BuildIsSupported = true,
        Reproduced = true,
    };

    private static TruthRecord Truth(TruthEventKind kind, long callId, long declared, long completed, int offsetMs) => new()
    {
        ScenarioId = "FX-PIPE-001",
        Role = "client",
        Sequence = callId,
        Kind = kind,
        ProcessId = ClientProcessId,
        ProcessStartFileTime = 1,
        MonotonicTicks = offsetMs,
        RecordedUtc = Origin.AddMilliseconds(offsetMs),
        CallId = callId,
        ResourceName = PipePath,
        DeclaredBytes = declared,
        CompletedBytes = completed,
    };

    private static PipeOperationObservation Observation(
        ObservationKind kind,
        ulong ordinal,
        int offsetMs,
        ulong irp = 0,
        ulong fileObject = 0,
        string? name = null,
        long? requested = null,
        long? completed = null,
        int processId = ClientProcessId) => new()
    {
        Id = new(new(new(Guid.Empty), 1, 1, ordinal), 0),
        Kind = kind,
        Direction = Direction.DirectionNotApplicable,
        TimestampUtcTicks = Origin.AddMilliseconds(offsetMs).UtcTicks,
        SourceTicks = offsetMs,
        ProcessId = processId,
        IrpKey = irp,
        FileObject = fileObject,
        ResourceName = name,
        RequestedBytes = requested,
        CompletedBytes = completed,
        AttributionQuality = QualityLevel.Qualified,
    };

    [Fact(DisplayName = "P3: requested and completed pipe bytes stay in separate domains")]
    public void RequestedAndCompletedBytesStaySeparate()
    {
        List<TruthRecord> truth = [Truth(TruthEventKind.MessageReceived, 1, 4_096, 1_024, 10)];
        List<PipeOperationObservation> observations =
        [
            Observation(ObservationKind.Open, 1, 1, fileObject: 0xAA, name: PipePath),
            Observation(ObservationKind.Receive, 2, 10, irp: 0xF1, fileObject: 0xAA, requested: 4_096),
            Observation(ObservationKind.RequestEnd, 3, 11, irp: 0xF1, completed: 1_024),
            Observation(ObservationKind.Close, 4, 20, fileObject: 0xAA),
        ];

        PipeCoverageResult result = PipeCoverageEvaluator.Evaluate(truth, observations, Settings());

        PipeOperationCoverage operation = Assert.Single(result.Operations);
        Assert.Equal(4_096, operation.ObservedRequestedBytes);
        Assert.Equal(1_024, operation.ObservedCompletedBytes);
        Assert.Equal(1, result.OperationsWithRequestedAboveCompleted);
        Assert.Equal(4_096, result.ObservedRequestedBytes);
        Assert.Equal(1_024, result.ObservedCompletedBytes);
    }

    [Fact(DisplayName = "P8: a completion is paired by I/O request identity, not by time proximity")]
    public void CompletionPairsByRequestIdentity()
    {
        List<TruthRecord> truth = [Truth(TruthEventKind.MessageReceived, 1, 512, 512, 10)];
        List<PipeOperationObservation> observations =
        [
            Observation(ObservationKind.Open, 1, 1, fileObject: 0xAA, name: PipePath),
            Observation(ObservationKind.Receive, 2, 10, irp: 0xF1, fileObject: 0xAA, requested: 512),

            // A nearer completion that belongs to a different request must not be adopted.
            Observation(ObservationKind.RequestEnd, 3, 10, irp: 0x99, completed: 77),
            Observation(ObservationKind.RequestEnd, 4, 30, irp: 0xF1, completed: 512),
        ];

        PipeCoverageResult result = PipeCoverageEvaluator.Evaluate(truth, observations, Settings());

        PipeOperationCoverage operation = Assert.Single(result.Operations);
        Assert.Equal(512, operation.ObservedCompletedBytes);
    }

    [Fact(DisplayName = "R3: an operation without a paired completion keeps completed bytes unknown")]
    public void MissingCompletionStaysUnknown()
    {
        List<TruthRecord> truth = [Truth(TruthEventKind.MessageReceived, 1, 512, 512, 10)];
        List<PipeOperationObservation> observations =
        [
            Observation(ObservationKind.Open, 1, 1, fileObject: 0xAA, name: PipePath),
            Observation(ObservationKind.Receive, 2, 10, irp: 0xF1, fileObject: 0xAA, requested: 512),
        ];

        PipeCoverageResult result = PipeCoverageEvaluator.Evaluate(truth, observations, Settings());

        PipeOperationCoverage operation = Assert.Single(result.Operations);
        Assert.Null(operation.ObservedCompletedBytes);
        Assert.Equal(1, result.MatchedOperationsWithoutCompletion);
        Assert.Equal(0, result.ObservedCompletedBytes);
    }

    [Fact(DisplayName = "R22: an operation on an unobserved create is left unresolved, not matched by name")]
    public void UnobservedCreateLeavesOperationUnresolved()
    {
        List<TruthRecord> truth = [Truth(TruthEventKind.MessageReceived, 1, 512, 512, 10)];
        List<PipeOperationObservation> observations =
        [
            Observation(ObservationKind.Receive, 1, 10, irp: 0xF1, fileObject: 0xAA, requested: 512),
        ];

        PipeCoverageResult result = PipeCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(1, result.ObservationsWithUnresolvedResource);
        Assert.Equal(1, result.FixtureProcessOperationsWithoutResource);
        Assert.Equal(0, result.Measurement.TruthOperationsWithAdmittedObservation);
        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);
    }

    [Fact(DisplayName = "R21: a negative result records the control that proves the source was live")]
    public void NegativeResultCarriesItsControl()
    {
        List<TruthRecord> truth = [Truth(TruthEventKind.MessageSent, 1, 512, 512, 10)];
        List<PipeOperationObservation> observations =
        [
            Observation(ObservationKind.Open, 1, 1, fileObject: 0xBB, name: @"\Device\HarddiskVolume1\other.log"),
            Observation(ObservationKind.Send, 2, 5, irp: 0xE1, fileObject: 0xBB, requested: 64),
            Observation(ObservationKind.RequestEnd, 3, 6, irp: 0xE1, completed: 64),
        ];

        PipeCoverageResult result = PipeCoverageEvaluator.Evaluate(truth, observations, Settings());

        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);
        Assert.Equal(3, result.ControlRecordsFromFixtureProcesses);
        Assert.Equal(1, result.ObservationsOutsideScope);
        Assert.Contains(result.Notes, note => note.StartsWith("Control:", StringComparison.Ordinal));
        Assert.Contains(
            result.Assessment.Gaps,
            gap => gap.Contains("No admitted observation", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "P7: a pipe peer pairing is checked against the truth log rather than assumed")]
    public void PeerPairingIsCheckedAgainstTruth()
    {
        const int serverProcessId = 2002;
        List<TruthRecord> truth =
        [
            Truth(TruthEventKind.MessageSent, 1, 512, 512, 10),
            Truth(TruthEventKind.MessageReceived, 2, 512, 512, 12) with { ProcessId = serverProcessId, Role = "server" },
        ];
        List<PipeOperationObservation> observations =
        [
            Observation(ObservationKind.Open, 1, 1, fileObject: 0xAA, name: PipePath),
            Observation(ObservationKind.Send, 2, 10, irp: 0xF1, fileObject: 0xAA, requested: 512),
            Observation(ObservationKind.RequestEnd, 3, 11, irp: 0xF1, completed: 512),
            Observation(ObservationKind.Open, 4, 2, fileObject: 0xAB, name: PipePath, processId: serverProcessId),
            Observation(ObservationKind.Receive, 5, 12, irp: 0xF2, fileObject: 0xAB, requested: 512, processId: serverProcessId),
            Observation(ObservationKind.RequestEnd, 6, 13, irp: 0xF2, completed: 512, processId: serverProcessId),
            Observation(ObservationKind.Close, 7, 20, fileObject: 0xAA),
        ];

        PipeCoverageResult result = PipeCoverageEvaluator.Evaluate(truth, observations, Settings());

        PipePeerAttributionCheck peer = Assert.Single(result.PeerAttributions);
        Assert.True(peer.AgreesWithTruth);
        Assert.Equal(0, result.Measurement.FalsePeerAttributions);
        Assert.Equal(2, result.Measurement.TruthOperationsWithAdmittedObservation);
        Assert.Equal(2, result.Measurement.ResolvedMemberships);
    }
}
