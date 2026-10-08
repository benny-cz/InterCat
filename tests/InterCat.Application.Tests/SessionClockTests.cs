using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §6.2's time base: a session's instants read in session time, or on the wall clock its capture's machine read, placed
/// through the capture's clock calibration and stated in a zone with its offset from UTC - one mapping for every instant a
/// view or the command line states (R5, R18).
/// </summary>
public sealed class SessionClockTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo East = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(2), "east", "east");

    /// <summary>A 10 MHz clock whose epoch, session time 0, is its reading 1,000: a native tick is a presentation tick.</summary>
    private static readonly SourceClockDescriptor Clock = new(
        TestSessions.Clock,
        HostId.Derive("session-clock-tests"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        1_000,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    [Fact(DisplayName = "§6.2: a capture's wall clock is placed through its calibration: at its start reading, on the line through its stop reading, at the capture's own rate with a start alone")]
    public void TheWallClockIsPlacedThroughTheCalibration()
    {
        // The calibration read the wall clock 50 µs into the session, 150 µs past noon, and at the stop, 2.5 s in, 25 µs
        // further than the capture's own clock had run: the wall clock ran 10 ppm fast between them.
        using (var calibrated = new TemporarySession())
        {
            Publish(calibrated.Store, [Record(1_100)], clock: Clock, calibration: Calibration(Sample(1_500), Sample(25_001_000, 250)));
            SessionWallClock wall = SessionRecording.WallClock(calibrated.Store.Root, calibrated.Store.Current!, Clock)!;
            Assert.Equal((500L, Noon.AddTicks(1_500), 200L), (wall.AnchorTicks, wall.AnchorUtc, wall.UncertaintyNanoseconds));
            Assert.Equal(10.0, wall.PartsPerMillion!.Value, 3);

            // At the start reading it reads that reading, at the stop reading the stop's, and in between on their line.
            Assert.Equal(Noon.AddTicks(1_500), wall.At(500));
            Assert.Equal(Noon.AddTicks(25_001_250), wall.At(25_000_000));
            Assert.Equal(Noon.AddTicks(12_501_375), wall.At(12_500_250));

            // The overview, the view's snapshot and a reader of the store say the same.
            SessionOverviewBundle overview = SessionOverviewProjector.Project(calibrated.Store);
            Assert.Equal(wall, overview.WallClock);
            Assert.Equal(wall, OverviewWorkspace.From(overview).WallClock);
            Assert.Equal(wall, SessionRecording.WallClock(calibrated.Store));
            calibrated.Store.ReleaseSegmentReaders();
        }

        // A start alone is read at the capture's own clock's rate, which nothing measured.
        using (var started = new TemporarySession())
        {
            Publish(started.Store, [Record(1_100)], clock: Clock, calibration: Calibration(Sample(1_500)));
            SessionWallClock wall = SessionRecording.WallClock(started.Store)!;
            Assert.Null(wall.PartsPerMillion);
            Assert.Equal(Noon.AddTicks(25_001_000), wall.At(25_000_000));
            Assert.Contains("placed from its reading at the capture's start", SessionClock.Wall(wall, East, new TimeRange(0, 1))
                .Basis(Invariant), StringComparison.Ordinal);
            started.Store.ReleaseSegmentReaders();
        }

        // An import recorded none, and a calibration of another clock places nothing on this one's time.
        using var imported = new TemporarySession();
        Publish(imported.Store, [Record(1_100)], clock: Clock);
        Assert.Null(SessionRecording.WallClock(imported.Store));
        Assert.Null(SessionOverviewProjector.Project(imported.Store).WallClock);
        imported.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§6.2: on the wall clock an instant, a range and a record read as times of day in the reader's zone with its offset, to the digits their span needs")]
    public void TheWallClockReadsTimesOfDay()
    {
        SessionClock clock = SessionClock.Wall(new SessionWallClock(0, Noon, null, 200), East, new TimeRange(0, 100_000_000));
        Assert.True(clock.IsWallClock);
        Assert.Equal("wall clock", clock.Name);

        // An axis edge to the digits its span needs: tenths over 10 s, milliseconds, microseconds, then 100 ns, cut and
        // never rounded into the next digit.
        Assert.Equal("14:00:00.1", clock.Instant(1_234_567, 100 * WorkspaceTime.TicksPerSecond, Invariant));
        Assert.Equal("14:00:00.123", clock.Instant(1_234_567, WorkspaceTime.TicksPerSecond, Invariant));
        Assert.Equal("14:00:00.123456", clock.Instant(1_234_567, 10_000, Invariant));
        Assert.Equal("14:00:00.1234567", clock.Instant(1_234_567, 10, Invariant));
        Assert.Equal("14:00:00.9", clock.Instant(9_999_999, 100 * WorkspaceTime.TicksPerSecond, Invariant));

        // A range and a half-open range state their offset once, and a comma-decimal culture parts the bounds by its list
        // separator, as session time's do.
        var range = new TimeRange(1_200_000, 2_500_000);
        Assert.Equal("14:00:00.120 – 14:00:00.250 UTC+02:00", clock.Range(range, Invariant));
        Assert.Equal("[14:00:00.120, 14:00:00.250) UTC+02:00", clock.HalfOpenRange(range, Invariant));
        CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("14:00:00,120 – 14:00:00,250 UTC+02:00", clock.Range(range, german));
        Assert.Equal("[14:00:00,120; 14:00:00,250) UTC+02:00", clock.HalfOpenRange(range, german));

        // A record's time to the microsecond, an instant between two ticks toward the earlier, and one with no time said so;
        // a record stated alone carries its date and offset.
        Assert.Equal("14:00:00.000110", clock.Record(110_000, Invariant));
        Assert.Equal("13:59:59.999999", clock.Record(-50, Invariant));
        Assert.Equal("time unavailable", clock.Record(null, Invariant));
        Assert.Equal("09/29/2026 14:00:00.000110", clock.Record(110_000, Invariant, dated: true));
        Assert.Equal("09/29/2026 14:00:00.000110 UTC+02:00", clock.Moment(110_000, Invariant));
        Assert.Equal("time unavailable", clock.Moment(null, Invariant));

        // The axis names the day it reads and the offset.
        Assert.Equal("wall clock on 09/29/2026, UTC+02:00", clock.Base(range, Noon, Invariant));
        Assert.Equal("UTC+02:00", clock.OffsetsAt([0, 1_000]));
        Assert.Equal("UTC", SessionClock.OffsetText(TimeSpan.Zero));
        Assert.Equal("UTC-05:30", SessionClock.OffsetText(-TimeSpan.FromMinutes(330)));
    }

    [Fact(DisplayName = "§6.2: an axis ticks round instants a ladder width apart, in session time its multiples and on the wall clock the times of day it reads, each named exactly")]
    public void AnAxisTicksRoundInstants()
    {
        // In session time, the multiples of the width strictly inside the span, each named to the width's digits.
        var ticks = new List<(long Ticks, string Label)> { (1, "stale") };
        SessionClock session = SessionClock.Session(East);
        session.AxisTicks(new TimeRange(2_000_000, 59_030_001), 5_000_000, ticks, Invariant);
        Assert.Equal(Enumerable.Range(1, 11).Select(step => (step * 5_000_000L, (step * 0.5m).ToString("N1", Invariant) + " s")), ticks);
        session.AxisTicks(new TimeRange(-25_000_000, 0), 10_000_000, ticks, Invariant);
        Assert.Equal([(-20_000_000L, "-2 s"), (-10_000_000L, "-1 s")], ticks);

        // On the wall clock, the round times of day it reads, placed where it reads them: here at a clock 10 ppm fast, from
        // a reading 0.25 s past noon, ticks every second name 14:00:01 to 14:00:05 in the reader's zone two hours east.
        var wall = new SessionWallClock(0, Noon.AddMilliseconds(250), 10.0, 200);
        SessionClock clock = SessionClock.Wall(wall, East, new TimeRange(0, 60_000_000));
        clock.AxisTicks(new TimeRange(0, 55_000_000), 10_000_000, ticks, Invariant);
        Assert.Equal(["14:00:01", "14:00:02", "14:00:03", "14:00:04", "14:00:05"], ticks.Select(tick => tick.Label));
        Assert.All(ticks.Select((tick, index) => (tick, index)), entry =>
            Assert.InRange((wall.At(entry.tick.Ticks) - Noon.AddSeconds(entry.index + 1)).Ticks, -1, 1));

        // A width under a second names the digits it needs, and none it does not; a clock running fast reads its fourth
        // quarter second just inside the span's one second.
        clock.AxisTicks(new TimeRange(0, 10_000_000), 2_500_000, ticks, Invariant);
        Assert.Equal(["14:00:00.50", "14:00:00.75", "14:00:01.00", "14:00:01.25"], ticks.Select(tick => tick.Label));

        // A round time is named from itself: on a clock 150 ppm fast, the instant 3,705 s from its anchor reads one tick
        // before 13:01:45 UTC when mapped back, so a label read from the instant would say a second too early.
        var fast = new SessionWallClock(0, Noon, 150.0, 200);
        long instant = fast.TicksAt(Noon.AddSeconds(3_705));
        Assert.Equal(Noon.AddSeconds(3_705).AddTicks(-1), fast.At(instant));
        SessionClock.Wall(fast, East, new TimeRange(0, instant + 1)).AxisTicks(new TimeRange(instant - 5_000_000, instant + 5_000_000),
            10_000_000, ticks, Invariant);
        Assert.Equal((instant, "15:01:45"), Assert.Single(ticks));

        // Above ten seconds the ladder's widths are those a clock's minutes and hours make round.
        long second = WorkspaceTime.TicksPerSecond;
        Assert.Equal(new[] { 5 * second, 10 * second, 20 * second, 30 * second, 60 * second, 120 * second, 7_200 * second,
                TimeSpan.TicksPerDay, 2 * TimeSpan.TicksPerDay },
            new[] { 4 * second, 10 * second, 11 * second, 25 * second, 31 * second, 61 * second, 3_601 * second,
                (12 * 3_600 * second) + 1, (TimeSpan.TicksPerDay * 3 / 2) }.Select(WorkspaceTime.LadderWidth));
    }

    [Fact(DisplayName = "§6.2: in session time every instant reads as it always did, and the axis names the moment it counts from")]
    public void SessionTimeReadsAsItDid()
    {
        SessionClock clock = SessionClock.Session(East);
        var range = new TimeRange(1_200_000, 2_500_000);
        Assert.False(clock.IsWallClock);
        Assert.Equal("session time", clock.Name);
        Assert.Equal(WorkspaceTime.FormatInstant(1_234_567, 10_000, Invariant), clock.Instant(1_234_567, 10_000, Invariant));
        Assert.Equal(WorkspaceTime.FormatRange(range, Invariant), clock.Range(range, Invariant));
        Assert.Equal(WorkspaceTime.FormatHalfOpenRange(range, Invariant), clock.HalfOpenRange(range, Invariant));
        Assert.Equal("+1.234568 s", clock.Record(1_234_567_890, Invariant));
        Assert.Equal("−0.500000 s", clock.Moment(-500_000_000, Invariant));
        Assert.Equal("session time since 09/29/2026 14:00:00 UTC+02:00", clock.Base(range, Noon, Invariant));
        Assert.Equal(string.Empty, clock.OffsetsAt([0]));
        Assert.Equal("Session time counts from the moment the capture began.", clock.Basis(Invariant));
    }

    [Fact(DisplayName = "§6.2: a wall-clock range across midnight names both dates, and one across a change of its zone's offset states each end's")]
    public void DaysAndOffsetChangesAreStated()
    {
        // 100 ms before midnight in a zone two hours east.
        DateTimeOffset late = new(2026, 9, 29, 21, 59, 59, 900, TimeSpan.Zero);
        var crossing = new TimeRange(0, 2_000_000);
        SessionClock clock = SessionClock.Wall(new SessionWallClock(0, late, null, 200), East, crossing);
        Assert.Equal("09/29/2026 23:59:59.900 – 09/30/2026 00:00:00.100 UTC+02:00", clock.Range(crossing, Invariant));
        Assert.Equal("wall clock from 09/29/2026 to 09/30/2026, UTC+02:00", clock.Base(crossing, null, Invariant));

        // The session spans the two days, so each record names its date; one ending at midnight does not cross it.
        Assert.Equal("09/29/2026 23:59:59.900000", clock.Record(0, Invariant));
        Assert.Equal("wall clock on 09/29/2026, UTC+02:00", clock.Base(new TimeRange(0, 1_000_000), null, Invariant));
        Assert.Equal("23:59:59.900000",
            SessionClock.Wall(new SessionWallClock(0, late, null, 200), East, new TimeRange(0, 1_000_000)).Record(0, Invariant));

        // Central European time leaves summer time at 03:00 on 25 October 2026: a range across it states each end's offset.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        TimeZoneInfo central = TimeZoneInfo.CreateCustomTimeZone("central", TimeSpan.FromHours(1), "central", "central",
            "central summer", [rule]);
        var change = new TimeRange(0, 20_000_000);
        SessionClock across = SessionClock.Wall(
            new SessionWallClock(0, new DateTimeOffset(2026, 10, 25, 0, 59, 59, TimeSpan.Zero), null, 200), central, change);
        Assert.Equal("02:59:59.000 UTC+02:00 – 02:00:01.000 UTC+01:00", across.Range(change, Invariant));
        Assert.Equal("[02:59:59.000 UTC+02:00, 02:00:01.000) UTC+01:00", across.HalfOpenRange(change, Invariant));
        Assert.Equal("UTC+02:00, then UTC+01:00", across.OffsetsAt([0, 10_000_000, 20_000_000]));
        Assert.Equal("wall clock on 10/25/2026, UTC+02:00 to UTC+01:00", across.Base(change, null, Invariant));
    }

    [Fact(DisplayName = "§6.2: a record's time and its summary state the time base their list reads, and how the wall clock was placed")]
    public void ARecordStatesItsTimeInTheBaseRead()
    {
        ObservationRowV1 send = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 1_024, 100)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_234_567_890 };
        var record = new SessionEvidenceRecord(send.ObservationIdIn(Capture, NormalizerContractVersion.V1), "seg", 0, send);
        SessionClock clock = SessionClock.Wall(new SessionWallClock(0, Noon, 10.0, 200), East, new TimeRange(0, 1));
        Assert.Equal("14:00:01.234580", EvidenceRowText.When(send, clock, Invariant));
        Assert.Equal("+1.234568 s", EvidenceRowText.When(send, Invariant));
        Assert.Equal("09/29/2026 14:00:01.234580 · TCP send · 1,024 B · 127.0.0.1:50000 → 127.0.0.1:8080",
            EvidenceRowText.Summary(record, clock, Invariant, dated: true));
        Assert.Equal("+1.234568 s · TCP send · 1,024 B · 127.0.0.1:50000 → 127.0.0.1:8080", EvidenceRowText.Summary(record, Invariant));
        Assert.Equal("The wall clock the capture's machine read, placed on the line through its readings at the capture's "
            + "start and stop, which ran +10.0 ppm against the capture's own clock, each reading taken within ±200 ns of the "
            + "capture's clock. How right that clock was is not known from it.", clock.Basis(Invariant));
    }

    /// <summary>A UDP send at a native reading of <see cref="Clock"/>, with its session time.</summary>
    private static ObservationRowV1 Record(long nativeTicks) =>
        Transfer(nativeTicks, ObservationKind.Send, AccountingSide.SendSide, 10, 100).Between("192.168.1.5:61000", "8.8.8.8:53") with
        {
            Mechanism = Mechanism.Udp,
            SessionRelativeTicks = (nativeTicks - Clock.CaptureEpochNativeTicks) * 100,
        };

    private static ClockCalibrationV1 Calibration(params ClockCalibrationSampleV1[] samples) => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = Capture.Value,
        ClockId = Clock.Id.Value,
        WallClock = "test-wall-clock",
        Samples = samples,
    };

    /// <summary>The wall clock read noon plus the reading, and <paramref name="fast"/> ticks more.</summary>
    private static ClockCalibrationSampleV1 Sample(long nativeTicks, long fast = 0) => new()
    {
        NativeTicks = nativeTicks,
        Utc = Noon.AddTicks(nativeTicks + fast),
        AcquisitionUncertaintyNanoseconds = 200,
    };
}
