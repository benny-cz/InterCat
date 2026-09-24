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
