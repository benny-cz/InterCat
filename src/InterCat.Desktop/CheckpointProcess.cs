using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InterCat.Application;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// What to launch to publish a finished capture's derivation checkpoint in a process of its own: its executable and the
/// arguments that precede the session's directory.
/// </summary>
public sealed record CheckpointLaunchTarget(string ExecutablePath, IReadOnlyList<string> LeadingArguments)
{
    /// <summary>The argument the Desktop takes to publish one session's checkpoint, show no window, and exit.</summary>
    public const string DesktopArgument = "--publish-checkpoint";

    private const string DesktopExecutableName = "InterCat.Desktop.exe";

    /// <summary>The Desktop's own executable, when this process is the Desktop; null in any other host, which publishes in its own process.</summary>
    public static CheckpointLaunchTarget? OfThisDesktop() =>
        Environment.ProcessPath is { } path
        && string.Equals(Path.GetFileName(path), DesktopExecutableName, StringComparison.OrdinalIgnoreCase)
            ? new(path, [DesktopArgument])
            : null;
}

/// <summary>
/// Publishes a finished capture's derivation checkpoint (`SessionCheckpoints`) in a process of its own, so it reads every
/// segment within a heap of its own rather than beside everything the window holds: at a busy hour's 4.5 million rows the
/// window held up to 478 MiB through its last minutes of recording, and the checkpoint it published beside them fitted
/// 512 MiB in one run of two, where alone it fits in every run (revision 884). The child writes what it published as one
/// line of JSON; a checkpoint only saves time, so a child that cannot start, ends without saying, or is refused fails
/// nothing and says why.
/// </summary>
public static class CheckpointProcess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Publishes the checkpoint of a session a capture finished writing through <paramref name="store"/>: in a process
    /// launched from <paramref name="target"/> when there is one, after which the store reads the generation it published,
    /// as it would another writer's, at a lease; in this process otherwise.
    /// </summary>
    public static CheckpointPublication PublishFinished(
        SessionStore store,
        string sessionPath,
        CheckpointLaunchTarget? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (target is null)
        {
            return SessionCheckpoints.Publish(store, DateTimeOffset.UtcNow, cancellationToken);
        }

        CheckpointPublication published = Publish(target, sessionPath, cancellationToken);
        using (store.AcquireLease())
        {
        }

        return published;
    }

    /// <summary>
    /// The child's half: opens the session in <paramref name="sessionPath"/> as its writer, publishes the checkpoint of its
    /// current generation, and writes what came of it to <paramref name="output"/> as one line of JSON. Returns the exit
    /// code: 0 unless the checkpoint was refused.
    /// </summary>
    public static int Run(string sessionPath, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        ArgumentNullException.ThrowIfNull(output);
        long started = Stopwatch.GetTimestamp();
        CheckpointPublication publication;
        try
        {
            string full = Path.GetFullPath(sessionPath);

            // A store opened for reading cannot publish: a generation names its session, which is taken from the one this
            // session already published, as icat checkpoint takes it.
            publication = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full)).Current is not { } current
                ? new(CheckpointOutcome.NothingToDerive, 0, null, 0, Stopwatch.GetElapsedTime(started),
                    "The session has published no generation to derive a checkpoint of.")
                : SessionCheckpoints.Publish(
                    SessionStore.Open(LocalOwnedDirectory.Open(full), current.SessionId, current.SourceIdentity),
                    DateTimeOffset.UtcNow,
                    cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            publication = new(CheckpointOutcome.Refused, 0, null, 0, Stopwatch.GetElapsedTime(started), exception.Message);
        }

        output.WriteLine(JsonSerializer.Serialize(Report.Of(publication), Json));
        output.Flush();
        return publication.Outcome == CheckpointOutcome.Refused ? 1 : 0;
    }

    /// <summary>
    /// The child's half as an executable's entry: <see cref="Run"/> over the process's standard output, written as UTF-8
    /// whatever the console's code page.
    /// </summary>
    public static int RunOverStandardOutput(string sessionPath, CancellationToken cancellationToken)
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Run(sessionPath, output, cancellationToken);
    }

    /// <summary>
    /// The window's half: launches <paramref name="target"/> to publish the checkpoint of the session in
    /// <paramref name="sessionPath"/> and waits for it, returning what it published. Cancelling ends the child and its
    /// children, as stopping the window's own publication cancelled it. The session's store learns of the generation the
    /// child published at its next lease.
    /// </summary>
    public static CheckpointPublication Publish(
        CheckpointLaunchTarget target,
        string sessionPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        long started = Stopwatch.GetTimestamp();
        var start = new ProcessStartInfo(target.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in target.LeadingArguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(Path.GetFullPath(sessionPath));
        Process? child;
        try
        {
            child = Process.Start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Refusal(started, $"Its process could not be started: {exception.Message}");
        }

        if (child is null)
        {
            return Refusal(started, "Its process could not be started.");
        }

        using (child)
        {
            Task<string> said = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> complained = child.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                child.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                End(child);
                throw;
            }

            string output = said.GetAwaiter().GetResult();
            string error = complained.GetAwaiter().GetResult();
            return Read(output) ?? Refusal(started,
                $"Its process ended with exit code {child.ExitCode} before saying what it published"
                + (LastLine(error) is { } last ? $": {last}" : "."));
        }
    }

    /// <summary>What the child said it published: the last line of its output that reads as a report, or null.</summary>
    private static CheckpointPublication? Read(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            try
            {
                if (JsonSerializer.Deserialize<Report>(line, Json)?.Publication() is { } publication)
                {
                    return publication;
                }
            }
            catch (JsonException)
            {
                // Not the report: whatever else the child wrote before it.
            }
        }

        return null;
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static CheckpointPublication Refusal(long started, string reason) =>
        new(CheckpointOutcome.Refused, 0, null, 0, Stopwatch.GetElapsedTime(started), reason);

    private static void End(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // It ended on its own meanwhile.
        }
    }

    /// <summary>What a child publishing a checkpoint says it published.</summary>
    private sealed record Report(
        string Outcome,
        long SourceGeneration,
        long? PublishedGeneration,
        long Bytes,
        long ElapsedMilliseconds,
        string? Reason)
    {
        public static Report Of(CheckpointPublication publication) => new(
            publication.Outcome.ToString(),
            publication.SourceGeneration,
            publication.PublishedGeneration,
            publication.Bytes,
            (long)publication.Elapsed.TotalMilliseconds,
            publication.Reason);

        public CheckpointPublication? Publication() =>
            Enum.TryParse(Outcome, out CheckpointOutcome outcome) && Enum.IsDefined(outcome)
                ? new(outcome, SourceGeneration, PublishedGeneration, Bytes, TimeSpan.FromMilliseconds(ElapsedMilliseconds), Reason)
                : null;
    }
}
