using System.Globalization;

namespace InterCat.Desktop.Theme;

/// <summary>One measured contrast result between an ink or fill token and a surface.</summary>
public sealed record ContrastResult(
    string Token,
    string Surface,
    string TokenHex,
    string SurfaceHex,
    double Ratio,
    double Required,
    bool Satisfied);

/// <summary>One measured separation between two adjacent families, under one vision model.</summary>
public sealed record SeparationResult(
    string First,
    string Second,
    string Model,
    double Distance,
    double Required,
    bool Satisfied);

/// <summary>
/// The verification of one theme mode. Status separations measure the caution ink against every family, since a
/// warning must not read as a mechanism (§6.6).
/// </summary>
public sealed record ThemeModeReport(
    string Mode,
    IReadOnlyList<ContrastResult> InkContrast,
    IReadOnlyList<ContrastResult> FillContrast,
    IReadOnlyList<SeparationResult> Separations,
    IReadOnlyList<SeparationResult> StatusSeparations,
    bool Satisfied);

/// <summary>The complete theme verification, written beside the theme definition (§21.2 item 9).</summary>
public sealed record ThemeReport(
    string ThemeVersion,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<ThemeModeReport> Modes,
    IReadOnlyList<string> Thresholds,
    bool Satisfied);

/// <summary>
/// Measures every requirement §6.6 states, so the palette is enforced by computation rather than by
/// judgement. A failing entry names the exact token pair and the measured value.
/// </summary>
public static class ThemeVerification
{
    public static ThemeReport Verify(DateTimeOffset generatedUtc)
    {
        List<ThemeModeReport> modes = [.. ThemePalette.Modes.Select(VerifyMode)];

        bool satisfied = true;
        foreach (ThemeModeReport mode in modes)
        {
            satisfied &= mode.Satisfied;
        }

        return new(
            ThemePalette.ThemeVersion,
            generatedUtc,
            modes,
            [
                string.Create(CultureInfo.InvariantCulture, $"Ink against every surface token: at least {ThemePalette.MinimumInkContrast:F1} to 1"),
                string.Create(CultureInfo.InvariantCulture, $"Fill against its plot ground: at least {ThemePalette.MinimumFillContrast:F1} to 1"),
                string.Create(CultureInfo.InvariantCulture, $"Adjacent families in normal vision: at least {ThemePalette.MinimumAdjacentDistance:F0} CIE76"),
                string.Create(CultureInfo.InvariantCulture, $"Adjacent families under each simulated deficiency: at least {ThemePalette.MinimumSimulatedDistance:F0} CIE76"),
                string.Create(CultureInfo.InvariantCulture, $"Adjacent families in greyscale: at least {ThemePalette.MinimumGreyscaleLightness:F0} CIELAB lightness"),
                string.Create(CultureInfo.InvariantCulture, $"Any two families in normal vision, neighbours or not: at least {ThemePalette.MinimumAnyPairDistance:F0} CIE76"),
                string.Create(CultureInfo.InvariantCulture, $"Caution ink against every family's fill and ink in normal vision: at least {ThemePalette.MinimumStatusDistance:F0} CIE76"),
                string.Create(CultureInfo.InvariantCulture, $"Action ink on the action fill in each state: at least {ThemePalette.MinimumInkContrast:F1} to 1; the fill against every surface: at least {ThemePalette.MinimumFillContrast:F1} to 1"),
                string.Create(CultureInfo.InvariantCulture, $"In a high-contrast mode every ink, the action ink included, clears {ThemePalette.MinimumHighContrastInk:F1} to 1, every fill {ThemePalette.MinimumHighContrastFill:F1} to 1, and the divider {ThemePalette.MinimumHighContrastDivider:F1} to 1 against every surface"),
                "Hatches and warning patterns are reserved for coverage and quality; no family may use one.",
                "The unknown grey is never reused for a supported mechanism.",
            ],
            satisfied);
    }

