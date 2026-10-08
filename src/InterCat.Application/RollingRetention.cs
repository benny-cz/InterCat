using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// §20.2's rolling retention of a session a follow derives while its capture records: how much session time it keeps,
/// and how far past that it grows before its oldest records go.
/// </summary>
public sealed record RollingRetentionPolicy
{
    public RollingRetentionPolicy(TimeSpan keep)
    {
        if (keep < TimeSpan.FromSeconds(1) || keep > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "A rolling window keeps between 1 second and 24 hours.");
        }

        Keep = keep;
    }

    /// <summary>The session time kept: every record read in the newest stretch this long is retained.</summary>
    public TimeSpan Keep { get; }

    /// <summary>
    /// How far past its window a session grows before its oldest records go: a quarter of it, so each release gives up
    /// about a quarter of the window and releases come that far apart in session time, not every publication.
    /// </summary>
    public TimeSpan Slack => Keep / 4;

    /// <summary>The window as a person reads it: "the last 10 minutes", "the last hour", "the last 90 seconds".</summary>
    public string Window
    {
        get
        {
            long seconds = (long)Keep.TotalSeconds;
            return seconds % 3_600 == 0 ? (seconds == 3_600 ? "the last hour" : string.Create(CultureInfo.CurrentCulture, $"the last {seconds / 3_600:N0} hours"))
                : seconds % 60 == 0 ? (seconds == 60 ? "the last minute" : string.Create(CultureInfo.CurrentCulture, $"the last {seconds / 60:N0} minutes"))
                : seconds == 1 ? "the last second"
                : string.Create(CultureInfo.CurrentCulture, $"the last {seconds:N0} seconds");
        }
    }

    /// <summary>Why a release this policy makes gives its records up, as the session's retention record states it.</summary>
    public string Reason => "rolling retention keeps " + Window;
}

/// <summary>
/// What one step of a rolling policy did (§20.2, ADR-045, ADR-046): the release it published, the pin that holds it back
/// from the window, and why the follow must stop.
/// </summary>
public sealed record RollingStep
{
    /// <summary>A step that found nothing due.</summary>
    public static RollingStep Idle { get; } = new();

    /// <summary>The release this step published; null when none was due or none could be made.</summary>
    public IntervalReleaseResult? Released { get; init; }

    /// <summary>
    /// The pin keeping the session from its window: the earliest whose moment lies before the records the policy is due to
    /// release, which every release then stops at; null when none does.
    /// </summary>
    public RetentionPin? HeldBy { get; init; }

    /// <summary>Whether <see cref="HeldBy"/> began holding the policy back at this step.</summary>
    public bool NewlyHeld { get; init; }

    /// <summary>
    /// Why the follow stops rather than go on: the session holds more than a pin holding it back allows, or its pins could
    /// not be read, so neither what they keep nor how much they allow is known. Null while it goes on.
    /// </summary>
    public string? Stop { get; init; }
}

/// <summary>
/// Runs a <see cref="RollingRetentionPolicy"/> for one follow (§20.2, ADR-043, ADR-044). Once the session holds more than
/// its window and a quarter past its oldest record or its latest release, the records read before its newest window go
/// by interval release, which keeps what later records rest on and states the boundary every reader then reads as a
/// partial gap before. The follow's own writer calls it between two mirrors: a release published beneath another writer
/// fails that writer's next publication (ADR-024).
/// </summary>
/// <remarks>
/// A pin a person placed keeps every record read from its moment (ADR-046), so a release due past one stops at it and the
/// session grows past its window. Once it holds more than such a pin allows, the step says the follow stops: the pin's
/// records are never released, and the session never grows past what the person allowed.
/// </remarks>
public sealed class RollingRetention
{
    // After a release, or one refused, nothing is asked again until the session has grown by another slack: a chunk is
    // released whole, so the boundary reached can lie well before the one asked for, and planning reads every row.
    private long? waitUntilNanoseconds;

