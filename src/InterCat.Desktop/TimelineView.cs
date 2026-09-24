using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

public sealed class TimelineView : Control
{
    private const ThemeMode Mode = ThemeMode.Dark;

    private static readonly IBrush GapBrush = Token(ThemePalette.TokensFor(Mode, MechanismFamily.RemoteCall).Ink);
    private static readonly IBrush SelectedBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly IBrush GridBrush = Token(ThemePalette.Surfaces(Mode).Elevated);
    private static readonly IBrush TextBrush = Token(ThemePalette.Surfaces(Mode).MutedInk);
    private static readonly SolidColorBrush DimBrush = Token(ThemePalette.Surfaces(Mode).Plot);

    private static SolidColorBrush Token(Srgb value) => new SolidColorBrush(ThemeResources.ToColor(value));

    /// <summary>A press that moves at least this far brushes a range; a shorter one selects the bucket under it.</summary>
    private const double BrushThreshold = 4;

    /// <summary>The plot's margins: the axis label gutter on the left and a little air on the right.</summary>
    private const double PlotLeft = 38;
    private const double PlotRightMargin = 14;

    /// <summary>The narrowest viewport a zoom may reach: 10 ms of presentation ticks, or the extent when shorter.</summary>
    private const long MinimumSpanTicks = 100_000;

    /// <summary>How long the viewport must rest before the timeline asks for its own resolution (§6.2, P25).</summary>
    private static readonly TimeSpan DetailSettle = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer detailTimer;
    private TimeRange? viewport;
    private long? brushAnchor;
    private long? brushEnd;
    private double pressX;
    private bool brushing;
    private bool moved;
    private TimeRange panOrigin;

    public TimelineView()
    {
        detailTimer = new DispatcherTimer { Interval = DetailSettle };
        detailTimer.Tick += (_, _) => RequestDetailNow();
    }

    /// <summary>Raised whenever the visible range changes, by a gesture, a key, or <see cref="SetViewport"/>.</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>The visible time range: the retained extent until the user zooms or pans (presentation only, §6.4).</summary>
    public TimeRange Viewport => viewport ?? Extent;

    /// <summary>Whether the whole retained extent is shown, following it as a live session grows.</summary>
    public bool IsFit => viewport is null;

    private TimeRange Extent => (DataContext as WorkspaceViewModel)?.Snapshot.Extent ?? new TimeRange(0, 1);

    private double PlotWidth => Math.Max(1, Bounds.Width - PlotLeft - PlotRightMargin);

    /// <summary>
    /// Shows a range, clamped inside the retained extent; null, or the whole extent, fits it. Every viewport change
    /// passes through here, so the minimap and the timeline's detail request always see the range that is drawn.
    /// </summary>
    public void SetViewport(TimeRange? range)
    {
        TimeRange extent = Extent;
        TimeRange? next = range?.ClampInside(extent);
        if (next == extent)
        {
            next = null;
        }

        if (next == viewport)
        {
            return;
        }

        viewport = next;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        detailTimer.Stop();
        detailTimer.Start();
    }

