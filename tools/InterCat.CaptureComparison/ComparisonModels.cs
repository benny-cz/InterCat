using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

internal sealed record ComparisonSettings(
    int Seed,
    int Connections,
    int MessagesPerConnection,
    int MaximumMessageBytes,
    int ReorderGraceSeconds,
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
/// Where each variant spends its cost. A stage this variant does not have is null with a stated reason,
/// because "no callback cost" and "cost not measured" are different findings (section 12, R21).
/// </summary>
internal sealed record VariantStageMetrics
{
    public required CaptureStageSnapshot? Acquisition { get; init; }
    public required string AcquisitionNote { get; init; }
    public required JournalProbeWriterMetrics? Writer { get; init; }
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
    public required TcpCoverageResult Coverage { get; init; }
    public required EvidenceArtifact Evidence { get; init; }
    public required VariantTiming Timing { get; init; }
    public required VariantStageMetrics Stages { get; init; }
    public required VariantClockEvidence Clock { get; init; }
    public required VariantExtendedDataEvidence ExtendedData { get; init; }
    public required long TruthRecords { get; init; }
    public required long ReplayedRecords { get; init; }
    public required long ReplayBudgetDrops { get; init; }
    public required long ReplaySourceEventsLost { get; init; }
    public required bool? ExactAdmittedProjectionReplay { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
}

internal sealed record CaptureComparisonResult
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required ComparisonSettings Settings { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required CaptureComparisonVariant Journal { get; init; }
    public required CaptureComparisonVariant Etl { get; init; }
    public required bool SameSeededWorkload { get; init; }
    public required bool DecisionReady { get; init; }
    public required IReadOnlyList<string> DecisionBlockers { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}
