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
/// Runs a <see cref="RollingRetentionPolicy"/> for one follow (§20.2, ADR-043, ADR-044). Once the session holds more than
/// its window and a quarter past its oldest record or its latest release, the records read before its newest window go
/// by interval release, which keeps what later records rest on and states the boundary every reader then reads as a
/// partial gap before. The follow's own writer calls it between two mirrors: a release published beneath another writer
/// fails that writer's next publication (ADR-024).
/// </summary>
public sealed class RollingRetention
{
    // After a release, or one refused, nothing is asked again until the session has grown by another slack: a chunk is
    // released whole, so the boundary reached can lie well before the one asked for, and planning reads every row.
    private long? waitUntilNanoseconds;

    public RollingRetention(RollingRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
    }

    public RollingRetentionPolicy Policy { get; }

    /// <summary>How many releases this follow's policy has published.</summary>
    public int Releases { get; private set; }

    /// <summary>
    /// Releases the records read before the session's newest window when they are due; null when the session holds too
    /// little yet, or nothing before the window can be released.
    /// </summary>
    public IntervalReleaseResult? Step(SessionStore store, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Current is not { } manifest || SessionRecording.RecordsExtent(store, cancellationToken) is not { } extent)
        {
            return null;
        }

        // Presentation ticks are 100 ns: the newest record reads at the extent's last tick.
        long latest = checked((extent.EndTicks - 1) * 100);
        long held = manifest.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds
            ?? checked(extent.StartTicks * 100);
        long keep = checked(Policy.Keep.Ticks * 100);
        long slack = checked(Policy.Slack.Ticks * 100);
        if (latest - held <= keep + slack || latest < waitUntilNanoseconds)
        {
            return null;
        }

        long boundary = latest - keep;
        waitUntilNanoseconds = latest + slack;
        IntervalReleasePreview preview = IntervalRelease.Preview(store, boundary, cancellationToken);
        if (!preview.ReleasesAnything)
        {
            return null;
        }

        IntervalReleaseResult released = IntervalRelease.Release(store, boundary, Policy.Reason, nowUtc,
            cancellationToken: cancellationToken);
        Releases++;
        return released;
    }
}
