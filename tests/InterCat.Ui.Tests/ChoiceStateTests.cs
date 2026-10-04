using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.1 and §6.6: a state a person chose - a selected row, a toggle or a box that is on, a tab, a focused field, the
/// text selected in it, a combo box's open list - is drawn from the verified tokens in every mode. The control theme
/// drew them in the platform's accent colour, which Windows lets a person set to any hue and no report measures: a
/// selected row's muted ink fell to 3.7:1 on its tint, and the per-second toggle wore the accent under white text.
/// </summary>
public sealed class ChoiceStateTests
{
    [AvaloniaFact(DisplayName = "§6.6: whatever accent the platform gives the control theme, a mode replaces it with its action fill, and every chosen state it restates is a token")]
    public void ThePlatformAccentReachesNoChoice()
    {
        Avalonia.Application application = Avalonia.Application.Current!;
        FluentTheme controlTheme = application.Styles.OfType<FluentTheme>().Single();
        Color platform = Color.Parse("#FF00FF");
        try
        {
            foreach (ThemeMode mode in EveryMode)
            {
                // A person's accent colour, handed to the control theme as the platform hands it.
                ThemeVariant variant = ThemePalette.IsDark(mode) ? ThemeVariant.Dark : ThemeVariant.Light;
                controlTheme.Palettes[variant] = new ColorPaletteResources { Accent = platform };
                Assert.True(application.TryGetResource("SystemAccentColor", variant, out object? given));
                Assert.Equal(platform, given);

                ThemeResources.Apply(application, mode);
                Assert.True(application.TryGetResource("SystemAccentColor", variant, out object? pinned));
                Assert.Equal(ThemeResources.ToColor(ThemePalette.Status(mode).ActionFill), pinned);

                HashSet<Color> tokens = Tokens(mode);
                foreach (string key in ThemeResources.ChoiceKeys)
                {
                    Assert.True(application.Resources.TryGetValue(key, out object? value), key);
                    Assert.True(tokens.Contains(ColorOf(value as IBrush)), $"{mode}: {key} draws no token of the mode.");
                }
            }
        }
        finally
        {
            ThemeResources.Apply(application, ThemeMode.Dark);
        }
    }

    [AvaloniaFact(DisplayName = "§6.1: every control-theme key the tokens restate is one the control theme reads, each restated by one rule")]
    public void EveryRestatedKeyIsTheControlThemes()
    {
        FluentTheme controlTheme = Avalonia.Application.Current!.Styles.OfType<FluentTheme>().Single();
        foreach (string key in ThemeResources.ControlChromeKeys.Concat(ThemeResources.ChoiceKeys))
        {
            Assert.True(controlTheme.TryGetResource(key, ThemeVariant.Dark, out _), $"The control theme reads no {key}.");
            Assert.True(controlTheme.TryGetResource(key, ThemeVariant.Light, out _), $"The control theme reads no {key}.");
        }

        // A key restated in high contrast alone is handed back to the control theme elsewhere; a chosen state's is not.
        Assert.Empty(ThemeResources.ControlChromeKeys.Intersect(ThemeResources.ChoiceKeys));
        Assert.Equal(ThemeResources.ControlChromeKeys.Count, ThemeResources.ControlChromeKeys.Distinct().Count());
        Assert.Equal(ThemeResources.ChoiceKeys.Count, ThemeResources.ChoiceKeys.Distinct().Count());
    }

