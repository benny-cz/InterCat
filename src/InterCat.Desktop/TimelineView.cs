using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    private TimeRange? viewport;

    /// <summary>The visible time range: the retained extent until the user zooms or pans (presentation only, §6.4).</summary>
    public TimeRange Viewport => viewport ?? (DataContext as WorkspaceViewModel)?.Snapshot.Extent ?? new TimeRange(0, 1);
    private long? brushAnchor;
    private long? brushEnd;
    private double pressX;
    private bool brushing;
    private bool moved;
    private TimeRange panOrigin;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        TimeRange visible = viewport ?? viewModel.Snapshot.Extent;
        double left = 38;
        double right = Math.Max(left + 1, Bounds.Width - 14);
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

        int maximum = Math.Max(1, viewModel.Snapshot.Timeline
            .Where(bucket => Intersects(bucket.Interval, visible))
            .Select(bucket => bucket.ObservationCount)
            .DefaultIfEmpty(1)
            .Max());

        foreach (TimelineBucket bucket in viewModel.Snapshot.Timeline)
        {
            if (!Intersects(bucket.Interval, visible))
            {
                continue;
            }

            double x1 = left + ViewportMath.PixelAtTick(visible, Math.Max(bucket.Interval.StartTicks, visible.StartTicks), plotWidth);
            double x2 = left + ViewportMath.PixelAtTick(visible, Math.Min(bucket.Interval.EndTicks, visible.EndTicks), plotWidth);
            double width = Math.Max(1, x2 - x1 - 2);
            if (bucket.ObservationCount > 0)
            {
                // Coverage and observation are separate facts. A gap or unknown coverage cannot erase records that
                // were actually seen; the hatch crosses their bar without claiming unseen activity (P1, P26).
                double height = Math.Max(3, plotHeight * bucket.ObservationCount / maximum);
                var rectangle = new Rect(x1, bottom - height, width, height);
                context.DrawRectangle(BrushFor(bucket.DominantMechanism), null, rectangle);
                if (viewModel.SelectedInterval == bucket.Interval)
                {
                    context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 2), rectangle.Inflate(2));
                }
            }

            if (bucket.Coverage != CoverageState.Covered)
            {
                DrawCoverageGap(context, new(x1, top, width, plotHeight));
            }
        }

        DrawSelection(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawEvidenceMarks(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawText(context, WorkspaceTime.FormatInstant(visible.StartTicks, visible.SpanTicks, CultureInfo.CurrentCulture), new(left, bottom + 7));
        string end = WorkspaceTime.FormatInstant(visible.EndTicks, visible.SpanTicks, CultureInfo.CurrentCulture);
        DrawText(context, end, new(right - (6.5 * end.Length), bottom + 7));
        DrawText(context, maximum.ToString("N0", CultureInfo.CurrentCulture), new(4, top - 4));
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
    private static void DrawCoverageGap(DrawingContext context, Rect cell)
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

        TimeRange current = viewport ?? viewModel.Snapshot.Extent;
        double plotWidth = Math.Max(1, Bounds.Width - 52);
        double pixel = Math.Clamp(e.GetPosition(this).X - 38, 0, plotWidth);
        decimal factor = e.Delta.Y > 0 ? 1.25m : 0.8m;
        viewport = ViewportMath.ZoomAtPixel(current, pixel, plotWidth, factor, viewModel.Snapshot.Extent, 100_000);
        InvalidateVisual();
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
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        pressX = point.Position.X;
        brushing = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || point.Properties.IsMiddleButtonPressed;
        moved = false;
        panOrigin = viewport ?? viewModel.Snapshot.Extent;
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
        }
        else
        {
            // Pan from the viewport the drag began with, so one continuous drag accumulates no rounding drift (§6.7).
            double plotWidth = Math.Max(1, Bounds.Width - 52);
            viewport = ViewportMath.PanByFraction(panOrigin, (decimal)(-(x - pressX) / plotWidth), viewModel.Snapshot.Extent);
        }

        InvalidateVisual();
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
        else if (wasClick
            && viewModel.Snapshot.Timeline.FirstOrDefault(candidate => candidate.Interval.Contains(anchor)) is { } bucket)
        {
            viewModel.SelectInterval(bucket.Interval);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        brushAnchor = null;
        brushEnd = null;
        InvalidateVisual();
    }

    private long TickAt(double x)
    {
        TimeRange visible = viewport ?? (DataContext as WorkspaceViewModel)?.Snapshot.Extent ?? new TimeRange(0, 1);
        double plotWidth = Math.Max(1, Bounds.Width - 52);
        return ViewportMath.TickAtPixel(visible, Math.Clamp(x - 38, 0, plotWidth), plotWidth);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        TimeRange current = viewport ?? viewModel.Snapshot.Extent;
        TimeRange extent = viewModel.Snapshot.Extent;
        switch (e.Key)
        {
            case Key.Home:
                viewport = new TimeRange(extent.StartTicks, extent.StartTicks + current.SpanTicks).ClampInside(extent);
                break;
            case Key.End:
                viewport = new TimeRange(extent.EndTicks - current.SpanTicks, extent.EndTicks).ClampInside(extent);
                break;
            case Key.D0:
            case Key.NumPad0:
                // Fit the analysis scope: the brushed interval when there is one, else the retained extent (§6.7).
                viewport = viewModel.SelectedInterval is { } scope ? scope.ClampInside(extent) : null;
                break;
            case Key.Left:
                viewport = ViewportMath.PanByFraction(current, -0.1m, viewModel.Snapshot.Extent);
                break;
            case Key.Right:
                viewport = ViewportMath.PanByFraction(current, 0.1m, viewModel.Snapshot.Extent);
                break;
            case Key.Add:
            case Key.OemPlus:
                viewport = ViewportMath.ZoomAtPixel(current, Bounds.Width / 2, Bounds.Width, 1.25m, viewModel.Snapshot.Extent, 100_000);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                viewport = ViewportMath.ZoomAtPixel(current, Bounds.Width / 2, Bounds.Width, 0.8m, viewModel.Snapshot.Extent, 100_000);
                break;
            default:
                return;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private static bool Intersects(TimeRange left, TimeRange right) =>
        left.StartTicks < right.EndTicks && right.StartTicks < left.EndTicks;

    /// <summary>Hue comes from the mechanism's family token; nothing else may assign it (section 6.6, R5).</summary>
    private static SolidColorBrush BrushFor(Mechanism mechanism) =>
        new SolidColorBrush(ThemeResources.FillOf(mechanism, Mode));

    private static void DrawText(DrawingContext context, string text, Point origin)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new("Segoe UI"), 10, TextBrush);
        context.DrawText(formatted, origin);
    }
}
