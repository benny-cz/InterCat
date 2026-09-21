using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

/// <summary>
/// The coverage evidence a series result keeps. A level can evaluate tens of thousands of truth
/// operations, and writing every one into the artifact would bury the counters that decide the tier in
/// megabytes of rows that all say the same thing. The counters, the criteria and every byte total are
/// kept whole; the per-operation rows are reduced to the ones that failed, bounded and counted, so a gap
/// is still readable and a clean level is still provably clean (section 13.6, R21).
/// </summary>
internal sealed record CoverageSummary
{
    /// <summary>How many failed rows one level keeps. The count of failures is always exact.</summary>
    public const int SampleLimit = 16;

    public required MechanismMeasurement Measurement { get; init; }
    public required TierAssessment Assessment { get; init; }
    public required long TruthBytesSent { get; init; }
    public required long TruthBytesReceived { get; init; }
    public required long ObservedBytesSent { get; init; }
    public required long ObservedBytesReceived { get; init; }
    public required int ObservationsOutsideScope { get; init; }

    public required int TruthOperations { get; init; }
    public required int OperationsWithGaps { get; init; }
    public required IReadOnlyList<OperationCoverage> SampledOperationGaps { get; init; }

    public required int PeerAttributions { get; init; }
    public required int FalsePeerAttributions { get; init; }
    public required IReadOnlyList<PeerAttributionCheck> SampledFalsePeers { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public static CoverageSummary From(TcpCoverageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<OperationCoverage> gaps = [.. result.Operations.Where(operation => operation.Gap is not null)];
        List<PeerAttributionCheck> falsePeers = [.. result.PeerAttributions.Where(peer => !peer.AgreesWithTruth)];
        return new()
        {
            Measurement = result.Measurement,
            Assessment = result.Assessment,
            TruthBytesSent = result.TruthBytesSent,
            TruthBytesReceived = result.TruthBytesReceived,
            ObservedBytesSent = result.ObservedBytesSent,
            ObservedBytesReceived = result.ObservedBytesReceived,
            ObservationsOutsideScope = result.ObservationsOutsideScope,
            TruthOperations = result.Operations.Count,
            OperationsWithGaps = gaps.Count,
            SampledOperationGaps = [.. gaps.Take(SampleLimit)],
            PeerAttributions = result.PeerAttributions.Count,
            FalsePeerAttributions = falsePeers.Count,
            SampledFalsePeers = [.. falsePeers.Take(SampleLimit)],
            Notes = result.Notes,
        };
    }
}
