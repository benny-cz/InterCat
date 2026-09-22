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
/// Replays a journal and its exact retained descriptor interpretation under one evidence lease. The old
/// generation is never edited: a successful replay atomically publishes new segments and fields as the
/// next generation, and an interrupted or invalid replay leaves the prior pointer current.
/// </summary>
public static class JournalRederivation
{
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
