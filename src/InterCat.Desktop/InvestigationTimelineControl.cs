using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>A lane's column a person chose on the merged time: which lane, which column.</summary>
internal readonly record struct TimelineColumn(int Lane, int Column);

/// <summary>
/// An investigation's merged time (§8.2): one lane per session on the investigation's own axis - its records counted over
/// shared columns where its alignment places them, its extent shaded, each lane scaled to its own busiest column - and a
/// session with no place said in its lane. A person moves a column cursor with the arrow keys, Home and End, or a click,
/// zooms with the wheel or the plus and minus keys (0 shows the whole), and opens a column with Enter or a double click.
/// The window beside it states every lane, and the column chosen, in words.
/// </summary>
internal sealed class InvestigationTimelineControl : Control
{
    public const double LabelWidth = 240;
    public const double LaneHeight = 48;
    public const double AxisHeight = 26;

    private InvestigationTimelineView? view;
    private IReadOnlyList<string> labels = [];
    private TimelineColumn? cursor;

    public InvestigationTimelineControl()
    {
        Focusable = true;
        AutomationProperties.SetName(this, Spoken(null));
    }

    /// <summary>The column the cursor is on moved, or the view it is on changed.</summary>
    public event EventHandler? CursorMoved;

    /// <summary>A person asked to open a column's records: Enter, or a double click.</summary>
    public event EventHandler<TimelineColumn>? ColumnChosen;

    /// <summary>A person asked to see another interval of the investigation's time; null asks for the whole.</summary>
    public event EventHandler<TimeRange?>? ZoomRequested;

    /// <summary>The column the cursor is on; null when no lane is placed.</summary>
    public TimelineColumn? ChosenColumn => cursor;

