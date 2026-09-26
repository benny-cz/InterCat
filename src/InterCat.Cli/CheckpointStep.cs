using System.Globalization;
using InterCat.Application;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The last thing a command that finished writing a session does: publish the session's derivation checkpoint, so that
/// opening it builds its processes and relationships without reading every record (`contracts/derivation-checkpoint-v1.md`).
/// A checkpoint only saves time, so one that is not published is a warning and never fails the command. Everything is
/// said on standard error, beside the command's own progress, so a `--json` document is unchanged.
/// </summary>
internal static class CheckpointStep
{
    public static CheckpointPublication Publish(SessionStore store, CancellationToken cancellationToken)
    {
        ConsoleUi.Progress("Publishing the session's derivation checkpoint, so it reopens without reading every record.");
        CheckpointPublication publication;
        try
        {
            publication = SessionCheckpoints.Publish(store, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // What the command wrote is already published; only the checkpoint is left for another time.
            publication = new(CheckpointOutcome.Refused, store.Current?.Generation ?? 0, null, 0, TimeSpan.Zero,
                "It was cancelled; icat checkpoint publishes it later.");
        }

        switch (publication.Outcome)
        {
            case CheckpointOutcome.Published:
                ConsoleUi.Success(string.Create(CultureInfo.CurrentCulture,
                    $"Derivation checkpoint of generation {publication.SourceGeneration:N0} published as generation "
                    + $"{publication.PublishedGeneration:N0}: {ConsoleUi.Size(publication.Bytes)}, "
                    + $"{publication.Elapsed.TotalSeconds:F1} s."));
                break;
            case CheckpointOutcome.Refused:
                ConsoleUi.Warn(
                    $"No derivation checkpoint was published: {publication.Reason} The session is complete; opening it "
                    + "derives its processes and relationships from every segment instead.");
                break;
            default:
                ConsoleUi.Progress(publication.Reason ?? "No derivation checkpoint was needed.");
                break;
        }

        return publication;
    }
}
