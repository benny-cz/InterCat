using System.Diagnostics;
using System.Security.Cryptography;
using InterCat.Analysis;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

/// <summary>What the dedicated journal writer thread produced, including anything it refused.</summary>
internal sealed class JournalWriterOutcome
{
    public long FileBytes { get; set; }
    public long Batches { get; set; }
    public int DurableFlushes { get; set; }
    public LatencyHistogram EncodeLatency { get; } = new();
    public LatencyHistogram DurableFlushLatency { get; } = new();
    public byte[]? Fingerprint { get; set; }
    public long EnvelopeRecords { get; set; }
    public long RecordsWithoutAdmissionPlan { get; set; }
    public long ExtendedItemsPersisted { get; set; }
    public long ExtendedItemsPolicyOmitted { get; set; }
    public SortedSet<ushort> ExtendedTypes { get; } = [];
    /// <summary>Null when this host could not read the writing thread's clock. Never zero for unread (R3).</summary>
    public TimeSpan? WriterCpu { get; set; }

    public long WriterAllocatedBytes { get; set; }
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
        CancellationToken cancellationToken,
        CaptureImpactMeter? impactMeter = null)
    {
        string directory = Path.Combine(root, "journal");
        Directory.CreateDirectory(directory);
        string evidencePath = Path.Combine(directory, "capture.icatj");
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("cmpjournal", Environment.ProcessId);
        OwnedSessionPlan sessionPlan = BuildSessionPlan(identity, sources, providers, settings);
        await using var session = new OwnedCaptureSession(sessionPlan, new TraceEventSessionHost());
        CaptureStartResult start = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        EnsureStarted("journal", start.Started, start.FailureReason, start.Providers);

        CaptureClockEvidence clock = session.SourceClock
            ?? throw new InvalidOperationException("A started capture must carry a source clock descriptor.");
        var mapper = new AdmittedEventEnvelopeMapper(sources, clock.Descriptor.Id);
        var outcome = new JournalWriterOutcome();

        // The writer owns one thread from open to terminal frame, so its processor time and allocations
        // belong to the writer stage rather than to whichever pool thread happened to resume a await.
        Task writer = Task.Factory.StartNew(
            () => RunWriterThread(evidencePath, session, mapper, identity.CaptureId, settings, clock, outcome),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        impactMeter?.Start();
        long acquisitionStarted = Stopwatch.GetTimestamp();
        long workloadStarted = Stopwatch.GetTimestamp();
        int workloadExit = await RunWorkloadAsync(workload, directory, settings, cancellationToken)
            .ConfigureAwait(false);
        impactMeter?.RecordWorkloadElapsed(Stopwatch.GetElapsedTime(workloadStarted));
        if (workloadExit != 0)
        {
            throw new InvalidOperationException($"Journal workload exited with code {workloadExit}.");
        }

        await Task.Delay(TimeSpan.FromSeconds(settings.ReorderGraceSeconds), cancellationToken)
            .ConfigureAwait(false);
        impactMeter?.Complete();
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

        CaptureStageSnapshot captureStages = session.ReadStageMetrics();
        CaptureClockEvidence finalClock = session.SourceClock ?? clock;

        long replayStarted = Stopwatch.GetTimestamp();
        using JournalV1Contents replayed = JournalV1Reader.ReadFile(evidencePath);
        var admittedRecords = new List<AdmittedEvent>(settings.RecordBudget);
        using IncrementalHash replayHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long replayedRecords = 0;
        long extendedReplayed = 0;
        foreach (RecordEnvelopeV1 envelope in replayed.Records)
        {
            AdmittedEventEnvelopeMapper.AppendFingerprint(replayHash, envelope);
            AdmittedEventPlan? plan = session.AdmissionTable.Find(
                envelope.Header.ProviderId,
                envelope.Header.EventId,
                envelope.Header.Version);
            if (plan is null)
            {
                throw new InvalidDataException("A replayed journal record has no admission plan.");
            }

            replayedRecords++;
            extendedReplayed += envelope.ExtendedItems.Count;
            if (admittedRecords.Count < settings.RecordBudget)
            {
                admittedRecords.Add(AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan));
            }
        }

        byte[] replayDigest = replayHash.GetHashAndReset();
        bool exact = outcome.Fingerprint is not null
            && outcome.EnvelopeRecords == replayedRecords
            && CryptographicOperations.FixedTimeEquals(outcome.Fingerprint, replayDigest);
        double replayMilliseconds = Stopwatch.GetElapsedTime(replayStarted).TotalMilliseconds;

        var writerMetrics = new WriterStageMetrics
        {
            PayloadBytesWritten = outcome.FileBytes,
            EncodeLatency = outcome.EncodeLatency.Read(),
            DurableFlushLatency = outcome.DurableFlushLatency.Read(),
            WriterThreadCpu = outcome.WriterCpu,
            WriterThreadAllocatedBytes = outcome.WriterAllocatedBytes,
        };

        IReadOnlyList<TruthRecord> truth = await ReadTruthAsync(directory, cancellationToken).ConfigureAwait(false);
        TcpCoverageResult coverage = BuildCoverage(
            truth,
            admittedRecords,
            session.AdmissionTable,
            identity.CaptureId);
        return new()
        {
            Name = "admitted-journal-v1",
            CaptureId = identity.CaptureId,
            Providers = start.Providers,
            Health = stop.Health,
            Coverage = CoverageSummary.From(coverage),
            Evidence = new(
                "journal-v1",
                Path.GetRelativePath(root, evidencePath),
                outcome.FileBytes,
                outcome.EnvelopeRecords,
                outcome.DurableFlushes,
                true),
            Timing = new(acquisitionMilliseconds, finalizationMilliseconds, replayMilliseconds),
            Stages = new()
            {
                Acquisition = captureStages,
                AcquisitionNote =
                    "Callback latency, allocations and processor time are the delivery thread's own totals; "
                    + "the queue high-water mark is sampled where the callback enqueues, not once a second.",
                Writer = writerMetrics,
                WriterNote =
                    "One dedicated thread owns the file from its header to its terminal frame, so its "
                    + "processor time and allocations are the writer stage's own. journal-v1 performs no "
                    + "durable flush - that is IC-016's - so this probe opens the file write-through and "
                    + "flushes each batch itself to measure what the commit protocol will cost.",
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
            ReplayedRecords = replayedRecords,
            ReplayBudgetDrops = replayedRecords - admittedRecords.Count,
            ReplaySourceEventsLost = 0,
            ExactAdmittedProjectionReplay = exact,
            Saturation = LoadSeries.Assess(
                stop.Health,
                captureStages,
                new(
                    outcome.DurableFlushes,
                    writerMetrics.DurableFlushLatency.TotalNanoseconds / 1_000_000d,
                    outcome.WriterCpu?.TotalMilliseconds ?? 0),
                acquisitionMilliseconds),
            SourceLossKnown = true,
            Degradations = stop.Degradations,
        };
    }

    /// <summary>
    /// Owns the journal file from its header to its terminal frame on one thread, so that the processor
    /// time and allocations it reports are the writer stage's own rather than whichever pool thread
    /// happened to resume an await.
    /// </summary>
    /// <remarks>
    /// The file is opened here rather than by <see cref="JournalV1Writer.CreateNewFile"/> because this
    /// probe measures a cost journal-v1 does not itself pay: the format contract leaves durability to
    /// IC-016, so the writer never flushes to the device. Opening write-through and flushing each batch
    /// here measures what that protocol will cost on this machine, and keeps the format honest about
    /// owning none of it.
    /// </remarks>
    private static void RunWriterThread(
        string evidencePath,
        OwnedCaptureSession session,
        AdmittedEventEnvelopeMapper mapper,
        CaptureId captureId,
        ComparisonSettings settings,
        CaptureClockEvidence clock,
        JournalWriterOutcome outcome)
    {
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        bool cpuReadable = ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtStart);
        using IncrementalHash fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var file = new FileStream(
                Path.GetFullPath(evidencePath),
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.Read,
                    BufferSize = 128 * 1024,
                    Options = FileOptions.SequentialScan | FileOptions.WriteThrough,
                });
            using (JournalV1Writer writer = JournalV1Writer.Create(
                file,
                captureId,
                clock.Descriptor,
                DateTimeOffset.UtcNow,
                settings.JournalBatchRecords))
            {
                writer.WriteSchemas(mapper.Schemas);
                ChannelDrain(session, mapper, captureId, file, writer, fingerprint, settings, outcome);
                // The final batch and the terminal frame are an encode; getting them to the device is a
                // flush. They are separate costs and are timed separately, so a run short enough to fill
                // no batch still records a flush sample rather than reporting none.
                long completeStarted = Stopwatch.GetTimestamp();
                writer.Complete();
                long completeFlushStarted = Stopwatch.GetTimestamp();
                outcome.EncodeLatency.RecordTicks(completeFlushStarted - completeStarted, Stopwatch.Frequency);
                file.Flush(flushToDisk: true);
                outcome.DurableFlushLatency.RecordTicks(
                    Stopwatch.GetTimestamp() - completeFlushStarted,
                    Stopwatch.Frequency);
                outcome.DurableFlushes++;
                outcome.Batches = writer.BatchesWritten;
                outcome.FileBytes = file.Length;
            }

            outcome.Fingerprint = fingerprint.GetHashAndReset();
            if (cpuReadable && ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtEnd))
            {
                outcome.WriterCpu = (cpuAtEnd - cpuAtStart).Total;
            }

            outcome.WriterAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome.Failure = exception;
        }
    }

    /// <summary>
    /// Drains the admission queue into the journal until the capture closes it. Ownership of each pooled
    /// envelope passes to the writer at <see cref="JournalV1Writer.Append"/>, which returns its buffers
    /// when the batch is written, so nothing here outlives its single owner (section 18.1).
    /// </summary>
    private static void ChannelDrain(
        OwnedCaptureSession session,
        AdmittedEventEnvelopeMapper mapper,
        CaptureId captureId,
        FileStream file,
        JournalV1Writer writer,
        IncrementalHash fingerprint,
        ComparisonSettings settings,
        JournalWriterOutcome outcome)
    {
        int sinceBatch = 0;
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

                RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, captureId);
                outcome.ExtendedItemsPersisted += envelope.ExtendedItems.Count;
                outcome.ExtendedItemsPolicyOmitted += envelope.OmittedExtendedItemCount;
                AdmittedEventEnvelopeMapper.AppendFingerprint(fingerprint, envelope);

                // The append that fills the batch is the one that encodes and writes it, so it is the
                // only one whose latency is an encode cost. Timing every append would measure a list add.
                bool closes = ++sinceBatch >= settings.JournalBatchRecords;
                if (!closes)
                {
                    writer.Append(envelope);
                    outcome.EnvelopeRecords++;
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                writer.Append(envelope);
                outcome.EncodeLatency.RecordTicks(Stopwatch.GetTimestamp() - started, Stopwatch.Frequency);
                outcome.EnvelopeRecords++;
                sinceBatch = 0;

                long flushStarted = Stopwatch.GetTimestamp();
                file.Flush(flushToDisk: true);
                outcome.DurableFlushLatency.RecordTicks(Stopwatch.GetTimestamp() - flushStarted, Stopwatch.Frequency);
                outcome.DurableFlushes++;
            }

            if (!session.Records.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
            {
                return;
            }
        }
    }
}
