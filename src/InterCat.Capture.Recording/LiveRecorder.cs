using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// What a live recording does beyond its admitted evidence. The recorder writes the journal; a derivation, when there is
/// one, adds each record's rows to the generation being built and acts on every publication. A privileged recorder has
/// none (§9, ADR-027).
/// </summary>
public interface ILiveRecordingDerivation
{
    /// <summary>The normalizer contract the rows this derivation adds are derived under.</summary>
    NormalizerContractVersion Derivation { get; }

    /// <summary>
    /// Adds one admitted record's rows. It runs before the record is appended, because appending passes the envelope's
    /// buffers to the journal, which becomes their only owner (§18.1).
    /// </summary>
    void Derive(RecordEnvelopeV1 envelope, AdmittedEventPlan descriptor, ulong journalIndex, DerivedGenerationBuilder builder);

    /// <summary>
    /// Runs after each publication and before the next chunk takes the next generation's number, <paramref name="last"/>
    /// when the capture has stopped. Returns the generation that is current afterwards.
    /// </summary>
    DerivedGenerationResult Published(DerivedGenerationResult published, bool last);
}

/// <summary>What one live recording captured and published.</summary>
public sealed record LiveCaptureResult
{
    public required CaptureStartResult Start { get; init; }

    /// <summary>The session's stop, with its health counters; null when it never started.</summary>
    public CaptureStopResult? Stop { get; init; }

    /// <summary>The generation current when the recording ended; null when the capture never started.</summary>
    public DerivedGenerationResult? Generation { get; init; }

    /// <summary>How many journal chunks the recording published, each in a generation of its own.</summary>
    public int Publications { get; init; }

    /// <summary>Records written to the admitted journal.</summary>
    public long JournaledRecords { get; init; }

    /// <summary>True when the journal byte ceiling ended acquisition; unjournaled admissions are storage loss.</summary>
    public bool JournalQuotaReached { get; init; }

    /// <summary>
    /// What the capture's sources could observe and what they lost (`coverage-v1`); null when a loss counter could not
    /// be read, so the session's coverage is unknown.
    /// </summary>
    public CoverageLedgerV1? Coverage { get; init; }
}

