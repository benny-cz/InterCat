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
/// The keys the keys sheet says act anywhere type nothing, so they act from the window's search box too, as F1,
/// Ctrl+F, F6 and F11 do; a letter typed there is text (R15).
/// </summary>
public sealed class SearchBoxKeysTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R15: Ctrl+R, Ctrl+E and Ctrl+O act from the search box as from anywhere, typing nothing there")]
    public async Task CommandKeysActFromTheSearchBox()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 101)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ]);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            TextBox search = window.GetControl<TextBox>("SearchBox");
            Assert.True(search.Focus());

            // A letter typed there is text, the E of "net" too, not the key E is elsewhere: nothing is asked or shown.
            var workspace = (WorkspaceViewModel)window.DataContext!;
            int depth = workspace.Crumbs.Count;
            Type(window, "net");
            Dispatch();
            Assert.Equal("net", search.Text);
            Assert.Equal(0, choices.Asked);
            Assert.Equal(depth, workspace.Crumbs.Count);

            // Ctrl+E asks where to export the view, and Ctrl+O which session to open; neither types in the box.
            choices.Answer(null);
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Control);
            await Until(() => choices.Saves.Count == 1);
            Assert.True(search.IsFocused);
            choices.Answer(null);
            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
            await Until(() => choices.Folders.Count == 1);
            Assert.Equal("net", search.Text);

            // Ctrl+R starts the capture chosen: here, a content capture, whose processes are chosen first.
            window.GetControl<ComboBox>("CaptureKeepSelector").SelectedItem = CaptureKeepChoice.All[^1];
            Assert.True(search.Focus());
            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
            await Until(() => window.OwnedWindows.OfType<ContentCaptureWindow>().Any());
            window.OwnedWindows.OfType<ContentCaptureWindow>().Single().Close();
            Dispatch();
            Assert.Null(window.CaptureRun);
            Assert.Equal("net", search.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: F5, Alt+Left and Alt+Right act from the search box as from anywhere, typing nothing there")]
    public async Task RefreshAndHistoryKeysActFromTheSearchBox()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 20));
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Update(session));
            Dispatch();
            var reading = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            int depth = reading.Crumbs.Count;
            window.GetControl<ListBox>("RungList").Focus();
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            await reading.EvidenceReady;
            Dispatch();
            Assert.Equal(depth + 1, reading.Crumbs.Count);

            // A publication is held while records are read; F5 shows it from the search box, which keeps its words.
            Publish(session.Store, Exchange(20, 5));
            window.ApplyCaptureUpdate(Update(session));
            Dispatch();
            Assert.Same(reading, window.DataContext);
            Assert.True(window.GetControl<Border>("HeldBanner").IsVisible);
            TextBox search = window.GetControl<TextBox>("SearchBox");
            Assert.True(search.Focus());
            Type(window, "net");
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
            Dispatch();
            var refreshed = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.NotSame(reading, refreshed);
            Assert.True(refreshed.IsEvidenceRung);
            Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
            Assert.True(search.IsKeyboardFocusWithin);
            Assert.Equal("net", search.Text);

            // Left and Right without Alt are the box's own: they move its caret, and the view stays where it is.
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(2, search.CaretIndex);
            Assert.Equal(depth + 1, refreshed.Crumbs.Count);

            // Alt+Left goes back a level from there, and Alt+Right forward again, the search keeping its words.
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
            Dispatch();
            Assert.Equal(depth, refreshed.Crumbs.Count);
            Assert.False(refreshed.IsEvidenceRung);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(3, search.CaretIndex);
            Assert.Equal(depth, refreshed.Crumbs.Count);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            Dispatch();
            Assert.Equal(depth + 1, refreshed.Crumbs.Count);
            Assert.True(refreshed.IsEvidenceRung);
            Assert.True(search.IsKeyboardFocusWithin);
            Assert.Equal("net", search.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// <paramref name="count"/> exchanges from the <paramref name="first"/>: a client's send and the server's receipt.
    /// </summary>
    private static ObservationRowV1[] Exchange(int first, int count) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index =>
        {
            int ticks = 10 + (2 * index);
            ulong ordinal = (ulong)(90 + ticks);
            return new[]
            {
                Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, 100, ordinal)
                    .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = ticks * 100L },
                Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, ordinal + 1)
                    .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (ticks + 1) * 100L },
            };
        }),
    ];

    /// <summary>Types as a keyboard does: a letter's key goes down, the letter is typed, the key comes up.</summary>
    private static void Type(Window window, string text)
    {
        foreach (char letter in text)
        {
            PhysicalKey key = Enum.Parse<PhysicalKey>(letter.ToString(), ignoreCase: true);
            window.KeyPressQwerty(key, RawInputModifiers.None);
            window.KeyTextInput(letter.ToString());
            window.KeyReleaseQwerty(key, RawInputModifiers.None);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 200 && !condition(); wait++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
