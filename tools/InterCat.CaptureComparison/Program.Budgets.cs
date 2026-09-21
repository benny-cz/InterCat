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
    private static IReadOnlyList<BudgetResult> EvaluateBudgets(List<CaptureComparisonResult> levels)
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

            long admitted = busiest.Journal.Health.AdmittedRecords;
            double? perRecord = stage.DeliveryThreadAllocatedBytes is { } bytes && admitted > 0
                ? (double)bytes / admitted
                : null;
            results.Add(PerformanceBudgets.Evaluate(
                PerformanceBudgets.Find(PerformanceBudgets.CallbackAllocation),
                perRecord,
                level + $" {stage.DeliveryThreadAllocatedBytes:N0} bytes allocated on the delivery thread "
                    + $"across {admitted:N0} admitted records."));

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
