using System.Security.Cryptography;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>What one pass of following an evidence session did, and where the follow stands.</summary>
public sealed record FollowStep
{
    /// <summary>Chunks this pass mirrored, and the records they held.</summary>
    public required int MirroredChunks { get; init; }

    public required long MirroredRecords { get; init; }

    /// <summary>Chunks the derived session now holds, and chunks the evidence session has committed.</summary>
    public required int DerivedChunks { get; init; }

    public required int EvidenceChunks { get; init; }

    /// <summary>Records derived so far, across every mirrored chunk.</summary>
    public required long DerivedRecords { get; init; }

    /// <summary>
    /// Whether the capture has stopped and every chunk is mirrored. New evidence sessions prove this with a
    /// capture-finalization marker; a coverage ledger is copied when available and remains a legacy finality signal.
    /// </summary>
    public required bool Finished { get; init; }

    /// <summary>The derived session's current generation, or null when nothing has been mirrored yet.</summary>
    public required long? DerivedGeneration { get; init; }

    /// <summary>Compactions this pass published (§20.1, ADR-026).</summary>
    public required int Compactions { get; init; }
}

/// <summary>
/// Follows a privileged recorder's evidence session from an ordinary process (§9, ADR-027). The recorder publishes
/// admitted evidence only - journal chunks, the normalizer plan, finalization marker and optional coverage ledger - and nothing privileged derives,
/// queries or compacts. The follower mirrors each committed chunk byte for byte into a session of its own, checked
/// against the evidence manifest's digest, derives its rows there exactly as a recording that derives in-process would,
/// and copies the plan with the first chunk and finality evidence with the last. The derived session is an ordinary session:
/// every command reads, re-derives, retains and compacts it.
/// </summary>
/// <remarks>
/// The follower resumes: opening it reads what the derived session already mirrored, so a follower that stopped - or
/// crashed between two publications - continues from the next chunk and never mirrors one twice. The evidence session
/// must still hold every chunk from the first; following across a release of the evidence's own chunks is refused.
/// </remarks>
public sealed class LiveSessionFollower
{
    private readonly SessionStore evidence;
    private readonly SessionStore derived;
    private readonly DerivedGenerationOptions options;
    private readonly CompactionOptions compaction;
    private readonly Dictionary<(uint Stream, uint Epoch), ulong> endedAt = [];
    private (CaptureId Capture, SourceClockDescriptor Clock)? recorded;
    private ObservationNormalizerV1? normalizer;
    private ulong journalIndex;
    private int smallUnits;

    private LiveSessionFollower(
        SessionStore evidence,
        SessionStore derived,
        DerivedGenerationOptions options,
        CompactionOptions compaction)
    {
        this.evidence = evidence;
        this.derived = derived;
        this.options = options;
        this.compaction = compaction;
    }

    /// <summary>
    /// Opens a follower of <paramref name="evidence"/> into <paramref name="derived"/>, which is empty or holds what an
    /// earlier follower mirrored. What it already holds is read once, so the next chunk continues its ordinals and its
    /// journal index.
    /// </summary>
    public static LiveSessionFollower Open(
        SessionStore evidence,
        SessionStore derived,
        DerivedGenerationOptions? options = null,
        CompactionOptions? compaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(derived);
        CompactionOptions bounds = compaction ?? CompactionOptions.Default;
        if (bounds.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(compaction));
        }

        // A store opened on an empty directory knows no session, so it could never lease the one that appears later.
        if (evidence.Current is null)
        {
            throw new ArgumentException(
                "The evidence session has published nothing yet. Open the follower once its first generation exists.",
                nameof(evidence));
        }

