namespace InterCat.Domain;

/// <summary>
/// Counters measured against an independent truth workload (§13.1). Every denominator is explicit so
/// a tier is computed rather than argued (§14.2, P27). A zero denominator is unknown, never a pass (R3).
/// </summary>
public sealed record MechanismMeasurement
{
    public required string FixtureId { get; init; }
    public required string BuildId { get; init; }

    /// <summary>True only when the same fixture produced observations on more than one run.</summary>
    public required bool Reproduced { get; init; }

    /// <summary>
    /// Whether <see cref="BuildId"/> is one of the supported builds of section 1.3. A measurement taken
    /// on an untested build can never promote a mechanism past experimental evidence (section 14.2, P27).
    /// </summary>
    public required bool MeasuredOnSupportedBuild { get; init; }

    public long TruthOperations { get; init; }
    public long TruthOperationsWithAdmittedObservation { get; init; }
    public long TruthOperationsBoundToResourceInstance { get; init; }
    public long PeerAttributions { get; init; }
    public long FalsePeerAttributions { get; init; }
    public long ByteEligibleOperations { get; init; }
    public long ByteMeasuredOperations { get; init; }

    public long TruthResources { get; init; }
    public long DiscoveredResourcesWithLifetime { get; init; }
    public long ExpectedMemberships { get; init; }
    public long ResolvedMemberships { get; init; }

    /// <summary>Whether the adapter presents byte or operation values for this mechanism at all.</summary>
    public bool ClaimsBytesOrOperations { get; init; }
}

/// <summary>One §14.2 threshold, with its denominator kept visible.</summary>
public sealed record TierCriterion(
    string Name,
    string Requirement,
    long Numerator,
    long Denominator,
    decimal? Measured,
    bool Satisfied,
    string? Gap = null);

/// <summary>The computed tier and the evidence for every threshold that decided it.</summary>
public sealed record TierAssessment(
    CapabilityTier Tier,
    IReadOnlyList<TierCriterion> Criteria,
    IReadOnlyList<string> Gaps);

/// <summary>Computes §14.2 coverage tiers from measured fixture counters only.</summary>
public static class CoverageTierCalculator
{
    public const decimal AdmittedObservationThreshold = 0.95m;
    public const decimal ResourceBindingThreshold = 0.90m;
    public const decimal FalsePeerCeiling = 0.01m;
    public const decimal ByteMeasurementThreshold = 0.90m;
    public const decimal ResourceDiscoveryThreshold = 0.95m;

    public static TierAssessment Assess(MechanismMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        TierCriterion admitted = AtLeast(
            "AdmittedObservations",
            "at least 95% of truth operations produce an admitted observation",
            measurement.TruthOperationsWithAdmittedObservation,
            measurement.TruthOperations,
            AdmittedObservationThreshold);

        TierCriterion bound = AtLeast(
            "ResourceInstanceBinding",
            "at least 90% of truth operations bind to a specific resource instance",
            measurement.TruthOperationsBoundToResourceInstance,
            measurement.TruthOperations,
            ResourceBindingThreshold);

        TierCriterion falsePeers = AtMost(
            "FalsePeerAttribution",
            "at most 1% false peer attributions",
            measurement.FalsePeerAttributions,
            measurement.PeerAttributions,
            FalsePeerCeiling);

        TierCriterion bytes = AtLeast(
            "ByteMeasurement",
            "a byte measurement in a named domain on at least 90% of eligible operations",
            measurement.ByteMeasuredOperations,
            measurement.ByteEligibleOperations,
            ByteMeasurementThreshold);

        TierCriterion resources = AtLeast(
            "ResourceDiscovery",
            "at least 95% of truth resources discovered with their lifetimes",
            measurement.DiscoveredResourcesWithLifetime,
            measurement.TruthResources,
            ResourceDiscoveryThreshold);

        TierCriterion memberships = AtLeast(
            "MembershipResolution",
            "memberships or endpoints resolved for every discovered resource",
            measurement.ResolvedMemberships,
            measurement.ExpectedMemberships,
            1.0m);

        var criteria = new List<TierCriterion>
        {
            admitted, bound, falsePeers, bytes, resources, memberships,
        };

        bool traffic = admitted.Satisfied && bound.Satisfied && falsePeers.Satisfied && bytes.Satisfied;
        bool topology = resources.Satisfied && memberships.Satisfied && !measurement.ClaimsBytesOrOperations;
        bool anyObservation =
            measurement.TruthOperationsWithAdmittedObservation > 0 || measurement.DiscoveredResourcesWithLifetime > 0;

        CapabilityTier tier;
        if (traffic)
        {
            tier = CapabilityTier.TrafficVisualization;
        }
        else if (topology)
        {
            tier = CapabilityTier.TopologyOnly;
        }
        else if (anyObservation && measurement.Reproduced)
        {
            tier = CapabilityTier.ExperimentalEvidence;
        }
        else
        {
            tier = CapabilityTier.Unsupported;
        }

        var gaps = new List<string>();
        foreach (TierCriterion criterion in criteria)
        {
            if (criterion.Gap is not null)
            {
                gaps.Add(criterion.Gap);
            }
        }

        if (tier != CapabilityTier.TrafficVisualization && !anyObservation)
        {
            gaps.Add("No admitted observation and no discovered resource was measured for this mechanism.");
        }

        if (tier == CapabilityTier.ExperimentalEvidence && !measurement.Reproduced)
        {
            gaps.Add("Observations were not reproduced on a second run of the same fixture.");
        }

        if (!measurement.MeasuredOnSupportedBuild && tier < CapabilityTier.ExperimentalEvidence)
        {
            gaps.Add(
                $"Build {measurement.BuildId} is not one of the supported builds of section 1.3, so the measured "
                + "thresholds cannot promote this mechanism beyond experimental evidence.");
            tier = CapabilityTier.ExperimentalEvidence;
        }

        return new(tier, criteria, gaps);
    }

    private static TierCriterion AtLeast(
        string name,
        string requirement,
        long numerator,
        long denominator,
        decimal threshold)
    {
        if (denominator <= 0)
        {
            return new(
                name,
                requirement,
                numerator,
                denominator,
                null,
                false,
                $"{name}: not measured; the truth workload declared no eligible item for this criterion.");
        }

        decimal measured = (decimal)numerator / denominator;
        bool satisfied = measured >= threshold;
        return new(
            name,
            requirement,
            numerator,
            denominator,
            measured,
            satisfied,
            satisfied
                ? null
                : $"{name}: measured {measured:P1} of {denominator}, below the required {threshold:P0}.");
    }

    private static TierCriterion AtMost(
        string name,
        string requirement,
        long numerator,
        long denominator,
        decimal ceiling)
    {
        if (denominator <= 0)
        {
            return new(
                name,
                requirement,
                numerator,
                denominator,
                null,
                numerator == 0,
                numerator == 0
                    ? null
                    : $"{name}: {numerator} false attributions were recorded without any attribution denominator.");
        }

        decimal measured = (decimal)numerator / denominator;
        bool satisfied = measured <= ceiling;
        return new(
            name,
            requirement,
            numerator,
            denominator,
            measured,
            satisfied,
            satisfied
                ? null
                : $"{name}: measured {measured:P1} of {denominator}, above the permitted {ceiling:P0}.");
    }
}
