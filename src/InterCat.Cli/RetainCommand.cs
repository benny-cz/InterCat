using System.Globalization;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What a retention action would give up, or did. The extent is the point of the document.</summary>
internal sealed record RetentionDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required bool Performed { get; init; }
    public required RetentionPreviewDocument Preview { get; init; }
    public required RetentionResultDocument? Result { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed record RetentionPreviewDocument
{
    public required long Generation { get; init; }
    public required string JournalName { get; init; }

    /// <summary>1, or how many chunks a live recording's journal spans; the byte and record totals cover them all.</summary>
    public required int JournalChunks { get; init; }

    /// <summary>What a release gives up whole: `batch`, or `chunk` for a live recording.</summary>
    public required string ReleaseUnit { get; init; }
    public required long JournalBytes { get; init; }
    public required long TotalRecords { get; init; }
    public required long ReleasedRecords { get; init; }
    public required long RetainedRecords { get; init; }
    public required long ReleasedBatches { get; init; }
    public required long RetainedBatches { get; init; }

    /// <summary>The chunks this boundary gives up, oldest first; empty for a single journal.</summary>
    public required IReadOnlyList<string> ReleasedChunks { get; init; }
    public required bool ReleasesAnything { get; init; }
    public required bool WouldEmptyTheJournal { get; init; }

    /// <summary>The smallest boundary that releases anything, or 0 when the journal is a single batch.</summary>
    public required long SmallestReleasingBoundary { get; init; }

    /// <summary>The largest boundary that still leaves admitted evidence behind.</summary>
    public required long LargestReleasingBoundary { get; init; }
}

internal sealed record RetentionResultDocument
{
    public required long Generation { get; init; }
    public required string ManifestDigest { get; init; }
    public required string RetainedJournalName { get; init; }
    public required int RetainedJournalChunks { get; init; }
    public required long RetainedJournalBytes { get; init; }
    public required long RetainedJournalRecords { get; init; }

    /// <summary>How many bytes the release wrote: a single journal's retained suffix, or nothing for chunks.</summary>
    public required long WrittenBytes { get; init; }
    /// <summary>How many bytes of admitted evidence the release gave up: the old journal less the new one.</summary>
    public required long ReleasedBytes { get; init; }

    /// <summary>How many bytes the removal took off the disk: the whole of the journal it replaced.</summary>
    public required long ReclaimedBytes { get; init; }
    public required IReadOnlyList<string> ReleasedFiles { get; init; }
    public required IReadOnlyList<string> RemovedFiles { get; init; }
    public required IReadOnlyList<string> HeldByLease { get; init; }
}

/// <summary>
/// Releases a prefix of a session's admitted journal. ADR-010 makes this explicit rather than a default,
/// so the command is a dry run unless it is told otherwise: the extent it would give up is disclosed first
/// and performed only on a separate decision (S5).
/// </summary>
internal static class RetainCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionPath = command.TakePositional();
        string? beforeOption = command.TakeOption("--release-journal-before-record");
        string? reasonOption = command.TakeOption("--reason");
        string? outputOption = command.TakeOption("--output");
        bool confirm = command.TryTakeFlag("--confirm");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null || beforeOption is null)
        {
            ConsoleUi.Failure(
                "A session directory and a boundary are required: "
                + "icat retain <directory> --release-journal-before-record <n>");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!long.TryParse(beforeOption, NumberStyles.None, CultureInfo.InvariantCulture, out long before))
        {
            ConsoleUi.Failure(
                $"--release-journal-before-record expects a non-negative whole number; '{beforeOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (confirm && string.IsNullOrWhiteSpace(reasonOption))
        {
            ConsoleUi.Failure(
                "--confirm needs --reason. A release with no stated reason is indistinguishable from data loss, "
                + "so the record refuses to be written without one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            ConsoleUi.Failure("This session has published no generation, so there is nothing to retain from.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Measuring what releasing records before {before} would give up. Nothing is written yet.");
        JournalReleasePreview preview = JournalRetention.Preview(store, before, cancellationToken);
        RetentionOutcome? outcome = null;
        if (confirm && preview.ReleasesAnything && !preview.WouldEmptyTheJournal)
        {
            // A store opened for reading cannot publish, because a generation names the session it belongs to
            // and this one was handed a directory. The session identity comes from the generation it already
            // published, which is the only identity a retention may write under.
            SessionStore writable = SessionStore.Open(
                LocalOwnedDirectory.Open(full),
                store.Current.SessionId,
                store.Current.SourceIdentity);
            ConsoleUi.Progress(
                preview.JournalChunks > 1
                    ? $"Releasing {preview.ReleasedRecords:N0} records in the {preview.ReleasedChunks.Count:N0} oldest "
                        + "chunks and publishing the retention generation."
                    : $"Releasing {preview.ReleasedRecords:N0} records in {preview.ReleasedBatches:N0} batches and "
                        + "publishing the retention generation.");
            outcome = JournalRetention.Release(
                writable,
                before,
                reasonOption!,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken);
        }

        RetentionDocument document = Describe(store, full, preview, outcome, confirm);
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Retention report written to {outputPath}.");
        }

        return document.Performed || preview.ReleasesAnything
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static RetentionDocument Describe(
        SessionStore store,
        string path,
        JournalReleasePreview preview,
        RetentionOutcome? outcome,
        bool confirmed)
    {
        bool chunked = preview.JournalChunks > 1;
        var notes = new List<string>
        {
            "Releasing admitted evidence gives up the ability to re-derive those records after a normalizer "
            + "revision. ADR-010 keeps a journal by default, which is why this needs a separate decision.",
            "The rows derived from the released records stay, and a replay could rebuild only the retained ones, "
            + "so re-derivation is refused after a release rather than dropping them (ADR-024).",
            chunked
                ? "A chunk is the unit of release in a live recording: a boundary inside a chunk releases only the "
                    + "chunks before it. Nothing is rewritten; the oldest chunks stop being named."
                : "A batch is the unit of release: a boundary inside a batch releases nothing, because a frame's "
                    + "checksum covers the records it holds.",
        };

        if (!preview.ReleasesAnything)
        {
            notes.Add(
                preview.HasAnyReleasableBoundary
                    ? $"No {preview.ReleaseUnit} of this journal ends before that boundary, so nothing would be "
                        + $"released. The boundaries it allows run from {preview.SmallestReleasingBoundary:N0} to "
                        + $"{preview.LargestReleasingBoundary:N0} records."
                    : chunked
                        ? "No chunk of this recording can be released and still leave admitted records behind."
                        : "This journal was written as a single batch, so it has no releasable boundary at all. A "
                            + "capture or an import chooses that granularity with its journal batch size.");
        }

        if (preview.WouldEmptyTheJournal)
        {
            notes.Add(
                "This boundary would leave no admitted evidence at all, which is refused rather than "
                + "published: a session with no journal cannot re-derive anything.");
        }

        if (outcome is null && confirmed)
        {
            notes.Add("Nothing was published, because the release above is refused.");
        }

        if (outcome is null && !confirmed && preview.ReleasesAnything)
        {
            notes.Add("This was a measurement. Add --confirm and --reason to perform it.");
        }

        if (outcome?.AwaitingRelease == true)
        {
            notes.Add(
                "The released evidence is no longer part of any generation, but a live evidence lease still "
                + "holds its bytes. They go when that reader lets go; retention never removes evidence behind "
                + "an open reader (I18).");
        }

        return new()
        {
            Contract = "store-v1",
            Path = path,
            Action = "release-journal-prefix",
            Performed = outcome is not null,
            Preview = new()
            {
                Generation = store.Current!.Generation,
                JournalName = preview.JournalName,
                JournalChunks = preview.JournalChunks,
                ReleaseUnit = preview.ReleaseUnit,
                JournalBytes = preview.TotalBytes,
                TotalRecords = preview.TotalRecords,
                ReleasedRecords = preview.ReleasedRecords,
                RetainedRecords = preview.RetainedRecords,
                ReleasedBatches = preview.ReleasedBatches,
                RetainedBatches = preview.RetainedBatches,
                ReleasedChunks = preview.ReleasedChunks,
                ReleasesAnything = preview.ReleasesAnything,
                WouldEmptyTheJournal = preview.WouldEmptyTheJournal,
                SmallestReleasingBoundary = preview.SmallestReleasingBoundary,
                LargestReleasingBoundary = preview.LargestReleasingBoundary,
            },
            Result = outcome is null ? null : new()
            {
                Generation = outcome.Manifest.Generation,
                ManifestDigest = outcome.Manifest.Digest,
                RetainedJournalName = outcome.Manifest.Boundary.JournalName,
                RetainedJournalChunks = outcome.Manifest.Dependencies.Count(dependency =>
                    dependency.Kind == StoreDependencyKind.Journal),
                RetainedJournalBytes = outcome.Manifest.Dependencies
                    .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                    .Sum(dependency => dependency.LengthBytes),
                RetainedJournalRecords = preview.RetainedRecords,
                WrittenBytes = chunked ? 0 : outcome.Manifest.Boundary.CommittedBytes,
                ReleasedBytes = outcome.Manifest.Retention?.ReleasedBytes ?? 0,
                ReclaimedBytes = outcome.ReclaimedBytes,
                ReleasedFiles = outcome.ReleasedFiles,
                RemovedFiles = outcome.RemovedFiles,
                HeldByLease = outcome.HeldByLease,
            },
            Notes = notes,
        };
    }

    private static void Render(RetentionDocument document)
    {
        RetentionPreviewDocument preview = document.Preview;
        bool chunked = preview.JournalChunks > 1;
        ConsoleUi.Heading(document.Performed ? "Journal prefix released" : "Journal prefix release, measured only");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", preview.Generation.ToString("N0", CultureInfo.CurrentCulture));
        ConsoleUi.Field(
            "Journal",
            (chunked ? $"{preview.JournalChunks:N0} chunks of one recording" : preview.JournalName)
            + $", {preview.JournalBytes:N0} B, {preview.TotalRecords:N0} records");
        ConsoleUi.Field(
            "Boundaries it allows",
            preview.SmallestReleasingBoundary == 0
                ? chunked ? "none; no chunk's release leaves records behind" : "none; the journal is a single batch"
                : $"{preview.SmallestReleasingBoundary:N0} to {preview.LargestReleasingBoundary:N0} records");

        ConsoleUi.Heading("What this boundary gives up");
        int releasedChunks = preview.ReleasedChunks.Count;
        ConsoleUi.Table(
            chunked ? ["Extent", "Records", "Batches", "Chunks"] : ["Extent", "Records", "Batches"],
            [
                [
                    "Released",
                    preview.ReleasedRecords.ToString("N0", CultureInfo.CurrentCulture),
                    preview.ReleasedBatches.ToString("N0", CultureInfo.CurrentCulture),
                    .. chunked ? [releasedChunks.ToString("N0", CultureInfo.CurrentCulture)] : Array.Empty<string>(),
                ],
                [
                    "Retained",
                    preview.RetainedRecords.ToString("N0", CultureInfo.CurrentCulture),
                    preview.RetainedBatches.ToString("N0", CultureInfo.CurrentCulture),
                    .. chunked
                        ? [(preview.JournalChunks - releasedChunks).ToString("N0", CultureInfo.CurrentCulture)]
                        : Array.Empty<string>(),
                ],
            ]);

        if (document.Result is { } result)
        {
            ConsoleUi.Heading("Published retention generation");
            ConsoleUi.Field("Generation", result.Generation.ToString("N0", CultureInfo.CurrentCulture));
            ConsoleUi.Field(
                "Retained journal",
                (result.RetainedJournalChunks > 1
                    ? $"{result.RetainedJournalChunks:N0} chunks through {result.RetainedJournalName}"
                    : result.RetainedJournalName)
                + $", {result.RetainedJournalBytes:N0} B, {result.RetainedJournalRecords:N0} records");
            ConsoleUi.Field("Given up", $"{result.ReleasedBytes:N0} B of admitted evidence");
            ConsoleUi.Field("Removed from disk", $"{result.ReclaimedBytes:N0} B");
            ConsoleUi.Field(
                "Net change",
                $"{result.WrittenBytes - result.ReclaimedBytes:N0} B "
                + $"({result.WrittenBytes:N0} B written, {result.ReclaimedBytes:N0} B removed)");
            ConsoleUi.Field("Released files", string.Join(", ", result.ReleasedFiles));
            ConsoleUi.Field(
                "Held by a lease",
                result.HeldByLease.Count == 0 ? "none" : string.Join(", ", result.HeldByLease));
            ConsoleUi.Field("Manifest digest", result.ManifestDigest);
        }

        ConsoleUi.Line();
        foreach (string note in document.Notes)
        {
            ConsoleUi.Note(note);
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat retain <directory> --release-journal-before-record <n>");
        ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Measures what releasing a prefix of the session's admitted journal would give");
        ConsoleUi.Line("      up, and performs it only with --confirm and a stated reason. A batch is the");
        ConsoleUi.Line("      unit of release, and a whole chunk for a live recording; a release that would");
        ConsoleUi.Line("      empty the journal is refused. <n> counts records in stored order across the");
        ConsoleUi.Line("      journal the current generation holds. Re-derivation is refused afterwards,");
        ConsoleUi.Line("      because the released records' rows could not be rebuilt.");
    }
}
