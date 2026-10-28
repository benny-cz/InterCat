using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Journal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The machine-readable result of one canonical import, including the session it published when requested.
/// </summary>
internal sealed record ImportSummaryDocument
{
    public required string Contract { get; init; }
    public required uint ImportContractVersion { get; init; }
    public required DateTimeOffset ImportedAtUtc { get; init; }
    public required string SourcePath { get; init; }
    public required string SourceKind { get; init; }
    public required long SourceLengthBytes { get; init; }
    public required string SourceContentDigest { get; init; }
    public required string ImportDigest { get; init; }
    public required string RetainedEvidence { get; init; }
    public required string IdentityBasis { get; init; }
    public required string SchemaTableDigest { get; init; }
    public required ImportClockDocument Clock { get; init; }
    public required ImportCountsDocument Counts { get; init; }
    public required ImportSpillDocument Spill { get; init; }
    public required bool ProducesSession { get; init; }
    public required ImportGenerationDocument? Session { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// What an import says when the folder it was told to publish into already holds this file's import, or the catalogue of
/// imports lists another folder that does: the session it reuses rather than reading the file again
/// (contracts/import-v1.md §8, §8.1).
/// </summary>
internal sealed record ImportReuseDocument
{
    public required string Contract { get; init; }
    public required uint ImportContractVersion { get; init; }
    public required string SourcePath { get; init; }
    public required string SourceKind { get; init; }
    public required long SourceLengthBytes { get; init; }
    public required string SourceContentDigest { get; init; }
    public required string ImportDigest { get; init; }
    public required string RetainedEvidence { get; init; }

    /// <summary>The folder --into named; the session reused is in another one where the catalogue of imports listed it.</summary>
    public required string Into { get; init; }

    /// <summary>The catalogue of imports that listed the folder reused; null where it is the folder --into named.</summary>
    public required string? Catalogue { get; init; }

    public required ImportReusedSessionDocument Session { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>The session an import reused, at the generation that verified.</summary>
internal sealed record ImportReusedSessionDocument
{
    public required string Path { get; init; }
    public required string SessionId { get; init; }
    public required long Generation { get; init; }
    public required DateTimeOffset CommittedUtc { get; init; }
    public required string ManifestDigest { get; init; }
    public required long ObservationRows { get; init; }

    /// <summary>
    /// Why the newest generation did not verify, when the session reads its last-known-good one; null otherwise.
    /// </summary>
    public required string? RolledBackBecause { get; init; }
}

/// <summary>The generation an import published, when it was given a session to publish into.</summary>
internal sealed record ImportGenerationDocument
{
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string ManifestDigest { get; init; }
    public required string JournalName { get; init; }
    public required long JournalRecords { get; init; }
    public required long JournalBytes { get; init; }
    public required long ObservationRows { get; init; }
    public required IReadOnlyList<ImportSegmentDocument> Segments { get; init; }
    public required long SourceFieldRows { get; init; }
    public required IReadOnlyList<ImportSegmentDocument> FieldSegments { get; init; }
}

internal sealed record ImportSegmentDocument
{
    public required string Name { get; init; }
    public required int RowCount { get; init; }
    public required long LengthBytes { get; init; }
    public required long MinNativeTicks { get; init; }
    public required long MaxNativeTicks { get; init; }
    public required IReadOnlyList<string> Dictionaries { get; init; }
}

internal sealed record ImportClockDocument
{
    public required string ClockId { get; init; }
    public required string HostId { get; init; }
    public required string Encoding { get; init; }
    public required long TicksPerSecond { get; init; }
    public required long ReferenceNativeTicks { get; init; }
    public required string Derivation { get; init; }
}

internal sealed record ImportCountsDocument
{
    public required long ObservedRecords { get; init; }
    public required long AdmittedRecords { get; init; }
    public required long PolicyOmissions { get; init; }
    public required long UndecodableRecords { get; init; }
    public required long SourceEventsLost { get; init; }
    public required long IndexedRecords { get; init; }
    public required long DistinctFacts { get; init; }
    public required int MaximumMultiplicity { get; init; }
    public required double ElapsedMilliseconds { get; init; }
}

internal sealed record ImportSpillDocument
{
    public required int RunsWritten { get; init; }
    public required long BytesSpilled { get; init; }
}

/// <summary>
/// Imports one standalone ETL through the canonical import contract. It reads a file and needs no
/// elevation: R16 keeps parsing out of the privileged broker, and this command is the proof of it.
/// </summary>
internal static partial class ImportCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? intoOption = command.TakeOption("--into");
        string? rowsOption = command.TakeOption("--rows-per-segment");
        string? batchOption = command.TakeOption("--journal-batch-records");
        string? outputOption = command.TakeOption("--output");
        string? entriesOption = command.TakeOption("--max-entries-in-memory");
        string? spillOption = command.TakeOption("--spill-directory");
        string? catalogueOption = command.TakeOption("--catalogue");
        string? sourcePath = command.TakePositional();
        bool content = command.TryTakeFlag("--retain-content");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool again = command.TryTakeFlag("--again");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (sourcePath is null)
        {
            ConsoleUi.Failure("An ETL path is required: icat import <source.etl>");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
        {
            // A folder - a session's, as often as not - is not a trace: say what it is rather than that nothing is there.
            ConsoleUi.Failure(Directory.Exists(full)
                ? $"{full} is a folder; icat import reads one ETL trace file. A session's folder is opened by the other "
                    + "commands as it is."
                : $"ETL evidence not found: {full}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (content)
        {
            ConsoleUi.Failure(
                "Content retention has no validated source contract, pre-persistence scope proof or "
                + "payload-specific impact evidence, so this import refuses it rather than keeping bytes "
                + "no policy approved. Import metadata only.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!TryReadBound(entriesOption, out int maximumEntries, out string? boundProblem))
        {
            ConsoleUi.Failure(boundProblem!);
            return InterCatExitCode.InvalidInvocation;
        }

        if (!TryReadRowsPerSegment(rowsOption, out int rowsPerSegment, out string? rowProblem))
        {
            ConsoleUi.Failure(rowProblem!);
            return InterCatExitCode.InvalidInvocation;
        }

        if (!TryReadPositive(
            batchOption,
            DerivedGenerationOptions.Default.JournalBatchRecords,
            "--journal-batch-records",
            out int journalBatchRecords,
            out string? batchProblem))
        {
            ConsoleUi.Failure(batchProblem!);
            return InterCatExitCode.InvalidInvocation;
        }

        string? sessionPath = intoOption is null ? null : Path.GetFullPath(intoOption);
        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? cataloguePath = catalogueOption is null ? ImportCatalogue.DefaultPath() : Path.GetFullPath(catalogueOption);
        if (catalogueOption is not null && Directory.Exists(cataloguePath))
        {
            ConsoleUi.Failure(
                $"{cataloguePath} is a folder. --catalogue names the file the catalogue of imports is kept in, such as "
                + $"{Path.Combine(cataloguePath, ImportCatalogue.FileName)}.");
            return InterCatExitCode.InvalidInvocation;
        }

        // What the folder holds is read before a schema is compiled or a record read: a folder that already holds this
        // file's import is reused, and so is one the catalogue of imports lists as holding it; the file is hashed only when
        // there is a session to compare it with (§18.4).
        ImportIdentity? identity = null;
        var catalogue = new CatalogueKeeping(cataloguePath);
        IReadOnlyList<string> gone = [];
        if (sessionPath is not null)
        {
            ImportDestination destination = ImportDestination.Inspect(sessionPath, () =>
            {
                ConsoleUi.Progress($"Hashing {full} to compare it with the session {sessionPath} holds.");
                return (identity ??= IdentityOf(full)).CaptureId.Value;
            });
            (int?, int?) shape = (rowsOption is null ? null : rowsPerSegment, batchOption is null ? null : journalBatchRecords);
            if (destination.Kind == ImportDestinationKind.ThisImport)
            {
                catalogue.Record(identity!, destination.Path, full, []);
                return await ReuseAsync(
                    destination, identity!, full, shape, (destination.Path, null, again), json, outputPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (destination.Kind != ImportDestinationKind.New)
            {
                ConsoleUi.Failure(Refusal(destination, full));
                return destination.Kind == ImportDestinationKind.Unreadable
                    ? InterCatExitCode.CorruptedInput
                    : InterCatExitCode.InvalidInvocation;
            }

            if (!again)
            {
                CatalogueLookup lookup = catalogue.LookUp(full, destination.Path, () => identity ??= IdentityOf(full));
                gone = lookup.Gone;
                if (lookup.Reused is { } listed)
                {
                    catalogue.Record(identity!, listed.Path, full, gone);
                    return await ReuseAsync(
                        listed, identity!, full, shape, (destination.Path, catalogue.Path, false), json, outputPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        ConsoleUi.Progress("Compiling admission plans from the schemas this machine reports.");
        IReadOnlyList<SourceAdmissionPlan> plans = probe.CompilePlans(
            [WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.KernelNetworkSourceId],
            out IReadOnlyList<string> refusals);
        foreach (string refusal in refusals)
        {
            ConsoleUi.Warn(refusal);
        }

        if (plans.Count == 0)
        {
            ConsoleUi.Failure("No source could be compiled into an admission plan, so nothing was imported.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        var sessionPlan = new OwnedSessionPlan
        {
            Identity = CaptureSessionIdentity.Create("m1import", System.Environment.ProcessId),
            Sources = plans,
            Providers = [],
            MaximumDuration = TimeSpan.FromMinutes(5),
            PreserveExtendedData = true,
        };

        ConsoleUi.Progress($"Hashing {full} before reading a record from it.");
        var bounds = new CanonicalImportOptions
        {
            MaximumEntriesInMemory = maximumEntries,
            SpillDirectory = spillOption is null ? null : Path.GetFullPath(spillOption),
        };

        EtlImportResult result;
        DerivedGenerationResult? generation = null;
        if (sessionPath is null)
        {
            result = EtlCanonicalImport.Import(
                full,
                sessionPlan,
                RetainedEvidencePolicy.MetadataOnly,
                bounds,
                cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(sessionPath);
            ConsoleUi.Progress(
                $"Publishing into {sessionPath}: the admitted journal first, then the segments derived from it.");
            SessionStore store = SessionStore.Open(
                LocalOwnedDirectory.Open(sessionPath),
                (identity ??= IdentityOf(full)).CaptureId.Value,
                $"import-v1:{full}");
            EtlSessionImportResult published = EtlCanonicalImport.ImportIntoSession(
                full,
                sessionPlan,
                store,
                DateTimeOffset.UtcNow,
                RetainedEvidencePolicy.MetadataOnly,
                bounds,
                new DerivedGenerationOptions
                {
                    RowsPerSegment = rowsPerSegment,
                    JournalBatchRecords = journalBatchRecords,
                },
                cancellationToken);
            result = published.Import;
            generation = published.Generation;
            _ = CheckpointStep.Publish(store, cancellationToken);
            catalogue.Record(result.Summary.Identity, sessionPath, full, gone);
        }

        ImportSummaryDocument document = Describe(result, environment, sessionPath, generation);
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, result);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Import summary written to {outputPath}.");
        }

        return result.Summary.RecordCount == 0
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    /// <summary>
    /// The import's identity, whose capture identity is its session's: derived from the evidence rather than minted, so
    /// importing one file into a fresh directory twice produces the same session rather than two that disagree about
    /// the same records, and a folder that already holds the import is recognised as holding it.
    /// </summary>
    private static ImportIdentity IdentityOf(string etlPath)
    {
        using FileStream stream = File.OpenRead(etlPath);
        ImportSourceIdentity source = ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, stream);
        return ImportIdentity.Create(source, RetainedEvidencePolicy.MetadataOnly);
    }

    /// <summary>
    /// Reports the session that already holds this file's import, which every file its generation names verified, and
    /// reads and writes nothing more (§18.4): in the folder --into named, or in the one the catalogue of imports listed,
    /// writing nothing into the folder --into named (§8.1).
    /// </summary>
    private static async Task<InterCatExitCode> ReuseAsync(
        ImportDestination destination,
        ImportIdentity identity,
        string sourcePath,
        (int? RowsPerSegment, int? JournalBatchRecords) shape,
        (string Into, string? Catalogue, bool Again) asked,
        bool json,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        SessionManifestV1 manifest = destination.Manifest!;
        LocalOwnedDirectory root = LocalOwnedDirectory.Open(destination.Path);
        long rows = SessionSegments.Names(manifest)
            .Sum(name => (long)SessionSegments.DeclaredRowCount(root, manifest, name));
        const string Derived =
            "a session's identity is derived from the file's bytes, the evidence an import keeps and the import contract, "
            + "and every file its generation names verified. The file was hashed and not read again";
        var notes = new List<string>
        {
            asked.Catalogue is { } catalogue
                ? $"The catalogue of imports {catalogue} lists {destination.Path} as holding this file's import, so it was "
                    + $"reused rather than imported into {asked.Into}: {Derived}, and nothing was written into either "
                    + $"folder. Pass --again to import it into {asked.Into} all the same."
                : $"This folder already holds this file's import, so it was reused: {Derived}, and nothing was written "
                    + "into it.",
        };
        if (asked.Again)
        {
            notes.Add(
                "--again imports a file into a new folder even where the catalogue of imports lists another that holds "
                + "it; this folder holds it already, and an import never publishes into a session twice.");
        }

        if (destination.Reason is { } reason)
        {
            notes.Add(
                "Its newest generation did not verify, so the session reads the retained last-known-good one: "
                + $"{reason}. Nothing was repaired and nothing was removed. icat recover \"{destination.Path}\" "
                + "reviews the pointer and, once you confirm, re-points it to this generation.");
        }

        // What shapes a new publication does not change the one reused; the journal it keeps can be derived again.
        if (shape.RowsPerSegment is { } rowsPerSegment)
        {
            notes.Add(
                "--rows-per-segment shapes a new publication, so this session keeps the segments it was published "
                + $"with. icat rederive \"{destination.Path}\" --rows-per-segment "
                + $"{rowsPerSegment.ToString(CultureInfo.InvariantCulture)} derives them again from its journal.");
        }

        if (shape.JournalBatchRecords is not null)
        {
            notes.Add(
                "--journal-batch-records shapes a new journal, so this session keeps the journal it was imported with.");
        }

        var document = new ImportReuseDocument
        {
            Contract = "import-reuse-v1",
            ImportContractVersion = identity.ImportContractVersion,
            SourcePath = sourcePath,
            SourceKind = identity.Source.Kind.ToString(),
            SourceLengthBytes = identity.Source.Length,
            SourceContentDigest = identity.Source.ContentDigest,
            ImportDigest = identity.Digest,
            RetainedEvidence = identity.RetainedEvidence.ToString(),
            Into = asked.Into,
            Catalogue = asked.Catalogue,
            Session = new()
            {
                Path = destination.Path,
                SessionId = manifest.SessionId.ToString("N"),
                Generation = manifest.Generation,
                CommittedUtc = manifest.CommittedUtc,
                ManifestDigest = manifest.Digest,
                ObservationRows = rows,
                RolledBackBecause = destination.Reason,
            },
            Notes = notes,
        };
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            ConsoleUi.Heading("Import reused");
            ConsoleUi.Field("Source", document.SourcePath);
            ConsoleUi.Field("Content digest", document.SourceContentDigest);
            ConsoleUi.Field("Import identity", document.ImportDigest);
            ConsoleUi.Field("Session", document.Session.Path);
            if (asked.Catalogue is not null)
            {
                ConsoleUi.Field("Not imported into", asked.Into);
            }

            ConsoleUi.Field(
                "Generation",
                $"{document.Session.Generation.ToString("N0", CultureInfo.CurrentCulture)}, published "
                + document.Session.CommittedUtc.ToString("u", CultureInfo.InvariantCulture));
            ConsoleUi.Field("Observations", rows.ToString("N0", CultureInfo.CurrentCulture));
            ConsoleUi.Line();
            foreach (string note in notes)
            {
                ConsoleUi.Note(note);
            }

            ConsoleUi.Note($"Open it with: icat session \"{destination.Path}\"");
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Import summary written to {outputPath}.");
        }

        return destination.Reason is null ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>Why an import does not publish into a folder, in what it found there and what to do instead.</summary>
    private static string Refusal(ImportDestination destination, string sourcePath)
    {
        string folder = destination.Path;
        const string Elsewhere = "--overwrite replaces only the report file.";
        return destination.Kind switch
        {
            ImportDestinationKind.Occupied when File.Exists(folder) =>
                $"{folder} is a file. --into names the folder an import publishes its session into: a new one, or one "
                + "that already holds this file's import.",
            ImportDestinationKind.Occupied =>
                $"{folder} holds {Listed(destination.Entries)}, which no session owns. An import publishes only into a "
                + $"new folder, so its evidence is never mixed with anything else: choose a new folder. {Elsewhere}",
            ImportDestinationKind.AnotherSession when destination.Manifest is { } other
                && string.Equals(other.SourceIdentity, $"import-v1:{sourcePath}", StringComparison.Ordinal) =>
                $"{folder} holds an import of {sourcePath} made from other bytes than the file holds now, or under "
                + "another import contract, so it is not this import and is not reused: publishing into it would mix the "
                + $"two. Open it with icat session \"{folder}\", or choose a new folder for this import. {Elsewhere}",
            ImportDestinationKind.AnotherSession =>
                $"{folder} holds another session, {Described(destination.Manifest)}. An import publishes only into a new "
                + "folder, or reuses one that already holds this file's import; publishing into another session would "
                + $"mix its evidence with this file's. Open it with icat session \"{folder}\", or choose a new folder. "
                + Elsewhere,
            ImportDestinationKind.Unreadable when destination.Manifest is not null =>
                $"{folder} holds this file's import, but it cannot be reused. {destination.Reason} icat recover "
                + $"\"{folder}\" reviews it; or choose a new folder for this import.",
            ImportDestinationKind.Unreadable =>
                $"{folder} holds a session whose identity cannot be read, so it is neither reused nor imported into. "
                + $"{destination.Reason} icat recover \"{folder}\" reviews it; or choose a new folder for this import.",
            ImportDestinationKind.Unfinished =>
                $"{folder} holds no session: only {CountText.Of(destination.Entries.Count, "file")} "
                + $"({ConsoleUi.Size(destination.StagedBytes)}) that a publication staged and never published, as an "
                + $"import or a recording that stopped before it finished leaves them. icat staging \"{folder}\" lists "
                + "them and removes them once you confirm; then run this import again, or choose a new folder.",
            ImportDestinationKind.Publishing =>
                $"{folder} holds no session yet, and something is publishing into it now: a writer holds files it is "
                + "staging there. Wait for it to finish and run this import again, which reuses the session if it holds "
                + "this file's import, or choose a new folder.",
            _ => throw new ArgumentOutOfRangeException(nameof(destination), destination.Kind, "Only a refused folder is explained."),
        };
    }

    /// <summary>A session as a refusal names it: what it was derived from and when it was published.</summary>
    private static string Described(SessionManifestV1? manifest) => manifest is null
        ? "which could not be read"
        : (manifest.SourceIdentity.Length == 0 ? "which does not say what it was derived from"
            : $"derived from {manifest.SourceIdentity}")
            + $", published {manifest.CommittedUtc.ToString("u", CultureInfo.InvariantCulture)}";

    /// <summary>The first few entries of a folder by name, and how many more there are.</summary>
    private static string Listed(IReadOnlyList<string> entries)
    {
        const int Named = 3;
        string named = string.Join(", ", entries.Take(Named));
        return entries.Count <= Named ? named : $"{named} and {entries.Count - Named:N0} more";
    }

    private static ImportSummaryDocument Describe(
        EtlImportResult result,
        ProbeEnvironment environment,
        string? sessionPath,
        DerivedGenerationResult? generation)
    {
        CanonicalImportSummary summary = result.Summary;
        var notes = new List<string>
        {
            generation is null
                ? "This is a canonical import index, not a session. Pass --into <directory> to publish the "
                    + "admitted journal and its derived segments as a session a viewer can open."
                : "The admitted records are this session's own journal-v1 evidence, and every published "
                    + "segment names the durable extent of that journal it derives from (ADR-010).",
            "Records are identified by canonical key and occurrence index, because ETL callback delivery "
            + "order is not reproducible.",
            $"Read on build {environment.BuildId}; the file was recorded elsewhere, so its clock and host "
            + "identities are derived from its own content rather than from this machine.",
        };
        if (result.SourceEventsLost > 0)
        {
            notes.Add(
                $"The file itself reports {result.SourceEventsLost} lost source events. They were never in "
                + "the file, so they are reported beside the counts rather than inside them.");
        }

        return new()
        {
            Contract = "import-v1",
            ImportContractVersion = summary.Identity.ImportContractVersion,
            ImportedAtUtc = DateTimeOffset.UtcNow,
            SourcePath = result.EtlPath,
            SourceKind = summary.Identity.Source.Kind.ToString(),
            SourceLengthBytes = summary.Identity.Source.Length,
            SourceContentDigest = summary.Identity.Source.ContentDigest,
            ImportDigest = summary.Identity.Digest,
            RetainedEvidence = summary.Identity.RetainedEvidence.ToString(),
            IdentityBasis = summary.Basis.ToString(),
            SchemaTableDigest = summary.SchemaTableDigest,
            Clock = new()
            {
                ClockId = summary.SourceClock.Id.ToString(),
                HostId = summary.SourceClock.HostId.ToString(),
                Encoding = summary.SourceClock.Encoding.ToString(),
                TicksPerSecond = summary.SourceClock.TicksPerSecond,
                ReferenceNativeTicks = summary.SourceClock.CaptureEpochNativeTicks,
                Derivation =
                    "Derived from the file's content identity and its own readings, then checked against "
                    + "every sampled reading; an unverifiable rate is refused rather than assumed.",
            },
            Counts = new()
            {
                ObservedRecords = result.ObservedRecords,
                AdmittedRecords = result.AdmittedRecords,
                PolicyOmissions = result.OmittedRecords,
                UndecodableRecords = result.UndecodableRecords,
                SourceEventsLost = result.SourceEventsLost,
                IndexedRecords = summary.RecordCount,
                DistinctFacts = summary.DistinctFactCount,
                MaximumMultiplicity = summary.MaximumObservedMultiplicity,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
            },
            Spill = new()
            {
                RunsWritten = summary.SpillRunsWritten,
                BytesSpilled = summary.SpilledBytes,
            },
            ProducesSession = generation is not null,
            Session = generation is null || sessionPath is null ? null : new()
            {
                Path = sessionPath,
                Generation = generation.Manifest.Generation,
                ManifestDigest = generation.Manifest.Digest,
                JournalName = generation.JournalName,
                JournalRecords = generation.JournalRecords,
                JournalBytes = generation.JournalBytes,
                ObservationRows = generation.RowCount,
                Segments =
                [
                    .. generation.Segments.Select(segment => new ImportSegmentDocument
                    {
                        Name = segment.Name,
                        RowCount = segment.RowCount,
                        LengthBytes = segment.LengthBytes,
                        MinNativeTicks = segment.MinNativeTicks,
                        MaxNativeTicks = segment.MaxNativeTicks,
                        Dictionaries = segment.DictionaryNames,
                    }),
                ],
                SourceFieldRows = generation.FieldRowCount,
                FieldSegments =
                [
                    .. generation.FieldSegments.Select(segment => new ImportSegmentDocument
                    {
                        Name = segment.Name,
                        RowCount = segment.RowCount,
                        LengthBytes = segment.LengthBytes,
                        MinNativeTicks = segment.MinNativeTicks,
                        MaxNativeTicks = segment.MaxNativeTicks,
                        Dictionaries = segment.DictionaryNames,
                    }),
                ],
            },
            Notes = notes,
        };
    }

    /// <summary>What an import spilled to disk, as its report says it: nothing, or its runs and their bytes, each counted.</summary>
    internal static string Spilled(ImportSpillDocument spill) => spill.RunsWritten == 0
        ? "nothing; the index fitted in memory"
        : CountText.Of(spill.RunsWritten, "run") + ", " + CountText.Of(spill.BytesSpilled, "byte");

    private static void Render(ImportSummaryDocument document, EtlImportResult result)
    {
        ConsoleUi.Heading("Canonical import");
        ConsoleUi.Field("Source", document.SourcePath);
        ConsoleUi.Field("Kind", $"{document.SourceKind}, {document.SourceLengthBytes:N0} bytes");
        ConsoleUi.Field("Content digest", document.SourceContentDigest);
        ConsoleUi.Field("Import identity", document.ImportDigest);
        ConsoleUi.Field("Retained evidence", document.RetainedEvidence);
        ConsoleUi.Field("Identity basis", document.IdentityBasis);

        ConsoleUi.Heading("Clock, derived from the file");
        ConsoleUi.Field("Clock", document.Clock.ClockId);
        ConsoleUi.Field("Host", document.Clock.HostId);
        ConsoleUi.Field("Rate", $"{document.Clock.TicksPerSecond:N0} {document.Clock.Encoding} ticks/second");

        ConsoleUi.Heading("What the file carried");
        ConsoleUi.Table(
            ["Quantity", "Records"],
            [
                ["Delivered by the file", document.Counts.ObservedRecords.ToString("N0", CultureInfo.InvariantCulture)],
                ["Admitted and indexed", document.Counts.IndexedRecords.ToString("N0", CultureInfo.InvariantCulture)],
                ["Omitted by policy", document.Counts.PolicyOmissions.ToString("N0", CultureInfo.InvariantCulture)],
                ["Undecodable", document.Counts.UndecodableRecords.ToString("N0", CultureInfo.InvariantCulture)],
                ["Lost by the file itself", document.Counts.SourceEventsLost.ToString("N0", CultureInfo.InvariantCulture)],
            ]);
        ConsoleUi.Field("Distinct facts", document.Counts.DistinctFacts.ToString("N0", CultureInfo.InvariantCulture));
        ConsoleUi.Field(
            "Largest repeat",
            document.Counts.MaximumMultiplicity == 1
                ? "1 (no two records shared an instant and a key)"
                : $"{document.Counts.MaximumMultiplicity:N0} indistinguishable records");
        ConsoleUi.Field("Spilled", Spilled(document.Spill));
        ConsoleUi.Field("Elapsed", $"{result.ElapsedMilliseconds:N0} ms");

        if (document.Session is { } session)
        {
            ConsoleUi.Heading("Published session");
            ConsoleUi.Field("Path", session.Path);
            ConsoleUi.Field("Generation", session.Generation.ToString("N0", CultureInfo.CurrentCulture));
            ConsoleUi.Field(
                "Journal",
                $"{session.JournalName}, {CountText.Of(session.JournalRecords, "record")}, {session.JournalBytes:N0} B");
            ConsoleUi.Field("Observations", session.ObservationRows.ToString("N0", CultureInfo.CurrentCulture));
            ConsoleUi.Field("Source fields", session.SourceFieldRows.ToString("N0", CultureInfo.CurrentCulture));
            ConsoleUi.Table(
                ["Segment", "Rows", "Bytes", "Native interval"],
                [
                    .. session.Segments.Concat(session.FieldSegments).Select(segment => new[]
                    {
                        segment.Name,
                        segment.RowCount.ToString("N0", CultureInfo.CurrentCulture),
                        segment.LengthBytes.ToString("N0", CultureInfo.CurrentCulture),
                        $"[{segment.MinNativeTicks:N0}, {segment.MaxNativeTicks:N0}]",
                    }),
                ]);
            ConsoleUi.Field("Manifest digest", session.ManifestDigest);
            ConsoleUi.Line();
            ConsoleUi.Note($"Open it with: icat session {session.Path}");
        }

        ConsoleUi.Line();
        foreach (string note in document.Notes)
        {
            ConsoleUi.Note(note);
        }
    }

    private static bool TryReadBound(string? option, out int value, out string? problem)
    {
        value = CanonicalImportOptions.Default.MaximumEntriesInMemory;
        problem = null;
        if (option is null)
        {
            return true;
        }

        if (!int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1)
        {
            problem = $"--max-entries-in-memory expects a positive whole number; '{option}' is not one.";
            return false;
        }

        return true;
    }

    private static bool TryReadRowsPerSegment(string? option, out int value, out string? problem) =>
        TryReadPositive(
            option,
            DerivedGenerationOptions.Default.RowsPerSegment,
            "--rows-per-segment",
            out value,
            out problem);

    private static bool TryReadPositive(
        string? option,
        int fallback,
        string name,
        out int value,
        out string? problem)
    {
        value = fallback;
        problem = null;
        if (option is null)
        {
            return true;
        }

        if (!int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1)
        {
            problem = $"{name} expects a positive whole number; '{option}' is not one.";
            return false;
        }

        return true;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat import <source.etl> [--into <session-dir>] [--rows-per-segment <n>]");
        ConsoleUi.Line("             [--journal-batch-records <n>] [--catalogue <file>] [--again]");
        ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("             [--max-entries-in-memory <n>] [--spill-directory <dir>]");
        ConsoleUi.Line("      Reads a standalone ETL through the canonical import contract and reports its");
        ConsoleUi.Line("      source identity, import identity, derived clock, counts and multiplicity.");
        ConsoleUi.Line("      With --into it also publishes a session: the admitted journal-v1 evidence and");
        ConsoleUi.Line("      the observation-v1 segments derived from it, as one committed generation.");
        ConsoleUi.Line("      --into names a new folder. A folder that already holds this file's import is");
        ConsoleUi.Line("      reused, not imported into again; any other session or file there is refused.");
        ConsoleUi.Line("      Each import published or reused is recorded in your catalogue of imports, and");
        ConsoleUi.Line("      one it lists in another folder is reused there, writing nothing into the new");
        ConsoleUi.Line("      folder; --again imports the file into the new folder all the same. The");
        if (ImportCatalogue.DefaultPath() is { } kept)
        {
            ConsoleUi.Line($"      catalogue is kept in {kept},");
            ConsoleUi.Line("      or in the file --catalogue names. --overwrite replaces only a report file.");
        }
        else
        {
            ConsoleUi.Line("      catalogue is kept only in the file --catalogue names: this system has no");
            ConsoleUi.Line("      folder of your own for it. --overwrite replaces only a report file.");
        }

        ConsoleUi.Line("      Needs no elevation.");
    }
}
