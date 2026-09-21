using InterCat.Analysis;
using InterCat.Benchmarks;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

internal sealed record ComparisonSettings(
    int Seed,
    int Connections,
    int MessagesPerConnection,
    int MaximumMessageBytes,
    int InterMessageDelayMilliseconds,
    int Concurrency,
    int ReorderGraceSeconds,
    int QueueCapacityRecords,
    int JournalBatchRecords,
    int RecordBudget,
    bool PreserveExtendedData,
    bool RequestCallStacks);

internal sealed record VariantTiming(
    double AcquisitionMilliseconds,
    double FinalizationMilliseconds,
    double ReplayMilliseconds);

internal sealed record EvidenceArtifact(
    string Kind,
    string RelativePath,
    long Bytes,
    long Records,
    int DurableFlushes,
    bool Complete);

/// <summary>
/// What the writer stage cost. Encode and durable flush are separate quantities because they are paid on
/// different resources, and the per-flush distribution is kept rather than an average (section 12).
/// journal-v1 itself performs no durable flush - durability is IC-016's - so this probe opens the file
/// write-through and flushes each batch itself, which is the cost IC-016 will have to pay.
/// </summary>
internal sealed record WriterStageMetrics
{
    public required long PayloadBytesWritten { get; init; }
    public required LatencyHistogramSnapshot EncodeLatency { get; init; }
    public required LatencyHistogramSnapshot DurableFlushLatency { get; init; }

    /// <summary>Processor time of the writing thread, or null when it moved threads or cannot be read.</summary>
    public required TimeSpan? WriterThreadCpu { get; init; }

    /// <summary>Bytes the writing thread allocated, or null when the writer could not isolate a thread.</summary>
    public required long? WriterThreadAllocatedBytes { get; init; }
}

/// <summary>
/// Where each variant spends its cost. A stage this variant does not have is null with a stated reason,
/// because "no callback cost" and "cost not measured" are different findings (section 12, R21).
/// </summary>
internal sealed record VariantStageMetrics
{
    public required CaptureStageSnapshot? Acquisition { get; init; }
    public required string AcquisitionNote { get; init; }
    public required WriterStageMetrics? Writer { get; init; }
    public required string WriterNote { get; init; }
    public required CaptureStageSnapshot? Replay { get; init; }
    public required string ReplayNote { get; init; }
}

/// <summary>The capture's source clock and what happened to it between acquisition and replay (ADR-006).</summary>
internal sealed record VariantClockEvidence
{
    public required SourceClockDescriptor? Descriptor { get; init; }
    public required SourceClockConfidence Confidence { get; init; }
    public required string? RefusalReason { get; init; }

    /// <summary>True when replay read the same descriptor back; null when the variant persists none.</summary>
    public required bool? ReplayedIdentical { get; init; }
}

/// <summary>
/// Where a replay's allocations came from. The same ETL is replayed twice through the same adapter: once
/// with the real admission table and once with an empty one, where every record is refused at the first
/// check before any InterCat work. The difference is admission's own cost; the remainder belongs to the
/// adapter that dispatches the record. R9 and R11 are rules about InterCat's callback work, so they are
/// evaluated against the first figure and the second is reported beside it (section 18.3).
/// </summary>
internal sealed record AllocationAttribution
{
    public required long RecordsObserved { get; init; }
    public required long DispatchOnlyBytes { get; init; }
    public required long FullAdmissionBytes { get; init; }

    public long AdmissionBytes => Math.Max(0, FullAdmissionBytes - DispatchOnlyBytes);

    public double? AdapterBytesPerRecord =>
        RecordsObserved > 0 ? (double)DispatchOnlyBytes / RecordsObserved : null;

    public double? AdmissionBytesPerRecord =>
        RecordsObserved > 0 ? (double)AdmissionBytes / RecordsObserved : null;
}

/// <summary>
/// How admission's allocation grows with the number of records it admits, measured across the series'
/// levels. A fixed setup cost divided by a record count looks like per-record allocation and is not one;
/// the slope between the smallest and the largest level is what "no unpooled allocation" means (R9, R11).
/// </summary>
internal sealed record AllocationSlope
{
    public required string SmallestLevel { get; init; }
    public required string LargestLevel { get; init; }
    public required long SmallestRecords { get; init; }
    public required long LargestRecords { get; init; }
    public required long SmallestAdmissionBytes { get; init; }
    public required long LargestAdmissionBytes { get; init; }

    /// <summary>Bytes of admission allocation per additional record. Null when the levels are too alike.</summary>
    public double? BytesPerRecord => LargestRecords > SmallestRecords
        ? (double)(LargestAdmissionBytes - SmallestAdmissionBytes) / (LargestRecords - SmallestRecords)
        : null;