    public void Show(InvestigationTimelineView? shown, IReadOnlyList<string> laneLabels)
    {
        view = shown;
        labels = laneLabels;

        // The cursor keeps its lane and its place along the axis, as far as the new view has them.
        cursor = cursor is { } kept && Placed(kept.Lane) ? kept with { Column = Math.Min(kept.Column, view!.Lanes[kept.Lane].Buckets.Count - 1) }
            : FirstPlaced() is { } lane ? new TimelineColumn(lane, Busiest(lane)) : null;
        AutomationProperties.SetName(this, Spoken(cursor));
        InvalidateMeasure();
        InvalidateVisual();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the cursor to a lane's column, as the arrow keys or a click do; a test uses it.</summary>
    internal void MoveTo(TimelineColumn column)
    {
        if (!Placed(column.Lane) || column.Column < 0 || column.Column >= view!.Lanes[column.Lane].Buckets.Count) return;
        cursor = column;
        AutomationProperties.SetName(this, Spoken(cursor));
        InvalidateVisual();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The chosen column in words: its session, its time, how many records it holds, and what its capture covered there,
    /// so a column of none the capture did not cover is not heard as a quiet one (R21). Only a column with records has
    /// any to open.
    /// </summary>
    internal string? Describe()
    {
        if (cursor is not { } at || view is null) return null;
        InvestigationLane lane = view.Lanes[at.Lane];
        TimelineBucket bucket = lane.Buckets[at.Column];
        string span = WorkspaceTime.FormatRange(bucket.Interval, CultureInfo.CurrentCulture) + " of the investigation's time";
        return string.Create(CultureInfo.CurrentCulture, $"Session {lane.SessionId.ToString("N")[..8]}, column {at.Column + 1:N0} of {lane.Buckets.Count:N0}: ")
            + span + ", " + Presentation.Spoken.Count(bucket.ObservationCount, "record")
            + ", " + Presentation.Spoken.Coverage(CoverageStateText.Label(bucket.Coverage))
            + (bucket.ObservationCount == 0 ? "." : ". Enter opens them in InterCat.");
    }

    /// <summary>
    /// Where a column's coverage is drawn, as the session's own timeline draws it (R21): nothing where its capture covered
    /// the column; a thin hatched strip along the lane's foot where nothing is known of it, as outside every reading the
    /// capture delivered; and the whole lane hatched where the capture lost, or did not collect, what it could have seen.
    /// </summary>
    internal Rect? CoverageCell(int lane, int column)
    {
        if (view is null || !Placed(lane) || column < 0 || column >= view.Lanes[lane].Buckets.Count)
        {
            return null;
        }

        IReadOnlyList<TimelineBucket> buckets = view.Lanes[lane].Buckets;
        CoverageState coverage = buckets[column].Coverage;
        if (coverage == CoverageState.Covered)
        {
            return null;
        }

        double width = PlotWidth / buckets.Count;
        double x = LabelWidth + (column * width) + 0.5;
        double top = AxisHeight + (lane * LaneHeight);
        return coverage == CoverageState.UnknownCoverage
            ? new Rect(x, top + LaneHeight - 9, Math.Max(width - 1, 1), 5)
            : new Rect(x, top + 4, Math.Max(width - 1, 1), LaneHeight - 8);
    }

    /// <summary>
    /// A lane's hatching as it is drawn: each run of adjacent columns in one state other than covered as one cell, from its
    /// first column's to its last's, so a stretch the capture did not cover reads as one band rather than a comb of columns.
    /// </summary>
    internal IReadOnlyList<Rect> CoverageRuns(int lane)
    {
        var runs = new List<Rect>();
        IReadOnlyList<TimelineBucket> buckets = Placed(lane) ? view!.Lanes[lane].Buckets : [];
        int first = 0;
        while (first < buckets.Count)
        {
            int last = first;
            while (last + 1 < buckets.Count && buckets[last + 1].Coverage == buckets[first].Coverage)
            {
                last++;
            }

            if (CoverageCell(lane, first) is { } start && CoverageCell(lane, last) is { } end)
            {
                runs.Add(start.Union(end));
            }

            first = last + 1;
        }

        return runs;
    }

    /// <summary>
    /// The merged time as a screen reader meets it: its role, its keyboard path, and the column chosen now; the list below
    /// it states every lane in words (R15).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "timeline",
        "Left and Right choose a column, Home and End a lane's first and last, Up and Down another session's lane; Enter "
        + "opens the column's records in InterCat; plus and minus zoom around it, and 0 shows the whole investigation. The "
        + "list below states every lane in words.",
        Describe);

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
        AxisHeight + (Math.Max(view?.Lanes.Count ?? 0, 1) * LaneHeight) + 8);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (view is null || cursor is not { } at)
        {
            return;
        }

