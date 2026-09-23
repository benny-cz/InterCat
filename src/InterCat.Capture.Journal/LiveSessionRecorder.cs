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

    /// <summary>The generation the recording published; null when the capture never started.</summary>
    public DerivedGenerationResult? Generation { get; init; }

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
/// `journal-v1` evidence in acquisition order, each derives its `observation-v1` rows as the import's do, and the
/// generation is published through the §20.1 commit protocol when the capture stops, with a live coverage epoch built
/// from the session's own counters. It is the runtime the broker's capture binding wraps (IC-014), usable directly by an
/// elevated command line.
/// </summary>
public static class LiveSessionRecorder
{
    /// <summary>
    /// Starts the capture, records until <paramref name="recordUntil"/> completes, then stops and publishes. Cancelling
    /// <paramref name="recordUntil"/>'s token ends the recording early; what was captured is still published. Only a
    /// failure to capture, write or publish leaves the session without a generation.
    /// </summary>
    public static async Task<LiveRecordingResult> RecordAsync(
        OwnedSessionPlan plan,
        IEtwSessionHost host,
        SessionStore store,
        Func<CancellationToken, Task> recordUntil,
        DateTimeOffset committedUtc,
        DerivedGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recordUntil);
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
        var mapper = new AdmittedEventEnvelopeMapper(plan.Sources, clock.Descriptor.Id);
        CaptureId captureId = plan.Identity.CaptureId;
        using DerivedGenerationBuilder generation = DerivedGenerationBuilder.Begin(
            store,
            new()
            {
                CaptureId = captureId,
                ClockId = clock.Descriptor.Id,
                TimestampEncoding = clock.Descriptor.Encoding,
                Derivation = ObservationNormalizerV1.ContractVersion,
            },
            clock.Descriptor,
            committedUtc,
            options ?? DerivedGenerationOptions.Default);

        // The descriptor interpretation and the schema table precede the first record, so a later machine can re-derive
        // this journal without consulting its own schemas (normalizer-plan-v1).
        JournalNormalizationPlanV1 normalizerPlan = JournalNormalizationPlanV1.FromSources(plan.Sources);
        normalizerPlan.ValidateAgainst(mapper.Schemas);
        generation.StageNormalizerPlan(normalizerPlan.Encode());
        generation.Journal.WriteSchemas(mapper.Schemas);

        // One writer thread from the first record to the last, so the journal takes records in acquisition order (I7).
        var normalizer = new ObservationNormalizerV1(clock.Descriptor);
        Task<long> writer = Task.Factory.StartNew(
            () => Drain(session, mapper, generation, normalizer, captureId),
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
        // wrong clock, so nothing is published over it (I8).
        if (session.SourceClock is { Confidence: SourceClockConfidence.Refused } refused)
        {
            throw new InvalidDataException(
                $"The capture's readings are not on the clock its journal names: {refused.RefusalReason} "
                + "Nothing was published.");
        }

        // A ledger states what the session lost, so it is published only when every loss counter was read: an unread
        // counter is not a reported zero, and the session's coverage then stays unknown rather than claimed (R21).
        if (session.SourceLossUnreadable)
        {
            return new()
            {
                Start = start,
                Stop = stop,
                Generation = generation.Complete(committedUtc, CancellationToken.None),
                JournaledRecords = journaled,
            };
        }

        CoverageLedgerV1 ledger = coverage.ToLedger(
        [
            new() { Layer = LossLayer.SourceSession, Lost = stop.Health.ProviderReportedEventLoss },
            new() { Layer = LossLayer.ConsumerBuffers, Lost = stop.Health.ConsumerReportedBufferLoss },
            new() { Layer = LossLayer.CallbackQueue, Lost = stop.Health.ApplicationDrops },

            // Admitted into the queue and never written: the writer's own loss, measured rather than assumed zero.
            new() { Layer = LossLayer.Storage, Lost = Math.Max(0, stop.Health.AdmittedRecords - journaled) },
        ]);
        generation.StageCoverageLedger(ledger);
        DerivedGenerationResult published = generation.Complete(committedUtc, CancellationToken.None);
        return new()
        {
            Start = start,
            Stop = stop,
            Generation = published,
            JournaledRecords = journaled,
            Coverage = ledger,
        };
    }

    /// <summary>
    /// Drains the admission queue into the journal and the derived rows until the capture closes it. Each envelope's
    /// buffers pass to the journal at append, so nothing here outlives its single owner (section 18.1).
    /// </summary>
    private static long Drain(
        OwnedCaptureSession session,
        AdmittedEventEnvelopeMapper mapper,
        DerivedGenerationBuilder generation,
        ObservationNormalizerV1 normalizer,
        CaptureId captureId)
    {
        ulong journalIndex = 0;
        while (true)
        {
            while (session.Records.TryRead(out AdmittedEvent admitted))
            {
                AdmittedEventPlan plan = session.AdmissionTable.FindBySourceIndex(
                    admitted.SourceIndex,
                    admitted.EventId,
                    admitted.Version)
                    ?? throw new InvalidDataException(
                        $"An admitted record names descriptor {admitted.SourceIndex}/{admitted.EventId}/"
                        + $"v{admitted.Version}, which the compiled plan does not describe.");
                RecordEnvelopeV1 envelope = mapper.ToEnvelope(in admitted, plan, captureId);
                ObservationRowV1 row = normalizer.ToRow(envelope, plan, journalIndex);
                generation.AddRow(row);
                foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, plan, row))
                {
                    generation.AddFieldRow(field);
                }

                generation.Journal.Append(envelope);
                journalIndex++;
            }

            if (!session.Records.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
            {
                return checked((long)journalIndex);
            }
        }
    }
}
