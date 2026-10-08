using System.Text.Json;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What publishing a session's derivation checkpoint did.</summary>
internal sealed record CheckpointDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required string Outcome { get; init; }
    public required long SourceGeneration { get; init; }
    public required long? PublishedGeneration { get; init; }
    public required long Bytes { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required string? Reason { get; init; }
}

/// <summary>
/// `icat checkpoint`: publishes the derivation checkpoint of a session's current generation
/// (`contracts/derivation-checkpoint-v1.md`), with its overview and its RPC calls (`contracts/operation-index-v1.md`), for
/// a session written before its writer published them. Opening the session then builds its processes, relationships and
/// calls from them rather than from every record. It derives only what the segments already hold, so it publishes
/// without a separate confirmation.
/// </summary>
internal static class CheckpointCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? outputOption = command.TakeOption("--output");
        string? pathOption = command.TakePositional();
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (pathOption is null)
        {
            ConsoleUi.Failure("A published session directory is required: icat checkpoint <directory>.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string full = System.IO.Path.GetFullPath(pathOption);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? output = outputOption is null ? null : System.IO.Path.GetFullPath(outputOption);
        if (output is not null && File.Exists(output) && !overwrite)
        {
            ConsoleUi.Failure($"{output} exists. Pass --overwrite to replace the report.");
            return InterCatExitCode.InvalidInvocation;
        }

        // A follow writing this session holds its ticket (live-follow-v1). A generation published beside it would make the
        // follow's next commit stale, and the follow publishes a checkpoint of its own when it finishes.
        if (File.Exists(LiveFollowTicket.PathFor(full)) && LiveFollowTicket.FindInterruptedFor(full) is null)
        {
            ConsoleUi.Failure(
                $"A capture is being followed into {full} right now. Its follow publishes the checkpoint when it finishes.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ConsoleUi.Progress($"Opening {full} and verifying the published generation and every dependency.");
        SessionStore inspected = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (inspected.Current is not { } current)
        {
            return Icat.NoSession();
        }

        // A store opened for reading cannot publish: a generation names its session, which is taken from the one this
        // session already published.
        SessionStore writable = SessionStore.Open(LocalOwnedDirectory.Open(full), current.SessionId, current.SourceIdentity);
        CheckpointPublication publication = CheckpointStep.Publish(writable, cancellationToken);
        var document = new CheckpointDocument
        {
            Contract = "checkpoint-v1",
            Path = full,
            Outcome = publication.Outcome.ToString(),
            SourceGeneration = publication.SourceGeneration,
            PublishedGeneration = publication.PublishedGeneration,
            Bytes = publication.Bytes,
            ElapsedMilliseconds = (long)publication.Elapsed.TotalMilliseconds,
            Reason = publication.Reason,
        };
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document);
        }

        if (output is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Checkpoint report written to {output}.");
        }

        return publication.Outcome == CheckpointOutcome.Refused
            ? InterCatExitCode.PermissionOrCapabilityFailure
            : InterCatExitCode.Success;
    }

    private static void Render(CheckpointDocument document)
    {
        ConsoleUi.Heading(document.PublishedGeneration is null ? "No checkpoint published" : "Checkpoint published");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field(
            "Generation",
            document.PublishedGeneration is { } published
                ? $"{ConsoleUi.Count(document.SourceGeneration)} → {ConsoleUi.Count(published)}"
                : ConsoleUi.Count(document.SourceGeneration));
        if (document.PublishedGeneration is not null)
        {
            ConsoleUi.Field("Size", ConsoleUi.Size(document.Bytes));
        }

        if (document.Reason is { } reason)
        {
            ConsoleUi.Note(reason);
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat checkpoint <session-dir> [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Publishes the derivation checkpoint of the session's current generation, with its overview");
        ConsoleUi.Line("  and its RPC calls, so opening the session builds its processes, relationships and calls");
        ConsoleUi.Line("  without reading every record. Imports, finished live captures, compactions and re-derivations");
        ConsoleUi.Line("  publish one themselves; this is for sessions written before they did, or before they kept");
        ConsoleUi.Line("  everything a checkpoint keeps now. A session that already names a current one is unchanged.");
    }
}