    /// <summary>The part that does not grow: what admission costs once, whatever the record count.</summary>
    public double? FixedBytes => BytesPerRecord is { } slope
        ? SmallestAdmissionBytes - (slope * SmallestRecords)
        : null;
}

/// <summary>Extended-data outcomes, counted separately at each stage so no count implies another.</summary>
internal sealed record VariantExtendedDataEvidence
{
    public required long CopiedInCallback { get; init; }
    public required long OmittedInCallback { get; init; }
    public required long PersistedInEnvelopes { get; init; }
    public required long OmittedByPersistencePolicy { get; init; }
    public required long ReplayedIdentical { get; init; }
    public required IReadOnlyList<string> TypesObserved { get; init; }
    public required string? UnavailableReason { get; init; }
}

internal sealed record CaptureComparisonVariant
{
    public required string Name { get; init; }
    public required CaptureId CaptureId { get; init; }
    public required IReadOnlyList<ProviderEnablementResult> Providers { get; init; }
    public required CaptureHealthSnapshot Health { get; init; }
    public required CoverageSummary Coverage { get; init; }
    public required EvidenceArtifact Evidence { get; init; }
    public required VariantTiming Timing { get; init; }
    public required VariantStageMetrics Stages { get; init; }
    public required VariantClockEvidence Clock { get; init; }
    public required VariantExtendedDataEvidence ExtendedData { get; init; }

    /// <summary>
    /// Which part of a replay's allocation belongs to InterCat and which to the adapter. Null for a
    /// variant that runs no replay of its own.
    /// </summary>
    public required AllocationAttribution? Allocations { get; init; }
    public required long TruthRecords { get; init; }
    public required long ReplayedRecords { get; init; }
    public required long ReplayBudgetDrops { get; init; }
    public required long ReplaySourceEventsLost { get; init; }
    public required bool? ExactAdmittedProjectionReplay { get; init; }
    public required SaturationAssessment Saturation { get; init; }

    /// <summary>
    /// Whether the source's loss counters were readable for this variant. When false, the reported loss
    /// is a default and the real value is unknown (R3).
    /// </summary>
    public required bool SourceLossKnown { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
}

/// <summary>One level of the series: both variants at one declared load point.</summary>
internal sealed record CaptureComparisonResult
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required LoadLevel Level { get; init; }
    public required ComparisonSettings Settings { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required CaptureComparisonVariant Journal { get; init; }
    public required CaptureComparisonVariant Etl { get; init; }
    public required bool SameSeededWorkload { get; init; }
    public required IReadOnlyList<string> LevelBlockers { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// What the series as a whole showed. A series that never reached a bound has not tested the bound, and
/// says so rather than reporting the highest level it happened to run (R21).
/// </summary>
internal sealed record SeriesSaturationSummary
{
    public required string? HighestHeadroomLevel { get; init; }
    public required string? FirstQueueSaturatedLevel { get; init; }
    public required string? FirstSourceLossLevel { get; init; }
    public required bool ReachedQueueLimit { get; init; }
    public required bool WriterSetThePaceAtSomeLevel { get; init; }

    /// <summary>
    /// The largest share of an acquisition window the writer stage was busy for, and where. It is
    /// reported as the measured number because a yes/no verdict against a declared threshold flips from
    /// run to run when the measurement sits near it (R3).
    /// </summary>
    public required double? PeakWriterBusyFraction { get; init; }

    public required string? PeakWriterBusyLevel { get; init; }
    public required double PeakAdmittedRecordsPerSecond { get; init; }
    public required string PeakLevel { get; init; }
}

internal sealed record CaptureSeriesResult
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required MachineDescriptor Machine { get; init; }
    public required string SeriesName { get; init; }
    public required int Seed { get; init; }
    public required int RecordBudget { get; init; }
    public required bool PreserveExtendedData { get; init; }
    public required bool RequestCallStacks { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required IReadOnlyList<CaptureComparisonResult> Levels { get; init; }
    public required SeriesSaturationSummary Saturation { get; init; }

    /// <summary>
    /// Every §12 budget this series could evaluate, and every one it could not. A budget nothing measured
    /// reports as unmeasured, which is an open item rather than a pass (IC-010).
    /// </summary>
    public required IReadOnlyList<BudgetResult> Budgets { get; init; }

    /// <summary>How admission's allocation grows with records, or null when no two levels differ enough.</summary>
    public required AllocationSlope? AllocationSlope { get; init; }
    public required bool DecisionReady { get; init; }
    public required IReadOnlyList<string> DecisionBlockers { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}
