using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// `--keep-last <seconds>` for a follow, as `icat capture` and `icat follow` take it: the session it derives keeps the
/// newest stretch of session time that long, its oldest records released between two mirrors by the follow's own writer
/// (§20.2, ADR-043, ADR-044).
/// </summary>
internal static class RollingFollow
{
    public const int MaximumSeconds = 86_400;

    /// <summary>Reads the option: null when it was not given, false with why when it is not whole seconds in range.</summary>
    public static bool TryParse(string? option, out RollingRetention? rolling, out string? problem)
    {
        rolling = null;
        problem = null;
        if (option is null)
        {
            return true;
        }

        if (!int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > MaximumSeconds)
        {
            problem = $"--keep-last expects whole seconds from 1 to {MaximumSeconds:N0}: the newest stretch of session time the session keeps.";
            return false;
        }

        rolling = new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(seconds)));
        return true;
    }

    /// <summary>
    /// Runs the policy after a pass that mirrored chunks, says what a release gave up in the words the window's size line
    /// uses and when a pin begins to keep the session past its window, and answers why the follow must stop: the session
    /// outgrew what a pin holding it back allows. Null while it goes on.
    /// </summary>
    public static string? Step(RollingRetention? rolling, SessionStore derived, FollowedPass pass, bool json, CancellationToken cancellationToken)
    {
        if (rolling is null || pass.MirroredChunks == 0)
        {
            return null;
        }

        RollingStep step = rolling.Step(derived, DateTimeOffset.UtcNow, cancellationToken);
        if (!json && step.Released is not null && derived.Current is { } manifest && SessionGrowth.Retained(manifest) is { } retained)
        {
            ConsoleUi.Progress(retained);
        }

        if (!json && step.NewlyHeld && step.HeldBy is { } pin && step.Stop is null)
        {
            ConsoleUi.Progress(RetentionPinText.HoldsWindow(pin, rolling.Policy));
        }

        return step.Stop;
    }

    /// <summary>
    /// What a person does to go on after a pin stopped the follow: remove it, or keep its records with a larger allowance.
    /// </summary>
    public static string GoOn(string sessionPath) =>
        $"To go on, remove the pin (icat pin {sessionPath} --remove <pin>) or pin from the same moment allowing more, "
        + "then follow again.";

    /// <summary>
    /// The limits a prepared capture stops at (§12.1 S5), as the window states them: one the broker releases for keeping
    /// <paramref name="rolling"/>'s window holds that window, and what its session gave up until the owner's next lease
    /// renewal, <paramref name="renewal"/> apart, says so and the broker's next publication releases it.
    /// </summary>
    public static CaptureLimits Limits(BrokerEffectiveCaptureSummary summary, RollingRetentionPolicy? rolling, TimeSpan renewal)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new(TimeSpan.FromSeconds(summary.Quota.MaximumDurationSeconds), summary.Quota.MaximumJournalBytes,
            summary.Quota.MinimumFreeDiskBytes,
            rolling is not null && summary.Retention == BrokerRetentionPolicy.ReleaseFollowed
                ? new CaptureWindow(rolling, renewal + TimeSpan.FromMilliseconds(summary.PublicationIntervalMilliseconds))
                : null);
    }

    /// <summary>What a document states of the policy: its window, its releases, from when every record is kept, and why it stopped.</summary>
    public static RollingDocument? Describe(RollingRetention? rolling, SessionStore derived) => rolling is null ? null : new()
    {
        KeepSeconds = (int)rolling.Policy.Keep.TotalSeconds,
        Window = rolling.Policy.Window,
        Releases = rolling.Releases,
        KeptFromNanoseconds = derived.Current?.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds,
        PinnedFromNanoseconds = rolling.HeldBy?.FromNanoseconds,
        Stopped = rolling.Stopped,
    };
}

/// <summary>How many chunks one pass of a follow mirrored: a release is due only after one that mirrored some.</summary>
internal readonly record struct FollowedPass(int MirroredChunks);

/// <summary>A follow's rolling retention, as its document states it.</summary>
internal sealed record RollingDocument
{
    /// <summary>The session time kept, in seconds.</summary>
    public required int KeepSeconds { get; init; }

    /// <summary>The window in words: "the last 10 minutes".</summary>
    public required string Window { get; init; }

    /// <summary>The releases this follow published.</summary>
    public required int Releases { get; init; }

    /// <summary>The boundary of the session's latest interval release: every record read at or after it is kept.</summary>
    public required long? KeptFromNanoseconds { get; init; }

    /// <summary>The moment of the pin keeping the session past its window at the latest step; null when none did.</summary>
    public long? PinnedFromNanoseconds { get; init; }

    /// <summary>Why the follow stopped rather than outgrow what a pin allows; null when no pin stopped it.</summary>
    public string? Stopped { get; init; }
}
