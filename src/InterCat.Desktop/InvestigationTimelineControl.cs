using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Theme;

namespace InterCat.Desktop;

/// <summary>
/// An investigation's merged time (§8.2): one lane per session on the investigation's own axis - its records counted over
/// shared columns where its alignment places them, its extent shaded, each lane scaled to its own busiest column - and a
/// session with no place said in its lane. It draws; the window beside it states every lane in words.
/// </summary>
internal sealed class InvestigationTimelineControl : Control
{
    public const double LabelWidth = 240;
    public const double LaneHeight = 48;
    public const double AxisHeight = 26;

    private InvestigationTimelineView? view;
    private IReadOnlyList<string> labels = [];

    public void Show(InvestigationTimelineView? shown, IReadOnlyList<string> laneLabels)
    {
        view = shown;
        labels = laneLabels;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
        AxisHeight + (Math.Max(view?.Lanes.Count ?? 0, 1) * LaneHeight) + 8);

    public override void Render(DrawingContext context)
    {
        Ink current = Inks.TryGetValue(ThemeResources.CurrentMode, out Ink? known) ? known : Inks[ThemeResources.CurrentMode] = new(ThemeResources.CurrentMode);
        (IBrush plot, IBrush extent, IBrush bar, IBrush ink, IBrush muted, Pen divider) =
            (current.Plot, current.Extent, current.Bar, current.Text, current.Muted, current.Divider);
        context.FillRectangle(plot, new Rect(Bounds.Size));
        if (view?.Interval is not { } interval)
        {
            Text(context, "No session is aligned yet: align one to another to place them on one axis.", 12, muted, new Point(12, 12), Bounds.Width - 24);
            return;
        }

        double left = LabelWidth;
        double width = Math.Max(Bounds.Width - LabelWidth - 12, 10);
        double X(long ticks) => left + (width * (ticks - interval.StartTicks) / (double)(interval.EndTicks - interval.StartTicks));

        // The axis: the investigation's time, in seconds of its reference session's clock, to the precision its ticks need.
        double step = (interval.EndTicks - interval.StartTicks) / 4.0 / 10_000_000;
        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(step)) + 1, 0, 7);
        string format = decimals == 0 ? "0" : "0." + new string('0', decimals);
        for (int tick = 0; tick <= 4; tick++)
        {
            long at = interval.StartTicks + ((interval.EndTicks - interval.StartTicks) * tick / 4);
            double x = X(at);
            context.DrawLine(divider, new Point(x, AxisHeight - 4), new Point(x, Bounds.Height - 4));
            string label = (at / 10_000_000m).ToString(format, CultureInfo.CurrentCulture) + " s";
            Text(context, label, 10, muted, new Point(tick == 4 ? x - 60 : x + 3, 6), 90);
        }

        for (int index = 0; index < view.Lanes.Count; index++)
        {
            InvestigationLane lane = view.Lanes[index];
            double top = AxisHeight + (index * LaneHeight);
            context.DrawLine(divider, new Point(0, top), new Point(Bounds.Width, top));
            Text(context, index < labels.Count ? labels[index] : string.Empty, 11, ink, new Point(10, top + 5), LabelWidth - 18);
            if (!lane.Placed)
            {
                continue;
            }

            context.FillRectangle(extent, new Rect(new Point(X(lane.Extent!.Value.StartTicks), top + 4),
                new Point(Math.Max(X(lane.Extent.Value.EndTicks), X(lane.Extent.Value.StartTicks) + 1), top + LaneHeight - 4)));
            int busiest = lane.Buckets.Count == 0 ? 0 : lane.Buckets.Max(bucket => bucket.ObservationCount);
            if (busiest == 0)
            {
                continue;
            }

            double column = width / lane.Buckets.Count;
            for (int b = 0; b < lane.Buckets.Count; b++)
            {
                int count = lane.Buckets[b].ObservationCount;
                if (count == 0)
                {
                    continue;
                }

                double height = Math.Max(2, (LaneHeight - 12) * count / busiest);
                context.FillRectangle(bar, new Rect(left + (b * column) + 0.5, top + LaneHeight - 6 - height, Math.Max(column - 1, 1), height));
            }
        }
    }

    // Brushes are built once per theme mode and reused every frame (R11), as the session timeline's are.
    private static readonly Dictionary<ThemeMode, Ink> Inks = [];

    private sealed class Ink(ThemeMode mode)
    {
        public IBrush Plot { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Plot));
        public IBrush Extent { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Elevated));
        public IBrush Bar { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Accent));
        public IBrush Text { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink));
        public IBrush Muted { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).MutedInk));
        public Pen Divider { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Divider)), 1);
    }

    private static void Text(DrawingContext context, string text, double size, IBrush brush, Point at, double maximumWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(maximumWidth, 10),
        };
        context.DrawText(formatted, at);
    }
}
