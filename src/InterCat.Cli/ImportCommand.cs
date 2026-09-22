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
internal static class ImportCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sourcePath = command.TakePositional();
        string? intoOption = command.TakeOption("--into");
        string? rowsOption = command.TakeOption("--rows-per-segment");
        string? batchOption = command.TakeOption("--journal-batch-records");
        string? outputOption = command.TakeOption("--output");
        string? entriesOption = command.TakeOption("--max-entries-in-memory");
        string? spillOption = command.TakeOption("--spill-directory");
        bool content = command.TryTakeFlag("--retain-content");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sourcePath is null)
        {
            ConsoleUi.Failure("An ETL path is required: icat import <source.etl>");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
        {
            ConsoleUi.Failure($"ETL evidence not found: {full}");
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
        if (sessionPath is not null
            && Directory.Exists(sessionPath)
            && Directory.EnumerateFileSystemEntries(sessionPath).Any()
            && !overwrite)
        {
            // Evidence is never published into a directory that already holds something. A session that
            // gained files from two unrelated imports would name dependencies neither of them produced.
            ConsoleUi.Failure(
                $"{sessionPath} is not empty. Pass --overwrite to publish into an existing session directory, "
                + "or choose a directory of its own.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
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
                DeriveSessionId(full),
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
    /// A session identity derived from the evidence rather than minted, so importing one file into a fresh
    /// directory twice produces the same session rather than two that disagree about the same records.
    /// </summary>
    private static Guid DeriveSessionId(string etlPath)
    {
        using FileStream stream = File.OpenRead(etlPath);
        ImportSourceIdentity source = ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, stream);
        return ImportIdentity.Create(source, RetainedEvidencePolicy.MetadataOnly).CaptureId.Value;
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
        ConsoleUi.Field(
            "Spilled",
            document.Spill.RunsWritten == 0
                ? "nothing; the index fitted in memory"
                : $"{document.Spill.RunsWritten:N0} runs, {document.Spill.BytesSpilled:N0} bytes");
        ConsoleUi.Field("Elapsed", $"{result.ElapsedMilliseconds:N0} ms");

        if (document.Session is { } session)
        {
            ConsoleUi.Heading("Published session");
            ConsoleUi.Field("Path", session.Path);
            ConsoleUi.Field("Generation", session.Generation.ToString("N0", CultureInfo.CurrentCulture));
            ConsoleUi.Field(
                "Journal",
                $"{session.JournalName}, {session.JournalRecords:N0} records, {session.JournalBytes:N0} B");
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
        ConsoleUi.Line("             [--journal-batch-records <n>]");
        ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("             [--max-entries-in-memory <n>] [--spill-directory <dir>]");
        ConsoleUi.Line("      Reads a standalone ETL through the canonical import contract and reports its");
        ConsoleUi.Line("      source identity, import identity, derived clock, counts and multiplicity.");
        ConsoleUi.Line("      With --into it also publishes a session: the admitted journal-v1 evidence and");
        ConsoleUi.Line("      the observation-v1 segments derived from it, as one committed generation.");
        ConsoleUi.Line("      Needs no elevation.");
    }
}
