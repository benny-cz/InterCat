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
/// §6.6's unmeasured value in the window: under a byte ranking, a relationship whose sends recorded no size is an open
/// cross-hatched band, never a zero-width hairline, and the legend keys it only while the graph draws one (R3).
/// </summary>
public sealed class UnmeasuredEncodingWindowTests
{
    [AvaloniaFact(DisplayName = "R3: the graph draws an unmeasured relationship with the unmeasured key only while a byte ranking sizes it")]
    public async Task TheUnmeasuredKeyFollowsTheDrawing()
    {
        using var session = new TemporarySession();
        Publish(session.Store, MeasuredBlindAndQuiet());
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        StackPanel key = window.GetControl<StackPanel>("UnmeasuredLegend");
        Assert.False(key.IsVisible);

        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        Dispatch();
        Assert.True(key.IsVisible);
        Assert.Contains(workspace.GraphDisplay.Edges, edge => edge.Unmeasured);
        Save(window, "unmeasured-graph.png");

        workspace.RankBy = RankingMetric.Records;
        await workspace.RankingReady;
        Dispatch();
        Assert.False(key.IsVisible);
        window.Close();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// client.exe sends the server 500 bytes; blind.exe sends it once without recording a size; quiet.exe's connection to it
    /// carries no send at either end, only receives.
    /// </summary>
    private static ObservationRowV1[] MeasuredBlindAndQuiet() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\blind.exe" }),
        Timed(Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\quiet.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 500, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, null, 300, 12).Between("127.0.0.1:51000", "127.0.0.1:8080")),
        Timed(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:51000")),
        Timed(Transfer(14, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 400, 14).Between("127.0.0.1:52000", "127.0.0.1:8080")),
        Timed(Transfer(15, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 15).Between("127.0.0.1:8080", "127.0.0.1:52000")),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
