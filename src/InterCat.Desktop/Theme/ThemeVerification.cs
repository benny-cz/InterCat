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

/// <summary>The verification of one theme mode.</summary>
public sealed record ThemeModeReport(
    string Mode,
    IReadOnlyList<ContrastResult> InkContrast,
    IReadOnlyList<ContrastResult> FillContrast,
    IReadOnlyList<SeparationResult> Separations,
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
        List<ThemeModeReport> modes = [VerifyMode(ThemeMode.Dark), VerifyMode(ThemeMode.Light)];

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
                    ThemePalette.MinimumInkContrast,
                    ratio >= ThemePalette.MinimumInkContrast));
            }

            double fillRatio = ColorMath.ContrastRatio(family.Fill, surfaces.Plot);
            fillResults.Add(new(
                $"{family.Family} fill",
                "plot",
                family.Fill.ToHex(),
                surfaces.Plot.ToHex(),
                Math.Round(fillRatio, 2),
                ThemePalette.MinimumFillContrast,
                fillRatio >= ThemePalette.MinimumFillContrast));
        }

        AddSurfaceInk(inkResults, "body ink", surfaces.Ink, surfaces, surfaceNames);
        AddSurfaceInk(inkResults, "muted ink", surfaces.MutedInk, surfaces, surfaceNames);
        AddSurfaceInk(inkResults, "accent", surfaces.Accent, surfaces, surfaceNames);

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

        return new(mode.ToString(), inkResults, fillResults, separations, satisfied);
    }

    private static void AddSurfaceInk(
        List<ContrastResult> results,
        string name,
        Srgb token,
        SurfaceTokens surfaces,
        string[] surfaceNames)
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
                ThemePalette.MinimumInkContrast,
                ratio >= ThemePalette.MinimumInkContrast));
        }
    }
}
