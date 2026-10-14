using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// A measured mechanism's coverage as icat states it wherever it computes one, icat measure and icat verify alike (R5,
/// R18): each §14.2 criterion in words with what it counted, the tier computed from them, and every gap that kept a
/// criterion from being met or measured.
/// </summary>
internal static class CoverageReport
{
    /// <summary>A TCP or UDP run's counters against its truth log, then its assessment.</summary>
    public static void Render(TcpCoverageResult coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ConsoleUi.Heading("Measured coverage against the truth log");
        MechanismMeasurement measurement = coverage.Measurement;
        ConsoleUi.Field("Truth operations", ConsoleUi.Count(measurement.TruthOperations));
        ConsoleUi.Field("With observation", ConsoleUi.Count(measurement.TruthOperationsWithAdmittedObservation));
        ConsoleUi.Field("Bound to a flow", ConsoleUi.Count(measurement.TruthOperationsBoundToResourceInstance));
        ConsoleUi.Field("Byte measured", ConsoleUi.Count(measurement.ByteMeasuredOperations));
        ConsoleUi.Field("Peer attributions", ConsoleUi.Count(measurement.PeerAttributions));
        ConsoleUi.Field("False attributions", ConsoleUi.Count(measurement.FalsePeerAttributions));
        ConsoleUi.Field("Truth bytes sent", ConsoleUi.Bytes(coverage.TruthBytesSent));
        ConsoleUi.Field("Observed transport bytes", ConsoleUi.Bytes(coverage.ObservedBytesSent));
        Render(coverage.Assessment);
    }

    /// <summary>The criteria in words with their counts, the computed tier, and each gap.</summary>
    public static void Render(TierAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ConsoleUi.Line();
        ConsoleUi.Table(
            ["Criterion", "Counted", "Measured", "Result"],
            [
                .. assessment.Criteria.Select(criterion => (IReadOnlyList<string>)
                [
                    CapabilityText.Criterion(criterion.Name),
                    string.Create(CultureInfo.CurrentCulture, $"{criterion.Numerator:N0}/{criterion.Denominator:N0}"),
                    ConsoleUi.Ratio(criterion.Measured),
                    criterion.Satisfied ? "met" : "not met",
                ]),
            ]);
        ConsoleUi.Line();
        ConsoleUi.Field("Computed tier", CapabilityText.Tier(assessment.Tier));
        foreach (string gap in assessment.Gaps)
        {
            ConsoleUi.Bullet(gap);
        }
    }
}