    // The boundary the last attempt planned to. A pin holds every attempt at its moment, where nothing new can go: the
    // units before it are those planned already.
    private long? lastPlanned;

    public RollingRetention(RollingRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
    }

    public RollingRetentionPolicy Policy { get; }

    /// <summary>How many releases this follow's policy has published.</summary>
    public int Releases { get; private set; }

    /// <summary>The pin holding the policy back from the window at its latest step; null when none did.</summary>
    public RetentionPin? HeldBy { get; private set; }

    /// <summary>Why a step said the follow must stop; null while none has.</summary>
    public string? Stopped { get; private set; }

    /// <summary>
    /// Releases the records read before the session's newest window when they are due, up to the earliest pin, and says
    /// whether the follow must stop because the session outgrew what a pin holding it back allows.
    /// </summary>
    public RollingStep Step(SessionStore store, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Current is not { } manifest || SessionRecording.RecordsExtent(store, cancellationToken) is not { } extent)
        {
            return RollingStep.Idle;
        }

        // Presentation ticks are 100 ns: the newest record reads at the extent's last tick.
        long latest = checked((extent.EndTicks - 1) * 100);
        long held = manifest.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds
            ?? checked(extent.StartTicks * 100);
        long keep = checked(Policy.Keep.Ticks * 100);
        long slack = checked(Policy.Slack.Ticks * 100);
        if (latest - held <= keep + slack)
        {
            HeldBy = null;
            return RollingStep.Idle;
        }

        long boundary = latest - keep;
        IReadOnlyList<RetentionPin> pins;
        try
        {
            pins = store.Pins();
        }
        catch (InvalidDataException exception)
        {
            Stopped = exception.Message + " Nothing is released that a pin might keep, and how much its pins allow the "
                + "session to hold is not known.";
            return new() { Stop = Stopped };
        }

        RetentionPin[] holding = [.. pins.Where(pin => pin.FromNanoseconds < boundary)];
        RetentionPin? heldBy = holding.FirstOrDefault();
        bool newly = heldBy is not null && heldBy.Id != HeldBy?.Id;
        HeldBy = heldBy;
        long planned = heldBy?.FromNanoseconds ?? boundary;
        IntervalReleaseResult? released = null;
        if (latest >= waitUntilNanoseconds.GetValueOrDefault(long.MinValue) && planned != lastPlanned)
        {
            waitUntilNanoseconds = latest + slack;
            lastPlanned = planned;
            try
            {
                if (IntervalRelease.Preview(store, boundary, cancellationToken).ReleasesAnything)
                {
                    released = IntervalRelease.Release(store, boundary, Policy.Reason, nowUtc, cancellationToken: cancellationToken);
                    Releases++;
                }
            }
            catch (RetentionPinnedException)
            {
                // A pin placed while the release planned refused it at publication, or the pins could no longer be read;
                // the next step reads them again and plans to the pin.
                lastPlanned = null;
            }
        }

        long size = (store.Current ?? manifest).HeldBytes();
        string? stop = holding.Where(pin => size > pin.AllowanceBytes).MinBy(pin => pin.AllowanceBytes) is { } outgrown
            ? Outgrown(outgrown, size)
            : null;
        Stopped = stop ?? Stopped;
        return new()
        {
            Released = released,
            HeldBy = heldBy,
            NewlyHeld = newly,
            Stop = stop,
        };
    }

    /// <summary>Why a session that outgrew what a pin holding it back allows cannot go on growing.</summary>
    private static string Outgrown(RetentionPin pin, long size) =>
        $"The pin from {SessionTimeText.Seconds(pin.FromNanoseconds, CultureInfo.CurrentCulture)} ({pin.Reason}) allows this "
        + $"session {ByteSizeText.Of(pin.AllowanceBytes)}, and it holds {ByteSizeText.Of(size)}: the records the pin keeps "
        + "cannot be released, and the session cannot grow past what it allows.";
}
