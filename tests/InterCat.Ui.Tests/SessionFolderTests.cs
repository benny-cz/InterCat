using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InterCat.Analysis.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Open folder, beside the card's line naming the session shown, opens the session's folder in the system's file
/// manager, and the line's menu copies the path whole, which the line may shorten (§3.1).
/// </summary>
public sealed class SessionFolderTests
{
    [AvaloniaFact(DisplayName = "§3.1: Open folder beside the session's line opens its folder, by the keyboard too, and the line's menu copies the path whole")]
    public async Task TheSessionLineOpensTheFolderAndCopiesThePath()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)]);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        List<string> opened = [];
        window.FolderOpener = folder =>
        {
            opened.Add(folder);
            return Task.FromResult(true);
        };
        window.Show();
        try
        {
            // Before a session is shown there is no folder to open.
            Button open = window.GetControl<Button>("SessionFolderButton");
            Assert.False(open.IsEffectivelyVisible);

            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            Assert.True(open.IsEffectivelyVisible);
            Assert.Equal("Open folder", ControlAutomationPeer.CreatePeerForElement(open).GetName());
            Assert.Equal("Open the session's folder in the file manager; its menu copies the path",
                AutomationProperties.GetHelpText(open));

            // It opens the folder, by the pointer and by the keyboard, and says nothing over the card.
            string detail = window.GetControl<TextBlock>("CaptureDetail").Text!;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(open.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => opened.Count == 2);
            Assert.Equal([session.Path, session.Path], opened);
            Assert.Equal(detail, window.GetControl<TextBlock>("CaptureDetail").Text);

            // The line's menu opens it too, and copies the path, which the card says.
            Item(open, "OpenSessionFolderItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => opened.Count == 3);
            Item(open, "CopySessionPathItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TextBlock detailLine = window.GetControl<TextBlock>("CaptureDetail");
            await Until(() => detailLine.Text == "The session's path is copied: " + session.Path);
            Assert.Equal(session.Path, await window.Clipboard!.TryGetTextAsync());

            // A folder the system would not open, or could not, says so.
            window.FolderOpener = _ => Task.FromResult(false);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(window) == "The session's folder was not opened");
            Assert.Equal(session.Path + " could not be opened: the system did not open it.",
                window.GetControl<TextBlock>("CaptureDetail").Text);
            window.FolderOpener = _ => throw new IOException("No file manager is registered.");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.GetControl<TextBlock>("CaptureDetail").Text
                == session.Path + " could not be opened: No file manager is registered.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a session's folder gone since it was opened is not opened, and the card says so")]
    public async Task AFolderGoneSinceSaysSo()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)]);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        List<string> opened = [];
        window.FolderOpener = folder =>
        {
            opened.Add(folder);
            return Task.FromResult(true);
        };
        window.Show();
        string moved = session.Path + "-moved";
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            Directory.Move(session.Path, moved);
            Button open = window.GetControl<Button>("SessionFolderButton");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(window) == "The session's folder was not opened");
            Assert.Equal(session.Path + " could not be opened: it is no longer there, moved or deleted.",
                window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Empty(opened);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(moved)) Directory.Move(moved, session.Path);
        }
    }

    private static string? Status(Window window) => window.GetControl<TextBlock>("CaptureStatus").Text;

    /// <summary>An item of the menu the session's line and its Open folder button share.</summary>
    private static MenuItem Item(Control button, string name) =>
        ((Control)button.Parent!).ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 200 && !condition(); wait++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Dispatcher.UIThread.RunJobs();
}
