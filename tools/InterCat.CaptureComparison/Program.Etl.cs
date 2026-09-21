using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    /// <summary>
    /// Measures what admission itself allocates, by replaying the same evidence through the same adapter
    /// with an empty admission table. R9 and R11 are rules about InterCat's callback work, and a figure
    /// that includes the adapter's dispatch cannot say whether they are met (section 18.3).
    /// </summary>
    private static AllocationAttribution AttributeAllocations(
        string evidencePath,
        OwnedSessionPlan sessionPlan,
        CancellationToken cancellationToken)
    {
        // Both runs use a sink that keeps nothing. A sink that stored its records would allocate more
        // than either path under measurement and would swamp the difference being measured.
        (long dispatchBytes, long dispatchObserved) = Replay(
            sessionPlan with { Sources = [], Providers = [] });
        (long admissionBytes, long admissionObserved) = Replay(sessionPlan);
        return new()
        {
            RecordsObserved = Math.Max(dispatchObserved, admissionObserved),
            DispatchOnlyBytes = dispatchBytes,
            FullAdmissionBytes = admissionBytes,
        };

        (long Bytes, long Observed) Replay(OwnedSessionPlan plan)
        {
            var counting = new CountingSink();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = EtlAdmissionReplay.Replay(evidencePath, plan, counting, cancellationToken);
            return (GC.GetAllocatedBytesForCurrentThread() - before, counting.Observed);
        }
    }

    private static async Task<CaptureComparisonVariant> RunEtlAsync(
        string root,
        string workload,
        IReadOnlyList<SourceAdmissionPlan> sources,
        IReadOnlyList<ProviderEnablementRequest> providers,
        ComparisonSettings settings,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(root, "etl");
        Directory.CreateDirectory(directory);
        string evidencePath = Path.Combine(directory, "diagnostic-evidence.etl");
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("cmpetl", Environment.ProcessId);
        OwnedSessionPlan sessionPlan = BuildSessionPlan(identity, sources, providers, settings);
        var etlPlan = new EtlFileCapturePlan { Session = sessionPlan, OutputPath = evidencePath };
        await using var capture = new OwnedEtlFileCapture(etlPlan, new TraceEventEtlFileSessionHost());
        EtlCaptureStartResult start = await capture.StartAsync(cancellationToken).ConfigureAwait(false);
        EnsureStarted("ETL", start.Started, start.FailureReason, start.Providers);

        long acquisitionStarted = Stopwatch.GetTimestamp();
        int workloadExit = await RunWorkloadAsync(workload, directory, settings, cancellationToken)
            .ConfigureAwait(false);
        if (workloadExit != 0)
        {
            throw new InvalidOperationException($"ETL workload exited with code {workloadExit}.");
        }

        await Task.Delay(TimeSpan.FromSeconds(settings.ReorderGraceSeconds), cancellationToken)
            .ConfigureAwait(false);
        double acquisitionMilliseconds = Stopwatch.GetElapsedTime(acquisitionStarted).TotalMilliseconds;

        long finalizationStarted = Stopwatch.GetTimestamp();
        EtlCaptureStopResult stop = await capture.StopAsync(cancellationToken).ConfigureAwait(false);
        double finalizationMilliseconds = Stopwatch.GetElapsedTime(finalizationStarted).TotalMilliseconds;

        // Replay runs on this thread from first record to last, so its own totals isolate the stage.
        var sink = new ReplaySink(settings.RecordBudget);
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        bool cpuReadable = ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtStart);
        EtlAdmissionReplayResult replay = EtlAdmissionReplay.Replay(
            evidencePath,
            sessionPlan,
            sink,
            cancellationToken);
        ThreadCpuReading? replayCpu = null;
        if (cpuReadable && ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtEnd))
        {
            replayCpu = cpuAtEnd - cpuAtStart;
        }

        CaptureStageSnapshot replayStages = sink.ReadStages(
            GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
            replayCpu);
        AllocationAttribution allocations = AttributeAllocations(evidencePath, sessionPlan, cancellationToken);
        CaptureHealthSnapshot health = sink.Snapshot(stop.EventsLost ?? 0, 0);
        IReadOnlyList<TruthRecord> truth = await ReadTruthAsync(directory, cancellationToken).ConfigureAwait(false);
        var table = new EventAdmissionTable(sources);
        TcpCoverageResult coverage = BuildCoverage(truth, sink.Records, table, identity.CaptureId);
        var extendedTypes = new SortedSet<ushort>();
        foreach (AdmittedEvent record in sink.Records)
        {
            for (int index = 0; index < record.ExtendedItemsCopied; index++)
            {
                extendedTypes.Add(record.GetExtendedItem(index).Type);
            }
        }

        return new()
        {
            Name = "diagnostic-etl",
            CaptureId = identity.CaptureId,
            Providers = start.Providers,
            Health = health,
            Coverage = CoverageSummary.From(coverage),
            Evidence = new(
                "original-diagnostic-etl",
                Path.GetRelativePath(root, evidencePath),
                stop.FileLengthBytes,
                replay.AssignedRecordCount,
                0,
                stop.FileLengthBytes > 0),
            Timing = new(
                acquisitionMilliseconds,
                finalizationMilliseconds,
                replay.Elapsed.TotalMilliseconds),
            Stages = new()
            {
                Acquisition = null,
                AcquisitionNote =
                    "Windows writes this file from the logger's own threads. InterCat runs no callback "
                    + "during acquisition, so there is no in-process callback cost to measure — which is "
                    + "itself the finding, not a missing measurement.",
                Writer = null,
                WriterNote = "The ETW logger owns buffering and flushing; InterCat cannot attribute its cost.",
                Replay = replayStages,
                ReplayNote =
                    "Offline replay runs the same admission callback as live capture, measured on the "
                    + "replaying thread.",
            },
            Clock = new()
            {
                Descriptor = null,
                Confidence = SourceClockConfidence.Unconfirmed,
                RefusalReason = null,
                ReplayedIdentical = null,
            },
            ExtendedData = new()
            {
                CopiedInCallback = replayStages.ExtendedItemsCopied,
                OmittedInCallback = replayStages.ExtendedItemsOmitted,
                PersistedInEnvelopes = 0,
                OmittedByPersistencePolicy = 0,
                ReplayedIdentical = replayStages.ExtendedItemsCopied,
                TypesObserved = [.. extendedTypes.Select(EtwExtendedDataTypes.Describe)],
                UnavailableReason = replayStages.ExtendedDataUnavailableReason,
            },
            Allocations = allocations,
            TruthRecords = truth.Count,
            ReplayedRecords = sink.Records.Count,
            ReplayBudgetDrops = health.ApplicationDrops,
            ReplaySourceEventsLost = replay.SourceEventsLost,
            ExactAdmittedProjectionReplay = null,
            Saturation = LoadSeries.Assess(
                health,
                null,
                null,
                acquisitionMilliseconds,
                sourceLossKnown: stop.EventsLost is not null),
            SourceLossKnown = stop.EventsLost is not null,
            Degradations = stop.Degradations,
        };
    }
}
