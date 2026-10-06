using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// §6.1's collapse floor: either main pane may fill the column, and only by a person's explicit command - its header's
/// Expand, or F11 with the keyboard in it - and the same command gives both their places back at the split the person
/// left.
/// </summary>
public sealed class PaneExpandWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§6.1: either main pane fills the column by an explicit command, and the same command gives both their places back at the split the person left")]
    public async Task EitherPaneFillsTheColumnByCommand()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            .. Enumerable.Range(0, 10).SelectMany(index => new[]
            {
                Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(10 + (2 * index)))
                    .Between(ClientEnd, ServerEnd)),
                Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                    (ulong)(11 + (2 * index))).Between(ServerEnd, ClientEnd)),
            }),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
        Grid panes = window.GetControl<Grid>("PanesGrid");
        Border graphPane = window.GetControl<Border>("GraphPane");
        Border timelinePane = window.GetControl<Border>("TimelinePane");
        ToggleButton graphExpand = window.GetControl<ToggleButton>("GraphExpandToggle");
        ToggleButton timelineExpand = window.GetControl<ToggleButton>("TimelineExpandToggle");

        // The two share the column, and the person drags the split to give the graph two thirds of it.
        Render(window);
        Assert.Null(window.ExpandedPane);
        Assert.Equal(graphPane.Bounds.Height, timelinePane.Bounds.Height, 1);
        panes.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
        Render(window);
        double left = graphPane.Bounds.Height;
        Assert.True(left > timelinePane.Bounds.Height * 1.5);

        // Expand: the graph fills the column, the timeline hidden one click away.
        graphExpand.IsChecked = true;
        Render(window);
        Assert.Equal(MainPane.Graph, window.ExpandedPane);
        Assert.False(timelinePane.IsEffectivelyVisible);
        Assert.False(window.GetControl<GridSplitter>("PaneSplitter").IsEffectivelyVisible);
        Assert.Equal(panes.Bounds.Height, graphPane.Bounds.Height, 1);

        // The same command gives both their places back, at the split the person left.
        graphExpand.IsChecked = false;
        Render(window);
        Assert.Null(window.ExpandedPane);
        Assert.True(timelinePane.IsEffectivelyVisible);
        Assert.Equal(left, graphPane.Bounds.Height, 1);

        // F11 with the keyboard in the timeline lets it fill the column, its toggle saying so; F11 again restores, wherever
        // the keyboard went meanwhile.
        window.GetControl<TimelineView>("TimelineSurface").Focus();
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        Render(window);
        Assert.Equal(MainPane.Timeline, window.ExpandedPane);
        Assert.True(timelineExpand.IsChecked);
        Assert.False(graphPane.IsEffectivelyVisible);
        Assert.Equal(panes.Bounds.Height, timelinePane.Bounds.Height, 1);
        Save(window, "timeline-expanded-1456x939.png");
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        Assert.Empty(LegibleTextTests.CutOff(window, "the timeline filling the column, at the smallest window"));
        (window.Width, window.Height) = (1456, 939);
        window.GetControl<TextBox>("SearchBox").Focus();
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        Render(window);
        Assert.Null(window.ExpandedPane);
        Assert.False(timelineExpand.IsChecked);
        Assert.True(graphPane.IsEffectivelyVisible);

        // With the keyboard in neither pane, F11 lets none fill the column: no pane is hidden but by a person's command.
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        Assert.Null(window.ExpandedPane);
        window.Close();
    }

    private static void Render(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Render(window);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
