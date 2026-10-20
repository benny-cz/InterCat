using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Page Down and Page Up in a list move the keyboard a page and choose the row they come to, as Down and Up do (R15): they
/// only scrolled the rows, leaving the keyboard on a row out of sight that the next arrow scrolled back to.
/// </summary>
public sealed class ListPagesTests
{
    [AvaloniaFact(DisplayName = "R15: Page Down and Page Up move the keyboard a page through the ranked table, choosing the row they come to, and stay at its ends")]
    public async Task PageKeysMoveThroughTheRankedTable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, GraphLabelLeaderTests.BusyMachine());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            ListBox rail = window.GetControl<ListBox>("RungList");
            _ = window.CaptureRenderedFrame();
            Dispatch();
            Assert.True(rail.ContainerFromIndex(0)!.Focus());
            Dispatch();
            void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPressQwerty(key, modifiers);
                Dispatch();
                _ = window.CaptureRenderedFrame();
            }

            // From the top, Page Down comes to the last row the table shows whole, and from there a page further, as many
            // rows on as it shows less the one kept in sight - each chosen, in sight and with the keyboard.
            int[] shown = Whole(rail);
            Assert.True(shown is [0, _, _, ..], string.Join(", ", shown));
            Press(PhysicalKey.PageDown);
            AssertOn(rail, workspace, shown[^1]);
            Press(PhysicalKey.PageDown);
            AssertOn(rail, workspace, shown[^1] + shown.Length - 1);

            // Up a row, Page Up comes to the first row in sight, and from there a page further up.
            Press(PhysicalKey.ArrowUp);
            shown = Whole(rail);
            Assert.True(shown[0] < rail.SelectedIndex, string.Join(", ", shown));
            Press(PhysicalKey.PageUp);
            AssertOn(rail, workspace, shown[0]);
            shown = Whole(rail);
            Press(PhysicalKey.PageUp);
            AssertOn(rail, workspace, Math.Max(0, shown[0] - (shown.Length - 1)));

            // From a row within what is shown, Page Down comes to the last row shown, not a page past it.
            Press(PhysicalKey.Home);
            Press(PhysicalKey.ArrowDown);
            shown = Whole(rail);
            Press(PhysicalKey.PageDown);
            AssertOn(rail, workspace, shown[^1]);

            // At the ends they stay; with a modifier, which a window may take for its own, they choose nothing.
            Press(PhysicalKey.End);
            Press(PhysicalKey.PageDown);
            AssertOn(rail, workspace, rail.ItemCount - 1);
            Press(PhysicalKey.Home);
            Press(PhysicalKey.PageUp);
            AssertOn(rail, workspace, 0);
            Press(PhysicalKey.PageDown, RawInputModifiers.Control);
            Assert.Equal((0, 0), (rail.SelectedIndex, Keyboard(rail)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: Page Down and Page Up move the keyboard a page through the keys sheet's rows, each a height of its own")]
    public void PageKeysMoveThroughTheKeysSheet()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            Dispatch();
            Window sheet = Assert.IsAssignableFrom<Window>(window.KeysSheet);
            ListBox keys = Assert.IsType<KeysWindow>(sheet).List;
            _ = sheet.CaptureRenderedFrame();
            Dispatch();
            Assert.Equal(0, Keyboard(keys));
            int[] shown = Whole(keys);
            sheet.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Dispatch();
            _ = sheet.CaptureRenderedFrame();
            Assert.Equal((shown[^1], shown[^1]), (keys.SelectedIndex, Keyboard(keys)));
            sheet.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Dispatch();
            _ = sheet.CaptureRenderedFrame();
            Assert.True(keys.SelectedIndex > shown[^1], $"{keys.SelectedIndex}");
            Assert.Equal(keys.SelectedIndex, Keyboard(keys));
            Assert.Contains(keys.SelectedIndex, Whole(keys));
            sheet.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(keys.SelectedIndex, Keyboard(keys));
            Assert.True(keys.SelectedIndex < shown[^1] + shown.Length, $"{keys.SelectedIndex}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The rows <paramref name="list"/> shows whole, in order.</summary>
    private static int[] Whole(ListBox list)
    {
        ScrollViewer scroll = list.FindDescendantOfType<ScrollViewer>()!;
        return [.. list.GetRealizedContainers()
            .Where(row => row.IsVisible && row.TranslatePoint(default, scroll) is { } top
                && top.Y >= -0.5 && top.Y + row.Bounds.Height <= scroll.Viewport.Height + 0.5)
            .Select(list.IndexFromContainer)
            .Where(index => index >= 0)
            .Order()];
    }

    /// <summary>The row of <paramref name="list"/> the keyboard is on, or -1.</summary>
    private static int Keyboard(ListBox list) =>
        TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is ListBoxItem row ? list.IndexFromContainer(row) : -1;

    /// <summary>The ranked table is on row <paramref name="index"/>: chosen there and in the workspace, in sight, with the keyboard.</summary>
    private static void AssertOn(ListBox rail, WorkspaceViewModel workspace, int index)
    {
        Assert.Equal(index, rail.SelectedIndex);
        Assert.Equal(index, Keyboard(rail));
        Assert.Same(rail.SelectedItem, workspace.SelectedRung);
        Assert.Contains(index, Whole(rail));
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
