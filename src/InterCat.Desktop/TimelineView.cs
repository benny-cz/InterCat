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

    private static SolidColorBrush Token(Srgb value) => new SolidColorBrush(ThemeResources.ToColor(value));
    private TimeRange? viewport;

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
            if (bucket.Coverage != CoverageState.Covered)
            {
                DrawCoverageGap(context, new(x1, top, width, plotHeight));
                continue;
            }

            double height = Math.Max(3, plotHeight * bucket.ObservationCount / maximum);
            var rectangle = new Rect(x1, bottom - height, width, height);
            context.DrawRectangle(BrushFor(bucket.DominantMechanism), null, rectangle);
            if (viewModel.SelectedInterval == bucket.Interval)
            {
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 2), rectangle.Inflate(2));
            }
        }

        DrawText(context, $"{visible.StartTicks / (decimal)WorkspaceTime.TicksPerSecond:N1}s", new(left, bottom + 7));
        DrawText(context, $"{visible.EndTicks / (decimal)WorkspaceTime.TicksPerSecond:N1}s", new(right - 38, bottom + 7));
        DrawText(context, maximum.ToString("N0", CultureInfo.CurrentCulture), new(4, top - 4));
    }

    /// <summary>
    /// A coverage gap is drawn as the section 6.6 diagonal hatch: a texture, not a colour and not a
    /// symbol, so it survives greyscale and a colour-vision difference and never looks like a value. The
    /// cell is never filled with an interpolated height, because a gap has no measurement to draw (P26).
    /// </summary>
    private static void DrawCoverageGap(DrawingContext context, Rect cell)
    {
        const double spacing = 7;
        context.DrawRectangle(Token(ThemePalette.Surfaces(Mode).Elevated), new Pen(GapBrush, 1), cell);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        TimeRange visible = viewport ?? viewModel.Snapshot.Extent;
        double plotWidth = Math.Max(1, Bounds.Width - 52);
        long tick = ViewportMath.TickAtPixel(visible, e.GetPosition(this).X - 38, plotWidth);
        TimelineBucket? bucket = viewModel.Snapshot.Timeline.FirstOrDefault(candidate => candidate.Interval.Contains(tick));
        if (bucket is not null)
        {
            viewModel.SelectInterval(bucket.Interval);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        TimeRange current = viewport ?? viewModel.Snapshot.Extent;
        switch (e.Key)
        {
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
