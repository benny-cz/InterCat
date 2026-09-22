using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Diagnostics.Tracing;

namespace InterCat.Capture.Journal;

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
    double ElapsedMilliseconds)
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

    public static EtlImportResult Import(
        string etlPath,
        OwnedSessionPlan admissionPlan,
        RetainedEvidencePolicy retainedEvidence = RetainedEvidencePolicy.MetadataOnly,
        CanonicalImportOptions? bounds = null,
        CancellationToken cancellationToken = default)
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
        var sink = new ImportSink(mapper, plans, builder, cancellationToken);
        long started = Stopwatch.GetTimestamp();
        EtlAdmissionReplayResult replay = EtlAdmissionReplay.Replay(path, admissionPlan, sink, cancellationToken);
        sink.Rethrow();
        using CanonicalImportIndex index = builder.Complete(cancellationToken);

        return new(
            index.Summary,
            path,
            new FileInfo(path).Length,
            ticksPerSecond,
            sink.Observed,
            sink.Admitted,
            sink.Omitted,
            sink.Undecodable,
            replay.SourceEventsLost,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

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
        CancellationToken cancellationToken) : IAdmittedEventSink
    {
        private Exception? failure;

        public long Observed { get; private set; }

        public long Admitted { get; private set; }

        public long Omitted { get; private set; }

        public long Undecodable { get; private set; }

        public void OnObserved() => Observed++;

        public bool Admit(in AdmittedEvent admitted)
        {
            if (failure is not null)
            {
                return false;
            }

            try
            {
                AdmittedEventPlan plan = plans.Resolve(in admitted);
                using RecordEnvelopeV1 envelope = mapper.ToEnvelope(in admitted, plan, CaptureId.New());
                builder.Add(envelope, cancellationToken);
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

        public void OnOmitted(OmissionReason reason) => Omitted++;

        public void OnUndecodable(UndecodableReason reason) => Undecodable++;

        public void Rethrow()
        {
            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}