/// <summary>
/// Records a live capture's admitted evidence into a session (ADR-021, ADR-022). The owned session's admitted records
/// become the session's own `journal-v1` evidence in acquisition order, published through the §20.1 commit protocol: with
/// a publication interval, one journal chunk per generation, so a reader can follow the capture. The normalizer plan comes
/// with the first generation, and the live coverage ledger with the last. It derives nothing itself; a derivation, when
/// given, adds rows. With none, it is what the broker's capture runtime runs (IC-014, ADR-027).
/// </summary>
public static class LiveRecorder
{
    /// <summary>
    /// Starts the capture, records until <paramref name="recordUntil"/> completes, then stops and publishes. Cancelling
    /// <paramref name="recordUntil"/>'s token ends the recording early; what was captured is still published. Only a
    /// failure to capture, write or publish leaves the session without its final generation.
    /// </summary>
    /// <param name="publishEvery">
    /// How often to publish what was recorded so far, or null to publish once when the capture stops.
    /// </param>
    /// <param name="derive">
    /// Creates the derivation once the capture's clock is known, or null to publish admitted evidence alone.
    /// </param>
    /// <param name="onReady">
    /// Called once after ETW and the journal writer are running, before waiting for the capture to end. It is not
    /// called if startup is refused; inspect the completed result for that refusal.
    /// </param>
    public static async Task<LiveCaptureResult> RecordAsync(
        OwnedSessionPlan plan,
        IEtwSessionHost host,
        SessionStore store,
        Func<CancellationToken, Task> recordUntil,
        DateTimeOffset committedUtc,
        DerivedGenerationOptions? options = null,
        TimeSpan? publishEvery = null,
        Func<SourceClockDescriptor, ILiveRecordingDerivation>? derive = null,
        Action<CaptureStartResult>? onReady = null,
        long? maximumJournalBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recordUntil);
        if (publishEvery is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(publishEvery), "A publication interval is positive.");
        }

        if (maximumJournalBytes is { } maximum && maximum < 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJournalBytes), "The journal quota is at least 1 MiB.");
        }

        if (maximumJournalBytes is not null && derive is not null)
        {
            throw new ArgumentException(
                "A byte-bounded live recorder is evidence-only: derived rows cannot precede a refused journal append.",
                nameof(derive));
        }

        if (store.Current is not null)
        {
            throw new InvalidOperationException(
                "A live recording publishes only into a session with no generation. Recording into an existing session "
                + "would mix two captures' evidence under one generation; choose a new empty directory.");
        }

        var coverage = new CaptureCoverageTally(plan, CoverageAcquisition.LiveCapture);
        await using var session = new OwnedCaptureSession(plan, host, observer: coverage);
        CaptureStartResult start = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!start.Started)
        {
            return new() { Start = start };
        }

        HashSet<string> enabledSources = [.. start.Providers.Where(provider => provider.Enabled).Select(provider => provider.SourceId)];
        coverage.RestrictToEnabledProviders(
            plan.Providers.Where(provider => enabledSources.Contains(provider.SourceId)).Select(provider => provider.ProviderGuid));
        CaptureClockEvidence clock = session.SourceClock
            ?? throw new InvalidOperationException("A started capture carries a source clock descriptor.");
        using var quotaStop = new CancellationTokenSource();
        using var chunks = new ChunkWriter(
            session,
            plan,
            store,
            clock.Descriptor,
            committedUtc,
            options ?? DerivedGenerationOptions.Default,
            publishEvery,
            derive?.Invoke(clock.Descriptor),
            maximumJournalBytes,
            quotaStop.Cancel);

        // One writer thread from the first record to the last, so the journal takes records in acquisition order (I7).
        var writerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long> writer = Task.Factory.StartNew(
            () =>
            {
                writerReady.TrySetResult();
                return chunks.Drain();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // A writer that fails ends the recording at once: capturing on would only fill the bounded queue and drop
        // records the journal can no longer take.
        using var recording = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quotaStop.Token);
        _ = writer.ContinueWith(
            _ => recording.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        CaptureStopResult stop;
        long journaled;
        try
        {
            // A broker may acknowledge Start only after ETW, the source clock and the single journal
            // writer are ready. Startup refusal never calls this hook; the completed result carries it.
            await writerReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            onReady?.Invoke(start);
            try
            {
                await recordUntil(recording.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // An early end is a stop, not a failure: what was captured is still the capture's evidence.
            }
        }
        finally
        {
            stop = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            journaled = await writer.ConfigureAwait(false);
        }

        // A reading that turned out not to be on the clock the journal names would present every record against the
        // wrong clock, so nothing more is published over it (I8).
        ThrowIfClockRefused(session);

        // The ledger states what the whole capture lost, so it is published with the last chunk, and only when every loss
        // counter was read: an unread counter is not a reported zero, and coverage then stays unknown (R21).
        CoverageLedgerV1? ledger = null;
        if (!session.SourceLossUnreadable)
        {
            ledger = coverage.ToLedger(
            [
                new() { Layer = LossLayer.SourceSession, Lost = stop.Health.ProviderReportedEventLoss },
                new() { Layer = LossLayer.ConsumerBuffers, Lost = stop.Health.ConsumerReportedBufferLoss },
                new() { Layer = LossLayer.CallbackQueue, Lost = stop.Health.ApplicationDrops },

                // Admitted into the queue and never written: the writer's own loss, measured rather than assumed zero.
                new() { Layer = LossLayer.Storage, Lost = Math.Max(0, stop.Health.AdmittedRecords - journaled) },
            ]);
        }

        DerivedGenerationResult published = chunks.PublishLast(ledger, stop.ProvidersStopped, stop.CallbacksDrained);
        return new()
        {
            Start = start,
            Stop = stop,
            Generation = published,
            Publications = chunks.Publications,
            JournaledRecords = journaled,
            JournalQuotaReached = chunks.JournalQuotaReached,
            Coverage = ledger,
        };
    }

    private static void ThrowIfClockRefused(OwnedCaptureSession session)
    {
        if (session.SourceClock is { Confidence: SourceClockConfidence.Refused } refused)
        {
            throw new InvalidDataException(
                $"The capture's readings are not on the clock its journal names: {refused.RefusalReason} "
                + "Nothing more was published.");
        }
    }

    /// <summary>
    /// The journal chunk being written and the generation it will publish. Each chunk is a complete `journal-v1` file of
    /// this capture - its own header, clock and schema table, then its batches and a terminal frame - so every published
    /// file stays immutable; record ordinals continue from one chunk to the next, and a row's journal index counts its
    /// record across the capture's chunks in order. Only the writer thread touches it until the capture has stopped.
    /// </summary>
    private sealed class ChunkWriter : IDisposable
    {
        private readonly OwnedCaptureSession session;
        private readonly OwnedSessionPlan plan;
        private readonly SessionStore store;
        private readonly SourceClockDescriptor clock;
        private readonly DateTimeOffset createdUtc;
        private readonly DerivedGenerationOptions options;
        private readonly TimeSpan? publishEvery;
        private readonly ILiveRecordingDerivation? derivation;
        private readonly long? maximumJournalBytes;
        private readonly Action onJournalQuotaReached;
        private readonly AdmittedEventEnvelopeMapper mapper;
        private readonly Stopwatch sincePublished = Stopwatch.StartNew();
        private DerivedGenerationBuilder builder;
        private readonly long emptyChunkBytes;
        private long publishedJournalBytes;
        private bool rolloverBlocked;
        private ulong recordsInChunk;
        private long journaled;

        public ChunkWriter(
            OwnedCaptureSession session,
            OwnedSessionPlan plan,
            SessionStore store,
            SourceClockDescriptor clock,
            DateTimeOffset createdUtc,
            DerivedGenerationOptions options,
            TimeSpan? publishEvery,
            ILiveRecordingDerivation? derivation,
            long? maximumJournalBytes,
            Action onJournalQuotaReached)
        {
            this.session = session;
            this.plan = plan;
            this.store = store;
            this.clock = clock;
            this.createdUtc = createdUtc;
            this.options = options;
            this.publishEvery = publishEvery;
            this.derivation = derivation;
            this.maximumJournalBytes = maximumJournalBytes;
            this.onJournalQuotaReached = onJournalQuotaReached;
            mapper = new AdmittedEventEnvelopeMapper(plan.Sources, clock.Id);
            builder = BeginChunk(stagePlan: true);
            emptyChunkBytes = builder.Journal.ProjectedCompleteLength;
        }

        public int Publications { get; private set; }

        public bool JournalQuotaReached { get; private set; }

        /// <summary>Drains the admission queue until the capture closes it, publishing a chunk whenever one is due.</summary>
        public long Drain()
        {
            while (true)
            {
                while (session.Records.TryRead(out AdmittedEvent admitted))
                {
                    Write(in admitted);
                    if (Due())
                    {
                        PublishChunk();
                    }
                }

                if (Due())
                {
                    PublishChunk();
                }

                if (!WaitForRecords())
                {
                    return journaled;
                }
            }
        }

        /// <summary>
        /// Publishes the last chunk, with the capture's coverage ledger when it could be measured; the derivation then acts
        /// on it as the capture's last publication.
        /// </summary>
        public DerivedGenerationResult PublishLast(
            CoverageLedgerV1? ledger,
            bool providersStopped,
            bool callbacksDrained)
        {
            if (ledger is not null)
            {
                builder.StageCoverageLedger(ledger);
            }

            DateTimeOffset finalizedUtc = DateTimeOffset.UtcNow;
            builder.StageCaptureFinalization(new CaptureFinalizationV1
            {
                Contract = CaptureFinalizationV1.ContractName,
                CaptureId = plan.Identity.CaptureId.Value,
                FinalizedUtc = finalizedUtc,
                ProvidersStopped = providersStopped,
                CallbacksDrained = callbacksDrained,
            });
            DerivedGenerationResult published = builder.Complete(finalizedUtc, CancellationToken.None);
            publishedJournalBytes = checked(publishedJournalBytes + published.JournalBytes);
            Publications++;
            return derivation?.Published(published, last: true) ?? published;
        }

        public void Dispose() => builder.Dispose();

        private bool Due()
        {
            if (JournalQuotaReached || rolloverBlocked || publishEvery is not { } interval
                || recordsInChunk == 0 || sincePublished.Elapsed < interval)
            {
                return false;
            }

            // A live publication must leave enough allowance for a complete next journal even if
            // Stop arrives immediately. Once that cannot fit, keep this chunk open for finalization.
            if (maximumJournalBytes is { } maximum
                && checked(publishedJournalBytes + builder.Journal.ProjectedCompleteLength + emptyChunkBytes) > maximum)
            {
                rolloverBlocked = true;
                return false;
            }

            return true;
        }

        private void Write(in AdmittedEvent admitted)
        {
            if (JournalQuotaReached)
            {
                return;
            }

            AdmittedEventPlan descriptor = session.AdmissionTable.FindBySourceIndex(
                admitted.SourceIndex,
                admitted.EventId,
                admitted.Version)
                ?? throw new InvalidDataException(
                    $"An admitted record names descriptor {admitted.SourceIndex}/{admitted.EventId}/"
                    + $"v{admitted.Version}, which the compiled plan does not describe.");

            // Each envelope's buffers pass to the journal at append, so nothing here outlives its single owner (§18.1).
            RecordEnvelopeV1 envelope = mapper.ToEnvelope(in admitted, descriptor, plan.Identity.CaptureId);
            derivation?.Derive(envelope, descriptor, (ulong)journaled, builder);
            if (maximumJournalBytes is { } maximum)
            {
                if (!builder.Journal.TryAppendWithin(envelope, maximum - publishedJournalBytes))
                {
                    envelope.Dispose();
                    JournalQuotaReached = true;
                    onJournalQuotaReached();
                    return;
                }
            }
            else
            {
                builder.Journal.Append(envelope);
            }

            recordsInChunk++;
            journaled++;
        }

        /// <summary>
        /// Completes the current chunk and publishes a generation holding every chunk so far. A reading off the journal's
        /// clock stops publication before anything is presented against the wrong clock (I8).
        /// </summary>
        private void PublishChunk()
        {
            ThrowIfClockRefused(session);
            DerivedGenerationResult published = builder.Complete(DateTimeOffset.UtcNow, CancellationToken.None);
            builder.Dispose();
            publishedJournalBytes = checked(publishedJournalBytes + published.JournalBytes);
            Publications++;

            // Whatever the derivation publishes after a chunk takes a generation before the next chunk does.
            _ = derivation?.Published(published, last: false);
            builder = BeginChunk(stagePlan: false);
            rolloverBlocked = false;
        }

        private DerivedGenerationBuilder BeginChunk(bool stagePlan)
        {
            DerivedGenerationBuilder next = DerivedGenerationBuilder.Begin(
                store,
                new()
                {
                    CaptureId = plan.Identity.CaptureId,
                    ClockId = clock.Id,
                    TimestampEncoding = clock.Encoding,
                    Derivation = derivation?.Derivation ?? NormalizerContractVersion.V1,
                },
                clock,
                createdUtc,
                options);
            try
            {
                // The descriptor interpretation is retained once, with the first chunk, and later generations carry it;
                // every chunk repeats the schema table, so each is a complete journal of its own (normalizer-plan-v1).
                if (stagePlan)
                {
                    JournalNormalizationPlanV1 normalizerPlan = JournalNormalizationPlanV1.FromSources(plan.Sources);
                    normalizerPlan.ValidateAgainst(mapper.Schemas);
                    next.StageNormalizerPlan(normalizerPlan.Encode());
                }

                next.Journal.WriteSchemas(mapper.Schemas);
                if (maximumJournalBytes is { } maximum
                    && next.Journal.ProjectedCompleteLength > maximum - publishedJournalBytes)
                {
                    throw new InvalidDataException(
                        "The journal header and schema table alone exceed the configured journal byte allowance.");
                }

                recordsInChunk = 0;
                sincePublished.Restart();
                return next;
            }
            catch
            {
                next.Dispose();
                throw;
            }
        }

        /// <summary>Waits for more records, or until the next chunk is due; false when the capture has closed the queue.</summary>
        private bool WaitForRecords()
        {
            if (publishEvery is not { } interval || recordsInChunk == 0 || rolloverBlocked)
            {
                return session.Records.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }

            TimeSpan remaining = interval - sincePublished.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            using var due = new CancellationTokenSource(remaining);
            try
            {
                return session.Records.WaitToReadAsync(due.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }
    }
}
