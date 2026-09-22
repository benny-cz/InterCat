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
    public required string NextAction { get; init; }

    /// <summary>The exact command that confirms the reviewed repair, or null when none is pending.</summary>
    public required string? ConfirmCommand { get; init; }
}

/// <summary>Reviews a rollback and repairs only the current pointer after explicit confirmation.</summary>
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

        string? path = command.TakePositional();
        string? expectedManifest = command.TakeOption("--expect-manifest");
        bool confirm = command.TryTakeFlag("--confirm");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (path is null)
        {
            ConsoleUi.Failure("A session directory is required: icat recover <directory> [--confirm].");
            return InterCatExitCode.InvalidInvocation;
        }

        if (confirm && expectedManifest is null)
        {
            ConsoleUi.Failure(
                "--confirm requires --expect-manifest from a prior recovery preview. This binds the "
                + "decision to the exact verified generation you reviewed.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!confirm && expectedManifest is not null)
        {
            ConsoleUi.Failure("--expect-manifest applies only with --confirm.");
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(path);
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
            ConsoleUi.Failure("This session has no complete generation to recover.");
            return InterCatExitCode.CorruptedInput;
        }

        if (expectedManifest is not null
            && !string.Equals(expectedManifest, store.Current.Digest, StringComparison.Ordinal))
        {
            // A confirmation names the generation it reviewed. When the session no longer verifies to that one, the
            // confirmation does not apply to it: the invocation is stale, not a permission failure.
            ConsoleUi.Failure(
                $"The verified manifest is now {store.Current.Digest}, not the one this confirmation names. "
                + "Nothing was changed. Run the preview again and review what it shows before confirming.");
            return InterCatExitCode.InvalidInvocation;
        }

        PointerRepairOutcome? outcome = null;
        if (confirm && store.Recovery.RolledBackToLastKnownGood)
        {
            ConsoleUi.Progress(
                $"Preserving the damaged pointer, then pointing current to verified generation "
                + $"{store.Current.Generation}.");
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
            NextAction = outcome?.Repaired == true
                ? "The current pointer now names the verified last-known-good generation. The damaged pointer "
                    + "and any later orphan generation were preserved; inspect them before optional cleanup."
                : pending
                    ? "Nothing has been changed. Review the rollback reason and the preserved files, then run "
                        + "this to re-point current to the verified generation:"
                    : "The current pointer verifies. No repair is needed or performed.",
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
                    ? $"damaged; readers fall back to generation {generation}"
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

        ConsoleUi.Field(
            "Preserved files",
            document.OrphanFiles.Count == 0
                ? "none"
                : $"{ConsoleUi.Count(document.OrphanFiles.Count)}, named by no verified generation; never deleted here");
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
        ConsoleUi.Line("  Without --confirm, verifies both pointers and previews a possible rollback; writes nothing.");
        ConsoleUi.Line("  --confirm with the preview's manifest digest preserves the damaged pointer, then");
        ConsoleUi.Line("  re-points current to the fully verified last-known-good generation.");
        ConsoleUi.Line("  Later orphan evidence is never deleted. A confirmation whose digest no longer matches");
        ConsoleUi.Line("  the verified generation changes nothing and exits 2; run the preview again.");
    }
}
