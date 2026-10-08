using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
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
/// What releasing a session's oldest interval would give up, or did (store-v1 §8, ADR-043): the records read before a
/// moment with their rows, but for the rows later records rest on, which are kept as evidence.
/// </summary>
internal sealed record IntervalRetentionDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required bool Performed { get; init; }

    /// <summary>The moment as it was typed.</summary>
    public required string Moment { get; init; }

    /// <summary>Where it falls: on the wall clock the capture's machine read, when the session recorded one, and in session time.</summary>
    public required string Placed { get; init; }
    public required IntervalRetentionPreviewDocument Preview { get; init; }
    public required IntervalRetentionResultDocument? Result { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed record IntervalRetentionPreviewDocument
{
    public required long Generation { get; init; }

    /// <summary>The boundary asked for, in session-time nanoseconds.</summary>
    public required long RequestedNanoseconds { get; init; }

    /// <summary>The boundary the release achieves: every record read at or after it is kept. Null when nothing is released.</summary>
    public required long? BoundaryNanoseconds { get; init; }

    /// <summary>What a release gives up whole: `chunk` of a recording, or `batch` of a single journal.</summary>
    public required string ReleaseUnit { get; init; }
    public required int Units { get; init; }
    public required int ReleasedUnits { get; init; }

    /// <summary>The chunks given up, oldest first; empty for a single journal's batches.</summary>
    public required IReadOnlyList<string> ReleasedChunks { get; init; }
    public required long Records { get; init; }
    public required long ReleasedRecords { get; init; }
    public required long Rows { get; init; }

    /// <summary>The rows of released records that go with them.</summary>
    public required long ReleasedRows { get; init; }

    /// <summary>The rows of released records kept as the evidence later records rest on.</summary>
    public required long KeptRows { get; init; }

    /// <summary>
    /// The smallest boundary before which a whole unit lies, so before which nothing can be released, whatever is kept as
    /// evidence; null when no unit can be.
    /// </summary>
    public required long? EarliestReleasingNanoseconds { get; init; }

    /// <summary>The smallest boundary from which a release takes every unit it can, so a later one releases no more.</summary>
    public required long? MostReleasingNanoseconds { get; init; }

    /// <summary>The derived files the release rewrites without the rows it gives up.</summary>
    public required IReadOnlyList<string> ReplacedFiles { get; init; }
    public required bool ReleasesAnything { get; init; }

    /// <summary>Why nothing would be released, in kebab case: `none` when something would be.</summary>
    public required string Obstacle { get; init; }

    /// <summary>Whether no recorder can still be writing the session, which a release needs.</summary>
    public required bool Finished { get; init; }
}

internal sealed record IntervalRetentionResultDocument
{
    public required long Generation { get; init; }
    public required string ManifestDigest { get; init; }
    public required long ReleasedBytes { get; init; }
    public required long ReclaimedBytes { get; init; }
    public required IReadOnlyList<string> ReleasedFiles { get; init; }
    public required IReadOnlyList<string> RemovedFiles { get; init; }
    public required IReadOnlyList<string> HeldByLease { get; init; }
}

/// <summary>What a content release would give up, or did (content-v1 §2): every message's bytes and content facts.</summary>
internal sealed record ContentRetentionDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required string Action { get; init; }
    public required bool Performed { get; init; }
    public required ContentRetentionPreviewDocument Preview { get; init; }
    public required ContentRetentionResultDocument? Result { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed record ContentRetentionPreviewDocument
{
    public required long Generation { get; init; }

    /// <summary>The content chunks a release gives up, by name.</summary>
    public required IReadOnlyList<string> Chunks { get; init; }

    /// <summary>How many records' content they hold, as their headers declare.</summary>
    public required long Records { get; init; }

    /// <summary>How many bytes of messages were kept; null when a chunk could not be read.</summary>
    public required long? KeptBytes { get; init; }

    /// <summary>How many bytes the chunks take beside the journal.</summary>
    public required long FileBytes { get; init; }

    /// <summary>Whether the session's capture has finished, which a release needs.</summary>
    public required bool Finished { get; init; }

    /// <summary>The chunks whose records could not be counted; a release gives them up too.</summary>
    public required IReadOnlyList<string> Unreadable { get; init; }
    public required bool ReleasesAnything { get; init; }
}

internal sealed record ContentRetentionResultDocument
{
    public required long Generation { get; init; }
    public required string ManifestDigest { get; init; }
    public required long ReleasedRecords { get; init; }
    public required long ReleasedBytes { get; init; }
    public required long ReclaimedBytes { get; init; }
    public required IReadOnlyList<string> ReleasedFiles { get; init; }
    public required IReadOnlyList<string> RemovedFiles { get; init; }
    public required IReadOnlyList<string> HeldByLease { get; init; }
}

/// <summary>
/// Releases a session's oldest interval, a prefix of its admitted journal, or its kept content on its own. ADR-010 makes
/// a release explicit rather than a default, so the command is a dry run unless it is told otherwise: the extent it would
/// give up is disclosed first and performed only on a separate decision (S5).
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

        string? beforeOption = command.TakeOption("--release-journal-before-record");
        string? momentOption = command.TakeOption("--release-before");
        string? reasonOption = command.TakeOption("--reason");
        string? outputOption = command.TakeOption("--output");
        string? sessionPath = command.TakePositional();
        bool releaseContent = command.TryTakeFlag("--release-content");
        bool confirm = command.TryTakeFlag("--confirm");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        string[] asked =
        [
            .. momentOption is null ? Array.Empty<string>() : ["--release-before"],
            .. releaseContent ? ["--release-content"] : Array.Empty<string>(),
            .. beforeOption is null ? Array.Empty<string>() : ["--release-journal-before-record"],
        ];
        if (sessionPath is null || asked.Length == 0)
        {
            ConsoleUi.Failure(
                "A session directory and what to release are required: icat retain <directory> --release-before <moment>, "
                + "--release-journal-before-record <n>, or --release-content");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (asked.Length > 1)
        {
            ConsoleUi.Failure(
                (asked.Length == 2 ? $"{asked[0]} and {asked[1]} are two" : $"{asked[0]}, {asked[1]} and {asked[2]} are three")
                + " releases, each published with its own record; ask for one at a time.");
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
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

        if (releaseContent)
        {
            return await ReleaseContentAsync(full, confirm ? reasonOption : null, outputPath, json, cancellationToken)
                .ConfigureAwait(false);
        }

        if (momentOption is not null)
        {
            return await ReleaseIntervalAsync(full, momentOption, confirm ? reasonOption : null, outputPath, json,
                cancellationToken).ConfigureAwait(false);
        }

        if (!long.TryParse(beforeOption, NumberStyles.None, CultureInfo.InvariantCulture, out long before))
        {
            ConsoleUi.Failure(
                $"--release-journal-before-record expects a non-negative whole number; '{beforeOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            return Icat.NoSession();
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
                    ? $"Releasing {CountText.Of(preview.ReleasedRecords, "record")} in the "
                        + (preview.ReleasedChunks.Count == 1 ? "oldest chunk" : $"{preview.ReleasedChunks.Count:N0} oldest chunks")
                        + " and publishing the retention generation."
                    : $"Releasing {CountText.Of(preview.ReleasedRecords, "record")} in "
                        + $"{CountText.Of(preview.ReleasedBatches, "batch", "batches")} and publishing the retention generation.");
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
            + $", {preview.JournalBytes:N0} B, {CountText.Of(preview.TotalRecords, "record")}");
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
                + $", {result.RetainedJournalBytes:N0} B, {CountText.Of(result.RetainedJournalRecords, "record")}");
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

    /// <summary>
    /// Measures, and with a reason performs, the release of the session's oldest interval (ADR-043): the records read before
    /// a moment, placed as `icat evidence --from` places one, with their rows, but for the rows later records rest on. A
    /// session a recorder may still be writing is refused, since its next publication would fail beneath the release.
    /// </summary>
    private static async Task<InterCatExitCode> ReleaseIntervalAsync(string path, string moment, string? reason,
        string? outputPath, bool json, CancellationToken cancellationToken)
    {
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
        if (store.Current is not { } manifest)
        {
            return Icat.NoSession();
        }

        if (SessionRecording.RecordsExtent(store, cancellationToken) is not { } extent)
        {
            ConsoleUi.Failure($"No record of this session has a session time, so {moment} places nothing in it.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionWallClock? wall = SessionRecording.WallClock(store);
        if (!SessionMoment.TryPlace(moment, wall, TimeZoneInfo.Local, extent, CultureInfo.CurrentCulture, out long ticks,
                out string? problem))
        {
            ConsoleUi.Failure(problem ?? "--release-before takes a moment: a time of day on the wall clock the capture's "
                + "machine read, such as 14:32:05.120, with its date or offset where needed, or session time with its unit, "
                + $"such as 312.5 s. '{moment}' is neither.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        long requested = ticks * 100;
        string placed = Instant(requested, wall, extent);
        ConsoleUi.Progress($"Measuring what releasing the records read before {placed} would give up. Nothing is written yet.");
        IntervalReleasePreview preview = IntervalRelease.Preview(store, requested, cancellationToken);
        bool finished = NoRecorderWrites(store, manifest);
        IntervalReleaseResult? released = null;
        string? refused = null;
        if (reason is not null && preview.ReleasesAnything && finished)
        {
            SessionStore writable = SessionStore.Open(LocalOwnedDirectory.Open(path), manifest.SessionId, manifest.SourceIdentity);
            ConsoleUi.Progress($"Releasing {CountText.Of(preview.ReleasedRecords, "record")} in {Units(preview)} and "
                + "publishing the retention generation.");
            try
            {
                released = IntervalRelease.Release(writable, requested, reason, DateTimeOffset.UtcNow,
                    cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                refused = exception.Message;
            }
        }

        var notes = new List<string>
        {
            "An interval release gives up the records read before its boundary and the rows derived from them, but for "
            + "the rows later records rest on - each process's lifecycle records, each connection's opening and first "
            + "records, and the records of a call or exchange open across the boundary - kept as evidence, so every record "
            + "it keeps binds, pairs and keys as before.",
            "Afterwards every reader says the time before the boundary is a partial gap, never a quiet one, and the "
            + "session says from when it keeps every record.",
        };
        if (!preview.ReleasesAnything)
        {
            notes.Add(IntervalRelease.Refusal(preview));
        }
        else
        {
            if (preview.KeptRows > 0)
            {
                notes.Add("The rows it keeps as evidence lose their records, so the session can no longer be re-derived "
                    + "while it holds them (ADR-024).");
            }

            if (!finished)
            {
                notes.Add("This session's capture has not finished. A recorder still writing it would find the session "
                    + "changed beneath it and fail, and a capture that stopped without finishing cannot be told from one "
                    + "still recording, so its interval is released once it has finished.");
            }
        }

        if (refused is not null)
        {
            notes.Add("Nothing was published: " + refused);
        }
        else if (released is null && reason is null && preview.ReleasesAnything && finished)
        {
            notes.Add("This was a measurement. Add --confirm and --reason to perform it.");
        }

        if (released?.Retention.AwaitingRelease == true)
        {
            notes.Add("The released evidence is no longer part of any generation, but a live evidence lease still holds "
                + "its bytes. They go when that reader lets go; retention never removes evidence behind an open reader (I18).");
        }

        var document = new IntervalRetentionDocument
        {
            Contract = "store-v1",
            Path = path,
            Action = "release-interval",
            Performed = released is not null,
            Moment = moment,
            Placed = placed,
            Preview = new()
            {
                Generation = preview.Generation,
                RequestedNanoseconds = preview.RequestedNanoseconds,
                BoundaryNanoseconds = preview.BoundaryNanoseconds,
                ReleaseUnit = preview.ReleaseUnit,
                Units = preview.Units,
                ReleasedUnits = preview.ReleasedUnits,
                ReleasedChunks = preview.ReleasedChunks,
                Records = preview.Records,
                ReleasedRecords = preview.ReleasedRecords,
                Rows = preview.Rows,
                ReleasedRows = preview.ReleasedRows,
                KeptRows = preview.KeptRows,
                EarliestReleasingNanoseconds = preview.EarliestReleasingNanoseconds,
                MostReleasingNanoseconds = preview.MostReleasingNanoseconds,
                ReplacedFiles = preview.ReplacedFiles,
                ReleasesAnything = preview.ReleasesAnything,
                Obstacle = Kebab(preview.Obstacle.ToString()),
                Finished = finished,
            },
            Result = released is null ? null : new()
            {
                Generation = released.Retention.Manifest.Generation,
                ManifestDigest = released.Retention.Manifest.Digest,
                ReleasedBytes = released.Retention.Manifest.Retention?.ReleasedBytes ?? 0,
                ReclaimedBytes = released.Retention.ReclaimedBytes,
                ReleasedFiles = released.Retention.ReleasedFiles,
                RemovedFiles = released.Retention.RemovedFiles,
                HeldByLease = released.Retention.HeldByLease,
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
            RenderInterval(document, wall, extent);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Retention report written to {outputPath}.");
        }

        if (refused is not null)
        {
            ConsoleUi.Warn("Nothing was published: " + refused);
        }

        return document.Performed || (reason is null && preview.ReleasesAnything && finished)
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static void RenderInterval(IntervalRetentionDocument document, SessionWallClock? wall, TimeRange extent)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        IntervalRetentionPreviewDocument preview = document.Preview;
        ConsoleUi.Heading(document.Performed ? "Interval released" : "Interval release, measured only");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", preview.Generation.ToString("N0", culture));
        ConsoleUi.Field("Asked for", "the records read before " + document.Placed);
        ConsoleUi.Field("Journal", (preview.ReleaseUnit == "chunk"
                ? string.Create(culture, $"{preview.Units:N0} chunks of one recording")
                : string.Create(culture, $"one journal of {CountText.Of(preview.Units, "batch", "batches")}"))
            + $", {CountText.Of(preview.Records, "record")}, {CountText.Of(preview.Rows, "row")}");
        if (preview.ReleasesAnything && preview.BoundaryNanoseconds is { } boundary)
        {
            ConsoleUi.Heading("What this boundary gives up");
            ConsoleUi.Field("Gives up", $"{CountText.Of(preview.ReleasedRecords, "record")} in {Units(preview)}, and "
                + $"{CountText.Of(preview.ReleasedRows, "row")} derived from them");
            ConsoleUi.Field("Keeps as evidence", preview.KeptRows == 0
                ? "none of their rows"
                : $"{CountText.Of(preview.KeptRows, "row")} of those records, which later records rest on");
            ConsoleUi.Field("Keeps every record from", Instant(boundary, wall, extent));
            ConsoleUi.Field("Rewrites", preview.ReplacedFiles.Count == 0
                ? "no derived file"
                : CountText.Of(preview.ReplacedFiles.Count, "derived file") + ", without the rows it gives up");
            if (preview.MostReleasingNanoseconds is { } most && most > boundary)
            {
                ConsoleUi.Field("Gives up more", $"before a later moment, up to {SessionTimeText.Seconds(most, culture)}, "
                    + $"from which a release takes every {preview.ReleaseUnit} but the newest");
            }
        }

        if (document.Result is { } result)
        {
            ConsoleUi.Heading("Published retention generation");
            ConsoleUi.Field("Generation", result.Generation.ToString("N0", culture));
            ConsoleUi.Field("Given up", $"{ConsoleUi.Bytes(result.ReleasedBytes)} of evidence and derived files");
            ConsoleUi.Field("Removed from disk", ConsoleUi.Bytes(result.ReclaimedBytes));
            ConsoleUi.Field("Released files", string.Join(", ", result.ReleasedFiles));
            ConsoleUi.Field("Held by a lease", result.HeldByLease.Count == 0 ? "none" : string.Join(", ", result.HeldByLease));
            ConsoleUi.Field("Manifest digest", result.ManifestDigest);
        }

        ConsoleUi.Line();
        foreach (string note in document.Notes)
        {
            ConsoleUi.Note(note);
        }
    }

    /// <summary>The units a release gives up, as its report names them: "the oldest chunk of 3", "2 batches of 5".</summary>
    private static string Units(IntervalReleasePreview preview) => Units(preview.ReleaseUnit, preview.ReleasedUnits, preview.Units);

    private static string Units(IntervalRetentionPreviewDocument preview) => Units(preview.ReleaseUnit, preview.ReleasedUnits, preview.Units);

    private static string Units(string unit, int released, int units) => unit == "chunk"
        ? (released == 1 ? "the oldest chunk" : string.Create(CultureInfo.CurrentCulture, $"the {released:N0} oldest chunks"))
            + string.Create(CultureInfo.CurrentCulture, $" of {units:N0}")
        : CountText.Of(released, "batch", "batches") + string.Create(CultureInfo.CurrentCulture, $" of {units:N0}");

    /// <summary>
    /// An instant of the session as a release names it: its session time, as the retention record and the window's size
    /// line say it, after where it falls on the wall clock the capture's machine read when the session recorded one.
    /// </summary>
    private static string Instant(long nanoseconds, SessionWallClock? wall, TimeRange extent) => wall is null
        ? SessionTimeText.Seconds(nanoseconds, CultureInfo.CurrentCulture)
        : SessionClock.Wall(wall, TimeZoneInfo.Local, extent).Moment(nanoseconds, CultureInfo.CurrentCulture)
            + " · session time " + SessionTimeText.Seconds(nanoseconds, CultureInfo.CurrentCulture);

    /// <summary>
    /// Whether no recorder can still be writing the session: its capture finished, it is a redacted package, or every epoch
    /// of its coverage read a file rather than a live session. A capture that stopped without finishing cannot be told from
    /// one still recording, and a session that states neither is taken to be one.
    /// </summary>
    private static bool NoRecorderWrites(SessionStore store, SessionManifestV1 manifest) =>
        manifest.Dependencies.Any(dependency => dependency.Kind is StoreDependencyKind.CaptureFinalization or StoreDependencyKind.RedactionPolicy)
        || (SessionSegments.CoverageLedger(store.Root, manifest) is { Epochs.Count: > 0 } ledger
            && ledger.Epochs.All(epoch => epoch.Acquisition == CoverageAcquisition.EtlImport));

    /// <summary>An enumeration's name as a person types it: "NothingBefore" as "nothing-before".</summary>
    private static string Kebab(string name) => string.Concat(name.Select((character, index) =>
        char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    /// <summary>
    /// Measures, and with a reason performs, the release of every content chunk the session keeps (content-v1 §2). The
    /// messages' bytes and each record's content facts go; every journal, row and derived file stays.
    /// </summary>
    private static async Task<InterCatExitCode> ReleaseContentAsync(string path, string? reason, string? outputPath, bool json,
        CancellationToken cancellationToken)
    {
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
        if (store.Current is not { } manifest)
        {
            return Icat.NoSession();
        }

        ConsoleUi.Progress("Measuring what releasing the session's kept content would give up. Nothing is written yet.");
        ContentReleasePreview preview = ContentRetention.Preview(store, cancellationToken);
        SessionContentSummary? summary = preview.ReleasesAnything
            ? SessionContentIndex.Read(store.Root, manifest, cancellationToken).Summarize()
            : null;
        RetentionOutcome? outcome = null;
        string? refused = null;
        if (reason is not null && preview.ReleasesAnything && preview.Finished)
        {
            SessionStore writable = SessionStore.Open(LocalOwnedDirectory.Open(path), manifest.SessionId, manifest.SourceIdentity);
            ConsoleUi.Progress(string.Create(CultureInfo.CurrentCulture,
                $"Releasing the content of {CountText.Of(preview.Records, "record")} and publishing the retention generation."));
            try
            {
                outcome = ContentRetention.Release(writable, reason, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                refused = exception.Message;
            }
        }

        var notes = new List<string>
        {
            "A content release gives up every message's bytes and what each record kept of its message: its classification, "
            + "lengths and how it was cut. Every journal, row and derived file stays, so every record and its size remain.",
        };
        if (!preview.ReleasesAnything)
        {
            notes.Add("This session keeps no content, so there is nothing to release.");
        }
        else if (!preview.Finished)
        {
            notes.Add("This session's capture has not finished. A capture still recording would find the session changed "
                + "beneath it and fail, and one that stopped without finishing cannot be told from it, so its content is "
                + "released with its journal chunks instead (--release-journal-before-record), or once it has finished.");
        }

        if (preview.Unreadable.Count > 0)
        {
            notes.Add($"Some content could not be read, so its records are not counted; a release gives it up too: "
                + string.Join(", ", preview.Unreadable) + ".");
        }

        if (refused is not null)
        {
            notes.Add("Nothing was published: " + refused);
        }
        else if (outcome is null && reason is null && preview.ReleasesAnything && preview.Finished)
        {
            notes.Add("This was a measurement. Add --confirm and --reason to perform it.");
        }

        if (outcome?.AwaitingRelease == true)
        {
            notes.Add("The released content is no longer part of any generation, but a live evidence lease still holds "
                + "its bytes. They go when that reader lets go; retention never removes evidence behind an open reader (I18).");
        }

        var document = new ContentRetentionDocument
        {
            Contract = "store-v1",
            Path = path,
            Action = "release-content",
            Performed = outcome is not null,
            Preview = new()
            {
                Generation = preview.Generation,
                Chunks = preview.Chunks,
                Records = preview.Records,
                KeptBytes = summary is { Problem: null } ? summary.KeptBytes : null,
                FileBytes = preview.FileBytes,
                Finished = preview.Finished,
                Unreadable = preview.Unreadable,
                ReleasesAnything = preview.ReleasesAnything,
            },
            Result = outcome is null ? null : new()
            {
                Generation = outcome.Manifest.Generation,
                ManifestDigest = outcome.Manifest.Digest,
                ReleasedRecords = outcome.Manifest.Retention?.ReleasedRecords ?? 0,
                ReleasedBytes = outcome.Manifest.Retention?.ReleasedBytes ?? 0,
                ReclaimedBytes = outcome.ReclaimedBytes,
                ReleasedFiles = outcome.ReleasedFiles,
                RemovedFiles = outcome.RemovedFiles,
                HeldByLease = outcome.HeldByLease,
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
            RenderContent(document, summary);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Retention report written to {outputPath}.");
        }

        if (refused is not null)
        {
            ConsoleUi.Warn("Nothing was published: " + refused);
        }

        return document.Performed || (reason is null && preview.ReleasesAnything && preview.Finished)
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static void RenderContent(ContentRetentionDocument document, SessionContentSummary? summary)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ContentRetentionPreviewDocument preview = document.Preview;
        ConsoleUi.Heading(document.Performed ? "Kept content released" : "Content release, measured only");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", preview.Generation.ToString("N0", culture));
        if (preview.ReleasesAnything)
        {
            ConsoleUi.Field("Kept content", string.Create(culture,
                $"{preview.Chunks.Count:N0} {(preview.Chunks.Count == 1 ? "chunk" : "chunks")} beside the journal, {ConsoleUi.Bytes(preview.FileBytes)}"));
            ConsoleUi.Field("Messages", summary is { Problem: null }
                ? string.Create(culture,
                    $"{RecordsContent(summary.Records)}: {summary.Whole:N0} kept whole, {summary.Cut:N0} cut, {summary.Omitted:N0} not kept; {ConsoleUi.Bytes(summary.KeptBytes)} of message bytes")
                : "at least " + RecordsContent(preview.Records));
            ConsoleUi.Field("Capture", preview.Finished ? "finished" : "not finished; its content is not released on its own");
        }

        if (document.Result is { } result)
        {
            ConsoleUi.Heading("Published retention generation");
            ConsoleUi.Field("Generation", result.Generation.ToString("N0", culture));
            ConsoleUi.Field("Given up", string.Create(culture,
                $"the content of {CountText.Of(result.ReleasedRecords, "record")}, {ConsoleUi.Bytes(result.ReleasedBytes)} of chunks"));
            ConsoleUi.Field("Removed from disk", ConsoleUi.Bytes(result.ReclaimedBytes));
            ConsoleUi.Field("Released files", string.Join(", ", result.ReleasedFiles));
            ConsoleUi.Field("Held by a lease", result.HeldByLease.Count == 0 ? "none" : string.Join(", ", result.HeldByLease));
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
        ConsoleUi.Line("  icat retain <directory> --release-before <moment>");
        ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Measures what releasing the records read before <moment> would give up - with their");
        ConsoleUi.Line("      rows, but for the rows later records rest on, kept as evidence - and performs it only");
        ConsoleUi.Line("      with --confirm and a stated reason, once the capture has finished. <moment> is a time");
        ConsoleUi.Line("      of day on the wall clock the capture's machine read, such as 14:32:05.120, or session");
        ConsoleUi.Line("      time with its unit, such as 312.5 s. A chunk, or a single journal's batch, is the unit");
        ConsoleUi.Line("      of release, and the newest is always kept.");
        ConsoleUi.Line("  icat retain <directory> --release-journal-before-record <n>");
        ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Measures what releasing a prefix of the session's admitted journal would give");
        ConsoleUi.Line("      up, and performs it only with --confirm and a stated reason. A batch is the");
        ConsoleUi.Line("      unit of release, and a whole chunk for a live recording; a release that would");
        ConsoleUi.Line("      empty the journal is refused. <n> counts records in stored order across the");
        ConsoleUi.Line("      journal the current generation holds. Re-derivation is refused afterwards,");
        ConsoleUi.Line("      because the released records' rows could not be rebuilt.");
        ConsoleUi.Line("  icat retain <directory> --release-content");
        ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Measures what releasing every message's kept content would give up - its bytes and");
        ConsoleUi.Line("      what each record kept of its message - and performs it only with --confirm and a");
        ConsoleUi.Line("      stated reason, once the capture has finished. Every journal, row and derived file");
        ConsoleUi.Line("      stays, so every record and its size remain.");
    }

    /// <summary>The content of a number of records, as the report heads it: "1 record's content", "3 records' content".</summary>
    private static string RecordsContent(long records) =>
        records == 1 ? "1 record's content" : string.Create(CultureInfo.CurrentCulture, $"{records:N0} records' content");
}
