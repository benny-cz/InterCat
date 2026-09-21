using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class CoverageTierCalculatorTests
{
    private static MechanismMeasurement FullyMeasured(bool supportedBuild = true) => new()
    {
        FixtureId = "FX-TCP-001",
        BuildId = "10.0.26100.0-x64",
        Reproduced = true,
        MeasuredOnSupportedBuild = supportedBuild,
        TruthOperations = 100,
        TruthOperationsWithAdmittedObservation = 100,
        TruthOperationsBoundToResourceInstance = 95,
        PeerAttributions = 100,
        FalsePeerAttributions = 0,
        ByteEligibleOperations = 100,
        ByteMeasuredOperations = 95,
        TruthResources = 4,
        DiscoveredResourcesWithLifetime = 4,
        ExpectedMemberships = 8,
        ResolvedMemberships = 8,
        ClaimsBytesOrOperations = true,
    };

    [Fact(DisplayName = "P27: every section 14.2 threshold met on a supported build yields traffic visualization")]
    public void FullMeasurementReachesTrafficVisualization()
    {
        TierAssessment assessment = CoverageTierCalculator.Assess(FullyMeasured());

        Assert.Equal(CapabilityTier.TrafficVisualization, assessment.Tier);
        Assert.Empty(assessment.Gaps);
    }

    [Fact(DisplayName = "P27: a measurement on an untested build cannot promote past experimental evidence")]
    public void UnsupportedBuildCapsTheTier()
    {
        TierAssessment assessment = CoverageTierCalculator.Assess(FullyMeasured(supportedBuild: false));

        Assert.Equal(CapabilityTier.ExperimentalEvidence, assessment.Tier);
        Assert.Contains(assessment.Gaps, gap => gap.Contains("not one of the supported builds", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R3: an absent denominator is reported as not measured rather than as a pass")]
    public void MissingDenominatorIsNotAPass()
    {
        MechanismMeasurement measurement = FullyMeasured() with
        {
            TruthOperations = 0,
            TruthOperationsWithAdmittedObservation = 0,
            TruthOperationsBoundToResourceInstance = 0,
            ByteEligibleOperations = 0,
            ByteMeasuredOperations = 0,
        };

        TierAssessment assessment = CoverageTierCalculator.Assess(measurement);

        TierCriterion admitted = assessment.Criteria.Single(criterion => criterion.Name == "AdmittedObservations");
        Assert.Null(admitted.Measured);
        Assert.False(admitted.Satisfied);
        Assert.NotEqual(CapabilityTier.TrafficVisualization, assessment.Tier);
    }

    [Fact(DisplayName = "P27: discovered resources without a byte claim are topology only")]
    public void TopologyWithoutByteClaimIsTopologyOnly()
    {
        MechanismMeasurement measurement = FullyMeasured() with
        {
            TruthOperations = 10,
            TruthOperationsWithAdmittedObservation = 0,
            TruthOperationsBoundToResourceInstance = 0,
            ByteEligibleOperations = 10,
            ByteMeasuredOperations = 0,
            ClaimsBytesOrOperations = false,
        };

        TierAssessment assessment = CoverageTierCalculator.Assess(measurement);

        Assert.Equal(CapabilityTier.TopologyOnly, assessment.Tier);
    }

    [Fact(DisplayName = "P27: a source that registers and enables but emits nothing stays unsupported")]
    public void SilentSourceStaysUnsupported()
    {
        var measurement = new MechanismMeasurement
        {
            FixtureId = "FX-PIPE-001",
            BuildId = "10.0.26100.0-x64",
            Reproduced = true,
            MeasuredOnSupportedBuild = true,
            TruthOperations = 20,
            ByteEligibleOperations = 20,
            TruthResources = 2,
            ExpectedMemberships = 4,
            ClaimsBytesOrOperations = true,
        };

        TierAssessment assessment = CoverageTierCalculator.Assess(measurement);

        Assert.Equal(CapabilityTier.Unsupported, assessment.Tier);
        Assert.Contains(assessment.Gaps, gap => gap.Contains("No admitted observation", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "P27: false peer attribution above one percent blocks traffic visualization")]
    public void FalsePeersBlockPromotion()
    {
        MechanismMeasurement measurement = FullyMeasured() with { FalsePeerAttributions = 5 };

        TierAssessment assessment = CoverageTierCalculator.Assess(measurement);

        Assert.NotEqual(CapabilityTier.TrafficVisualization, assessment.Tier);
        Assert.Contains(assessment.Gaps, gap => gap.Contains("FalsePeerAttribution", StringComparison.Ordinal));
    }
}
