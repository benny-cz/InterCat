using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What a compaction coalesced, or would. Every row is kept, so the extent is files, not records.</summary>
internal sealed record CompactionDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required bool Performed { get; init; }
    public required long SourceGeneration { get; init; }
    public required long? PublishedGeneration { get; init; }

    /// <summary>How many publications - generations that derived observations - the session holds, and how many are small.</summary>
    public required int Publications { get; init; }
    public required int SmallPublications { get; init; }
    public required int CoalescedPublications { get; init; }
    public required long CoalescedRows { get; init; }
    public required long CoalescedFieldRows { get; init; }
    public required int ObservationSegmentsBefore { get; init; }
    public required int? ObservationSegmentsAfter { get; init; }
    public required int ReleasedFiles { get; init; }
    public required long ReleasedBytes { get; init; }
    public required long ReclaimedBytes { get; init; }
    public required IReadOnlyList<string> HeldByLease { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// `icat compact`: coalesces a session's small publications into bounded segments (§20.1, ADR-026). A live recording
/// compacts as it records and when it stops; this command does the same for a session that was not, or was
/// interrupted. It is lossless - every row moves unchanged - so it publishes without a separate confirmation.
/// </summary>
internal static class CompactCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? pathOption = command.TakePositional();
        string? outputOption = command.TakeOption("--output");
        bool check = command.TryTakeFlag("--check");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (pathOption is null)
        {
            ConsoleUi.Failure("A published session directory is required: icat compact <directory>.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(pathOption);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? output = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (output is not null && File.Exists(output) && !overwrite)
        {
            ConsoleUi.Failure($"{output} exists. Pass --overwrite to replace the report.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Opening {full} and verifying the published generation and every dependency.");
        SessionStore inspected = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (inspected.Current is not { } current)
        {
            ConsoleUi.Failure("This session has no published generation to compact.");
            return InterCatExitCode.InvalidInvocation;
        }

        CompactionDocument document;
        if (check)
        {
            ConsoleUi.Progress("Measuring the session's publications; nothing is written.");
            document = Describe(full, SegmentCompaction.Plan(inspected, cancellationToken: cancellationToken), result: null);
        }
        else
        {
            // A store opened for reading cannot publish: a generation names its session, which is taken from the one
            // this session already published.
            SessionStore writable = SessionStore.Open(LocalOwnedDirectory.Open(full), current.SessionId, current.SourceIdentity);
            ConsoleUi.Progress(
                "Coalescing the session's small publications; the current generation stays published until the "
                + "compacted one is complete.");
            CompactionResult? result;
            try
            {
                result = SegmentCompaction.Compact(writable, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                ConsoleUi.Failure(exception.Message);
                return InterCatExitCode.PermissionOrCapabilityFailure;
            }

            document = result is null
                ? Describe(full, SegmentCompaction.Plan(writable, cancellationToken: cancellationToken), result: null)
                : Describe(full, result.Plan, result);
        }

        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, check);
        }

        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Compaction report written to {output}.");
        }

        return InterCatExitCode.Success;
    }

    private static CompactionDocument Describe(string path, CompactionPlan plan, CompactionResult? result)
    {
        var notes = new List<string>
        {
            "Compaction moves rows between segment files and keeps every one of them, with its raw-record locator and "
            + "journal index. The journals, the normalizer plan, the coverage ledger and the committed boundary are "
            + "carried unchanged.",
        };
        if (!plan.CompactsAnything)
        {
            notes.Add(
                "Nothing to coalesce: no two consecutive publications are small, below both 64,000 rows and 8 MiB.");
        }
        else if (result is null)
        {
            notes.Add("This was a measurement. Run without --check to coalesce.");
        }

        if (result?.Retention.AwaitingRelease == true)
        {
            notes.Add(
                "A live evidence lease still holds some of the replaced files. They are no longer part of any "
                + "generation and go when that reader lets go (I18).");
        }

        return new()
        {
            Contract = "compact-v1",
            Path = path,
            Performed = result is not null,
            SourceGeneration = plan.SourceGeneration,
            PublishedGeneration = result?.Generation.Manifest.Generation,
            Publications = plan.Units.Count,
            SmallPublications = plan.SmallUnits,
            CoalescedPublications = plan.Coalesced.Count(),
            CoalescedRows = plan.CoalescedRows,
            CoalescedFieldRows = plan.CoalescedFieldRows,
            ObservationSegmentsBefore = plan.ObservationSegments,
            ObservationSegmentsAfter = result is null ? null : SessionSegments.Names(result.Generation.Manifest).Count,
            ReleasedFiles = result?.Retention.ReleasedFiles.Count ?? plan.CoalescedFiles,
            ReleasedBytes = result?.Generation.Manifest.Retention?.ReleasedBytes ?? 0,
            ReclaimedBytes = result?.Retention.ReclaimedBytes ?? 0,
            HeldByLease = result?.Retention.HeldByLease ?? [],
            Notes = notes,
        };
    }

    private static void Render(CompactionDocument document, bool check)
    {
        ConsoleUi.Heading(document.Performed ? "Compacted session" : check ? "Compaction, measured only" : "Nothing to compact");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field(
            "Generation",
            document.PublishedGeneration is { } published
                ? $"{document.SourceGeneration} → {published}"
                : ConsoleUi.Count(document.SourceGeneration));
        ConsoleUi.Field("Publications", $"{ConsoleUi.Count(document.Publications)}, {ConsoleUi.Count(document.SmallPublications)} small");
        if (document.CoalescedPublications > 0)
        {
            ConsoleUi.Field(
                document.Performed ? "Coalesced" : "Would coalesce",
                $"{ConsoleUi.Count(document.CoalescedPublications)} publications: {ConsoleUi.Count(document.CoalescedRows)} rows "
                + $"and {ConsoleUi.Count(document.CoalescedFieldRows)} source fields, in {ConsoleUi.Count(document.ReleasedFiles)} files");
        }

        ConsoleUi.Field(
            "Observation segments",
            document.ObservationSegmentsAfter is { } after
                ? $"{ConsoleUi.Count(document.ObservationSegmentsBefore)} → {ConsoleUi.Count(after)}"
                : ConsoleUi.Count(document.ObservationSegmentsBefore));
        if (document.Performed)
        {
            ConsoleUi.Field("Removed from disk", ConsoleUi.Bytes(document.ReclaimedBytes));
            ConsoleUi.Field("Held by a lease", document.HeldByLease.Count == 0 ? "none" : string.Join(", ", document.HeldByLease));
        }

        foreach (string note in document.Notes)
        {
            ConsoleUi.Note(note);
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat compact <directory> [--check] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Coalesces consecutive small publications - below 64,000 rows and 8 MiB - into");
        ConsoleUi.Line("      bounded segments and releases the files they came from. Every row is kept, and");
        ConsoleUi.Line("      the journals, plan, ledger and boundary are unchanged. --check only measures.");
        ConsoleUi.Line("      A live recording compacts as it records and when it stops (§20.1, ADR-026).");
    }
}
