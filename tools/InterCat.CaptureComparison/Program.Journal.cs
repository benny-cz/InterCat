using System.Diagnostics;
using System.Security.Cryptography;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

/// <summary>What the dedicated journal writer thread produced, including anything it refused.</summary>
internal sealed class JournalWriterOutcome
{
    public JournalProbeFileSummary? Summary { get; set; }
    public byte[]? Fingerprint { get; set; }
    public long EnvelopeRecords { get; set; }
    public long RecordsWithoutAdmissionPlan { get; set; }
    public long ExtendedItemsPersisted { get; set; }
    public long ExtendedItemsPolicyOmitted { get; set; }
    public SortedSet<ushort> ExtendedTypes { get; } = [];
    public Exception? Failure { get; set; }
}

internal static partial class Program
{
    private static async Task<CaptureComparisonVariant> RunJournalAsync(
        string root,
        string workload,
        IReadOnlyList<SourceAdmissionPlan> sources,
        IReadOnlyList<ProviderEnablementRequest> providers,
        ComparisonSettings settings,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(root, "journal");
        Directory.CreateDirectory(directory);
        string evidencePath = Path.Combine(directory, "callback-envelope.ijp0");
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("cmpjournal", Environment.ProcessId);
        OwnedSessionPlan sessionPlan = BuildSessionPlan(identity, sources, providers, settings);
        await using var session = new OwnedCaptureSession(sessionPlan, new TraceEventSessionHost());
        CaptureStartResult start = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        EnsureStarted("journal", start.Started, start.FailureReason, start.Providers);

        CaptureClockEvidence clock = session.SourceClock
            ?? throw new InvalidOperationException("A started capture must carry a source clock descriptor.");
        var mapper = new CallbackEnvelopeMapper(sources, clock.Descriptor.Id);
        var outcome = new JournalWriterOutcome();

        // The writer owns one thread from open to terminal frame, so its processor time and allocations
        // belong to the writer stage rather than to whichever pool thread happened to resume a await.
        Task writer = Task.Factory.StartNew(
            () => RunWriterThread(evidencePath, session, mapper, identity.CaptureId, settings, clock, outcome),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        long acquisitionStarted = Stopwatch.GetTimestamp();
        int workloadExit = await RunWorkloadAsync(workload, directory, settings, cancellationToken)
            .ConfigureAwait(false);
        if (workloadExit != 0)
        {
            throw new InvalidOperationException($"Journal workload exited with code {workloadExit}.");
        }

        await Task.Delay(TimeSpan.FromSeconds(settings.ReorderGraceSeconds), cancellationToken)
            .ConfigureAwait(false);
        double acquisitionMilliseconds = Stopwatch.GetElapsedTime(acquisitionStarted).TotalMilliseconds;

        long finalizationStarted = Stopwatch.GetTimestamp();
        CaptureStopResult stop = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        await writer.ConfigureAwait(false);
        double finalizationMilliseconds = Stopwatch.GetElapsedTime(finalizationStarted).TotalMilliseconds;
        if (outcome.Failure is not null)
        {
            throw new InvalidOperationException(
                $"The journal writer thread stopped early: {outcome.Failure.Message}",
                outcome.Failure);
        }

        JournalProbeFileSummary fileSummary = outcome.Summary
            ?? throw new InvalidOperationException("The journal writer produced no file summary.");
        CaptureStageSnapshot captureStages = session.ReadStageMetrics();
        CaptureClockEvidence finalClock = session.SourceClock ?? clock;

        long replayStarted = Stopwatch.GetTimestamp();
        JournalProbeFileContents replayed = JournalProbeFileWriter.ReadCompleteFile(evidencePath);
        var admittedRecords = new List<AdmittedEvent>(Math.Min(replayed.Records.Count, settings.RecordBudget));
        using IncrementalHash replayHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long extendedReplayed = 0;
        foreach (JournalProbeEnvelope envelope in replayed.Records)
        {
            CallbackEnvelopeMapper.AppendFingerprint(replayHash, envelope);
            AdmittedEventPlan? plan = session.AdmissionTable.Find(
                envelope.ProviderGuid,
                envelope.EventId,
                envelope.Version);
            if (plan is null)
            {
                throw new InvalidDataException("A replayed journal record has no admission plan.");
            }

            extendedReplayed += envelope.ExtendedItems.Count;
            if (admittedRecords.Count < settings.RecordBudget)
            {
                admittedRecords.Add(CallbackEnvelopeMapper.FromEnvelope(envelope, plan));
            }
        }

        byte[] replayDigest = replayHash.GetHashAndReset();
        bool exact = outcome.Fingerprint is not null
            && outcome.EnvelopeRecords == replayed.Records.Count
            && CryptographicOperations.FixedTimeEquals(outcome.Fingerprint, replayDigest);
        double replayMilliseconds = Stopwatch.GetElapsedTime(replayStarted).TotalMilliseconds;

        IReadOnlyList<TruthRecord> truth = await ReadTruthAsync(directory, cancellationToken).ConfigureAwait(false);
        TcpCoverageResult coverage = BuildCoverage(
            truth,
            admittedRecords,
            session.AdmissionTable,
            identity.CaptureId);
        return new()
        {
            Name = "admitted-journal-callback-envelope-v0",
            CaptureId = identity.CaptureId,
            Providers = start.Providers,
            Health = stop.Health,
            Coverage = CoverageSummary.From(coverage),
            Evidence = new(
                "journal-probe-v0",
                Path.GetRelativePath(root, evidencePath),
                fileSummary.Bytes,
                fileSummary.Records,
                fileSummary.DurableFlushes,
                true),
            Timing = new(acquisitionMilliseconds, finalizationMilliseconds, replayMilliseconds),
            Stages = new()
            {
                Acquisition = captureStages,
                AcquisitionNote =
                    "Callback latency, allocations and processor time are the delivery thread's own totals; "
                    + "the queue high-water mark is sampled where the callback enqueues, not once a second.",
                Writer = fileSummary.Writer,
                WriterNote =
                    "One dedicated thread owns the file from its header to its terminal frame, so its "
                    + "processor time and allocations are the writer stage's own.",
                Replay = null,
                ReplayNote = "Journal replay reads framed batches; it runs no admission callback to measure.",
            },
            Clock = new()
            {
                Descriptor = finalClock.Descriptor,
                Confidence = finalClock.Confidence,
                RefusalReason = finalClock.RefusalReason,
                ReplayedIdentical = replayed.SourceClock is { } stored && stored == finalClock.Descriptor,
            },
            ExtendedData = new()
            {
                CopiedInCallback = captureStages.ExtendedItemsCopied,
                OmittedInCallback = captureStages.ExtendedItemsOmitted,
                PersistedInEnvelopes = outcome.ExtendedItemsPersisted,
                OmittedByPersistencePolicy = outcome.ExtendedItemsPolicyOmitted,
                ReplayedIdentical = extendedReplayed,
                TypesObserved = [.. outcome.ExtendedTypes.Select(EtwExtendedDataTypes.Describe)],
                UnavailableReason = captureStages.ExtendedDataUnavailableReason,
            },
            Allocations = null,
            TruthRecords = truth.Count,
            ReplayedRecords = replayed.Records.Count,
            ReplayBudgetDrops = replayed.Records.Count - admittedRecords.Count,
            ReplaySourceEventsLost = 0,
            ExactAdmittedProjectionReplay = exact,
            Saturation = LoadSeries.Assess(
                stop.Health,
                captureStages,
                new(
                    fileSummary.DurableFlushes,
                    fileSummary.Writer.DurableFlushLatency.TotalNanoseconds / 1_000_000d,
                    fileSummary.Writer.WriterThreadCpu?.TotalMilliseconds ?? 0),
                acquisitionMilliseconds),
            SourceLossKnown = true,
            Degradations = stop.Degradations,
        };
    }

    private static void RunWriterThread(
        string evidencePath,
        OwnedCaptureSession session,
        CallbackEnvelopeMapper mapper,
        CaptureId captureId,
        ComparisonSettings settings,
        CaptureClockEvidence clock,
        JournalWriterOutcome outcome)
    {
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        bool cpuReadable = ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtStart);
        using IncrementalHash fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using JournalProbeFileWriter writer = JournalProbeFileWriter.CreateNew(
            evidencePath,
            settings.JournalBatchRecords,
            flushEachBatch: true,
            sourceClock: clock.Descriptor,
            synchronous: true);
        try
        {
            ChannelDrain(session, mapper, captureId, writer, fingerprint, outcome);
            outcome.Fingerprint = fingerprint.GetHashAndReset();
            TimeSpan? cpu = null;
            if (cpuReadable && ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtEnd))
            {
                cpu = (cpuAtEnd - cpuAtStart).Total;
            }

            outcome.Summary = writer.Complete(
                cpu,
                GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome.Failure = exception;
        }
    }

