using System.Globalization;
using System.Text.Json;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The §6.6 palette requirements, enforced by measurement. A palette change that breaks one of these
/// fails here rather than in a review, and the committed report must match what the code computes.
/// </summary>
public sealed class ThemeContractTests
{
    [Fact(DisplayName = "R14: every ink token clears 4.5 to 1 against every surface it can land on")]
    public void InkClearsContrastOnEverySurface()
    {
        foreach (ThemeMode mode in (ThemeMode[])[ThemeMode.Dark, ThemeMode.Light])
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);
            foreach (ContrastResult result in report.InkContrast)
            {
                Assert.True(
                    result.Satisfied,
                    $"{mode}: {result.Token} on {result.Surface} measured {result.Ratio:F2} to 1, "
                    + $"required {result.Required:F1} to 1");
            }
        }
    }

    [Fact(DisplayName = "R14: every fill clears 3 to 1 against the ground it is drawn on")]
    public void FillClearsContrastOnItsGround()
    {
        foreach (ThemeMode mode in (ThemeMode[])[ThemeMode.Dark, ThemeMode.Light])
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);
            foreach (ContrastResult result in report.FillContrast)
            {
                Assert.True(
                    result.Satisfied,
                    $"{mode}: {result.Token} measured {result.Ratio:F2} to 1, required {result.Required:F1} to 1");
            }
        }
    }

    [Fact(DisplayName = "R14: adjacent families stay separable under every simulated vision model")]
    public void AdjacentFamiliesStaySeparable()
    {
        foreach (ThemeMode mode in (ThemeMode[])[ThemeMode.Dark, ThemeMode.Light])
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);
            foreach (SeparationResult result in report.Separations)
            {
                Assert.True(
                    result.Satisfied,
                    $"{mode}: {result.First} to {result.Second} under {result.Model} measured "
                    + $"{result.Distance:F1}, required {result.Required:F0}");
            }
        }
    }

    [Fact(DisplayName = "P24: the unknown grey is never reused for a supported mechanism")]
    public void UnknownGreyIsReserved()
    {
        foreach (ThemeMode mode in (ThemeMode[])[ThemeMode.Dark, ThemeMode.Light])
        {
            FamilyTokens unknown = ThemePalette.TokensFor(mode, MechanismFamily.UnknownMechanism);
            foreach (FamilyTokens family in ThemePalette.Families(mode))
            {
                if (family.Family == MechanismFamily.UnknownMechanism)
                {
                    continue;
                }

                Assert.NotEqual(unknown.Fill, family.Fill);
                Assert.NotEqual(unknown.Ink, family.Ink);
            }
        }
    }

    [Fact(DisplayName = "P24: every mechanism resolves to exactly one family, and every family has a glyph")]
    public void EveryMechanismHasOneFamilyAndAGlyph()
    {
        foreach (Mechanism mechanism in Enum.GetValues<Mechanism>())
        {
            MechanismFamily family = ThemePalette.FamilyOf(mechanism);
            FamilyTokens tokens = ThemePalette.TokensFor(ThemeMode.Dark, family);

            // The glyph is the redundant channel that carries the same meaning without colour (R14).
            Assert.False(string.IsNullOrWhiteSpace(tokens.Glyph));
            Assert.False(string.IsNullOrWhiteSpace(tokens.Label));
        }

        Assert.Equal(ThemePalette.Order.Count, ThemePalette.Families(ThemeMode.Dark).Count);
        Assert.Equal(ThemePalette.Order.Count, ThemePalette.Families(ThemeMode.Light).Count);
    }

    [Fact(DisplayName = "R20: the committed theme report matches what the palette computes today")]
    public void CommittedReportMatchesTheCode()
    {
        string root = RepositoryRoot();
        string path = Path.Combine(root, "theme", "contrast-report.json");
        Assert.True(File.Exists(path), "theme/contrast-report.json is the recorded verification and must exist.");

        using JsonDocument committed = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(ThemePalette.ThemeVersion, committed.RootElement.GetProperty("themeVersion").GetString());
        Assert.True(committed.RootElement.GetProperty("satisfied").GetBoolean());

        ThemeReport current = ThemeVerification.Verify(DateTimeOffset.UnixEpoch);
        foreach (JsonElement mode in committed.RootElement.GetProperty("modes").EnumerateArray())
        {
            string name = mode.GetProperty("mode").GetString()!;
            ThemeModeReport live = current.Modes.Single(candidate => candidate.Mode == name);

            foreach (JsonElement entry in mode.GetProperty("inkContrast").EnumerateArray())
            {
                ContrastResult match = live.InkContrast.Single(candidate =>
                    candidate.Token == entry.GetProperty("token").GetString()
                    && candidate.Surface == entry.GetProperty("surface").GetString());
                Assert.Equal(
                    entry.GetProperty("ratio").GetDouble(),
                    match.Ratio,
                    2);
            }

            foreach (JsonElement entry in mode.GetProperty("separations").EnumerateArray())
            {
                SeparationResult match = live.Separations.Single(candidate =>
                    candidate.First == entry.GetProperty("first").GetString()
                    && candidate.Second == entry.GetProperty("second").GetString()
                    && candidate.Model == entry.GetProperty("model").GetString());
                Assert.Equal(
                    entry.GetProperty("distance").GetDouble(),
                    match.Distance,
                    2);
            }
        }
    }

    [Theory(DisplayName = "R20: colour measurements match published reference values")]
    [InlineData("#FFFFFF", "#000000", 21.0)]
    [InlineData("#777777", "#FFFFFF", 4.48)]
    [InlineData("#0000FF", "#FFFFFF", 8.59)]
    public void ContrastMatchesReferenceValues(string first, string second, double expected)
    {
        double measured = ColorMath.ContrastRatio(Srgb.Parse(first), Srgb.Parse(second));

        Assert.Equal(expected, Math.Round(measured, 2), 1);
    }

    [Fact(DisplayName = "R20: greyscale simulation keeps luminance and drops chroma")]
    public void GreyscaleSimulationKeepsLuminance()
    {
        var color = Srgb.Parse("#0BA2F7");
        Srgb grey = ColorMath.Simulate(color, VisionModel.Greyscale);

        Assert.Equal(grey.R, grey.G);
        Assert.Equal(grey.G, grey.B);
        // Eight-bit quantisation costs at most one step, so the comparison is to two decimal places.
        Assert.Equal(
            ColorMath.RelativeLuminance(color),
            ColorMath.RelativeLuminance(grey),
            2);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"Could not locate the repository root."));
    }
}
