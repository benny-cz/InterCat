using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Why a capture failed, at the smallest window (§6.1): the capture card's detail shows a few lines and scrolls for the
/// rest, so it says what a failure means before why, and names a file of the session from the session's folder down,
/// which its path line names whole. A reason naming a file by its whole path filled those lines and left the meaning below.
/// </summary>
public sealed class CaptureCardReasonTests
{
    [AvaloniaFact(DisplayName = "§6.1: the capture card names a file of its session from the session's folder down, and hover keeps the reason whole")]
    public void TheCardNamesAFileOfItsSessionFromItsFolderDown()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 4));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            string chunk = Path.Combine(session.Path, "journal", "chunk-000017.icj");
            string reason = $"The process cannot access the file '{chunk}' because it is being used by another process. It "
                + "failed again each time the follow tried it, 4 more times over 3.75 s. Any published session data remains at "
                + "the path shown below.";
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "8 observed records", DesktopCaptureRunner.Published(1, 1),
                SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture interrupted", reason, SessionPath: session.Path));
            Dispatch();

            string shown = reason.Replace(session.Path, "…", StringComparison.Ordinal);
            Assert.Equal(shown, window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Contains($"'…{Path.DirectorySeparatorChar}journal{Path.DirectorySeparatorChar}chunk-000017.icj'", shown,
                StringComparison.Ordinal);
            Assert.Equal(session.Path, window.GetControl<TextBlock>("CaptureSessionPath").Text);
            Assert.Equal(reason, ToolTip.GetTip(window.GetControl<Button>("StartExploringButton")));
            Assert.Equal(reason, ToolTip.GetTip(window.GetControl<TextBlock>("HealthStateText")));

            // A later update that names no session is read against the folder the card still names.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture interrupted", "Could not find '" + chunk + "'."));
            Dispatch();
            Assert.Equal($"Could not find '…{Path.DirectorySeparatorChar}journal{Path.DirectorySeparatorChar}chunk-000017.icj'.",
                window.GetControl<TextBlock>("CaptureDetail").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.1: a compaction not published and a view not read say what they mean within the capture card's lines at the smallest window")]
    public void WhatAFailureMeansIsWithinTheCardsLines()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 4));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            ScrollViewer lines = window.GetControl<ScrollViewer>("CaptureDetailScroller");
            const string Unverified = "Segment segment-000041-compacted.ics of generation 3 could not be verified: its length "
                + "on disk, 1,048,576 bytes, is not the 2,097,152 bytes its manifest names, and its digest is not the one "
                + "published for it.";

            // A recording whose newest view could not be read: the view shown stays, and the session is kept.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "8 observed records", DesktopCaptureRunner.Published(1, 1),
                SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Capture continues; its newest view could not be read",
                DesktopCaptureRunner.ViewUnread(Unverified, 3, 3), SessionPath: session.Path));
            Within(window, detail, lines, "the session is kept.");

            // A capture saved whose compaction was not published: the session is whole.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Session saved",
                "The capture is closed. All published evidence was followed. " + FollowText.CompactionFailed(Unverified),
                SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            Within(window, detail, lines, "the session is whole,");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Asserts the words end within the detail's lines shown before it is scrolled, at its first line.</summary>
    private static void Within(MainWindow window, TextBlock detail, ScrollViewer lines, string words)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        int end = detail.Text!.IndexOf(words, StringComparison.Ordinal) + words.Length - 1;
        Assert.True(end >= words.Length - 1, $"The detail says \"{words}\": {detail.Text}");
        Assert.Equal(0, lines.Offset.Y);
        Assert.True(lines.Extent.Height > lines.Viewport.Height, "The detail is longer than its lines, so it must say what matters first.");
        double bottom = detail.TextLayout.HitTestTextPosition(end).Bottom;
        Assert.True(bottom <= lines.Viewport.Height + 0.5,
            $"\"{words}\" ends {bottom:F0} px down, below the detail's {lines.Viewport.Height:F0} px of lines.");
    }

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
