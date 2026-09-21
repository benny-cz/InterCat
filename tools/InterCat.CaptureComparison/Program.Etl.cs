using System.Diagnostics;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
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
        CaptureHealthSnapshot health = sink.Snapshot(stop.EventsLost, 0);
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
            Coverage = coverage,
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
            TruthRecords = truth.Count,
            ReplayedRecords = sink.Records.Count,
            ReplayBudgetDrops = health.ApplicationDrops,
            ReplaySourceEventsLost = replay.SourceEventsLost,
            ExactAdmittedProjectionReplay = null,
            Degradations = stop.Degradations,
        };
    }
}
