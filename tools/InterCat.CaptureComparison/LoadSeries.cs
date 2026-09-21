using System.Globalization;
using InterCat.Capture.Windows;

namespace InterCat.CaptureComparison;

/// <summary>
/// One declared point of a load series. Every field that changes the measurement is here and is written
/// into the result, so a level's numbers can never be read without the settings that produced them
/// (section 1.4, section 12).
/// </summary>
internal sealed record LoadLevel(
    string Name,
    string Intent,
    int Connections,
    int MessagesPerConnection,
    int MaximumMessageBytes,
    int InterMessageDelayMilliseconds,
    int QueueCapacityRecords,
    int JournalBatchRecords,
    int Concurrency)
{
    /// <summary>Messages the workload exchanges at this level, before any observation.</summary>
    public int DeclaredMessages => Connections * MessagesPerConnection;
}

/// <summary>What the pipeline did at one level, classified from its counters rather than from intent.</summary>
internal enum SaturationOutcome
{
    /// <summary>Nothing was lost or dropped and the queue stayed below half its bound.</summary>
    Headroom = 0,

    /// <summary>The queue passed half its bound without dropping. The limit is in sight, not reached.</summary>
    QueuePressure = 1,

    /// <summary>The bounded queue reached its limit and counted its drops (R8).</summary>
    QueueSaturated = 2,

    /// <summary>The source lost events before InterCat saw them. That is not a queue outcome.</summary>
    SourceLoss = 3,
}

/// <summary>
/// The saturation reading for one variant at one level. Source loss, application drops and queue depth
/// stay separate quantities; the outcome names which limit was reached, never a sum of them (section 9.3).
/// </summary>
internal sealed record SaturationAssessment
{
    public required SaturationOutcome Outcome { get; init; }
    public required string Evidence { get; init; }

    /// <summary>Records admitted per second of the acquisition window, as measured, not as configured.</summary>
    public required double AdmittedRecordsPerSecond { get; init; }

    /// <summary>Queue high-water as a fraction of its bound, or null when the variant has no queue.</summary>
    public required double? QueueUtilization { get; init; }

    /// <summary>
    /// False when the source's own loss counters could not be read. The queue outcome still holds, because
    /// it describes a different quantity, but no run with an unreadable counter may be called loss-free.
    /// </summary>
    public required bool SourceLossKnown { get; init; }

    /// <summary>
    /// The share of the acquisition window the writer stage was busy, counting its own processor time and
    /// the time its durable flushes took. Null when the variant has no writer InterCat owns.
    /// </summary>
    public required double? WriterBusyFraction { get; init; }

    /// <summary>
    /// False when the writer was busy for at least half the acquisition window: at that point the writer,
    /// not the source, is setting the pace. The fraction is reported beside it, so the threshold is
    /// visible rather than hidden inside a verdict (R21).
    /// </summary>
    public required bool? WriterKeptUp { get; init; }
}

internal static class LoadSeries
{
    /// <summary>
    /// The share of the acquisition window above which the writer is treated as the pacing stage. It is a
    /// declared threshold, not a measured constant, so it is named here rather than buried in a comparison.
    /// </summary>
    public const double WriterPacingFraction = 0.5;

    /// <summary>
    /// The declared series. The first three levels raise the workload's own rate; the last two hold that
    /// rate and shrink a bound instead, because a machine faster than the fixture never reaches its queue
    /// or its disk by running the fixture harder. A constrained level records its constraint, so its drops
    /// are read as the bound working rather than as a source failure (R8, R21).
    /// </summary>
    public static IReadOnlyList<LoadLevel> Default { get; } =
    [
        new(
            "paced",
            "The fixture's own pacing, and the point the single-run artifacts already cite.",
            Connections: 2,
            MessagesPerConnection: 8,
            MaximumMessageBytes: 4_096,
            InterMessageDelayMilliseconds: 15,
            QueueCapacityRecords: 65_536,
            JournalBatchRecords: 4_096,
            Concurrency: 1),
        new(
            "steady",
            "Unpaced at a moderate message count, four connections at a time.",
            Connections: 4,
            MessagesPerConnection: 512,
            MaximumMessageBytes: 4_096,
            InterMessageDelayMilliseconds: 0,
            QueueCapacityRecords: 65_536,
            JournalBatchRecords: 4_096,
            Concurrency: 4),
        new(
            "peak",
            "The highest unconstrained rate in the series, with every connection in flight at once.",
            Connections: 8,
            MessagesPerConnection: 2_048,
            MaximumMessageBytes: 4_096,
            InterMessageDelayMilliseconds: 0,
            QueueCapacityRecords: 65_536,
            JournalBatchRecords: 4_096,
            Concurrency: 8),
        new(
            "queue-bound",
            "The peak rate against a deliberately small queue, so the bound is reached and its drops counted.",
            Connections: 8,
            MessagesPerConnection: 2_048,
            MaximumMessageBytes: 4_096,
            InterMessageDelayMilliseconds: 0,
            QueueCapacityRecords: 512,
            JournalBatchRecords: 4_096,
            Concurrency: 8),
        new(
            "disk-bound",
            "The peak rate with a durable flush every 32 records, so the writer sets the pace.",
            Connections: 8,
            MessagesPerConnection: 2_048,
            MaximumMessageBytes: 4_096,
            InterMessageDelayMilliseconds: 0,
            QueueCapacityRecords: 65_536,
            JournalBatchRecords: 32,
            Concurrency: 8),
    ];

