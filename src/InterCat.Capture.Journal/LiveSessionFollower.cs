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

    /// <summary>
    /// The evidence session's chunks the derived session has followed - those it holds, and the oldest ones a release of it
    /// gave up since (ADR-044) - and the chunks the evidence session has committed. They are equal once it holds every one.
    /// </summary>
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

    /// <summary>
    /// How many of the capture's oldest chunks the derived session no longer holds: those its releases gave up, which the
    /// evidence may then release in turn (ADR-048 decision 5). Mirroring never changes it; a release of the derived session
    /// raises it from the next pass.
    /// </summary>
    public int ReleasedChunks { get; init; }
}

/// <summary>
/// Follows a privileged recorder's evidence session from an ordinary process (§9, ADR-027). The recorder publishes
/// admitted evidence only - journal chunks with the content kept of their records, the normalizer plan, finalization
/// marker and optional coverage ledger - and nothing privileged derives,
/// queries or compacts. The follower mirrors each committed chunk byte for byte into a session of its own, checked
/// against the evidence manifest's digest, derives its rows there exactly as a recording that derives in-process would,
/// and copies the plan with the first chunk and finality evidence with the last. The derived session is an ordinary session:
/// every command reads, re-derives, retains and compacts it.
/// </summary>
/// <remarks>
/// The follower resumes: opening it reads what the derived session already mirrored, so a follower that stopped - or
/// crashed between two publications - continues from the next chunk and never mirrors one twice. The derived session may
/// release its own oldest interval meanwhile (ADR-043): the chunks it still holds are found among the evidence's by their
/// bytes, so the follow goes on from them, counting the capture's chunks and records as before (ADR-044). The evidence
/// session may release its own oldest chunks once the derived session gave them up, stating how many of the capture's
/// chunks and records it gave up in all (ADR-048): the derived session's chunks are still found among the evidence's by
/// their bytes, and the count places them. Evidence that released chunks without stating so is refused.
/// The content a capture kept of a chunk's records is mirrored with the chunk, byte for byte (content-v1 §2).
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

    // The digest of the calibration the derived session holds, which a mirror is byte for byte of the evidence's.
    private string? mirroredCalibration;

    // Whether the capture's collectors are mirrored: they are published once, with its first generation.
    private bool mirroredCollectors;
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
        (int sourceReleased, _) = ReleasedOf(source);
        StoreDependency[] mirrored = derived.Current is { } current ? Chunks(current) : [];
        int start = RequireMirrorOf(sourceChunks, sourceReleased, mirrored, derived.Current);
        bool finished = IsFinished(derived.Current);
        int chunks = 0;
        long records = 0;
        int compactions = 0;

        // The evidence holds the capture's chunks from the one after those it released; the next to mirror follows the
        // derived session's newest, wherever either session released.
        for (int index = start + mirrored.Length - sourceReleased;
            index < sourceChunks.Length && chunks < maximumChunks && !finished;
            index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Finality evidence comes only with the capture's last chunk, so mirroring that chunk finishes the follow.
            // A coverage ledger is accepted as the legacy proof for sessions written before capture-finalization-v1.
            bool last = index == sourceChunks.Length - 1 && IsFinished(source);
            (DerivedGenerationResult published, long chunkRecords) = Mirror(
                source,
                sourceChunks[index],
                firstChunk: sourceReleased + index == 0,
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
            DerivedChunks = start + (derived.Current is { } after ? Chunks(after).Length : 0),
            EvidenceChunks = sourceReleased + sourceChunks.Length,
            DerivedRecords = checked((long)journalIndex),
            ReleasedChunks = start,
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
        mirroredCalibration = lease.Manifest.Dependencies
            .SingleOrDefault(dependency => dependency.Kind == StoreDependencyKind.ClockCalibration)?.Digest;
        mirroredCollectors = lease.Manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CollectorIdentities);
        StoreDependency[] mirrored = Chunks(lease.Manifest);

        // The chunks a release of the derived session gave up are read from the evidence where it still holds them, checked
        // as a mirror checks them, so the next chunk continues the capture's journal index and every stream's ordinals as if
        // they had stayed (ADR-044). Those the evidence released too are counted from what it states it gave up in all
        // (ADR-048); their streams' ordinals are no longer there to check a later chunk against.
        int heldReleased = 0;
        using (EvidenceLease source = evidence.AcquireLease())
        {
            RequireWholeEvidence(source.Manifest);
            StoreDependency[] sourceChunks = Chunks(source.Manifest);
            (int sourceReleased, long releasedRecords) = ReleasedOf(source.Manifest);
            int start = RequireMirrorOf(sourceChunks, sourceReleased, mirrored, lease.Manifest);
            journalIndex = checked((ulong)releasedRecords);
            heldReleased = Math.Max(0, sourceReleased - start);
            foreach (StoreDependency chunk in sourceChunks.Take(Math.Max(0, start - sourceReleased)))
            {
                _ = Replay(evidence, chunk, source.Manifest, cancellationToken);
            }
        }

        // The derived session's own chunks the evidence released were counted among those it gave up in all.
        ulong counted = 0;
        foreach ((StoreDependency chunk, int position) in mirrored.Select((chunk, position) => (chunk, position)))
        {
            long replayed = Replay(derived, chunk, manifest: null, cancellationToken);
            if (position < heldReleased)
            {
                counted = checked(counted + (ulong)replayed);
            }
        }

        journalIndex = checked(journalIndex - counted);

        smallUnits = SegmentCompaction.Plan(derived, compaction, cancellationToken).SmallUnits;
    }

    /// <summary>
    /// Reads one chunk's records into the follow's journal index and each stream's highest ordinal, and returns how many it
    /// holds. A chunk read from the evidence (<paramref name="manifest"/> given) is checked against the digest its
    /// generation recorded first.
    /// </summary>
    private long Replay(SessionStore store, StoreDependency chunk, SessionManifestV1? manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = store.Root.OpenOwnedFile(
            chunk.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        if (manifest is not null)
        {
            RequireRecorded(stream, chunk, manifest);
            stream.Position = 0;
        }

        (CaptureId capture, SourceClockDescriptor clock) = JournalV1Reader.ReadSourceClock(stream);
        Continue(capture, clock, chunk.Name);
        long records = 0;
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
                    records++;
                }
            },
            cancellationToken);
        return records;
    }

    /// <summary>An evidence chunk must be the bytes its generation recorded, measured by length and digest.</summary>
    private static void RequireRecorded(FileStream stream, StoreDependency chunk, SessionManifestV1 source)
    {
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
        RequireRecorded(stream, chunk, source);
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

        // A capture publishes its start's calibration early and its whole one last, which replaces it; each is mirrored
        // when first seen, so a capture that ends before its last publication still names its boot and start here.
        StoreDependency? calibration = source.Dependencies.SingleOrDefault(dependency =>
            dependency.Kind == StoreDependencyKind.ClockCalibration);
        if (calibration is not null && !string.Equals(calibration.Digest, mirroredCalibration, StringComparison.Ordinal))
        {
            builder.StageClockCalibration(Read(evidence, calibration));
        }

        // The capture's collectors, published with its first generation, are mirrored with the first chunk that has them.
        StoreDependency? collectors = mirroredCollectors ? null : source.Dependencies.SingleOrDefault(dependency =>
            dependency.Kind == StoreDependencyKind.CollectorIdentities);
        if (collectors is not null)
        {
            builder.StageCollectorIdentities(Read(evidence, collectors));
        }

        // The content the capture kept of this chunk's records was published beside it, under its generation's number; it
        // is copied byte for byte beside the mirrored chunk, under this generation's, so the two stay paired (content-v1 §2).
        string pairedContent = ContentChunkV1.FileName(SegmentFormatV1.GenerationOfJournal(chunk.Name)
            ?? throw new InvalidDataException($"'{chunk.Name}' is not named as a journal chunk is; nothing was mirrored."));
        if (source.Dependencies.SingleOrDefault(dependency => dependency.Kind == StoreDependencyKind.Content
            && dependency.Name.Equals(pairedContent, StringComparison.OrdinalIgnoreCase)) is { } content)
        {
            using FileStream kept = evidence.Root.OpenOwnedFile(
                content.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            RequireRecorded(kept, content, source);
            builder.StageMirroredContent(kept, cancellationToken);
        }

        DerivedGenerationResult published = builder.CompleteMirror(records, DateTimeOffset.UtcNow, cancellationToken);
        mirroredCalibration = calibration?.Digest ?? mirroredCalibration;
        mirroredCollectors |= collectors is not null;

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
        RequireWholeEvidence(source);
        if (SessionSegments.Names(source).Count > 0)
        {
            throw new InvalidOperationException(
                $"Generation {source.Generation} already has derived rows, so it is an ordinary session rather than "
                + "evidence to follow. Open it directly.");
        }
    }

    /// <summary>
    /// Evidence that released its own oldest records without stating how many of the capture's chunks and records it gave
    /// up in all no longer says where its chunks lie in the capture, so a follow would number what it derives from the
    /// wrong record; evidence that released its kept content would leave records whose content went without the session
    /// saying so. Both are refused (ADR-027, ADR-048, content-v1 §2).
    /// </summary>
    private static void RequireWholeEvidence(SessionManifestV1 source)
    {
        if (source.LatestRelease(RetentionExtentKind.Interval) is not null
            || source.LatestRelease(RetentionExtentKind.JournalPrefix) is { Record.Recording: null })
        {
            throw new InvalidDataException(
                $"Evidence generation {source.Generation} released its oldest records without stating how many of the "
                + "capture's chunks and records it gave up, so a follow would number what it derives from the wrong record. "
                + "Nothing was followed.");
        }

        if (source.LatestRelease(RetentionExtentKind.Content) is not null)
        {
            throw new InvalidDataException(
                $"Evidence generation {source.Generation} released the content its capture kept, so a follow would keep "
                + "records whose content went without saying so. Nothing was followed.");
        }
    }

    /// <summary>
    /// Where the derived session's chunks lie among the capture's: they must be a run of the evidence session's chunks, byte
    /// for byte, from the capture's first, or from a later one once a release the derived session states gave up the ones
    /// before it (ADR-044). The evidence holds the capture's chunks from the one after those it states it released
    /// (<paramref name="sourceReleased"/>, ADR-048), so the run is placed by a chunk both hold: the derived session's
    /// oldest, or the evidence's oldest among the derived session's. Returns how many of the capture's chunks lie before the
    /// run. Anything else is another capture, a session that was changed by hand, or evidence that released chunks this
    /// session does not hold, and following on would be a guess.
    /// </summary>
    private static int RequireMirrorOf(
        StoreDependency[] source,
        int sourceReleased,
        StoreDependency[] mirrored,
        SessionManifestV1? derivedManifest)
    {
        if (mirrored.Length == 0)
        {
            return sourceReleased == 0
                ? 0
                : throw new InvalidDataException(
                    $"The evidence session released the capture's first {sourceReleased} chunk(s) before this session "
                    + "mirrored any, so this session would begin after records it never held and states no release of. "
                    + "Nothing was followed.");
        }

        // Identity first: a chunk that is none of the evidence's means other evidence however many chunks either holds, and
        // saying so is the useful refusal. Where it lies says how many chunks the derived session gave up. A run the evidence
        // released the start of is placed by the evidence's oldest chunk, which the run must hold.
        int found = Array.FindIndex(source, chunk => Same(chunk, mirrored[0]));
        int reached = found >= 0 || source.Length == 0 ? -1 : Array.FindIndex(mirrored, chunk => Same(chunk, source[0]));
        if (found < 0 && (reached < 0 || reached > sourceReleased))
        {
            throw new InvalidDataException(
                $"The derived session's oldest chunk is not the evidence session's '{(source.Length > 0 ? source[0].Name : "first chunk")}' "
                + "nor any later chunk of it. It mirrors other evidence, or the evidence released every chunk this session "
                + "holds; following on would be a guess.");
        }

        int start = found >= 0 ? sourceReleased + found : sourceReleased - reached;
        int offset = found >= 0 ? found : -reached;
        if (start > 0
            && derivedManifest?.LatestRelease(RetentionExtentKind.Interval) is null
            && derivedManifest?.LatestRelease(RetentionExtentKind.JournalPrefix) is null)
        {
            throw new InvalidDataException(
                $"The derived session begins at the capture's chunk {start + 1}, but states no release of the chunks before "
                + "it, so it was changed by hand; following on would be a guess.");
        }

        for (int index = Math.Max(1, -offset); index < mirrored.Length && offset + index < source.Length; index++)
        {
            if (!Same(mirrored[index], source[offset + index]))
            {
                throw new InvalidDataException(
                    $"The derived session's chunk {start + index + 1} is not the evidence session's "
                    + $"'{source[offset + index].Name}'. It mirrors other evidence, or the evidence was changed by hand; "
                    + "following on would be a guess.");
            }
        }

        if (offset + mirrored.Length > source.Length)
        {
            throw new InvalidDataException(
                $"The derived session holds {mirrored.Length} chunks and the evidence session only {source.Length} after "
                + $"the {sourceReleased} it released; it does not mirror this evidence.");
        }

        return start;
    }

    /// <summary>
    /// How many of the capture's chunks, and records, the evidence released, as its latest chunk release states them in all
    /// (ADR-048); none when it released none. Evidence that released chunks without stating so is refused before this is
    /// read (<see cref="RequireWholeEvidence"/>).
    /// </summary>
    private static (int Chunks, long Records) ReleasedOf(SessionManifestV1 source) =>
        source.LatestRelease(RetentionExtentKind.JournalPrefix)?.Record.Recording is { } recording
            ? (checked((int)recording.Chunks), recording.Records)
            : (0, 0);

    /// <summary>Whether two chunks are the same bytes, whatever name each session gives them.</summary>
    private static bool Same(StoreDependency left, StoreDependency right) =>
        left.LengthBytes == right.LengthBytes && string.Equals(left.Digest, right.Digest, StringComparison.Ordinal);

    /// <summary>
    /// How many of <paramref name="evidence"/>'s chunks <paramref name="derived"/> has followed: the ones it holds and,
    /// after a release of it, the oldest ones it gave up (ADR-044). Without the evidence, or when the derived session holds
    /// none of its chunks, only the chunks it holds. Reads the manifests only.
    /// </summary>
    public static int Followed(SessionManifestV1? evidence, SessionManifestV1? derived)
    {
        if (derived is null)
        {
            return 0;
        }

        StoreDependency[] mirrored = Chunks(derived);
        if (mirrored.Length == 0 || evidence is null)
        {
            return mirrored.Length;
        }

        // Placed as a follow places them: by the derived session's oldest chunk among the evidence's, or by the evidence's
        // oldest among the derived session's when the evidence released the run's start (ADR-048).
        StoreDependency[] source = Chunks(evidence);
        int released = ReleasedOf(evidence).Chunks;
        int found = Array.FindIndex(source, chunk => Same(chunk, mirrored[0]));
        int reached = found >= 0 || source.Length == 0 ? -1 : Array.FindIndex(mirrored, chunk => Same(chunk, source[0]));
        return (found >= 0 ? released + found : reached >= 0 ? Math.Max(0, released - reached) : 0) + mirrored.Length;
    }

    /// <summary>
    /// How many journal chunks a generation names, and whether it carries the capture's finality: for an evidence
    /// session, whether its capture has stopped; for a derived one, whether its follow finished.
    /// </summary>
    public static (int Chunks, bool Finished) Progress(SessionManifestV1? manifest) =>
        (manifest is null ? 0 : ReleasedOf(manifest).Chunks + Chunks(manifest).Length, IsFinished(manifest));

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
