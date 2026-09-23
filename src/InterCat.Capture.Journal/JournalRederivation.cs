using System.Security.Cryptography;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>
/// One replacement generation derived only from a published session's retained admitted evidence: its journal, or
/// every chunk of a live recording's journal, which <see cref="JournalChunks"/> counts.
/// </summary>
public sealed record JournalRederivationResult(
    long SourceGeneration,
    long ReplayedRecords,
    DerivedGenerationResult Generation,
    int JournalChunks = 1);

/// <summary>
/// A complete, nonpublishing replay of the retained journal and descriptor interpretation. The journal is named by the
/// boundary; a live recording's capture spans several chunks, replayed in order, which <see cref="JournalChunks"/> counts.
/// </summary>
public sealed record JournalRederivationVerification(
    long SourceGeneration,
    string JournalName,
    long ReplayedRecords,
    long ObservationRows,
    long SourceFieldRows,
    int JournalChunks = 1);

/// <summary>
/// Manifest-level preflight only. An eligible session still needs the full journal and saved-plan replay;
/// this status never promises that a rebuild will succeed or changes the generation.
/// </summary>
public sealed record JournalRederivationReadiness(bool CanAttempt, string Explanation);

/// <summary>
/// Replays a journal and its exact retained descriptor interpretation under one evidence lease. The old
/// generation is never edited: a successful replay atomically publishes new segments and fields as the
/// next generation, and an interrupted or invalid replay leaves the prior pointer current.
/// </summary>
public static class JournalRederivation
{
    public static JournalRederivationReadiness Assess(SessionManifestV1? manifest)
    {
        if (manifest is null)
        {
            return new(false, "No generation has been published yet.");
        }

        if (!manifest.Boundary.IsDeclared)
        {
            return new(false, "This generation names no committed journal boundary.");
        }

        StoreDependency[] journals = [.. manifest.Dependencies.Where(dependency =>
            dependency.Kind == StoreDependencyKind.Journal)];
        if (journals.Length == 0)
        {
            return new(false, "No admitted journal is retained.");
        }

        StoreDependency? bounded = journals.SingleOrDefault(journal =>
            string.Equals(journal.Name, manifest.Boundary.JournalName, StringComparison.OrdinalIgnoreCase));
        if (bounded is null
            || bounded.LengthBytes != manifest.Boundary.CommittedBytes
            || !string.Equals(bounded.Digest, manifest.Boundary.Digest, StringComparison.Ordinal))
        {
            return new(false, "The committed boundary and retained journal dependency disagree.");
        }

        StoreDependency[] plans = [.. manifest.Dependencies.Where(dependency =>
            dependency.Kind == StoreDependencyKind.DerivationPlan)];
        if (plans.Length != 1)
        {
            return new(false, plans.Length == 0
                ? "No retained normalization plan; this legacy session needs a verified plan migration."
                : "More than one retained normalization plan is present; the interpretation is ambiguous.");
        }

        if (plans[0].LengthBytes is < 1 or > JournalNormalizationPlanV1.MaximumBytes)
        {
            return new(false, "The retained normalization plan is outside its 1 MiB bound.");
        }

        return new(true,
            (journals.Length == 1
                ? "One committed journal and one retained plan are present."
                : $"{journals.Length} journal chunks and one retained plan are present; the replay checks that they "
                    + "continue one recording, in order, before it derives anything.")
            + " Full schema and batch validation runs when re-derivation starts.");
    }

    /// <summary>
    /// Reads and normalizes every committed record without staging a file or publishing a generation.
    /// This validates the evidence and saved plan, not the success of a future segment write or commit.
    /// </summary>
    public static JournalRederivationVerification Verify(
        SessionStore store,
        CancellationToken cancellationToken = default) =>
        Replay(store, committedUtc: null, options: null, cancellationToken).Verification;

