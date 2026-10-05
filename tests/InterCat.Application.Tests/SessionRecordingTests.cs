using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>A session's recording (metrics-v1 §7): the whole interval a whole session's rates divide by.</summary>
public sealed class SessionRecordingTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A 10 MHz clock whose epoch, session time 0, is its reading 1,000: a native tick is a presentation tick.</summary>
    private static readonly SourceClockDescriptor Clock = new(
        TestSessions.Clock,
        HostId.Derive("recording-tests"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        1_000,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    [Fact(DisplayName = "R3: a session's recording runs from its capture's epoch to the stop its clock calibration records, and a whole-session rate divides by it")]
    public void ARecordingRunsFromTheEpochToTheStop()
    {
        // Ten records 100 µs to 109 µs into a capture whose calibration read its clock 50 µs in and at its stop, 2.5 s in.
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(0, 10).Select(index => Record(1_000 + index))], clock: Clock,
            calibration: Calibration(1_500, 25_001_000));
        SessionManifestV1 manifest = session.Store.Current!;

        var recording = new TimeRange(0, 25_000_000);
        Assert.Equal(recording, SessionOverviewProjector.Project(session.Store).Recording);
        Assert.Equal(recording, SessionRecording.Interval(session.Store.Root, manifest, Clock, new TimeRange(1_000, 1_010)));
        Assert.Equal(recording, SessionRecording.Interval(session.Store.Root, manifest, Clock, records: null));

        // Natively it is the epoch reading to the stop reading, which icat metric names as a whole session's rate's interval:
        // ten records over 2.5 s, none left out.
        TimeRange native = SessionRecording.NativeInterval(session.Store, manifest, Clock)!.Value;
        Assert.Equal(new TimeRange(1_000, 25_001_000), native);
        MetricResult rate = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Rate,
            RateNumerator = Metric.Observations,
            Interval = native,
        });
        Assert.Equal(10, rate.Rate!.Numerator);
        Assert.Equal(4m, rate.Rate.PerSecond);
        Assert.Equal(0, rate.ExcludedOutsideInterval);
    }

    [Fact(DisplayName = "R3: a record before the epoch or after the stop widens the recording to hold it, and one with no session time widens nothing")]
    public void RecordsBeyondTheReadingsWidenTheRecording()
    {
        // 300 ns before the epoch, 3 s in, past the 2.5 s stop, and one whose reading is not on the session's time at all.
        using var session = new TemporarySession();
        Publish(session.Store, [Record(997), Record(1_500), Record(30_001_000), Record(900_000_000) with { SessionRelativeTicks = null }],
            clock: Clock, calibration: Calibration(1_500, 25_001_000));
        SessionManifestV1 manifest = session.Store.Current!;

        Assert.Equal(new TimeRange(-3, 30_000_001), SessionOverviewProjector.Project(session.Store).Recording);
        TimeRange native = SessionRecording.NativeInterval(session.Store, manifest, Clock)!.Value;
        Assert.Equal(new TimeRange(997, 30_001_001), native);

        // The rate counts every record with a session time; the one without is outside the recording, and said to be.
        MetricResult rate = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Rate,
            RateNumerator = Metric.Observations,
            Interval = native,
        });
        Assert.Equal(3, rate.Rate!.Numerator);
        Assert.Equal(1, rate.ExcludedOutsideInterval);
    }

    [Fact(DisplayName = "R3: a session whose capture recorded no stop has no recording, and its whole-session rate stays unavailable")]
    public void NoStopNamesNoRecording()
    {
        ObservationRowV1[] rows = [Record(1_000), Record(1_100)];
        Assert.Null(RecordingOf(rows, calibration: null));

        // Nor do a calibration of the start alone, a stop no later than the start, or readings asked of another clock.
        Assert.Null(RecordingOf(rows, Calibration(1_500, 25_001_000) with { Samples = [Sample(1_500)] }));
        Assert.Null(RecordingOf(rows, Calibration(1_500, 1_500)));

        // Nor does a stop at or before the epoch, though records follow it: their span is no interval (§19.2).
        Assert.Null(RecordingOf(rows, Calibration(900, 1_000)));

        using (var calibrated = new TemporarySession())
        {
            Publish(calibrated.Store, rows, clock: Clock, calibration: Calibration(1_500, 25_001_000));
            SourceClockDescriptor elsewhere = ClockFor(ClockId.New(), "elsewhere");
            Assert.Null(SessionRecording.NativeInterval(calibrated.Store, calibrated.Store.Current!, elsewhere));
            calibrated.Store.ReleaseSegmentReaders();
        }

        using var session = new TemporarySession();
        Publish(session.Store, rows, clock: Clock);
        MetricResult rate = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Rate,
            RateNumerator = Metric.Observations,
        });
        Assert.Equal(MetricUnavailableReason.NoInterval, rate.Unavailable);
    }

    [Fact(DisplayName = "R3: a recording whose readings are further apart than a tick count holds is held centred on its epoch")]
    public void ARecordingWiderThanATickCountIsHeldAroundItsEpoch()
    {
        // A 3 GHz clock, epoch at its reading 0, with records some 84 years either side, which no capture's plausibility
        // window admits but a session can hold: the recording reaches further than a tick count, so it is the part
        // centred on the epoch, and a whole-session rate over it says the two records beyond it are outside it.
        var fast = new SourceClockDescriptor(TestSessions.Clock, HostId.Derive("fast-recording"), SourceClockKind.Monotonic,
            TimestampEncoding.Qpc, 3_000_000_000, 0, TimestampRounding.NearestEven, long.MaxValue);
        const long far = 8_000_000_000_000_000_000;
        using var session = new TemporarySession();
        Publish(session.Store, [FastRecord(-far), FastRecord(3_000), FastRecord(far)], clock: fast,
            calibration: Calibration(1_500, 7_500_000_000));

        TimeRange native = SessionRecording.NativeInterval(session.Store, session.Store.Current!, fast)!.Value;
        Assert.Equal(new TimeRange(-(1L << 62), (1L << 62) - 1), native);
        MetricResult rate = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Rate,
            RateNumerator = Metric.Observations,
            Interval = native,
        });
        Assert.Equal((1, long.MaxValue, 2L), (rate.Rate!.Numerator, rate.Rate.IntervalTicks, rate.ExcludedOutsideInterval));

        // A 3 GHz reading is a third of a nanosecond.
        static ObservationRowV1 FastRecord(long nativeTicks) => Record(nativeTicks) with { SessionRelativeTicks = nativeTicks / 3 };
    }

    private static TimeRange? RecordingOf(ObservationRowV1[] rows, ClockCalibrationV1? calibration)
    {
        using var session = new TemporarySession();
        Publish(session.Store, rows, clock: Clock, calibration: calibration);
        TimeRange? native = SessionRecording.NativeInterval(session.Store, session.Store.Current!, Clock);
        TimeRange? presented = SessionOverviewProjector.Project(session.Store).Recording;
        Assert.Equal(native is null, presented is null);
        session.Store.ReleaseSegmentReaders();
        return native;
    }

    /// <summary>A UDP send at a native reading of <see cref="Clock"/>, with its session time.</summary>
    private static ObservationRowV1 Record(long nativeTicks) =>
        Transfer(nativeTicks, ObservationKind.Send, AccountingSide.SendSide, 10, 100).Between("192.168.1.5:61000", "8.8.8.8:53") with
        {
            Mechanism = Mechanism.Udp,
            SessionRelativeTicks = (nativeTicks - Clock.CaptureEpochNativeTicks) * 100,
        };

    private static ClockCalibrationV1 Calibration(long start, long stop) => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = Capture.Value,
        ClockId = Clock.Id.Value,
        WallClock = "test-wall-clock",
        Samples = [Sample(start), Sample(stop)],
    };

    private static ClockCalibrationSampleV1 Sample(long nativeTicks) => new()
    {
        NativeTicks = nativeTicks,
        Utc = Started.AddTicks(nativeTicks),
        AcquisitionUncertaintyNanoseconds = 200,
    };
}
