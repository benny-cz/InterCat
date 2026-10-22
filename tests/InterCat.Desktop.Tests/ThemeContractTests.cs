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
        foreach (ThemeMode mode in ThemePalette.Modes)
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
        foreach (ThemeMode mode in ThemePalette.Modes)
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

    [Fact(DisplayName = "R14: a high-contrast mode holds every ink to 7 to 1, every fill to 4.5 to 1 and its divider to 3 to 1 on every surface")]
    public void HighContrastHoldsTheEnhancedRatios()
    {
        foreach (ThemeMode mode in ThemePalette.Modes.Where(ThemePalette.IsHighContrast))
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);

            // The stricter ratios are what the report measured against, not a label on the ordinary ones.
            Assert.All(report.InkContrast, result => Assert.Equal(ThemePalette.MinimumHighContrastInk, result.Required));
            Assert.All(report.InkContrast, result => Assert.True(result.Satisfied,
                $"{mode}: {result.Token} on {result.Surface} measured {result.Ratio:F2} to 1"));
            ContrastResult[] fills = [.. report.FillContrast.Where(result => result.Token != "divider")];
            Assert.All(fills, result => Assert.Equal(ThemePalette.MinimumHighContrastFill, result.Required));
            Assert.All(fills, result => Assert.True(result.Satisfied,
                $"{mode}: {result.Token} on {result.Surface} measured {result.Ratio:F2} to 1"));

            // Its divider edges panes, cards and controls, so it must be seen on every surface it can separate.
            ContrastResult[] divider = [.. report.FillContrast.Where(result => result.Token == "divider")];
            Assert.Equal(4, divider.Length);
            Assert.All(divider, result => Assert.True(result.Satisfied,
                $"{mode}: the divider on {result.Surface} measured {result.Ratio:F2} to 1"));
        }

        // The ordinary modes keep their quiet seam, which is the elevated tone and not a line to be read.
        foreach (ThemeMode mode in ThemePalette.Modes.Where(mode => !ThemePalette.IsHighContrast(mode)))
        {
            Assert.Equal(ThemePalette.Surfaces(mode).Elevated, ThemePalette.Surfaces(mode).Divider);
            Assert.DoesNotContain(ThemeVerification.VerifyMode(mode).FillContrast, result => result.Token == "divider");
        }
    }

    [Fact(DisplayName = "R14: adjacent families stay separable under every simulated vision model, and any two in normal vision")]
    public void AdjacentFamiliesStaySeparable()
    {
        foreach (ThemeMode mode in ThemePalette.Modes)
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
        foreach (ThemeMode mode in ThemePalette.Modes)
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

    [Fact(DisplayName = "P24: the caution ink that strokes the coverage hatch and words a warning keeps its distance from every family's fill and ink")]
    public void CautionIsNoFamilysHue()
    {
        foreach (ThemeMode mode in ThemePalette.Modes)
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);

            // Both variants of every family are measured, so a family added later cannot slip past the check.
            Assert.Equal(ThemePalette.Families(mode).Count * 2, report.StatusSeparations.Count);
            foreach (SeparationResult result in report.StatusSeparations)
            {
                Assert.True(
                    result.Satisfied,
                    $"{mode}: caution to {result.Second} measured {result.Distance:F1}, required {result.Required:F0}");
            }

            // And the caution ink is legible as words on every surface a warning can sit on.
            Assert.Equal(4, report.InkContrast.Count(result => result.Token == "caution" && result.Satisfied));
        }
    }

    [Fact(DisplayName = "R14: the primary action's ink clears 4.5 to 1 on its fill in every state, and that fill is no family's")]
    public void PrimaryActionIsLegibleInEveryState()
    {
        foreach (ThemeMode mode in ThemePalette.Modes)
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);
            foreach ((string name, Srgb fill) in ThemePalette.Status(mode).ActionStates)
            {
                ContrastResult ink = Assert.Single(report.InkContrast,
                    result => result.Token == "action ink" && result.Surface == name);
                Assert.True(ink.Satisfied, $"{mode}: the action ink on the {name} measured {ink.Ratio:F2} to 1");

                // The button's edge is its fill against whatever surface holds it.
                ContrastResult[] edges = [.. report.FillContrast.Where(result => result.Token == name)];
                Assert.Equal(4, edges.Length);
                Assert.All(edges, edge => Assert.True(edge.Satisfied,
                    $"{mode}: the {name} on {edge.Surface} measured {edge.Ratio:F2} to 1"));

                // The primary action once wore the TCP fill with white text: 2.79 to 1 in the dark mode.
                Assert.DoesNotContain(ThemePalette.Families(mode), family => family.Fill == fill || family.Ink == fill);
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

        foreach (ThemeMode mode in ThemePalette.Modes)
        {
            // Every mode keys the same families with the same glyphs and names; only the colours differ.
            Assert.Equal(ThemePalette.Order, ThemePalette.Families(mode).Select(family => family.Family));
            Assert.Equal(
                ThemePalette.Families(ThemeMode.Dark).Select(family => (family.Glyph, family.Label)),
                ThemePalette.Families(mode).Select(family => (family.Glyph, family.Label)));
        }
    }

    [Fact(DisplayName = "P24: every mechanism a capture source can collect wears a family of its own, never the unknown grey")]
    public void EveryCollectableMechanismHasAFamilyOfItsOwn()
    {
        // Revision 165 found process lifecycle, which every capture collects, drawn and keyed as Unknown. The families'
        // colours were all measured, and none of those measurements asked which mechanisms reach the grey.
        Mechanism[] collectable = [.. InterCat.Capture.Windows.WindowsSourceCatalog.All
            .SelectMany(source => source.Mechanisms.Concat(source.MechanismsOmittedByProfile))
            .Distinct()
            .Order()];
        Assert.Contains(Mechanism.ProcessLifecycle, collectable);
        Assert.Contains(Mechanism.ThreadLifecycle, collectable);
        Assert.All(collectable, mechanism => Assert.NotEqual(MechanismFamily.UnknownMechanism, ThemePalette.FamilyOf(mechanism)));
        Assert.Equal(MechanismFamily.UnknownMechanism, ThemePalette.FamilyOf(Mechanism.UnknownMechanism));

        // Lifecycle is its own neutral family, keyed by name in every mode, rather than borrowing an IPC hue.
        Assert.Equal(MechanismFamily.Lifecycle, ThemePalette.FamilyOf(Mechanism.ProcessLifecycle));
        Assert.Equal(MechanismFamily.Lifecycle, ThemePalette.FamilyOf(Mechanism.ThreadLifecycle));
        Assert.All(ThemePalette.Modes, mode => Assert.Equal("Lifecycle", ThemePalette.TokensFor(mode, MechanismFamily.Lifecycle).Label));
    }

    [Fact(DisplayName = "R20: theme/ holds exactly what the report tool writes from the palette today: its tokens, every measurement and the summary")]
    public void CommittedThemeFilesAreWhatTheToolWrites()
    {
        // The measurements alone once stood for the files: a glyph, a label, a hex, a target or a summary row could
        // change in the code while the committed definition kept the old one. Each file is compared whole, with the
        // time the committed report was generated, so only regenerating them with the tool makes this pass.
        string root = RepositoryRoot();
        using JsonDocument committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "theme", "contrast-report.json")));
        ThemeReport report = ThemeVerification.Verify(committed.RootElement.GetProperty("generatedUtc").GetDateTimeOffset());

        Assert.True(report.Satisfied);
        Assert.Equal(ThemeFiles.Tokens(), Committed(root, "tokens.json"));
        Assert.Equal(ThemeFiles.Report(report), Committed(root, "contrast-report.json"));
        Assert.Equal(ThemeFiles.Summary(report), Committed(root, "README.md"));
    }

    [Fact(DisplayName = "R14: the palette is held to the targets ADR-050 records, so a lower one is a decision, not a regenerated report")]
    public void TheTargetsAreTheRecordedOnes()
    {
        // A lowered target passes every measurement once the report is regenerated with it, so the targets themselves are
        // held here: changing one changes ADR-050 first.
        double[] targets =
        [
            ThemePalette.MinimumInkContrast, ThemePalette.MinimumFillContrast, ThemePalette.MinimumAdjacentDistance,
            ThemePalette.MinimumSimulatedDistance, ThemePalette.MinimumGreyscaleLightness, ThemePalette.MinimumAnyPairDistance,
            ThemePalette.MinimumStatusDistance, ThemePalette.MinimumHighContrastInk, ThemePalette.MinimumHighContrastFill,
            ThemePalette.MinimumHighContrastDivider,
        ];
        Assert.Equal([4.5, 3.0, 18.0, 10.0, 6.0, 15.0, 18.0, 7.0, 4.5, 3.0], targets);

        // And every measurement is held to the target of its kind, in every mode.
        foreach (ThemeMode mode in ThemePalette.Modes)
        {
            ThemeModeReport report = ThemeVerification.VerifyMode(mode);
            bool high = ThemePalette.IsHighContrast(mode);
            Assert.All(report.InkContrast, result => Assert.Equal(high ? 7.0 : 4.5, result.Required));
            Assert.All(report.FillContrast, result =>
                Assert.Equal(result.Token == "divider" || !high ? 3.0 : 4.5, result.Required));
            Assert.All(report.Separations, result => Assert.Equal(
                result.Model switch { "Normal" => 18.0, "AnyPair" => 15.0, "Greyscale" => 6.0, _ => 10.0 },
                result.Required));
            Assert.All(report.StatusSeparations, result => Assert.Equal(18.0, result.Required));
        }
    }

    [Fact(DisplayName = "R20: the summary shows each check's measurement nearest its own target, so a failing one is never hidden by a lower one that passes")]
    public void TheSummaryShowsTheMeasurementNearestItsTarget()
    {
        // A high-contrast mode holds its fills to 4.5 to 1 and its divider to 3. Shown by value alone, the divider's 3.2
        // was the worst row and read "met" while a fill failed at 4.4.
        ContrastResult[] ink = [new("body ink", "canvas", "#FFFFFF", "#000000", 21.0, 7.0, true)];
        ContrastResult[] fills =
        [
            new("divider", "elevated", "#5A5A5A", "#1A1A1A", 3.2, 3.0, true),
            new("Tcp fill", "plot", "#3A6AA0", "#000000", 4.4, 4.5, false),
        ];
        ThemeReport report = new("0.0.0", DateTimeOffset.UnixEpoch,
            [new("HighContrastDark", ink, fills, [], [], false)], [], false);

        string summary = ThemeFiles.Summary(report);

        Assert.Contains("| Fill contrast | 4.40 to 1 (Tcp fill on plot) | 4.5 to 1 | NOT MET |\n", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("divider", summary, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R20: the committed theme measurements match what the palette computes today, each named where one drifts")]
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

            foreach (JsonElement entry in mode.GetProperty("fillContrast").EnumerateArray())
            {
                ContrastResult match = live.FillContrast.Single(candidate =>
                    candidate.Token == entry.GetProperty("token").GetString()
                    && candidate.Surface == entry.GetProperty("surface").GetString());
                Assert.Equal(entry.GetProperty("ratio").GetDouble(), match.Ratio, 2);
            }

            (string Property, IReadOnlyList<SeparationResult> Results)[] measured =
                [("separations", live.Separations), ("statusSeparations", live.StatusSeparations)];
            foreach ((string property, IReadOnlyList<SeparationResult> results) in measured)
            {
                JsonElement[] entries = [.. mode.GetProperty(property).EnumerateArray()];
                Assert.Equal(results.Count, entries.Length);
                foreach (JsonElement entry in entries)
                {
                    SeparationResult match = results.Single(candidate =>
                        candidate.First == entry.GetProperty("first").GetString()
                        && candidate.Second == entry.GetProperty("second").GetString()
                        && candidate.Model == entry.GetProperty("model").GetString());
                    Assert.Equal(
                        entry.GetProperty("distance").GetDouble(),
                        match.Distance,
                        2);
                }
            }

            Assert.Equal(live.InkContrast.Count, mode.GetProperty("inkContrast").GetArrayLength());
            Assert.Equal(live.FillContrast.Count, mode.GetProperty("fillContrast").GetArrayLength());
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

    [Fact(DisplayName = "§6.8: the motion tokens are 120 ms for a selection and 200 ms for a level change, as the committed theme states")]
    public void TheMotionTokensAreTheCommittedOnes()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(120), ThemePalette.Motion.Selection);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ThemePalette.Motion.LevelChange);

        using JsonDocument committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "theme", "tokens.json")));
        Assert.Equal(ThemePalette.ThemeVersion, committed.RootElement.GetProperty("themeVersion").GetString());
        JsonElement motion = committed.RootElement.GetProperty("motion");
        Assert.Equal(ThemePalette.Motion.Selection.TotalMilliseconds, motion.GetProperty("selectionMilliseconds").GetDouble());
        Assert.Equal(ThemePalette.Motion.LevelChange.TotalMilliseconds, motion.GetProperty("levelChangeMilliseconds").GetDouble());
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

    // A checkout may turn line feeds into carriage return and line feed; the tool writes line feeds everywhere.
    private static string Committed(string root, string name) =>
        File.ReadAllText(Path.Combine(root, "theme", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

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
