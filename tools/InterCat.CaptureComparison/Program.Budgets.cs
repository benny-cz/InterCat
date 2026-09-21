using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    /// <summary>
    /// Evaluates the section 12 budgets this series can speak to. The callback stages are measured here;
    /// the stages after the journal do not exist yet, and a budget nothing measured says so rather than
    /// being left out, because an absent budget would read as satisfied (IC-010, R21).
    /// </summary>
    /// <summary>
    /// Fits admission's allocation against the number of records admitted, using the two levels whose
    /// record counts differ most. One level cannot tell a fixed cost from a per-record one.
    /// </summary>
    private static AllocationSlope? MeasureAllocationSlope(List<CaptureComparisonResult> levels)
    {
        CaptureComparisonResult[] measured =
        [
            .. levels
                .Where(level => level.Etl.Allocations is not null)
                .OrderBy(level => level.Etl.Allocations!.RecordsObserved),
        ];
        if (measured.Length < 2)
        {
            return null;
        }

        AllocationAttribution smallest = measured[0].Etl.Allocations!;
        AllocationAttribution largest = measured[^1].Etl.Allocations!;
        return new()
        {
            SmallestLevel = measured[0].Level.Name,
            LargestLevel = measured[^1].Level.Name,
            SmallestRecords = smallest.RecordsObserved,
            LargestRecords = largest.RecordsObserved,
            SmallestAdmissionBytes = smallest.AdmissionBytes,
            LargestAdmissionBytes = largest.AdmissionBytes,
        };
    }

    private static IReadOnlyList<BudgetResult> EvaluateBudgets(
        List<CaptureComparisonResult> levels,
        AllocationSlope? slope)
    {
        var results = new List<BudgetResult>();
        CaptureComparisonResult? busiest = levels
            .Where(level => level.Journal.Stages.Acquisition is not null)
            .OrderByDescending(level => level.Journal.Saturation.AdmittedRecordsPerSecond)
            .FirstOrDefault();
        if (busiest?.Journal.Stages.Acquisition is { } stage && stage.CallbackLatency.Samples > 0)
        {
            string level = $"Busiest level '{busiest.Level.Name}'.";
            results.Add(PerformanceBudgets.Evaluate(
                PerformanceBudgets.Find(PerformanceBudgets.CallbackAdmission),
                stage.CallbackLatency.Percentile99?.UpperNanoseconds,
                level + " The bucket's upper bound is compared, so a met budget is met by the whole bucket."));

            // R9 and R11 are rules about InterCat's callback work. The delivery thread's total includes
            // the adapter's dispatch, which InterCat does not control and cannot pool, so the budget is
            // evaluated against the attributed figure and the adapter's is reported beside it.
            string slopeEvidence = slope is null
                ? "Two levels with different record counts are needed to tell a fixed cost from a "
                    + "per-record one, and this series has fewer."
                : $"Fitted across levels '{slope.SmallestLevel}' and '{slope.LargestLevel}': "
                    + $"{slope.SmallestAdmissionBytes:N0} bytes over {slope.SmallestRecords:N0} records "
                    + $"against {slope.LargestAdmissionBytes:N0} bytes over {slope.LargestRecords:N0}. "
                    + $"About {slope.FixedBytes:N0} bytes of that does not grow with the record count. "
                    + "Both runs replay the same evidence through the same adapter, once with the "
                    + "admission table and once with none, so what is left is admission's own cost.";
            results.Add(PerformanceBudgets.Evaluate(
                PerformanceBudgets.Find(PerformanceBudgets.CallbackAllocation),
                slope?.BytesPerRecord,
                slopeEvidence));

            results.Add(PerformanceBudgets.Evaluate(
                PerformanceBudgets.Find(PerformanceBudgets.SustainedIngest),
                busiest.Journal.Saturation.AdmittedRecordsPerSecond,
                level + " This is the fixture's rate, not the pipeline's ceiling, and the interval is "
                    + "seconds rather than the ten minutes the budget names."));
        }

        LoadLevel reference = LoadSeries.Default[0];
        CaptureComparisonResult? unconstrained = levels
            .Where(candidate =>
                candidate.Level.QueueCapacityRecords >= reference.QueueCapacityRecords
                && candidate.Level.JournalBatchRecords >= reference.JournalBatchRecords)
            .OrderByDescending(candidate => candidate.Journal.Saturation.AdmittedRecordsPerSecond)
            .FirstOrDefault();
        if (unconstrained is not null)
        {
            results.Add(PerformanceBudgets.Evaluate(
                PerformanceBudgets.Find(PerformanceBudgets.QueueDropFreedom),
                unconstrained.Journal.Health.ApplicationDrops,
                $"Level '{unconstrained.Level.Name}', the busiest level that shrank no bound. A level that "
                    + "shrinks a bound is expected to drop and is not evaluated against this budget."));
        }

        return PerformanceBudgets.Complete(
            results,
            "No stage in this harness produces this measurement. The journal, segment, snapshot and frame "
            + "stages arrive with IC-011 and M2.");
    }
}
