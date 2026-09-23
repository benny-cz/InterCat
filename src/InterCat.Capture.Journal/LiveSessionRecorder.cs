using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>What one live recording captured and published into its session.</summary>
public sealed record LiveRecordingResult
{
    public required CaptureStartResult Start { get; init; }

    /// <summary>The session's stop, with its health counters; null when it never started.</summary>
    public CaptureStopResult? Stop { get; init; }

    /// <summary>The last generation the recording published; null when the capture never started.</summary>
    public DerivedGenerationResult? Generation { get; init; }

    /// <summary>How many journal chunks the recording published, each in a generation of its own.</summary>
    public int Publications { get; init; }

    /// <summary>
    /// How many compaction generations coalesced the recording's small publications (§20.1, ADR-026): while it
    /// recorded, whenever enough had accumulated, and once when it stopped.
    /// </summary>
    public int Compactions { get; init; }

    /// <summary>
    /// Why a compaction was not published, or null. The recording itself was, and the session is whole; it is left
    /// with more segments than it needs until `icat compact` coalesces them.
    /// </summary>
    public string? CompactionFailure { get; init; }

    /// <summary>Records written to the admitted journal, each of which derived its rows.</summary>
    public long JournaledRecords { get; init; }

    /// <summary>
    /// What the capture's sources could observe and what they lost (`coverage-v1`); null when a loss counter could not
    /// be read, so the session's coverage is unknown.
    /// </summary>
    public CoverageLedgerV1? Coverage { get; init; }
}

