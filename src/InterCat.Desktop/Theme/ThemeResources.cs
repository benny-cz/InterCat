using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using InterCat.Domain;

namespace InterCat.Desktop.Theme;

/// <summary>
/// Publishes the verified theme tokens as application resources, so the UI never carries a literal colour.
/// A palette change therefore reaches every surface at once and stays covered by the §6.6 tests.
/// </summary>
public static class ThemeResources
{
    /// <summary>
    /// The mode the tokens were last applied in. The drawn panes and the rows that carry a colour read it, so one change
    /// of mode reaches the canvases as it reaches every resource.
    /// </summary>
    public static ThemeMode CurrentMode { get; private set; } = ThemeMode.Dark;

    /// <summary>Raised on the UI thread after the tokens have been applied in another mode.</summary>
    public static event EventHandler? ModeChanged;

    public static void Apply(Avalonia.Application application, ThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(application);

        SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
        application.Resources["Surface.Canvas"] = Brush(surfaces.Canvas);
        application.Resources["Surface.Panel"] = Brush(surfaces.Panel);
        application.Resources["Surface.Plot"] = Brush(surfaces.Plot);
        application.Resources["Surface.Elevated"] = Brush(surfaces.Elevated);
        application.Resources["Ink.Body"] = Brush(surfaces.Ink);
        application.Resources["Ink.Muted"] = Brush(surfaces.MutedInk);
        application.Resources["Ink.Accent"] = Brush(surfaces.Accent);
        application.Resources["Line.Divider"] = Brush(surfaces.Divider);

        foreach (FamilyTokens family in ThemePalette.Families(mode))
        {
            application.Resources[$"Family.{family.Family}.Fill"] = Brush(family.Fill);
            application.Resources[$"Family.{family.Family}.Ink"] = Brush(family.Ink);
        }

        // A warning, the live state and the primary action have tokens of their own; none borrows a family's hue (§6.6).
        StatusTokens status = ThemePalette.Status(mode);
        application.Resources["Status.Caution"] = Brush(status.Caution);
        application.Resources["Action.Fill"] = Brush(status.ActionFill);
        application.Resources["Action.FillHover"] = Brush(status.ActionFillHover);
        application.Resources["Action.FillPressed"] = Brush(status.ActionFillPressed);
        application.Resources["Action.Ink"] = Brush(status.ActionInk);

        ApplyControlChrome(application, mode, surfaces);
        application.RequestedThemeVariant = ThemePalette.IsDark(mode) ? ThemeVariant.Dark : ThemeVariant.Light;
        if (CurrentMode != mode)
        {
            CurrentMode = mode;
            ModeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The control theme's own keys a high-contrast mode restates: a button's face, edge and label in every state, a
    /// text box's edge, a menu's face, edge and items, a tool tip, a scroll bar's thumb and track, and a list row under
    /// the pointer, pressed or selected. The control theme draws them faint against the ground; in high contrast every
    /// interactive edge and state must be seen, so they are taken from the verified tokens there and left to the control
    /// theme otherwise.
    /// </summary>
    internal static IReadOnlyList<string> ControlChromeKeys { get; } =
    [
        "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
        "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled",
        "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
        "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
        "MenuFlyoutPresenterBackground", "MenuFlyoutPresenterBorderBrush",
        "MenuFlyoutItemBackground", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed",
        "MenuFlyoutItemBackgroundDisabled", "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver",
        "MenuFlyoutItemForegroundPressed", "MenuFlyoutItemForegroundDisabled",
        "ToolTipBackground", "ToolTipForeground", "ToolTipBorderBrush",
        "ScrollBarPanningThumbBackground", "ScrollBarThumbFillPointerOver", "ScrollBarThumbFillPressed",
        "ScrollBarThumbFillDisabled", "ScrollBarTrackFill", "ScrollBarTrackFillPointerOver", "ScrollBarTrackStroke",
        "ScrollBarTrackStrokePointerOver",
        "SystemControlHighlightListLowBrush", "SystemControlHighlightListMediumBrush",
        "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
        "SystemControlHighlightListAccentHighBrush", "SystemControlHighlightAltBaseHighBrush",
    ];

    /// <summary>The high-contrast styles no resource key reaches, while a high-contrast mode is applied.</summary>
    private static Styles? highContrastStyles;

    private static void ApplyControlChrome(Avalonia.Application application, ThemeMode mode, SurfaceTokens surfaces)
    {
        if (highContrastStyles is { } applied)
        {
            _ = application.Styles.Remove(applied);
            highContrastStyles = null;
        }

        if (!ThemePalette.IsHighContrast(mode))
        {
            foreach (string key in ControlChromeKeys)
            {
                application.Resources.Remove(key);
            }

            return;
        }

        // A button rests as body ink on the elevated face inside a divider edge, takes the accent edge under the
        // pointer, and is pressed as the action pair: the canvas on the accent, which the report measures. Disabled, it
        // sits on the bare ground with its label in the divider's tone, plainly dimmer than any enabled label. A text
        // box's edge is the divider, and the accent when pointed at or focused.
        SolidColorBrush face = Brush(surfaces.Elevated);
        SolidColorBrush edge = Brush(surfaces.Divider);
        SolidColorBrush label = Brush(surfaces.Ink);
        SolidColorBrush accent = Brush(surfaces.Accent);
        SolidColorBrush ground = Brush(surfaces.Canvas);
        application.Resources["ButtonBackground"] = face;
        application.Resources["ButtonBackgroundPointerOver"] = face;
        application.Resources["ButtonBackgroundPressed"] = accent;
        application.Resources["ButtonBackgroundDisabled"] = ground;
        application.Resources["ButtonForeground"] = label;
        application.Resources["ButtonForegroundPointerOver"] = label;
        application.Resources["ButtonForegroundPressed"] = ground;
        application.Resources["ButtonForegroundDisabled"] = edge;
        application.Resources["ButtonBorderBrush"] = edge;
        application.Resources["ButtonBorderBrushPointerOver"] = accent;
        application.Resources["ButtonBorderBrushPressed"] = accent;
        application.Resources["ButtonBorderBrushDisabled"] = edge;
        application.Resources["TextControlBorderBrush"] = edge;
        application.Resources["TextControlBorderBrushPointerOver"] = accent;
        application.Resources["TextControlBorderBrushFocused"] = accent;

        // A menu is the elevated face inside a divider edge. The item under the pointer, or pressed, is the action pair
        // the report measures, the canvas on the accent; one that cannot be chosen keeps the muted ink, not a faint grey.
        SolidColorBrush muted = Brush(surfaces.MutedInk);
        application.Resources["MenuFlyoutPresenterBackground"] = face;
        application.Resources["MenuFlyoutPresenterBorderBrush"] = edge;
        application.Resources["MenuFlyoutItemBackground"] = face;
        application.Resources["MenuFlyoutItemBackgroundPointerOver"] = accent;
        application.Resources["MenuFlyoutItemBackgroundPressed"] = accent;
        application.Resources["MenuFlyoutItemBackgroundDisabled"] = face;
        application.Resources["MenuFlyoutItemForeground"] = label;
        application.Resources["MenuFlyoutItemForegroundPointerOver"] = ground;
        application.Resources["MenuFlyoutItemForegroundPressed"] = ground;
        application.Resources["MenuFlyoutItemForegroundDisabled"] = muted;

        // A tool tip is a card: body ink on the elevated face inside a divider edge.
        application.Resources["ToolTipBackground"] = face;
        application.Resources["ToolTipForeground"] = label;
        application.Resources["ToolTipBorderBrush"] = edge;

        // A scroll bar's resting thumb is a divider line, the accent under the pointer, on the bare ground.
        application.Resources["ScrollBarPanningThumbBackground"] = edge;
        application.Resources["ScrollBarThumbFillPointerOver"] = accent;
        application.Resources["ScrollBarThumbFillPressed"] = accent;
        application.Resources["ScrollBarThumbFillDisabled"] = edge;
        application.Resources["ScrollBarTrackFill"] = ground;
        application.Resources["ScrollBarTrackFillPointerOver"] = ground;
        application.Resources["ScrollBarTrackStroke"] = edge;
        application.Resources["ScrollBarTrackStrokePointerOver"] = edge;

        // A list row under the pointer, pressed or selected lies on the elevated face, where every ink a row carries,
        // muted included, is measured. A fill alone could not both stand apart from the ground and keep muted ink
        // legible, so selection is also an accent edge, drawn by a style no resource key reaches.
        application.Resources["SystemControlHighlightListLowBrush"] = face;
        application.Resources["SystemControlHighlightListMediumBrush"] = face;
        application.Resources["SystemControlHighlightListAccentLowBrush"] = face;
        application.Resources["SystemControlHighlightListAccentMediumBrush"] = face;
        application.Resources["SystemControlHighlightListAccentHighBrush"] = face;
        application.Resources["SystemControlHighlightAltBaseHighBrush"] = label;
        highContrastStyles = SelectionEdges(accent);
        application.Styles.Add(highContrastStyles);
    }

    /// <summary>
    /// In high contrast every list row keeps a transparent two-pixel edge, so selecting one moves nothing, and the
    /// selected row draws that edge in the accent.
    /// </summary>
    private static Styles SelectionEdges(IBrush accent) =>
    [
        new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2)),
                new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
            },
        },
        new Style(selector => selector.OfType<ListBoxItem>().Class(":selected"))
        {
            Setters = { new Setter(TemplatedControl.BorderBrushProperty, accent) },
        },
    ];

    /// <summary>The fill token of a mechanism, resolved through its family so no hue is invented.</summary>
    public static Color FillOf(Mechanism mechanism, ThemeMode mode) =>
        ToColor(ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(mechanism)).Fill);

    /// <summary>The ink token of a mechanism, for labels and legend text.</summary>
    public static Color InkOf(Mechanism mechanism, ThemeMode mode) =>
        ToColor(ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(mechanism)).Ink);

    public static Color ToColor(Srgb value) => Color.FromRgb(value.R, value.G, value.B);

    private static SolidColorBrush Brush(Srgb value) => new(ToColor(value));
}
