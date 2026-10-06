using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// R21 in the window: beneath the inspector's time scope, what the capture covered there, in muted ink where it covered
/// what it collected and in the caution ink where it did not, wrapped whole at the window's smallest.
/// </summary>
public sealed class ScopeCoverageWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R21: the inspector states its time scope's coverage beneath the range, in the caution ink where the capture saw nothing")]
    public async Task TheTimeScopeStatesItsCoverage()
    {
        // The capture delivered readings from 0 to 20 and from 40 to 60, and none between.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(6, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(51, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
        ], coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [Epoch(1, 0, 20, processes: 2), Epoch(2, 40, 60, processes: 0)],
        });
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Render(window);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TextBlock range = window.GetControl<TextBlock>("TimeScopeValue");
        TextBlock coverage = window.GetControl<TextBlock>("TimeScopeCoverage");

        // Over the whole session the capture covered what it collected: said plainly, beneath the range it qualifies.
        Assert.Equal("Coverage: covered for process lifecycle and TCP · no other mechanism collected", coverage.Text);
        Assert.True(coverage.IsEffectivelyVisible);
        Assert.Equal(ThemeResources.ToColor(ThemePalette.Surfaces(ThemeResources.CurrentMode).MutedInk), ColorOf(coverage.Foreground));
        Assert.True(coverage.TranslatePoint(default, range)!.Value.Y >= range.Bounds.Height,
            "The scope's coverage is not beneath its range.");

        // Brushed where the capture delivered nothing, it says so in the caution ink, and at the smallest window its words
        // wrap whole within the inspector.
        workspace.SelectInterval(new TimeRange(25, 35));
        Assert.Equal("Reading this range's coverage…", coverage.Text);
        await workspace.IntervalReady;
        Render(window);
        Assert.Equal(workspace.ScopeCoverage, coverage.Text);
        Assert.StartsWith("Coverage unknown: outside the readings", coverage.Text, StringComparison.Ordinal);
        Assert.Equal(ThemeResources.ToColor(ThemePalette.Status(ThemeResources.CurrentMode).Caution), ColorOf(coverage.Foreground));
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        Render(window);
        Assert.True(coverage.TextLayout.TextLines.Count > 1, "The scope's coverage does not wrap.");
        Assert.DoesNotContain(LegibleTextTests.CutOff(window, "the smallest window, brushed in a gap"),
            line => line.Contains("Coverage unknown", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>A live epoch between two delivered readings that collected TCP and process creations and lost nothing.</summary>
    private static CoverageEpochV1 Epoch(int number, long first, long last, long processes) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
            new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 1, Version = 4, Mechanism = Mechanism.ProcessLifecycle },
        ],
        Deliveries =
        [
            new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 },
            .. processes == 0 ? Array.Empty<CoverageDeliveryV1>() : [new CoverageDeliveryV1
            {
                ProviderId = ProcessProvider,
                EventId = 1,
                Version = 4,
                Delivered = processes,
                Admitted = processes,
                Omitted = 0,
            }],
        ],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Render(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        _ = window.CaptureRenderedFrame();
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
