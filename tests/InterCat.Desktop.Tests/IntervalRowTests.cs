using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The interval table's rows (§6.2's table equivalent, R15): a row keys the mechanism its records mostly are, and a row
/// with no record keys none, so an empty interval reads neither as a "?" nor, to a screen reader, as an unknown mechanism.
/// </summary>
public sealed class IntervalRowTests
{
    [Fact(DisplayName = "R15: an interval holding no record keys no mechanism in the interval table, by glyph or by name")]
    public void AnEmptyIntervalKeysNoMechanism()
    {
        IReadOnlyList<IntervalRow> rows = WorkspaceRowBuilder.Intervals(
        [
            new TimelineBucket(new TimeRange(0, 10), 3, null, Mechanism.Tcp, CoverageState.Covered),
            new TimelineBucket(new TimeRange(10, 20), 0, null, Mechanism.UnknownMechanism, CoverageState.Covered),
        ], ThemeMode.Dark, sumsBytes: false);

        // A row with records keys their mechanism, by its glyph and, read aloud, by its name.
        LegendEntry tcp = LegendEntry.For(Mechanism.Tcp, ThemeMode.Dark);
        Assert.Equal((tcp.Glyph, tcp.Label), (rows[0].Glyph, rows[0].Mechanism));
        Assert.Contains($", {tcp.Label}, ", rows[0].AccessibleName, StringComparison.Ordinal);

        // A row with none keys nothing, and says only its window, its count of none and its coverage.
        Assert.Equal((string.Empty, string.Empty), (rows[1].Glyph, rows[1].Mechanism));
        Assert.Equal($"{rows[1].Window}, 0 observations, {Spoken.Coverage(rows[1].Coverage)}", rows[1].AccessibleName);
    }
}
