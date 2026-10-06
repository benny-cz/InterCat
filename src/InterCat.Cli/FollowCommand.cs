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

        string? pollOption = command.TakeOption("--poll");
        string? evidenceOption = command.TakePositional();
        string? sessionOption = command.TakePositional();
        bool once = command.TryTakeFlag("--once");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (evidenceOption is not null && sessionOption is null && pollOption is null && !once)
        {
            return FinishInterrupted(Path.GetFullPath(evidenceOption), json, cancellationToken);
        }

        if (evidenceOption is null || sessionOption is null)
        {
            ConsoleUi.Failure("An evidence directory and a session directory are required: icat follow <evidence-dir> <session-dir>.");
            ConsoleUi.Explain(PrintHelp);
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
                                $"Mirrored {CountText.Of(step.MirroredChunks, "chunk")} of {CountText.Of(step.MirroredRecords, "record")}: "
                                + $"{step.DerivedChunks:N0} of {CountText.Of(step.EvidenceChunks, "chunk")} and "
                                + $"{CountText.Of(step.DerivedRecords, "record")} derived, generation {step.DerivedGeneration:N0}.");
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

        if (document.Finished)
        {
            _ = CheckpointStep.Publish(derived, cancellationToken);
        }

        return document.Finished ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>
    /// `icat follow <session-dir>`: finishes a session whose follow ended early - `icat capture` or the Desktop stopped
    /// before every published chunk was derived - from the ticket beside it, which names the evidence (live-follow-v1).
    /// Nothing is recorded again: the chunks the broker kept are derived here, as the follow would have.
    /// </summary>
    private static InterCatExitCode FinishInterrupted(string sessionPath, bool json, CancellationToken cancellationToken)
    {
        LiveFollowTicket? ticket = LiveFollowTicket.FindInterruptedFor(sessionPath);
        if (ticket is null)
        {
            ConsoleUi.Failure(File.Exists(LiveFollowTicket.PathFor(sessionPath))
                ? $"The capture beside {sessionPath} is being followed or finished elsewhere right now. Let that end, "
                    + "then run this again if the session is still unfinished."
                : Directory.Exists(sessionPath)
                    ? $"Nothing unfinished is recorded beside {sessionPath}: its capture's follow completed, or it was not "
                        + "followed from a capture. To derive a session from evidence, name both: "
                        + "icat follow <evidence-dir> <session-dir>."
                    : $"There is no session at {sessionPath}, and no unfinished capture recorded beside it. To derive a "
                        + "session from evidence, name both: icat follow <evidence-dir> <session-dir>.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        InterruptedFollow found = InterruptedFollow.Assess(ticket);
        switch (found.State)
        {
            case InterruptedFollowState.Complete:
                _ = LiveFollowTicket.Remove(sessionPath);
                ConsoleUi.Progress($"{sessionPath} already holds everything its capture published; its ticket is removed.");
                return InterCatExitCode.Success;
            case InterruptedFollowState.EvidenceGone:
                ConsoleUi.Failure(
                    $"The capture's evidence at {ticket.EvidenceDirectory} can no longer be read"
                    + (found.Problem is { } problem ? $" ({problem})" : string.Empty)
                    + $". The session keeps the {ConsoleUi.Count(found.SessionChunks)} chunk(s) followed before.");
                return InterCatExitCode.PartialResultSuccess;
            case InterruptedFollowState.StillRecording:
                ConsoleUi.Warn(
                    $"The capture may still be recording or stopping until {found.SettledUtc.ToLocalTime():g}. What it has "
                    + "published so far is derived now; run this again after then to finish it.");
                break;
            default:
                break;
        }

        InterruptedFollowResult result;
        try
        {
            ConsoleUi.Progress($"Finishing {sessionPath} from the evidence at {ticket.EvidenceDirectory}.");
            result = InterruptedFollow.Finish(ticket, new StepReport(json), cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ConsoleUi.Warn("Stopped. What was derived is kept; running this again continues.");
            return InterCatExitCode.PartialResultSuccess;
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        var document = new FollowDocument
        {
            Contract = "follow-v1",
            EvidencePath = ticket.EvidenceDirectory,
            SessionPath = sessionPath,
            Finished = result.Step.Finished,
            EvidenceChunks = result.Step.EvidenceChunks,
            DerivedChunks = result.Step.DerivedChunks,
            DerivedRecords = result.Step.DerivedRecords,
            Generation = result.Session.Current?.Generation,
            Compactions = result.Step.Compactions,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
            if (result.Completed && !result.Step.Finished)
            {
                ConsoleUi.Note(
                    "The capture ended before it was finalized, so records after its last publication were never kept. "
                    + "The session holds everything it published.");
            }
        }

        if (result.Completed)
        {
            _ = CheckpointStep.Publish(result.Session, cancellationToken);
        }

        return result.Completed ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>Reports each step of a finish as it is taken, on the finishing thread, so lines never interleave.</summary>
    private sealed class StepReport(bool json) : IProgress<FollowStep>
    {
        public void Report(FollowStep value)
        {
            if (!json && value.MirroredChunks > 0)
            {
                ConsoleUi.Progress(
                    $"{ConsoleUi.Count(value.DerivedChunks)} of {ConsoleUi.Count(value.EvidenceChunks)} chunks derived, "
                    + $"{ConsoleUi.Count(value.DerivedRecords)} records.");
            }
        }
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
        ConsoleUi.Line("  icat follow <session-dir> [--json]");
        ConsoleUi.Line("      Finishes a session whose follow ended early, from the ticket icat capture or the");
        ConsoleUi.Line("      Desktop left beside it. Nothing is recorded again: what the broker kept is derived.");
    }
}
