using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Desktop.Theme;

/// <summary>
/// The three files <c>theme/</c> holds - the palette's definition, its measured verification and their summary - as
/// <c>tools/InterCat.ThemeReport</c> writes them (§21.2 item 11). A test compares the committed files with this text,
/// so a token, a target or a measurement that changes without them fails a test rather than a review (§6.6, ADR-050).
/// Lines end in a line feed on every platform, so a regeneration anywhere writes the same bytes.
/// </summary>
public static class ThemeFiles
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The palette's definition: its version, motion, family order and every mode's tokens.</summary>
    public static string Tokens() => JsonSerializer.Serialize(
        new
        {
            themeVersion = ThemePalette.ThemeVersion,
            motion = new
            {
                selectionMilliseconds = ThemePalette.Motion.Selection.TotalMilliseconds,
                levelChangeMilliseconds = ThemePalette.Motion.LevelChange.TotalMilliseconds,
            },
            order = ThemePalette.Order,
            modes = ThemePalette.Modes.Select(mode => new
            {
                mode = mode.ToString(),
                surfaces = new
                {
                    canvas = ThemePalette.Surfaces(mode).Canvas.ToHex(),
                    panel = ThemePalette.Surfaces(mode).Panel.ToHex(),
                    plot = ThemePalette.Surfaces(mode).Plot.ToHex(),
                    elevated = ThemePalette.Surfaces(mode).Elevated.ToHex(),
                    accent = ThemePalette.Surfaces(mode).Accent.ToHex(),
                    ink = ThemePalette.Surfaces(mode).Ink.ToHex(),
                    mutedInk = ThemePalette.Surfaces(mode).MutedInk.ToHex(),
                    divider = ThemePalette.Surfaces(mode).Divider.ToHex(),
                },
                status = new
                {
                    caution = ThemePalette.Status(mode).Caution.ToHex(),
                    actionFill = ThemePalette.Status(mode).ActionFill.ToHex(),
                    actionFillHover = ThemePalette.Status(mode).ActionFillHover.ToHex(),
                    actionFillPressed = ThemePalette.Status(mode).ActionFillPressed.ToHex(),
                    actionInk = ThemePalette.Status(mode).ActionInk.ToHex(),
                },
                families = ThemePalette.Families(mode).Select(family => new
                {
                    family = family.Family.ToString(),
                    family.Label,
                    family.Glyph,
                    fill = family.Fill.ToHex(),
                    ink = family.Ink.ToHex(),
                }),
            }),
        },
        Options);

    /// <summary>The measured verification: every measurement, with the target it is held to and whether it met it.</summary>
    public static string Report(ThemeReport report) => JsonSerializer.Serialize(report, Options);

    /// <summary>
    /// The summary: the targets, then each mode's checks, each with the measurement nearest its own target. Nearest is
    /// measured against each one's target, not by the value alone: a high-contrast mode holds its fills to 4.5 to 1 and
    /// its divider to 3, so a fill that fails at 4.4 is the row to show, not a divider that passes at 3.2.
    /// </summary>
    public static string Summary(ThemeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var summary = new StringBuilder();
        Line(summary, "# Theme verification");
        Line(summary, string.Empty);
        Line(summary, string.Create(CultureInfo.InvariantCulture,
            $"Theme version {report.ThemeVersion}, generated {report.GeneratedUtc:yyyy-MM-dd}."));
        Line(summary, string.Empty);
        foreach (string threshold in report.Thresholds)
        {
            Line(summary, "- " + threshold);
        }

        Line(summary, string.Create(CultureInfo.InvariantCulture,
            $"- Motion: a selection's ring settles in {ThemePalette.Motion.Selection.TotalMilliseconds:0} ms and a level change or a layout settling moves nodes in {ThemePalette.Motion.LevelChange.TotalMilliseconds:0} ms; nothing animates per message, and reduced motion draws every change where it ends at once."));

        foreach (ThemeModeReport mode in report.Modes)
        {
            Line(summary, string.Empty);
            Line(summary, "## " + mode.Mode);
            Line(summary, string.Empty);
            Line(summary, "| Check | Worst measured | Required | Result |");
            Line(summary, "|---|---|---|---|");
            WorstContrast(summary, "Ink contrast", mode.InkContrast);
            WorstContrast(summary, "Fill contrast", mode.FillContrast);
            WorstSeparation(summary, "Separation", mode.Separations);
            WorstSeparation(summary, "Caution from every family", mode.StatusSeparations);
        }

        return summary.ToString();
    }

    private static void WorstContrast(StringBuilder summary, string label, IReadOnlyList<ContrastResult> results)
    {
        ContrastResult worst = results.OrderBy(result => result.Ratio / result.Required).First();
        Line(summary, string.Create(CultureInfo.InvariantCulture,
            $"| {label} | {worst.Ratio:F2} to 1 ({worst.Token} on {worst.Surface}) | {worst.Required:F1} to 1 | {Result(worst.Satisfied)} |"));
    }

    private static void WorstSeparation(StringBuilder summary, string label, IReadOnlyList<SeparationResult> results)
    {
        // A vision model holds every pair to one target, so its nearest is its smallest.
        foreach (IGrouping<string, SeparationResult> group in results.GroupBy(result => result.Model))
        {
            SeparationResult worst = group.OrderBy(result => result.Distance).First();
            Line(summary, string.Create(CultureInfo.InvariantCulture,
                $"| {label}, {group.Key} | {worst.Distance:F1} ({worst.First} to {worst.Second}) | {worst.Required:F0} | {Result(worst.Satisfied)} |"));
        }
    }

    private static string Result(bool satisfied) => satisfied ? "met" : "NOT MET";

    private static void Line(StringBuilder summary, string text) => summary.Append(text).Append('\n');

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
