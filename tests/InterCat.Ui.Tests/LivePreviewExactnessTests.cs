using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// What a live capture looks like beside its exact results: before its first publication, the window says it is
/// recording rather than that nothing runs; after it, the broker's preview (broker-v1 §5.9) is drawn and explained but
/// never becomes an exact result (P26, §19.3).
/// </summary>
public sealed class LivePreviewExactnessTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "P26: a live preview never reaches the ranking, the interval table, a step, a brush or an export")]
    public async Task ALivePreviewNeverBecomesAnExactResult()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 100));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        CaptureUiUpdate published = Update(session) with { OverviewChunks = 2 };
        window.ApplyCaptureUpdate(published);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange extent = workspace.Snapshot.Extent;
        var exportedAt = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        string csv = (await workspace.ExportAsync(ExportFormat.Csv, exportedAt)).Content;
        string json = (await workspace.ExportAsync(ExportFormat.Json, exportedAt)).Content;
        IntervalRow[] intervals = [.. workspace.Intervals];
        RungRow[] ranking = [.. workspace.RungRows];

        // Chunk 3 is not shown yet: the broker previews 6 of its records after the published extent's end at tick 210.
        var preview = new BrokerCapturePreview(20, 3, 1, 11, 11, 0,
            [new(2, 10, Mechanism.Tcp, 5), new(3, 11, Mechanism.Tcp, 4), new(3, 12, Mechanism.Tcp, 2)]);
        window.ApplyCaptureUpdate(published with { Overview = null, OverviewChunks = null, LivePreview = preview });
        Dispatch();
        LiveEdge edge = Assert.IsType<LiveEdge>(workspace.LiveEdge);
        Assert.Equal(6, edge.PreviewedRecords);

        // The applied view is exactly what it was: its ranking, its interval table and both exports.
        Assert.Equal(ranking, workspace.RungRows);
        Assert.Equal(intervals, workspace.Intervals);
        Assert.Equal(csv, (await workspace.ExportAsync(ExportFormat.Csv, exportedAt)).Content);
        Assert.Equal(json, (await workspace.ExportAsync(ExportFormat.Json, exportedAt)).Content);

        // Stepping to the last record lands on a published bucket, never on a preview bin after the extent.
        timeline.Focus();
        TimeRange? last = null;
        for (int step = 0; step < 200 && timeline.Navigate(Key.OemCloseBrackets); step++)
        {
            last = workspace.SelectedInterval;
        }

        Assert.NotNull(last);
        Assert.True(last!.Value.EndTicks <= extent.EndTicks, $"A step reached {last} beyond the published {extent}.");

        // A brush dragged from the published plot into the preview stops exactly at the published extent's end.
        workspace.ClearSelection();
        Assert.Null(workspace.SelectedInterval);
        Point inside = timeline.TranslatePoint(new(timeline.PlotStart + (timeline.PlotSpan / 2), timeline.Bounds.Height / 2), window)!.Value;
        Point intoPreview = timeline.TranslatePoint(timeline.PointOfLive(edge.Bins[^1])!.Value, window)!.Value;
        window.MouseDown(inside, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(intoPreview, RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
        window.MouseUp(intoPreview, MouseButton.Left, RawInputModifiers.Shift);
        Dispatch();
        TimeRange brushed = Assert.IsType<TimeRange>(workspace.SelectedInterval);
        Assert.Equal(extent.EndTicks, brushed.EndTicks);
        Assert.True(brushed.StartTicks > extent.StartTicks, $"The brush began inside the plot, not at {brushed}.");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.1: a recording that has published nothing says so, and a saved view gives way only once it records")]
    public void ARecordingWithNothingPublishedSaysSo()
    {
        // A saved session is open when the user starts exploring; starting leaves it, since approval may be declined.
        using var saved = new TemporarySession();
        Publish(saved.Store, Exchange(0, 3));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(saved) with { Phase = CaptureUiPhase.Complete }, forceOverview: true);
        var open = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.ForgetDisplayedSession();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Starting Explore", "Windows will ask once."));
        Assert.Same(open, window.DataContext);
        Assert.StartsWith("Session · generation", open.Title, StringComparison.Ordinal);

        // Recording replaces it with an empty workspace that says what is happening, not that nothing is running.
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Nothing published yet."));
        Dispatch();
        var waiting = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(open, waiting);
        Assert.Equal("Recording · first view pending", waiting.Title);
        Assert.StartsWith("Recording. The first view appears once the broker's first chunk is published and derived",
            waiting.EmptyReason, StringComparison.Ordinal);
        Assert.Equal(waiting.EmptyReason, waiting.WorkspaceDisclosure);
        Assert.Equal("No time recorded yet", waiting.IntervalLabel);
        Save(window, "recording-first-view-pending-1080x700.png");

        // The first publication replaces the waiting workspace with the capture's own view.
        using var live = new TemporarySession();
        Publish(live.Store, Exchange(0, 5));
        window.ApplyCaptureUpdate(Update(live) with { OverviewChunks = 1 });
        Dispatch();
        var first = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(waiting, first);
        Assert.StartsWith("Session · generation", first.Title, StringComparison.Ordinal);

        // A capture that ends having published nothing says so, and the words for no capture return.
        window.ForgetDisplayedSession();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Nothing published yet."));
        var again = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping and keeping the session", "Finalizing."));
        Assert.StartsWith("Stopping.", again.EmptyReason, StringComparison.Ordinal);
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Capture closed without a derived session", "Nothing kept."));
        Assert.Same(again, window.DataContext);
        Assert.Equal("Start exploring", again.Title);
        Assert.Equal("No capture is running. Start exploring to publish a live session.", again.EmptyReason);
        window.Close();
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times from <paramref name="first"/>.</summary>
    private static ObservationRowV1[] Exchange(int first, int count) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
