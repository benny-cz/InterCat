using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// Going to a moment in the window (§6.2, §6.7): Ctrl+F, a time copied from the machine's own logs, and Enter move the
/// timeline there - zoomed as it was - and make the selected lane's cell holding it the analysis interval, which the
/// inspector explains.
/// </summary>
public sealed class GoToMomentWindowTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [AvaloniaFact(DisplayName = "§6.2: a time of day typed into the search and Enter move the zoomed timeline there and choose and explain the selected lane's cell holding it")]
    public async Task ATimeTypedIsGoneTo()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Records(), calibration: Calibration());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");

        // Zoomed in on the session's start, a tenth of it in view, with the TCP lane selected.
        workspace.SelectTimelineLane(Mechanism.Tcp);
        TimeRange extent = workspace.Snapshot.Extent;
        timeline.SetViewport(new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 10)));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        long span = timeline.Viewport.SpanTicks;

        // Ctrl+F, the time 1.5 s in as the machine's clock read it, and Enter.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        TextBox search = window.GetControl<TextBox>("SearchBox");
        Assert.True(search.IsFocused);
        search.Text = TimeZoneInfo.ConvertTime(Noon.AddSeconds(1.5), TimeZoneInfo.Local).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Dispatch();
        Assert.Equal("TIME", workspace.SearchResults[0].Kind);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        await workspace.GoToReady;
        Dispatch();

        // The timeline is centred on it at the span it had, and the TCP cell holding it is chosen and explained.
        Assert.Equal(span, timeline.Viewport.SpanTicks);
        Assert.Equal(15_000_000, timeline.Viewport.StartTicks + (span / 2));
        TimeRange chosen = workspace.SelectedInterval!.Value;
        Assert.True(chosen.StartTicks <= 15_000_000 && 15_000_000 < chosen.EndTicks);
        Assert.Equal(string.Empty, search.Text);
        TextBlock explanation = window.GetControl<TextBlock>("CellExplanationText");
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.Contains(" TCP record", explanation.Text, StringComparison.Ordinal);
        Assert.Contains(" in this interval", explanation.Text, StringComparison.Ordinal);

        // It is a cell of the zoom's own count, which the lanes draw: the lifecycle lane, with nothing in view, is counted
        // there too rather than leaving every lane on the overview's coarse columns.
        Assert.Contains(" here one of this view's own ", explanation.Text, StringComparison.Ordinal);
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string rendered = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(rendered);
        window.CaptureRenderedFrame()!.Save(Path.Combine(rendered, "go-to-moment-1456x939.png"));
        window.Close();
    }

    /// <summary>client.exe (PID 100) created, then sending every 10 ms for 2.5 s.</summary>
    private static ObservationRowV1[] Records() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        .. Enumerable.Range(1, 250).Select(index => Transfer(index * 100_000L, ObservationKind.Send, AccountingSide.SendSide, 64, 100,
            (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = index * 10_000_000L }),
    ];

    /// <summary>The wall clock read at the session's start, noon, and at its stop, 2.5 s in.</summary>
    private static ClockCalibrationV1 Calibration() => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = TestSessions.Capture.Value,
        ClockId = TestClock.Id.Value,
        WallClock = "test-wall-clock",
        Samples =
        [
            new() { NativeTicks = 0, Utc = Noon, AcquisitionUncertaintyNanoseconds = 200 },
            new() { NativeTicks = 25_000_000, Utc = Noon.AddSeconds(2.5), AcquisitionUncertaintyNanoseconds = 200 },
        ],
    };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