    [AvaloniaFact(DisplayName = "§6.1: a selected row is ringed in the accent on the elevated face in every mode, its inks keep their measured contrast there, and neither selecting nor choosing a row moves it")]
    public void ASelectedRowIsRingedAndNothingMoves()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20)]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ListBox list = window.GetControl<ListBox>("RungList");
        Assert.Equal(3, workspace.RungRows.Count);
        int client = Row(workspace, "client");
        int server = Row(workspace, "server");
        int third = Row(workspace, "third");
        Point away = new(700, 300);

        try
        {
            foreach (ThemeMode mode in EveryMode)
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                window.MouseMove(away);
                list.SelectedIndex = -1;
                _ = Settle(window);
                Control row = list.ContainerFromIndex(third)!;
                TextBlock name = Label(row, workspace.RungRows[third].Label);
                Point resting = name.TranslatePoint(default, row)!.Value;

                // A list recycles its rows as it scrolls the selected one into view, so the row is found again.
                list.SelectedIndex = third;
                WriteableBitmap frame = Settle(window);
                row = list.ContainerFromIndex(third)!;
                name = Label(row, workspace.RungRows[third].Label);
                SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
                Color accent = ThemeResources.ToColor(surfaces.Accent);
                Color face = ThemeResources.ToColor(surfaces.Elevated);
                double width = row.Bounds.Width;
                double height = row.Bounds.Height;
                Assert.True(IsWhole(row, list), $"{mode}: the selected row is not drawn whole.");

                // The ring runs round all four sides inside the row, and the face lies within it.
                Assert.Equal(accent, At(frame, Spot(row, 0.5, height / 2, window)));
                Assert.Equal(accent, At(frame, Spot(row, width - 0.5, height / 2, window)));
                Assert.Equal(accent, At(frame, Spot(row, width / 2, 0.5, window)));
                Assert.Equal(accent, At(frame, Spot(row, width / 2, height - 0.5, window)));
                Assert.Equal(face, At(frame, Spot(row, 6, 3, window)));
                Assert.Equal(resting, name.TranslatePoint(default, row)!.Value);

                // Every ink the row carries is one the report measures on that face, at the mode's threshold.
                foreach (TextBlock text in row.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible))
                {
                    Color ink = ColorOf(text.Foreground);
                    double ratio = ColorMath.ContrastRatio(new Srgb(ink.R, ink.G, ink.B), surfaces.Elevated);
                    Assert.True(ratio >= ThemePalette.MinimumInkContrastIn(mode),
                        $"{mode}: '{text.Text}' in {ink} reads {ratio:F2}:1 on the selected face.");
                }

                // A row in a multi-selection carries a bar at its leading edge, drawn inside it as the ring is, so its
                // content lies where an unchosen row's does. A selection would join the set, so there is none.
                list.SelectedIndex = -1;
                workspace.ClearSelection();
                CtrlClick(window, list, client);
                CtrlClick(window, list, server);
                window.MouseMove(away);
                frame = Settle(window);
                Control chosen = list.ContainerFromIndex(client)!;
                Control unchosen = list.ContainerFromIndex(third)!;
                Assert.Equal([100, 200], workspace.ChosenProcesses.Select(process => process.ProcessId));
                Assert.Contains("chosen", chosen.Classes);
                Assert.Equal(accent, At(frame, Spot(chosen, 0.5, chosen.Bounds.Height / 2, window)));
                Assert.Equal(accent, At(frame, Spot(chosen, 3.5, chosen.Bounds.Height / 2, window)));
                Assert.NotEqual(accent, At(frame, Spot(chosen, 5.5, chosen.Bounds.Height / 2, window)));
                Assert.NotEqual(accent, At(frame, Spot(chosen, chosen.Bounds.Width / 2, 0.5, window)));
                Assert.Equal(
                    Label(unchosen, workspace.RungRows[third].Label).TranslatePoint(default, unchosen)!.Value.X,
                    Label(chosen, workspace.RungRows[client].Label).TranslatePoint(default, chosen)!.Value.X);
                Assert.Equal(unchosen.Bounds.Height, chosen.Bounds.Height);

                // A row under the pointer lies on the elevated face too, never on a tint of the control theme's own.
                window.MouseMove(Spot(unchosen, unchosen.Bounds.Width / 2, unchosen.Bounds.Height / 2, window));
                frame = Settle(window);
                Assert.Equal(face, At(frame, Spot(unchosen, unchosen.Bounds.Width - 6, 3, window)));
                window.MouseMove(away);

                CtrlClick(window, list, client);
                CtrlClick(window, list, server);
                Assert.Empty(workspace.ChosenProcesses);
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.1: the per-second toggle is the action pair when on - at rest, under the pointer and pressed - with its label centred, and a button's edges in high contrast when off")]
    public async Task TheToggleIsTheActionPairWhenOn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        ToggleButton toggle = window.GetControl<ToggleButton>("PerSecondToggle");
        Point away = new(700, 300);

        try
        {
            foreach (ThemeMode mode in EveryMode)
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                toggle.IsChecked = true;
                window.MouseMove(away);
                WriteableBitmap frame = Settle(window);
                StatusTokens status = ThemePalette.Status(mode);
                ContentPresenter face = toggle.GetVisualDescendants().OfType<ContentPresenter>()
                    .First(presenter => presenter.Name == "PART_ContentPresenter");
                Point fill = toggle.TranslatePoint(new(3, toggle.Bounds.Height / 2), window)!.Value;
                Point middle = toggle.TranslatePoint(new(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;

                // On, it is the action fill under the action ink: it once wore the platform's accent under white text.
                Assert.Equal(ThemeResources.ToColor(status.ActionFill), At(frame, fill));
                Assert.Equal(ThemeResources.ToColor(status.ActionInk), ColorOf(face.Foreground));

                // Its label's line sits in the middle of the box, where it once sat at the top of a stretched block.
                TextBlock label = toggle.GetVisualDescendants().OfType<TextBlock>().Single();
                double lineMiddle = label.TranslatePoint(new(0, label.DesiredSize.Height / 2), toggle)!.Value.Y;
                Assert.InRange(lineMiddle, (toggle.Bounds.Height / 2) - 1, (toggle.Bounds.Height / 2) + 1);

                window.MouseMove(middle);
                frame = Settle(window);
                Assert.Equal(ThemeResources.ToColor(status.ActionFillHover), At(frame, fill));
                Assert.Equal(ThemeResources.ToColor(status.ActionInk), ColorOf(face.Foreground));

                // Pressed, and released away from it, so it stays on.
                window.MouseDown(middle, MouseButton.Left);
                frame = Settle(window);
                Assert.Equal(ThemeResources.ToColor(status.ActionFillPressed), At(frame, fill));
                Assert.Equal(ThemeResources.ToColor(status.ActionInk), ColorOf(face.Foreground));
                window.MouseMove(away);
                window.MouseUp(away, MouseButton.Left);
                Dispatch();
                Assert.True(toggle.IsChecked);

                // Off, it is a button: in high contrast its face inside the divider's edge, with body ink.
                toggle.IsChecked = false;
                frame = Settle(window);
                if (ThemePalette.IsHighContrast(mode))
                {
                    // The toggle stretches to its row, whose height is the text's, so its edge may straddle two pixels.
                    SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
                    Color divider = ThemeResources.ToColor(surfaces.Divider);
                    Assert.Equal(divider, ColorOf(face.BorderBrush));
                    Color edge = At(frame, Spot(toggle, toggle.Bounds.Width / 2, 0.5, window));
                    Assert.True(Near(edge, divider), $"{mode}: the toggle's edge drew {edge}, not the divider {divider}.");
                    Assert.Equal(ThemeResources.ToColor(surfaces.Elevated), At(frame, fill));
                    Assert.Equal(ThemeResources.ToColor(surfaces.Ink), ColorOf(face.Foreground));
                }
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.1: a combo box's open list is the canvas inside the divider, its chosen row ringed on the elevated face with every ink measured there, and the closed box a button in high contrast")]
    public async Task AComboBoxListIsDrawnFromTheTokens()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        ComboBox selector = window.GetControl<ComboBox>("RankBySelector");
        Point away = new(700, 300);

        try
        {
            foreach (ThemeMode mode in EveryMode)
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                window.MouseMove(away);
                SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
                Color accent = ThemeResources.ToColor(surfaces.Accent);
                Color face = ThemeResources.ToColor(surfaces.Elevated);
                Color canvas = ThemeResources.ToColor(surfaces.Canvas);
                WriteableBitmap frame = Settle(window);

                // Closed, in high contrast, it is a button: body ink on the elevated face inside the divider's edge.
                if (ThemePalette.IsHighContrast(mode))
                {
                    // It sits in the toggle's row, so its edge too may straddle two pixels.
                    Color divider = ThemeResources.ToColor(surfaces.Divider);
                    Assert.Equal(divider, ColorOf(selector.BorderBrush));
                    Color edge = At(frame, Spot(selector, selector.Bounds.Width / 2, 0.5, window));
                    Assert.True(Near(edge, divider), $"{mode}: the closed box's edge drew {edge}, not the divider {divider}.");
                    Assert.Equal(face, At(frame, Spot(selector, 4, selector.Bounds.Height / 2, window)));
                    Assert.Equal(ThemeResources.ToColor(surfaces.Ink), ColorOf(selector.Foreground));
                }

                selector.IsDropDownOpen = true;
                frame = Settle(window);
                Control chosen = Assert.IsAssignableFrom<Control>(selector.ContainerFromIndex(selector.SelectedIndex));
                Control other = Assert.IsAssignableFrom<Control>(selector.ContainerFromIndex(selector.SelectedIndex + 1));
                Border list = chosen.GetVisualAncestors().OfType<Border>().First(border => border.Name == "PopupBorder");

                // The list is the canvas inside the divider's edge, and a row not chosen lies on it.
                Assert.Equal(ThemeResources.ToColor(surfaces.Divider), At(frame, Spot(list, list.Bounds.Width / 2, 0.5, window)));
                Assert.Equal(canvas, At(frame, Spot(list, list.Bounds.Width / 2, 2.5, window)));
                Assert.Equal(canvas, At(frame, Spot(other, other.Bounds.Width - 6, 3, window)));

                // The chosen row is ringed in the accent on the elevated face, and each ink it carries is measured there.
                Assert.Equal(accent, At(frame, Spot(chosen, 0.5, chosen.Bounds.Height / 2, window)));
                Assert.Equal(face, At(frame, Spot(chosen, chosen.Bounds.Width - 6, 3, window)));
                foreach (TextBlock text in chosen.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible))
                {
                    Color ink = ColorOf(text.Foreground);
                    double ratio = ColorMath.ContrastRatio(new Srgb(ink.R, ink.G, ink.B), surfaces.Elevated);
                    Assert.True(ratio >= ThemePalette.MinimumInkContrastIn(mode),
                        $"{mode}: '{text.Text}' in {ink} reads {ratio:F2}:1 on the chosen row.");
                }

                Assert.Equal(ThemeResources.ToColor(surfaces.Ink), ColorOf(other.GetValue(TemplatedControl.ForegroundProperty)));
                selector.IsDropDownOpen = false;
                Dispatch();
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.1: a ticked box, a selected tab, a focused field and the text selected in it are drawn from the tokens in every mode")]
    public void BoxesTabsAndFieldsTakeTheTokens()
    {
        var ticked = new CheckBox { Content = "Copy session a", IsChecked = true };
        var empty = new CheckBox { Content = "Copy session b", IsChecked = false };
        var field = new TextBox { Text = "search text", Width = 220 };
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Sessions", Content = new TextBlock { Text = "One" } });
        tabs.Items.Add(new TabItem { Header = "Timeline", Content = new TextBlock { Text = "Two" } });
        var list = new ListBox { ItemsSource = new[] { "Session a", "Session b" } };
        var window = new Window
        {
            Width = 420,
            Height = 420,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(16), Children = { ticked, empty, field, tabs, list } },
        };
        window.Show();

        try
        {
            foreach (ThemeMode mode in EveryMode)
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                field.Focus(NavigationMethod.Tab);
                field.SelectionStart = 0;
                field.SelectionEnd = 6;
                WriteableBitmap frame = Settle(window);
                SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
                StatusTokens status = ThemePalette.Status(mode);

                // A ticked box is the action fill under a tick in the action ink.
                Border box = ticked.GetVisualDescendants().OfType<Border>().First(border => border.Name == "NormalRectangle");
                Assert.Equal(ThemeResources.ToColor(status.ActionFill), At(frame, Spot(box, 3, box.Bounds.Height / 2, window)));
                Avalonia.Controls.Shapes.Path tick = ticked.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                    .First(path => path.Name == "CheckGlyph");
                Assert.Equal(ThemeResources.ToColor(status.ActionInk), ColorOf(tick.Fill));

                // An empty box is edged in the divider in high contrast, a line to be seen on the ground.
                if (ThemePalette.IsHighContrast(mode))
                {
                    Border open = empty.GetVisualDescendants().OfType<Border>().First(border => border.Name == "NormalRectangle");
                    Assert.Equal(ThemeResources.ToColor(surfaces.Divider), At(frame, Spot(open, open.Bounds.Width / 2, 0.5, window)));
                }

                // The focused field is edged in the accent, and its selected text is the action pair.
                Assert.Equal(ThemeResources.ToColor(surfaces.Accent), At(frame, Spot(field, field.Bounds.Width / 2, 0.5, window)));
                Assert.Equal(ThemeResources.ToColor(status.ActionFill), ColorOf(field.SelectionBrush));
                Assert.Equal(ThemeResources.ToColor(status.ActionInk), ColorOf(field.SelectionForegroundBrush));

                // The selected tab is underlined in the accent and named in body ink; the other is named in the muted ink.
                TabItem selected = Assert.IsType<TabItem>(tabs.ContainerFromIndex(0));
                TabItem other = Assert.IsType<TabItem>(tabs.ContainerFromIndex(1));
                Border pipe = selected.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_SelectedPipe");
                Assert.Equal(ThemeResources.ToColor(surfaces.Accent),
                    At(frame, Spot(pipe, pipe.Bounds.Width / 2, pipe.Bounds.Height / 2, window)));
                Assert.Equal(ThemeResources.ToColor(surfaces.Ink), ColorOf(Header(selected).Foreground));
                Assert.Equal(ThemeResources.ToColor(surfaces.MutedInk), ColorOf(Header(other).Foreground));

                // A list a window builds lies on the window's canvas, as the main window's lists do, never on a grey of
                // the control theme's own.
                Control row = list.ContainerFromIndex(1)!;
                Assert.Equal(ThemeResources.ToColor(surfaces.Canvas), At(frame, Spot(row, row.Bounds.Width - 6, 3, window)));
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    /// <summary>Every theme mode, ending in the dark one the other tests expect to find.</summary>
    private static readonly ThemeMode[] EveryMode =
        [ThemeMode.Light, ThemeMode.HighContrastDark, ThemeMode.HighContrastLight, ThemeMode.Dark];

    /// <summary>Every surface, ink and status token of a mode, as colours.</summary>
    private static HashSet<Color> Tokens(ThemeMode mode)
    {
        SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
        StatusTokens status = ThemePalette.Status(mode);
        Srgb[] all =
        [
            surfaces.Canvas, surfaces.Panel, surfaces.Plot, surfaces.Elevated, surfaces.Accent, surfaces.Ink,
            surfaces.MutedInk, surfaces.Divider, status.Caution, status.ActionFill, status.ActionFillHover,
            status.ActionFillPressed, status.ActionInk,
        ];
        return [.. all.Select(ThemeResources.ToColor)];
    }

    /// <summary>A colour within antialiasing of another: an edge drawn across a pixel boundary blends into its ground.</summary>
    private static bool Near(Color seen, Color expected) =>
        Math.Abs(seen.R - expected.R) <= 16 && Math.Abs(seen.G - expected.G) <= 16 && Math.Abs(seen.B - expected.B) <= 16;

    private static Point Spot(Visual visual, double x, double y, Visual window) => visual.TranslatePoint(new(x, y), window)!.Value;

    /// <summary>Whether a row lies wholly within its list's viewport, so each of its edges is drawn.</summary>
    private static bool IsWhole(Control row, ListBox list)
    {
        ScrollViewer viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Point top = row.TranslatePoint(default, viewer)!.Value;
        return top.Y >= 0 && top.Y + row.Bounds.Height <= viewer.Viewport.Height;
    }

    private static TextBlock Label(Control row, string name) =>
        row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == name);

    /// <summary>The presenter a tab's header is drawn in, whose foreground the control theme sets by its state.</summary>
    private static ContentPresenter Header(TabItem tab) =>
        tab.GetVisualDescendants().OfType<ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter");

    private static int Row(WorkspaceViewModel workspace, string name) =>
        workspace.RungRows.ToList().FindIndex(row => row.Label.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static void CtrlClick(Window window, ListBox list, int index)
    {
        list.ScrollIntoView(index);
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Control item = list.ContainerFromIndex(index)!;
        Point at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.Control);
        Dispatch();
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>A client, a server and a third process as rundowns name them, so each is a group of its own.</summary>
    private static ObservationRowV1[] Named() =>
    [
        Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
        Lifecycle(3, ObservationKind.Inventory, 300, 3) with { ResourceName = @"C:\Tools\third.exe", SessionRelativeTicks = 300 },
    ];

    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    private static ObservationRowV1[] Third(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index =>
            Transfer(12 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 32, 300, (ulong)(5_000 + index))
                .Between("127.0.0.1:51000", "10.0.0.7:443") with { SessionRelativeTicks = (12 + (2 * index)) * 100L }),
    ];

    /// <summary>Two executables, one sending and one receiving, so the rail offers a ranking per second.</summary>
    private static ObservationRowV1[] Traffic() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\listen.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10)),
        .. Enumerable.Range(0, 6).Select(index =>
            Timed(Transfer(20 + index, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 400, (ulong)(20 + index)))),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the bindings and a layout pass and returns the frame, so what the test reads is what is drawn.</summary>
    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }
}
