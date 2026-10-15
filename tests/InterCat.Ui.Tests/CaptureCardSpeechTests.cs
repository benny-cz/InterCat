using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// What the capture card says aloud (R15): what changes the capture's state and what a person's action did, never a
/// recording's count alone, which each newer generation restates.
/// </summary>
public sealed class CaptureCardSpeechTests
{
    private const long Mebibyte = 1024 * 1024;

    [AvaloniaFact(DisplayName = "R15: the capture card says aloud what changes the capture's state, and a newer generation's count in silence")]
    public void TheCardSaysTheCapturesStateNotItsCount()
    {
        using var session = new TemporarySession();
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            List<string?> heard = Heard(status, detail);

            // A capture starts and records, says so, and its first three generations arrive: their counts are shown, and
            // said aloud by none.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Starting Explore", "Asking the broker to record."));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Raw evidence is broker-owned.",
                SessionPath: session.Path));
            for (int generation = 1; generation <= 3; generation++)
            {
                Publish(session.Store, Exchange(generation * 10, 5));
                window.ApplyCaptureUpdate(Generation(session, CaptureUiPhase.Recording, generation));
                Dispatch();
            }

            Assert.Equal("30 observed records", status.Text);
            Assert.Equal("Generation 3 · 3 published chunks.", detail.Text);

            // Stopping is said, and so is the saved session, while the generation that arrives as it stops is shown alone.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping and keeping the session",
                "The broker is finalizing.", SessionPath: session.Path));
            Publish(session.Store, Exchange(40, 5));
            window.ApplyCaptureUpdate(Generation(session, CaptureUiPhase.Finishing, 4));
            Dispatch();
            Assert.Equal("40 observed records", status.Text);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Session saved", "The capture is closed.",
                SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
            Dispatch();
            Assert.Equal(
                ["Starting Explore", "Asking the broker to record.", "Recording · follow latest", "Raw evidence is broker-owned.",
                    "Stopping and keeping the session", "The broker is finalizing.", "Session saved", "The capture is closed."],
                heard);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(detail));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a recording's newer generation leaves the card's detail where it is read, and a change of state shows its first line")]
    public void ANewerGenerationLeavesTheDetailWhereItIsRead()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 5));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            ScrollViewer detail = window.GetControl<ScrollViewer>("CaptureDetailScroller");

            // A recording whose detail is longer than its lines: the person scrolls it to its end, and a newer generation,
            // restated in silence, leaves it there, where stopping, said aloud, shows its first line.
            window.ApplyCaptureUpdate(Generation(session, CaptureUiPhase.Recording, 1, Keeps));
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Dispatch();
            Assert.True(detail.Extent.Height > detail.Viewport.Height,
                $"The detail fits, so none of it needed to scroll: {detail.Extent.Height:F0} px in {detail.Viewport.Height:F0}.");
            double reading = detail.Extent.Height - detail.Viewport.Height;
            detail.Offset = new(0, reading);
            Publish(session.Store, Exchange(10, 5));
            window.ApplyCaptureUpdate(Generation(session, CaptureUiPhase.Recording, 2, Keeps));
            Dispatch();
            Assert.Equal(reading, detail.Offset.Y);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping and keeping the session",
                "The broker is finalizing. " + Keeps, SessionPath: session.Path));
            Dispatch();
            Assert.Equal(0, detail.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: what an action did is said aloud: a pin placed from the inspector says what it keeps")]
    public async Task WhatAnActionDidIsSaidAloud()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 5));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            TextBlock said = window.GetControl<TextBlock>("CaptureDetail");
            List<string?> heard = Heard(window.GetControl<TextBlock>("CaptureStatus"), said);
            MainWindow.PinOffer offer = Assert.IsType<MainWindow.PinOffer>(await window.PinOfferAsync());
            Assert.NotNull(await window.PlacePinAsync(offer.FromNanoseconds, 64 * Mebibyte, "the first exchange"));
            Dispatch();
            Assert.StartsWith("Every record read from ", said.Text, StringComparison.Ordinal);
            Assert.Equal(["Records kept", said.Text], heard);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a package's progress, redacted or exact, is shown by the percent, and said aloud only as it starts and as it ends")]
    public async Task APackagesProgressIsShownNotSaid()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 50));
        string packages = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages");
        string destination = MainWindow.NewPackageDirectory(packages, DateTimeOffset.Now);
        string original = Path.Combine(packages, "original-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            TextBlock said = window.GetControl<TextBlock>("CaptureDetail");
            var shown = new List<string?>();
            said.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextBlock.TextProperty) shown.Add(said.Text);
            };
            List<string?> heard = Heard(status, said);
            Assert.NotNull(await window.WriteRedactedPackageAsync(session.Path, destination));
            Dispatch();
            Assert.Contains(shown, line => line?.EndsWith('%') == true);
            Assert.Equal(["Creating redacted session package", "Reading the session's rows…", "Redacted session package saved",
                said.Text], heard);

            // So is an exact copy's.
            shown.Clear();
            heard.Clear();
            Assert.NotNull(await window.WriteOriginalPackageAsync(session.Path, session.Store.SessionId, original));
            Dispatch();
            Assert.Contains(shown, line => line?.EndsWith('%') == true);
            Assert.Equal(["Saving the original session", "Copying and checking the session's files…", "Original session saved",
                said.Text], heard);
        }
        finally
        {
            window.Close();
            foreach (string written in (string[])[destination, original])
            {
                if (Directory.Exists(written)) Directory.Delete(written, recursive: true);
            }
        }
    }

    [AvaloniaFact(DisplayName = "R15: a rolling capture's newer generation says in silence that its release clipped a brush reaching into what it gave up")]
    public async Task ARollingReleaseClipsAViewInSilence()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        Publish(session.Store, Exchange(100, 10));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Generation(session, CaptureUiPhase.Recording, 1));
            var first = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await first.LayoutReady;
            Dispatch();

            // The person opens the first group and brushes the capture's first moments.
            window.GetControl<ListBox>("RungList").Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("L1 · GROUP", first.LevelBadge);
            TimeRange extent = first.Snapshot.Extent;
            first.SelectInterval(new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 10)));
            await first.IntervalReady;
            Dispatch();

            // The capture keeps its newest stretch, releasing what is older, so its next generation cannot keep all the
            // person brushed: it says so beside the generation's chunks, and in silence, as it says them.
            _ = IntervalRelease.Release(session.Store, 10_000, "older than the retained window", DateTimeOffset.UtcNow);
            Publish(session.Store, Exchange(110, 10));
            TextBlock said = window.GetControl<TextBlock>("CaptureDetail");
            List<string?> heard = Heard(window.GetControl<TextBlock>("CaptureStatus"), said);
            window.ApplyCaptureUpdate(Generation(session, CaptureUiPhase.Recording, 2));
            Dispatch();
            Assert.Equal("Generation 2 · 2 published chunks. Part of the analysis interval lay in time the session released, "
                + "so it now covers the rest.", said.Text);
            Assert.Empty(heard);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// What a screen reader says of <paramref name="lines"/> from now on: each text a line takes while it is live, as a
    /// live region's change of name is announced.
    /// </summary>
    private static List<string?> Heard(params TextBlock[] lines)
    {
        var heard = new List<string?>();
        foreach (TextBlock line in lines)
        {
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(line);
            peer.PropertyChanged += (_, change) =>
            {
                if (change.Property == AutomationElementIdentifiers.NameProperty && peer.GetLiveSetting() != AutomationLiveSetting.Off)
                {
                    heard.Add(peer.GetName());
                }
            };
        }

        return heard;
    }

    /// <summary>What a rolling capture's detail says it keeps, at more length than the card's detail has lines for.</summary>
    private const string Keeps = "The session keeps the newest ten minutes of session time and releases what is older "
        + "between publications, unless a pin keeps it. The graph relates processes by paired TCP only and draws their "
        + "connections to other hosts; other observations remain in the timeline. Unobserved activity is not zero, and a "
        + "gap in coverage is stated where it falls.";

    /// <summary>A recording's newer generation, which restates its count and chunks.</summary>
    private static CaptureUiUpdate Generation(TemporarySession session, CaptureUiPhase phase, int generation,
        string? more = null) => new(phase, $"{generation * 10} observed records",
        DesktopCaptureRunner.Published(generation, generation) + (more is null ? string.Empty : " " + more),
        SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times from <paramref name="first"/>.</summary>
    private static ObservationRowV1[] Exchange(int first, int count) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