    /// <summary>Asks at once for the detail the resting viewport needs, without waiting for it to settle.</summary>
    internal void RequestDetailNow()
    {
        detailTimer.Stop();
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.RequestTimelineDetail(Viewport, (int)Math.Clamp(PlotWidth / 10, 16, 256));
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // A newer generation replaces the view model: the zoomed range stays, and its detail is asked for again.
        RequestDetailNow();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        detailTimer.Stop();
        detailTimer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        TimeRange visible = Viewport;
        double left = PlotLeft;
        double right = Math.Max(left + 1, Bounds.Width - PlotRightMargin);
        double top = 24;
        double bottom = Math.Max(top + 1, Bounds.Height - 30);
        double plotWidth = right - left;
        double plotHeight = bottom - top;
        context.DrawLine(new Pen(GridBrush, 1), new(left, bottom), new(right, bottom));
        for (int line = 1; line <= 3; line++)
        {
            double y = top + (plotHeight * line / 4);
            context.DrawLine(new Pen(GridBrush, 0.7), new(left, y), new(right, y));
        }

        // The overview's coarse buckets stand wherever the viewport's own count has not arrived; the detail replaces
        // them over the interval it answers. Heights are rates, records per tick, so a coarse bucket and a fine one
        // side by side are drawn on one honest scale rather than by counts over unequal widths (§6.2).
        SessionTimelineDetail? detail = viewModel.TimelineDetail is { } answered && Intersects(answered.Interval, visible)
            ? answered : null;
        TimelineBucket[] coarse = [.. viewModel.Snapshot.Timeline
            .Where(bucket => Intersects(bucket.Interval, visible)
                && (detail is null || !Inside(bucket.Interval, detail.Interval)))];
        TimelineBucket[] fine = detail is null ? [] : [.. detail.Buckets.Where(bucket => Intersects(bucket.Interval, visible))];
        double maximumRate = coarse.Concat(fine).Select(Rate).DefaultIfEmpty(0).Max();
        var scale = new BarScale(visible, left, plotWidth, top, bottom, maximumRate);
        if (detail is null)
        {
            DrawBuckets(context, viewModel, coarse, scale);
        }
        else
        {
            double x1 = scale.X(Math.Max(detail.Interval.StartTicks, visible.StartTicks));
            double x2 = scale.X(Math.Min(detail.Interval.EndTicks, visible.EndTicks));
            using (context.PushClip(new Rect(left, 0, Math.Max(0, x1 - left), Bounds.Height)))
            {
                DrawBuckets(context, viewModel, coarse, scale);
            }

            using (context.PushClip(new Rect(x2, 0, Math.Max(0, right - x2), Bounds.Height)))
            {
                DrawBuckets(context, viewModel, coarse, scale);
            }

            using (context.PushClip(new Rect(x1, 0, Math.Max(0, x2 - x1), Bounds.Height)))
            {
                DrawBuckets(context, viewModel, fine, scale);
            }

            if (detail.Generation != viewModel.DisplayedGeneration)
            {
                // A paused or held view keeps its generation; a zoomed count reads the newest one and says so.
                string note = $"zoomed detail from generation {detail.Generation:N0}";
                DrawText(context, note, new(right - (5.6 * note.Length), top - 18));
            }
        }

        DrawSelection(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawEvidenceMarks(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawText(context, WorkspaceTime.FormatInstant(visible.StartTicks, visible.SpanTicks, CultureInfo.CurrentCulture), new(left, bottom + 7));
        string end = WorkspaceTime.FormatInstant(visible.EndTicks, visible.SpanTicks, CultureInfo.CurrentCulture);
        DrawText(context, end, new(right - (6.5 * end.Length), bottom + 7));
        DrawText(context, RateText(maximumRate * WorkspaceTime.TicksPerSecond), new(4, top - 4));
    }

    /// <summary>Records per presentation tick: what a bar's height states, comparable across bucket widths.</summary>
    private static double Rate(TimelineBucket bucket) => (double)bucket.ObservationCount / bucket.Interval.SpanTicks;

    /// <summary>The axis's top label: the peak rate drawn, in records per second.</summary>
    internal static string RateText(double perSecond) => perSecond switch
    {
        <= 0 => "0/s",
        >= 100 => $"{perSecond.ToString("N0", CultureInfo.CurrentCulture)}/s",
        >= 1 => $"{perSecond.ToString("N1", CultureInfo.CurrentCulture)}/s",
        _ => $"{perSecond.ToString("N2", CultureInfo.CurrentCulture)}/s",
    };

    private readonly record struct BarScale(
        TimeRange Visible, double Left, double PlotWidth, double Top, double Bottom, double MaximumRate)
    {
        public double X(long tick) => Left + ViewportMath.PixelAtTick(Visible, tick, PlotWidth);
    }

    private static void DrawBuckets(
        DrawingContext context, WorkspaceViewModel viewModel, IEnumerable<TimelineBucket> buckets, BarScale scale)
    {
        double plotHeight = scale.Bottom - scale.Top;
        foreach (TimelineBucket bucket in buckets)
        {
            double x1 = scale.X(Math.Max(bucket.Interval.StartTicks, scale.Visible.StartTicks));
            double x2 = scale.X(Math.Min(bucket.Interval.EndTicks, scale.Visible.EndTicks));
            double width = Math.Max(1, x2 - x1 - 2);
            if (bucket.ObservationCount > 0)
            {
                // Coverage and observation are separate facts. A gap or unknown coverage cannot erase records that
                // were actually seen; the hatch crosses their bar without claiming unseen activity (P1, P26).
                double height = Math.Max(3, plotHeight * Rate(bucket) / Math.Max(double.Epsilon, scale.MaximumRate));
                var rectangle = new Rect(x1, scale.Bottom - height, width, height);
                context.DrawRectangle(BrushFor(bucket.DominantMechanism), null, rectangle);
                if (viewModel.SelectedInterval == bucket.Interval)
                {
                    context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 2), rectangle.Inflate(2));
                }
            }

            if (bucket.Coverage != CoverageState.Covered)
            {
                DrawCoverageGap(context, new(x1, scale.Top, width, plotHeight));
            }
        }
    }

