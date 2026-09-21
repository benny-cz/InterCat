using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal sealed record CaptureImpactRunMeasurement
{
    public required MachineProcessorTimeInterval ProcessorTime { get; init; }
    public required double WallClockMilliseconds { get; init; }
    public required double WorkloadMilliseconds { get; init; }
    public required int DeclaredMessages { get; init; }
    public required double MessagesPerSecond { get; init; }
}

internal sealed record CaptureImpactHealth(
    long AdmittedRecords,
    long ProviderReportedEventLoss,
    long ConsumerReportedBufferLoss,
    long ApplicationDrops,
    bool ExactAdmittedProjectionReplay,
    string EvidencePath);

internal sealed record CaptureImpactPair
{
    public required int Pair { get; init; }
    public required string Order { get; init; }
    public required CaptureImpactRunMeasurement Baseline { get; init; }
    public required CaptureImpactRunMeasurement Capture { get; init; }
    public required double ObservedCpuDifferencePercentagePoints { get; init; }
    public required double CaptureImpactPercentagePoints { get; init; }
    public required double ObservedThroughputRegressionPercent { get; init; }
    public required double ThroughputRegressionPercent { get; init; }
    public required CaptureImpactHealth Health { get; init; }
}

internal sealed record SourceCaptureImpact
{
    public required string SourceId { get; init; }
    public required IReadOnlyList<CaptureImpactPair> Pairs { get; init; }
    public required double MedianObservedCpuDifferencePercentagePoints { get; init; }
    public required double MedianCaptureImpactPercentagePoints { get; init; }
    public required double MedianObservedThroughputRegressionPercent { get; init; }
    public required double MedianThroughputRegressionPercent { get; init; }
    public required OverheadClass Overhead { get; init; }
    public required bool CpuTargetMet { get; init; }
    public required bool ThroughputTargetMet { get; init; }
    public required bool LossFree { get; init; }
    public required bool EvidenceValid { get; init; }
}

internal sealed record CaptureImpactResult
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required MachineDescriptor Machine { get; init; }
    public required ComparisonSettings Settings { get; init; }
    public required int PairsPerSource { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required IReadOnlyList<SourceCaptureImpact> Sources { get; init; }
    public required bool DecisionReady { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>Owns the exact processor and wall-clock interval around one workload plus reorder grace.</summary>
internal sealed class CaptureImpactMeter(int declaredMessages)
{
    private MachineProcessorTimeReading _processorAtStart;
    private long _wallStarted;
    private TimeSpan? _workloadElapsed;

    public CaptureImpactRunMeasurement? Measurement { get; private set; }

    public void Start()
    {
        if (_wallStarted != 0)
        {
            throw new InvalidOperationException("The capture-impact interval was started twice.");
        }

        if (!MachineProcessorTime.TryRead(out _processorAtStart, out string? reason))
        {
            throw new InvalidOperationException($"Total-machine processor time is unavailable: {reason}");
        }

        _wallStarted = Stopwatch.GetTimestamp();
    }

    public void RecordWorkloadElapsed(TimeSpan elapsed)
    {
        if (_wallStarted == 0 || _workloadElapsed is not null || elapsed <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The workload interval is absent, duplicated, or empty.");
        }

        _workloadElapsed = elapsed;
    }

    public CaptureImpactRunMeasurement Complete()
    {
        if (_wallStarted == 0 || _workloadElapsed is not { } workloadElapsed || Measurement is not null)
        {
            throw new InvalidOperationException("The capture-impact interval is incomplete or already complete.");
        }

        if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading processorAtEnd, out string? reason))
        {
            throw new InvalidOperationException($"Total-machine processor time is unavailable: {reason}");
        }

        if (!MachineProcessorTime.TryMeasure(_processorAtStart, processorAtEnd, out MachineProcessorTimeInterval processor))
        {
            throw new InvalidOperationException("The total-machine processor clock reset or produced an empty interval.");
        }

        TimeSpan wallElapsed = Stopwatch.GetElapsedTime(_wallStarted);
        Measurement = new()
        {
            ProcessorTime = processor,
            WallClockMilliseconds = wallElapsed.TotalMilliseconds,
            WorkloadMilliseconds = workloadElapsed.TotalMilliseconds,
            DeclaredMessages = declaredMessages,
            MessagesPerSecond = declaredMessages / workloadElapsed.TotalSeconds,
        };
        return Measurement;
    }
}

internal static class CaptureImpactMath
{
    public static double Median(IEnumerable<double> values)
    {
        double[] ordered = [.. values.Order()];
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one measurement is required.", nameof(values));
        }

        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}
