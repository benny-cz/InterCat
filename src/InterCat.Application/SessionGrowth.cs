using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A session's size as its current generation measured it (§12.1 S5): every file the generation names, at the length its
/// manifest measured before naming it, the journal's part of that, and the records its segments hold. Measured, never
/// estimated from a figure per record.
/// </summary>
/// <param name="Committed">When the generation was published: the moment its bytes were measured at.</param>
public sealed record SessionSize(long Bytes, int Files, long JournalBytes, long Records, DateTimeOffset Committed)
{
    /// <summary>What the session takes on disk for each record it holds, to the nearest byte; null while it holds none.</summary>
    public long? BytesPerRecord => Records > 0 ? (long)Math.Round((double)Bytes / Records, MidpointRounding.AwayFromZero) : null;

    /// <summary>The §12.1 tier its size on disk is in.</summary>
    public SessionSizeTier Tier => SessionGrowth.TierOf(Bytes);
}

/// <summary>§12.1's session size tiers, by size on disk: what InterCat is qualified to keep true at each.</summary>
public enum SessionSizeTier
{
    /// <summary>T1, up to 2 GiB: every §12 budget, cold and warm.</summary>
    Interactive = 1,

    /// <summary>T2, up to 20 GiB: every §12 budget, compaction inside §20.1's segment targets.</summary>
    Standard = 2,

    /// <summary>
    /// T3, up to 100 GiB: the interactive budgets hold for indexed predicates, and a full-content search or a novel graph
    /// expansion may answer progressively, saying so.
    /// </summary>
    Large = 3,

    /// <summary>T4, past 100 GiB, which M13 qualifies: no release is qualified there yet.</summary>
    Qualification = 4,
}

/// <summary>What stops a capture that stops at its limits (`StopAtLimit`): its length, its journal, and the disk it leaves free.</summary>
public sealed record CaptureLimits(TimeSpan MaximumDuration, long MaximumJournalBytes, long MinimumFreeDiskBytes);

