using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
/// The keys sheet (§6.7, R15): F1, from anywhere, or the strip's Keys button lists every key the window takes, by where it
/// acts, one row a screen reader reads at a time; Esc or F1 closes it and gives the keyboard back where it was.
/// </summary>
public sealed class KeysSheetTests
{
    [AvaloniaFact(DisplayName = "R15: F1 lists the window's keys by where they act, each row named for a screen reader, and Esc gives the keyboard back")]
    public async Task F1ListsTheWindowsKeys()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();

        // F6 gives the ranked table the keyboard on its first row, which nothing has chosen yet; F1 opens the sheet, whose
        // list has the keyboard on its first row.
        ListBox rail = window.GetControl<ListBox>("RungList");
        Press(window, PhysicalKey.F6);
        Control row = Assert.IsAssignableFrom<Control>(window.FocusManager?.GetFocusedElement());
        Assert.True(rail.IsVisualAncestorOf(row), Focused(window));
        Assert.Null(workspace.SelectedRung);
        Press(window, PhysicalKey.F1);
        KeysWindow sheet = Assert.IsType<KeysWindow>(window.KeysSheet);
        Assert.True(sheet.IsVisible);
        Assert.Equal("Keys", sheet.Title);
        ListBox keys = sheet.List;
        Assert.Same(keys.ContainerFromIndex(0), sheet.FocusManager?.GetFocusedElement());
        Save(sheet.CaptureRenderedFrame()!, "keys-sheet.png");

        // Each place's first row heads it, and a screen reader hears the place as it enters it, then each key and what it
        // does; symbols are said in words.
        Assert.Equal("Anywhere. F1: list the keys this window takes.", Name(keys, 0));
        Assert.Equal("F6 or Shift+F6: move the keyboard to the next or previous pane: the ranked table, the graph, the "
            + "timeline and the inspector, or the tables while they are shown.", Name(keys, 1));
        IReadOnlyList<WindowKey> listed = WindowKeys.All;
        int timeline = listed.ToList().FindIndex(key => key.Where == "Timeline");
        Assert.True(listed[timeline].Heads);
        Assert.False(listed[timeline + 1].Heads);
        int step = listed.ToList().FindIndex(key => key.Keys == "[ or ]");
        Assert.Equal("Left or right bracket: step to the previous or next moment holding records.", Name(keys, step));
        Assert.Equal(["Anywhere", "Ranked table", "Graph", "Timeline", "Minimap", "Relationship table", "Interval table",
            "Search box", "Inspector", "A level's records"], listed.Where(key => key.Heads).Select(key => key.Where));
        Assert.Equal(["TIMELINE", "Left or Right", "Pan by a tenth of the view; with Shift, by one cell"],
            Texts(keys.ContainerFromIndex(timeline) ?? Reveal(keys, timeline)!));
        Assert.Equal(["+ or -", "Zoom in or out around the analysis interval"],
            Texts(keys.ContainerFromIndex(timeline + 1) ?? Reveal(keys, timeline + 1)!));

        // Asked for again while it is open, it stays the one sheet.
        await Task.WhenAny(window.ShowKeysAsync(), Task.Delay(0));
        Dispatch();
        Assert.Same(sheet, window.KeysSheet);
        Assert.Single(window.OwnedWindows.OfType<KeysWindow>());

        // Down reads on; Esc closes the sheet and gives the keyboard back to the row it was on, still not chosen.
        sheet.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatch();
        Assert.Same(keys.ContainerFromIndex(1), sheet.FocusManager?.GetFocusedElement());
        sheet.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(window.KeysSheet);
        Assert.False(sheet.IsVisible);
        Assert.Same(row, window.FocusManager?.GetFocusedElement());
        Assert.Null(workspace.SelectedRung);

        // F1 types nothing in the search box, so it opens the sheet from there too, and F1 closes it, the search kept.
        Press(window, PhysicalKey.F, RawInputModifiers.Control);
        TextBox search = window.GetControl<TextBox>("SearchBox");
        search.Text = "client";
        Dispatch();
        Press(window, PhysicalKey.F1);
        sheet = Assert.IsType<KeysWindow>(window.KeysSheet);
        sheet.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
        Dispatch();
        Assert.Null(window.KeysSheet);
        Assert.True(search.IsFocused, Focused(window));
        Assert.Equal("client", search.Text);

        // The strip's Keys button opens the same sheet, for a pointer as for a screen reader's invoke.
        Button button = window.GetControl<Button>("KeysButton");
        Assert.Equal("Keys (F1)", button.Content);
        Assert.Equal("List every key this window takes, by where it acts", AutomationProperties.GetHelpText(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        sheet = Assert.IsType<KeysWindow>(window.KeysSheet);
        sheet.Close();
        Dispatch();
        Assert.Null(window.KeysSheet);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: before a session opens F1 lists the keys too, and gives the keyboard back to Start exploring")]
    public void F1ListsTheKeysBeforeASessionOpens()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        Dispatch();
        Button start = window.GetControl<Button>("StartExploringButton");
        Assert.True(start.IsFocused, Focused(window));
        Press(window, PhysicalKey.F1);
        KeysWindow sheet = Assert.IsType<KeysWindow>(window.KeysSheet);
        sheet.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(window.KeysSheet);
        Assert.True(start.IsFocused, Focused(window));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: at the minimum window size the strip's Keys button is whole, and the coverage line gives way to it")]
    public void TheKeysButtonFitsTheStripAtTheMinimumSize()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Button button = window.GetControl<Button>("KeysButton");
        Assert.True(button.IsEffectivelyVisible);
        Save(window.CaptureRenderedFrame()!, "keys-button-1080x700.png");
        Rect drawn = new(button.TranslatePoint(default, window)!.Value, button.Bounds.Size);
        Assert.True(drawn.Right <= window.Bounds.Width && drawn.Bottom <= window.Bounds.Height, $"The button is drawn at {drawn}.");
        TextBlock face = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>());
        Assert.True(face.DesiredSize.Width <= face.Bounds.Width + 0.5, "The button's face is cut.");
        TextBlock coverage = window.GetControl<TextBlock>("CoverageSummaryText");
        Rect covered = new(coverage.TranslatePoint(default, window)!.Value, coverage.Bounds.Size);
        Assert.True(covered.Right <= drawn.Left, $"The coverage line at {covered} runs under the button at {drawn}.");
        window.Close();
    }

    private static string? Name(ListBox list, int index) =>
        AutomationProperties.GetName(Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index) ?? Reveal(list, index)));

    private static Control? Reveal(ListBox list, int index)
    {
        list.ScrollIntoView(index);
        Dispatch();
        return list.ContainerFromIndex(index);
    }

    private static void Save(Avalonia.Media.Imaging.WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static List<string> Texts(Control control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text ?? string.Empty)];

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatch();
    }

    private static string Focused(Window window) =>
        "The keyboard is on " + (window.FocusManager?.GetFocusedElement() switch
        {
            null => "nothing",
            Control control => $"{control.GetType().Name} {control.Name} ({control.DataContext?.GetType().Name})",
            var other => other.ToString(),
        });

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>client.exe sending server.exe a message a tick, over one paired connection.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, 10).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (2 * index)))
                .Between("127.0.0.1:8080", "127.0.0.1:50000")),
        }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