    public static JournalRederivationResult Rebuild(
        SessionStore store,
        DateTimeOffset committedUtc,
        DerivedGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (JournalRederivationVerification verification, DerivedGenerationResult? generation) =
            Replay(store, committedUtc, options, cancellationToken);
        return new(verification.SourceGeneration, verification.ReplayedRecords, generation!, verification.JournalChunks);
    }

    private static (JournalRederivationVerification Verification, DerivedGenerationResult? Generation) Replay(
        SessionStore store,
        DateTimeOffset? committedUtc,
        DerivedGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (!manifest.Boundary.IsDeclared)
        {
            throw new InvalidOperationException(
                "This generation names no committed journal boundary, so it has no admitted evidence to re-derive.");
        }

        // A live recording's capture spans several journal chunks, each complete, named for the generation that published
        // it, so their names sort in the order they were recorded. The boundary names the newest.
        StoreDependency[] journals =
        [
            .. manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
        ];
        StoreDependency bounded = journals.SingleOrDefault(dependency =>
            dependency.Name.Equals(manifest.Boundary.JournalName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The committed journal is missing from this generation's dependencies.");
        if (bounded.LengthBytes != manifest.Boundary.CommittedBytes
            || !string.Equals(bounded.Digest, manifest.Boundary.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The generation's committed boundary and its journal dependency disagree about the retained bytes.");
        }

        if (!ReferenceEquals(journals[^1], bounded))
        {
            throw new InvalidDataException(
                $"The committed boundary names '{bounded.Name}', but '{journals[^1].Name}' was recorded after it. "
                + "Evidence past the boundary is not committed, so the replay is refused rather than guessed.");
        }

        StoreDependency planFile = manifest.Dependencies.SingleOrDefault(dependency =>
            dependency.Kind == StoreDependencyKind.DerivationPlan)
            ?? throw new InvalidOperationException(
                "This generation has no retained normalization plan. Its journal schema fingerprints do not "
                + "contain the field layout or roles needed to replay safely; a verified legacy-plan migration "
                + "is required before re-derivation.");
        if (planFile.LengthBytes is < 1 or > JournalNormalizationPlanV1.MaximumBytes)
        {
            throw new InvalidDataException("The retained normalization plan is outside its 1 MiB bound.");
        }

        byte[] planBytes = new byte[checked((int)planFile.LengthBytes)];
        using (FileStream input = store.Root.OpenOwnedFile(
            planFile.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            input.ReadExactly(planBytes);
            if (input.Position != input.Length)
            {
                throw new InvalidDataException("The retained normalization plan contains undeclared trailing bytes.");
            }
        }

        string measuredPlanDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(planBytes));
        if (!string.Equals(measuredPlanDigest, planFile.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The retained normalization plan changed after the generation was opened; replay is refused.");
        }

        JournalNormalizationPlanV1 plan = JournalNormalizationPlanV1.Decode(planBytes);
        DerivedGenerationBuilder? builder = null;
        ObservationNormalizerV1? normalizer = null;
        (CaptureId Capture, SourceClockDescriptor Clock)? recorded = null;
        JournalV1SchemaTable? validated = null;

        // The highest ordinal each stream reached in the chunks already replayed, and in every record replayed so far.
        Dictionary<(uint Stream, uint Epoch), ulong> endedAt = [];
        Dictionary<(uint Stream, uint Epoch), ulong> reached = [];
        ulong journalIndex = 0;
        long fieldRows = 0;
        long replayed = 0;
        try
        {
            foreach (StoreDependency journal in journals)
            {
                using FileStream evidence = store.Root.OpenOwnedFile(
                    journal.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                if (evidence.Length != journal.LengthBytes)
                {
                    throw new InvalidDataException(
                        $"The admitted journal '{journal.Name}' changed length after the generation was opened.");
                }

                string measuredJournalDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(evidence));
                if (!string.Equals(measuredJournalDigest, journal.Digest, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The admitted journal '{journal.Name}' changed after the generation was opened; replay is refused.");
                }

                long chunkRecords = JournalV1Reader.ReplayBatches(
                    evidence,
                    (capture, clock, schemas, batch) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Every chunk is one capture's, on one clock, read by the one retained interpretation: a chunk
                        // of another capture would be derived into rows labelled as this capture's (I1, I8).
                        if (recorded is { } first && (capture != first.Capture || clock != first.Clock))
                        {
                            throw new InvalidDataException(
                                $"The journal chunk '{journal.Name}' belongs to another capture or clock than the chunks "
                                + "before it. A session's chunks are one recording, and nothing else is replayed with them.");
                        }

                        if (!ReferenceEquals(validated, schemas))
                        {
                            plan.ValidateAgainst(schemas);
                            validated = schemas;
                        }

                        if (normalizer is null)
                        {
                            recorded = (capture, clock);
                            normalizer = new ObservationNormalizerV1(clock);
                            if (committedUtc is not null)
                            {
                                builder = DerivedGenerationBuilder.BeginFromJournal(
                                    store,
                                    new SegmentIdentityV1
                                    {
                                        CaptureId = capture,
                                        ClockId = clock.Id,
                                        TimestampEncoding = clock.Encoding,
                                        Derivation = ObservationNormalizerV1.ContractVersion,
                                    },
                                    clock,
                                    manifest.Boundary,
                                    manifest.Generation,
                                    options);
                            }
                        }

                        // A row's journal index counts its record across the capture's chunks in order.
                        foreach (RecordEnvelopeV1 envelope in batch.Records)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // A chunk continues the ones before it. A record its stream had already passed in an earlier
                            // chunk was replayed twice or out of order, and its raw identity would repeat (I1).
                            (uint Stream, uint Epoch) stream = (envelope.StreamId, envelope.SourceEpoch);
                            if (endedAt.TryGetValue(stream, out ulong passed) && envelope.RecordOrdinal <= passed)
                            {
                                throw new InvalidDataException(
                                    $"The journal chunk '{journal.Name}' holds record {envelope.RecordOrdinal} of stream "
                                    + $"{envelope.StreamId}, which an earlier chunk had already passed. Chunks that do not "
                                    + "continue one another are not one recording, so the replay is refused.");
                            }

                            reached[stream] = reached.TryGetValue(stream, out ulong highest)
                                ? Math.Max(highest, envelope.RecordOrdinal)
                                : envelope.RecordOrdinal;
                            AdmittedEventPlan descriptor = plan.Resolve(envelope, schemas);
                            ObservationRowV1 row = normalizer!.ToRow(envelope, descriptor, journalIndex);
                            builder?.AddRow(row);
                            foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, descriptor, row))
                            {
                                builder?.AddFieldRow(field);
                                fieldRows = checked(fieldRows + 1);
                            }

                            journalIndex = checked(journalIndex + 1);
                        }
                    },
                    cancellationToken);
                if (ReferenceEquals(journal, bounded) && chunkRecords != manifest.Boundary.CommittedRecords)
                {
                    throw new InvalidDataException(
                        $"The journal replayed {chunkRecords} records but generation {manifest.Generation} commits "
                        + $"{manifest.Boundary.CommittedRecords}. A partial or different boundary is refused.");
                }

                replayed = checked(replayed + chunkRecords);
                foreach (KeyValuePair<(uint Stream, uint Epoch), ulong> stream in reached)
                {
                    endedAt[stream.Key] = stream.Value;
                }
            }

            if (replayed != checked((long)journalIndex))
            {
                throw new InvalidDataException(
                    $"The journal replayed {replayed} records but derived {journalIndex}; the replay is refused.");
            }

            if (normalizer is null)
            {
                throw new InvalidDataException("The admitted journal has no record batch to re-derive.");
            }

            var verification = new JournalRederivationVerification(
                manifest.Generation, bounded.Name, replayed, checked((long)journalIndex), fieldRows, journals.Length);
            DerivedGenerationResult? published = committedUtc is { } when
                ? builder!.Complete(when, cancellationToken)
                : null;
            return (verification, published);
        }
        finally
        {
            builder?.Dispose();
        }
    }
}
