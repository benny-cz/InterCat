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
