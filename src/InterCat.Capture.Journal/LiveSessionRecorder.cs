using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>What a live recording publishes.</summary>
public enum LiveRecordingOutput
{
    /// <summary>Admitted evidence and the rows derived from it: a session every command reads.</summary>
    Session = 1,

    /// <summary>
    /// Admitted evidence only: journal chunks, the normalizer plan and the coverage ledger, with no rows. A privileged
    /// recorder publishes this, and an unprivileged follower mirrors it and derives the rows (§9, ADR-027).
    /// </summary>
    EvidenceOnly = 2,
}

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
/// Records a live capture straight into a session: `LiveRecorder` writes the admitted evidence, one journal chunk per
/// publication, and this derives each record's `observation-v1` rows as the import's do and coalesces small publications
/// as it goes (ADR-021, ADR-022, ADR-026). It is what an elevated `icat record` runs. With evidence only, nothing is
/// derived, and `icat follow` derives the session in an ordinary process instead, as the broker requires (ADR-027).
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
    /// <param name="output">
    /// Whether the session gets rows, or only the admitted evidence an unprivileged follower derives them from.
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
        LiveRecordingOutput output = LiveRecordingOutput.Session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (compaction?.Validate() is { } compactionProblem)
        {
            throw new ArgumentException(compactionProblem, nameof(compaction));
        }

        if (!Enum.IsDefined(output))
        {
            throw new ArgumentOutOfRangeException(nameof(output), output, "A recording publishes a session or evidence only.");
        }

        DerivedGenerationOptions bounds = options ?? DerivedGenerationOptions.Default;
        RowDerivation? derivation = null;
        LiveCaptureResult captured = await LiveRecorder.RecordAsync(
            plan,
            host,
            store,
            recordUntil,
            committedUtc,
            bounds,
            publishEvery,
            output == LiveRecordingOutput.Session
                ? clock => derivation = new RowDerivation(clock, store, bounds, compaction ?? CompactionOptions.Default)
                : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new()
        {
            Start = captured.Start,
            Stop = captured.Stop,
            Generation = captured.Generation,
            Publications = captured.Publications,
            Compactions = derivation?.Compactions ?? 0,
            CompactionFailure = derivation?.CompactionFailure,
            JournaledRecords = captured.JournaledRecords,
            Coverage = captured.Coverage,
        };
    }

    /// <summary>
    /// Derives each admitted record's rows as the import does, with a journal index counted across the capture's chunks,
    /// and coalesces small publications: a bounded step whenever enough have accumulated, and every run left when the
    /// capture stops (ADR-026). Only the recorder's writer thread calls it.
    /// </summary>
    private sealed class RowDerivation(
        SourceClockDescriptor clock,
        SessionStore store,
        DerivedGenerationOptions options,
        CompactionOptions compaction) : ILiveRecordingDerivation
    {
        private readonly ObservationNormalizerV1 normalizer = new(clock);
        private int smallUnits;

        public NormalizerContractVersion Derivation => ObservationNormalizerV1.ContractVersion;

        public int Compactions { get; private set; }

        public string? CompactionFailure { get; private set; }

        public void Derive(RecordEnvelopeV1 envelope, AdmittedEventPlan descriptor, ulong journalIndex, DerivedGenerationBuilder builder)
        {
            ObservationRowV1 row = normalizer.ToRow(envelope, descriptor, journalIndex);
            builder.AddRow(row);
            foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, descriptor, row))
            {
                builder.AddFieldRow(field);
            }
        }

        public DerivedGenerationResult Published(DerivedGenerationResult published, bool last)
        {
            if (IsSmall(published))
            {
                smallUnits++;
            }

            if (last)
            {
                return Compact(compaction with { RowBudget = 0 })?.Generation ?? published;
            }

            // A step coalesces the oldest small publications, at most one segment's worth of rows, before the next chunk
            // takes the next generation's number.
            if (smallUnits >= compaction.SmallUnitsBeforeCompaction)
            {
                _ = Compact(compaction with { RowBudget = compaction.RowBudget > 0 ? compaction.RowBudget : options.RowsPerSegment });
            }

            return published;
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
    }
}
