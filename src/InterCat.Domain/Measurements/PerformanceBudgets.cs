using System.Globalization;

namespace InterCat.Domain;

/// <summary>Which statistic a budget is stated against. A budget on p99 is not met by a median.</summary>
public enum BudgetStatistic
{
    Maximum = 1,
    Percentile95 = 2,
    Percentile99 = 3,
    Sustained = 4,
    Total = 5,
}

/// <summary>Whether a budget was met, missed, or never measured. The third is not a pass.</summary>
public enum BudgetOutcome
{
    /// <summary>Nothing measured this budget. It is open, not satisfied (R21).</summary>
    Unmeasured = 1,
    Met = 2,
    Missed = 3,
}

/// <summary>
/// One §12 engineering budget. It is a proposed acceptance target, not a measured result: §12 says so,
/// and a budget that is revised is revised through an ADR with evidence, never quietly dropped.
/// </summary>
public sealed record PerformanceBudget(
    string Id,
    string Stage,
    BudgetStatistic Statistic,
    double Target,
    MeasurementUnit Unit,
    string Requirement)
{
    /// <summary>True when a smaller measured value is better, which is so for every latency budget.</summary>
    public bool LowerIsBetter { get; init; } = true;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Stage}: {Statistic} {(LowerIsBetter ? "under" : "at least")} {Target:N0} {Unit}");
}

/// <summary>
/// One budget and what a run measured against it. The measured value is kept beside the outcome, so a
/// "met" can be checked and a "missed" states by how much (section 1.4).
/// </summary>
public sealed record BudgetResult
{
    public required PerformanceBudget Budget { get; init; }
    public required BudgetOutcome Outcome { get; init; }

    /// <summary>What was measured, in the budget's unit. Null when nothing measured it.</summary>
    public required double? Measured { get; init; }

    /// <summary>Where the measurement came from, or why there is none.</summary>
    public required string Evidence { get; init; }

    public string Describe() => Outcome switch
    {
        BudgetOutcome.Unmeasured => $"{Budget.Stage}: unmeasured. {Evidence}",
        BudgetOutcome.Met => string.Create(
            CultureInfo.InvariantCulture,
            $"{Budget.Stage}: met. Measured {Measured:N0} {Budget.Unit} against {Budget.Target:N0}. {Evidence}"),
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"{Budget.Stage}: MISSED. Measured {Measured:N0} {Budget.Unit} against {Budget.Target:N0}. {Evidence}"),
    };
}

/// <summary>
/// The §12 budget table, written down once so a result can cite a budget instead of restating it. Every
/// entry is a proposed acceptance target that IC-010 measures; a budget nothing measures reports as
/// unmeasured, which is an open item rather than a pass (§12, R21).
/// </summary>
public static class PerformanceBudgets
{
    /// <summary>The end-to-end live latency budget of §12, stage by stage.</summary>
    public const string CallbackAdmission = "callback-admission";
    public const string CallbackAllocation = "callback-allocation";
    public const string RecordToBatch = "record-to-committed-batch";
    public const string BatchToSegment = "batch-to-segment";
    public const string SegmentToSnapshot = "segment-to-snapshot";
    public const string SnapshotToFrame = "snapshot-to-frame";
    public const string AcquiredToVisible95 = "acquired-to-visible-p95";
    public const string AcquiredToVisible99 = "acquired-to-visible-p99";
    public const string SustainedIngest = "sustained-ingest";
    public const string QueueDropFreedom = "queue-drop-freedom";

    public static IReadOnlyList<PerformanceBudget> All { get; } =
    [
        new(
            CallbackAdmission,
            "Callback admission and copy, per record",
            BudgetStatistic.Percentile99,
            50_000,
            MeasurementUnit.Nanoseconds,
            "p99 under 50 µs (R9, R11). Stated in nanoseconds so a bucket bound can be compared exactly."),
        new(
            CallbackAllocation,
            "Callback allocation, per record",
            BudgetStatistic.Maximum,
            1,
            MeasurementUnit.Bytes,
            "No unpooled allocation on the callback path (R9, R11). Measured as the slope of admission's "
            + "allocation against records admitted, so a fixed setup cost is not read as a per-record one. "
            + "One byte per record is the resolution at which a slope tells growth from noise; the adapter's "
            + "own allocation is a separate quantity and is never added to this one (ADR-009)."),
        new(
            RecordToBatch,
            "Admitted record to committed journal batch",
            BudgetStatistic.Percentile95,
            250,
            MeasurementUnit.Milliseconds,
            "p95 under 250 ms."),
        new(
            BatchToSegment,
            "Committed batch to normalized immutable segment",
            BudgetStatistic.Percentile95,
            500,
            MeasurementUnit.Milliseconds,
            "p95 under 500 ms."),
        new(
            SegmentToSnapshot,
            "Segment to published snapshot",
            BudgetStatistic.Percentile95,
            250,
            MeasurementUnit.Milliseconds,
            "p95 under 250 ms at a publication cadence of at least 4 Hz."),
        new(
            SnapshotToFrame,
            "Published snapshot to first drawn frame",
            BudgetStatistic.Percentile95,
            100,
            MeasurementUnit.Milliseconds,
            "p95 under 100 ms."),
        new(
            AcquiredToVisible95,
            "Event acquired to event visible",
            BudgetStatistic.Percentile95,
            1_500,
            MeasurementUnit.Milliseconds,
            "p95 under 1.5 s."),
        new(
            AcquiredToVisible99,
            "Event acquired to event visible",
            BudgetStatistic.Percentile99,
            3_000,
            MeasurementUnit.Milliseconds,
            "p99 under 3 s."),
        new(
            SustainedIngest,
            "Sustained normalized ingest",
            BudgetStatistic.Sustained,
            100_000,
            MeasurementUnit.CountPerSecond,
            "100,000 normalized metadata observations per second for 10 minutes with no application drops.")
        {
            LowerIsBetter = false,
        },
        new(
            QueueDropFreedom,
            "Application drops at the sustained rate",
            BudgetStatistic.Total,
            0,
            MeasurementUnit.Count,
            "No application drop while inside the declared queue and disk policy (R8)."),
    ];

