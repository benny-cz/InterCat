using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// Renders the window without a screen. A review of a desktop application otherwise depends on someone
/// having a display; this lane produces the frame as a file, so the layout can be looked at from a build
/// and a defect found by looking is reproducible (section 17).
/// </summary>
public sealed class WindowRenderTests
{
    [AvaloniaTheory(DisplayName = "§3.1: a capture refusal stays visible at the minimum and review window sizes")]
    [MemberData(nameof(Sizes))]
    public void CaptureRefusalIsVisible(int width, int height)
    {
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture unavailable",
            "The capture broker could not use its protected data folder. The session was not started; "
            + "check the folder and retry, or open a saved session."));
        Dispatch();

        Button retry = window.GetControl<Button>("StartExploringButton");
        Assert.Equal("Retry exploring (Ctrl+R)", retry.Content);
        Assert.Equal("Capture unavailable", window.GetControl<TextBlock>("CaptureStatus").Text);
        Assert.Contains("protected data folder", window.GetControl<TextBlock>("CaptureDetail").Text,
            StringComparison.Ordinal);
        Assert.Equal(window.GetControl<TextBlock>("CaptureDetail").Text, ToolTip.GetTip(retry));
        Point buttonAt = retry.TranslatePoint(new(0, 0), window)!.Value;
        Point statusAt = window.GetControl<TextBlock>("CaptureStatus").TranslatePoint(new(0, 0), window)!.Value;
        Assert.True(statusAt.Y >= buttonAt.Y + retry.Bounds.Height,
            "The failure must be explained immediately below the start/retry action.");

        foreach (string name in new[] { "CaptureStatus", "CaptureDetail" })
        {
            TextBlock text = window.GetControl<TextBlock>(name);
            Point location = text.TranslatePoint(new(0, 0), window)!.Value;
            Assert.True(location.Y >= 0 && location.Y + text.Bounds.Height <= window.Bounds.Height,
                $"{name} is at {location.Y:0.#}–{location.Y + text.Bounds.Height:0.#} of {window.Bounds.Height:0.#}");
        }

        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.1: a long broker refusal cannot push the retry control or status offscreen")]
    public void LongCaptureRefusalKeepsRetryVisible()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture unavailable",
            string.Concat(Enumerable.Repeat("The protected root failed its security read-back. ", 30))));
        Dispatch();

        foreach (string name in new[] { "StartExploringButton", "CaptureStatus" })
        {
            Control control = name == "StartExploringButton"
                ? window.GetControl<Button>(name) : window.GetControl<TextBlock>(name);
            Point point = control.TranslatePoint(new(0, 0), window)!.Value;
            Assert.True(point.Y >= 0 && point.Y + control.Bounds.Height <= window.Bounds.Height);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void FirstRunShowsEmptyWorkspaceAndFocusedStartAction()
    {
        var window = new MainWindow();
        window.Show();
        Dispatch();

        var viewModel = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Button start = window.GetControl<Button>("StartExploringButton");
        Assert.Empty(viewModel.Snapshot.Processes);
        Assert.Empty(viewModel.Snapshot.Timeline);
        Assert.True(start.IsFocused);
        Assert.Equal("Ready to explore", window.GetControl<TextBlock>("CaptureStatus").Text);
        Assert.Equal("Hatching = coverage gap or unknown · outline = unmeasured",
            window.GetControl<TextBlock>("CoverageLegendText").Text);
        Assert.False(window.GetControl<Button>("ShowRecordsButton").IsEnabled);
        Assert.False(window.GetControl<Button>("BrowseChannelsButton").IsEnabled);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a custom workspace header names its session without inventing a capture gap")]
    public void CustomWorkspaceHeaderIsTruthful()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create() with
        {
            Title = "Imported session generation 2",
            Timeline = [],
        };
        var window = new MainWindow(new WorkspaceViewModel(snapshot, "generation-2"));
        window.Show();
        Dispatch();

        Assert.Equal(snapshot.Title, window.GetControl<TextBlock>("WorkspaceTitleText").Text);
        Assert.Equal("Coverage not quantified", window.GetControl<TextBlock>("CoverageSummaryText").Text);
        window.Close();
    }

    /// <summary>The section 1.3 minimum window, and a size a reviewer is likely to use.</summary>
    public static TheoryData<int, int> Sizes => new() { { 1080, 700 }, { 1456, 939 } };

    [AvaloniaTheory(DisplayName = "R15: the window renders at its minimum size and at a review size")]
    [MemberData(nameof(Sizes))]
    public async Task WindowRendersAtEverySupportedSize(int width, int height)
    {
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        await ((WorkspaceViewModel)window.DataContext!).LayoutReady;
        Dispatch();

        WriteableBitmap? frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width >= width - 1, $"rendered width {frame.PixelSize.Width}");
        Assert.True(frame.PixelSize.Height >= height - 1, $"rendered height {frame.PixelSize.Height}");
        Save(frame, $"window-{width}x{height}.png");
    }

    [AvaloniaTheory(DisplayName = "R15: the window renders every rung of the ladder down to evidence")]
    [MemberData(nameof(Sizes))]
    public async Task EveryRungRenders(int width, int height)
    {
        var window = new MainWindow(new WorkspaceViewModel()) { Width = width, Height = height };
        window.Show();
        var viewModel = (WorkspaceViewModel)window.DataContext!;
        await viewModel.LayoutReady;

        for (int depth = 0; depth < 5 && !viewModel.IsEmptyRung; depth++)
        {
            viewModel.SelectedRung = viewModel.RungRows[0];
            Assert.True(viewModel.Descend(), $"the ladder refused to descend at depth {depth}");
            Dispatch();
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Save(frame!, $"rung-{depth + 1}-{width}x{height}.png");
        }

        Assert.Equal("L5 · EVIDENCE", viewModel.LevelBadge);
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }
}
