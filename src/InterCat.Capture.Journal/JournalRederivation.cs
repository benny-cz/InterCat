using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>One replacement generation derived only from a published session's retained admitted evidence.</summary>
public sealed record JournalRederivationResult(
    long SourceGeneration,
    long ReplayedRecords,
    DerivedGenerationResult Generation);

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
        if (journals.Length != 1)
        {
            return new(false, journals.Length == 0
                ? "No admitted journal is retained."
                : "More than one journal is retained; replacement would risk dropping another capture's rows.");
        }

        if (!string.Equals(journals[0].Name, manifest.Boundary.JournalName, StringComparison.OrdinalIgnoreCase)
            || journals[0].LengthBytes != manifest.Boundary.CommittedBytes
            || !string.Equals(journals[0].Digest, manifest.Boundary.Digest, StringComparison.Ordinal))
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
            "One committed journal and one retained plan are present. Full schema and batch validation runs "
            + "when re-derivation starts.");
    }

    public static JournalRederivationResult Rebuild(
        SessionStore store,
        DateTimeOffset committedUtc,
        DerivedGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (!manifest.Boundary.IsDeclared)
        {
            throw new InvalidOperationException(
                "This generation names no committed journal boundary, so it has no admitted evidence to re-derive.");
        }

        if (manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal) != 1)
        {
            throw new InvalidOperationException(
                "This session names more than one admitted journal. Re-deriving only its current boundary "
                + "would drop another capture's derived rows, so this command refuses multi-capture sessions.");
        }

        StoreDependency journal = manifest.Dependencies.SingleOrDefault(dependency =>
            dependency.Kind == StoreDependencyKind.Journal
            && dependency.Name.Equals(manifest.Boundary.JournalName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The committed journal is missing from this generation's dependencies.");
        if (journal.LengthBytes != manifest.Boundary.CommittedBytes
            || !string.Equals(journal.Digest, manifest.Boundary.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The generation's committed boundary and its journal dependency disagree about the retained bytes.");
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

        JournalNormalizationPlanV1 plan = JournalNormalizationPlanV1.Decode(planBytes);
        using FileStream evidence = store.Root.OpenOwnedFile(
            journal.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        if (evidence.Length != manifest.Boundary.CommittedBytes)
        {
            throw new InvalidDataException("The admitted journal changed length after the generation was opened.");
        }

        DerivedGenerationBuilder? builder = null;
        ObservationNormalizerV1? normalizer = null;
        ulong journalIndex = 0;
        try
        {
            long replayed = JournalV1Reader.ReplayBatches(
                evidence,
                (capture, clock, schemas, batch) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (builder is null)
                    {
                        plan.ValidateAgainst(schemas);
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
                        normalizer = new ObservationNormalizerV1(clock);
                    }

                    foreach (RecordEnvelopeV1 envelope in batch.Records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AdmittedEventPlan descriptor = plan.Resolve(envelope, schemas);
                        ObservationRowV1 row = normalizer!.ToRow(envelope, descriptor, journalIndex);
                        builder.AddRow(row);
                        foreach (SourceFieldRowV1 field in ObservationNormalizerV1.FieldRows(envelope, descriptor, row))
                        {
                            builder.AddFieldRow(field);
                        }

                        journalIndex = checked(journalIndex + 1);
                    }
                },
                cancellationToken);
            if (replayed != manifest.Boundary.CommittedRecords || replayed != checked((long)journalIndex))
            {
                throw new InvalidDataException(
                    $"The journal replayed {replayed} records but generation {manifest.Generation} commits "
                    + $"{manifest.Boundary.CommittedRecords}. A partial or different boundary is refused.");
            }

            if (builder is null)
            {
                throw new InvalidDataException("The admitted journal has no record batch to re-derive.");
            }

            DerivedGenerationResult published = builder.Complete(committedUtc, cancellationToken);
            return new(manifest.Generation, replayed, published);
        }
        finally
        {
            builder?.Dispose();
        }
    }
}