/// <summary>
/// Records a live capture straight into a session: the owned session's admitted records become the session's own
/// `journal-v1` evidence in acquisition order, each derives its `observation-v1` rows as the import's do, and the capture
/// is published through the §20.1 commit protocol. With a publication interval, every interval that admitted records
/// completes a journal chunk and publishes a generation holding every chunk so far, so a reader can follow the capture;
/// the generation published when the capture stops adds the live coverage epoch built from the session's own counters.
/// It is the runtime the broker's capture binding wraps (IC-014), usable directly by an elevated command line.
/// </summary>
public static class LiveSessionRecorder
{
    /// <summary>
    /// Starts the capture, records until <paramref name="recordUntil"/> completes, then stops and publishes. Cancelling
    /// <paramref name="recordUntil"/>'s token ends the recording early; what was captured is still published. Only a
    /// failure to capture, write or publish leaves the session without its final generation.
    /// </summary>
    /// <param name="publishEvery">
    /// How often to publish what was recorded so far, or null to publish once when the capture stops.
    /// </param>
    /// <param name="compaction">
    /// When small publications are coalesced; §20.1's targets by default. A step while recording rewrites at most one
    /// segment's worth of rows, so the writer is held up for a bounded time.
    /// </param>
    public static async Task<LiveRecordingResult> RecordAsync(
        OwnedSessionPlan plan,
        IEtwSessionHost host,
        SessionStore store,
        Func<CancellationToken, Task> recordUntil,
        DateTimeOffset committedUtc,
        DerivedGenerationOptions? options = null,
        TimeSpan? publishEvery = null,
        CompactionOptions? compaction = null,
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

        if (compaction?.Validate() is { } compactionProblem)
        {
            throw new ArgumentException(compactionProblem, nameof(compaction));
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
        using var chunks = new ChunkWriter(
            session,
            plan,
            store,
            clock.Descriptor,
            committedUtc,
            options ?? DerivedGenerationOptions.Default,
            publishEvery,
            compaction ?? CompactionOptions.Default);

        // One writer thread from the first record to the last, so the journal takes records in acquisition order (I7).
        Task<long> writer = Task.Factory.StartNew(
            chunks.Drain,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // A writer that fails ends the recording at once: capturing on would only fill the bounded queue and drop
        // records the journal can no longer take.
        using var recording = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = writer.ContinueWith(
            _ => recording.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        CaptureStopResult stop;
        long journaled;
        try
        {
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

        DerivedGenerationResult published = chunks.PublishLast(ledger);
        return new()
        {
            Start = start,
            Stop = stop,
            Generation = published,
            Publications = chunks.Publications,
            Compactions = chunks.Compactions,
            CompactionFailure = chunks.CompactionFailure,
            JournaledRecords = journaled,
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
        private readonly CompactionOptions compaction;
        private readonly AdmittedEventEnvelopeMapper mapper;
        private readonly ObservationNormalizerV1 normalizer;
        private readonly Stopwatch sincePublished = Stopwatch.StartNew();
        private DerivedGenerationBuilder builder;
        private ulong recordsInChunk;
        private long journaled;
        private int smallUnits;

        public ChunkWriter(
            OwnedCaptureSession session,
            OwnedSessionPlan plan,
            SessionStore store,
            SourceClockDescriptor clock,
            DateTimeOffset createdUtc,
            DerivedGenerationOptions options,
            TimeSpan? publishEvery,
            CompactionOptions compaction)
        {
            this.session = session;
            this.plan = plan;
            this.store = store;
            this.clock = clock;
            this.createdUtc = createdUtc;
            this.options = options;
            this.publishEvery = publishEvery;
            this.compaction = compaction;
            mapper = new AdmittedEventEnvelopeMapper(plan.Sources, clock.Id);
            normalizer = new ObservationNormalizerV1(clock);
            builder = BeginChunk(stagePlan: true);
        }

        public int Publications { get; private set; }

        public int Compactions { get; private set; }

        public string? CompactionFailure { get; private set; }

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
        /// Publishes the last chunk, with the capture's coverage ledger when it could be measured, then coalesces every run
        /// of small publications left, so a finished recording opens from as few segments as its rows need.
        /// </summary>
        public DerivedGenerationResult PublishLast(CoverageLedgerV1? ledger)
        {
            if (ledger is not null)
            {
                builder.StageCoverageLedger(ledger);
            }

            DerivedGenerationResult published = builder.Complete(DateTimeOffset.UtcNow, CancellationToken.None);
            Publications++;
            Count(published);
            return Compact(compaction with { RowBudget = 0 })?.Generation ?? published;
        }

        public void Dispose() => builder.Dispose();

        private bool Due() =>
            publishEvery is { } interval && recordsInChunk > 0 && sincePublished.Elapsed >= interval;

        private void Write(in AdmittedEvent admitted)
        {
            AdmittedEventPlan descriptor = session.AdmissionTable.FindBySourceIndex(
                admitted.SourceIndex,
                admitted.EventId,
                admitted.Version)
                ?? throw new InvalidDataException(
                    $"An admitted record names descriptor {admitted.SourceIndex}/{admitted.EventId}/"
                    + $"v{admitted.Version}, which the compiled plan does not describe.");

            // Each envelope's buffers pass to the journal at append, so nothing here outlives its single owner (§18.1).
            RecordEnvelopeV1 envelope = mapper.ToEnvelope(in admitted, descriptor, plan.Identity.CaptureId);
            ObservationRowV1 row = normalizer.ToRow(envelope, descriptor, (ulong)journaled);
            builder.AddRow(row);
            foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, descriptor, row))
            {
                builder.AddFieldRow(field);
            }

            builder.Journal.Append(envelope);
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
            Publications++;
            Count(published);

            // A step coalesces the oldest small publications, at most one segment's worth of rows, before the next chunk
            // takes the next generation's number.
            if (smallUnits >= compaction.SmallUnitsBeforeCompaction)
            {
                _ = Compact(compaction with { RowBudget = compaction.RowBudget > 0 ? compaction.RowBudget : options.RowsPerSegment });
            }

            builder = BeginChunk(stagePlan: false);
        }

        /// <summary>Counts a publication whose rows and bytes are below what a coalesced output should reach.</summary>
        private void Count(DerivedGenerationResult published)
        {
            if (IsSmall(published))
            {
                smallUnits++;
            }
        }

        private bool IsSmall(DerivedGenerationResult published) =>
            published.RowCount > 0
            && published.RowCount < compaction.TargetRows
            && published.Segments.Sum(segment => segment.LengthBytes) < compaction.TargetBytes;

        /// <summary>
        /// Publishes one compaction, or nothing when no run of small publications is left. A compaction that fails leaves
        /// the recording published and whole, so it is reported and not tried again rather than ending the capture.
        /// </summary>
        private CompactionResult? Compact(CompactionOptions bounds)
        {
            if (CompactionFailure is not null)
            {
                return null;
            }

            try
            {
                CompactionResult? result = SegmentCompaction.Compact(store, DateTimeOffset.UtcNow, bounds, options);
                if (result is not null)
                {
                    Compactions++;
                    smallUnits += (IsSmall(result.Generation) ? 1 : 0) - result.Plan.Coalesced.Count();
                }

                return result;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                CompactionFailure = exception.Message;
                return null;
            }
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
                    Derivation = ObservationNormalizerV1.ContractVersion,
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
            if (publishEvery is not { } interval || recordsInChunk == 0)
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