    /// <summary>
    /// The analysis interval as a translucent band with accent edges, and the brush being dragged as the same band, so
    /// the range the ranking and graph now count is visible on the axis it came from (plan §6.4).
    /// </summary>
    private void DrawSelection(DrawingContext context, WorkspaceViewModel viewModel, TimeRange visible,
        double left, double plotWidth, double top, double bottom)
    {
        TimeRange? range = brushAnchor is { } anchor && brushEnd is { } end
            ? new TimeRange(Math.Min(anchor, end), Math.Max(anchor, end) + 1)
            : viewModel.SelectedInterval;
        if (range is not { } shown || shown.EndTicks <= visible.StartTicks || shown.StartTicks >= visible.EndTicks)
        {
            return;
        }

        double x1 = left + ViewportMath.PixelAtTick(visible, Math.Max(shown.StartTicks, visible.StartTicks), plotWidth);
        double x2 = Math.Max(x1 + 2, left + ViewportMath.PixelAtTick(visible, Math.Min(shown.EndTicks, visible.EndTicks), plotWidth));

        // Everything outside the range is dimmed rather than the range tinted, so the counted records keep their true
        // mechanism hue (§6.6) while the rest steps back.
        var dim = new SolidColorBrush(DimBrush.Color, 0.6);
        context.DrawRectangle(dim, null, new Rect(left, top, Math.Max(0, x1 - left), bottom - top));
        context.DrawRectangle(dim, null, new Rect(x2, top, Math.Max(0, left + plotWidth - x2), bottom - top));
        var edge = new Pen(SelectedBrush, 2);
        context.DrawLine(edge, new(x1, top), new(x1, bottom));
        context.DrawLine(edge, new(x2, top), new(x2, bottom));
        context.DrawRectangle(SelectedBrush, null, new Rect(x1, top - 6, x2 - x1, 3));
    }

    /// <summary>
    /// At the evidence rung each loaded record is an individual mark on a strip above the axis, and the selected one
    /// is drawn full height in the accent, so the list and the time axis point at the same record (section 3.2 L5).
    /// Marks are records, not a rate: an empty stretch between them says nothing about records not yet loaded.
    /// </summary>
    private static void DrawEvidenceMarks(DrawingContext context, WorkspaceViewModel viewModel, TimeRange visible,
        double left, double plotWidth, double top, double bottom)
    {
        IReadOnlyList<long> marks = viewModel.EvidenceMarkTicks;
        if (marks.Count == 0)
        {
            return;
        }

        var pen = new Pen(TextBrush, 1);
        double markTop = bottom - 10;
        double lastX = double.NaN;
        foreach (long tick in marks)
        {
            if (!visible.Contains(tick))
            {
                continue;
            }

            double x = left + ViewportMath.PixelAtTick(visible, tick, plotWidth);
            if (Math.Abs(x - lastX) < 1)
            {
                continue;
            }

            context.DrawLine(pen, new(x, markTop), new(x, bottom));
            lastX = x;
        }

        if (viewModel.SelectedEvidenceTick is { } selected && visible.Contains(selected))
        {
            double x = left + ViewportMath.PixelAtTick(visible, selected, plotWidth);
            context.DrawLine(new Pen(SelectedBrush, 2), new(x, top), new(x, bottom));
        }
    }

