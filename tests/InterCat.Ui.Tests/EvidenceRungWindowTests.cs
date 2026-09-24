using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The evidence rung in a real window over a published session: the keys that reach it, load more and leave it, and
/// the hold that keeps records still while a live capture publishes a newer generation.
/// </summary>
public sealed class EvidenceRungWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R7: records hold the view still; a newer generation is offered, and applied on leaving")]
    public async Task TheEvidenceRungHoldsItsGenerationAndEscapeAppliesTheNewestOne()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 150));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var reading = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.True(window.GetControl<Button>("ShowRecordsButton").IsEnabled);

        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Assert.True(reading.IsEvidenceRung);
        await reading.EvidenceReady;
        Dispatch();
        Assert.Equal(SessionEvidenceQuery.DefaultPageSize, reading.RungRows.Count);
        Assert.True(window.GetControl<ListBox>("RungList").IsVisible);
        Assert.False(window.GetControl<Button>("ShowRecordsButton").IsEnabled);

        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        await reading.EvidenceReady;
        Dispatch();
        Assert.Equal(2 * SessionEvidenceQuery.DefaultPageSize, reading.RungRows.Count);
        Assert.True(reading.CanLoadMoreEvidence);

        // A live publication while records are read is held; the rows the user is reading stay where they are.
        Publish(session.Store, Exchange(150, 10));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.Same(reading, window.DataContext);
        Assert.True(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Contains("Generation 2", window.GetControl<TextBlock>("HeldBannerText").Text, StringComparison.Ordinal);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        var updated = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(reading, updated);
        Assert.False(updated.IsEvidenceRung);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Contains("generation 2", updated.Snapshot.Title, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7: F5 applies the held generation and keeps the evidence rung and its scope")]
    public async Task RefreshAppliesTheHeldGenerationWithoutLeavingTheRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 20));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var reading = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        await reading.EvidenceReady;

        Publish(session.Store, Exchange(20, 5));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.Same(reading, window.DataContext);

        window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        Dispatch();
        var refreshed = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(reading, refreshed);
        Assert.True(refreshed.IsEvidenceRung);
        await refreshed.EvidenceReady;
        Dispatch();
        Assert.Equal(2 * 25, refreshed.RungRows.Count);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Shift and a drag across the timeline brushes a range and the ranking counts only that range")]
    public async Task DraggingTheTimelineBrushesARangeAndReRanks()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 100));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        string whole = workspace.RungRows.Single().Observations;

        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        double plot = timeline.Bounds.Width - 52;
        Avalonia.Point from = timeline.TranslatePoint(new(38 + (0.10 * plot), timeline.Bounds.Height / 2), window)!.Value;
        Avalonia.Point to = timeline.TranslatePoint(new(38 + (0.40 * plot), timeline.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(new(from.X + 20, from.Y), RawInputModifiers.Shift);
        window.MouseMove(to, RawInputModifiers.Shift);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift);
        Dispatch();

        TimeRange brushed = Assert.IsType<TimeRange>(workspace.SelectedInterval);
        long span = workspace.Snapshot.Extent.EndTicks - workspace.Snapshot.Extent.StartTicks;
        Assert.InRange(brushed.EndTicks - brushed.StartTicks, span / 4, span / 2);
        await workspace.IntervalReady;
        Dispatch();
        Assert.True(workspace.IsRankedWithinInterval);
        Assert.NotEqual(whole, workspace.RungRows.Single().Observations);
        Assert.StartsWith("Ranked within", workspace.RankingScopeText, StringComparison.Ordinal);
        WriteableBitmapCheck(window, "brushed-interval.png");

        // A press without a drag still selects the single bucket under the pointer, as before.
        window.MouseDown(from, MouseButton.Left);
        window.MouseUp(from, MouseButton.Left);
        Dispatch();
        Assert.Contains(workspace.Snapshot.Timeline, bucket => bucket.Interval == workspace.SelectedInterval);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a plain drag pans the timeline, and Home, End and 0 move it by keyboard")]
    public void APlainDragPansAndKeysMoveTheViewport()
    {
        using var session = new TemporarySession();
        // Exchanges 50 ms apart, so the session spans seconds and there is room to zoom and pan.
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        timeline.Focus();

        // Zoom in first so there is room to pan; a drag then moves the viewport and selects nothing.
        for (int step = 0; step < 4; step++) window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        TimeRange zoomed = timeline.Viewport;
        Assert.True(zoomed.SpanTicks < workspace.Snapshot.Extent.SpanTicks);
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal(workspace.Snapshot.Extent.StartTicks, timeline.Viewport.StartTicks);

        double plot = timeline.Bounds.Width - 52;
        Point from = timeline.TranslatePoint(new(38 + (0.6 * plot), timeline.Bounds.Height / 2), window)!.Value;
        Point to = timeline.TranslatePoint(new(38 + (0.3 * plot), timeline.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatch();
        Assert.True(timeline.Viewport.StartTicks > workspace.Snapshot.Extent.StartTicks);
        Assert.Equal(zoomed.SpanTicks, timeline.Viewport.SpanTicks);
        Assert.Null(workspace.SelectedInterval);

        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        Assert.Equal(workspace.Snapshot.Extent.EndTicks, timeline.Viewport.EndTicks);
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
        Assert.Equal(workspace.Snapshot.Extent, timeline.Viewport);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7: pausing the live view holds its generation while recording continues, and F resumes it")]
    public void PausingTheLiveViewHoldsItAndFResumes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var first = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.True(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.Equal("Recording · following live", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("Loss is stated when recording stops", window.GetControl<TextBlock>("HealthLossText").Text);
        Assert.StartsWith("last publication", window.GetControl<TextBlock>("HealthFreshnessText").Text, StringComparison.Ordinal);

        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Assert.Equal("Recording · view paused", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("Follow live (F)", window.GetControl<Button>("FollowButton").Content);

        Publish(session.Store, Exchange(10, 5));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.Same(first, window.DataContext);
        Assert.StartsWith("View paused at generation 1", window.GetControl<TextBlock>("HeldBannerText").Text,
            StringComparison.Ordinal);
        Assert.True(window.GetControl<Button>("HeldFollowButton").IsVisible);
        WriteableBitmapCheck(window, "paused-live-view.png");

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatch();
        var resumed = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(first, resumed);
        Assert.Contains("generation 2", resumed.Snapshot.Title, StringComparison.Ordinal);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Equal("Recording · following live", window.GetControl<TextBlock>("HealthStateText").Text);

        // A saved session is not live: F does nothing and the strip says which loss statement applies.
        window.ApplyCaptureUpdate(Update(session) with { Phase = CaptureUiPhase.Complete }, forceOverview: true);
        Dispatch();
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.Equal("Saved session", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("No coverage ledger · loss unknown", window.GetControl<TextBlock>("HealthLossText").Text);
        Assert.Equal(string.Empty, window.GetControl<TextBlock>("HealthFreshnessText").Text);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "6.4: the window writes the applied view to the file the user chose, in either format")]
    public async Task TheWindowWritesTheAppliedViewToTheChosenFile()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 12));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.True(window.GetControl<Button>("ExportButton").IsVisible);

        string json = Path.Combine(session.Path, "view.json");
        Assert.Equal(1, await window.WriteExportAsync(json, ExportFormat.Json));
        using (var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json)))
        {
            Assert.Equal("ranking", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal("Machine", document.RootElement.GetProperty("context").GetProperty("rung").GetString());
        }

        string csv = Path.Combine(session.Path, "view.csv");
        Assert.Equal(1, await window.WriteExportAsync(csv, ExportFormat.Csv));
        Assert.Equal(2, (await File.ReadAllLinesAsync(csv)).Length);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "3.1: the capture card states how soon the first view arrived and how often it refreshes")]
    public void TheCaptureCardStatesFirstFeedback()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 3));
        var window = new MainWindow();
        window.Show();
        TextBlock latency = window.GetControl<TextBlock>("CaptureLatency");

        window.ApplyCaptureUpdate(Update(session) with
        {
            Milestones = new(Started: TimeSpan.FromMilliseconds(650), PublicationInterval: TimeSpan.FromSeconds(2)),
        });
        Assert.Equal(string.Empty, latency.Text);

        window.ApplyCaptureUpdate(Update(session) with
        {
            Milestones = new(Started: TimeSpan.FromMilliseconds(650), FirstOverview: TimeSpan.FromMilliseconds(1_950),
                PublicationInterval: TimeSpan.FromSeconds(2)),
        });
        Assert.Contains($"{1.3:0.0} s after recording began", latency.Text, StringComparison.Ordinal);
        Assert.Contains("every 2 s", latency.Text, StringComparison.Ordinal);
        window.Close();
    }

    public static TheoryData<int, int> Sizes => new() { { 1080, 700 }, { 1456, 939 } };

    [AvaloniaTheory(DisplayName = "R15: a published session's channel and evidence rungs render at every supported size")]
    [MemberData(nameof(Sizes))]
    public async Task TheChannelAndEvidenceRungsRender(int width, int height)
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 40));
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Channel channel = workspace.Snapshot.Channels.Single();
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString(), channel.Key })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        Dispatch();
        Assert.True(workspace.OffersEvidenceStep);
        Assert.NotNull(window.CaptureRenderedFrame());
        Dispatch();
        Save(window.CaptureRenderedFrame()!, $"real-channel-{width}x{height}.png");
        ScrollViewer crumbs = window.GetControl<ScrollViewer>("CrumbScroller");
        Assert.Equal(crumbs.Extent.Width - crumbs.Viewport.Width, crumbs.Offset.X, 1.0);

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        workspace.SelectedRung = workspace.RungRows[3];
        Dispatch();
        WriteableBitmapCheck(window, $"real-evidence-{width}x{height}.png");
        window.Close();
    }

    private static void WriteableBitmapCheck(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Save(frame!, name);
    }

    private static void Save(Avalonia.Media.Imaging.WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
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

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
