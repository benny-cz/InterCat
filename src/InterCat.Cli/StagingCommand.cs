using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

internal sealed record StagingDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required bool Performed { get; init; }
    public required StagingCleanupReport Report { get; init; }
    public required string? ConfirmCommand { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>
/// Reviews the files staged in a session, changing nothing, and removes only the reviewed set whose writer let go, once a
/// person confirms it (§20.1). A file a writer still holds and one staged with no ownership marker are always kept.
/// </summary>
internal static class StagingCommand
{
    private const int ListedFiles = 12;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? expectedSet = command.TakeOption("--expect-set");
        string? path = command.TakePositional();
        bool confirm = command.TryTakeFlag("--confirm");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (path is null)
        {
            ConsoleUi.Failure(
                "A session directory is required: icat staging <directory> reviews the files staged in it and changes "
                + "nothing.");
            return InterCatExitCode.InvalidInvocation;
        }

        // Either half of a confirmation alone is refused with the review that supplies the other.
        string full = Path.GetFullPath(path);
        if (confirm && expectedSet is null)
        {
            ConsoleUi.Failure(
                "--confirm needs the --expect-set digest a review printed, so a cleanup removes only the files you "
                + $"reviewed: run icat staging \"{full}\" first.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!confirm && expectedSet is not null)
        {
            ConsoleUi.Failure(
                $"--expect-set applies only with --confirm; icat staging \"{full}\" alone reviews the staged files and "
                + "changes nothing.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        StagingCleanupReport report;
        try
        {
            report = confirm
                ? store.CleanupAbandonedStaging(expectedSet, cancellationToken)
                : store.PreviewStagingCleanup();
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }

        int removable = Removable(report).Count();
        IReadOnlyList<string> notes = Notes(report, confirm, removable);
        var document = new StagingDocument
        {
            Contract = "staging-cleanup-v1",
            Path = full,
            Performed = confirm && report.RemovedFiles.Count > 0,
            Report = report,
            ConfirmCommand = !confirm && removable > 0
                ? $"icat staging \"{full}\" --confirm --expect-set {report.CandidateDigest}"
                : null,
            Explanation = string.Join(' ', notes),
        };

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document, confirm, notes);
        }

        return report.ActiveFiles.Count > 0 || report.UnmarkedFiles.Count > 0
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    /// <summary>
    /// Every file a confirmed cleanup removes: each staged file whose writer let go with its ownership marker, and each
    /// marker left without its staged file.
    /// </summary>
    private static IEnumerable<string> Removable(StagingCleanupReport report) =>
        report.AbandonedFiles.SelectMany(name => new[] { name, MarkerOf(name) }).Concat(report.MarkerOnlyFiles);

    private static string MarkerOf(string staged) =>
        staged[..^SessionStore.StagingSuffix.Length] + SessionStore.StagingOwnershipSuffix;

    /// <summary>What was found, what a cleanup does with it, and what is left for the person, in that order.</summary>
    private static List<string> Notes(StagingCleanupReport report, bool confirm, int removable)
    {
        var notes = new List<string>();
        int active = report.ActiveFiles.Count;
        int unmarked = report.UnmarkedFiles.Count;
        if (removable + active + unmarked == 0)
        {
            notes.Add(confirm
                ? "Nothing was staged in this session, so nothing was removed."
                : "Nothing is staged in this session, so there is nothing to remove.");
            return notes;
        }

        if (confirm)
        {
            int removed = report.RemovedFiles.Count;
            notes.Add(removed == 0
                ? "Nothing was removed: no file of the reviewed set was left by a writer that let go."
                : $"Removed {CountText.Of(removed, "file")}: the reviewed staged files whose writer let go, and their "
                    + "ownership markers.");
        }

        if (active > 0)
        {
            notes.Add(
                $"A writer still holds {CountText.Of(active, "file")}: a capture or a publication is at work, and a cleanup "
                + "never removes what a writer holds.");
        }

        if (unmarked > 0)
        {
            notes.Add(
                $"{CountText.Of(unmarked, "file")} {CountText.Agree(unmarked, "was", "were")} staged with no ownership "
                + $"marker, so nothing proves {CountText.Agree(unmarked, "its writer", "their writers")} let go, and InterCat "
                + $"never removes {CountText.Agree(unmarked, "it", "them")}. Once you know no InterCat process is writing "
                + $"to this session, you can delete {CountText.Agree(unmarked, "it", "them")} yourself.");
        }

        if (!confirm)
        {
            notes.Add(removable == 0
                ? "Nothing has been changed, and a cleanup would remove nothing."
                : $"Nothing has been changed. To remove the {CountText.Of(removable, "file")} the table marks, and "
                    + "nothing else, run:");
        }

        return notes;
    }

    private static void Render(StagingDocument document, bool confirm, IReadOnlyList<string> notes)
    {
        StagingCleanupReport report = document.Report;
        ConsoleUi.Heading(confirm ? "Staging cleanup" : "Staging review");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Abandoned staged files", ConsoleUi.Count(report.AbandonedFiles.Count));
        ConsoleUi.Field("Marker-only files", ConsoleUi.Count(report.MarkerOnlyFiles.Count));
        ConsoleUi.Field("Active writer files", ConsoleUi.Count(report.ActiveFiles.Count));
        ConsoleUi.Field("Unmarked staged files", ConsoleUi.Count(report.UnmarkedFiles.Count));
        if (confirm)
        {
            ConsoleUi.Field("Files removed", ConsoleUi.Count(report.RemovedFiles.Count));
        }

        // Each file the review found, what it is and what a cleanup does with it: a confirmation removes exactly the
        // files listed as removed, so none of them is left unnamed.
        List<IReadOnlyList<string>> rows = Rows(report, confirm);
        if (rows.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Table(["File", "What it is", confirm ? "The cleanup" : "A cleanup"], [.. rows.Take(ListedFiles)]);
            if (rows.Count > ListedFiles)
            {
                ConsoleUi.Note($"... and {ConsoleUi.Count(rows.Count - ListedFiles)} more; --json lists every one.");
            }
        }

        ConsoleUi.Line();
        foreach (string note in notes)
        {
            ConsoleUi.Note(note);
        }

        if (document.ConfirmCommand is not null)
        {
            ConsoleUi.Line($"    {document.ConfirmCommand}");
        }
    }

    private static List<IReadOnlyList<string>> Rows(StagingCleanupReport report, bool confirm)
    {
        var removed = new HashSet<string>(report.RemovedFiles, StringComparer.OrdinalIgnoreCase);
        string Removes(string name) => !confirm ? "removes it" : removed.Contains(name) ? "removed it" : "kept it";
        string keeps = confirm ? "kept it" : "keeps it";
        var rows = new List<IReadOnlyList<string>>();
        foreach (string name in report.AbandonedFiles)
        {
            rows.Add([name, "staged; its writer let go", Removes(name)]);
            rows.Add([MarkerOf(name), "its ownership marker", Removes(MarkerOf(name))]);
        }

        rows.AddRange(report.MarkerOnlyFiles.Select(name =>
            (IReadOnlyList<string>)[name, "ownership marker; its staged file is gone", Removes(name)]));
        rows.AddRange(report.ActiveFiles.Select(name => (IReadOnlyList<string>)[name, "a writer still holds it", keeps]));
        rows.AddRange(report.UnmarkedFiles.Select(name => (IReadOnlyList<string>)[name, "staged; no ownership marker", keeps]));
        return rows;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat staging <directory> [--confirm --expect-set <digest>] [--json]");
        ConsoleUi.Line("  Reviews the files staged in a session and changes nothing: each one, what it is, and whether a");
        ConsoleUi.Line("  cleanup removes it. --confirm with the digest the review printed removes only the files it");
        ConsoleUi.Line("  listed whose writer let go, with their ownership markers. A file a writer still holds, one");
        ConsoleUi.Line("  staged with no ownership marker and every published file are never removed.");
    }
}
