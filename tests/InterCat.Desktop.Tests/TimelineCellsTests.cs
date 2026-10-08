using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.2's mark geometry: a row whose cells are narrower than 3 device pixels is drawn as density, a run of occupied cells
/// narrower than 5 px is widened around its centre so it can be pointed at, and a point near a mark but on none snaps to
/// it - within 5 px of the mark, the earlier on a tie, never past 128 columns - while a point far from every mark still
/// answers with the empty cell under it. Widening never moves a cell's own column, which hit tests report (R13).
/// </summary>
public sealed class TimelineCellsTests
{
    private const double Left = 100;

    [Theory(DisplayName = "§6.2: the density regime is chosen from a cell's width in device pixels, not from a zoom or a count")]
    [InlineData(1_000, 1.0, true)]
    [InlineData(400, 1.0, true)]
    [InlineData(333, 1.0, false)]
    [InlineData(64, 1.0, false)]
    [InlineData(400, 1.25, false)]
    [InlineData(800, 2.0, true)]
    public void TheRegimeFollowsDevicePixels(int cells, double scaling, bool density)
    {
        TimelineCells layout = Lay(cells, 1_000, scaling);
        Assert.Equal(density, layout.Density);
    }

    [Fact(DisplayName = "§6.2: an isolated occupied cell is drawn 5 px wide around its own column, which stays its own")]
    public void AnIsolatedCellIsWidenedAroundItsColumn()
    {
        TimelineCells layout = Lay(1_000, 1_000, occupied: [500]);
        Assert.Equal((600, 601), (layout.ColumnX1(500), layout.ColumnX2(500)));
        Assert.Equal((598, 603), (layout.X1(500), layout.X2(500)));

        // The empty cells either side keep their own columns.
        Assert.Equal((599, 600), (layout.X1(499), layout.X2(499)));
        Assert.Equal((601, 602), (layout.X1(501), layout.X2(501)));
    }

    [Fact(DisplayName = "§6.2: a short run is widened around its centre and its members share the width evenly; a run already 5 px wide keeps its true shape")]
    public void AShortRunsMembersShareItsWidth()
    {
        TimelineCells layout = Lay(1_000, 1_000, occupied: [500, 501, 502, 700, 701, 702, 703, 704]);
        double share = 5.0 / 3;
        Assert.Equal(599, layout.X1(500), 9);
        Assert.Equal(599 + share, layout.X2(500), 9);
        Assert.Equal(599 + share, layout.X1(501), 9);
        Assert.Equal(599 + (2 * share), layout.X1(502), 9);
        Assert.Equal(604, layout.X2(502), 9);
        for (int cell = 700; cell <= 704; cell++)
        {
            Assert.Equal((layout.ColumnX1(cell), layout.ColumnX2(cell)), (layout.X1(cell), layout.X2(cell)));
        }
    }

    [Fact(DisplayName = "§6.2: a widened mark is clamped inside the plot at either end")]
    public void AWidenedMarkStaysInsideThePlot()
    {
        TimelineCells layout = Lay(1_000, 1_000, occupied: [0, 999]);
        Assert.Equal((100, 105), (layout.X1(0), layout.X2(0)));
        Assert.Equal((1_095, 1_100), (layout.X1(999), layout.X2(999)));
    }

    [Fact(DisplayName = "§6.2: two widened marks never draw over each other: what both would cover is split halfway across the gap between their own columns")]
    public void NeighbouringMarksSplitWhatTheyShare()
    {
        TimelineCells layout = Lay(1_000, 1_000, occupied: [500, 503]);
        Assert.Equal((598, 602), (layout.X1(500), layout.X2(500)));
        Assert.Equal((602, 606), (layout.X1(503), layout.X2(503)));

        // Beside a run drawn as it is, a widened mark stops halfway to it, and the run keeps its shape.
        layout = Lay(1_000, 1_000, occupied: [500, 501, 502, 503, 504, 505, 507]);
        Assert.Equal((605, 606), (layout.X1(505), layout.X2(505)));
        Assert.Equal((606.5, 610), (layout.X1(507), layout.X2(507)));
    }

    [Fact(DisplayName = "§6.2: a point on a mark names it; near one, within 5 px, it snaps to it; further off it names the empty cell under it")]
    public void APointSnapsOnlyNearAMark()
    {
        TimelineCells layout = Lay(1_000, 1_000, occupied: [500]);
        Assert.Equal(500, layout.CellAt(600.5));
        Assert.Equal(500, layout.CellAt(598));
        Assert.Equal(500, layout.CellAt(602.5));
        Assert.Equal(500, layout.CellAt(607.9));
        Assert.Equal(500, layout.CellAt(593.1));
        Assert.Equal(508, layout.CellAt(608.1));
        Assert.Equal(492, layout.CellAt(592.9));

        // Off the plot, nothing.
        Assert.Null(layout.CellAt(99));
        Assert.Null(layout.CellAt(1_101));
    }