    public static ThemeModeReport VerifyMode(ThemeMode mode)
    {
        SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
        IReadOnlyList<FamilyTokens> families = ThemePalette.Families(mode);
        string[] surfaceNames = ["canvas", "panel", "plot", "elevated"];

        double inkRequired = ThemePalette.MinimumInkContrastIn(mode);
        double fillRequired = ThemePalette.MinimumFillContrastIn(mode);
        var inkResults = new List<ContrastResult>();
        var fillResults = new List<ContrastResult>();
        foreach (FamilyTokens family in families)
        {
            for (int index = 0; index < surfaces.All.Count; index++)
            {
                Srgb surface = surfaces.All[index];
                double ratio = ColorMath.ContrastRatio(family.Ink, surface);
                inkResults.Add(new(
                    $"{family.Family} ink",
                    surfaceNames[index],
                    family.Ink.ToHex(),
                    surface.ToHex(),
                    Math.Round(ratio, 2),
                    inkRequired,
                    ratio >= inkRequired));
            }

            double fillRatio = ColorMath.ContrastRatio(family.Fill, surfaces.Plot);
            fillResults.Add(new(
                $"{family.Family} fill",
                "plot",
                family.Fill.ToHex(),
                surfaces.Plot.ToHex(),
                Math.Round(fillRatio, 2),
                fillRequired,
                fillRatio >= fillRequired));
        }

        MeasureAgainstSurfaces(inkResults, "body ink", surfaces.Ink, surfaces, surfaceNames, inkRequired);
        MeasureAgainstSurfaces(inkResults, "muted ink", surfaces.MutedInk, surfaces, surfaceNames, inkRequired);
        MeasureAgainstSurfaces(inkResults, "accent", surfaces.Accent, surfaces, surfaceNames, inkRequired);

        // The caution ink words warnings on any surface and strokes the hatch on the plot; the action ink is read on
        // the action fill in every state it can be drawn in, and that fill must stand out from the surface under it.
        StatusTokens status = ThemePalette.Status(mode);
        MeasureAgainstSurfaces(inkResults, "caution", status.Caution, surfaces, surfaceNames, inkRequired);
        foreach ((string name, Srgb fill) in status.ActionStates)
        {
            double ratio = ColorMath.ContrastRatio(status.ActionInk, fill);
            inkResults.Add(new(
                "action ink",
                name,
                status.ActionInk.ToHex(),
                fill.ToHex(),
                Math.Round(ratio, 2),
                inkRequired,
                ratio >= inkRequired));

            MeasureAgainstSurfaces(fillResults, name, fill, surfaces, surfaceNames, fillRequired);
        }

        // A high-contrast mode edges panes, cards and controls with its divider, which must be seen on every surface; the
        // dark and light dividers are the elevated tone, a quiet seam by design.
        if (ThemePalette.IsHighContrast(mode))
        {
            MeasureAgainstSurfaces(fillResults, "divider", surfaces.Divider, surfaces, surfaceNames,
                ThemePalette.MinimumHighContrastDivider);
        }

        var statusSeparations = new List<SeparationResult>();
        foreach (FamilyTokens family in families)
        {
            (string Variant, Srgb Token)[] variants = [("fill", family.Fill), ("ink", family.Ink)];
            foreach ((string variant, Srgb token) in variants)
            {
                double distance = ColorMath.PerceptualDistance(status.Caution, token);
                statusSeparations.Add(new(
                    "Caution",
                    $"{family.Family} {variant}",
                    VisionModel.Normal.ToString(),
                    Math.Round(distance, 2),
                    ThemePalette.MinimumStatusDistance,
                    distance >= ThemePalette.MinimumStatusDistance));
            }
        }

        var separations = new List<SeparationResult>();
        IReadOnlyList<MechanismFamily> order = ThemePalette.Order;
        VisionModel[] simulated =
        [
            VisionModel.Normal,
            VisionModel.Protanopia,
            VisionModel.Deuteranopia,
            VisionModel.Tritanopia,
        ];

        for (int index = 1; index < order.Count; index++)
        {
            FamilyTokens first = ThemePalette.TokensFor(mode, order[index - 1]);
            FamilyTokens second = ThemePalette.TokensFor(mode, order[index]);
            foreach (VisionModel model in simulated)
            {
                double required = model == VisionModel.Normal
                    ? ThemePalette.MinimumAdjacentDistance
                    : ThemePalette.MinimumSimulatedDistance;
                double distance = ColorMath.PerceptualDistance(
                    ColorMath.Simulate(first.Fill, model),
                    ColorMath.Simulate(second.Fill, model));
                separations.Add(new(
                    first.Family.ToString(),
                    second.Family.ToString(),
                    model.ToString(),
                    Math.Round(distance, 2),
                    required,
                    distance >= required));
            }

            double lightness = ColorMath.LightnessDistance(
                ColorMath.Simulate(first.Fill, VisionModel.Greyscale),
                ColorMath.Simulate(second.Fill, VisionModel.Greyscale));
            separations.Add(new(
                first.Family.ToString(),
                second.Family.ToString(),
                VisionModel.Greyscale.ToString(),
                Math.Round(lightness, 2),
                ThemePalette.MinimumGreyscaleLightness,
                lightness >= ThemePalette.MinimumGreyscaleLightness));
        }

        // Any two families, wherever they sit in palette order, are read side by side in the legend.
        for (int first = 0; first < order.Count; first++)
        {
            for (int second = first + 2; second < order.Count; second++)
            {
                FamilyTokens one = ThemePalette.TokensFor(mode, order[first]);
                FamilyTokens other = ThemePalette.TokensFor(mode, order[second]);
                double distance = ColorMath.PerceptualDistance(one.Fill, other.Fill);
                separations.Add(new(
                    one.Family.ToString(),
                    other.Family.ToString(),
                    "AnyPair",
                    Math.Round(distance, 2),
                    ThemePalette.MinimumAnyPairDistance,
                    distance >= ThemePalette.MinimumAnyPairDistance));
            }
        }

        bool satisfied = true;
        foreach (ContrastResult result in inkResults)
        {
            satisfied &= result.Satisfied;
        }

        foreach (ContrastResult result in fillResults)
        {
            satisfied &= result.Satisfied;
        }

        foreach (SeparationResult result in separations)
        {
            satisfied &= result.Satisfied;
        }

        foreach (SeparationResult result in statusSeparations)
        {
            satisfied &= result.Satisfied;
        }

        return new(mode.ToString(), inkResults, fillResults, separations, statusSeparations, satisfied);
    }

    /// <summary>Measures <paramref name="token"/> against every surface it can land on.</summary>
    private static void MeasureAgainstSurfaces(
        List<ContrastResult> results,
        string name,
        Srgb token,
        SurfaceTokens surfaces,
        string[] surfaceNames,
        double required)
    {
        for (int index = 0; index < surfaces.All.Count; index++)
        {
            Srgb surface = surfaces.All[index];
            double ratio = ColorMath.ContrastRatio(token, surface);
            results.Add(new(
                name,
                surfaceNames[index],
                token.ToHex(),
                surface.ToHex(),
                Math.Round(ratio, 2),
                required,
                ratio >= required));
        }
    }
}
