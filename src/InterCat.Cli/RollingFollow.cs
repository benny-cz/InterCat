using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
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
    /// Runs the policy after a pass that mirrored chunks, and says what a release gave up in the words the window's size
    /// line uses.
    /// </summary>
    public static void Step(RollingRetention? rolling, SessionStore derived, FollowedPass pass, bool json, CancellationToken cancellationToken)
    {
        if (rolling is null || pass.MirroredChunks == 0)
        {
            return;
        }

        IntervalReleaseResult? released = rolling.Step(derived, DateTimeOffset.UtcNow, cancellationToken);
        if (released is not null && !json && derived.Current is { } manifest && SessionGrowth.Retained(manifest) is { } retained)
        {
            ConsoleUi.Progress(retained);
        }
    }

    /// <summary>What a document states of the policy: its window, its releases, and from when every record is kept.</summary>
    public static RollingDocument? Describe(RollingRetention? rolling, SessionStore derived) => rolling is null ? null : new()
    {
        KeepSeconds = (int)rolling.Policy.Keep.TotalSeconds,
        Window = rolling.Policy.Window,
        Releases = rolling.Releases,
        KeptFromNanoseconds = derived.Current?.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds,
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
}
