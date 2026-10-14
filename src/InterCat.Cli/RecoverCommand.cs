using System.Globalization;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

internal sealed record RecoveryDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required bool RecoveryNeeded { get; init; }
    public required bool Repaired { get; init; }
    public required long VerifiedGeneration { get; init; }
    public required string VerifiedManifestDigest { get; init; }
    public required string? RollbackReason { get; init; }
    public required string? DamagedPointerBackup { get; init; }
    public required IReadOnlyList<string> OrphanFiles { get; init; }

    /// <summary>The bytes the files no verified generation names take, every one of them kept.</summary>
    public required long OrphanBytes { get; init; }

    public required string NextAction { get; init; }

    /// <summary>The exact command that confirms the reviewed repair, or null when none is pending.</summary>
    public required string? ConfirmCommand { get; init; }
}

/// <summary>
/// Reviews a session whose newest generation does not verify, changing nothing, and repairs only its pointer once a person
/// confirms the generation they reviewed (§20.1). Every file is kept: the pointer's earlier bytes and every file no
/// verified generation names.
/// </summary>
internal static class RecoverCommand
{
    private const int ListedOrphans = 12;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? expectedManifest = command.TakeOption("--expect-manifest");
        string? path = command.TakePositional();
        bool confirm = command.TryTakeFlag("--confirm");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (path is null)
        {
            ConsoleUi.Failure("A session directory is required: icat recover <directory> reviews it and changes nothing.");
            return InterCatExitCode.InvalidInvocation;
        }

