using InterCat.Capture.Journal;

namespace InterCat.Cli;

/// <summary>
/// A session a capture is being followed into right now (live-follow-v1). Its follow alone publishes into it: a generation
/// published beside the follow makes the follow's next publication fail, which ends the follow (ADR-044), so a command
/// that would publish into the session refuses before it publishes. A follow that was interrupted no longer holds the
/// session, which is finished rather than refused, and a command that only measures or reads goes ahead.
/// </summary>
internal static class FollowedSessions
{
    /// <summary>
    /// Whether a capture is being followed into a session's folder: its follow holds the folder's ticket and was not
    /// interrupted. A test says which of its own sessions are, since only Windows enforces a hold between two handles in
    /// one process.
    /// </summary>
    internal static Func<string, bool> IsFollowed { get; set; } = sessionDirectory =>
        File.Exists(LiveFollowTicket.PathFor(sessionDirectory)) && LiveFollowTicket.FindInterruptedFor(sessionDirectory) is null;

    /// <summary>
    /// Why a command does not publish into <paramref name="sessionDirectory"/> while a capture is being followed into it,
    /// ending with <paramref name="instead"/>, what can be done; null when no follow holds it.
    /// </summary>
    public static string? Refusal(string sessionDirectory, string instead) => IsFollowed(sessionDirectory)
        ? $"A capture is being followed into {sessionDirectory} right now, and only its follow publishes into it: anything "
            + $"published beside it would end the follow. {instead}"
        : null;
}
