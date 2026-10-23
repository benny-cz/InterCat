using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A comparison's difference timeline (`contracts/comparison-v1.md` §8d): each interval cut into as many equal columns, the
/// k-th of one compared with the k-th of the other as two intervals are, all read with the comparison at one generation.
/// </summary>
public sealed partial class SessionComparisonTests
{
    [Fact(DisplayName = "I16: a difference timeline is read with its comparison at one generation, each row's columns adding up to it")]
    public void ADifferenceTimelineComparesEachRowColumnByColumn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            // Steady: an exchange every 10 ticks throughout.
            .. Every(0, 300, 10, pair: 0),
            // A burst: one exchange in the baseline's third quarter, six at the compared interval's start.
            .. Exchange(50, pair: 1), .. Every(100, 160, 10, pair: 1),
            // Gone: three exchanges early in the baseline alone.
            .. Every(20, 50, 10, pair: 2),
            // New: two exchanges late in the compared interval alone.
            .. Exchange(250, pair: 3), .. Exchange(260, pair: 3),
        ]), coverage: Covered());

        foreach (ComparisonMetric metric in Enum.GetValues<ComparisonMetric>())
        {
            ComparisonTimeline timeline = SessionComparison.Timeline(session.Store, new(0, 100), new(100, 300), columns: 4, metric);
            IntervalComparison whole = timeline.Whole, alone = SessionComparison.Compare(session.Store, new(0, 100), new(100, 300), metric);
            Assert.Equal(alone.Relationships.Select(row => (row.Key, row.Change, row.Baseline.Value, row.Compared.Value)),
                whole.Relationships.Select(row => (row.Key, row.Change, row.Baseline.Value, row.Compared.Value)));
            Assert.Equal((alone.Generation, metric), (whole.Generation, whole.Metric));

            // Each interval is cut into equal columns, end to end, each with its coverage.
            Assert.Equal([new TimeRange(0, 25), new(25, 50), new(50, 75), new(75, 100)], timeline.Columns.Select(column => column.Baseline));
            Assert.Equal([new TimeRange(100, 150), new(150, 200), new(200, 250), new(250, 300)],
                timeline.Columns.Select(column => column.Compared));
            Assert.All(timeline.Columns, column => Assert.Contains(column.BaselineCoverage,
                coverage => coverage.Mechanism == Mechanism.Tcp && coverage.State == CoverageState.Covered));

            // Every row's columns add up to its whole values, records and what it measures alike.
            foreach (ComparedRelationship row in whole.Relationships)
            {
                IReadOnlyList<ComparedRelationship> cells = timeline.Of(row);
                Assert.Equal(4, cells.Count);
                Assert.All(cells, cell => Assert.Equal(row.Key, cell.Key));
                Assert.Equal((row.Baseline.Records, row.Compared.Records),
                    (cells.Sum(cell => cell.Baseline.Records), cells.Sum(cell => cell.Compared.Records)));
                Assert.Equal((row.Baseline.Value, row.Compared.Value),
                    (cells.Sum(cell => cell.Baseline.Value), cells.Sum(cell => cell.Compared.Value)));
            }

            // Each column is compared as two intervals are, a quiet one too: the burst came at the compared interval's start,
            // the gone pair's exchanges early in the baseline, the new pair's late in the compared interval.
            Assert.Equal([ComparisonChange.Appeared, ComparisonChange.Appeared, ComparisonChange.Disappeared, ComparisonChange.Unchanged],
                timeline.Of(Row(whole, 1)).Select(cell => cell.Change));
            Assert.Equal([ComparisonChange.Disappeared, ComparisonChange.Disappeared, ComparisonChange.Unchanged, ComparisonChange.Unchanged],
                timeline.Of(Row(whole, 2)).Select(cell => cell.Change));
            Assert.Equal([ComparisonChange.Unchanged, ComparisonChange.Unchanged, ComparisonChange.Unchanged, ComparisonChange.Appeared],
                timeline.Of(Row(whole, 3)).Select(cell => cell.Change));
            Assert.Equal([6L, 4L, 6L, 4L], timeline.Of(Row(whole, 0)).Select(cell => cell.Baseline.Records));
            Assert.Equal([10L, 10L, 10L, 10L], timeline.Of(Row(whole, 0)).Select(cell => cell.Compared.Records));
        }

        // Listing fewer rows keeps only the most changed rows' columns; the rest have none.
        ComparisonTimeline first = SessionComparison.Timeline(session.Store, new(0, 100), new(100, 300), columns: 2, listed: 1);
        Assert.Equal(2, first.Of(first.Whole.Relationships[0]).Count);
        Assert.Empty(first.Of(first.Whole.Relationships[1]));
        Assert.Equal([first.Whole.Relationships[0].Key], first.Cells.Keys);

        // No column, more than it reads, columns shorter than a tick, or no row listed, is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionComparison.Timeline(session.Store, new(0, 100), new(100, 300), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionComparison.Timeline(session.Store, new(0, 1_000), new(1_000, 2_000), SessionComparison.MostTimelineColumns + 1));
        Assert.Equal(100, SessionComparison.Timeline(session.Store, new(0, 1_000), new(1_000, 2_000), 100, listed: 1).Columns.Count);
        Assert.StartsWith(SessionComparison.ColumnsPastTicks, Assert.Throws<ArgumentException>(() =>
            SessionComparison.Timeline(session.Store, new(0, 3), new(100, 300), 4)).Message, StringComparison.Ordinal);
        Assert.StartsWith(SessionComparison.ColumnsPastTicks, Assert.Throws<ArgumentException>(() =>
            SessionComparison.Timeline(session.Store, new(0, 100), new(100, 103), 4)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionComparison.Timeline(session.Store, new(0, 100), new(100, 300), 4, listed: 0));
    }

    [Fact(DisplayName = "R21: a difference timeline judges each column by its own coverage, comparing nothing where a column was not collected")]
    public void ADifferenceTimelineJudgesEachColumnByItsOwnCoverage()
    {
        // TCP collected only until tick 100.
        using var stopped = new TemporarySession();
        Publish(stopped.Store, Sorted([.. Every(0, 100, 10, pair: 0)]), coverage: Ledger(Epoch(1, 0, 99, 10), Epoch(2, 100, 1_000_000)));

        // The whole intervals compare nothing, the compared one running past what was collected; its first column, which
        // was collected, compares alike, and its second states both values with why.
        ComparisonTimeline timeline = SessionComparison.Timeline(stopped.Store, new(0, 100), new(50, 150), columns: 2);
        ComparedRelationship row = Assert.Single(timeline.Whole.Relationships);
        Assert.Equal(ComparisonChange.NotComparable, row.Change);
        IReadOnlyList<ComparedRelationship> cells = timeline.Of(row);
        Assert.Equal((ComparisonChange.Unchanged, (string?)null, 10L, 10L),
            (cells[0].Change, cells[0].Reason, cells[0].Baseline.Records, cells[0].Compared.Records));
        Assert.Equal((ComparisonChange.NotComparable, "TCP was not collected over the compared interval: no admitted descriptor records it."),
            (cells[1].Change, cells[1].Reason));
        Assert.Equal((10L, 0L, CoverageState.NotCollected), (cells[1].Baseline.Records, cells[1].Compared.Records, cells[1].Compared.Coverage.State));
        Assert.Contains(timeline.Columns[1].ComparedCoverage, coverage => coverage.Mechanism == Mechanism.Tcp && coverage.State == CoverageState.NotCollected);
        Assert.Contains(timeline.Columns[0].ComparedCoverage, coverage => coverage.Mechanism == Mechanism.Tcp && coverage.State == CoverageState.Covered);

        // Columns of epochs that collected TCP through other descriptors compare nothing either, though both are covered.
        using var changed = new TemporarySession();
        Publish(changed.Store, Sorted([.. Every(0, 300, 10, pair: 0)]),
            coverage: Ledger(Epoch(1, 0, 99, 10, 12), Epoch(2, 100, 199, 10), Epoch(3, 200, 1_000_000, 12)));
        ComparisonTimeline across = SessionComparison.Timeline(changed.Store, new(100, 200), new(150, 250), columns: 2);
        IReadOnlyList<ComparedRelationship> otherwise = across.Of(Assert.Single(across.Whole.Relationships));
        Assert.Equal((ComparisonChange.Unchanged, (string?)null), (otherwise[0].Change, otherwise[0].Reason));
        Assert.Equal(ComparisonChange.NotComparable, otherwise[1].Change);
        Assert.Equal("The capture collected TCP through other descriptors in its epoch 3 than in its epoch 2, so its counts there "
            + "were not made alike.", otherwise[1].Reason);
    }

    [Fact(DisplayName = "I12: two captures' difference timeline cuts each capture's interval on its own clock, its rows paired by path")]
    public void TwoCapturesDifferenceTimelinePairsTheirColumnsByExecutable()
    {
        // client.exe exchanged with server.exe each 10 ticks in the first capture; in the second, of another host and other
        // PIDs, each 5 ticks in its interval's first half and each 20 in its second.
        using var first = new TemporarySession();
        using var second = new TemporarySession(OtherCapture);
        Publish(first.Store, Sorted(
        [
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 200, ServerPath, ObservationKind.Inventory, slot: 15),
            .. Every(10, 210, 10, pair: 0),
        ]), coverage: Covered());
        Publish(second.Store, Sorted(
        [
            Started(1, 300, ClientPath, ObservationKind.Inventory), Started(1, 400, ServerPath, ObservationKind.Inventory, slot: 15),
            .. Every(100, 200, 5, pair: 1), .. Every(200, 300, 20, pair: 1),
        ]), coverage: Covered(), clock: ClockFor(Clock, "another-host"));

        ComparisonTimeline timeline = SessionComparison.CaptureTimeline(first.Store, new(10, 210), second.Store, new(100, 300), columns: 2);
        Assert.Equal((ComparisonGrouping.Executable, (Guid?)TestSessions.Session, OtherCapture),
            (timeline.Whole.Grouping, timeline.Whole.BaselineSessionId, timeline.Whole.SessionId));
        Assert.Equal([(new TimeRange(10, 110), new TimeRange(100, 200)), (new TimeRange(110, 210), new TimeRange(200, 300))],
            timeline.Columns.Select(column => (column.Baseline, column.Compared)));
        ComparedRelationship pair = Assert.Single(timeline.Whole.Relationships);
        Assert.Equal("executables:tcp:executable:" + ClientPath.ToUpperInvariant() + "|executable:" + ServerPath.ToUpperInvariant(), pair.Key);
        Assert.Equal([(20L, 40L, ComparisonChange.Increased), (20L, 10L, ComparisonChange.Decreased)],
            timeline.Of(pair).Select(cell => (cell.Baseline.Records, cell.Compared.Records, cell.Change)));

        // Of one session twice, there are no two captures to compare.
        Assert.StartsWith(SessionComparison.OneSession, Assert.Throws<ArgumentException>(() =>
            SessionComparison.CaptureTimeline(first.Store, new(0, 100), first.Store, new(100, 200), 2)).Message, StringComparison.Ordinal);

        // A capture that changed its descriptors halfway through its interval compares with the other in the columns it
        // collected through the same ones, and in no other.
        using var changed = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000760"));
        Publish(changed.Store, Sorted(
        [
            Started(1, 300, ClientPath, ObservationKind.Inventory), Started(1, 400, ServerPath, ObservationKind.Inventory, slot: 15),
            .. Every(100, 300, 10, pair: 1),
        ]), coverage: Ledger(Epoch(1, 0, 199, 10), Epoch(2, 200, 1_000_000, 12)), clock: ClockFor(Clock, "another-host"));
        ComparisonTimeline halfway = SessionComparison.CaptureTimeline(first.Store, new(10, 210), changed.Store, new(100, 300), columns: 2);
        IReadOnlyList<ComparedRelationship> cells = halfway.Of(Assert.Single(halfway.Whole.Relationships));
        Assert.Equal((ComparisonChange.Unchanged, (string?)null), (cells[0].Change, cells[0].Reason));
        Assert.Equal(ComparisonChange.NotComparable, cells[1].Change);
        Assert.Equal(ComparisonChange.NotComparable, halfway.Whole.Relationships[0].Change);
    }
}
