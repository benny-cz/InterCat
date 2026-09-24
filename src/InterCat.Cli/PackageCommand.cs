using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What a redacted package holds, or would: its counts, what it left out, and what verified it.</summary>
internal sealed record PackageDocument
{
    public required string Contract { get; init; }
    public required string Kind { get; init; }
    public required bool Performed { get; init; }
    public required string Source { get; init; }
    public required string SourceSessionId { get; init; }
    public required long SourceGeneration { get; init; }
    public required string? Directory { get; init; }
    public required string? PackageSessionId { get; init; }
    public required string PackageContract { get; init; }
    public required string Policy { get; init; }
    public required long Rows { get; init; }
    public required long SourceFieldRows { get; init; }
    public required long SourceFieldRowsRedacted { get; init; }
    public required bool CoverageLedger { get; init; }
    public required PackageLeftOutDocument LeftOut { get; init; }
    public required RedactedSessionCounts? Pseudonyms { get; init; }
    public required PackageVerificationDocument? Verification { get; init; }
    public required string Warning { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed record PackageLeftOutDocument
{
    public required int SourceJournals { get; init; }
    public required long SourceJournalBytes { get; init; }
    public required bool NormalizerPlan { get; init; }
    public required bool CaptureFinalization { get; init; }
    public required string Description { get; init; }
}

internal sealed record PackageVerificationDocument
{
    public required int FilesVerified { get; init; }
    public required int IdentityNeedles { get; init; }
    public required int NameNeedles { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// `icat package --redacted`: §11.3's reopenable redacted normalized session (`contracts/redacted-session-v1.md`). It
/// writes a new session directory, never the source, and publishes it only after it reopened and verified.
/// </summary>
internal static class PackageCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionOption = command.TakePositional();
        string? outputOption = command.TakeOption("--output");
        string? reportOption = command.TakeOption("--report");
        bool redacted = command.TryTakeFlag("--redacted");
        bool original = command.TryTakeFlag("--original");
        bool check = command.TryTakeFlag("--check");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        string? problem = command.TryReportUnknown(out string? unknown) ? $"Unknown or incomplete option: {unknown}"
            : sessionOption is null ? "A session directory is required: icat package <directory> --redacted --output <new-directory>."
            : original ? "The original evidence package is not implemented yet. --redacted writes a reopenable redacted session."
            : !redacted ? "Choose what to package: --redacted writes a reopenable redacted session with pseudonymized values."
            : !check && outputOption is null
                ? "--output <new-directory> is required; a package is written only where it is asked to be."
            : null;
        if (problem is not null)
        {
            ConsoleUi.Failure(problem);
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string session = Path.GetFullPath(sessionOption!);
        if (!System.IO.Directory.Exists(session))
        {
            ConsoleUi.Failure($"No session directory at {session}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? destination = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (!check && (System.IO.Directory.Exists(destination) || File.Exists(destination)))
        {
            ConsoleUi.Failure($"{destination} already exists. A package is written only to a new directory.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? report = reportOption is null ? null : Path.GetFullPath(reportOption);
        if (report is not null && File.Exists(report) && !overwrite)
        {
            ConsoleUi.Failure($"{report} exists. Pass --overwrite to replace the report.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Opening {session} and verifying the published generation and every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        if (store.Current is null)
        {
            ConsoleUi.Failure("This session has published no generation, so there is nothing to package.");
            return InterCatExitCode.InvalidInvocation;
        }

        var progress = new ConsoleProgress();
        PackageDocument document;
        try
        {
            if (check)
            {
                RedactedSessionPackagePreview preview = RedactedSessionPackage.Preview(store, progress, cancellationToken);
                document = Describe(session, preview, result: null);
            }
            else
            {
                RedactedSessionPackageResult result = RedactedSessionPackage.Create(store, destination!,
                    DateTimeOffset.UtcNow, progress, cancellationToken);
                document = Describe(session, result.Source, result);
            }
        }
        catch (ArgumentException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document);
        }

        if (report is not null)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Package report written to {report}.");
        }

        return InterCatExitCode.Success;
    }

    private static PackageDocument Describe(string session, RedactedSessionPackagePreview source,
        RedactedSessionPackageResult? result)
    {
        var notes = new List<string>
        {
            "The package is a new session directory: open it with icat session, overview or evidence, or the Desktop's "
                + "Open saved session. Its original-record view shows synthetic metadata records, never the source's.",
        };
        if (result is null)
        {
            notes.Add("This was a measurement; nothing was written. Run without --check, with --output, to write the package.");
        }
        else
        {
            notes.Add("Review the package before sharing it. Deleting the source afterwards is not a secure erase on an SSD.");
        }

        if (!source.CoverageLedger)
        {
            notes.Add("The source published no coverage ledger, so the package's coverage and loss are unknown, as the "
                + "source's are.");
        }

        return new()
        {
            Contract = "package-v1",
            Kind = "redacted-session",
            Performed = result is not null,
            Source = session,
            SourceSessionId = source.SourceSessionId.ToString("N"),
            SourceGeneration = source.SourceGeneration,
            Directory = result?.Directory,
            PackageSessionId = result?.SessionId.ToString("N"),
            PackageContract = RedactedSessionPackage.Contract,
            Policy = RedactedSessionPackage.Policy,
            Rows = source.Rows,
            SourceFieldRows = source.SourceFieldRows,
            SourceFieldRowsRedacted = source.SourceFieldRowsRedacted,
            CoverageLedger = source.CoverageLedger,
            LeftOut = new()
            {
                SourceJournals = source.SourceJournals,
                SourceJournalBytes = source.SourceJournalBytes,
                NormalizerPlan = source.NormalizerPlan,
                CaptureFinalization = source.CaptureFinalization,
                Description = LeftOut(source),
            },
            Pseudonyms = result?.Counts,
            Verification = result is null ? null : new()
            {
                FilesVerified = result.FilesVerified,
                IdentityNeedles = result.IdentityNeedles,
                NameNeedles = result.NameNeedles,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"Reopened as a recipient would; every value checked against the issued pseudonyms; "
                    + $"{result.FilesVerified:N0} files searched for {result.IdentityNeedles:N0} source identity and "
                    + $"{result.NameNeedles:N0} name patterns, none found."),
            },
            Warning = RedactedSessionPackage.Warning,
            Notes = notes,
        };
    }

    private static string LeftOut(RedactedSessionPackagePreview source)
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.CurrentCulture,
                $"{ConsoleUi.Count(source.SourceJournals)} original journal {(source.SourceJournals == 1 ? "file" : "files")} "
                + $"({ConsoleUi.Bytes(source.SourceJournalBytes)}) with any body and extended bytes"),
        };
        if (source.NormalizerPlan) parts.Add("the normalizer plan");
        if (source.CaptureFinalization) parts.Add("the capture finalization marker");
        parts.Add("original identities, locators and absolute clock readings");
        return string.Join(" · ", parts);
    }

    private static void Render(PackageDocument document)
    {
        ConsoleUi.Heading(document.Performed ? "Redacted session package" : "Redacted session package, measured only");
        ConsoleUi.Field("Source", $"{document.Source} · generation {ConsoleUi.Count(document.SourceGeneration)}");
        if (document.Directory is { } directory) ConsoleUi.Field("Written to", directory);
        ConsoleUi.Field("Rows", ConsoleUi.Count(document.Rows)
            + (document.SourceFieldRows == 0 ? string.Empty
                : $" · {ConsoleUi.Count(document.SourceFieldRows)} source fields, {ConsoleUi.Count(document.SourceFieldRowsRedacted)} redacted"));
        ConsoleUi.Field("Coverage ledger", document.CoverageLedger
            ? "rewritten under pseudonymous providers; states and losses unchanged"
            : "none in the source");
        ConsoleUi.Field("Left out", document.LeftOut.Description);
        if (document.Pseudonyms is { } counts)
        {
            ConsoleUi.Field("Pseudonyms", string.Join(" · ",
                $"{ConsoleUi.Count(counts.Names)} names",
                $"{ConsoleUi.Count(counts.ProcessesAndThreads)} process and thread IDs",
                $"{ConsoleUi.Count(counts.Addresses)} addresses",
                $"{ConsoleUi.Count(counts.Ports)} ports",
                $"{ConsoleUi.Count(counts.Identifiers)} identifiers",
                $"{ConsoleUi.Count(counts.Providers)} providers"));
        }

        if (document.Verification is { } verification) ConsoleUi.Field("Verified", verification.Description);
        ConsoleUi.Note(document.Warning);
        foreach (string note in document.Notes) ConsoleUi.Note(note);
        if (document.Directory is { } written) ConsoleUi.Note($"icat session \"{written}\"");
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat package <session-directory> --redacted --output <new-directory> [--check] [--json]");
        ConsoleUi.Line("             [--report <path>] [--overwrite]");
        ConsoleUi.Line("  Writes a reopenable redacted session: a new session directory with fresh identities, whose");
        ConsoleUi.Line("  names, process and thread IDs, addresses, ports and identifiers are random pseudonyms and");
        ConsoleUi.Line("  whose records are synthetic metadata. It leaves out the original journal, bodies, extended");
        ConsoleUi.Line("  data, locators and absolute clock readings, and is verified before it is published.");
        ConsoleUi.Line("  --check measures what the package would hold and writes nothing. --report also writes the");
        ConsoleUi.Line("  JSON report to a file. Pseudonymized is not anonymous: review a package before sharing it.");
    }

    /// <summary>Reports each stage and every tenth of it to stderr, so a long package shows it is moving.</summary>
    private sealed class ConsoleProgress : IProgress<RedactedPackageProgress>
    {
        private RedactedPackageStage? stage;
        private long tenth = -1;

        public void Report(RedactedPackageProgress value)
        {
            long now = value.Total <= 0 ? 10 : value.Done * 10 / value.Total;
            if (value.Stage == stage && now == tenth) return;
            stage = value.Stage;
            tenth = now;
            string what = value.Stage switch
            {
                RedactedPackageStage.Inspecting => "Reading the source's rows",
                RedactedPackageStage.Writing => "Writing pseudonymized rows",
                _ => "Reopening and verifying the package",
            };
            ConsoleUi.Progress(string.Create(CultureInfo.CurrentCulture, $"{what}: {now * 10}%"));
        }
    }
}