    /// <summary>
    /// A coverage gap is drawn as the section 6.6 diagonal hatch: a texture, not a colour and not a
    /// symbol, so it survives greyscale and a colour-vision difference and never looks like a value. The
    /// cell is never filled with an interpolated height, because a gap has no measurement to draw (P26).
    /// </summary>
    internal static void DrawCoverageGap(DrawingContext context, Rect cell)
    {
        const double spacing = 7;
        context.DrawRectangle(Brushes.Transparent, new Pen(GapBrush, 1), cell);
        using (context.PushClip(cell))
        {
            var pen = new Pen(GapBrush, 1);
            for (double offset = -cell.Height; offset < cell.Width; offset += spacing)
            {
                context.DrawLine(
                    pen,
                    new(cell.X + offset, cell.Bottom),
                    new(cell.X + offset + cell.Height, cell.Top));
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        double pixel = Math.Clamp(e.GetPosition(this).X - PlotLeft, 0, PlotWidth);
        decimal factor = e.Delta.Y > 0 ? 1.25m : 0.8m;
        SetViewport(ViewportMath.ZoomAtPixel(Viewport, pixel, PlotWidth, factor, viewModel.Snapshot.Extent, MinimumSpanTicks));
        e.Handled = true;
    }

    /// <summary>
    /// The §6.7 gestures: a drag pans the viewport, Shift+drag or a middle-button drag brushes a time range that becomes
    /// the analysis interval, and a press that does not move selects the bucket under it. Pan and brush never conflict,
    /// and every one has a keyboard equivalent (arrows, Home/End, 0) or a table equivalent (T).
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (DataContext is not WorkspaceViewModel)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        pressX = point.Position.X;
        brushing = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || point.Properties.IsMiddleButtonPressed;
        moved = false;
        panOrigin = Viewport;
        brushAnchor = TickAt(pressX);
        brushEnd = null;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (brushAnchor is null || DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        double x = e.GetPosition(this).X;
        if (!moved && Math.Abs(x - pressX) < BrushThreshold)
        {
            return;
        }

        moved = true;
        if (brushing)
        {
            brushEnd = TickAt(x);
            InvalidateVisual();
        }
        else
        {
            // Pan from the viewport the drag began with, so one continuous drag accumulates no rounding drift (§6.7).
            SetViewport(ViewportMath.PanByFraction(panOrigin, (decimal)(-(x - pressX) / PlotWidth), viewModel.Snapshot.Extent));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (brushAnchor is not { } anchor || DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        long? end = brushEnd;
        bool wasBrushing = brushing && moved;
        bool wasClick = !moved;
        brushAnchor = null;
        brushEnd = null;
        e.Pointer.Capture(null);
        TimeRange extent = viewModel.Snapshot.Extent;
        if (wasBrushing && end is { } dragged)
        {
            long start = Math.Max(extent.StartTicks, Math.Min(anchor, dragged));
            long stop = Math.Min(extent.EndTicks, Math.Max(anchor, dragged) + 1);
            if (stop > start)
            {
                viewModel.SelectInterval(new TimeRange(start, stop));
            }
        }
        else if (wasClick && BucketAt(viewModel, anchor) is { } bucket)
        {
            viewModel.SelectInterval(bucket.Interval);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>The finest bucket drawn at a tick: the zoomed detail where it has arrived, else the overview's.</summary>
    private static TimelineBucket? BucketAt(WorkspaceViewModel viewModel, long tick) =>
        (viewModel.TimelineDetail is { } detail && detail.Interval.Contains(tick)
            ? detail.Buckets
            : viewModel.Snapshot.Timeline).FirstOrDefault(candidate => candidate.Interval.Contains(tick));

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        brushAnchor = null;
        brushEnd = null;
        InvalidateVisual();
    }

    private long TickAt(double x) =>
        ViewportMath.TickAtPixel(Viewport, Math.Clamp(x - PlotLeft, 0, PlotWidth), PlotWidth);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Navigate(e.Key)) e.Handled = true;
    }

    /// <summary>Shared timeline/minimap keyboard navigation over one viewport and one retained extent.</summary>
    internal bool Navigate(Key key)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return false;
        }

        TimeRange current = Viewport;
        TimeRange extent = viewModel.Snapshot.Extent;
        TimeRange? next;
        switch (key)
        {
            case Key.Home:
                next = new TimeRange(extent.StartTicks, extent.StartTicks + current.SpanTicks);
                break;
            case Key.End:
                next = new TimeRange(extent.EndTicks - current.SpanTicks, extent.EndTicks);
                break;
            case Key.D0:
            case Key.NumPad0:
                // Fit the analysis scope: the brushed interval when there is one, else the retained extent (§6.7).
                next = viewModel.SelectedInterval;
                break;
            case Key.Left:
                next = ViewportMath.PanByFraction(current, -0.1m, extent);
                break;
            case Key.Right:
                next = ViewportMath.PanByFraction(current, 0.1m, extent);
                break;
            case Key.Add:
            case Key.OemPlus:
                next = ViewportMath.ZoomAtPixel(current, PlotWidth / 2, PlotWidth, 1.25m, extent, MinimumSpanTicks);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                next = ViewportMath.ZoomAtPixel(current, PlotWidth / 2, PlotWidth, 0.8m, extent, MinimumSpanTicks);
                break;
            default:
                return false;
        }

        SetViewport(next);
        return true;
    }

    private static bool Intersects(TimeRange left, TimeRange right) =>
        left.StartTicks < right.EndTicks && right.StartTicks < left.EndTicks;

    private static bool Inside(TimeRange inner, TimeRange outer) =>
        inner.StartTicks >= outer.StartTicks && inner.EndTicks <= outer.EndTicks;

    /// <summary>Hue comes from the mechanism's family token; nothing else may assign it (section 6.6, R5).</summary>
    private static SolidColorBrush BrushFor(Mechanism mechanism) =>
        new SolidColorBrush(ThemeResources.FillOf(mechanism, Mode));

    internal static void DrawText(DrawingContext context, string text, Point origin)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new("Segoe UI"), 10, TextBrush);
        context.DrawText(formatted, origin);
    }
}
