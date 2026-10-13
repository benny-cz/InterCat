using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The main window is named by what it shows, as switching between windows lists it and a screen reader says it when
/// the window comes to the front (R15): the session's folder, and a capture's state while one runs.
/// </summary>
public sealed class WindowTitleTests
{
    [AvaloniaFact(DisplayName = "R15: the main window is named by the session it shows, and by the capture's state while one runs")]
    public async Task TheWindowIsNamedByWhatItShows()
    {
        using var recorded = new TemporarySession();
        using var saved = new TemporarySession();
        Publish(saved.Store, Exchange());
        string recording = Path.GetFileName(recorded.Path);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Assert.Equal(MainWindow.ProductTitle, window.Title);

            // A capture is named as it starts, records and saves its session, then by the session it saved.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Starting Explore", "Asking the broker to record."));
            Assert.Equal("InterCat · Starting a capture", window.Title);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Assert.Equal("InterCat · Recording", window.Title);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording.",
                SessionPath: recorded.Path));
            Assert.Equal($"InterCat · Recording {recording}", window.Title);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping and keeping the session", "Finalizing.",
                SessionPath: recorded.Path));
            Assert.Equal($"InterCat · Saving {recording}", window.Title);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Session saved", "Saved.", SessionPath: recorded.Path));
            Assert.Equal($"InterCat · {recording}", window.Title);

            // A session opened is named by its folder, given with a separator at its end or not.
            Assert.True(await window.OpenSessionAsync(saved.Path + Path.DirectorySeparatorChar));
            Assert.Equal($"InterCat · {Path.GetFileName(saved.Path)}", window.Title);

            // A new capture forgets the session shown; one that could not start leaves the window named as before any.
            window.ForgetDisplayedSession();
            Assert.Equal(MainWindow.ProductTitle, window.Title);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Starting Explore", "Asking the broker to record."));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Explore did not start", "Declined."));
            Assert.Equal(MainWindow.ProductTitle, window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    private static ObservationRowV1[] Exchange() =>
    [
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
        Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 101)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