/// <summary>
/// The volume a capture's evidence is written to: its free space when last measured, and whether the session derived from
/// that evidence grows on it too.
/// </summary>
public readonly record struct RecordingVolume(long FreeBytes, bool HoldsSession)
{
    /// <summary>
    /// The volume <paramref name="evidencePath"/> is on, measured now, and whether <paramref name="sessionPath"/> is on it;
    /// null when it cannot be read, which a statement then says.
    /// </summary>
    public static RecordingVolume? Measure(string evidencePath, string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        try
        {
            string? evidence = Path.GetPathRoot(Path.GetFullPath(evidencePath));
            if (string.IsNullOrEmpty(evidence))
            {
                return null;
            }

            return new(new DriveInfo(evidence).AvailableFreeSpace,
                string.Equals(evidence, Path.GetPathRoot(Path.GetFullPath(sessionPath)), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Which of a capture's limits stops it.</summary>
public enum CaptureStopCause
{
    /// <summary>Its length: certain, whatever it records.</summary>
    Duration = 1,

    /// <summary>Its journal reaching its limit, at the rate the session has grown so far.</summary>
    Journal = 2,

    /// <summary>Free disk reaching the reserve it keeps, at the rate the session has grown so far.</summary>
    Disk = 3,
}

/// <summary>When a recording capture stops, and which limit stops it, in words (§12.1 S5).</summary>
/// <param name="Remaining">How long until the first of its limits stops it.</param>
/// <param name="Cause">The limit that does.</param>
/// <param name="Measured">
/// Whether its journal and disk limits were projected from how fast the session has grown; false before it has grown
/// measurably, when only its length is certain.
/// </param>
public sealed record CaptureHeadroom(TimeSpan Remaining, CaptureStopCause Cause, bool Measured, string Statement);

/// <summary>
/// §12.1 S5: growth is disclosed before it hurts. A session's size, its bytes per record and the tier it is in, and while
/// it records, how long until its capture's limits stop it, from measured sizes - in one set of words for the window and
/// the command line (R18).
/// </summary>
public static class SessionGrowth
{
    /// <summary>T1's bound: 2 GiB on disk.</summary>
    public const long InteractiveBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>T2's bound: 20 GiB on disk.</summary>
    public const long StandardBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>T3's bound: 100 GiB on disk.</summary>
    public const long LargeBytes = 100L * 1024 * 1024 * 1024;

    /// <summary>How soon a stop is close enough that the window states it in caution ink.</summary>
    public static readonly TimeSpan Imminent = TimeSpan.FromMinutes(1);

    /// <summary>The size of the generation <paramref name="manifest"/> names, which holds <paramref name="records"/> records.</summary>
    public static SessionSize Measure(SessionManifestV1 manifest, long records)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentOutOfRangeException.ThrowIfNegative(records);
        long bytes = 0;
        long journal = 0;
        foreach (StoreDependency dependency in manifest.Dependencies)
        {
            bytes = checked(bytes + dependency.LengthBytes);
            if (dependency.Kind == StoreDependencyKind.Journal)
            {
                journal = checked(journal + dependency.LengthBytes);
            }
        }

        return new(bytes, manifest.Dependencies.Count, journal, records, manifest.CommittedUtc);
    }

    /// <summary>The tier a session of <paramref name="bytes"/> on disk is in.</summary>
    public static SessionSizeTier TierOf(long bytes) => bytes switch
    {
        <= InteractiveBytes => SessionSizeTier.Interactive,
        <= StandardBytes => SessionSizeTier.Standard,
        <= LargeBytes => SessionSizeTier.Large,
        _ => SessionSizeTier.Qualification,
    };

    /// <summary>A tier as a person reads it, with its bound and, where it changes what to expect, what does.</summary>
    public static string TierText(SessionSizeTier tier) => tier switch
    {
        SessionSizeTier.Interactive => "T1 interactive, up to 2 GiB",
        SessionSizeTier.Standard => "T2 standard, up to 20 GiB",
        SessionSizeTier.Large => "T3 large, up to 100 GiB, where a full search or a new graph expansion may answer progressively",
        SessionSizeTier.Qualification => "T4, past 100 GiB, which no release is qualified at yet",
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    /// <summary>A session's size: "214.3 MiB in 31 files · 359 B per record · T1 interactive, up to 2 GiB".</summary>
    public static string Describe(SessionSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        return ByteSizeText.Of(size.Bytes) + " in " + CountText.Of(size.Files, "file") + " · "
            + (size.BytesPerRecord is { } each ? ByteSizeText.Of(each) + " per record" : "no record yet")
            + " · " + TierText(size.Tier);
    }

    /// <summary>
    /// A session's size as a sentence, followed by when its capture stops while it records: on one line for a terminal's
    /// progress, or with the stop on a line of its own where a narrow pane wraps it, so it reads at a glance.
    /// </summary>
    public static string Statement(SessionSize size, CaptureHeadroom? headroom, bool stopOnItsOwnLine = false) =>
        Describe(size) + "." + (headroom is null ? string.Empty : (stopOnItsOwnLine ? "\n" : " ") + headroom.Statement);

    /// <summary>
    /// How long a capture that began at <paramref name="began"/> has left at <paramref name="now"/> under
    /// <paramref name="limits"/>. Its length is certain; its journal and the disk are projected at the rate the session grew
    /// between its beginning and the generation that measured <paramref name="size"/>, the session's own bytes counted
    /// against the disk too when <paramref name="volume"/> holds it. Before the session has grown measurably only its
    /// length is stated as a time, and an unread volume is said to be unread.
    /// </summary>
    public static CaptureHeadroom Headroom(SessionSize size, DateTimeOffset began, DateTimeOffset now, CaptureLimits limits,
        RecordingVolume? volume)
    {
        ArgumentNullException.ThrowIfNull(size);
        ArgumentNullException.ThrowIfNull(limits);
        TimeSpan length = Positive(limits.MaximumDuration - (now - began));
        string journal = ByteSizeText.Of(limits.MaximumJournalBytes);
        string reserve = ByteSizeText.Of(limits.MinimumFreeDiskBytes);
        string limit = LimitText(limits.MaximumDuration);
        double grew = (size.Committed - began).TotalSeconds;
        if (grew <= 0 || size.JournalBytes <= 0)
        {
            return new(length, CaptureStopCause.Duration, Measured: false,
                $"Stops in {About(length)}, at its {limit} limit, or sooner if its journal reaches {journal} or free disk its "
                + $"{reserve} reserve; how fast it grows is measured from its first publication.");
        }

        // A rate per second over what was measured. The journal kept growing after the generation measured it; free disk
        // was read just now.
        double journalRate = size.JournalBytes / grew;
        TimeSpan journalLeft = Seconds(((limits.MaximumJournalBytes - size.JournalBytes) / journalRate)
            - Math.Max(0, (now - size.Committed).TotalSeconds));
        TimeSpan? diskLeft = volume is { } measured
            ? Seconds((measured.FreeBytes - limits.MinimumFreeDiskBytes)
                / (journalRate + (measured.HoldsSession ? size.Bytes / grew : 0)))
            : null;

        // Each limit with when it stops the capture; the first stops it, and a tie goes to its length, which is certain.
        var stops = new List<(CaptureStopCause Cause, TimeSpan When)>
        {
            (CaptureStopCause.Duration, length),
            (CaptureStopCause.Journal, journalLeft),
        };
        if (diskLeft is { } disk)
        {
            stops.Add((CaptureStopCause.Disk, disk));
        }

        (CaptureStopCause cause, TimeSpan remaining) = stops.OrderBy(entry => entry.When).ThenBy(entry => entry.Cause).First();
        string head = cause switch
        {
            CaptureStopCause.Journal => $"Stops in {About(remaining)} at the rate so far, when its journal reaches {journal}",
            CaptureStopCause.Disk => $"Stops in {About(remaining)} at the rate so far, when free disk reaches its {reserve} reserve",
            _ => $"Stops in {About(remaining)}, at its {limit} limit",
        };

        // The limits that come later, soonest first, then a volume that could not be read. After a certain stop, the
        // first projection says it is one.
        bool projected = cause != CaptureStopCause.Duration;
        var clauses = new List<string>();
        foreach ((CaptureStopCause later, TimeSpan when) in stops.Where(entry => entry.Cause != cause)
            .OrderBy(entry => entry.When).ThenBy(entry => entry.Cause))
        {
            string prefix = later != CaptureStopCause.Duration && !projected ? "at the rate so far " : string.Empty;
            projected |= later != CaptureStopCause.Duration;
            clauses.Add(prefix + later switch
            {
                CaptureStopCause.Journal => $"its journal would reach {journal} in {About(when)}",
                CaptureStopCause.Disk => $"free disk would reach its {reserve} reserve in {About(when)}",
                _ => $"its {limit} limit is in {About(when)}",
            });
        }

        if (diskLeft is null)
        {
            clauses.Add("free disk could not be read");
        }

        return new(remaining, cause, Measured: true, head + "; " + JoinAnd(clauses) + ".");
    }

    /// <summary>A capture's length as its limit is named: "10-minute", "1-hour", "90-second".</summary>
    private static string LimitText(TimeSpan length)
    {
        long seconds = (long)length.TotalSeconds;
        return seconds % 3_600 == 0 ? string.Create(CultureInfo.CurrentCulture, $"{seconds / 3_600:N0}-hour")
            : seconds % 60 == 0 ? string.Create(CultureInfo.CurrentCulture, $"{seconds / 60:N0}-minute")
            : string.Create(CultureInfo.CurrentCulture, $"{seconds:N0}-second");
    }

    /// <summary>A time ahead as a person reads it: "under a minute", "about 6 min", "about 2 h 10 min", "about 3 days".</summary>
    private static string About(TimeSpan time)
    {
        if (time < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        long minutes = (long)Math.Round(time.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes >= 48 * 60)
        {
            return "about " + CountText.Of((long)Math.Round(time.TotalDays, MidpointRounding.AwayFromZero), "day");
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        return minutes < 60 ? string.Create(culture, $"about {minutes:N0} min")
            : minutes % 60 == 0 ? string.Create(culture, $"about {minutes / 60:N0} h")
            : string.Create(culture, $"about {minutes / 60:N0} h {minutes % 60:N0} min");
    }

    private static string JoinAnd(List<string> clauses) => clauses.Count switch
    {
        0 => string.Empty,
        1 => clauses[0],
        _ => string.Join(", ", clauses.Take(clauses.Count - 1)) + ", and " + clauses[^1],
    };

    private static TimeSpan Positive(TimeSpan time) => time > TimeSpan.Zero ? time : TimeSpan.Zero;

    /// <summary>Seconds ahead, none when already past, and at most a span can hold.</summary>
    private static TimeSpan Seconds(double seconds) =>
        seconds <= 0 ? TimeSpan.Zero : seconds >= TimeSpan.MaxValue.TotalSeconds / 2 ? TimeSpan.MaxValue / 2
            : TimeSpan.FromSeconds(seconds);
}