        // Either half of a confirmation alone is refused with the review that supplies the other.
        string full = Path.GetFullPath(path);
        if (confirm && expectedManifest is null)
        {
            ConsoleUi.Failure(
                "--confirm needs the --expect-manifest digest a review printed, which binds the repair to the generation "
                + $"you reviewed: run icat recover \"{full}\" first.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!confirm && expectedManifest is not null)
        {
            ConsoleUi.Failure(
                $"--expect-manifest applies only with --confirm; icat recover \"{full}\" alone reviews the session and "
                + "changes nothing.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ConsoleUi.Progress($"Verifying both generation pointers and dependencies in {full}.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            return Icat.NoSession();
        }

        if (expectedManifest is not null
            && !string.Equals(expectedManifest, store.Current.Digest, StringComparison.Ordinal))
        {
            // A confirmation names the generation it reviewed. When the session no longer verifies to that one, the
            // confirmation does not apply to it: the invocation is stale, not a permission failure.
            ConsoleUi.Failure(
                "The session's verified generation is no longer the one this confirmation names, so nothing was changed. "
                + $"Run icat recover \"{full}\" again and review what it shows before confirming.");
            return InterCatExitCode.InvalidInvocation;
        }

        PointerRepairOutcome? outcome = null;
        if (confirm && store.Recovery.RolledBackToLastKnownGood)
        {
            ConsoleUi.Progress(
                $"Keeping the pointer's present bytes, then pointing it to verified generation "
                + $"{store.Current.Generation.ToString("N0", CultureInfo.CurrentCulture)}.");
            try
            {
                outcome = store.RepairCurrentPointer(cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                ConsoleUi.Failure(exception.Message);
                return InterCatExitCode.InvalidInvocation;
            }
        }

        bool pending = outcome?.Repaired != true && store.Recovery.RolledBackToLastKnownGood;
        string generation = store.Current.Generation.ToString("N0", CultureInfo.CurrentCulture);

        // A repair keeps the pointer's bytes when there are any: a pointer that is gone has none to keep.
        string kept = File.Exists(Path.Combine(full, SessionPointerV1.FileName))
            ? "keeping the pointer's present bytes and every file"
            : "keeping every file";
        var document = new RecoveryDocument
        {
            Contract = "pointer-recovery-v1",
            Path = full,
            RecoveryNeeded = store.Recovery.RolledBackToLastKnownGood,
            Repaired = outcome?.Repaired ?? false,
            VerifiedGeneration = outcome?.VerifiedGeneration ?? store.Current.Generation,
            VerifiedManifestDigest = store.Current.Digest,
            RollbackReason = outcome?.RollbackReason ?? store.Recovery.RollbackReason,
            DamagedPointerBackup = outcome?.DamagedPointerBackup,
            OrphanFiles = store.Recovery.OrphanFiles,
            OrphanBytes = store.Recovery.OrphanBytes,
            NextAction = outcome?.Repaired == true
                ? Repaired(outcome, generation, store.Recovery.OrphanFiles.Count)
                : pending
                    ? $"Nothing has been changed. Readers read generation {generation} in the newest one's place, and "
                        + "nothing can be published into this session or released from it until its pointer is repaired. "
                        + $"Once you have reviewed the reason and the files above, this re-points it to generation {generation}, "
                        + kept + ":"
                    : "The current pointer verifies, so there is nothing to repair. Nothing was changed.",
            ConfirmCommand = pending
                ? $"icat recover \"{full}\" --confirm --expect-manifest {store.Current.Digest}"
                : null,
        };

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
        }

        return document.RecoveryNeeded && !document.Repaired
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    /// <summary>What a repair did and kept: no file is deleted, and none is left for InterCat to delete later.</summary>
    private static string Repaired(PointerRepairOutcome outcome, string generation, int unreferenced) =>
        $"The current pointer names generation {generation}, which verifies, so the session can be published into again."
        + (outcome.DamagedPointerBackup is null ? string.Empty : " The pointer's earlier bytes are kept in the pointer backup.")
        + (unreferenced == 0
            ? string.Empty
            : $" The {CountText.Of(unreferenced, "unreferenced file")} {CountText.Agree(unreferenced, "is", "are")} kept as "
                + "well: InterCat never deletes one, and they stay for you to inspect.");

    private static void Render(RecoveryDocument document)
    {
        string generation = ConsoleUi.Count(document.VerifiedGeneration);
        ConsoleUi.Heading(document.Repaired ? "Session pointer repaired" : "Session recovery review");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field(
            "Current pointer",
            document.Repaired
                ? $"repaired; it names generation {generation}"
                : document.RecoveryNeeded
                    ? $"does not verify; readers fall back to generation {generation}"
                    : $"verifies; it names generation {generation}");
        ConsoleUi.Field("Manifest digest", document.VerifiedManifestDigest);
        if (document.RollbackReason is not null)
        {
            ConsoleUi.Field("Rollback reason", document.RollbackReason);
        }

        if (document.DamagedPointerBackup is not null)
        {
            ConsoleUi.Field("Pointer backup", document.DamagedPointerBackup);
        }

        // The words icat session states them in: files no verified generation names, every one kept.
        ConsoleUi.Field(
            "Unreferenced files",
            document.OrphanFiles.Count == 0
                ? "none"
                : $"{CountText.Of(document.OrphanFiles.Count, "file")} kept, {ConsoleUi.Bytes(document.OrphanBytes)}");
        foreach (string orphan in document.OrphanFiles.Take(ListedOrphans))
        {
            ConsoleUi.Bullet(orphan);
        }

        if (document.OrphanFiles.Count > ListedOrphans)
        {
            ConsoleUi.Note(
                $"... and {ConsoleUi.Count(document.OrphanFiles.Count - ListedOrphans)} more; --json lists every one.");
        }

        ConsoleUi.Line();
        ConsoleUi.Note(document.NextAction);
        if (document.ConfirmCommand is not null)
        {
            ConsoleUi.Line($"    {document.ConfirmCommand}");
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat recover <directory> [--confirm --expect-manifest <digest>] [--json]");
        ConsoleUi.Line("  Reviews a session whose newest generation does not verify and changes nothing: the generation");
        ConsoleUi.Line("  readers read in its place, why, and the files no verified generation names.");
        ConsoleUi.Line("  --confirm with the digest the review printed keeps the pointer's present bytes, then re-points");
        ConsoleUi.Line("  the pointer to that generation, so the session can be published into again. No file is deleted.");
        ConsoleUi.Line("  A confirmation whose digest no longer matches changes nothing and exits 2; review it again.");
    }
}