    private static void ChannelDrain(
        OwnedCaptureSession session,
        CallbackEnvelopeMapper mapper,
        CaptureId captureId,
        JournalProbeFileWriter writer,
        IncrementalHash fingerprint,
        JournalWriterOutcome outcome)
    {
        while (true)
        {
            while (session.Records.TryRead(out AdmittedEvent admitted))
            {
                AdmittedEventPlan? plan = session.AdmissionTable.FindBySourceIndex(
                    admitted.SourceIndex,
                    admitted.EventId,
                    admitted.Version);
                if (plan is null)
                {
                    outcome.RecordsWithoutAdmissionPlan++;
                    continue;
                }

                for (int index = 0; index < admitted.ExtendedItemsCopied; index++)
                {
                    outcome.ExtendedTypes.Add(admitted.GetExtendedItem(index).Type);
                }

                JournalProbeEnvelope envelope = mapper.ToEnvelope(admitted, plan, captureId);
                outcome.ExtendedItemsPersisted += envelope.ExtendedItems.Count;
                outcome.ExtendedItemsPolicyOmitted += envelope.OmittedExtendedItemCount;
                CallbackEnvelopeMapper.AppendFingerprint(fingerprint, envelope);
                writer.Append(envelope);
                outcome.EnvelopeRecords++;
            }

            if (!session.Records.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
            {
                return;
            }
        }
    }
}
