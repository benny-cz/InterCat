using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The whole retained extent under the timeline (plan §6.2). It shows observed records per column on the §6.2
/// logarithmic intensity scale and the capture's own coverage as a hatched gap row, with the analysis interval above
/// and the timeline's viewport as a brush. Dragging the brush moves the timeline's viewport and dragging an edge
/// resizes it; a press elsewhere centres the viewport there, and a double press fits the extent (§6.7).
/// Wheel zooms about the pointer; Home/End, arrows, +/- and 0 use the timeline's keyboard navigation.
/// </summary>
public sealed class MinimapView : Control
{
    private const ThemeMode Mode = ThemeMode.Dark;

    /// <summary>The minimap as a screen reader meets it: its role and its keyboard path.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "minimap",
        "The whole session: arrows pan the timeline's view, plus and minus zoom, Home and End jump to its edges, and 0 "
        + "fits the whole session.", () => null);

    private static readonly IBrush InkBrush = Token(ThemePalette.Surfaces(Mode).MutedInk);
    private static readonly IBrush SelectedBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly SolidColorBrush GridBrush = Token(ThemePalette.Surfaces(Mode).Elevated);
    private static readonly SolidColorBrush DimBrush = Token(ThemePalette.Surfaces(Mode).Plot);

    private static SolidColorBrush Token(Srgb value) => new SolidColorBrush(ThemeResources.ToColor(value));

    /// <summary>The plot margins match the timeline's, so the two axes start and end at the same x.</summary>
    private const double PlotLeft = 38;
    private const double PlotRightMargin = 14;

    /// <summary>Plan §6.2 and §23: the brush stays grabbable however deep the zoom.</summary>
    internal const double MinimumBrushWidth = 8;

    /// <summary>How close to a brush edge a press resizes rather than moves.</summary>
    private const double EdgeGrab = 4;

    /// <summary>Plan §6.2: any occupied column keeps this share of the row, so a lone burst never draws as a hairline.</summary>
    private const double OccupiedFloor = 0.42;

    private const double GapRowHeight = 5;
    private const long MinimumSpanTicks = 100_000;

    private TimelineView? timeline;
    private Gesture gesture;
    private long grabOffset;
    private Cursor? resizeCursor;
    private Cursor? moveCursor;

    private enum Gesture
    {
        None,
        Move,
        ResizeStart,
        ResizeEnd,
    }

    /// <summary>The timeline whose viewport this minimap shows and moves.</summary>
    public TimelineView? Timeline
    {
        get => timeline;
        set
        {
            if (timeline is not null) timeline.ViewportChanged -= OnViewportChanged;
            timeline = value;
            if (timeline is not null) timeline.ViewportChanged += OnViewportChanged;
            InvalidateVisual();
        }
    }

    private void OnViewportChanged(object? sender, EventArgs e) => InvalidateVisual();

    private TimeRange Extent => (DataContext as WorkspaceViewModel)?.Snapshot.Extent ?? new TimeRange(0, 1);

    private double PlotWidth => Math.Max(1, Bounds.Width - PlotLeft - PlotRightMargin);

    private double X(long tick) => PlotLeft + ViewportMath.PixelAtTick(Extent, tick, PlotWidth);

    private long TickAt(double x) => ViewportMath.TickAtPixel(Extent, Math.Clamp(x - PlotLeft, 0, PlotWidth), PlotWidth);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        double left = PlotLeft;
        double right = left + PlotWidth;
        double top = 3;
        double gapTop = Bounds.Height - GapRowHeight - 2;
        double densityBottom = gapTop - 2;
        context.DrawRectangle(new SolidColorBrush(GridBrush.Color, 0.35), null,
            new Rect(left, top, right - left, gapTop + GapRowHeight - top));

        IReadOnlyList<Cell> cells = Cells(viewModel.Snapshot);
        double peak = cells.Select(cell => (double)cell.Count).DefaultIfEmpty(0).Max();
        double height = Math.Max(1, densityBottom - top);
        foreach (Cell cell in cells.Where(cell => cell.Count > 0))
        {
            // §6.2 intensity: log2(1 + v) / log2(1 + vScale), above the occupied floor.
            double intensity = Math.Log2(1 + cell.Count) / Math.Log2(1 + peak);
            double drawn = height * (OccupiedFloor + ((1 - OccupiedFloor) * intensity));
            context.DrawRectangle(InkBrush, null, new Rect(cell.X1, densityBottom - drawn, Math.Max(1, cell.X2 - cell.X1), drawn));
        }

        // Where the capture itself was not covered, whatever was observed there, the gap row is hatched (§6.6).
        foreach ((double x1, double x2) in NotCoveredRuns(cells))
        {
            TimelineView.DrawCoverageGap(context, new Rect(x1, gapTop, Math.Max(2, x2 - x1), GapRowHeight));
        }

        if (viewModel.SelectedInterval is { } selected)
        {
            double x1 = X(Math.Max(selected.StartTicks, Extent.StartTicks));
            double x2 = Math.Max(x1 + 2, X(Math.Min(selected.EndTicks, Extent.EndTicks)));
            context.DrawRectangle(SelectedBrush, null, new Rect(x1, 0, x2 - x1, 2));
        }

        (double bx1, double bx2) = Brush();
        var dim = new SolidColorBrush(DimBrush.Color, 0.55);
        context.DrawRectangle(dim, null, new Rect(left, top, Math.Max(0, bx1 - left), gapTop + GapRowHeight - top));
        context.DrawRectangle(dim, null, new Rect(bx2, top, Math.Max(0, right - bx2), gapTop + GapRowHeight - top));
        context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 1.5),
            new Rect(bx1, top - 1, bx2 - bx1, gapTop + GapRowHeight - top + 2));
    }

    /// <summary>The brush's drawn edges: the viewport, widened around its centre to the minimum width when narrower.</summary>
    internal (double X1, double X2) Brush()
    {
        TimeRange viewport = timeline?.Viewport ?? Extent;
        double x1 = X(Math.Max(viewport.StartTicks, Extent.StartTicks));
        double x2 = X(Math.Min(viewport.EndTicks, Extent.EndTicks));
        if (x2 - x1 >= MinimumBrushWidth)
        {
            return (x1, x2);
        }

        double centre = (x1 + x2) / 2;
        double start = Math.Clamp(centre - (MinimumBrushWidth / 2), PlotLeft, PlotLeft + PlotWidth - MinimumBrushWidth);
        return (start, start + MinimumBrushWidth);
    }

    private readonly record struct Cell(double X1, double X2, long Count, CoverageState Capture);

    /// <summary>
    /// The minimap's columns as drawn: one cell per column while columns are at least a pixel wide, else one per pixel
    /// holding the largest column in it and the worst coverage, so a varying number of columns per pixel adds no
    /// false texture. A workspace without a minimap level (the synthetic tour) draws its timeline buckets instead.
    /// </summary>
    private List<Cell> Cells(WorkspaceSnapshot snapshot)
    {
        (TimeRange Interval, long Count, CoverageState Capture)[] columns = snapshot.Minimap is { } minimap
            ? [.. Enumerable.Range(0, minimap.Counts.Count)
                .Select(index => (minimap.IntervalOf(index), (long)minimap.Counts[index], minimap.Capture[index]))]
            : [.. snapshot.Timeline.Select(bucket => (bucket.Interval, (long)bucket.ObservationCount, bucket.Coverage))];
        var cells = new List<Cell>(columns.Length);
        if (columns.Length <= PlotWidth)
        {
            cells.AddRange(columns.Select(column =>
                new Cell(X(column.Interval.StartTicks), X(column.Interval.EndTicks), column.Count, column.Capture)));
            return cells;
        }

        int pixels = (int)Math.Ceiling(PlotWidth);
        var counts = new long[pixels];
        var states = new CoverageState[pixels];
        var used = new bool[pixels];
        foreach ((TimeRange interval, long count, CoverageState capture) in columns)
        {
            int pixel = Math.Clamp((int)(X(interval.StartTicks) - PlotLeft), 0, pixels - 1);
            counts[pixel] = Math.Max(counts[pixel], count);
            states[pixel] = used[pixel] ? (CoverageState)Math.Max((int)states[pixel], (int)capture) : capture;
            used[pixel] = true;
        }

        for (int pixel = 0; pixel < pixels; pixel++)
        {
            if (used[pixel])
            {
                cells.Add(new Cell(PlotLeft + pixel, PlotLeft + pixel + 1, counts[pixel], states[pixel]));
            }
        }

        return cells;
    }

    private static IEnumerable<(double X1, double X2)> NotCoveredRuns(IReadOnlyList<Cell> cells)
    {
        double? start = null;
        double end = 0;
        foreach (Cell cell in cells)
        {
            if (cell.Capture != CoverageState.Covered)
            {
                start ??= cell.X1;
                end = cell.X2;
            }
            else if (start is { } open)
            {
                yield return (open, end);
                start = null;
            }
        }

        if (start is { } last)
        {
            yield return (last, end);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (timeline is null || DataContext is not WorkspaceViewModel)
        {
            return;
        }

        Focus();
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            gesture = Gesture.None;
            timeline.SetViewport(null);
            return;
        }

        double x = e.GetPosition(this).X;
        (double bx1, double bx2) = Brush();
        TimeRange viewport = timeline.Viewport;
        bool resizable = bx2 - bx1 > (2 * EdgeGrab) + 4;
        gesture = resizable && Math.Abs(x - bx1) <= EdgeGrab ? Gesture.ResizeStart
            : resizable && Math.Abs(x - bx2) <= EdgeGrab ? Gesture.ResizeEnd
            : Gesture.Move;
        if (gesture == Gesture.Move)
        {
            if (x < bx1 || x > bx2)
            {
                // A press beside the brush centres the viewport there, then the same drag keeps moving it.
                timeline.SetViewport(ViewportMath.CenterOnTick(viewport, TickAt(x), Extent));
                viewport = timeline.Viewport;
            }

            grabOffset = TickAt(x) - viewport.StartTicks;
        }

        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        if (timeline is null || gesture == Gesture.None)
        {
            (double bx1, double bx2) = Brush();
            bool resizable = bx2 - bx1 > (2 * EdgeGrab) + 4;
            Cursor = resizable && (Math.Abs(x - bx1) <= EdgeGrab || Math.Abs(x - bx2) <= EdgeGrab)
                ? resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast)
                : moveCursor ??= new Cursor(StandardCursorType.Hand);
            return;
        }

        TimeRange viewport = timeline.Viewport;
        long tick = TickAt(x);
        long minimum = Math.Min(MinimumSpanTicks, Extent.SpanTicks);
        TimeRange next = gesture switch
        {
            Gesture.ResizeStart => new TimeRange(Math.Min(tick, viewport.EndTicks - minimum), viewport.EndTicks),
            Gesture.ResizeEnd => new TimeRange(viewport.StartTicks, Math.Max(tick, viewport.StartTicks + minimum)),
            _ => new TimeRange(tick - grabOffset, tick - grabOffset + viewport.SpanTicks),
        };
        timeline.SetViewport(next);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gesture != Gesture.None)
        {
            gesture = Gesture.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        gesture = Gesture.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (timeline is null || DataContext is not WorkspaceViewModel || e.Delta.Y == 0) return;

        long focusTick = TickAt(e.GetPosition(this).X);
        TimeRange current = timeline.Viewport;
        if (!current.Contains(focusTick))
            current = ViewportMath.CenterOnTick(current, focusTick, Extent);
        double focusPixel = ViewportMath.PixelAtTick(current, focusTick, PlotWidth);
        decimal factor = e.Delta.Y > 0 ? 1.25m : 0.8m;
        timeline.SetViewport(ViewportMath.ZoomAtPixel(current, focusPixel, PlotWidth, factor, Extent,
            MinimumSpanTicks));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (timeline?.Navigate(e.Key, e.KeyModifiers) == true) e.Handled = true;
    }
}
