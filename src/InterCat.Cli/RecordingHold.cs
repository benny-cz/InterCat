namespace InterCat.Cli;

/// <summary>
/// What icat record holds beside the session it records into for as long as it records (§20.2): a file it alone writes,
/// opened to be deleted once it is let go - however the recording ends, on Windows a crash included - so a command that
/// would publish into the session refuses while it records (<see cref="FollowedSessions"/>). A generation published beside
/// the recording makes its next publication fail, which ends the recording. Beside the session, as a follow's ticket is,
/// it is no file of the session's own.
/// </summary>
internal sealed class RecordingHold : IDisposable
{
    /// <summary>What the hold's file adds to its session folder's path.</summary>
    internal const string Suffix = ".recording";

    private readonly FileStream stream;

    private RecordingHold(FileStream stream) => this.stream = stream;

    /// <summary>The hold's file for a session directory, beside it.</summary>
    internal static string PathFor(string sessionDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory)) + Suffix;

    /// <summary>Holds <paramref name="sessionDirectory"/> for a recording until the hold is disposed.</summary>
    internal static RecordingHold Take(string sessionDirectory) =>
        new(new FileStream(PathFor(sessionDirectory), FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1,
            FileOptions.DeleteOnClose));

    /// <summary>Whether a recording holds <paramref name="sessionDirectory"/>: its file is there, and another holds it.</summary>
    internal static bool IsHeld(string sessionDirectory)
    {
        try
        {
            using var stream = new FileStream(PathFor(sessionDirectory), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // The recording that writes it holds it, sharing it only for reading.
            return true;
        }
    }

    public void Dispose() => stream.Dispose();
}
