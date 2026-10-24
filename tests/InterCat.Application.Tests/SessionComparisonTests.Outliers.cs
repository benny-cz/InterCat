using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Outliers (`contracts/outliers-v1.md`, plan M12's optional outlier detection): an interval's rows judged against the
/// windows of its baseline, each as long as the interval, by where the interval's records fall among the windows' - an
/// observation stated with its baseline, its windows and how often chance alone would make it.
/// </summary>
public sealed partial class SessionComparisonTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact(DisplayName = "R21: an interval's rows are judged against its baseline's windows, and a row its two intervals did not collect alike is not judged")]
    public void AnIntervalsRowsAreJudgedAgainstItsBaselinesWindows()
    {
        using var session = new TemporarySession();
        Publish(session.Store, OutlierRows(), coverage: OutlierCoverage());
        TimeRange baseline = new(0, 1_000), compared = new(1_000, 1_050);
        OutlierReading reading = ComparisonOutliers.Read(session.Store, baseline, compared);

        // The baseline is cut into 20 windows as long as the compared interval, end to end: one more interval among them,
        // the compared interval is above every one at most 1 time in 21 by chance alone.
        Assert.Equal(20, reading.Windows.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(window => new TimeRange(window * 50, (window * 50) + 50)), reading.Windows);
        Assert.Equal((21, baseline, compared), (reading.Chance, reading.Comparison.Baseline, reading.Comparison.Compared));

        // The burst and the new relationship are above every window, the stopped one below every one, the most records
        // outside the windows' range first; then those within it, in the comparison's order.
        IntervalComparison comparison = reading.Comparison;
        Assert.Equal(
            [
                (1, OutlierSide.Above, 50L, 10L, 12L, 38L),
                (2, OutlierSide.Below, 0L, 10L, 12L, 10L),
                (3, OutlierSide.Above, 4L, 0L, 0L, 4L),
                (4, OutlierSide.Within, 2L, 2L, 6L, 0L),
                (0, OutlierSide.Within, 10L, 10L, 10L, 0L),
            ],
            reading.Judged.Select(judged => (PairOf(comparison, judged.Row), judged.Side, judged.Row.Compared.Records, judged.Fewest,
                judged.Most, judged.Beyond)));
        Assert.Equal([1, 2, 3], reading.Outliers.Select(judged => PairOf(comparison, judged.Row)));

        // A row's records in each window, in order; its median is one window's own count, the lower middle one.
        OutlierJudgement varied = reading.Judged[3];
        Assert.Equal([6L, .. Enumerable.Repeat(2L, 19)], varied.Windows);
        Assert.Equal((2L, 44L), (varied.Median, varied.Row.Baseline.Records));
        Assert.Equal(2L, (reading.Judged[3] with { Windows = [1L, 4L, 3L, 2L] }).Median);
        Assert.Equal(3L, (reading.Judged[3] with { Windows = [5L, 1L, 3L] }).Median);
        Assert.Equal([10L], reading.Judged[4].Windows.Distinct());

        // The UDP flow's sources changed at the compared interval, so its two values are not of one measurement: it is not
        // judged, and says why, as the comparison does.
        ComparedRelationship datagrams = Assert.Single(reading.NotJudged);
        Assert.Equal((Mechanism.Udp, ComparisonChange.NotComparable), (datagrams.Mechanism, datagrams.Change));
        Assert.DoesNotContain(reading.Judged, judged => judged.Row.Key == datagrams.Key);
        Assert.Equal(comparison.Relationships.Count, reading.Judged.Count + reading.NotJudged.Count);

        // Without a baseline named, everything the session keeps before the compared interval is one; nothing is before an
        // interval at the session's start.
        Assert.Equal(baseline, ComparisonOutliers.Before(session.Store, compared));
        Assert.Null(ComparisonOutliers.Before(session.Store, new(0, 50)));
        Assert.Null(ComparisonOutliers.Before(session.Store, new(-50, 0)));

        // The comparison is read under the evidence policy asked; what cannot be judged is refused.
        Assert.Equal(EvidencePolicy.DirectOnly, ComparisonOutliers.Read(session.Store, baseline, compared, EvidencePolicy.DirectOnly)
            .Comparison.Policy);
        Assert.Throws<ArgumentException>(() => ComparisonOutliers.Read(session.Store, new(0, 1_000), new(500, 550)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComparisonOutliers.Read(session.Store, baseline, compared, (EvidencePolicy)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComparisonOutliers.Read(session.Store, baseline, compared,
            grouping: (ComparisonGrouping)0));
        Assert.Throws<ArgumentNullException>(() => ComparisonOutliers.Read(null!, baseline, compared));
        Assert.Throws<ArgumentNullException>(() => ComparisonOutliers.Before(null!, compared));
    }

    [Fact(DisplayName = "R22: by executable, a row's windows hold its executable's processes' records together, a grouping and never an identity")]
    public void ByExecutableARowsWindowsHoldItsProcessesTogether()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            .. OutlierRows(),
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 200, ServerPath, ObservationKind.Inventory, slot: 15),
            Started(2, 300, OtherClientPath, ObservationKind.Inventory), Started(2, 400, WorkerPath, ObservationKind.Inventory, slot: 15),
        ]), coverage: OutlierCoverage());
        OutlierReading reading = ComparisonOutliers.Read(session.Store, new(0, 1_000), new(1_000, 1_050),
            grouping: ComparisonGrouping.Executable);

        // client.exe and server.exe exchanged steadily; the other client.exe's burst to worker.exe is above every window;
        // what no witnessed executable is at an end of has no row and is counted apart.
        Assert.Equal(ComparisonGrouping.Executable, reading.Comparison.Grouping);
        Assert.Equal(
            [
                ("executable:" + WorkerPath.ToUpperInvariant(), OutlierSide.Above, 38L),
                ("executable:" + ClientPath.ToUpperInvariant(), OutlierSide.Within, 0L),
            ],
            reading.Judged.Select(judged => (judged.Row.Executables!.Source, judged.Side, judged.Beyond)));
        Assert.Equal(4, reading.Comparison.Unmatched.Rows);
    }

    [Fact(DisplayName = "R10: a baseline's windows are as long as the compared interval, whole ticks spread evenly from its start to its end, none overlapping another")]
    public void ABaselinesWindowsAreSpreadEvenlyAcrossIt()
    {
        // Twenty windows of 50 ticks fit in 1,030: the first starts where the baseline does, the last ends where it does,
        // and the 30 ticks left over are spread between them as evenly as whole ticks allow.
        IReadOnlyList<TimeRange> spread = ComparisonOutliers.Windows(new(0, 1_030), new(2_000, 2_050), out string? refusal, Invariant)!;
        Assert.Null(refusal);
        Assert.Equal(20, spread.Count);
        Assert.Equal((new TimeRange(0, 50), new TimeRange(51, 101), new TimeRange(103, 153), new TimeRange(980, 1_030)),
            (spread[0], spread[1], spread[2], spread[^1]));
        Assert.All(spread, window => Assert.Equal(50, window.SpanTicks));
        Assert.All(spread.Zip(spread.Skip(1)), pair => Assert.True(pair.Second.StartTicks >= pair.First.EndTicks));

        // However many fit, at most 199 are cut, still spread from the baseline's start to its end; a baseline after the
        // compared interval is cut alike.
        IReadOnlyList<TimeRange> most = ComparisonOutliers.Windows(new(1_000, 101_000), new(0, 10), out _, Invariant)!;
        Assert.Equal((199, new TimeRange(1_000, 1_010), new TimeRange(1_505, 1_515), new TimeRange(100_990, 101_000)),
            (most.Count, most[0], most[1], most[^1]));
        Assert.All(most.Zip(most.Skip(1)), pair => Assert.True(pair.Second.StartTicks >= pair.First.EndTicks));
        Assert.Equal(19, ComparisonOutliers.Windows(new(0, 950), new(1_000, 1_050), out _, Invariant)!.Count);
        Assert.Equal(new TimeRange(long.MaxValue - 50, long.MaxValue), ComparisonOutliers.Windows(
            new(long.MaxValue - 1_000, long.MaxValue), new(0, 50), out _, Invariant)![^1]);

        // Too few windows, none at all, or a baseline overlapping the compared interval: refused in words.
        Assert.Null(ComparisonOutliers.Windows(new(0, 949), new(1_000, 1_050), out refusal, Invariant));
        Assert.Equal("The baseline holds only 18 windows as long as the compared interval, and at least 19 are needed, so that "
            + "chance alone puts a row above every one of them no more than 1 time in 20: give a baseline at least 0.095 ms long, or "
            + "compare a shorter interval.", refusal);
        Assert.Null(ComparisonOutliers.Windows(new(0, 10), new(100, 200), out refusal, Invariant));
        Assert.StartsWith("The baseline holds no window as long as the compared interval, and at least 19 are needed", refusal,
            StringComparison.Ordinal);
        Assert.Null(ComparisonOutliers.Windows(new(0, 10), new(20, 20 + (long.MaxValue / 2)), out refusal, Invariant));
        Assert.EndsWith(": compare a shorter interval.", refusal, StringComparison.Ordinal);
        Assert.Null(ComparisonOutliers.Windows(new(0, 1_000), new(999, 1_049), out refusal, Invariant));
        Assert.Equal(ComparisonOutliers.Overlapping, refusal);
        Assert.Null(ComparisonOutliers.Windows(new(1_000, 2_000), new(950, 1_001), out refusal, Invariant));
        Assert.Equal(ComparisonOutliers.Overlapping, refusal);
        Assert.NotNull(ComparisonOutliers.Windows(new(1_050, 2_050), new(1_000, 1_050), out _, Invariant));
    }

    [Fact(DisplayName = "R18: an outlier is said in one set of words, the command line's and the window's, each describing what was counted and never why")]
    public void AnOutlierIsSaidInOneSetOfWords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, OutlierRows(), coverage: OutlierCoverage());
        OutlierReading reading = ComparisonOutliers.Read(session.Store, new(0, 1_000), new(1_000, 1_050));

        Assert.Equal("[0.000, 0.100) ms, 0.100 ms long, as 20 windows of 5.0 µs, end to end", OutlierText.Baseline(reading, Invariant));
        Assert.EndsWith("as 20 windows of 5.0 µs, spread evenly across it", OutlierText.Baseline(reading with
        {
            Comparison = reading.Comparison with { Baseline = new(0, 1_030) },
        }, Invariant), StringComparison.Ordinal);
        Assert.Equal("5 rows judged · 2 above every window of their baseline and 1 below every one", OutlierText.Found(reading, Invariant));
        Assert.Equal(
            [
                ("10 to 12, median 10", "38 more than the most", "above every window of its baseline"),
                ("10 to 12, median 10", "10 fewer than the fewest", "below every window of its baseline"),
                ("0 in each", "4 more than the most", "above every window of its baseline"),
                ("2 to 6, median 2", "-", "within its baseline's windows"),
            ],
            reading.Judged.Take(4).Select(judged => (OutlierText.Windows(judged, Invariant), OutlierText.Beyond(judged, Invariant),
                OutlierText.Side(judged.Side))));
        Assert.Equal("Were the compared interval like the baseline's windows, chance alone would put a row above every one of "
            + "them at most 1 time in 21, and below every one at most as often: on average no more than 0.24 of the 5 rows judged "
            + "each way.", OutlierText.Chance(reading, Invariant));
        string notJudged = Assert.Single(OutlierText.NotJudged(reading, Invariant));
        Assert.Equal("1 row not judged: " + reading.NotJudged[0].Reason, notJudged);
        Assert.StartsWith("1 row not judged: The capture collected UDP ", notJudged, StringComparison.Ordinal);

        // Each reason not judged for is said once with how many rows it covers, the most first, then in ordinal order.
        ComparedRelationship[] rows = [.. reading.Comparison.Relationships];
        Assert.Equal(["2 rows not judged: Second.", "1 row not judged: First.", "1 row not judged: its two values are not of the same measurement"],
            OutlierText.NotJudged(reading with
            {
                Comparison = reading.Comparison with
                {
                    Relationships =
                    [
                        rows[0] with { Change = ComparisonChange.NotComparable, Reason = "Second." },
                        rows[1] with { Change = ComparisonChange.NotComparable, Reason = null },
                        rows[2] with { Change = ComparisonChange.NotComparable, Reason = "First." },
                        rows[3] with { Change = ComparisonChange.NotComparable, Reason = "Second." },
                        rows[4],
                    ],
                },
            }, Invariant));

        // Where one side alone, neither, or no row at all is found, it says so.
        OutlierJudgement[] judged = [.. reading.Judged];
        Assert.Equal("5 rows judged · 1 above every window of its baseline", OutlierText.Found(reading with
        {
            Judged = [.. judged.Select(row => row.Side == OutlierSide.Above && row.Beyond == 4 || row.Side == OutlierSide.Below
                ? row with { Side = OutlierSide.Within } : row)],
        }, Invariant));
        Assert.Equal("5 rows judged · 3 below every window of their baseline", OutlierText.Found(reading with
        {
            Judged = [.. judged.Select(row => row with { Side = row.Side == OutlierSide.Within ? row.Side : OutlierSide.Below })],
        }, Invariant));
        Assert.Equal("1 row judged · none above or below every window", OutlierText.Found(reading with { Judged = [judged[4]] }, Invariant));
        Assert.Equal("no row judged", OutlierText.Found(reading with { Judged = [] }, Invariant));
        Assert.Null(OutlierText.Chance(reading with { Judged = [] }, Invariant));
        Assert.Contains("no more than 0.05 of the 1 row judged each way", OutlierText.Chance(reading with { Judged = [judged[4]] }, Invariant),
            StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => OutlierText.Side((OutlierSide)0));

        // Plan M12's exit test: an outlier's label describes an observation, never malware, an attack or a root cause.
        string[] said =
        [
            OutlierText.Note, .. Enum.GetValues<OutlierSide>().Select(OutlierText.Side), OutlierText.Found(reading, Invariant),
            OutlierText.Chance(reading, Invariant)!, .. reading.Judged.Select(row => OutlierText.Beyond(row, Invariant)),
        ];
        foreach (string word in new[] { "malware", "malicious", "attack", "suspicious", "anomal", "threat", "intrusion", "compromise",
            "cause", "root", "infect", "harm" })
        {
            Assert.All(said, text => Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains("that is all it says: nothing of what its records mean or why they differ", OutlierText.Note, StringComparison.Ordinal);
    }
}
