using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
        ApplyChoiceStates(application, mode, surfaces, status);
        application.RequestedThemeVariant = ThemePalette.IsDark(mode) ? ThemeVariant.Dark : ThemeVariant.Light;
        if (CurrentMode != mode)
        {
            CurrentMode = mode;
            ModeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The control theme's own keys a high-contrast mode restates: a button's, a toggle button's, a closed combo box's
    /// and a check box's face, edge and label in every state, a text box's edge, a menu's face, edge and items, a tool
    /// tip, a scroll bar's thumb and track, and the row of any other list control under the pointer, pressed or selected.
    /// The control theme draws them faint against the ground; in high contrast every interactive edge and state must be
    /// seen, so they are taken from the verified tokens there and left to the control theme otherwise.
    /// </summary>
    internal static IReadOnlyList<string> ControlChromeKeys { get; } =
    [
        "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
        "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled",
        "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
        "ToggleButtonBackground", "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundPressed",
        "ToggleButtonBackgroundDisabled", "ToggleButtonBackgroundCheckedDisabled", "ToggleButtonForeground",
        "ToggleButtonForegroundPointerOver", "ToggleButtonForegroundPressed", "ToggleButtonForegroundDisabled",
        "ToggleButtonForegroundCheckedDisabled", "ToggleButtonBorderBrush", "ToggleButtonBorderBrushPointerOver",
        "ToggleButtonBorderBrushPressed", "ToggleButtonBorderBrushDisabled", "ToggleButtonBorderBrushCheckedDisabled",
        "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxBackgroundDisabled",
        "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxBorderBrushDisabled",
        "ComboBoxForeground", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForeground",
        "ComboBoxDropDownGlyphForegroundDisabled", "ComboBoxItemForegroundDisabled",
        "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver",
        "CheckBoxCheckBackgroundStrokeUncheckedPressed", "CheckBoxCheckBackgroundStrokeUncheckedDisabled",
        "CheckBoxCheckBackgroundFillUncheckedPressed", "CheckBoxForegroundUnchecked", "CheckBoxForegroundUncheckedPointerOver",
        "CheckBoxForegroundUncheckedPressed", "CheckBoxForegroundUncheckedDisabled", "CheckBoxForegroundChecked",
        "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed", "CheckBoxForegroundCheckedDisabled",
        "TabItemHeaderForegroundDisabled",
        "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
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

    private static void ApplyControlChrome(Avalonia.Application application, ThemeMode mode, SurfaceTokens surfaces)
    {
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
        // box's edge is the divider, and the accent when pointed at; focused, it is the accent in every mode.
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

        // A toggle button that is off is a button, in every state; on, it is the action pair in every mode (below).
        application.Resources["ToggleButtonBackground"] = face;
        application.Resources["ToggleButtonBackgroundPointerOver"] = face;
        application.Resources["ToggleButtonBackgroundPressed"] = accent;
        application.Resources["ToggleButtonBackgroundDisabled"] = ground;
        application.Resources["ToggleButtonBackgroundCheckedDisabled"] = ground;
        application.Resources["ToggleButtonForeground"] = label;
        application.Resources["ToggleButtonForegroundPointerOver"] = label;
        application.Resources["ToggleButtonForegroundPressed"] = ground;
        application.Resources["ToggleButtonForegroundDisabled"] = edge;
        application.Resources["ToggleButtonForegroundCheckedDisabled"] = edge;
        application.Resources["ToggleButtonBorderBrush"] = edge;
        application.Resources["ToggleButtonBorderBrushPointerOver"] = accent;
        application.Resources["ToggleButtonBorderBrushPressed"] = accent;
        application.Resources["ToggleButtonBorderBrushDisabled"] = edge;
        application.Resources["ToggleButtonBorderBrushCheckedDisabled"] = edge;

        // A closed combo box is a button too: body ink and its glyph on the elevated face inside a divider edge, the
        // accent edge when pointed at or pressed, and the bare ground in the divider's tone when it cannot be used.
        application.Resources["ComboBoxBackground"] = face;
        application.Resources["ComboBoxBackgroundPointerOver"] = face;
        application.Resources["ComboBoxBackgroundPressed"] = face;
        application.Resources["ComboBoxBackgroundDisabled"] = ground;
        application.Resources["ComboBoxBorderBrush"] = edge;
        application.Resources["ComboBoxBorderBrushPointerOver"] = accent;
        application.Resources["ComboBoxBorderBrushPressed"] = accent;
        application.Resources["ComboBoxBorderBrushDisabled"] = edge;
        application.Resources["ComboBoxForeground"] = label;
        application.Resources["ComboBoxForegroundDisabled"] = edge;
        application.Resources["ComboBoxDropDownGlyphForeground"] = label;
        application.Resources["ComboBoxDropDownGlyphForegroundDisabled"] = edge;
        application.Resources["ComboBoxItemForegroundDisabled"] = edge;

        // A check box's empty box is edged in the divider, the accent when pointed at or pressed; its label is body ink,
        // and the divider's tone when it cannot be used. A ticked box is the action pair in every mode (below).
        application.Resources["CheckBoxCheckBackgroundStrokeUnchecked"] = edge;
        application.Resources["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = accent;
        application.Resources["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = accent;
        application.Resources["CheckBoxCheckBackgroundStrokeUncheckedDisabled"] = edge;
        application.Resources["CheckBoxCheckBackgroundFillUncheckedPressed"] = face;
        application.Resources["CheckBoxForegroundUnchecked"] = label;
        application.Resources["CheckBoxForegroundUncheckedPointerOver"] = label;
        application.Resources["CheckBoxForegroundUncheckedPressed"] = label;
        application.Resources["CheckBoxForegroundUncheckedDisabled"] = edge;
        application.Resources["CheckBoxForegroundChecked"] = label;
        application.Resources["CheckBoxForegroundCheckedPointerOver"] = label;
        application.Resources["CheckBoxForegroundCheckedPressed"] = label;
        application.Resources["CheckBoxForegroundCheckedDisabled"] = edge;
        application.Resources["TabItemHeaderForegroundDisabled"] = edge;

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

        // A row of any other list control under the pointer, pressed or selected lies on the elevated face, where every
        // ink a row carries is measured; a list box's and a combo box's rows say so in every mode, by the styles in App.
        application.Resources["SystemControlHighlightListLowBrush"] = face;
        application.Resources["SystemControlHighlightListMediumBrush"] = face;
        application.Resources["SystemControlHighlightListAccentLowBrush"] = face;
        application.Resources["SystemControlHighlightListAccentMediumBrush"] = face;
        application.Resources["SystemControlHighlightListAccentHighBrush"] = face;
        application.Resources["SystemControlHighlightAltBaseHighBrush"] = label;
    }

    /// <summary>
    /// The control theme's own keys for a state a person chose or is choosing: a toggle button or a check box that is
    /// on, a tab, a focused text box or combo box, the text selected in a text box, and a combo box's open list. The
    /// control theme draws most of them in the platform's accent colour, which Windows lets a person set to any hue and
    /// no report measures, under a label of the control theme's own; so in every mode they are taken from the tokens,
    /// each ink on a ground the report measures it on.
    /// </summary>
    internal static IReadOnlyList<string> ChoiceKeys { get; } =
    [
        "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed",
        "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
        "ToggleButtonBorderBrushChecked", "ToggleButtonBorderBrushCheckedPointerOver", "ToggleButtonBorderBrushCheckedPressed",
        "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
        "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
        "CheckBoxCheckBackgroundStrokeCheckedPressed", "CheckBoxCheckGlyphForegroundChecked",
        "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
        "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver",
        "CheckBoxCheckBackgroundFillIndeterminatePressed", "CheckBoxCheckBackgroundStrokeIndeterminate",
        "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "CheckBoxCheckBackgroundStrokeIndeterminatePressed",
        "CheckBoxCheckGlyphForegroundIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver",
        "CheckBoxCheckGlyphForegroundIndeterminatePressed",
        "TabItemHeaderSelectedPipeFill", "TabItemHeaderForegroundSelected", "TabItemHeaderForegroundSelectedPointerOver",
        "TabItemHeaderForegroundSelectedPressed", "TabItemHeaderForegroundUnselected",
        "TabItemHeaderForegroundUnselectedPointerOver", "TabItemHeaderForegroundUnselectedPressed",
        "TextControlBorderBrushFocused", "TextControlSelectionHighlightColor",
        "ComboBoxBackgroundUnfocused", "ComboBoxBackgroundBorderBrushUnfocused", "ComboBoxForegroundFocused",
        "ComboBoxForegroundFocusedPressed", "ComboBoxDropDownGlyphForegroundFocused",
        "ComboBoxDropDownGlyphForegroundFocusedPressed", "ComboBoxDropDownBackground", "ComboBoxDropDownBorderBrush",
        "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed",
        "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver",
        "ComboBoxItemForegroundSelectedPressed",
    ];

    /// <summary>How wide a selected row's accent ring is, and a multi-selection's bar at a row's leading edge.</summary>
    private const double RingWidth = 2;

    private const double ChosenBarWidth = 4;

    /// <summary>
    /// How strongly what a chosen timeline cell highlights is drawn, against the selection's whole: the graph's halo and
    /// ring, and the bar that marks a row standing for one in a table (§6.4, R15).
    /// </summary>
    internal const double CellHighlightStrength = 0.45;

    private static void ApplyChoiceStates(
        Avalonia.Application application, ThemeMode mode, SurfaceTokens surfaces, StatusTokens status)
    {
        // Whatever a control the window does not restate draws in the accent, it draws in the action fill, never in the
        // platform's colour: the control theme's palette takes the application's own voice as its accent.
        PinAccent(application, mode, status);

        SolidColorBrush ground = Brush(surfaces.Canvas);
        SolidColorBrush face = Brush(surfaces.Elevated);
        SolidColorBrush ink = Brush(surfaces.Ink);
        SolidColorBrush muted = Brush(surfaces.MutedInk);
        SolidColorBrush accent = Brush(surfaces.Accent);
        SolidColorBrush divider = Brush(surfaces.Divider);
        SolidColorBrush fill = Brush(status.ActionFill);
        SolidColorBrush fillHover = Brush(status.ActionFillHover);
        SolidColorBrush fillPressed = Brush(status.ActionFillPressed);
        SolidColorBrush actionInk = Brush(status.ActionInk);

        // A toggle button that is on is the action pair in its three states, edged in its own fill, as is a ticked box.
        application.Resources["ToggleButtonBackgroundChecked"] = fill;
        application.Resources["ToggleButtonBackgroundCheckedPointerOver"] = fillHover;
        application.Resources["ToggleButtonBackgroundCheckedPressed"] = fillPressed;
        application.Resources["ToggleButtonForegroundChecked"] = actionInk;
        application.Resources["ToggleButtonForegroundCheckedPointerOver"] = actionInk;
        application.Resources["ToggleButtonForegroundCheckedPressed"] = actionInk;
        application.Resources["ToggleButtonBorderBrushChecked"] = fill;
        application.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = fillHover;
        application.Resources["ToggleButtonBorderBrushCheckedPressed"] = fillPressed;
        foreach (string state in new[] { "Checked", "Indeterminate" })
        {
            application.Resources[$"CheckBoxCheckBackgroundFill{state}"] = fill;
            application.Resources[$"CheckBoxCheckBackgroundFill{state}PointerOver"] = fillHover;
            application.Resources[$"CheckBoxCheckBackgroundFill{state}Pressed"] = fillPressed;
            application.Resources[$"CheckBoxCheckBackgroundStroke{state}PointerOver"] = fillHover;
            application.Resources[$"CheckBoxCheckBackgroundStroke{state}Pressed"] = fillPressed;
            application.Resources[$"CheckBoxCheckGlyphForeground{state}"] = actionInk;
            application.Resources[$"CheckBoxCheckGlyphForeground{state}PointerOver"] = actionInk;
            application.Resources[$"CheckBoxCheckGlyphForeground{state}Pressed"] = actionInk;
        }

        application.Resources["CheckBoxCheckBackgroundStrokeIndeterminate"] = fill;

        // The selected tab is underlined in the accent and named in body ink; the others are named in the muted ink.
        application.Resources["TabItemHeaderSelectedPipeFill"] = accent;
        application.Resources["TabItemHeaderForegroundSelected"] = ink;
        application.Resources["TabItemHeaderForegroundSelectedPointerOver"] = ink;
        application.Resources["TabItemHeaderForegroundSelectedPressed"] = ink;
        application.Resources["TabItemHeaderForegroundUnselected"] = muted;
        application.Resources["TabItemHeaderForegroundUnselectedPointerOver"] = ink;
        application.Resources["TabItemHeaderForegroundUnselectedPressed"] = ink;

        // A focused text box is edged in the accent; text selected in it is the action pair (App draws its ink).
        application.Resources["TextControlBorderBrushFocused"] = accent;
        application.Resources["TextControlSelectionHighlightColor"] = fill;

        // A combo box the keyboard rests on lies on the elevated face inside an accent edge, with body ink; its open list
        // is the canvas inside a divider edge, and its rows are list rows (App), each label in body ink.
        application.Resources["ComboBoxBackgroundUnfocused"] = face;
        application.Resources["ComboBoxBackgroundBorderBrushUnfocused"] = accent;
        application.Resources["ComboBoxForegroundFocused"] = ink;
        application.Resources["ComboBoxForegroundFocusedPressed"] = ink;
        application.Resources["ComboBoxDropDownGlyphForegroundFocused"] = ink;
        application.Resources["ComboBoxDropDownGlyphForegroundFocusedPressed"] = ink;
        application.Resources["ComboBoxDropDownBackground"] = ground;
        application.Resources["ComboBoxDropDownBorderBrush"] = divider;
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Selected", "SelectedPointerOver", "SelectedPressed" })
        {
            application.Resources[$"ComboBoxItemForeground{state}"] = ink;
        }

        // A selected row is ringed in the accent inside its bounds, and a row in a multi-selection carries an accent bar
        // at its leading edge; both are drawn over the row's face, so neither moves its content (§6.1, §6.7).
        Color ring = ToColor(surfaces.Accent);
        BoxShadow selected = new() { IsInset = true, Spread = RingWidth, Color = ring };
        BoxShadow chosen = new() { IsInset = true, OffsetX = ChosenBarWidth, Color = ring };
        BoxShadow chosenPart = new() { IsInset = true, OffsetX = ChosenBarWidth / 2, Color = ring };
        application.Resources["ListItem.SelectedRing"] = new BoxShadows(selected);
        application.Resources["ListItem.ChosenBar"] = new BoxShadows(chosen);
        application.Resources["ListItem.ChosenPartBar"] = new BoxShadows(chosenPart);
        application.Resources["ListItem.ChosenSelectedRing"] = new BoxShadows(selected, [chosen]);
        application.Resources["ListItem.ChosenPartSelectedRing"] = new BoxShadows(selected, [chosenPart]);

        // A row standing for what the graph highlights for a chosen cell carries the bar at the highlight's own strength,
        // so it reads as the edge's halo does: fainter than a selection.
        BoxShadow highlighted = new()
        {
            IsInset = true, OffsetX = ChosenBarWidth,
            Color = Color.FromArgb((byte)Math.Round(255 * CellHighlightStrength), ring.R, ring.G, ring.B),
        };
        application.Resources["ListItem.HighlightBar"] = new BoxShadows(highlighted);
        application.Resources["ListItem.HighlightSelectedRing"] = new BoxShadows(selected, [highlighted]);
    }

    /// <summary>
    /// Sets the control theme's accent, for the variant the mode draws in, to the mode's action fill. Left unset, the
    /// control theme takes the platform's accent colour, which no report measures.
    /// </summary>
    private static void PinAccent(Avalonia.Application application, ThemeMode mode, StatusTokens status)
    {
        if (application.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } controlTheme)
        {
            return;
        }

        ThemeVariant variant = ThemePalette.IsDark(mode) ? ThemeVariant.Dark : ThemeVariant.Light;
        Color accent = ToColor(status.ActionFill);
        if (controlTheme.Palettes.TryGetValue(variant, out ColorPaletteResources? palette))
        {
            palette.Accent = accent;
        }
        else
        {
            controlTheme.Palettes[variant] = new ColorPaletteResources { Accent = accent };
        }
    }

    /// <summary>The fill token of a mechanism, resolved through its family so no hue is invented.</summary>
    public static Color FillOf(Mechanism mechanism, ThemeMode mode) =>
        ToColor(ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(mechanism)).Fill);

    /// <summary>The ink token of a mechanism, for labels and legend text.</summary>
    public static Color InkOf(Mechanism mechanism, ThemeMode mode) =>
        ToColor(ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(mechanism)).Ink);

    public static Color ToColor(Srgb value) => Color.FromRgb(value.R, value.G, value.B);

    private static SolidColorBrush Brush(Srgb value) => new(ToColor(value));
}
