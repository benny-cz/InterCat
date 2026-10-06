using System.Globalization;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.2: the timeline states the time base its instants count in, at all times - session time, since the wall-clock moment
/// its capture began where the capture recorded one, with that moment's offset from UTC (§1's units).
/// </summary>
public sealed class TimeBaseWindowTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A 10 MHz clock whose epoch, session time 0, is its reading 1,000.</summary>
    private static readonly SourceClockDescriptor Clock = new(
        TestSessions.Clock,
        HostId.Derive("time-base-tests"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        1_000,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    [AvaloniaFact(DisplayName = "§6.2: the timeline states the time base its instants count in: session time, since the wall-clock moment its capture began where it recorded one")]
    public void TheTimelineStatesItsTimeBase()
    {
        // A capture whose calibration read its clock 50 µs in: its axis counts session time since 100 µs past noon UTC,
        // said in the reader's zone with its offset from UTC, under the axis between its first and last instants, and to
        // a screen reader after the range in view.
        using var calibrated = new TemporarySession();
        Publish(calibrated.Store, Records(), clock: Clock, calibration: Calibration());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        Show(window, calibrated);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        string since = WorkspaceTime.TimeBase(Started.AddTicks(1_000), TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        Assert.StartsWith("session time since ", since, StringComparison.Ordinal);
        Assert.Contains(" UTC", since, StringComparison.Ordinal);
        Assert.Equal(since, timeline.TimeBaseText);
        Assert.EndsWith(" · " + since, ControlAutomationPeer.CreatePeerForElement(timeline).GetItemStatus(), StringComparison.Ordinal);
        Save(window, "time-base-1456x939.png");

        // At the smallest window it still fits whole.
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        Render(window);
        Assert.Equal(since, timeline.TimeBaseText);

        // Where an axis had room for less, it would say session time alone, and with room for not even that, nothing.
        double least = Enumerable.Range(0, 2_000).First(room => TimelineView.FittingTimeBase(since, room) is not null);
        Assert.Equal(WorkspaceTime.SessionTimeWords, TimelineView.FittingTimeBase(since, least));
        Assert.Null(TimelineView.FittingTimeBase(since, least - 1));
        Assert.Equal(since, TimelineView.FittingTimeBase(since, 2_000));

        // An import recorded no wall clock: its instants are session time, said so, and no moment is guessed.
        using var imported = new TemporarySession();
        Publish(imported.Store, Records(), clock: Clock);
        Show(window, imported);
        Assert.Equal(WorkspaceTime.SessionTimeWords, timeline.TimeBaseText);
        Assert.EndsWith(" · session time", ControlAutomationPeer.CreatePeerForElement(timeline).GetItemStatus(),
            StringComparison.Ordinal);
        window.Close();
    }

    private static void Show(MainWindow window, TemporarySession session)
    {
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Render(window);
    }

    /// <summary>Ten UDP sends a microsecond apart, from 10 µs into the session.</summary>
    private static ObservationRowV1[] Records() =>
    [
        .. Enumerable.Range(0, 10).Select(index => Transfer(1_100 + (10 * index), ObservationKind.Send, AccountingSide.SendSide,
            10, 100, (ulong)(1 + index)).Between("192.168.1.5:61000", "8.8.8.8:53") with
        {
            Mechanism = Mechanism.Udp,
            SessionRelativeTicks = (1_100 + (10 * index) - Clock.CaptureEpochNativeTicks) * 100,
        }),
    ];

    /// <summary>The wall clock read 50 µs into the session and at its stop, 2.5 s in, each at noon plus the reading.</summary>
    private static ClockCalibrationV1 Calibration() => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = TestSessions.Capture.Value,
        ClockId = Clock.Id.Value,
        WallClock = "test-wall-clock",
        Samples = [Sample(1_500), Sample(25_001_000)],
    };

    private static ClockCalibrationSampleV1 Sample(long nativeTicks) => new()
    {
        NativeTicks = nativeTicks,
        Utc = Started.AddTicks(nativeTicks),
        AcquisitionUncertaintyNanoseconds = 200,
    };

    private static void Render(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        _ = window.CaptureRenderedFrame();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Render(window);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
