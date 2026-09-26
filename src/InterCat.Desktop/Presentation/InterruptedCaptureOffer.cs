using System.Globalization;
using InterCat.Capture.Journal;

namespace InterCat.Desktop.Presentation;

/// <summary>The one action an unfinished capture's card offers besides forgetting it.</summary>
internal enum InterruptedCaptureAction
{
    /// <summary>Nothing yet, or nothing at all: the card explains and can be forgotten.</summary>
    None,

    /// <summary>Derive the chunks the session lacks from the evidence the broker kept.</summary>
    Finish,

    /// <summary>Open what the session holds; its evidence can no longer finish it.</summary>
    Open,
}

/// <summary>
/// What the rail's card says about a capture a viewer left unfinished, and what it offers (§3.1 step 6). A viewer that
/// crashes loses no evidence; the card says what the broker kept, what finishing does, and what it cannot recover.
/// </summary>
/// <param name="Rechecks">Whether the capture may still be stopping, so the card looks again until it has.</param>
internal sealed record InterruptedCaptureOffer(
    string Headline,
    string Detail,
    InterruptedCaptureAction Action,
    string ActionLabel,
    string ForgetLabel,
    bool Rechecks)
{
    /// <summary>What forgetting does, where the forget button explains itself.</summary>
    public const string ForgetExplanation =
        "Stop offering this capture. Your session so far and the broker's evidence stay where they are.";

    /// <summary>The card for an interrupted follow, or null when there is nothing left to offer.</summary>
    public static InterruptedCaptureOffer? For(
        InterruptedFollow follow,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(follow);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        string capture = "The capture from " + When(follow.Ticket.StartedUtc, nowUtc, zone, culture);
        string missing = Missing(follow.MissingChunks, follow.EvidenceChunks ?? 0, culture);
        return follow.State switch
        {
            InterruptedFollowState.Finishable => new(
                capture + " is not finished saving",
                $"The broker kept the capture, and {missing}. Finishing derives the rest; nothing is recorded again.",
                InterruptedCaptureAction.Finish, "Finish saving", "Forget", Rechecks: false),
            InterruptedFollowState.StillRecording => new(
                capture + " is still stopping",
                "InterCat closed while it was recording. The broker stops a capture within a minute of that and keeps "
                + "what it recorded; this card then offers to finish your session.",
                InterruptedCaptureAction.None, string.Empty, "Forget", Rechecks: true),
            InterruptedFollowState.EndedUnfinalized => new(
                capture + " ended before it was finalized",
                "The broker stopped before finalizing it, so records after its last publication were not kept, and "
                + $"{missing}. Saving derives what it published.",
                InterruptedCaptureAction.Finish, "Save what was published", "Forget", Rechecks: false),
            InterruptedFollowState.EvidenceGone when follow.HasSession => new(
                capture + " can no longer be finished",
                "Its evidence can no longer be read. Your session keeps the "
                + $"{Chunks(follow.SessionChunks, culture)} followed before InterCat closed.{Reason(follow)}",
                InterruptedCaptureAction.Open, "Open what was saved", "Forget", Rechecks: false),
            InterruptedFollowState.EvidenceGone => new(
                capture + " can no longer be finished",
                $"Its evidence can no longer be read, and nothing had been saved to your session.{Reason(follow)}",
                InterruptedCaptureAction.None, string.Empty, "Dismiss", Rechecks: false),
            _ => null,
        };
    }

    /// <summary>The card while a finish runs: how far it has come, and that stopping keeps what it derived.</summary>
    public static (string Headline, string Detail) Finishing(
        InterruptedFollow follow,
        FollowStep? step,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(follow);
        string headline = "Finishing the capture from " + When(follow.Ticket.StartedUtc, nowUtc, zone, culture);
        return step is null
            ? (headline, "Reading the evidence the broker kept. Stopping keeps what is derived.")
            : (headline, $"{step.DerivedChunks.ToString("N0", culture)} of {Chunks(step.EvidenceChunks, culture)} "
                + $"derived, {Records(step.DerivedRecords, culture)} so far. Stopping keeps what is derived.");
    }

    /// <summary>What the status says once a finished session opens.</summary>
    public static (string Headline, string Detail) Finished(InterruptedFollowResult result, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        FollowStep step = result.Step;
        return step.Finished
            ? ("Session saved", $"Every chunk the capture published is in this session: {Chunks(step.DerivedChunks, culture)}, "
                + $"{Records(step.DerivedRecords, culture)}. InterCat derived the rest from the evidence the broker kept.")
            : ("Partial session saved", "The capture ended before it was finalized, so records after its last publication "
                + $"were not kept. Every chunk it published is in this session: {Chunks(step.DerivedChunks, culture)}, "
                + $"{Records(step.DerivedRecords, culture)}.");
    }

    /// <summary>When a capture started, in the viewer's own zone: "today at 17:35", "yesterday at 09:02", or a date.</summary>
    public static string When(DateTimeOffset startedUtc, DateTimeOffset nowUtc, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        DateTime started = TimeZoneInfo.ConvertTime(startedUtc, zone).DateTime;
        DateTime today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        string time = started.ToString("t", culture);
        return started.Date == today ? "today at " + time
            : started.Date == today.AddDays(-1) ? "yesterday at " + time
            : started.ToString("d MMM", culture) + " at " + time;
    }

    /// <summary>"2 of its 3 published chunks are not in your session yet", or "its 1 published chunk is not…".</summary>
    private static string Missing(int missing, int published, CultureInfo culture) =>
        (missing >= published
            ? $"its {Chunks(published, culture)} {(published == 1 ? "is" : "are")}"
            : $"{missing.ToString("N0", culture)} of its {Chunks(published, culture)} {(missing == 1 ? "is" : "are")}")
        + " not in your session yet";

    private static string Chunks(int count, CultureInfo culture) =>
        count.ToString("N0", culture) + (count == 1 ? " published chunk" : " published chunks");

    private static string Records(long count, CultureInfo culture) =>
        count.ToString("N0", culture) + (count == 1 ? " record" : " records");

    private static string Reason(InterruptedFollow follow) =>
        string.IsNullOrWhiteSpace(follow.Problem) ? string.Empty : " Reason: " + follow.Problem;
}