    public static PerformanceBudget Find(string id) =>
        All.FirstOrDefault(budget => string.Equals(budget.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such §12 budget.");

    /// <summary>
    /// Evaluates one budget against a measured value. A null measurement is unmeasured, never a pass: the
    /// distinction between "we met it" and "nobody checked" is the whole point of the table (R21).
    /// </summary>
    public static BudgetResult Evaluate(PerformanceBudget budget, double? measured, string evidence)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        if (measured is not { } value)
        {
            return new()
            {
                Budget = budget,
                Outcome = BudgetOutcome.Unmeasured,
                Measured = null,
                Evidence = evidence,
            };
        }

        bool met = budget.LowerIsBetter ? value <= budget.Target : value >= budget.Target;
        return new()
        {
            Budget = budget,
            Outcome = met ? BudgetOutcome.Met : BudgetOutcome.Missed,
            Measured = value,
            Evidence = evidence,
        };
    }

    /// <summary>
    /// Fills in every budget nothing measured, so a report always covers the whole table. A budget that
    /// is absent from a result would read as satisfied; one that says "unmeasured" cannot.
    /// </summary>
    public static IReadOnlyList<BudgetResult> Complete(IEnumerable<BudgetResult> measured, string reason)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var results = measured.ToList();
        var seen = new HashSet<string>(results.Select(result => result.Budget.Id), StringComparer.Ordinal);
        foreach (PerformanceBudget budget in All)
        {
            if (!seen.Contains(budget.Id))
            {
                results.Add(Evaluate(budget, null, reason));
            }
        }

        return [.. results.OrderBy(result => All.Select(budget => budget.Id).ToList().IndexOf(result.Budget.Id))];
    }
}

/// <summary>
/// Computes the §4.3 overhead class from a measured capture impact. §12 targets under 5 percentage points
/// of total-machine processor time; the band below it is where a capture is not worth thinking about.
/// The thresholds are declared here rather than argued in a report (§4.3, §12).
/// </summary>
public static class OverheadClassCalculator
{
    /// <summary>At or below this share of one machine's processor time, capture cost is Low.</summary>
    public const double LowCeilingPercentagePoints = 1.0;

    /// <summary>§12's target: at or below this, capture cost is Moderate. Above it, High.</summary>
    public const double ModerateCeilingPercentagePoints = 5.0;

    /// <summary>
    /// Classifies a measured capture impact. A null measurement stays <see cref="OverheadClass.Unmeasured"/>,
    /// which is what every source reports until IC-010 measures it.
    /// </summary>
    public static OverheadClass Classify(double? percentagePointsOfMachineCpu) => percentagePointsOfMachineCpu switch
    {
        null => OverheadClass.Unmeasured,
        < 0 => throw new ArgumentOutOfRangeException(nameof(percentagePointsOfMachineCpu)),
        <= LowCeilingPercentagePoints => OverheadClass.Low,
        <= ModerateCeilingPercentagePoints => OverheadClass.Moderate,
        _ => OverheadClass.High,
    };

    /// <summary>
    /// The capture's share of one machine's processor time, from processor seconds and wall seconds
    /// across a known processor count. Returns null when the interval is too short to divide by.
    /// </summary>
    public static double? ImpactPercentagePoints(
        double captureProcessorSeconds,
        double wallClockSeconds,
        int logicalProcessors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(captureProcessorSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalProcessors);
        return wallClockSeconds <= 0
            ? null
            : 100d * captureProcessorSeconds / (wallClockSeconds * logicalProcessors);
    }
}
