using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Where a follow stands: what the evidence session committed and what the derived session holds.</summary>
internal sealed record FollowDocument
{
    public required string Contract { get; init; }
    public required string EvidencePath { get; init; }
    public required string SessionPath { get; init; }

    /// <summary>Whether the capture stopped and every chunk, with the coverage ledger, is mirrored.</summary>
    public required bool Finished { get; init; }
    public required int EvidenceChunks { get; init; }
    public required int DerivedChunks { get; init; }
    public required long DerivedRecords { get; init; }
    public required long? Generation { get; init; }
    public required int Compactions { get; init; }
}

/// <summary>
/// `icat follow`: derives a session from the evidence a privileged recorder publishes (§9, ADR-027). Every committed
/// journal chunk is copied byte for byte into the session and checked against the evidence's digest, and its rows are
/// derived there, in this ordinary process. It follows a recording while it records and ends when the capture stops;
/// Ctrl+C stops it, and running it again continues where it stopped.
/// </summary>
internal static class FollowCommand
{
    private const int DefaultPollSeconds = 1;
    private const int MaximumPollSeconds = 3_600;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? evidenceOption = command.TakePositional();
        string? sessionOption = command.TakePositional();
        string? pollOption = command.TakeOption("--poll");
        bool once = command.TryTakeFlag("--once");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (evidenceOption is null || sessionOption is null)
        {
            ConsoleUi.Failure("An evidence directory and a session directory are required: icat follow <evidence-dir> <session-dir>.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string evidencePath = Path.GetFullPath(evidenceOption);
        string sessionPath = Path.GetFullPath(sessionOption);
        if (!Directory.Exists(evidencePath))
        {
            ConsoleUi.Failure($"No evidence directory at {evidencePath}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (string.Equals(evidencePath.TrimEnd(Path.DirectorySeparatorChar), sessionPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUi.Failure("The session is derived into a directory of its own, not into the evidence directory.");
            return InterCatExitCode.InvalidInvocation;
        }

        int pollSeconds = DefaultPollSeconds;
        if (pollOption is not null
            && (!int.TryParse(pollOption, NumberStyles.None, CultureInfo.InvariantCulture, out pollSeconds)
                || pollSeconds is < 1 or > MaximumPollSeconds))
        {
            ConsoleUi.Failure($"--poll expects whole seconds from 1 to {MaximumPollSeconds:N0}.");
            return InterCatExitCode.InvalidInvocation;
        }

        TimeSpan poll = TimeSpan.FromSeconds(pollSeconds);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        if (evidence.Current is null)
        {
            if (once)
            {
                ConsoleUi.Warn("The recording has published nothing yet, so there is nothing to derive.");
                return InterCatExitCode.PartialResultSuccess;
            }

            ConsoleUi.Progress("Waiting for the recording's first publication. Ctrl+C stops.");
            try
            {
                while (evidence.Current is null)
                {
                    await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                    evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
                }
            }
            catch (OperationCanceledException)
            {
                ConsoleUi.Warn("Stopped before the recording published anything.");
                return InterCatExitCode.PartialResultSuccess;
            }
        }

        // The derived session is the same session in a directory of its own, so it takes the evidence's identity.
        SessionManifestV1 source = evidence.Current!;
        Directory.CreateDirectory(sessionPath);
        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(sessionPath), source.SessionId, source.SourceIdentity);
        FollowStep? last = null;
        try
        {
            LiveSessionFollower follower = LiveSessionFollower.Open(evidence, derived, cancellationToken: cancellationToken);
            ConsoleUi.Progress(
                once
                    ? $"Deriving what {evidencePath} has committed so far into {sessionPath}."
                    : $"Following {evidencePath} into {sessionPath}. Ctrl+C stops; running this again continues.");
            if (once)
            {
                last = follower.CatchUp(cancellationToken: cancellationToken);
            }
            else
            {
                last = await follower.FollowAsync(
                    poll,
                    step =>
                    {
                        last = step;
                        if (step.MirroredChunks > 0 && !json)
                        {
                            ConsoleUi.Progress(
                                $"Mirrored {step.MirroredChunks:N0} chunk(s) of {step.MirroredRecords:N0} records: "
                                + $"{step.DerivedChunks:N0} of {step.EvidenceChunks:N0} chunks and "
                                + $"{step.DerivedRecords:N0} records derived, generation {step.DerivedGeneration:N0}.");
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is how a follow is stopped: what was mirrored stays mirrored.
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        var document = new FollowDocument
        {
            Contract = "follow-v1",
            EvidencePath = evidencePath,
            SessionPath = sessionPath,
            Finished = last?.Finished ?? false,
            EvidenceChunks = last?.EvidenceChunks ?? 0,
            DerivedChunks = last?.DerivedChunks ?? 0,
            DerivedRecords = last?.DerivedRecords ?? 0,
            Generation = derived.Current?.Generation,
            Compactions = last?.Compactions ?? 0,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
        }

        return document.Finished ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    private static void Render(FollowDocument document)
    {
        ConsoleUi.Heading(document.Finished ? "Recording derived" : "Following stopped");
        ConsoleUi.Field("Evidence", document.EvidencePath);
        ConsoleUi.Field("Session", document.SessionPath);
        ConsoleUi.Field("Chunks", $"{ConsoleUi.Count(document.DerivedChunks)} of {ConsoleUi.Count(document.EvidenceChunks)} mirrored");
        ConsoleUi.Field("Records", $"{ConsoleUi.Count(document.DerivedRecords)} derived");
        ConsoleUi.Field("Generation", document.Generation is { } generation ? ConsoleUi.Count(generation) : "none published");
        ConsoleUi.Field(
            "State",
            document.Finished
                ? "finished: the capture stopped, and its coverage ledger is mirrored"
                : "the capture has not finished here; run this again to continue");
        ConsoleUi.Note(
            "Every chunk was copied byte for byte and checked against the evidence's digest; its rows were derived here. "
            + "The session is an ordinary one: icat session, processes and metric read it.");
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat follow <evidence-dir> <session-dir> [--poll <seconds>] [--once] [--json]");
        ConsoleUi.Line("      Derives a session from what icat record --evidence-only publishes, in this ordinary");
        ConsoleUi.Line("      process: each committed journal chunk is copied byte for byte and checked, and its");
        ConsoleUi.Line("      rows derived here. It follows while the capture records, ends when it stops, and");
        ConsoleUi.Line("      continues where it stopped when run again. --once derives what is committed now.");
    }
}
