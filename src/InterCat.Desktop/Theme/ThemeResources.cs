using Avalonia;
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
        application.Resources["Line.Divider"] = Brush(surfaces.Elevated);

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

        application.RequestedThemeVariant = mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (CurrentMode != mode)
        {
            CurrentMode = mode;
            ModeChanged?.Invoke(null, EventArgs.Empty);
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
