using System.Globalization;
using System.Text.Json;
using InterCat.Benchmarks;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    private const double CaptureCpuTargetPercentagePoints = 5;
    private const double ThroughputRegressionTargetPercent = 5;

    private static readonly string[] ImpactNotes =
    [
        "Each source is measured independently. A combined profile cost is not attributed to every source.",
        "Every pair uses the same seeded workload once without capture and once through the admitted journal-v1 path; pair order alternates to reduce drift bias.",
        "Total-machine CPU is the GetSystemTimes busy share: kernel plus user time with idle subtracted once. It includes kernel/provider work, unlike process CPU.",
        "The measured CPU window starts after provider enablement and ends after the workload plus reorder grace. Capture stop and offline replay are excluded.",
        "Signed observed differences remain in every pair. Only the non-negative cost used for budget and OverheadClass evaluation is clamped at zero.",
        "The median of three pairs is the default. Every pair remains in the artifact so dispersion and background interference are inspectable.",
        "A source is decision evidence only when every capture admitted records and replayed its admitted projection exactly.",
        "No benchmark can prove that another profiler or tracing session was absent; run on a quiet machine and record that condition with the retained artifact.",
    ];

    private static async Task<int> RunImpactAsync(
        string outputDirectory,
        string workload,
        ProbeEnvironment environment,
        MachineDescriptor machine,
        IReadOnlyList<SourceAdmissionPlan> sources,
        IReadOnlyList<string> refusals,
        Options options,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        LoadLevel level = LoadSeries.Impact;
        var settings = new ComparisonSettings(
            options.Seed,
            level.Connections,
            level.MessagesPerConnection,
            level.MaximumMessageBytes,
            level.InterMessageDelayMilliseconds,
            level.Concurrency,
            options.GraceSeconds,
            level.QueueCapacityRecords,
            level.JournalBatchRecords,
            options.RecordBudget,
            options.PreserveExtendedData,
            options.RequestCallStacks);

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Measuring {sources.Count:N0} sources independently with {options.ImpactPairs:N0} paired trials each; "
            + $"{level.DeclaredMessages:N0} messages per trial..."));

        var measured = new List<SourceCaptureImpact>(sources.Count);
        foreach (SourceAdmissionPlan source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.Error.WriteLine($"  {source.SourceId}...");
            measured.Add(await RunSourceImpactAsync(
                outputDirectory,
                workload,
                source,
                settings,
                options.ImpactPairs,
                options.RequestCallStacks,
                cancellationToken).ConfigureAwait(false));
        }

        bool ready = refusals.Count == 0
            && measured.Count == sources.Count
            && measured.All(source =>
                source.CpuTargetMet
                && source.ThroughputTargetMet
                && source.LossFree
                && source.EvidenceValid);
        var result = new CaptureImpactResult
        {
            Schema = "intercat.capture-impact.v1",
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            Machine = machine,
            Settings = settings,
            PairsPerSource = options.ImpactPairs,
            PlanRefusals = refusals,
            Sources = measured,
            DecisionReady = ready,
            Notes = ImpactNotes,
        };

        string resultPath = Path.Combine(outputDirectory, "impact.json");
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, Json),
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(result, Json));

        Console.Error.WriteLine();
        Console.Error.WriteLine("Capture impact by source");
        foreach (SourceCaptureImpact source in measured)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {source.SourceId}: {source.MedianCaptureImpactPercentagePoints:F2} CPU pp "
                + $"({source.Overhead}), {source.MedianThroughputRegressionPercent:F2}% throughput regression, "
                + $"loss-free {source.LossFree}, evidence-valid {source.EvidenceValid}"));
        }

        Console.Error.WriteLine($"Evidence: {resultPath}");
        return ready ? 0 : 1;
    }

    private static async Task<SourceCaptureImpact> RunSourceImpactAsync(
        string outputDirectory,
        string workload,
        SourceAdmissionPlan source,
        ComparisonSettings settings,
        int pairCount,
        bool requestCallStacks,
        CancellationToken cancellationToken)
    {
        List<ProviderEnablementRequest> providers = BuildProviderRequests([source], requestCallStacks);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException($"No provider request could be built for '{source.SourceId}'.");
        }

        string sourceDirectory = Path.Combine(outputDirectory, "sources", SafePathSegment(source.SourceId));
        var pairs = new List<CaptureImpactPair>(pairCount);
        for (int index = 0; index < pairCount; index++)
        {
            string pairDirectory = Path.Combine(sourceDirectory, $"pair-{index + 1:D2}");
            bool baselineFirst = index % 2 == 0;
            CaptureImpactRunMeasurement baseline;
            (CaptureImpactRunMeasurement Measurement, CaptureComparisonVariant Variant) captured;
            if (baselineFirst)
            {
                baseline = await RunNoCaptureAsync(
                    Path.Combine(pairDirectory, "baseline"),
                    workload,
                    settings,
                    cancellationToken).ConfigureAwait(false);
                captured = await RunImpactCaptureAsync(
                    Path.Combine(pairDirectory, "capture"),
                    workload,
                    source,
                    providers,
                    settings,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                captured = await RunImpactCaptureAsync(
                    Path.Combine(pairDirectory, "capture"),
                    workload,
                    source,
                    providers,
                    settings,
                    cancellationToken).ConfigureAwait(false);
                baseline = await RunNoCaptureAsync(
                    Path.Combine(pairDirectory, "baseline"),
                    workload,
                    settings,
                    cancellationToken).ConfigureAwait(false);
            }

            double observedCpu = captured.Measurement.ProcessorTime.BusyPercentage
                - baseline.ProcessorTime.BusyPercentage;
            double impactCpu = OverheadClassCalculator.PairedImpactPercentagePoints(
                baseline.ProcessorTime.BusyPercentage,
                captured.Measurement.ProcessorTime.BusyPercentage);
            double observedThroughput = 100d
                * (baseline.MessagesPerSecond - captured.Measurement.MessagesPerSecond)
                / baseline.MessagesPerSecond;
            double throughputRegression = OverheadClassCalculator.ThroughputRegressionPercent(
                    baseline.MessagesPerSecond,
                    captured.Measurement.MessagesPerSecond)
                ?? throw new InvalidOperationException("The baseline workload produced no throughput interval.");
            CaptureHealthSnapshot health = captured.Variant.Health;
            string evidencePath = Path.Combine(pairDirectory, "capture", "journal", "capture.icatj");
            pairs.Add(new()
            {
                Pair = index + 1,
                Order = baselineFirst ? "baseline-then-capture" : "capture-then-baseline",
                Baseline = baseline,
                Capture = captured.Measurement,
                ObservedCpuDifferencePercentagePoints = observedCpu,
                CaptureImpactPercentagePoints = impactCpu,
                ObservedThroughputRegressionPercent = observedThroughput,
                ThroughputRegressionPercent = throughputRegression,
                Health = new(
                    health.AdmittedRecords,
                    health.ProviderReportedEventLoss,
                    health.ConsumerReportedBufferLoss,
                    health.ApplicationDrops,
                    captured.Variant.ExactAdmittedProjectionReplay == true,
                    Path.GetRelativePath(outputDirectory, evidencePath)),
            });
        }

        double medianImpact = CaptureImpactMath.Median(pairs.Select(pair => pair.CaptureImpactPercentagePoints));
        double medianRegression = CaptureImpactMath.Median(pairs.Select(pair => pair.ThroughputRegressionPercent));
        bool lossFree = pairs.All(pair =>
            pair.Health.ProviderReportedEventLoss == 0
            && pair.Health.ConsumerReportedBufferLoss == 0
            && pair.Health.ApplicationDrops == 0);
        bool evidenceValid = pairs.All(pair =>
            pair.Health.AdmittedRecords > 0
            && pair.Health.ExactAdmittedProjectionReplay);
        return new()
        {
            SourceId = source.SourceId,
            Pairs = pairs,
            MedianObservedCpuDifferencePercentagePoints = CaptureImpactMath.Median(
                pairs.Select(pair => pair.ObservedCpuDifferencePercentagePoints)),
            MedianCaptureImpactPercentagePoints = medianImpact,
            MedianObservedThroughputRegressionPercent = CaptureImpactMath.Median(
                pairs.Select(pair => pair.ObservedThroughputRegressionPercent)),
            MedianThroughputRegressionPercent = medianRegression,
            Overhead = OverheadClassCalculator.Classify(medianImpact),
            CpuTargetMet = medianImpact < CaptureCpuTargetPercentagePoints,
            ThroughputTargetMet = medianRegression < ThroughputRegressionTargetPercent,
            LossFree = lossFree,
            EvidenceValid = evidenceValid,
        };
    }

    private static async Task<CaptureImpactRunMeasurement> RunNoCaptureAsync(
        string directory,
        string workload,
        ComparisonSettings settings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var meter = new CaptureImpactMeter(settings.Connections * settings.MessagesPerConnection);
        meter.Start();
        long workloadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        int exitCode = await RunWorkloadAsync(workload, directory, settings, cancellationToken)
            .ConfigureAwait(false);
        meter.RecordWorkloadElapsed(System.Diagnostics.Stopwatch.GetElapsedTime(workloadStarted));
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"No-capture workload exited with code {exitCode}.");
        }

        await Task.Delay(TimeSpan.FromSeconds(settings.ReorderGraceSeconds), cancellationToken)
            .ConfigureAwait(false);
        return meter.Complete();
    }

    private static async Task<(CaptureImpactRunMeasurement Measurement, CaptureComparisonVariant Variant)>
        RunImpactCaptureAsync(
            string directory,
            string workload,
            SourceAdmissionPlan source,
            IReadOnlyList<ProviderEnablementRequest> providers,
            ComparisonSettings settings,
            CancellationToken cancellationToken)
    {
        var meter = new CaptureImpactMeter(settings.Connections * settings.MessagesPerConnection);
        CaptureComparisonVariant variant = await RunJournalAsync(
            directory,
            workload,
            [source],
            providers,
            settings,
            cancellationToken,
            meter).ConfigureAwait(false);
        return (meter.Measurement
            ?? throw new InvalidOperationException("The capture run produced no impact interval."), variant);
    }

    private static string SafePathSegment(string value) => string.Concat(
        value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '-'));
}
