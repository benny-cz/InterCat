using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>Where a row of timeline cells is laid out: the time in view and the plot it is drawn across.</summary>
/// <param name="Visible">The time range in view.</param>
/// <param name="Left">The plot's left edge, in logical pixels.</param>
/// <param name="Width">The plot's width, in logical pixels.</param>
/// <param name="Scaling">Device pixels per logical pixel.</param>
internal readonly record struct CellView(TimeRange Visible, double Left, double Width, double Scaling)
{
    public double Right => Left + Width;

    /// <summary>
    /// Where an instant lies across the plot, in logical pixels, clamped to the view: in floating point, as a layout places
    /// thousands of columns a frame while a view moves, and a pixel never needs more than a double holds.
    /// </summary>
    public double X(long tick) =>
        Left + (Width * (Math.Clamp(tick, Visible.StartTicks, Visible.EndTicks) - Visible.StartTicks)
            / Math.Max(1, Visible.SpanTicks));
}

/// <summary>
/// §6.2's mark geometry for one row of timeline cells over one view: where each cell in view is drawn, and which cell a
/// point resolves to. A row whose cells are narrower than <see cref="DensityCellWidth"/> device pixels is drawn as density:
/// its cells edge to edge, a column per device pixel, and every run of occupied cells narrower than
/// <see cref="MinimumDrawnWidth"/> widened around its centre, clamped inside the plot and stopping halfway to the next
/// run, its members sharing the width evenly. A point on a drawn mark resolves to its cell; a point on no mark but within
/// <see cref="SnapAllowance"/> of one snaps to it, the earlier on a tie; any other point resolves to the cell under it,
/// empty or not, since an empty interval is a supported answer (§1.2). Widening is cosmetic: a cell is always its own
/// half-open interval and its own records (R13). A wider row's cells are bars, each its whole column's target and none
/// widened or snapped to, since within a snap's reach of a bar lies the whole of the next column.
/// </summary>
internal sealed class TimelineCells
{
    /// <summary>§6.2: a row whose cells are narrower than this many device pixels is drawn in the density regime.</summary>
    public const double DensityCellWidth = 3;

    /// <summary>§6.2: the narrowest a run of occupied density cells is drawn, in logical pixels.</summary>
    public const double MinimumDrawnWidth = 5;

    /// <summary>
    /// §6.2's allowance for aim: a point on no drawn mark but within this many logical pixels of one snaps to it. From an
    /// isolated mark's centre that is the table's snap radius, half the minimum drawn width and 5 px more.
    /// </summary>
    public const double SnapAllowance = 5;

    /// <summary>§6.2's snap search cap: a point never snaps to a cell more than this many columns from the one under it.</summary>
    public const int SnapSearchColumns = 128;

    // The cells in view are [first, first + count). Per cell in view, indexed from first: the column it stands for, and
    // where it is drawn. Kept from layout to layout and grown only when a row has more cells in view (R11).
    private double[] columnX1 = [];
    private double[] columnX2 = [];
    private double[] drawnX1 = [];
    private double[] drawnX2 = [];

    // The occupied cells in view, in order, as indices from first: the marks a point can land on or snap to.
    private int[] marks = [];
    private int markCount;

    // What the layout was made for: the row's cells, a byte row's metric, and the view.
    private object? source;
    private RankingMetric? metric;
    private CellView view;

    /// <summary>Whether the row's cells are drawn as density, narrower than <see cref="DensityCellWidth"/> device pixels.</summary>
    public bool Density { get; private set; }

    /// <summary>A cell's average span in ticks: what a density cell's count is read over against the row's peak rate.</summary>
    public double CellSpan { get; private set; }

    /// <summary>The first cell in view.</summary>
    public int First { get; private set; }

    /// <summary>One past the last cell in view; equal to <see cref="First"/> when none is.</summary>
    public int End { get; private set; }

    /// <summary>The view the row was laid out over.</summary>
    public CellView View => view;

    /// <summary>Whether a cell is in view.</summary>
    public bool Holds(int cell) => cell >= First && cell < End;

    /// <summary>Where a cell's own column begins, clipped to the view.</summary>
    public double ColumnX1(int cell) => columnX1[cell - First];

    /// <summary>Where a cell's own column ends, clipped to the view.</summary>
    public double ColumnX2(int cell) => columnX2[cell - First];

    /// <summary>Where a cell is drawn from: for an occupied density cell its share of its run, widened or not.</summary>
    public double X1(int cell) => drawnX1[cell - First];

    /// <summary>Where a cell is drawn to.</summary>
    public double X2(int cell) => drawnX2[cell - First];

    /// <summary>Whether the layout is for exactly these cells over this view.</summary>
    internal bool Answers(object cells, RankingMetric? byteMetric, in CellView over) =>
        ReferenceEquals(source, cells) && metric == byteMetric && view == over;