        int columns = view.Lanes[at.Lane].Buckets.Count;
        switch (e.Key)
        {
            case Key.Left:
                MoveTo(at with { Column = Math.Max(0, at.Column - 1) });
                break;
            case Key.Right:
                MoveTo(at with { Column = Math.Min(columns - 1, at.Column + 1) });
                break;
            case Key.Home:
                MoveTo(at with { Column = 0 });
                break;
            case Key.End:
                MoveTo(at with { Column = columns - 1 });
                break;
            case Key.Up when NextPlaced(at.Lane, -1) is { } above:
                MoveTo(new(above, Math.Min(at.Column, view.Lanes[above].Buckets.Count - 1)));
                break;
            case Key.Down when NextPlaced(at.Lane, 1) is { } below:
                MoveTo(new(below, Math.Min(at.Column, view.Lanes[below].Buckets.Count - 1)));
                break;
            case Key.Enter:
                ColumnChosen?.Invoke(this, at);
                break;
            case Key.Add or Key.OemPlus:
                ZoomAround(at, 0.5m);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomAround(at, 2m);
                break;
            case Key.D0 or Key.NumPad0:
                ZoomRequested?.Invoke(this, null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        Focus();
        if (ColumnAt(e.GetPosition(this)) is not { } at) return;
        MoveTo(at);
        if (e.ClickCount >= 2)
        {
            ColumnChosen?.Invoke(this, at);
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (view?.Interval is not { } interval || e.Delta.Y == 0) return;
        double left = LabelWidth;
        double width = Math.Max(Bounds.Width - LabelWidth - 12, 10);
        double fraction = Math.Clamp((e.GetPosition(this).X - left) / width, 0, 1);
        long at = interval.StartTicks + (long)((interval.EndTicks - interval.StartTicks) * fraction);
        ZoomRequested?.Invoke(this, Zoomed(interval, at, e.Delta.Y > 0 ? 0.5m : 2m));
        e.Handled = true;
    }

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
        double width = PlotWidth;
        double X(long ticks) => left + (width * (ticks - interval.StartTicks) / (double)(interval.EndTicks - interval.StartTicks));

        // A rule at each end of the axis and at each tick it names between them, its labels placed and measured once a frame.
        IReadOnlyList<(string Label, Rect Where, long Ticks)> axis = AxisLabels();
        foreach (long ticks in axis.Select(label => label.Ticks).Append(interval.StartTicks).Append(interval.EndTicks).Distinct())
        {
            double x = X(ticks);
            context.DrawLine(divider, new Point(x, AxisHeight - 4), new Point(x, Bounds.Height - 4));
        }

        foreach ((string label, Rect where, _) in axis)
        {
            Text(context, label, AxisFontSize, muted, where.TopLeft, where.Width + 1);
        }

        for (int index = 0; index < view.Lanes.Count; index++)
        {
            InvestigationLane lane = view.Lanes[index];
            double top = AxisHeight + (index * LaneHeight);
            context.DrawLine(divider, new Point(0, top), new Point(Bounds.Width, top));

            context.DrawText(LaneLabel(index, ink), new Point(10, top + 5));
            if (!lane.Placed)
            {
                continue;
            }

            context.FillRectangle(extent, new Rect(new Point(Math.Max(X(lane.Extent!.Value.StartTicks), left), top + 4),
                new Point(Math.Min(Math.Max(X(lane.Extent.Value.EndTicks), X(lane.Extent.Value.StartTicks) + 1), left + width), top + LaneHeight - 4)));
            double column = width / lane.Buckets.Count;
            if (cursor is { } chosen && chosen.Lane == index)
            {
                context.DrawRectangle(null, current.Cursor, new Rect(left + (chosen.Column * column), top + 2, Math.Max(column, 3), LaneHeight - 4));
            }

            int busiest = lane.Buckets.Count == 0 ? 0 : lane.Buckets.Max(bucket => bucket.ObservationCount);
            for (int b = 0; b < lane.Buckets.Count; b++)
            {
                int count = lane.Buckets[b].ObservationCount;
                if (count > 0)
                {
                    double height = Math.Max(2, (LaneHeight - 12) * count / busiest);
                    context.FillRectangle(bar, new Rect(left + (b * column) + 0.5, top + LaneHeight - 6 - height, Math.Max(column - 1, 1), height));
                }
            }

            // Coverage and records are separate facts: where the capture did not cover its columns they are hatched across
            // any records it did see, so they never read as quiet ones, and never erase them (R21, P1).
            foreach (Rect run in CoverageRuns(index))
            {
                TimelineView.DrawCoverageGap(context, run);
            }
        }

        DrawNotes(context, view, interval, X, current);
    }

    /// <summary>A note pinned at an instant: a flag at its place on its session's lane, and a thin line down the lane.</summary>
    private static void DrawNotes(DrawingContext context, InvestigationTimelineView view, TimeRange interval, Func<long, double> x, Ink ink)
    {
        foreach (InvestigationNote note in view.Notes)
        {
            if (note is not { Lane: { } lane, Ticks: { } ticks } || ticks < interval.StartTicks || ticks > interval.EndTicks)
            {
                continue;
            }

            double at = x(ticks);
            double top = AxisHeight + (lane * LaneHeight);
            var flag = new StreamGeometry();
            using (StreamGeometryContext figure = flag.Open())
            {
                figure.BeginFigure(new Point(at - 5, top + 2), true);
                figure.LineTo(new Point(at + 5, top + 2));
                figure.LineTo(new Point(at, top + 10));
                figure.EndFigure(true);
            }

            context.DrawGeometry(ink.Text, null, flag);
            context.DrawLine(ink.NoteLine, new Point(at, top + 10), new Point(at, top + LaneHeight - 4));
        }
    }

    /// <summary>An interval <paramref name="factor"/> times as long as <paramref name="interval"/>, centred where it was asked.</summary>
    internal static TimeRange Zoomed(TimeRange interval, long around, decimal factor)
    {
        long span = Math.Max((long)((interval.EndTicks - interval.StartTicks) * factor), 100);
        long start = around - (long)((around - interval.StartTicks) * factor);
        return new TimeRange(start, checked(start + span));
    }

    private void ZoomAround(TimelineColumn at, decimal factor)
    {
        if (view?.Interval is not { } interval) return;
        TimeRange column = view.Lanes[at.Lane].Buckets[at.Column].Interval;
        ZoomRequested?.Invoke(this, Zoomed(interval, column.StartTicks + ((column.EndTicks - column.StartTicks) / 2), factor));
    }

    private TimelineColumn? ColumnAt(Point point)
    {
        if (view?.Interval is null || point.X < LabelWidth || point.Y < AxisHeight) return null;
        int lane = (int)((point.Y - AxisHeight) / LaneHeight);
        if (!Placed(lane)) return null;
        double width = Math.Max(Bounds.Width - LabelWidth - 12, 10);
        int columns = view.Lanes[lane].Buckets.Count;
        int column = (int)((point.X - LabelWidth) / width * columns);
        return column >= 0 && column < columns ? new TimelineColumn(lane, column) : null;
    }

    private bool Placed(int lane) => view is not null && lane >= 0 && lane < view.Lanes.Count && view.Lanes[lane].Placed;

    private int? FirstPlaced() => view is null ? null : Enumerable.Range(0, view.Lanes.Count).Cast<int?>().FirstOrDefault(lane => Placed(lane!.Value));

    private int? NextPlaced(int from, int step)
    {
        for (int lane = from + step; view is not null && lane >= 0 && lane < view.Lanes.Count; lane += step)
        {
            if (Placed(lane)) return lane;
        }

        return null;
    }

    private int Busiest(int lane)
    {
        IReadOnlyList<TimelineBucket> buckets = view!.Lanes[lane].Buckets;
        int best = 0;
        for (int index = 1; index < buckets.Count; index++)
        {
            if (buckets[index].ObservationCount > buckets[best].ObservationCount) best = index;
        }

        return best;
    }

    private string Spoken(TimelineColumn? at) => at is null
        ? "The investigation's timeline: no session is placed on it"
        : "The investigation's timeline. " + Describe() + " Arrow keys choose a column, plus and minus zoom, 0 shows it all.";

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
        public Pen Cursor { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink)), 1.5);
        public Pen NoteLine { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink)), 1, new DashStyle([2, 2], 0));
    }

    private static void Text(DrawingContext context, string text, double size, IBrush brush, Point at, double maximumWidth) =>
        context.DrawText(Formatted(text, size, brush, maximumWidth), at);

    private static FormattedText Formatted(string text, double size, IBrush brush, double maximumWidth) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(maximumWidth, 10),
        };

    /// <summary>
    /// A lane's label as drawn, kept to its lane: a reason too long for it - why a session has no place - ends in an
    /// ellipsis on the lane's last line rather than running into the next lane, and the words beside the chart say it whole.
    /// </summary>
    internal FormattedText LaneLabel(int index, IBrush brush)
    {
        FormattedText label = Formatted(index < labels.Count ? labels[index] : string.Empty, 11, brush, LabelWidth - 18);
        label.MaxTextHeight = LaneHeight - 8;
        label.Trimming = TextTrimming.CharacterEllipsis;
        return label;
    }

    private const double AxisFontSize = 10;

    /// <summary>The most ticks the axis is offered between its ends before their labels are measured.</summary>
    private const int MostAxisTicks = 10;

    /// <summary>The least room kept clear between two of the axis's labels.</summary>
    private const double AxisLabelGap = 8;

    private double PlotWidth => Math.Max(Bounds.Width - LabelWidth - 12, 10);

    /// <summary>
    /// The axis's labels where they are drawn, each with the instant it names, in the investigation's time - its reference
    /// session's clock. The interval's two ends are labelled as the session's timeline labels its own, in the unit and digits
    /// its span needs, the first just after its rule and the last just before it. Between them are ticks on round instants
    /// of §6.2's 1-2-5 ladder, each named to exactly the digits its width needs and centred on the instant it names: the
    /// narrowest width whose labels, measured, keep clear of one another, and of them only those clear of both ends. So no
    /// label names an instant other than its tick's, and no two run together however narrow the chart or long the
    /// precision. An axis too short for even one label has none.
    /// </summary>
    internal IReadOnlyList<(string Label, Rect Where, long Ticks)> AxisLabels()
    {
        if (view?.Interval is not { } interval)
        {
            return [];
        }

        long span = interval.EndTicks - interval.StartTicks;
        double right = LabelWidth + PlotWidth - 3;
        (string Label, Rect Where, long Ticks) Place(long at, string label)
        {
            FormattedText formatted = Formatted(label, AxisFontSize, Brushes.Black, double.PositiveInfinity);
            double x = LabelWidth + (PlotWidth * (at - interval.StartTicks) / span);
            double from = at == interval.StartTicks ? x + 3 : at == interval.EndTicks ? x - 3 - formatted.Width : x - (formatted.Width / 2);
            return (label, new Rect(from, 6, formatted.Width, formatted.Height), at);
        }

        (string Label, Rect Where, long Ticks) End(long at) => Place(at, WorkspaceTime.FormatInstant(at, span, CultureInfo.CurrentCulture));
        (string Label, Rect Where, long Ticks) start = End(interval.StartTicks);
        if (start.Where.Right > right)
        {
            return [];
        }

        (string Label, Rect Where, long Ticks) last = End(interval.EndTicks);
        bool lastFits = last.Where.Left >= start.Where.Right + AxisLabelGap;
        var placed = new List<(string Label, Rect Where, long Ticks)> { start };
        for (long width = WorkspaceTime.LadderWidth(Math.Max(span / MostAxisTicks, 1)); width < span; width = WorkspaceTime.LadderWidth(width + 1))
        {
            // Every round instant strictly inside the interval, named to the width's digits; a width is taken once no two
            // of its labels run together.
            var ticks = new List<(string Label, Rect Where, long Ticks)>();
            for (long at = (Math.DivRem(interval.StartTicks, width, out long past) + (past > 0 ? 1 : 0)) * width; at < interval.EndTicks; at += width)
            {
                if (at > interval.StartTicks)
                {
                    ticks.Add(Place(at, WorkspaceTime.FormatTick(at, width, span, CultureInfo.CurrentCulture)));
                }
            }

            if (ticks.Zip(ticks.Skip(1)).All(pair => pair.Second.Where.Left >= pair.First.Where.Right + AxisLabelGap))
            {
                double before = start.Where.Right + AxisLabelGap;
                double after = (lastFits ? last.Where.Left : right + 3) - AxisLabelGap;
                placed.AddRange(ticks.Where(tick => tick.Where.Left >= before && tick.Where.Right <= after));
                break;
            }
        }

        if (lastFits)
        {
            placed.Add(last);
        }

        return placed;
    }
}