        var follower = new LiveSessionFollower(evidence, derived, options ?? DerivedGenerationOptions.Default, bounds);
        follower.Resume(cancellationToken);
        return follower;
    }

    /// <summary>
    /// Mirrors every chunk the evidence session has committed and the derived session does not hold yet, in order, at
    /// most <paramref name="maximumChunks"/> of them, deriving each one's rows as one derived generation.
    /// </summary>
    public FollowStep CatchUp(int maximumChunks = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChunks);
        using EvidenceLease lease = evidence.AcquireLease();
        SessionManifestV1 source = lease.Manifest;
        RequireEvidenceOnly(source);
        StoreDependency[] sourceChunks = Chunks(source);
        StoreDependency[] mirrored = derived.Current is { } current ? Chunks(current) : [];
        RequireMirrorOf(sourceChunks, mirrored);
        bool finished = IsFinished(derived.Current);
        int chunks = 0;
        long records = 0;
        int compactions = 0;
        for (int index = mirrored.Length; index < sourceChunks.Length && chunks < maximumChunks && !finished; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Finality evidence comes only with the capture's last chunk, so mirroring that chunk finishes the follow.
            // A coverage ledger is accepted as the legacy proof for sessions written before capture-finalization-v1.
            bool last = index == sourceChunks.Length - 1 && IsFinished(source);
            (DerivedGenerationResult published, long chunkRecords) = Mirror(
                source,
                sourceChunks[index],
                firstChunk: index == 0,
                last,
                cancellationToken);
            chunks++;
            records += chunkRecords;
            finished = last;
            if (IsSmall(published))
            {
                smallUnits++;
            }

            // The recorder's policy: a bounded step whenever enough small publications accumulated, and the rest at the end.
            if (smallUnits >= compaction.SmallUnitsBeforeCompaction)
            {
                compactions += Compact(compaction with { RowBudget = compaction.RowBudget > 0 ? compaction.RowBudget : options.RowsPerSegment });
            }
        }

        if (finished && chunks > 0)
        {
            compactions += Compact(compaction with { RowBudget = 0 });
        }

        return new()
        {
            MirroredChunks = chunks,
            MirroredRecords = records,
            DerivedChunks = derived.Current is { } after ? Chunks(after).Length : 0,
            EvidenceChunks = sourceChunks.Length,
            DerivedRecords = checked((long)journalIndex),
            Finished = finished,
            DerivedGeneration = derived.Current?.Generation,
            Compactions = compactions,
        };
    }

    /// <summary>
    /// Follows until the capture has stopped and every chunk is mirrored, or until cancelled: what was mirrored stays
    /// mirrored either way. <paramref name="progress"/> hears every pass.
    /// </summary>
    public async Task<FollowStep> FollowAsync(
        TimeSpan pollInterval,
        Action<FollowStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        while (true)
        {
            FollowStep step = CatchUp(cancellationToken: cancellationToken);
            progress?.Invoke(step);
            if (step.Finished)
            {
                return step;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads what the derived session already mirrored: its capture, clock, record count and ordinals.</summary>
    private void Resume(CancellationToken cancellationToken)
    {
        if (derived.Current is null)
        {
            return;
        }

        using EvidenceLease lease = derived.AcquireLease();
        foreach (StoreDependency chunk in Chunks(lease.Manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream stream = derived.Root.OpenOwnedFile(
                chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            (CaptureId capture, SourceClockDescriptor clock) = JournalV1Reader.ReadSourceClock(stream);
            Continue(capture, clock, chunk.Name);
            _ = JournalV1Reader.ReplayBatches(
                stream,
                (_, _, _, batch) =>
                {
                    foreach (RecordEnvelopeV1 envelope in batch.Records)
                    {
                        (uint, uint) key = (envelope.StreamId, envelope.SourceEpoch);
                        endedAt[key] = endedAt.TryGetValue(key, out ulong highest)
                            ? Math.Max(highest, envelope.RecordOrdinal)
                            : envelope.RecordOrdinal;
                        journalIndex = checked(journalIndex + 1);
                    }
                },
                cancellationToken);
        }

        smallUnits = SegmentCompaction.Plan(derived, compaction, cancellationToken).SmallUnits;
    }

    /// <summary>
    /// Mirrors one evidence chunk: verifies it against the evidence manifest, copies it into a derived generation and
    /// derives its rows from the same bytes, continuing the ordinals and journal index of the chunks before it.
    /// </summary>
    private (DerivedGenerationResult Published, long Records) Mirror(
        SessionManifestV1 source,
        StoreDependency chunk,
        bool firstChunk,
        bool last,
        CancellationToken cancellationToken)
    {
        using FileStream stream = evidence.Root.OpenOwnedFile(
            chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        if (stream.Length != chunk.LengthBytes
            || !string.Equals(
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream)),
                chunk.Digest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The evidence chunk '{chunk.Name}' does not match what generation {source.Generation} recorded for it; "
                + "nothing was mirrored.");
        }

        stream.Position = 0;
        (CaptureId capture, SourceClockDescriptor clock) = JournalV1Reader.ReadSourceClock(stream);
        Continue(capture, clock, chunk.Name);
        JournalNormalizationPlanV1 plan = JournalNormalizationPlanV1.Decode(ReadPlan(source));
        var identity = new SegmentIdentityV1
        {
            CaptureId = capture,
            ClockId = clock.Id,
            TimestampEncoding = clock.Encoding,
            Derivation = ObservationNormalizerV1.ContractVersion,
        };

        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.BeginMirror(derived, identity, clock, stream, options);
        var reached = new Dictionary<(uint Stream, uint Epoch), ulong>(endedAt);
        ulong index = journalIndex;
        JournalV1SchemaTable? validated = null;
        long records = JournalV1Reader.ReplayBatches(
            stream,
            (_, _, schemas, batch) =>
            {
                if (!ReferenceEquals(validated, schemas))
                {
                    plan.ValidateAgainst(schemas);
                    validated = schemas;
                }

                foreach (RecordEnvelopeV1 envelope in batch.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // A chunk continues the ones before it; a record its stream had already passed would repeat a raw
                    // identity, so the chunk is refused rather than mirrored (I1).
                    (uint Stream, uint Epoch) key = (envelope.StreamId, envelope.SourceEpoch);
                    if (endedAt.TryGetValue(key, out ulong passed) && envelope.RecordOrdinal <= passed)
                    {
                        throw new InvalidDataException(
                            $"The evidence chunk '{chunk.Name}' holds record {envelope.RecordOrdinal} of stream "
                            + $"{envelope.StreamId}, which an earlier chunk had already passed; nothing was mirrored.");
                    }

                    reached[key] = reached.TryGetValue(key, out ulong highest)
                        ? Math.Max(highest, envelope.RecordOrdinal)
                        : envelope.RecordOrdinal;
                    AdmittedEventPlan descriptor = plan.Resolve(envelope, schemas);
                    ObservationRowV1 row = normalizer!.ToRow(envelope, descriptor, index);
                    builder.AddRow(row);
                    foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, descriptor, row))
                    {
                        builder.AddFieldRow(field);
                    }

                    index = checked(index + 1);
                }
            },
            cancellationToken);

        if (firstChunk)
        {
            builder.StageNormalizerPlan(ReadPlan(source));
        }

        if (last)
        {
            StoreDependency? ledger = source.Dependencies.SingleOrDefault(dependency =>
                dependency.Kind == StoreDependencyKind.CoverageLedger);
            if (ledger is not null)
            {
                builder.StageCoverageLedger(Read(evidence, ledger));
            }

            StoreDependency? finalization = source.Dependencies.SingleOrDefault(dependency =>
                dependency.Kind == StoreDependencyKind.CaptureFinalization);
            if (finalization is not null)
            {
                builder.StageCaptureFinalization(Read(evidence, finalization));
            }
        }

        DerivedGenerationResult published = builder.CompleteMirror(records, DateTimeOffset.UtcNow, cancellationToken);

        // Only a published chunk moves the follow on: a refused one leaves it where it was.
        foreach (KeyValuePair<(uint Stream, uint Epoch), ulong> pair in reached)
        {
            endedAt[pair.Key] = pair.Value;
        }

        journalIndex = index;
        return (published, records);
    }

    /// <summary>Every chunk is one capture's, on one clock: another capture's would be derived as this one's (I1, I8).</summary>
    private void Continue(CaptureId capture, SourceClockDescriptor clock, string chunk)
    {
        if (recorded is { } first)
        {
            if (capture != first.Capture || clock != first.Clock)
            {
                throw new InvalidDataException(
                    $"The journal chunk '{chunk}' belongs to another capture or clock than the chunks before it; "
                    + "a follow mirrors one recording.");
            }

            return;
        }

        recorded = (capture, clock);
        normalizer = new ObservationNormalizerV1(clock);
    }

    private int Compact(CompactionOptions bounds)
    {
        CompactionResult? result = SegmentCompaction.Compact(derived, DateTimeOffset.UtcNow, bounds, options);
        if (result is null)
        {
            return 0;
        }

        smallUnits += (IsSmall(result.Generation) ? 1 : 0) - result.Plan.Coalesced.Count();
        return 1;
    }

    private bool IsSmall(DerivedGenerationResult published) =>
        published.RowCount > 0
        && published.RowCount < compaction.TargetRows
        && published.Segments.Sum(segment => segment.LengthBytes) < compaction.TargetBytes;

    private byte[] ReadPlan(SessionManifestV1 source) =>
        Read(evidence, source.Dependencies.SingleOrDefault(dependency => dependency.Kind == StoreDependencyKind.DerivationPlan)
            ?? throw new InvalidDataException(
                $"Evidence generation {source.Generation} retains no normalizer plan, so its records cannot be derived."));

    /// <summary>Reads a small dependency whole and checks it against the digest its generation recorded.</summary>
    private static byte[] Read(SessionStore store, StoreDependency dependency)
    {
        if (dependency.LengthBytes is < 1 or > JournalNormalizationPlanV1.MaximumBytes)
        {
            throw new InvalidDataException($"'{dependency.Name}' is outside the bound of what a follow copies whole.");
        }

        byte[] bytes = new byte[dependency.LengthBytes];
        using (FileStream stream = store.Root.OpenOwnedFile(
            dependency.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            stream.ReadExactly(bytes);
        }

        return string.Equals("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), dependency.Digest, StringComparison.Ordinal)
            ? bytes
            : throw new InvalidDataException($"'{dependency.Name}' changed after its generation was published.");
    }

    private static void RequireEvidenceOnly(SessionManifestV1 source)
    {
        if (SessionSegments.Names(source).Count > 0)
        {
            throw new InvalidOperationException(
                $"Generation {source.Generation} already has derived rows, so it is an ordinary session rather than "
                + "evidence to follow. Open it directly.");
        }
    }

    /// <summary>
    /// The derived session's chunks must be the evidence session's first chunks, byte for byte. Anything else is another
    /// capture, a session that was changed by hand, or evidence that released chunks, and following on would be a guess.
    /// </summary>
    private static void RequireMirrorOf(StoreDependency[] source, StoreDependency[] mirrored)
    {
        // Identity first: a chunk that differs means other evidence however many chunks either holds, and saying so is
        // the useful refusal. Only a matching prefix can be the same evidence holding fewer chunks than were mirrored.
        for (int index = 0; index < Math.Min(mirrored.Length, source.Length); index++)
        {
            if (mirrored[index].LengthBytes != source[index].LengthBytes
                || !string.Equals(mirrored[index].Digest, source[index].Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The derived session's chunk {index + 1} is not the evidence session's '{source[index].Name}'. It "
                    + "mirrors other evidence, or the evidence released chunks it had; following on would be a guess.");
            }
        }

        if (mirrored.Length > source.Length)
        {
            throw new InvalidDataException(
                $"The derived session holds {mirrored.Length} chunks and the evidence session only {source.Length}; "
                + "it does not mirror this evidence.");
        }
    }

    /// <summary>
    /// How many journal chunks a generation names, and whether it carries the capture's finality: for an evidence
    /// session, whether its capture has stopped; for a derived one, whether its follow finished.
    /// </summary>
    public static (int Chunks, bool Finished) Progress(SessionManifestV1? manifest) =>
        (manifest is null ? 0 : Chunks(manifest).Length, IsFinished(manifest));

    private static StoreDependency[] Chunks(SessionManifestV1 manifest) =>
    [
        .. manifest.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static bool IsFinished(SessionManifestV1? manifest) =>
        HasFinalization(manifest) || HasLedger(manifest);

    private static bool HasFinalization(SessionManifestV1? manifest) =>
        manifest?.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CaptureFinalization) == true;

    private static bool HasLedger(SessionManifestV1? manifest) =>
        manifest?.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger) == true;
}
