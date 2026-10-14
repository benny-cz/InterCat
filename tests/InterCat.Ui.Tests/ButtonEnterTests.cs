using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using InterCat.Analysis.Tests;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Enter opens the chosen row's level from anywhere in the window but a control with an Enter of its own: a button the
/// keyboard is on is pressed, and a list of choices opened, as in every dialog (R15).
/// </summary>
public sealed class ButtonEnterTests
{
    [AvaloniaFact(DisplayName = "R15: Enter on a button or a list of choices the keyboard is on presses or opens it, though a row is chosen in the ranked table")]
    public async Task EnterPressesTheButtonWithTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
        ]);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        List<string> opened = [];
        window.FolderOpener = folder =>
        {
            opened.Add(folder);
            return Task.FromResult(true);
        };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            workspace.SelectedRung = workspace.RungRows.OfType<RungRow>().First();
            int depth = workspace.Crumbs.Count;

            // Open saved session asks which session, rather than the chosen row being descended into.
            choices.Answer(null);
            Assert.True(window.GetControl<Button>("OpenSavedSessionButton").Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => choices.Folders.Count == 1);
            Assert.Equal(depth, workspace.Crumbs.Count);

            // The session's line opens its folder.
            Assert.True(window.GetControl<Button>("SessionFolderButton").Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => opened.Count == 1);
            Assert.Equal(depth, workspace.Crumbs.Count);

            // A list of choices opens.
            ComboBox rankBy = window.GetControl<ComboBox>("RankBySelector");
            Assert.True(rankBy.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.True(rankBy.IsDropDownOpen);
            rankBy.IsDropDownOpen = false;
            Assert.Equal(depth, workspace.Crumbs.Count);

            // From the ranked table, Enter still opens the chosen row's level.
            Control row = window.GetControl<ListBox>("RungList").ContainerFromItem(workspace.SelectedRung!)!;
            Assert.True(row.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(depth + 1, ((WorkspaceViewModel)window.DataContext!).Crumbs.Count);
        }
        finally
        {
            window.Close();
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

    private static void Dispatch() => Dispatcher.UIThread.RunJobs();
}
