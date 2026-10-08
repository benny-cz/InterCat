using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §12.1 S5: growth is disclosed before it hurts. A session's size is what its generation measured of every file it names,
/// its tier follows from that, and a recording capture's time left is its first limit's: its length, or its journal or
/// free disk at the rate the session has grown.
/// </summary>
public sealed class SessionGrowthTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;
    private static readonly DateTimeOffset Began = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly CaptureLimits Explore = new(TimeSpan.FromMinutes(10), GiB, GiB);

    // A day-long capture keeping its last ten minutes, its evidence released 12 s after its session gives chunks up.
    private static readonly CaptureLimits Rolling = new(TimeSpan.FromHours(24), GiB, GiB,
        new CaptureWindow(new RollingRetentionPolicy(TimeSpan.FromMinutes(10)), TimeSpan.FromSeconds(12)));

    [Fact(DisplayName = "§12.1: a session's size is every file its generation names as measured, with its bytes per record and tier")]
    public void ASessionsSizeIsEveryFileItsGenerationNames()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 100, 11) with { SessionRelativeTicks = 2_000 },
        ]);
        session.Store.ReleaseSegmentReaders();
        using EvidenceLease lease = session.Store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;

        // The bytes are the files' own lengths on disk, every one the generation names, and the journal's are its chunks'.
        SessionSize size = SessionGrowth.Measure(manifest, 3);
        long onDisk = manifest.Dependencies.Sum(dependency => new FileInfo(Path.Combine(session.Path, dependency.Name)).Length);
        Assert.Equal((onDisk, manifest.Dependencies.Count, 3L, manifest.CommittedUtc),
            (size.Bytes, size.Files, size.Records, size.Committed));
        Assert.Equal(manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .Sum(dependency => new FileInfo(Path.Combine(session.Path, dependency.Name)).Length), size.JournalBytes);
        Assert.Equal((long)Math.Round(onDisk / 3d, MidpointRounding.AwayFromZero), size.BytesPerRecord);
        Assert.Equal(SessionSizeTier.Interactive, size.Tier);
        Assert.Null(size.RetainedFromNanoseconds);

        // The projected overview carries the same measurement of the generation it projected.
        Assert.Equal(SessionGrowth.Measure(manifest, 3), SessionOverviewProjector.Project(session.Store).Size);

        // In words: the size, the files, the bytes each record takes, and the tier.
        InCulture("en-US", () => Assert.Equal(
            $"{ByteSizeText.Of(onDisk)} in {manifest.Dependencies.Count} files · {ByteSizeText.Of(size.BytesPerRecord!.Value)} per "
            + "record · T1 interactive, up to 2 GiB", SessionGrowth.Describe(size)));

        // A session that holds no record yet says so rather than dividing by none.
        SessionSize empty = SessionGrowth.Measure(manifest, 0);
        Assert.Null(empty.BytesPerRecord);
        Assert.Contains(" · no record yet · ", SessionGrowth.Describe(empty), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionGrowth.Measure(manifest, -1));
    }

    [Fact(DisplayName = "§12.1: a session crosses into the next tier past 2, 20 and 100 GiB on disk, and the words say which")]
    public void ASessionCrossesATierPastItsBound()
    {
        Assert.Equal(
            [
                SessionSizeTier.Interactive, SessionSizeTier.Interactive, SessionSizeTier.Standard, SessionSizeTier.Standard,
                SessionSizeTier.Large, SessionSizeTier.Large, SessionSizeTier.Qualification,
            ],
            new[] { 0, 2 * GiB, (2 * GiB) + 1, 20 * GiB, (20 * GiB) + 1, 100 * GiB, (100 * GiB) + 1 }.Select(SessionGrowth.TierOf));
        Assert.Equal(
            [
                "T1 interactive, up to 2 GiB",
                "T2 standard, up to 20 GiB",
                "T3 large, up to 100 GiB, where a full search or a new graph expansion may answer progressively",
                "T4, past 100 GiB, which no release is qualified at yet",
            ],
            Enum.GetValues<SessionSizeTier>().Select(SessionGrowth.TierText));
        Assert.Equal(SessionSizeTier.Standard, new SessionSize((2 * GiB) + 1, 9, GiB, 1_000, Began).Tier);
    }

    [Fact(DisplayName = "§12.1: a recording capture states when its first limit stops it - its length, its journal or free disk - and the rest after")]
    public void ARecordingCaptureStatesWhenItStops() => InCulture("en-US", () =>
    {
        // 100 MiB of journal in its first 100 s grows it 1 MiB a second, and the session 1.5 MiB a second beside it.
        DateTimeOffset now = Began.AddSeconds(100);
        var size = new SessionSize(150 * MiB, 30, 100 * MiB, 400_000, now);
        CaptureHeadroom length = SessionGrowth.Headroom(size, Began, now, Explore, new RecordingVolume(100 * GiB, HoldsSession: true));
        Assert.Equal((TimeSpan.FromSeconds(500), CaptureStopCause.Duration, true), (length.Remaining, length.Cause, length.Measured));
        Assert.Equal("Stops in about 8 min, at its 10-minute limit; at the rate so far its journal would reach 1 GiB in about "
            + "15 min, and free disk would reach its 1 GiB reserve in about 11 h 16 min.", length.Statement);

        // Six times as fast, its journal comes first; the disk, with the session growing beside the evidence, after.
        var fast = new SessionSize(900 * MiB, 30, 600 * MiB, 2_400_000, now);
        CaptureHeadroom journal = SessionGrowth.Headroom(fast, Began, now, Explore, new RecordingVolume(100 * GiB, HoldsSession: true));
        Assert.Equal((CaptureStopCause.Journal, 424 / 6d), (journal.Cause, journal.Remaining.TotalSeconds), new Near());
        Assert.Equal("Stops in about 1 min at the rate so far, when its journal reaches 1 GiB; its 10-minute limit is in about "
            + "8 min, and free disk would reach its 1 GiB reserve in about 1 h 53 min.", journal.Statement);

        // A nearly full volume comes first, counting only the evidence's growth when the session is on another.
        CaptureHeadroom disk = SessionGrowth.Headroom(size, Began, now, Explore,
            new RecordingVolume(GiB + (205 * MiB), HoldsSession: false));
        Assert.Equal((CaptureStopCause.Disk, 205d), (disk.Cause, disk.Remaining.TotalSeconds), new Near());
        Assert.Equal("Stops in about 3 min at the rate so far, when free disk reaches its 1 GiB reserve; its 10-minute limit "
            + "is in about 8 min, and its journal would reach 1 GiB in about 15 min.", disk.Statement);

        // A volume that could not be read is said to be, and a far limit reads in days.
        CaptureHeadroom unread = SessionGrowth.Headroom(size, Began, now, new(TimeSpan.FromHours(24), 1L << 40, GiB), null);
        Assert.Equal("Stops in about 23 h 58 min, at its 24-hour limit; at the rate so far its journal would reach 1 TiB in "
            + "about 12 days, and free disk could not be read.", unread.Statement);

        // The journal kept growing after the generation measured it: a minute later it has a minute less.
        CaptureHeadroom later = SessionGrowth.Headroom(size, Began, now.AddMinutes(1), Explore, null);
        Assert.Equal(TimeSpan.FromSeconds(440), later.Remaining);
        Assert.Contains("its journal would reach 1 GiB in about 14 min", later.Statement, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§12.1: before a session has grown measurably only its length is a time, and a passed limit is under a minute away")]
    public void BeforeASessionGrowsOnlyItsLengthIsATime() => InCulture("en-US", () =>
    {
        // Nothing journaled yet, or measured at the moment it began: no rate, so the journal and disk are named, not timed.
        foreach (SessionSize unmeasured in new[]
        {
            new SessionSize(4_096, 3, 0, 0, Began.AddSeconds(30)),
            new SessionSize(4_096, 3, 2_048, 10, Began),
        })
        {
            CaptureHeadroom first = SessionGrowth.Headroom(unmeasured, Began, Began.AddSeconds(30), Explore,
                new RecordingVolume(100 * GiB, HoldsSession: true));
            Assert.Equal((TimeSpan.FromSeconds(570), CaptureStopCause.Duration, false), (first.Remaining, first.Cause, first.Measured));
            Assert.Equal("Stops in about 10 min, at its 10-minute limit, or sooner if its journal reaches 1 GiB or free disk "
                + "its 1 GiB reserve; how fast it grows is measured from its first publication.", first.Statement);
        }

        // Past its length, it is stopping: none left, which the window states as imminent.
        var size = new SessionSize(150 * MiB, 30, 100 * MiB, 400_000, Began.AddSeconds(100));
        CaptureHeadroom over = SessionGrowth.Headroom(size, Began, Began.AddMinutes(11), Explore, null);
        Assert.Equal((TimeSpan.Zero, CaptureStopCause.Duration), (over.Remaining, over.Cause));
        Assert.StartsWith("Stops in under a minute, at its 10-minute limit; ", over.Statement, StringComparison.Ordinal);
        Assert.True(over.Remaining <= SessionGrowth.Imminent);

        // The statement the window and the command line print: the size, then when it stops.
        Assert.Equal(SessionGrowth.Describe(size) + ". " + over.Statement, SessionGrowth.Statement(size, over));
        Assert.Equal(SessionGrowth.Describe(size) + ".\n" + over.Statement, SessionGrowth.Statement(size, over, stopOnItsOwnLine: true));
        Assert.Equal(SessionGrowth.Describe(size) + ".", SessionGrowth.Statement(size, null));
    });

    [Fact(DisplayName = "S5: a capture keeping a window projects its journal and free disk from what the window holds, so they stop it only if the window outgrows them")]
    public void ACaptureKeepingAWindowStopsOnlyIfTheWindowOutgrowsItsLimits() => InCulture("en-US", () =>
    {
        // An hour in, its session keeps the 700 s from 2,900 s on in 70 MiB of journal and 105 MiB in all: 0.1 MiB and
        // 0.15 MiB a second of session time.
        DateTimeOffset now = Began.AddSeconds(3_600);
        var size = new SessionSize(105 * MiB, 30, 70 * MiB, 280_000, now, RetainedFromNanoseconds: 2_900_000_000_000);
        var roomy = new RecordingVolume(100 * GiB, HoldsSession: true);

        // Its evidence holds no more than the window, the quarter past it and the 12 s before the broker releases: 762 s,
        // 76.2 MiB. That fits its journal and the disk, so only its length stops it.
        CaptureHeadroom fits = SessionGrowth.Headroom(size, Began, now, Rolling, roomy);
        Assert.Equal((TimeSpan.FromHours(23), CaptureStopCause.Duration, true), (fits.Remaining, fits.Cause, fits.Measured));
        Assert.Equal("Stops in about 23 h, at its 24-hour limit; at the rate so far keeping the last 10 minutes takes up to "
            + "about 76.2 MiB of its 1 GiB journal, and free disk stays above its 1 GiB reserve.", fits.Statement);
        Assert.Equal("Stops in about 23 h, at its 24-hour limit; at the rate so far keeping the last 10 minutes takes up to "
            + "about 76.2 MiB of its 1 GiB journal, and free disk could not be read.",
            SessionGrowth.Headroom(size, Began, now, Rolling, null).Statement);

        // The same session under limits that keep everything: its journal holds all it recorded, 0.1 MiB a second over the
        // hour, measured over what the session holds rather than the hour its released records spanned.
        Assert.Equal("Stops in about 1 h 51 min at the rate so far, when its journal reaches 1 GiB; its 24-hour limit is in "
            + "about 23 h, and free disk would reach its 1 GiB reserve in about 5 days.",
            SessionGrowth.Headroom(size, Began, now, Rolling with { Window = null }, roomy).Statement);

        // Five minutes in at 2 MiB a second, the window would take 1.5 GiB: its journal stops it first, while it grows.
        DateTimeOffset early = Began.AddSeconds(300);
        CaptureHeadroom outgrown = SessionGrowth.Headroom(new SessionSize(900 * MiB, 30, 600 * MiB, 2_400_000, early), Began,
            early, Rolling, roomy);
        Assert.Equal((CaptureStopCause.Journal, 212d), (outgrown.Cause, outgrown.Remaining.TotalSeconds), new Near());
        Assert.Equal("Stops in about 4 min at the rate so far, when its journal reaches 1 GiB; keeping the last 10 minutes would "
            + "take up to about 1.5 GiB, its 24-hour limit is in about 23 h 55 min, and free disk stays above its 1 GiB reserve.",
            outgrown.Statement);

        // 140 s in, filling the window takes 153.7 MiB more of a volume with 100 MiB above its reserve when the session
        // grows on it too, and 62.2 MiB when only the evidence does. Its session first releases once it holds 750 s, in
        // about 10 min: said when that comes before it stops.
        DateTimeOffset filling = Began.AddSeconds(140);
        var young = new SessionSize(21 * MiB, 30, 14 * MiB, 56_000, filling);
        CaptureHeadroom disk = SessionGrowth.Headroom(young, Began, filling, Rolling,
            new RecordingVolume(GiB + (100 * MiB), HoldsSession: true));
        Assert.Equal((CaptureStopCause.Disk, 400d), (disk.Cause, disk.Remaining.TotalSeconds), new Near());
        Assert.Equal("Stops in about 7 min at the rate so far, when free disk reaches its 1 GiB reserve; keeping the last 10 "
            + "minutes takes up to about 76.2 MiB of its 1 GiB journal, and its 24-hour limit is in about 23 h 58 min.",
            disk.Statement);
        Assert.Equal("Stops in about 23 h 58 min, at its 24-hour limit; at the rate so far keeping the last 10 minutes takes up "
            + "to about 76.2 MiB of its 1 GiB journal, it begins releasing its oldest records in about 10 min, and free disk "
            + "stays above its 1 GiB reserve.",
            SessionGrowth.Headroom(young, Began, filling, Rolling, new RecordingVolume(GiB + (100 * MiB), HoldsSession: false))
                .Statement);

        // Before it has grown measurably, it names what keeping the window must fit.
        Assert.Equal("Stops in about 24 h, at its 24-hour limit, or sooner if keeping the last 10 minutes takes its journal to "
            + "1 GiB or free disk to its 1 GiB reserve; how fast it grows is measured from its first publication.",
            SessionGrowth.Headroom(new SessionSize(4_096, 3, 0, 0, Began.AddSeconds(30)), Began, Began.AddSeconds(30), Rolling,
                roomy).Statement);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureWindow(new RollingRetentionPolicy(TimeSpan.FromMinutes(10)), TimeSpan.FromSeconds(-1)));
    });

    [Fact(DisplayName = "S5: a pin keeping a rolling capture's session past its window projects when the session outgrows what the pin allows, its journal and free disk growing from the pin's moment")]
    public void APinKeepingARollingSessionProjectsItsStop() => InCulture("en-US", () =>
    {
        // As above: an hour in, 0.1 MiB of journal and 0.15 MiB in all a second, kept from 2,900 s; its follow's next release
        // step comes once the session holds more than 750 s, and then every 150 s.
        DateTimeOffset now = Began.AddSeconds(3_600);
        var size = new SessionSize(105 * MiB, 30, 70 * MiB, 280_000, now, RetainedFromNanoseconds: 2_900_000_000_000);
        var roomy = new RecordingVolume(100 * GiB, HoldsSession: true);
        string at = SessionTimeText.Seconds(3_500_000_000_000, CultureInfo.CurrentCulture);

        // Pinned from 3,500 s allowing 210 MiB: the window passes the pin at 4,100 s, and from then on the session grows from
        // 3,500 s, holding 210 MiB at 4,900 s - so the follow stops it in about 22 min. Its journal reaches 1 GiB, grown from
        // the pin's moment, at 13,740 s; free disk, once what it holds before the pin is released, in about 5 days.
        CaptureHeadroom pinned = SessionGrowth.Headroom(size, Began, now, Rolling, roomy, [Pin(3_500, 210)]);
        Assert.Equal((CaptureStopCause.Pin, 1_300d), (pinned.Cause, pinned.Remaining.TotalSeconds), new Near());
        Assert.Equal($"Stops in about 22 min at the rate so far, when the session outgrows the 210 MiB the pin from {at} "
            + "allows; its journal would reach 1 GiB in about 2 h 49 min, its 24-hour limit is in about 23 h, and free disk would "
            + "reach its 1 GiB reserve in about 5 days.", pinned.Statement);

        // A later pin allowing less stops it first: the session holds 120 MiB at 4,300 s.
        CaptureHeadroom least = SessionGrowth.Headroom(size, Began, now, Rolling, roomy, [Pin(3_500, 210), Pin(3_550, 120)]);
        Assert.Equal((CaptureStopCause.Pin, 700d), (least.Cause, least.Remaining.TotalSeconds), new Near());
        Assert.StartsWith("Stops in about 12 min at the rate so far, when the session outgrows the 120 MiB the pin from "
            + SessionTimeText.Seconds(3_550_000_000_000, CultureInfo.CurrentCulture) + " allows; ", least.Statement,
            StringComparison.Ordinal);

        // A pin keeps the session only once the window passes its moment: one from 3,590 s allowing 100 MiB stops it when
        // the window passes it at 4,190 s, though the session, kept from 3,000 s by another pin, holds more from 3,667 s.
        CaptureHeadroom later = SessionGrowth.Headroom(size, Began, now, Rolling, roomy, [Pin(3_000, 10 * 1_024), Pin(3_590, 100)]);
        Assert.Equal((CaptureStopCause.Pin, 590d), (later.Cause, later.Remaining.TotalSeconds), new Near());

        // No step checks between two: a pin the window passes at 4,120 s, allowing 100 MiB, which the session holds more
        // than by then, stops it only at the step at 4,150 s, once the session holds more than its window and the quarter
        // from 3,400 s, where an earlier pin keeps it.
        CaptureHeadroom idle = SessionGrowth.Headroom(size, Began, now, Rolling, roomy, [Pin(3_400, 10 * 1_024), Pin(3_520, 100)]);
        Assert.Equal((CaptureStopCause.Pin, 550d), (idle.Cause, idle.Remaining.TotalSeconds), new Near());

        // Pinned 20 s ago allowing what the session holds: the release step at 4,200 s first finds the pin keeping it, by
        // then holding 93 MiB, so it stops there rather than at the next step once the session holds 750 s.
        var released = new SessionSize(90 * MiB, 30, 60 * MiB, 240_000, now, RetainedFromNanoseconds: 3_000_000_000_000);
        CaptureHeadroom first = SessionGrowth.Headroom(released, Began, now, Rolling, roomy, [Pin(3_580, 90)]);
        Assert.Equal((CaptureStopCause.Pin, 600d), (first.Cause, first.Remaining.TotalSeconds), new Near());

        // A pin from before what the session keeps holds it already, and the next step finds it outgrown.
        CaptureHeadroom over = SessionGrowth.Headroom(size, Began, now, Rolling, roomy, [Pin(2_000, 50)]);
        Assert.Equal((CaptureStopCause.Pin, 50d), (over.Cause, over.Remaining.TotalSeconds), new Near());
        Assert.StartsWith("Stops in under a minute at the rate so far, when the session outgrows the 50 MiB ", over.Statement,
            StringComparison.Ordinal);

        // A window that outgrows its journal says so beside the pin, and the disk falls once the session grows from the pin.
        DateTimeOffset early = Began.AddSeconds(300);
        CaptureHeadroom fast = SessionGrowth.Headroom(new SessionSize(900 * MiB, 30, 600 * MiB, 2_400_000, early), Began, early,
            Rolling, roomy, [Pin(250, 10 * 1_024)]);
        Assert.Equal("Stops in about 4 min at the rate so far, when its journal reaches 1 GiB; keeping the last 10 minutes would "
            + "take up to about 1.5 GiB, the session would outgrow the 10 GiB the pin from "
            + SessionTimeText.Seconds(250_000_000_000, CultureInfo.CurrentCulture) + " allows in about 56 min, free disk would "
            + "reach its 1 GiB reserve in about 5 h 42 min, and its 24-hour limit is in about 23 h 55 min.", fast.Statement);

        // A capture that keeps everything stops for no pin: a pin only holds its session's releases back.
        Assert.Equal(SessionGrowth.Headroom(size, Began, now, Rolling with { Window = null }, roomy).Statement,
            SessionGrowth.Headroom(size, Began, now, Rolling with { Window = null }, roomy, [Pin(3_500, 210)]).Statement);
    });

    private static RetentionPin Pin(long fromSeconds, long allowanceMiB) => new()
    {
        Id = Guid.NewGuid(),
        FromNanoseconds = fromSeconds * 1_000_000_000,
        AllowanceBytes = allowanceMiB * MiB,
        PlacedUtc = Began,
        Reason = "the failover",
    };

    private static void InCulture(string name, Action check)
    {
        CultureInfo shown = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            check();
        }
        finally
        {
            CultureInfo.CurrentCulture = shown;
        }
    }

    /// <summary>A cause and a time in seconds, the time equal to a hundredth of a second.</summary>
    private sealed class Near : IEqualityComparer<(CaptureStopCause, double)>
    {
        public bool Equals((CaptureStopCause, double) x, (CaptureStopCause, double) y) =>
            x.Item1 == y.Item1 && Math.Abs(x.Item2 - y.Item2) < 0.01;

        public int GetHashCode((CaptureStopCause, double) obj) => obj.Item1.GetHashCode();
    }
}
