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
/// <param name="RetainedFromNanoseconds">
/// The session time from which it keeps every record, after an interval release
/// (<see cref="SessionRecording.RetainedFromNanoseconds"/>): its bytes hold the session time from there to its newest
/// record. Null when it released no interval, and they hold all of it.
/// </param>
public sealed record SessionSize(long Bytes, int Files, long JournalBytes, long Records, DateTimeOffset Committed,
    long? RetainedFromNanoseconds = null)
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

/// <summary>
/// What stops a recording capture: its length, its journal, and the disk it leaves free. A capture that keeps a window
/// (`ReleaseFollowed`, ADR-048) holds in its journal what <paramref name="Window"/> says rather than all it recorded; null for
/// one that keeps everything until it stops (`StopAtLimit`).
/// </summary>
public sealed record CaptureLimits(TimeSpan MaximumDuration, long MaximumJournalBytes, long MinimumFreeDiskBytes,
    CaptureWindow? Window = null);

/// <summary>
/// What a capture that keeps a window holds (ADR-045, ADR-048): its session keeps the policy's window and grows a quarter
/// past it between releases, and its evidence holds what the session holds and, for up to <see cref="Lag"/> after each
/// release, what the session gave up - until the owner's next lease renewal says so and the broker's next publication
/// releases it.
/// </summary>
public sealed record CaptureWindow
{
    public CaptureWindow(RollingRetentionPolicy policy, TimeSpan lag)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(lag, TimeSpan.Zero);
        Policy = policy;
        Lag = lag;
    }

    /// <summary>The window its session keeps.</summary>
    public RollingRetentionPolicy Policy { get; }

    /// <summary>How long its evidence may go on holding what its session gave up.</summary>
    public TimeSpan Lag { get; }

    /// <summary>The most session time its session holds: the window and the quarter past it.</summary>
    public TimeSpan SessionHolds => Policy.Keep + Policy.Slack;

    /// <summary>The most session time its evidence holds: what its session holds, and the lag.</summary>
    public TimeSpan EvidenceHolds => SessionHolds + Lag;
}

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

    /// <summary>
    /// A session a pin keeps past its window outgrowing what the pin allows, at the rate the session has grown so far: its
    /// follow then stops the capture rather than release the records the pin keeps (ADR-046).
    /// </summary>
    Pin = 4,
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

        return new(bytes, manifest.Dependencies.Count, journal, records, manifest.CommittedUtc,
            SessionRecording.RetainedFromNanoseconds(manifest));
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
    /// What a session retains after an interval release (§12.1 S5, ADR-043): the instant every record from which on is kept,
    /// and what went before it - when, how many records, how many of their rows were kept as the evidence of what came
    /// after, and why. Null when the generation states no interval release.
    /// </summary>
    public static string? Retained(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.LatestRelease(RetentionExtentKind.Interval) is not { Record: { Interval: { } interval } record })
        {
            return null;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        return string.Create(culture, $"Kept from {SessionTimeText.Seconds(interval.BoundaryNanoseconds, culture)} on: the records read before it were released ")
            + string.Create(CultureInfo.InvariantCulture, $"on {record.ReleasedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC")
            + " - " + CountText.Of(record.ReleasedRecords, "record")
            + (interval.KeptRows > 0
                ? ", keeping " + CountText.Of(interval.KeptRows, "row") + " of them as the evidence of what came after"
                : string.Empty)
            + " (" + record.Reason + ").";
    }

    /// <summary>
    /// A session's size as a sentence, followed by when its capture stops while it records: on one line for a terminal's
    /// progress, or with the stop on a line of its own where a narrow pane wraps it, so it reads at a glance.
    /// </summary>
    public static string Statement(SessionSize size, CaptureHeadroom? headroom, bool stopOnItsOwnLine = false) =>
        Describe(size) + "." + (headroom is null ? string.Empty : (stopOnItsOwnLine ? "\n" : " ") + headroom.Statement);

    /// <summary>
    /// How long a capture that began at <paramref name="began"/> has left at <paramref name="now"/> under
    /// <paramref name="limits"/>. Its length is certain. Its journal and the disk are projected at the rate the session
    /// grew over the session time the generation that measured <paramref name="size"/> holds, the session's own bytes
    /// counted against the disk too when <paramref name="volume"/> holds it: a capture that keeps everything grows its
    /// journal by all it records, and one that keeps a window holds no more than the window, which stops it only if it
    /// outgrows them. A pin among <paramref name="pins"/> keeps that session past its window once the window passes the
    /// pin's moment, so it grows from there until it holds more than a pin allows, which stops it too (ADR-046). Before
    /// the session has grown measurably only its length is stated as a time, and an unread volume is said to be unread.
    /// </summary>
    public static CaptureHeadroom Headroom(SessionSize size, DateTimeOffset began, DateTimeOffset now, CaptureLimits limits,
        RecordingVolume? volume, IReadOnlyList<RetentionPin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(size);
        ArgumentNullException.ThrowIfNull(limits);
        pins ??= [];
        TimeSpan length = Positive(limits.MaximumDuration - (now - began));
        string journal = ByteSizeText.Of(limits.MaximumJournalBytes);
        string reserve = ByteSizeText.Of(limits.MinimumFreeDiskBytes);
        string limit = LimitText(limits.MaximumDuration);
        CaptureWindow? window = limits.Window;

        // In seconds of session time: the generation holds the records read from `from` up to `reached`, when it was
        // published.
        double reached = (size.Committed - began).TotalSeconds;
        double from = Math.Max(0, (size.RetainedFromNanoseconds ?? 0) / 1e9);
        double held = reached - from;
        if (held <= 0 || size.JournalBytes <= 0)
        {
            return new(length, CaptureStopCause.Duration, Measured: false,
                $"Stops in {About(length)}, at its {limit} limit, or sooner if "
                + (window is null
                    ? $"its journal reaches {journal} or free disk its {reserve} reserve"
                    : $"keeping {window.Policy.Window} takes its journal to {journal} or free disk to its {reserve} reserve")
                + "; how fast it grows is measured from its first publication.");
        }

        // Rates per second of session time over what the generation holds. The journal kept growing after the generation
        // measured it, so a moment ahead in session time is that much nearer; free disk was read just now.
        double journalRate = size.JournalBytes / held;
        double sessionRate = size.Bytes / held;
        bool besideSession = volume is { HoldsSession: true };
        double diskRate = journalRate + (besideSession ? sessionRate : 0);
        double spare = volume is { } measured ? measured.FreeBytes - (double)limits.MinimumFreeDiskBytes : 0;
        double since = Math.Max(0, (now - size.Committed).TotalSeconds);
        TimeSpan At(double sessionSeconds) => Seconds(sessionSeconds - reached - since);

        TimeSpan? journalLeft;
        TimeSpan? diskLeft = null;
        double? windowBytes = null;
        (RetentionPin Pin, TimeSpan When)? outgrown = null;
        if (window is null)
        {
            // Its journal keeps everything it records until it stops.
            journalLeft = At(limits.MaximumJournalBytes / journalRate);
            diskLeft = volume is null ? null : Seconds(spare / diskRate);
        }
        else
        {
            // Its evidence holds no more than the window, the quarter its session grows past it, and what the broker has
            // yet to release; its session the window and the quarter. Its journal and the disk stop it while it grows to
            // that, if the window outgrows them, and never otherwise.
            double peak = journalRate * window.EvidenceHolds.TotalSeconds;
            double needed = Math.Max(0, peak - size.JournalBytes)
                + (besideSession ? Math.Max(0, (sessionRate * window.SessionHolds.TotalSeconds) - size.Bytes) : 0);
            bool journalOutgrown = peak >= limits.MaximumJournalBytes;
            bool diskOutgrown = volume is not null && spare < needed;
            windowBytes = peak;
            journalLeft = journalOutgrown ? Seconds(((limits.MaximumJournalBytes - size.JournalBytes) / journalRate) - since) : null;
            diskLeft = diskOutgrown ? Seconds(spare / diskRate) : null;

            // A pin keeps the session past its window from when the window passes the earliest pin's moment: from then on
            // it grows from there - or from where it keeps every record, if that is later - as a capture that keeps
            // everything does, and its follow stops the capture at a release step that finds the session holding more
            // than a pin keeping it allows (`RollingRetention`).
            if (pins.Count > 0)
            {
                double keep = window.Policy.Keep.TotalSeconds;
                double slack = window.Policy.Slack.TotalSeconds;
                double earliest = pins.Min(pin => pin.FromNanoseconds) / 1e9;
                double grows = Math.Max(from, earliest);
                journalLeft ??= At(grows + (limits.MaximumJournalBytes / journalRate));

                // Free disk falls as it grows from there, once what it holds from before is released.
                if (volume is not null && !diskOutgrown)
                {
                    diskLeft = Seconds(grows - from + (spare / diskRate));
                }

                // A release step runs each time the session holds more than its window and the quarter, releasing a quarter,
                // so the one that first finds the earliest pin keeping the session comes a whole number of quarters after
                // the next; from then on one runs whenever it holds more than that from where the pin keeps it. A step
                // checks the pins keeping the session: those whose moment the window has passed.
                double first = from + keep + slack + (Math.Max(0, Math.Floor((earliest - from) / slack)) * slack);
                outgrown = pins
                    .Select(pin =>
                    {
                        double moment = pin.FromNanoseconds / 1e9;
                        double stops = moment + keep < first && sessionRate * (first - grows) > pin.AllowanceBytes
                            ? first
                            : new[] { grows + keep + slack, moment + keep, grows + (pin.AllowanceBytes / sessionRate) }.Max();
                        return (pin, At(stops));
                    })
                    .MinBy(stop => stop.Item2);
            }
        }

        // Each limit with when it stops the capture; the first stops it, and a tie goes to its length, which is certain.
        var stops = new List<(CaptureStopCause Cause, TimeSpan When)> { (CaptureStopCause.Duration, length) };
        if (journalLeft is { } journalWhen)
        {
            stops.Add((CaptureStopCause.Journal, journalWhen));
        }

        if (diskLeft is { } diskWhen)
        {
            stops.Add((CaptureStopCause.Disk, diskWhen));
        }

        if (outgrown is { } pinned)
        {
            stops.Add((CaptureStopCause.Pin, pinned.When));
        }

        (CaptureStopCause cause, TimeSpan remaining) = stops.OrderBy(entry => entry.When).ThenBy(entry => entry.Cause).First();
        string allows = outgrown is { Pin: var pin }
            ? $"the {ByteSizeText.Of(pin.AllowanceBytes)} the pin from "
                + $"{SessionTimeText.Seconds(pin.FromNanoseconds, CultureInfo.CurrentCulture)} allows"
            : string.Empty;
        string head = cause switch
        {
            CaptureStopCause.Journal => $"Stops in {About(remaining)} at the rate so far, when its journal reaches {journal}",
            CaptureStopCause.Disk => $"Stops in {About(remaining)} at the rate so far, when free disk reaches its {reserve} reserve",
            CaptureStopCause.Pin => $"Stops in {About(remaining)} at the rate so far, when the session outgrows {allows}",
            _ => $"Stops in {About(remaining)}, at its {limit} limit",
        };

        // What keeping its window takes, unless a pin keeps the session past it, and when it begins releasing; the limits
        // that come later, soonest first; free disk when the window never takes it to its reserve, or a volume that could
        // not be read. After a certain stop, the first projection says it is one.
        bool projected = cause != CaptureStopCause.Duration;
        var clauses = new List<string>();
        void Say(string clause, bool projection)
        {
            clauses.Add((projection && !projected ? "at the rate so far " : string.Empty) + clause);
            projected |= projection;
        }

        if (window is not null && windowBytes is { } bytes && (pins.Count == 0 || bytes >= limits.MaximumJournalBytes))
        {
            Say(bytes < limits.MaximumJournalBytes
                ? $"keeping {window.Policy.Window} takes up to about {ByteSizeText.Of(Bytes(bytes))} of its {journal} journal"
                : $"keeping {window.Policy.Window} would take up to about {ByteSizeText.Of(Bytes(bytes))}", projection: true);

            // Where retention begins releasing, before it stops: once the session holds its window and the quarter past it
            // from its first record, which lies at about its beginning.
            if (pins.Count == 0 && size.RetainedFromNanoseconds is null
                && At(window.SessionHolds.TotalSeconds) is var releasing && releasing < remaining)
            {
                Say($"it begins releasing its oldest records in {About(releasing)}", projection: false);
            }
        }

        foreach ((CaptureStopCause later, TimeSpan when) in stops.Where(entry => entry.Cause != cause)
            .OrderBy(entry => entry.When).ThenBy(entry => entry.Cause))
        {
            Say(later switch
            {
                CaptureStopCause.Journal => $"its journal would reach {journal} in {About(when)}",
                CaptureStopCause.Disk => $"free disk would reach its {reserve} reserve in {About(when)}",
                CaptureStopCause.Pin => $"the session would outgrow {allows} in {About(when)}",
                _ => $"its {limit} limit is in {About(when)}",
            }, projection: later != CaptureStopCause.Duration);
        }

        if (volume is null)
        {
            clauses.Add("free disk could not be read");
        }
        else if (diskLeft is null)
        {
            Say($"free disk stays above its {reserve} reserve", projection: true);
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

    /// <summary>A projected size in whole bytes, at most a size can hold.</summary>
    private static long Bytes(double bytes) => bytes >= long.MaxValue / 2d ? long.MaxValue / 2 : (long)Math.Ceiling(bytes);

    /// <summary>Seconds ahead, none when already past, and at most a span can hold.</summary>
    private static TimeSpan Seconds(double seconds) =>
        seconds <= 0 ? TimeSpan.Zero : seconds >= TimeSpan.MaxValue.TotalSeconds / 2 ? TimeSpan.MaxValue / 2
            : TimeSpan.FromSeconds(seconds);
}
