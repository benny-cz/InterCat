using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §12.1 S5 in the window: a session's size, bytes per record and tier are stated as its generation measured them, and
/// while it records, when its capture's limits stop it - in the words `icat session` and `icat capture` print (R18).
/// </summary>
public sealed class SessionGrowthWindowTests
{
    private static readonly CaptureLimits Explore = new(TimeSpan.FromMinutes(10), 1L << 30, 1L << 30);

    [AvaloniaFact(DisplayName = "§12.1: the window states a session's size, and while it records when its limits stop it, in the command line's words")]
    public void TheWindowStatesASessionsGrowth()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 100, 11) with { SessionRelativeTicks = 2_000 },
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        SessionSize size = overview.Size!;
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        TextBlock growth = window.GetControl<TextBlock>("SessionGrowthText");
        Assert.False(growth.IsVisible);
        Assert.Equal("Session size and growth", AutomationProperties.GetName(growth));

        // A saved session: its size, files, bytes per record and tier, as icat session states them.
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: overview), forceOverview: true);
        Dispatch();
        Assert.True(growth.IsVisible);
        Assert.Equal(SessionGrowth.Statement(size, null), growth.Text);
        Assert.DoesNotContain("caution", growth.Classes);

        // A new capture forgets it until its own first generation measures it.
        window.ForgetDisplayedSession();
        Assert.False(growth.IsVisible);

        // Recording, half a minute before its 10-minute limit: when it stops and what comes after, in caution ink.
        DateTimeOffset began = DateTimeOffset.UtcNow.AddMinutes(-9.5);
        var volume = new RecordingVolume(100L << 30, HoldsSession: true);
        SessionSize measured = size with { Committed = DateTimeOffset.UtcNow };
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording.", SessionPath: session.Path,
            Overview: overview with { Began = began, Size = measured }, Limits: Explore, Volume: volume), forceOverview: true);
        Dispatch();
        Assert.Equal(SessionGrowth.Statement(measured,
            SessionGrowth.Headroom(measured, began, DateTimeOffset.UtcNow, Explore, volume), stopOnItsOwnLine: true), growth.Text);
        Assert.StartsWith(SessionGrowth.Describe(measured) + ".\nStops in under a minute, at its 10-minute limit; at the rate so "
            + "far its journal would reach 1 GiB in about ", growth.Text, StringComparison.Ordinal);
        Assert.Contains("caution", growth.Classes);

        // Stopping, nothing is left to count down: the size stays, in plain ink.
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping", "Stopping.", SessionPath: session.Path,
            Limits: Explore, Volume: volume));
        Dispatch();
        Assert.Equal(SessionGrowth.Statement(measured, null), growth.Text);
        Assert.DoesNotContain("caution", growth.Classes);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§12.1: after an interval release the window says what the session keeps and since when, beneath its size, and states each interval before it as a partial gap whole")]
    public void TheWindowStatesWhatASessionRetains()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2) with { SessionRelativeTicks = 1_000 },
        ]);
        Publish(session.Store,
        [
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3) with { SessionRelativeTicks = 5_000 },
        ], coverage: TransportLedger(tcp: true, udp: false));
        _ = InterCat.Analysis.IntervalRelease.Release(session.Store, 2_000, "older than the retained window", Committed, Committed);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: overview), forceOverview: true);
        Dispatch();

        // Its size first, then what it keeps: the line a person reads to know the interval before it is not quiet.
        TextBlock growth = window.GetControl<TextBlock>("SessionGrowthText");
        Assert.Equal(SessionGrowth.Statement(overview.Size!, null) + "\n" + overview.Retained, growth.Text);
        Assert.StartsWith("Kept from ", overview.Retained, StringComparison.Ordinal);

        // Each interval before the boundary is a partial gap, which its column states whole rather than under the bytes.
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.ShowTables = true;
        Render(window);
        IntervalRow gap = workspace.Intervals.First(row => row.Coverage == CoverageStateText.Value(CoverageState.PartialGap));
        TextBlock[] cells = [.. window.GetControl<ListBox>("IntervalList").ContainerFromItem(gap)!.GetVisualDescendants().OfType<TextBlock>()];
        TextBlock coverage = cells.Single(cell => Grid.GetColumn(cell) == 3);
        TextBlock bytes = cells.Single(cell => Grid.GetColumn(cell) == 4);
        Assert.True(coverage.TextLayout.WidthIncludingTrailingWhitespace <= coverage.Bounds.Width + 0.5,
            $"{gap.Coverage} is {coverage.TextLayout.WidthIncludingTrailingWhitespace} wide in {coverage.Bounds.Width}.");
        Assert.True(coverage.Bounds.Right <= bytes.Bounds.Left, $"{gap.Coverage} runs under {gap.KnownBytes}.");
        window.Close();
    }

    private static void Render(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        _ = window.CaptureRenderedFrame();
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
