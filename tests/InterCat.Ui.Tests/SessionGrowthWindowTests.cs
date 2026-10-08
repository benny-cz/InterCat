using System.Globalization;
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
using InterCat.Storage;
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

    [AvaloniaFact(DisplayName = "S5: while a capture keeping a window records, the window projects its stop from what the window holds, and once a pin keeps the session past it, from when the pin stops it")]
    public async Task TheWindowProjectsARollingCapturesStop()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10) with { SessionRelativeTicks = 1_000 },
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            // An hour into a day-long capture keeping its last ten minutes, the session kept from 3,000 s holds 60 MiB of
            // journal and 90 MiB in all: the window fits its journal and the disk, so only its length stops it.
            DateTimeOffset began = DateTimeOffset.UtcNow.AddHours(-1);
            SessionSize measured = overview.Size! with
            {
                Bytes = 90L << 20, JournalBytes = 60L << 20, Committed = began.AddHours(1),
                RetainedFromNanoseconds = 3_000_000_000_000,
            };
            var rolling = new CaptureLimits(TimeSpan.FromHours(24), 1L << 30, 1L << 30,
                new CaptureWindow(new RollingRetentionPolicy(TimeSpan.FromMinutes(10)), TimeSpan.FromSeconds(12)));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording.", SessionPath: session.Path,
                Overview: overview with { Began = began, Size = measured }, Limits: rolling,
                Volume: new RecordingVolume(100L << 30, HoldsSession: true)), forceOverview: true);
            Dispatch();
            TextBlock growth = window.GetControl<TextBlock>("SessionGrowthText");
            Assert.Equal(SessionGrowth.Describe(measured) + ".\nStops in about 23 h, at its 24-hour limit; at the rate so far "
                + "keeping the last 10 minutes takes up to about 76.2 MiB of its 1 GiB journal, and free disk stays above its 1 GiB "
                + "reserve.", growth.Text);

            // A pin placed 20 s back allowing what the session holds keeps it past its window from there, and the size line
            // says at once when the follow stops the capture for it.
            RetentionPin pin = Assert.IsType<RetentionPin>(await window.PlacePinAsync(3_580_000_000_000, 90L << 20, "the failover"));
            Dispatch();
            Assert.StartsWith(SessionGrowth.Describe(measured) + ".\nStops in about 10 min at the rate so far, when the session "
                + "outgrows the 90 MiB the pin from " + SessionTimeText.Seconds(pin.FromNanoseconds, CultureInfo.CurrentCulture)
                + " allows; its journal would reach 1 GiB in about 2 h 50 min, ", growth.Text, StringComparison.Ordinal);
            Assert.EndsWith("\n" + RetentionPinText.SizeLine([pin]), growth.Text, StringComparison.Ordinal);

            // The next generation states the pins it was projected with, and removed, the window bounds the session again.
            Assert.Equal([pin], SessionOverviewProjector.Project(session.Store).Pins);
            session.Store.ReleaseSegmentReaders();
            Assert.Equal(pin, await window.RemovePinAsync(pin));
            Dispatch();
            Assert.Contains("keeping the last 10 minutes takes up to about 76.2 MiB", growth.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§12.1: after an interval release the window says what the session keeps and since when, beneath its size, and its intervals begin where every record is kept")]
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
        long boundary = InterCat.Analysis.IntervalRelease.Release(session.Store, 2_000, "older than the retained window", Committed,
            Committed).Preview.BoundaryNanoseconds!.Value;
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: overview), forceOverview: true);
            Dispatch();

            // Its size first, then what it keeps: the line a person reads to know the interval before it is not quiet.
            TextBlock growth = window.GetControl<TextBlock>("SessionGrowthText");
            Assert.Equal(SessionGrowth.Statement(overview.Size!, null) + "\n" + overview.Retained, growth.Text);
            Assert.StartsWith("Kept from ", overview.Retained, StringComparison.Ordinal);
            Assert.Equal(boundary, overview.Size!.RetainedFromNanoseconds);

            // The timeline, and the interval table beside it, begin at the first tick wholly after the boundary: no interval
            // reaches into what was released, so none is a gap for it, and each states its coverage whole.
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.Equal((boundary + 99) / 100, workspace.Snapshot.Extent.StartTicks);
            workspace.ShowTables = true;
            Render(window);
            Assert.NotEmpty(workspace.Intervals);
            Assert.DoesNotContain(workspace.Intervals, row => row.Coverage == CoverageStateText.Value(CoverageState.PartialGap));
            IntervalRow first = workspace.Intervals[0];
            TextBlock[] cells = [.. window.GetControl<ListBox>("IntervalList").ContainerFromItem(first)!.GetVisualDescendants().OfType<TextBlock>()];
            TextBlock coverage = cells.Single(cell => Grid.GetColumn(cell) == 3);
            TextBlock bytes = cells.Single(cell => Grid.GetColumn(cell) == 4);
            Assert.True(coverage.TextLayout.WidthIncludingTrailingWhitespace <= coverage.Bounds.Width + 0.5,
                $"{first.Coverage} is {coverage.TextLayout.WidthIncludingTrailingWhitespace} wide in {coverage.Bounds.Width}.");
            Assert.True(coverage.Bounds.Right <= bytes.Bounds.Left, $"{first.Coverage} runs under {first.KnownBytes}.");
        }
        finally
        {
            window.Close();
        }
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