    /// <summary>Lays out a row of timeline buckets, occupied where they hold a record.</summary>
    internal void Lay(IReadOnlyList<TimelineBucket> buckets, in CellView over) => Lay(new Buckets(buckets), buckets, null, over);

    /// <summary>Lays out a row of byte columns, occupied where they plot a sum above zero or one nothing measured.</summary>
    internal void Lay(SessionIntervalByteMeasures lane, RankingMetric byteMetric, in CellView over) =>
        Lay(new ByteColumns(lane, byteMetric), lane, byteMetric, over);

    /// <summary>
    /// The cell a point at <paramref name="x"/> resolves to: the mark drawn under it; else, in density, the nearest mark
    /// within <see cref="SnapAllowance"/>, the earlier on a tie; else the cell whose column holds it. Null off the plot or
    /// where no cell is in view.
    /// </summary>
    public int? CellAt(double x)
    {
        int count = End - First;
        if (count == 0 || double.IsNaN(x) || x < view.Left || x > view.Right || x < columnX1[0] || x > columnX2[count - 1])
        {
            return null;
        }

        int under = ColumnUnder(x);
        if (!Density)
        {
            return First + under;
        }

        // The last mark drawn from at or before the point: the one it lies on, if any does.
        int low = 0;
        int high = markCount - 1;
        int before = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (drawnX1[marks[middle]] <= x)
            {
                before = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (before >= 0 && (x < drawnX2[marks[before]] || (x >= view.Right && drawnX2[marks[before]] >= view.Right)))
        {
            return First + marks[before];
        }

        // On no mark: the nearest within reach, the earlier on a tie, never beyond the search cap.
        int? snapped = null;
        double nearest = double.PositiveInfinity;
        if (before >= 0 && Reaches(marks[before], x - drawnX2[marks[before]]))
        {
            snapped = marks[before];
            nearest = x - drawnX2[marks[before]];
        }

        if (before + 1 < markCount && Reaches(marks[before + 1], drawnX1[marks[before + 1]] - x)
            && drawnX1[marks[before + 1]] - x < nearest)
        {
            snapped = marks[before + 1];
        }

        return First + (snapped ?? under);

        bool Reaches(int mark, double distance) => distance <= SnapAllowance && Math.Abs(mark - under) <= SnapSearchColumns;
    }

    /// <summary>The cell, as an index from <see cref="First"/>, whose own column holds <paramref name="x"/>.</summary>
    private int ColumnUnder(double x)
    {
        int low = 0;
        int high = End - First - 1;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (columnX1[middle] <= x)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private void Lay<TCells>(TCells cells, object row, RankingMetric? byteMetric, in CellView over)
        where TCells : struct, ICells
    {
        source = row;
        metric = byteMetric;
        view = over;
        markCount = 0;
        int total = cells.Count;
        TimeRange visible = over.Visible;

        // Cells are in time order and partition their span, so those in view are found by halving.
        int first = 0;
        int high = total;
        while (first < high)
        {
            int middle = first + ((high - first) / 2);
            if (cells.End(middle) <= visible.StartTicks) first = middle + 1;
            else high = middle;
        }

        int end = first;
        while (end < total && cells.Start(end) < visible.EndTicks)
        {
            end++;
        }

        First = first;
        End = end;
        CellSpan = total == 0 ? 0 : (double)(cells.End(total - 1) - cells.Start(0)) / total;
        Density = total > 0 && visible.SpanTicks > 0
            && CellSpan * over.Width / visible.SpanTicks * over.Scaling < DensityCellWidth;
        int count = end - first;
        if (columnX1.Length < count)
        {
            int size = Math.Max(count, columnX1.Length * 2);
            columnX1 = new double[size];
            columnX2 = new double[size];
            drawnX1 = new double[size];
            drawnX2 = new double[size];
            marks = new int[size];
        }

        for (int index = 0; index < count; index++)
        {
            int cell = first + index;
            columnX1[index] = over.X(Math.Max(cells.Start(cell), visible.StartTicks));
            columnX2[index] = over.X(Math.Min(cells.End(cell), visible.EndTicks));
            drawnX1[index] = columnX1[index];
            drawnX2[index] = columnX2[index];
            if (cells.Occupied(cell))
            {
                marks[markCount++] = index;
            }
        }

        if (!Density)
        {
            // A bar keeps a 2 px gap to the next, at least 1 px wide, and is its whole column's target.
            for (int index = 0; index < count; index++)
            {
                drawnX2[index] = drawnX1[index] + Math.Max(1, columnX2[index] - columnX1[index] - 2);
            }

            return;
        }

        Widen();
    }

    /// <summary>
    /// Widens each run of occupied cells narrower than the minimum around its centre, clamped inside the plot, then gives
    /// back what two neighbouring runs would both draw on, splitting it halfway across the gap between their own columns,
    /// so no mark is drawn over another and each still covers its own column. A widened run's members share it evenly.
    /// </summary>
    private void Widen()
    {
        double left = view.Left;
        double right = view.Right;
        double minimum = Math.Min(MinimumDrawnWidth, view.Width);
        int previousFirst = -1;
        int previousLast = -1;
        double previousStart = 0;
        double previousEnd = 0;
        for (int at = 0; at < markCount;)
        {
            // A run: marks whose cells follow one another without an empty cell between.
            int runFirst = marks[at];
            int runLast = runFirst;
            int next = at + 1;
            while (next < markCount && marks[next] == runLast + 1)
            {
                runLast = marks[next++];
            }

            double start = columnX1[runFirst];
            double end = columnX2[runLast];
            if (end - start < minimum)
            {
                double centre = (start + end) / 2;
                start = centre - (minimum / 2);
                end = centre + (minimum / 2);
                if (start < left)
                {
                    end += left - start;
                    start = left;
                }

                if (end > right)
                {
                    start = Math.Max(left, start - (end - right));
                    end = right;
                }
            }

            if (previousFirst >= 0 && previousEnd > start)
            {
                double half = (columnX2[previousLast] + columnX1[runFirst]) / 2;
                previousEnd = Math.Min(previousEnd, half);
                start = Math.Max(start, half);
            }

            if (previousFirst >= 0)
            {
                Share(previousFirst, previousLast, previousStart, previousEnd);
            }

            previousFirst = runFirst;
            previousLast = runLast;
            previousStart = start;
            previousEnd = end;
            at = next;
        }

        if (previousFirst >= 0)
        {
            Share(previousFirst, previousLast, previousStart, previousEnd);
        }
    }

    /// <summary>Gives a run's members their drawn extents: their own columns where it was not widened, else even shares.</summary>
    private void Share(int runFirst, int runLast, double start, double end)
    {
        if (start == columnX1[runFirst] && end == columnX2[runLast])
        {
            return;
        }

        int members = runLast - runFirst + 1;
        double share = (end - start) / members;
        for (int member = 0; member < members; member++)
        {
            drawnX1[runFirst + member] = start + (share * member);
            drawnX2[runFirst + member] = member == members - 1 ? end : start + (share * (member + 1));
        }
    }

    /// <summary>A row of cells as the layout reads it, without allocating (R11).</summary>
    private interface ICells
    {
        int Count { get; }

        long Start(int cell);

        long End(int cell);

        bool Occupied(int cell);
    }

    private readonly struct Buckets(IReadOnlyList<TimelineBucket> buckets) : ICells
    {
        public int Count => buckets.Count;

        public long Start(int cell) => buckets[cell].Interval.StartTicks;

        public long End(int cell) => buckets[cell].Interval.EndTicks;

        public bool Occupied(int cell) => buckets[cell].ObservationCount > 0;
    }

    private readonly struct ByteColumns(SessionIntervalByteMeasures lane, RankingMetric metric) : ICells
    {
        public int Count => lane.Columns.Count;

        public long Start(int cell) => lane.IntervalOf(cell).StartTicks;

        public long End(int cell) => lane.IntervalOf(cell).EndTicks;

        public bool Occupied(int cell)
        {
            (long? value, _, long unmeasured) = lane.Columns[cell].ValueOf(metric);
            return value > 0 || (value is null && unmeasured > 0);
        }
    }
}

/// <summary>
/// The layouts of the rows a timeline draws, kept from frame to frame by the cells they lay out and rebuilt only when a
/// row's cells or the view change, so drawing, hover and clicks read one geometry and a repaint allocates nothing (R11).
/// </summary>
internal sealed class TimelineCellLayouts
{
    /// <summary>More rows than any view draws at once; past it, layouts of rows no longer drawn are forgotten.</summary>
    private const int MostRows = 1_024;

    private readonly Dictionary<object, TimelineCells> rows = new(ReferenceEqualityComparer.Instance);

    /// <summary>The layout of a row of buckets over a view.</summary>
    public TimelineCells For(IReadOnlyList<TimelineBucket> buckets, in CellView view)
    {
        TimelineCells cells = Entry(buckets);
        if (!cells.Answers(buckets, null, view))
        {
            cells.Lay(buckets, view);
        }

        return cells;
    }

    /// <summary>The layout of a row of byte columns plotting <paramref name="metric"/> over a view.</summary>
    public TimelineCells For(SessionIntervalByteMeasures lane, RankingMetric metric, in CellView view)
    {
        TimelineCells cells = Entry(lane);
        if (!cells.Answers(lane, metric, view))
        {
            cells.Lay(lane, metric, view);
        }

        return cells;
    }

    private TimelineCells Entry(object row)
    {
        if (rows.TryGetValue(row, out TimelineCells? cells))
        {
            return cells;
        }

        if (rows.Count >= MostRows)
        {
            rows.Clear();
        }

        cells = new TimelineCells();
        rows[row] = cells;
        return cells;
    }
}