    /// <summary>The single paced level, for a quick check that does not claim to be a series.</summary>
    public static IReadOnlyList<LoadLevel> Quick { get; } = [Default[0]];

    /// <summary>
    /// The fixed Explore workload used by IC-010a. A few hundred 15 ms waits per connection produce a
    /// multi-second interval on Windows without relying on sub-tick delays or creating enormous truth
    /// logs. The setting is serialized beside every result.
    /// </summary>
    public static LoadLevel Impact { get; } = new(
        "explore-impact",
        "A multi-second, paced Explore workload for paired no-capture/capture processor-time evidence.",
        Connections: 4,
        MessagesPerConnection: 256,
        MaximumMessageBytes: 4_096,
        InterMessageDelayMilliseconds: 15,
        QueueCapacityRecords: 65_536,
        JournalBatchRecords: 4_096,
        Concurrency: 4);

    /// <summary>Resolves a named series, or null when the name is not declared.</summary>
    public static IReadOnlyList<LoadLevel>? Find(string name) => name switch
    {
        "default" => Default,
        "quick" => Quick,
        _ => null,
    };

    /// <summary>Resolves an explicit comma-separated level list against the declared series.</summary>
    public static IReadOnlyList<LoadLevel>? Select(string names)
    {
        var chosen = new List<LoadLevel>();
        foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            LoadLevel? level = Default.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (level is null)
            {
                return null;
            }

            chosen.Add(level);
        }

        return chosen.Count == 0 ? null : chosen;
    }

    /// <summary>
    /// Classifies one variant's behaviour at one level. The rules are ordered because the outcomes are not
    /// interchangeable: losing events before admission is a different finding from dropping them at a
    /// bound this process chose.
    /// </summary>
    public static SaturationAssessment Assess(
        CaptureHealthSnapshot health,
        CaptureStageSnapshot? stages,
        WriterFlushFacts? writer,
        double acquisitionMilliseconds,
        bool sourceLossKnown = true)
    {
        double seconds = acquisitionMilliseconds / 1_000d;
        double rate = seconds > 0 ? health.AdmittedRecords / seconds : 0;
        double? utilization = stages is null || stages.QueueCapacityRecords <= 0
            ? null
            : (double)stages.QueueHighWaterRecords / stages.QueueCapacityRecords;
        double? writerBusy = writer is null || acquisitionMilliseconds <= 0
            ? null
            : (writer.TotalFlushMilliseconds + writer.ProcessorMilliseconds) / acquisitionMilliseconds;
        bool? writerKeptUp = writerBusy is null ? null : writerBusy < WriterPacingFraction;

        long sourceLoss = health.ProviderReportedEventLoss + health.ConsumerReportedBufferLoss;
        (SaturationOutcome outcome, string evidence) = sourceLoss > 0
            ? (SaturationOutcome.SourceLoss, string.Create(
                CultureInfo.InvariantCulture,
                $"The source reported {health.ProviderReportedEventLoss} provider and "
                + $"{health.ConsumerReportedBufferLoss} consumer-buffer losses, so events were gone before admission."))
            : health.ApplicationDrops > 0
                ? (SaturationOutcome.QueueSaturated, string.Create(
                    CultureInfo.InvariantCulture,
                    $"The bounded queue reached its limit and counted {health.ApplicationDrops} drops of "
                    + $"{stages?.QueueCapacityRecords ?? 0} capacity."))
                : utilization >= 0.5
                    ? (SaturationOutcome.QueuePressure, string.Create(
                        CultureInfo.InvariantCulture,
                        $"The queue reached {utilization:P1} of its bound without dropping."))
                    : (SaturationOutcome.Headroom, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{(sourceLossKnown ? "No loss" : "No loss this process could read")}, no drop, and "
                        + $"the queue stayed at {utilization ?? 0:P1} of its bound."));

        return new()
        {
            Outcome = outcome,
            Evidence = evidence,
            AdmittedRecordsPerSecond = rate,
            QueueUtilization = utilization,
            SourceLossKnown = sourceLossKnown,
            WriterBusyFraction = writerBusy,
            WriterKeptUp = writerKeptUp,
        };
    }
}

/// <summary>The writer facts the saturation rule needs, kept separate from the full writer metrics.</summary>
internal sealed record WriterFlushFacts(
    int DurableFlushes,
    double TotalFlushMilliseconds,
    double ProcessorMilliseconds);
