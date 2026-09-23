using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Diagnostics.Tracing;

namespace InterCat.Capture.Journal;

/// <summary>An import that produced a session: what it read, and the generation it published.</summary>
public sealed record EtlSessionImportResult(EtlImportResult Import, DerivedGenerationResult? Generation);

/// <summary>What one ETL import read, admitted, refused and identified.</summary>
public sealed record EtlImportResult(
    CanonicalImportSummary Summary,
    string EtlPath,
    long EtlLengthBytes,
    long TicksPerSecond,
    long ObservedRecords,
    long AdmittedRecords,
    long OmittedRecords,
    long UndecodableRecords,
    long SourceEventsLost,
    double ElapsedMilliseconds,
    CoverageLedgerV1 Coverage)
{
    /// <summary>
    /// The share of delivered records this import carries. A source event the ETL itself lost was never
    /// in the file, so it is reported beside the counts rather than folded into them (§20.6).
    /// </summary>
    public double AdmittedShare => ObservedRecords == 0 ? 0 : (double)AdmittedRecords / ObservedRecords;
}

/// <summary>
/// Replays a standalone ETL through the same admission adapter live capture uses, and feeds the admitted
/// envelopes straight into the canonical importer. Nothing here needs elevation: reading an ETL file is
/// an ordinary file read, and R16 keeps every parser out of the privileged broker.
/// </summary>
public static class EtlCanonicalImport
{
    /// <summary>How many of a file's leading records are sampled to derive and check its tick rate.</summary>
    private const int ClockSampleCount = 4_096;

    /// <summary>The smallest reading span a rate may be derived from, in native ticks and milliseconds.</summary>
    private const long MinimumTickSpan = 1_000;

    private const double MinimumMillisecondSpan = 0.5;

    /// <summary>How far a re-derived reading may sit from the file's own, in milliseconds.</summary>
    private const double RateToleranceMilliseconds = 0.001;

    /// <summary>Keys and counts an ETL without publishing anything. It produces an index, not a session.</summary>
    public static EtlImportResult Import(
        string etlPath,
        OwnedSessionPlan admissionPlan,
        RetainedEvidencePolicy retainedEvidence = RetainedEvidencePolicy.MetadataOnly,
        CanonicalImportOptions? bounds = null,
        CancellationToken cancellationToken = default) =>
        Run(etlPath, admissionPlan, retainedEvidence, bounds, null, cancellationToken).Import;

    /// <summary>
    /// Imports an ETL into a session: the admitted records become the session's own `journal-v1` evidence, the
    /// normalizer derives an `observation-v1` row from each of them, and the generation is published through
    /// the §20.1 commit protocol with the committed boundary naming that journal.
    /// </summary>
    /// <remarks>
    /// This is the point at which an import stops being a computation and becomes something a reader can open.
    /// It needs no elevation: writing a session directory and parsing evidence are both ordinary file work,
    /// and R16 keeps every parser out of the privileged broker.
    /// </remarks>
    public static EtlSessionImportResult ImportIntoSession(
        string etlPath,
        OwnedSessionPlan admissionPlan,
        SessionStore store,
        DateTimeOffset committedUtc,
        RetainedEvidencePolicy retainedEvidence = RetainedEvidencePolicy.MetadataOnly,
        CanonicalImportOptions? bounds = null,
        DerivedGenerationOptions? generationOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Current is not null)
        {
            throw new InvalidOperationException(
                "An ETL import publishes only into a session with no generation. Appending another copy of "
                + "the capture would count its observations twice. Open the existing session, use 'icat "
                + "rederive' for its retained journal, or choose a new empty directory for a separate import.");
        }