    [Fact(DisplayName = "§6.2: a point as near to two marks snaps to the earlier, deterministically")]
    public void ATieSnapsToTheEarlierMark()
    {
        TimelineCells layout = Lay(1_000, 1_000, occupied: [500, 512]);
        Assert.Equal((598, 603), (layout.X1(500), layout.X2(500)));
        Assert.Equal((610, 615), (layout.X1(512), layout.X2(512)));
        Assert.Equal(500, layout.CellAt(606.5));
        Assert.Equal(512, layout.CellAt(606.6));
    }

    [Fact(DisplayName = "§6.2: snapping never reaches past 128 columns from the one under the point, however narrow the columns")]
    public void SnappingStopsAtTheSearchCap()
    {
        // Columns of a hundredth of a pixel: a mark 2 px away is 200 columns away.
        TimelineCells layout = Lay(100_000, 1_000, occupied: [50_000]);
        Assert.Equal(50_000, layout.CellAt(600.005));
        Assert.Equal(50_000, layout.CellAt(602.4));
        Assert.Equal(50_300, layout.CellAt(603.005));
    }

    [Fact(DisplayName = "§6.2: a bar wider than the density regime is its whole column's target, with no widening and no snapping")]
    public void ABarIsItsWholeColumn()
    {
        TimelineCells layout = Lay(100, 1_000, occupied: [40]);
        Assert.False(layout.Density);
        Assert.Equal((500, 508), (layout.X1(40), layout.X2(40)));
        Assert.Equal(40, layout.CellAt(509.9));
        Assert.Equal(41, layout.CellAt(510.5));
        Assert.Equal(39, layout.CellAt(499.5));
    }

    [Fact(DisplayName = "§6.2: a byte column occupies its cell when it sums more than zero or measured nothing it declared, never when it measured zero")]
    public void ByteColumnsOccupyWhatTheyPlot()
    {
        var interval = new TimeRange(0, 10_000);
        TransportBytes[] columns = [.. Enumerable.Repeat(TransportBytes.None, 1_000)];
        columns[100] = new TransportBytes(64, 1, 0, 0, 0, 0);
        columns[300] = new TransportBytes(0, 0, 1, 0, 0, 0);
        columns[500] = new TransportBytes(0, 1, 0, 0, 0, 0);
        var lane = new SessionIntervalByteMeasures(Guid.NewGuid(), 1, interval, IntervalByteScope.Whole, columns);
        var layouts = new TimelineCellLayouts();
        TimelineCells layout = layouts.For(lane, RankingMetric.BytesSent, new CellView(interval, Left, 1_000, 1));
        Assert.Equal(5, layout.X2(100) - layout.X1(100), 9);
        Assert.Equal(5, layout.X2(300) - layout.X1(300), 9);
        Assert.Equal(1, layout.X2(500) - layout.X1(500), 9);

        // Received, the same columns plot nothing.
        layout = layouts.For(lane, RankingMetric.BytesReceived, new CellView(interval, Left, 1_000, 1));
        Assert.Equal(1, layout.X2(100) - layout.X1(100), 9);
    }

    [Fact(DisplayName = "R11: a row's layout is kept while its cells and the view stay, and laid out again when either changes")]
    public void ALayoutIsKeptUntilItsRowOrViewChanges()
    {
        IReadOnlyList<TimelineBucket> buckets = Buckets(1_000, [500]);
        var layouts = new TimelineCellLayouts();
        var view = new CellView(new TimeRange(0, 10_000), Left, 1_000, 1);
        TimelineCells layout = layouts.For(buckets, view);
        Assert.Same(layout, layouts.For(buckets, view));
        Assert.Equal(598, layout.X1(500));

        // A pan moves every column.
        layout = layouts.For(buckets, view with { Visible = new TimeRange(1_000, 11_000) });
        Assert.Equal(100, layout.First);
        Assert.Equal(498, layout.X1(500));
    }

    /// <summary><paramref name="cells"/> buckets of 10 ticks laid out over <paramref name="width"/> px from x = 100.</summary>
    private static TimelineCells Lay(int cells, double width, double scaling = 1, int[]? occupied = null) =>
        new TimelineCellLayouts().For(Buckets(cells, occupied ?? []),
            new CellView(new TimeRange(0, cells * 10L), Left, width, scaling));

    private static IReadOnlyList<TimelineBucket> Buckets(int cells, int[] occupied) =>
    [
        .. Enumerable.Range(0, cells).Select(cell => new TimelineBucket(new TimeRange(cell * 10L, (cell + 1) * 10L),
            occupied.Contains(cell) ? 1 : 0, null, Mechanism.Tcp, CoverageState.Covered)),
    ];
}
