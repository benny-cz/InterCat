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

    [Fact(DisplayName = "R15: a run of empty intervals of one coverage is one row spanning them, and every interval holding a record a row of its own")]
    public void ARunOfEmptyIntervalsIsOneRow()
    {
        TimelineBucket[] buckets =
        [
            new(new TimeRange(0, 10), 3, null, Mechanism.Tcp, CoverageState.Covered),
            new(new TimeRange(10, 20), 0, null, Mechanism.UnknownMechanism, CoverageState.Covered),
            new(new TimeRange(20, 30), 0, null, Mechanism.UnknownMechanism, CoverageState.Covered),
            new(new TimeRange(30, 40), 0, null, Mechanism.UnknownMechanism, CoverageState.UnknownCoverage),
            new(new TimeRange(40, 50), 0, null, Mechanism.UnknownMechanism, CoverageState.UnknownCoverage),
            new(new TimeRange(50, 60), 0, null, Mechanism.UnknownMechanism, CoverageState.UnknownCoverage),
            new(new TimeRange(60, 70), 2, null, Mechanism.Tcp, CoverageState.Covered),
            new(new TimeRange(70, 80), 1, null, Mechanism.Tcp, CoverageState.Covered),
            new(new TimeRange(80, 90), 0, null, Mechanism.UnknownMechanism, CoverageState.Covered),
        ];
        IReadOnlyList<IntervalRow> rows = WorkspaceRowBuilder.Intervals(buckets, ThemeMode.Dark, sumsBytes: false);
        ListsDrawn(buckets, rows);

        // An empty run of one coverage is one row of its whole span; another coverage begins another row, since each row
        // states one; a record ends a run, and each interval holding one is its own row.
        Assert.Equal([(0, 10), (10, 30), (30, 60), (60, 70), (70, 80), (80, 90)],
            rows.Select(row => ((int)row.Interval.StartTicks, (int)row.Interval.EndTicks)));
        Assert.Equal([1, 2, 3, 1, 1, 1], rows.Select(row => row.Cells));
        Assert.Equal(["3", "0 in 2 intervals", "0 in 3 intervals", "2", "1", "0"], rows.Select(row => row.Observations));
        Assert.Equal($"{rows[2].Window}, 0 observations in 3 intervals, {Spoken.Coverage(rows[2].Coverage)}", rows[2].AccessibleName);
        Assert.Equal(CoverageStateText.Label(CoverageState.UnknownCoverage), rows[2].Coverage);

        // Beside a focus, a run's count of none in focus follows its own.
        TimelineBucket[] focus = [.. buckets.Select(bucket => bucket with { ObservationCount = Math.Min(bucket.ObservationCount, 1) })];
        rows = WorkspaceRowBuilder.Intervals(buckets, ThemeMode.Dark, focus, sumsBytes: false);
        Assert.Equal("0 in 2 intervals · 0 in focus", rows[1].Observations);
        Assert.Equal("3 · 1 in focus", rows[0].Observations);
    }

    /// <summary>
    /// Asserts <paramref name="rows"/> are the table equivalent of <paramref name="buckets"/> (R15): every bucket holding a
    /// record is a row of its own interval and count, and every other row joins a whole run of consecutive empty buckets of
    /// one coverage, so the rows tile the buckets' span with no gap and no overlap.
    /// </summary>
    internal static void ListsDrawn(IReadOnlyList<TimelineBucket> buckets, IReadOnlyList<IntervalRow> rows)
    {
        int index = 0;
        foreach (IntervalRow row in rows)
        {
            TimelineBucket first = buckets[index];
            Assert.Equal(first.Interval.StartTicks, row.Interval.StartTicks);
            if (first.ObservationCount > 0)
            {
                Assert.Equal((first.Interval, (long)first.ObservationCount, 1), (row.Interval, row.ObservationCount, row.Cells));
                index++;
                continue;
            }

            int end = index + row.Cells;
            Assert.Equal(0, row.ObservationCount);
            Assert.All(buckets.Skip(index).Take(row.Cells),
                bucket => Assert.Equal((0, first.Coverage), (bucket.ObservationCount, bucket.Coverage)));
            Assert.Equal(buckets[end - 1].Interval.EndTicks, row.Interval.EndTicks);
            Assert.True(end == buckets.Count || buckets[end].ObservationCount > 0 || buckets[end].Coverage != first.Coverage,
                $"The run joined at {row.Interval} stops before an empty bucket of its coverage.");
            index = end;
        }

        Assert.Equal(buckets.Count, index);
    }
}