        EtlSessionImportResult result = Run(
            etlPath,
            admissionPlan,
            retainedEvidence,
            bounds,
            new(store, committedUtc, generationOptions ?? DerivedGenerationOptions.Default),
            cancellationToken);
        return result.Generation is null
            ? throw new InvalidOperationException("A session import did not publish a generation.")
            : result;
    }

    private static EtlSessionImportResult Run(
        string etlPath,
        OwnedSessionPlan admissionPlan,
        RetainedEvidencePolicy retainedEvidence,
        CanonicalImportOptions? bounds,
        SessionTarget? target,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etlPath);
        ArgumentNullException.ThrowIfNull(admissionPlan);

        // Bounds are checked before the file is opened, so an impossible import is refused without
        // reading a byte of evidence (contracts/import-v1.md section 6).
        string? boundProblem = (bounds ?? CanonicalImportOptions.Default).Validate();
        if (boundProblem is not null)
        {
            throw new ArgumentException(boundProblem, nameof(bounds));
        }

        string path = Path.GetFullPath(etlPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The ETL evidence file does not exist.", path);
        }

        // The identity comes from the bytes, before a single record is read from them (§18.4).
        ImportSourceIdentity source = ImportSourceIdentity.OfFile(ImportSourceKind.StandaloneEtl, path);
        (long ticksPerSecond, long firstTicks) = ReadClockAnchor(path);
        SourceClockDescriptor clock = ImportedSourceClock.Derive(source, ticksPerSecond, firstTicks);
        var mapper = new AdmittedEventEnvelopeMapper(admissionPlan.Sources, clock.Id);
        var plans = new AdmittedEventPlanIndex(admissionPlan.Sources);

        using var builder = new CanonicalImportBuilder(
            source,
            retainedEvidence,
            clock,
            mapper.Schemas,
            bounds);

        // The capture identity is derived from the import, never minted per run: it is inside every raw-record
        // and observation identity, so a fresh one would make two imports of one file disagree about which
        // records they hold (§18.4, plan revision 26).
        CaptureId captureId = builder.Identity.CaptureId;
        DerivedGenerationBuilder? generation = null;
        try
        {
            if (target is not null)
            {
                generation = DerivedGenerationBuilder.Begin(
                    target.Store,
                    new()
                    {
                        CaptureId = captureId,
                        ClockId = clock.Id,
                        TimestampEncoding = clock.Encoding,
                        Derivation = ObservationNormalizerV1.ContractVersion,
                    },
                    clock,
                    target.CommittedUtc,
                    target.Options);

                // The schema table precedes the first batch, and the mapper already holds every descriptor
                // the compiled plan admits, so it is complete before a record is read (§18.3).
                JournalNormalizationPlanV1 normalizerPlan = JournalNormalizationPlanV1.FromSources(admissionPlan.Sources);
                normalizerPlan.ValidateAgainst(mapper.Schemas);
                generation.StageNormalizerPlan(normalizerPlan.Encode());
                generation.Journal.WriteSchemas(mapper.Schemas);
            }

            var sink = new ImportSink(
                mapper,
                plans,
                builder,
                captureId,
                generation,
                new ObservationNormalizerV1(clock),
                new CaptureCoverageTally(admissionPlan, CoverageAcquisition.EtlImport),
                cancellationToken);
            long started = Stopwatch.GetTimestamp();
            EtlAdmissionReplayResult replay = EtlAdmissionReplay.Replay(path, admissionPlan, sink, cancellationToken);
            sink.Rethrow();

            // What the file's sources delivered and lost is kept beside the evidence: the journal holds admitted
            // records only, and a reopened session must still say what it could not have seen (R21).
            // A reported zero is a fact; an absent loss entry would say nothing (`coverage-v1` §3).
            CoverageLedgerV1 coverage = sink.Coverage.ToLedger([new() { Layer = LossLayer.SourceSession, Lost = replay.SourceEventsLost }]);
            using CanonicalImportIndex index = builder.Complete(cancellationToken);
            generation?.StageCoverageLedger(coverage);
            DerivedGenerationResult? published = generation?.Complete(target!.CommittedUtc, cancellationToken);

            return new(
                new(
                    index.Summary,
                    path,
                    new FileInfo(path).Length,
                    ticksPerSecond,
                    sink.Observed,
                    sink.Admitted,
                    sink.Omitted,
                    sink.Undecodable,
                    replay.SourceEventsLost,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    coverage),
                published);
        }
        finally
        {
            generation?.Dispose();
        }
    }

    private sealed record SessionTarget(
        SessionStore Store,
        DateTimeOffset CommittedUtc,
        DerivedGenerationOptions Options);

    /// <summary>
    /// Derives the file's own tick rate and its first reading, and checks the rate against the file
    /// before using it. The adapter library does not expose the recorded performance-counter frequency,
    /// and this process's own frequency is a different machine's fact, so the rate is computed from the
    /// file's native readings and their relative times and then verified on every sample. A file whose
    /// readings span too little time, or that does not reproduce under the derived rate, is refused
    /// rather than read at an assumed rate.
    /// </summary>
    private static (long TicksPerSecond, long FirstTicks) ReadClockAnchor(string path)
    {
        var ticks = new List<long>(ClockSampleCount);
        var milliseconds = new List<double>(ClockSampleCount);
        using (var source = new ETWTraceEventSource(path, TraceEventSourceType.FileOnly))
        {
            void OnEvent(TraceEvent data)
            {
#pragma warning disable CS0618 // I8 requires the original clock reading; relative milliseconds cannot replace it.
                ticks.Add(data.TimeStampQPC);
#pragma warning restore CS0618
                milliseconds.Add(data.TimeStampRelativeMSec);
                if (ticks.Count >= ClockSampleCount)
                {
                    source.StopProcessing();
                }
            }

            source.AllEvents += OnEvent;
            try
            {
                source.Process();
            }
            finally
            {
                source.AllEvents -= OnEvent;
            }
        }

        if (ticks.Count < 2)
        {
            throw new InvalidDataException(
                $"'{path}' carries fewer than two readings, so nothing in it states the rate its readings "
                + "are on. It is refused rather than read at an assumed rate.");
        }

        int lowest = 0;
        int highest = 0;
        for (int index = 1; index < ticks.Count; index++)
        {
            lowest = ticks[index] < ticks[lowest] ? index : lowest;
            highest = ticks[index] > ticks[highest] ? index : highest;
        }

        long tickSpan = ticks[highest] - ticks[lowest];
        double millisecondSpan = milliseconds[highest] - milliseconds[lowest];
        if (tickSpan < MinimumTickSpan || millisecondSpan < MinimumMillisecondSpan)
        {
            throw new InvalidDataException(
                $"'{path}' spans {tickSpan} readings over {millisecondSpan:F3} ms, which is too little to "
                + "derive the rate they are on. It is refused rather than read at an assumed rate.");
        }

        long ticksPerSecond = (long)Math.Round(tickSpan * 1000.0 / millisecondSpan);
        for (int index = 0; index < ticks.Count; index++)
        {
            double derived = (ticks[index] - ticks[lowest]) * 1000.0 / ticksPerSecond;
            double recorded = milliseconds[index] - milliseconds[lowest];
            if (Math.Abs(derived - recorded) > RateToleranceMilliseconds)
            {
                throw new InvalidDataException(
                    $"'{path}' does not reproduce its own relative times at {ticksPerSecond} ticks per "
                    + $"second: reading {ticks[index]} derives {derived:F6} ms where the file records "
                    + $"{recorded:F6} ms. Its rate is refused rather than assumed.");
            }
        }

        return (ticksPerSecond, ticks[0]);
    }

    /// <summary>Resolves the descriptor plan one admitted record was admitted against.</summary>
    private sealed class AdmittedEventPlanIndex
    {
        private readonly Dictionary<(int Source, int EventId, int Version), AdmittedEventPlan> plans = [];

        public AdmittedEventPlanIndex(IReadOnlyList<SourceAdmissionPlan> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);
            foreach (SourceAdmissionPlan source in sources)
            {
                foreach (AdmittedEventPlan plan in source.Events)
                {
                    plans[(plan.SourceIndex, plan.EventId, plan.Version)] = plan;
                }
            }
        }

        public AdmittedEventPlan Resolve(in AdmittedEvent admitted) =>
            plans.TryGetValue((admitted.SourceIndex, admitted.EventId, admitted.Version), out AdmittedEventPlan? plan)
                ? plan
                : throw new InvalidDataException(
                    $"An admitted record names descriptor {admitted.SourceIndex}/{admitted.EventId}/"
                    + $"v{admitted.Version}, which the compiled plan does not describe.");
    }

    /// <summary>
    /// Keys each admitted record and releases its envelope immediately. Holding the envelopes would undo
    /// the bound the importer exists to keep, and the index entry is all the import needs.
    /// </summary>
    private sealed class ImportSink(
        AdmittedEventEnvelopeMapper mapper,
        AdmittedEventPlanIndex plans,
        CanonicalImportBuilder builder,
        CaptureId captureId,
        DerivedGenerationBuilder? generation,
        ObservationNormalizerV1 normalizer,
        CaptureCoverageTally coverage,
        CancellationToken cancellationToken) : IAdmittedEventSink
    {
        private Exception? failure;
        private ulong journalIndex;

        public CaptureCoverageTally Coverage => coverage;

        public long Observed { get; private set; }

        public long Admitted { get; private set; }

        public long Omitted { get; private set; }

        public long Undecodable { get; private set; }

        public void OnObserved(in DeliveredRecord delivered)
        {
            Observed++;
            coverage.Delivered(in delivered);
        }

        public bool Admit(in AdmittedEvent admitted)
        {
            if (failure is not null)
            {
                return false;
            }

            try
            {
                AdmittedEventPlan plan = plans.Resolve(in admitted);
                coverage.Admitted(plan.ProviderGuid, plan.EventId, plan.Version, queued: true);
                RecordEnvelopeV1 envelope = mapper.ToEnvelope(in admitted, plan, captureId);
                if (generation is null)
                {
                    using (envelope)
                    {
                        builder.Add(envelope, cancellationToken);
                    }

                    Admitted++;
                    return true;
                }

                // The index entry and the derived row are taken first; the journal takes ownership of the
                // envelope last, because appending it transfers the buffers it holds (§18.1).
                builder.Add(envelope, cancellationToken);
                ObservationRowV1 row = normalizer.ToRow(envelope, plan, journalIndex);
                generation.AddRow(row);
                foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, plan, row))
                {
                    generation.AddFieldRow(field);
                }

                generation.Journal.Append(envelope);
                journalIndex++;
                Admitted++;
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The replay is driven by a native callback; throwing through it would leave the trace
                // source half-stopped. The failure is kept and raised once the replay has ended.
                failure = exception;
                return false;
            }
        }

        public void OnOmitted(OmissionReason reason, in DeliveredRecord delivered)
        {
            Omitted++;
            coverage.Omitted(reason, in delivered);
        }

        public void OnUndecodable(UndecodableReason reason, in DeliveredRecord delivered)
        {
            Undecodable++;
            coverage.Undecodable(reason, in delivered);
        }

        public void Rethrow()
        {
            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}
