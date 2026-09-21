using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    /// <summary>
    /// The share of an acquisition window a level must keep the writer busy for before the series counts
    /// disk-bound behaviour as exercised at all. It is deliberately well below the pacing threshold of
    /// <see cref="LoadSeries.WriterPacingFraction"/>, which decides only how a single level is labelled.
    /// </summary>
    private const double WriterExercisedFraction = 0.25;

    /// <summary>
    /// What one level could not show. A level's blockers are its own, and a constrained level is expected
    /// to drop: its drops are the bound working, so they are not counted against it here (R8).
    /// </summary>
    private static List<string> BuildLevelBlockers(
        LoadLevel level,
        CaptureComparisonVariant journal,
        CaptureComparisonVariant etl)
    {
        var blockers = new List<string>();
        AddClockBlockers(journal, blockers);
        AddStageBlockers(journal, blockers);
        if (journal.ExactAdmittedProjectionReplay != true)
        {
            blockers.Add("The admitted-journal envelope did not replay with an identical ordered fingerprint.");
        }

        LoadLevel unconstrained = LoadSeries.Default[0];
        bool constrained = level.QueueCapacityRecords < unconstrained.QueueCapacityRecords
            || level.JournalBatchRecords < unconstrained.JournalBatchRecords;
        if (!constrained)
        {
            AddHealthBlockers("journal", journal, blockers);
            AddHealthBlockers("ETL", etl, blockers);
            AddCoverageBlockers("journal", journal, blockers);
            AddCoverageBlockers("ETL", etl, blockers);
        }

        return blockers;
    }

    /// <summary>
    /// What the series as a whole could not show. This is the ADR-008 ledger: every gate input is either
    /// recorded or named as missing, so the number of blockers is the number of open problems.
    /// </summary>
    private static List<string> BuildSeriesBlockers(
        List<CaptureComparisonResult> levels,
        SeriesSaturationSummary saturation,
        MachineDescriptor machine,
        ProbeEnvironment environment)
    {
        var blockers = new List<string>();
        if (levels.Count < 2)
        {
            blockers.Add(
                "One level is a point, not a series. ADR-008 requires a declared load series that reaches "
                + "queue and disk saturation.");
        }

        if (!saturation.ReachedQueueLimit)
        {
            string peakRate = saturation.PeakAdmittedRecordsPerSecond.ToString("N0", CultureInfo.InvariantCulture);
            blockers.Add(
                "No level reached the bounded queue's limit, so behaviour at the limit is untested. The "
                + $"fastest level was '{saturation.PeakLevel}' at {peakRate} admitted records per second.");
        }

        // A run-to-run verdict against the pacing threshold flips when the measurement sits near it, so
        // the blocker fires only when no level came close to exercising the writer at all (R3).
        if (saturation.PeakWriterBusyFraction is not { } writerBusy || writerBusy < WriterExercisedFraction)
        {
            string measured = saturation.PeakWriterBusyFraction is { } value
                ? value.ToString("P1", CultureInfo.InvariantCulture)
                : "nothing";
            blockers.Add(
                $"No level kept the writer meaningfully busy: the deepest was {measured} of its acquisition "
                + "window, so disk-bound behaviour is untested.");
        }

        AddSeriesExtendedDataBlockers(levels, blockers);
        AddSeriesLossReadabilityBlockers(levels, blockers);

        foreach (string gap in machine.ReferenceGaps)
        {
            blockers.Add($"This is not the section 12 reference machine: {gap}");
        }

        if (!environment.IsSupportedBuild)
        {
            blockers.Add(
                $"Build {environment.BuildId} is outside the section 1.3 support matrix, so the series is "
                + "evidence on an untested build.");
        }

        foreach (CaptureComparisonResult level in levels)
        {
            foreach (string blocker in level.LevelBlockers)
            {
                blockers.Add($"Level '{level.Level.Name}': {blocker}");
            }
        }

        return blockers;
    }

    /// <summary>Reads the series' outcome from its levels' counters, never from the intent behind them.</summary>
    private static SeriesSaturationSummary Summarize(List<CaptureComparisonResult> levels)
    {
        string? headroom = null;
        string? queueSaturated = null;
        string? sourceLoss = null;
        bool writerPaced = false;
        double? peakWriterBusy = null;
        string? peakWriterLevel = null;
        double peakRate = 0;
        string peakLevel = levels.Count > 0 ? levels[0].Level.Name : "none";
        foreach (CaptureComparisonResult level in levels)
        {
            SaturationAssessment assessment = level.Journal.Saturation;
            if (assessment.Outcome == SaturationOutcome.Headroom)
            {
                headroom = level.Level.Name;
            }

            if (queueSaturated is null && assessment.Outcome == SaturationOutcome.QueueSaturated)
            {
                queueSaturated = level.Level.Name;
            }

            if (sourceLoss is null && assessment.Outcome == SaturationOutcome.SourceLoss)
            {
                sourceLoss = level.Level.Name;
            }

            writerPaced |= assessment.WriterKeptUp == false;
            if (assessment.WriterBusyFraction is { } busy && busy > (peakWriterBusy ?? -1))
            {
                peakWriterBusy = busy;
                peakWriterLevel = level.Level.Name;
            }

            if (assessment.AdmittedRecordsPerSecond > peakRate)
            {
                peakRate = assessment.AdmittedRecordsPerSecond;
                peakLevel = level.Level.Name;
            }
        }

        return new()
        {
            HighestHeadroomLevel = headroom,
            FirstQueueSaturatedLevel = queueSaturated,
            FirstSourceLossLevel = sourceLoss,
            ReachedQueueLimit = queueSaturated is not null,
            WriterSetThePaceAtSomeLevel = writerPaced,
            PeakWriterBusyFraction = peakWriterBusy,
            PeakWriterBusyLevel = peakWriterLevel,
            PeakAdmittedRecordsPerSecond = peakRate,
            PeakLevel = peakLevel,
        };
    }

    /// <summary>
    /// An unreadable loss counter is one environmental fact, not one finding per level. It is stated once,
    /// naming the levels it affected, so the blocker count stays the count of distinct problems (R21).
    /// </summary>
    private static void AddSeriesLossReadabilityBlockers(
        List<CaptureComparisonResult> levels,
        List<string> blockers)
    {
        string[] affected =
        [
            .. levels.Where(level => !level.Etl.SourceLossKnown).Select(level => level.Level.Name),
        ];
        if (affected.Length > 0)
        {
            blockers.Add(
                "The ETL variant's provider loss counters could not be read at stop on this build, so its "
                + $"loss is unknown rather than zero at {affected.Length} of {levels.Count} levels "
                + $"({string.Join(", ", affected)}).");
        }

        string[] journalAffected =
        [
            .. levels.Where(level => !level.Journal.SourceLossKnown).Select(level => level.Level.Name),
        ];
        if (journalAffected.Length > 0)
        {
            blockers.Add(
                "The journal variant's provider loss counters could not be read at "
                + $"{journalAffected.Length} of {levels.Count} levels ({string.Join(", ", journalAffected)}).");
        }
    }

    /// <summary>
    /// Extended-data fidelity is one question for the whole series, not one per level: the answer depends
    /// on the enablement, which every level shares. Stating it once keeps the blocker count the count of
    /// distinct problems.
    /// </summary>
    private static void AddSeriesExtendedDataBlockers(
        List<CaptureComparisonResult> levels,
        List<string> blockers)
    {
        string? unavailable = levels
            .Select(level => level.Journal.ExtendedData.UnavailableReason)
            .FirstOrDefault(reason => reason is not null);
        if (unavailable is not null)
        {
            blockers.Add($"Extended-data items could not be read during capture: {unavailable}");
            return;
        }

        long copied = levels.Sum(level => level.Journal.ExtendedData.CopiedInCallback);
        if (copied == 0)
        {
            blockers.Add(
                "No level copied an extended-data item, because extended data is opt-in per provider "
                + "enablement. Envelope fidelity for extended items is untested by this series; rerun it "
                + "with --request-stacks true to exercise it.");
            return;
        }

        foreach (CaptureComparisonResult level in levels)
        {
            VariantExtendedDataEvidence evidence = level.Journal.ExtendedData;
            if (evidence.PersistedInEnvelopes != evidence.ReplayedIdentical)
            {
                blockers.Add(
                    $"Level '{level.Level.Name}': replay returned {evidence.ReplayedIdentical} extended "
                    + $"items for {evidence.PersistedInEnvelopes} persisted ones.");
            }
        }
    }

    private static void AddClockBlockers(CaptureComparisonVariant journal, List<string> blockers)
    {
        if (journal.Clock.Confidence != SourceClockConfidence.Confirmed)
        {
            blockers.Add(
                "The capture's source clock is "
                + $"{journal.Clock.Confidence.ToString().ToLowerInvariant()}: "
                + (journal.Clock.RefusalReason ?? "no delivered record was checked against it."));
        }

        if (journal.Clock.ReplayedIdentical != true)
        {
            blockers.Add("The persisted source clock descriptor did not read back identically from the journal.");
        }
    }

    private static void AddStageBlockers(CaptureComparisonVariant journal, List<string> blockers)
    {
        CaptureStageSnapshot? acquisition = journal.Stages.Acquisition;
        if (acquisition is null || acquisition.CallbackLatency.Samples == 0)
        {
            blockers.Add("No callback latency sample was recorded for the journal variant.");
            return;
        }

        if (acquisition.DeliveryThreadCpu is null)
        {
            blockers.Add("The delivery thread's processor time could not be read on this host.");
        }

        if (acquisition.DeliveryThreadAllocatedBytes is null)
        {
            blockers.Add("The delivery thread's allocation total was not recorded.");
        }

        if (journal.Stages.Writer is not { } writer)
        {
            blockers.Add("The journal writer stage produced no metrics.");
            return;
        }

        if (writer.DurableFlushLatency.Samples == 0)
        {
            blockers.Add("No durable flush latency sample was recorded for the journal writer.");
        }

        if (writer.WriterThreadCpu is null)
        {
            blockers.Add("The journal writer thread's processor time could not be read on this host.");
        }
    }

    /// <summary>
    /// A coverage blocker names the criterion that failed. The unsupported-build gap is reported once for
    /// the series, because a met threshold on an untested build is not a missed threshold (P27).
    /// </summary>
    private static void AddCoverageBlockers(
        string name,
        CaptureComparisonVariant variant,
        List<string> blockers)
    {
        foreach (TierCriterion criterion in variant.Coverage.Assessment.Criteria)
        {
            if (!criterion.Satisfied)
            {
                blockers.Add($"the {name} variant missed {criterion.Name}: {criterion.Gap ?? criterion.Requirement}");
            }
        }
    }

    private static void AddHealthBlockers(
        string name,
        CaptureComparisonVariant variant,
        List<string> blockers)
    {
        if (variant.SourceLossKnown
            && (!variant.Health.IsLossFree || variant.ReplaySourceEventsLost != 0 || variant.ReplayBudgetDrops != 0))
        {
            blockers.Add($"the {name} variant reported loss, a bounded-queue drop, or replay loss.");
        }

        if (!variant.Evidence.Complete)
        {
            blockers.Add($"the {name} evidence artifact is incomplete.");
        }
    }
}
