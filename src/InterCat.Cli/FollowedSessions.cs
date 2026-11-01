using InterCat.Capture.Journal;

namespace InterCat.Cli;

/// <summary>
/// A session a capture is being followed or recorded into right now (live-follow-v1, icat record). That capture alone
/// publishes into it: a generation published beside it makes the capture's next publication fail, which ends it (ADR-044),
/// so a command that would publish into the session refuses before it publishes. A follow that was interrupted no longer
/// holds the session, which is finished rather than refused, and a command that only measures or reads goes ahead.
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
    /// Whether icat record is recording into a session's folder: it holds the file beside it (<see cref="RecordingHold"/>).
    /// A test says which of its own sessions are, as for a follow.
    /// </summary>
    internal static Func<string, bool> IsRecorded { get; set; } = RecordingHold.IsHeld;

    /// <summary>
    /// Why a command does not publish into <paramref name="sessionDirectory"/> while a capture is being followed or recorded
    /// into it, ending with <paramref name="instead"/>, what can be done; null when no capture holds it.
    /// </summary>
    public static string? Refusal(string sessionDirectory, string instead) =>
        IsFollowed(sessionDirectory)
            ? $"A capture is being followed into {sessionDirectory} right now, and only its follow publishes into it: anything "
                + $"published beside it would end the follow. {instead}"
            : IsRecorded(sessionDirectory)
                ? $"A capture is being recorded into {sessionDirectory} right now, and only its recording publishes into it: "
                    + $"anything published beside it would end the recording. {instead}"
                : null;
}
