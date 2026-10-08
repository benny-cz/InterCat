using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
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

    [AvaloniaFact(DisplayName = "§6.2: between its ends the timeline ticks round instants of the ladder, in session time or on the wall clock, each named where it is drawn and clear of the time base")]
    public void TheAxisTicksRoundInstants()
    {
        // Ten sends 0.2 s apart, within the calibration's 2.5 s.
        using var calibrated = new TemporarySession();
        Publish(calibrated.Store,
        [
            .. Enumerable.Range(0, 10).Select(index => Transfer(1_100 + index, ObservationKind.Send, AccountingSide.SendSide,
                10, 100, (ulong)(1 + index)).Between("192.168.1.5:61000", "8.8.8.8:53") with
            {
                Mechanism = Mechanism.Udp,
                SessionRelativeTicks = (1_000 + (index * 2_000_000L)) * 100,
            }),
        ], clock: Clock, calibration: Calibration());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        Show(window, calibrated);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        CultureInfo culture = CultureInfo.CurrentCulture;
        foreach ((bool wallClock, double width) in new[] { (false, 1_456d), (true, 1_456d), (false, 1_080d), (true, 1_080d) })
        {
            (window.Width, window.Height) = (width, width == 1_456 ? 939 : 700);
            window.GetControl<ToggleButton>("WallClockToggle").IsChecked = wallClock;
            Render(window);
            IReadOnlyList<TimelineView.AxisTick> ticks = timeline.AxisTicks;
            // At the smallest window the whole time base, which the axis states first, may leave no room for a tick.
            Assert.True(width < 1_456 || ticks.Count >= 2, $"{(wallClock ? "The wall clock" : "Session time")} at {width} px ticks {ticks.Count} instants.");

            // Each label is centred on its rule, keeps clear of the next, of the end labels and of the time base.
            (double saidLeft, double saidRight) = timeline.TimeBaseSpan;
            (double from, double to) = timeline.AxisEndsSpan;
            Assert.All(ticks, tick =>
            {
                Assert.Equal(tick.X, tick.Left + (tick.Width / 2), 3);
                Assert.True(tick.Left >= from && tick.Left + tick.Width <= to, $"{tick.Label} runs into an end's label.");
                Assert.True(double.IsNaN(saidLeft) || tick.Left + tick.Width + 12 <= saidLeft || tick.Left >= saidRight + 12,
                    $"{tick.Label} runs into the time base.");
            });
            Assert.All(ticks.Zip(ticks.Skip(1)), pair => Assert.True(pair.Second.Left >= pair.First.Left + pair.First.Width + 12,
                $"{pair.First.Label} and {pair.Second.Label} run together."));

            // Each names the instant it is drawn at: a round session time, or the time of day the wall clock read there.
            SessionClock clock = workspace.TimeBase;
            foreach (TimelineView.AxisTick tick in ticks)
            {
                if (wallClock)
                {
                    TimeSpan read = TimeZoneInfo.ConvertTime(clock.WallClock!.At(tick.Ticks), clock.Zone).TimeOfDay;
                    Assert.InRange((read - TimeSpan.Parse(tick.Label, CultureInfo.InvariantCulture)).Ticks, -1, 1);
                }
                else
                {
                    string[] parts = tick.Label.Split(' ');
                    decimal ticksPerUnit = parts[1] switch { "s" => 10_000_000m, "ms" => 10_000m, "µs" => 10m, _ => 0m };
                    Assert.Equal(tick.Ticks, (long)(decimal.Parse(parts[0], NumberStyles.Number, culture) * ticksPerUnit));
                }
            }
        }

        Save(window, "axis-ticks-wall-clock-1080x700.png");
        window.Close();
    }

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

    [AvaloniaFact(DisplayName = "§6.2: where its capture recorded its wall clock, one toggle reads the timeline's instants on it, and the axis, its words and the time scope say so")]
    public void TheWallClockToggleReadsTheAxisOnTheWallClock()
    {
        using var calibrated = new TemporarySession();
        Publish(calibrated.Store, Records(), clock: Clock, calibration: Calibration());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        Show(window, calibrated);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ToggleButton toggle = window.GetControl<ToggleButton>("WallClockToggle");
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.False(toggle.IsChecked);
        Assert.StartsWith("session time since ", timeline.TimeBaseText, StringComparison.Ordinal);
        Assert.Equal(workspace.WallClockTip, ToolTip.GetTip(toggle));

        // Toggled, the axis names the wall clock's day and offset between instants read on it, as a screen reader hears.
        toggle.IsChecked = true;
        Render(window);
        Assert.True(workspace.ReadsWallClock);
        SessionClock wall = SessionClock.Wall(workspace.Snapshot.WallClock!, TimeZoneInfo.Local, workspace.Snapshot.Extent);
        string named = wall.Base(timeline.Viewport, workspace.Snapshot.Began, CultureInfo.CurrentCulture);
        Assert.StartsWith("wall clock on ", named, StringComparison.Ordinal);
        Assert.Equal(named, timeline.TimeBaseText);
        Assert.Equal(wall.Instant(timeline.Viewport.StartTicks, timeline.Viewport.SpanTicks, CultureInfo.CurrentCulture),
            timeline.AxisStartText);
        Assert.EndsWith(" · " + named, ControlAutomationPeer.CreatePeerForElement(timeline).GetItemStatus(), StringComparison.Ordinal);
        Assert.Contains(wall.Range(workspace.Snapshot.Extent, CultureInfo.CurrentCulture),
            ControlAutomationPeer.CreatePeerForElement(timeline).GetItemStatus(), StringComparison.Ordinal);
        Assert.Contains("its times read on the wall clock", workspace.ChangedSettings);
        Save(window, "time-base-wall-clock-1456x939.png");

        // Restore defaults reads session time again.
        Assert.True(window.RestoreDefaultViewAsync().Result);
        Render(window);
        Assert.False(toggle.IsChecked);
        Assert.StartsWith("session time since ", timeline.TimeBaseText, StringComparison.Ordinal);

        // An import recorded no wall clock: the toggle is not there.
        using var imported = new TemporarySession();
        Publish(imported.Store, Records(), clock: Clock);
        Show(window, imported);
        Assert.False(window.GetControl<ToggleButton>("WallClockToggle").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.2: the interval table states each window on the wall clock whole at the smallest window, never under the count beside it, and keys no mechanism where it holds no record")]
    public void TheIntervalTableFitsTheWallClock()
    {
        using var calibrated = new TemporarySession();
        Publish(calibrated.Store, Records(), clock: Clock, calibration: Calibration());
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        Show(window, calibrated);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.GetControl<ToggleButton>("WallClockToggle").IsChecked = true;
        workspace.ShowTables = true;
        Render(window);

        // Each window reads the wall clock's time of day at both ends, which session time's column was too narrow for.
        ListBox intervals = window.GetControl<ListBox>("IntervalList");
        Assert.Contains(" – ", workspace.Intervals[0].Window, StringComparison.Ordinal);
        foreach (IntervalRow row in workspace.Intervals.Take(3))
        {
            Control container = intervals.ContainerFromItem(row)!;
            TextBlock text = container.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == row.Window);
            TextBlock count = container.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == row.Observations);
            Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5,
                $"{row.Window} is {text.TextLayout.WidthIncludingTrailingWhitespace} wide in {text.Bounds.Width}.");
            Assert.True(text.Bounds.Right <= count.Bounds.Left, $"{row.Window} runs under {row.Observations}.");

            // The bytes end inside the table, trimmed to what is left of it rather than cut by its edge.
            TextBlock bytes = container.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == row.KnownBytes);
            Assert.True(bytes.TranslatePoint(new Point(bytes.Bounds.Width, 0), intervals)!.Value.X <= intervals.Bounds.Width,
                $"{row.KnownBytes} ends past the table.");
        }

        // An interval with no record keys no mechanism: no glyph beside it, and no name read aloud.
        IntervalRow empty = workspace.Intervals.First(row => row.ObservationCount == 0);
        Assert.Equal(string.Empty, empty.Glyph);
        Assert.DoesNotContain("Unknown", empty.AccessibleName, StringComparison.Ordinal);
        Save(window, "interval-table-wall-clock-1080x700.png");
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
