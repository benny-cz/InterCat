using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

public sealed class TimelineView : Control, IHoverCardSource
{
    private const ThemeMode Mode = ThemeMode.Dark;

    private static readonly IBrush GapBrush = Token(ThemePalette.TokensFor(Mode, MechanismFamily.RemoteCall).Ink);
    private static readonly IBrush SelectedBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly IBrush GridBrush = Token(ThemePalette.Surfaces(Mode).Elevated);
    private static readonly IBrush TextBrush = Token(ThemePalette.Surfaces(Mode).MutedInk);
    private static readonly SolidColorBrush DimBrush = Token(ThemePalette.Surfaces(Mode).Plot);

    /// <summary>A focused rung's rest of the machine: muted ink, so no bar of it is read as a mechanism's hue (§6.6).</summary>
    private static readonly SolidColorBrush ContextBarBrush =
        new(ThemeResources.ToColor(ThemePalette.Surfaces(Mode).MutedInk), 0.4);

    private static SolidColorBrush Token(Srgb value) => new SolidColorBrush(ThemeResources.ToColor(value));

    /// <summary>A press that moves at least this far brushes a range; a shorter one selects the bucket under it.</summary>
    private const double BrushThreshold = 4;

    /// <summary>The plot's margins: the axis label gutter on the left and a little air on the right.</summary>
    private const double AggregatePlotLeft = 38;
    private const double LanePlotLeft = 118;
    private const double ProcessPlotLeft = 170;
    private const double PlotRightMargin = 14;
    private const double PlotTop = 24;
    private const double PlotBottomMargin = 30;
    private const double MinimumLaneHeight = 30;

    /// <summary>The narrowest viewport a zoom may reach: 10 ms of presentation ticks, or the extent when shorter.</summary>
    private const long MinimumSpanTicks = 100_000;

    /// <summary>§6.7 and §26.2: a double click zooms in by this factor around the pointer.</summary>
    private const decimal DoubleClickZoom = 2.0m;

    /// <summary>How long the viewport must rest before the timeline asks for its own resolution (§6.2, P25).</summary>
    private static readonly TimeSpan DetailSettle = TimeSpan.FromMilliseconds(150);

    private static readonly Pen HoverPen = new(Token(ThemePalette.Surfaces(Mode).Ink), 1.5);

    // Hover (§6.2): the tick under the pointer and where the pointer is. The card is described from the bucket drawn there
    // at each frame, so it follows a count that arrives while the pointer rests. Hover never selects or brushes.
    private long? hoverTick;
    private Point hoverPoint;
    private double peakRate;

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
        DoubleTapped += ZoomAtDoubleClick;
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(Gestures.PinchEvent, ZoomByPinch);
        AddHandler(Gestures.PinchEndedEvent, (_, _) => pinchStart = null);
    }

    // The viewport a pinch began from: every scale of one gesture is applied to it, so a long pinch accumulates no drift.
    private TimeRange? pinchStart;

    /// <summary>
    /// §6.7: a pinch zooms against the viewport it began from, around its current focal point, by the gesture's scale.
    /// </summary>
    private void ZoomByPinch(object? sender, PinchEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel || e.Scale <= 0 || !double.IsFinite(e.Scale))
        {
            return;
        }

        // The recognizer states the gesture's centre in this control's own coordinates.
        pinchStart ??= Viewport;
        double focus = Math.Clamp(e.ScaleOrigin.X - PlotLeft, 0, PlotWidth);
        SetViewport(ViewportMath.ZoomAtPixel(pinchStart.Value, focus, PlotWidth, (decimal)Math.Clamp(e.Scale, 0.01, 100),
            viewModel.Snapshot.Extent, MinimumSpanTicks));
        e.Handled = true;
    }

    /// <summary>Raised whenever the visible range changes, by a gesture, a key, or <see cref="SetViewport"/>.</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>The visible time range: the retained extent until the user zooms or pans (presentation only, §6.4).</summary>
    public TimeRange Viewport => viewport ?? Extent;

    /// <summary>Whether the whole retained extent is shown, following it as a live session grows.</summary>
    public bool IsFit => viewport is null;

    private TimeRange Extent => (DataContext as WorkspaceViewModel)?.Snapshot.Extent ?? new TimeRange(0, 1);

    private bool ShowingMechanismLanes => DataContext is WorkspaceViewModel { ShowsMechanismLanes: true };

    private IReadOnlyList<ProcessTimelineLane>? ProcessLanesForViewport =>
        DataContext is WorkspaceViewModel { ShowsProcessLanes: true } viewModel
        && viewModel.ProcessLaneDisplay is { Count: > 0 } lanes
        && lanes.All(lane => lane.Buckets.Count > 0
            && lane.Buckets[0].Interval.StartTicks <= Viewport.StartTicks
            && lane.Buckets[^1].Interval.EndTicks >= Viewport.EndTicks)
            ? lanes : null;

    private IReadOnlyList<TimelineBucket>? ProcessContextBuckets
    {
        get
        {
            if (ProcessLanesForViewport is not { } lanes || DataContext is not WorkspaceViewModel viewModel)
                return null;
            IReadOnlyList<TimelineBucket> first = lanes[0].Buckets;
            if (viewModel.TimelineDetail is { } detail && MatchingIntervals(first, detail.Buckets))
                return detail.Buckets;
            return MatchingIntervals(first, viewModel.Snapshot.Timeline) ? viewModel.Snapshot.Timeline : null;
        }
    }

    private bool ShowingProcessLanes => ProcessContextBuckets is not null;

    private bool ShowingLanes => ShowingMechanismLanes || ShowingProcessLanes;

    private int LaneCount => ShowingMechanismLanes
        ? ((WorkspaceViewModel)DataContext!).Snapshot.MechanismLanes.Count
        : ShowingProcessLanes ? ProcessLanesForViewport!.Count + 1 : 0;

    private static bool MatchingIntervals(IReadOnlyList<TimelineBucket> left, IReadOnlyList<TimelineBucket> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Interval != right[index].Interval) return false;
        }

        return true;
    }

    private double PlotLeft => ShowingMechanismLanes ? LanePlotLeft
        : ShowingProcessLanes ? ProcessPlotLeft : AggregatePlotLeft;

    private double PlotWidth => Math.Max(1, Bounds.Width - PlotLeft - PlotRightMargin);

    /// <summary>The plotted span in control coordinates, shared with precise headless gesture checks.</summary>
    internal double PlotStart => PlotLeft;
    internal double PlotSpan => PlotWidth;

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
        RefreshLaneLayout();
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
        RefreshLaneLayout();
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

    /// <summary>
    /// Keep every L0 or L1 row addressable. A long list grows inside the pane's vertical ScrollViewer instead of
    /// compressing records into unpointable slivers; the aggregate fallback keeps its ordinary stretch height.
    /// </summary>
    internal void RefreshLaneLayout()
    {
        double minimum = LaneCount > 0
            ? PlotTop + PlotBottomMargin + (LaneCount * MinimumLaneHeight)
            : 0;
        if (Math.Abs(MinHeight - minimum) < 0.1) return;
        MinHeight = minimum;
        InvalidateMeasure();
    }

    /// <summary>A ranked-table or graph selection should reveal its L1 row, not merely outline it off-screen.</summary>
    internal void BringSelectedProcessLaneIntoView()
    {
        if (!ShowingProcessLanes || DataContext is not WorkspaceViewModel { SelectedProcess: { } selected }
            || ProcessLanesForViewport is not { } lanes) return;
        int index = lanes.ToList().FindIndex(lane => lane.ProcessId == selected.Id);
        ScrollViewer? scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (index < 0 || scroller is null) return;
        double bottom = Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin);
        Rect row = LaneRow(index + 1, lanes.Count + 1, PlotTop, bottom);
        double offset = scroller.Offset.Y;
        if (row.Top < offset + 6) offset = row.Top - 6;
        else if (row.Bottom > offset + scroller.Viewport.Height - 6)
            offset = row.Bottom - scroller.Viewport.Height + 6;
        else return;
        scroller.Offset = new Vector(scroller.Offset.X,
            Math.Clamp(offset, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        // An empty interval is still a selectable answer. Paint a transparent hit surface so the scroller does not
        // take clicks that land between the lane's bars or over a genuinely empty column.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        TimeRange visible = Viewport;
        double left = PlotLeft;
        double right = Math.Max(left + 1, Bounds.Width - PlotRightMargin);
        double top = PlotTop;
        double bottom = Math.Max(top + 1, Bounds.Height - PlotBottomMargin);
        double plotWidth = right - left;
        double plotHeight = bottom - top;
        context.DrawLine(new Pen(GridBrush, 1), new(left, bottom), new(right, bottom));
        for (int line = 1; !ShowingLanes && line <= 3; line++)
        {
            double y = top + (plotHeight * line / 4);
            context.DrawLine(new Pen(GridBrush, 0.7), new(left, y), new(right, y));
        }

        // The overview's coarse buckets stand wherever the viewport's own count has not arrived; the detail replaces
        // them over the interval it answers. Heights are rates, records per tick, so a coarse bucket and a fine one
        // side by side are drawn on one honest scale rather than by counts over unequal widths (§6.2).
        SessionTimelineDetail? detail = viewModel.TimelineDetail is { } answered && Intersects(answered.Interval, visible)
            ? answered : null;
        if (ShowingMechanismLanes && detail is not null
            && (detail.MechanismLanes.Count != viewModel.Snapshot.MechanismLanes.Count
                || viewModel.Snapshot.MechanismLanes.Any(lane =>
                    !detail.MechanismLanes.Any(candidate => candidate.Mechanism == lane.Mechanism))))
        {
            // A carried or older detail with only some lanes cannot replace an interval for every row or share an
            // honest peak scale with the remaining coarse rows. Keep the complete overview until detail catches up.
            detail = null;
        }
        TimelineBucket[] coarse = [.. viewModel.Snapshot.Timeline
            .Where(bucket => Intersects(bucket.Interval, visible)
                && (detail is null || !Inside(bucket.Interval, detail.Interval)))];
        TimelineBucket[] fine = detail is null ? [] : [.. detail.Buckets.Where(bucket => Intersects(bucket.Interval, visible))];
        double maximumRate = ShowingMechanismLanes
            ? viewModel.Snapshot.MechanismLanes.SelectMany(lane => lane.Buckets)
                .Where(bucket => Intersects(bucket.Interval, visible)
                    && (detail is null || !Inside(bucket.Interval, detail.Interval)))
                .Concat(detail?.MechanismLanes.SelectMany(lane => lane.Buckets)
                    .Where(bucket => Intersects(bucket.Interval, visible)) ?? [])
                .Select(Rate).DefaultIfEmpty(0).Max()
            : ShowingProcessLanes
                ? ProcessContextBuckets!.Concat(ProcessLanesForViewport!.SelectMany(lane => lane.Buckets))
                    .Where(bucket => Intersects(bucket.Interval, visible))
                    .Select(Rate).DefaultIfEmpty(0).Max()
            : coarse.Concat(fine).Select(Rate).DefaultIfEmpty(0).Max();
        peakRate = maximumRate;

        // A focused rung draws every record as grey context and its own records in their mechanism's hue on the same
        // rate scale, inside the grey bar of the same interval: when the focus was active, against the machine (§3.2).
        bool focused = viewModel.TimelineShowsFocus;
        var scale = new BarScale(visible, left, plotWidth, top, bottom, maximumRate, focused);
        if (ShowingMechanismLanes)
        {
            DrawMechanismLanes(context, viewModel, detail, scale);
            if (detail is not null && detail.Generation != viewModel.DisplayedGeneration)
            {
                string note = $"zoomed detail from generation {detail.Generation:N0}";
                DrawText(context, note, new(right - (5.6 * note.Length), top - 18));
            }
        }
        else if (ShowingProcessLanes)
        {
            DrawProcessLanes(context, viewModel, ProcessContextBuckets!, ProcessLanesForViewport!, scale);
            if (detail is not null && detail.Generation != viewModel.DisplayedGeneration)
            {
                string note = $"zoomed detail from generation {detail.Generation:N0}";
                DrawText(context, note, new(right - (5.6 * note.Length), top - 18));
            }
        }
        else if (detail is null)
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

        if (!ShowingProcessLanes && focused && viewModel.TimelineFocusBuckets is { } focus)
        {
            DrawFocus(context, focus.Where(bucket => Intersects(bucket.Interval, visible)), scale);
        }

        DrawSelection(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawEvidenceMarks(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawText(context, WorkspaceTime.FormatInstant(visible.StartTicks, visible.SpanTicks, CultureInfo.CurrentCulture), new(left, bottom + 7));
        string end = WorkspaceTime.FormatInstant(visible.EndTicks, visible.SpanTicks, CultureInfo.CurrentCulture);
        DrawText(context, end, new(right - (6.5 * end.Length), bottom + 7));
        DrawText(context, RateText(maximumRate * WorkspaceTime.TicksPerSecond), new(4, top - 4));

        if (HoveredBucket is { } hovered)
        {
            // The hovered bucket is outlined in ink over its whole column, lighter than the selection's accent.
            double x1 = scale.X(Math.Max(hovered.Interval.StartTicks, visible.StartTicks));
            double x2 = scale.X(Math.Min(hovered.Interval.EndTicks, visible.EndTicks));
            Rect row = HoveredLaneIndex is { } index
                ? LaneRow(index, LaneCount, top, bottom)
                : new Rect(left, top, plotWidth, bottom - top);
            using (context.PushOpacity(0.5))
            {
                context.DrawRectangle(Brushes.Transparent, HoverPen,
                    new Rect(x1 - 1, row.Top, Math.Max(2, x2 - x1), row.Height));
            }
        }
    }

    /// <summary>The bucket drawn under the pointer, if it rests on the plot; hover never changes selection (§6.4).</summary>
    internal TimelineBucket? HoveredBucket
    {
        get
        {
            if (hoverTick is not { } tick || DataContext is not WorkspaceViewModel viewModel)
            {
                return null;
            }

            if (HoveredLaneIndex is not { } index)
            {
                return ShowingLanes ? null : BucketAt(viewModel, tick);
            }

            if (ShowingProcessLanes)
            {
                IReadOnlyList<TimelineBucket> buckets = index == 0
                    ? ProcessContextBuckets!
                    : ProcessLanesForViewport![index - 1].Buckets;
                return buckets.FirstOrDefault(bucket => bucket.Interval.Contains(tick));
            }

            Mechanism mechanism = viewModel.Snapshot.MechanismLanes[index].Mechanism;
            IReadOnlyList<MechanismTimelineLane> lanes = viewModel.TimelineDetail is { } detail
                && detail.Interval.Contains(tick)
                && detail.MechanismLanes.Count == viewModel.Snapshot.MechanismLanes.Count
                && viewModel.Snapshot.MechanismLanes.All(lane =>
                    detail.MechanismLanes.Any(candidate => candidate.Mechanism == lane.Mechanism))
                    ? detail.MechanismLanes : viewModel.Snapshot.MechanismLanes;
            return lanes.FirstOrDefault(lane => lane.Mechanism == mechanism)?.Buckets
                .FirstOrDefault(bucket => bucket.Interval.Contains(tick));
        }
    }

    private int? HoveredLaneIndex => hoverTick is not null ? LaneIndexAt(hoverPoint.Y) : null;

    private int? LaneIndexAt(double y)
    {
        if (!ShowingLanes || DataContext is not WorkspaceViewModel viewModel) return null;
        int count = LaneCount;
        double bottom = Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin);
        if (count == 0 || y < PlotTop || y >= bottom) return null;
        return Math.Clamp((int)((y - PlotTop) * count / (bottom - PlotTop)), 0, count - 1);
    }

    /// <summary>The card the hovered bucket draws, as the view model describes it against the visible peak.</summary>
    public HoverCard? HoverCard
    {
        get
        {
            if (HoveredBucket is not { } bucket || DataContext is not WorkspaceViewModel viewModel)
                return null;
            int? index = HoveredLaneIndex;
            Mechanism? mechanism = ShowingMechanismLanes && index is { } mechanismIndex
                ? viewModel.Snapshot.MechanismLanes[mechanismIndex].Mechanism : null;
            ProcessNode? owner = ShowingProcessLanes && index is > 0
                ? viewModel.Snapshot.Processes.FirstOrDefault(process =>
                    process.Id == ProcessLanesForViewport![index.Value - 1].ProcessId)
                : null;
            return viewModel.DescribeTimelineHover(bucket, peakRate * WorkspaceTime.TicksPerSecond, mechanism, owner);
        }
    }

    /// <inheritdoc />
    public Point HoverPoint => hoverPoint;

    /// <inheritdoc />
    public event EventHandler? HoverChanged;

    /// <summary>Where on the plot a bucket's column is, in this control's coordinates, for pointing at it.</summary>
    internal Point? PointOf(TimelineBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        TimeRange visible = Viewport;
        if (!Intersects(bucket.Interval, visible)) return null;
        long middle = bucket.Interval.StartTicks + (bucket.Interval.SpanTicks / 2);
        int index = ShowingMechanismLanes && DataContext is WorkspaceViewModel viewModel
            ? viewModel.Snapshot.MechanismLanes.ToList().FindIndex(lane => lane.Buckets.Contains(bucket)
                || viewModel.TimelineDetail?.MechanismLanes.Any(detailLane => detailLane.Mechanism == lane.Mechanism
                    && detailLane.Buckets.Contains(bucket)) == true)
            : -1;
        if (ShowingProcessLanes && ProcessLanesForViewport is { } processes)
        {
            int processIndex = processes.ToList().FindIndex(lane =>
                lane.Buckets.Any(candidate => ReferenceEquals(candidate, bucket)));
            if (processIndex >= 0) index = processIndex + 1;
            else if (ProcessContextBuckets!.Any(candidate => ReferenceEquals(candidate, bucket))) index = 0;
        }

        double y = index >= 0
            ? LaneRow(index, LaneCount, PlotTop,
                Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin)).Center.Y
            : Math.Max(PlotTop, Bounds.Height - 40);
        return new(PlotLeft + ViewportMath.PixelAtTick(visible, Math.Clamp(middle, visible.StartTicks, visible.EndTicks - 1), PlotWidth),
            y);
    }

    /// <summary>A process ID disambiguates two owner rows whose bucket values and intervals happen to match.</summary>
    internal Point? PointOf(ProcessInstanceId processId, TimelineBucket bucket)
    {
        if (!ShowingProcessLanes || ProcessLanesForViewport is not { } lanes) return null;
        int index = lanes.ToList().FindIndex(lane => lane.ProcessId == processId);
        if (index < 0 || !lanes[index].Buckets.Contains(bucket)) return null;
        TimeRange visible = Viewport;
        if (!Intersects(bucket.Interval, visible)) return null;
        long middle = bucket.Interval.StartTicks + (bucket.Interval.SpanTicks / 2);
        return new(PlotLeft + ViewportMath.PixelAtTick(visible,
                Math.Clamp(middle, visible.StartTicks, visible.EndTicks - 1), PlotWidth),
            LaneRow(index + 1, lanes.Count + 1, PlotTop,
                Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin)).Center.Y);
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
        TimeRange Visible, double Left, double PlotWidth, double Top, double Bottom, double MaximumRate, bool Focused)
    {
        public double X(long tick) => Left + ViewportMath.PixelAtTick(Visible, tick, PlotWidth);

        /// <summary>A bar for this bucket: its rate's share of the plot, never under 3 px so a lone record stays visible.</summary>
        public Rect Bar(TimelineBucket bucket)
        {
            double x1 = X(Math.Max(bucket.Interval.StartTicks, Visible.StartTicks));
            double x2 = X(Math.Min(bucket.Interval.EndTicks, Visible.EndTicks));
            double height = Math.Max(3, (Bottom - Top) * Rate(bucket) / Math.Max(double.Epsilon, MaximumRate));
            return new(x1, Bottom - height, Math.Max(1, x2 - x1 - 2), height);
        }
    }

    /// <summary>A focused rung's own records, each in its bucket's dominant mechanism's hue over the grey of all records.</summary>
    private static void DrawFocus(DrawingContext context, IEnumerable<TimelineBucket> buckets, BarScale scale)
    {
        foreach (TimelineBucket bucket in buckets)
        {
            if (bucket.ObservationCount > 0)
            {
                context.DrawRectangle(BrushFor(bucket.DominantMechanism), null, scale.Bar(bucket));
            }
        }
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
                Rect rectangle = scale.Bar(bucket);
                context.DrawRectangle(scale.Focused ? ContextBarBrush : BrushFor(bucket.DominantMechanism), null, rectangle);
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

    private static Rect LaneRow(int index, int count, double top, double bottom)
    {
        double height = (bottom - top) / Math.Max(1, count);
        return new Rect(0, top + (index * height), 1, height);
    }

    /// <summary>One L0 row per observed mechanism, all on the same visible rate scale and time columns.</summary>
    private static void DrawMechanismLanes(
        DrawingContext context, WorkspaceViewModel viewModel, SessionTimelineDetail? detail, BarScale scale)
    {
        IReadOnlyList<MechanismTimelineLane> lanes = viewModel.Snapshot.MechanismLanes;
        for (int index = 0; index < lanes.Count; index++)
        {
            MechanismTimelineLane lane = lanes[index];
            Rect row = LaneRow(index, lanes.Count, scale.Top, scale.Bottom);
            double baseline = row.Bottom - 5;
            context.DrawLine(new Pen(GridBrush, 0.7), new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            if (viewModel.SelectedTimelineMechanism == lane.Mechanism)
            {
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 1),
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }
            context.DrawRectangle(BrushFor(lane.Mechanism), null, new Rect(8, row.Center.Y - 3, 6, 6));
            DrawText(context, EvidenceRowText.MechanismName(lane.Mechanism), new(19, row.Center.Y - 7));
            var laneScale = new BarScale(scale.Visible, scale.Left, scale.PlotWidth,
                row.Top + 3, baseline, scale.MaximumRate, Focused: false);

            TimelineBucket[] coarse = [.. lane.Buckets.Where(bucket => Intersects(bucket.Interval, scale.Visible))];
            MechanismTimelineLane? detailLane = detail?.MechanismLanes
                .FirstOrDefault(candidate => candidate.Mechanism == lane.Mechanism);
            if (detail is null || detailLane is null)
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, coarse, laneScale, row);
                continue;
            }

            double x1 = laneScale.X(Math.Max(detail.Interval.StartTicks, scale.Visible.StartTicks));
            double x2 = laneScale.X(Math.Min(detail.Interval.EndTicks, scale.Visible.EndTicks));
            using (context.PushClip(new Rect(scale.Left, row.Top, Math.Max(0, x1 - scale.Left), row.Height)))
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, coarse, laneScale, row);
            }

            using (context.PushClip(new Rect(x2, row.Top, Math.Max(0, scale.Left + scale.PlotWidth - x2), row.Height)))
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, coarse, laneScale, row);
            }

            using (context.PushClip(new Rect(x1, row.Top, Math.Max(0, x2 - x1), row.Height)))
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism,
                    detailLane.Buckets.Where(bucket => Intersects(bucket.Interval, scale.Visible)), laneScale, row);
            }
        }
    }

    /// <summary>L1's exact owner rows, with one non-additive machine context row above them.</summary>
    private static void DrawProcessLanes(DrawingContext context, WorkspaceViewModel viewModel,
        IReadOnlyList<TimelineBucket> machine, IReadOnlyList<ProcessTimelineLane> lanes, BarScale scale)
    {
        int count = lanes.Count + 1;
        for (int index = 0; index < count; index++)
        {
            Rect row = LaneRow(index, count, scale.Top, scale.Bottom);
            context.DrawLine(new Pen(GridBrush, 0.7), new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            var rowScale = new BarScale(scale.Visible, scale.Left, scale.PlotWidth,
                row.Top + 3, row.Bottom - 5, scale.MaximumRate, Focused: false);
            if (index == 0)
            {
                DrawText(context, "Machine · all records", new(9, row.Center.Y - 7));
                DrawLaneSeries(context, viewModel, null,
                    machine.Where(bucket => Intersects(bucket.Interval, scale.Visible)), rowScale, row, contextRow: true);
                continue;
            }

            ProcessTimelineLane lane = lanes[index - 1];
            ProcessNode? process = viewModel.Snapshot.Processes.FirstOrDefault(node => node.Id == lane.ProcessId);
            string name = process?.Name ?? "Process";
            string shortName = name.Length > 14 ? name[..13] + "…" : name;
            string label = process is null ? $"{shortName} · {lane.ProcessId.Value.ToString("N")[..6]}"
                : $"{shortName} · PID {process.ProcessId}";
            if (viewModel.SelectedProcess?.Id == lane.ProcessId)
            {
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 1),
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }

            DrawText(context, label, new(9, row.Center.Y - 7));
            DrawLaneSeries(context, viewModel, null,
                lane.Buckets.Where(bucket => Intersects(bucket.Interval, scale.Visible)), rowScale, row);
        }
    }

    private static void DrawLaneSeries(DrawingContext context, WorkspaceViewModel viewModel,
        Mechanism? mechanism, IEnumerable<TimelineBucket> buckets, BarScale scale, Rect row,
        bool contextRow = false)
    {
        foreach (TimelineBucket bucket in buckets)
        {
            double x1 = scale.X(Math.Max(bucket.Interval.StartTicks, scale.Visible.StartTicks));
            double x2 = scale.X(Math.Min(bucket.Interval.EndTicks, scale.Visible.EndTicks));
            double width = Math.Max(1, x2 - x1 - 2);
            if (bucket.ObservationCount > 0)
            {
                Rect measured = scale.Bar(bucket);
                double height = Math.Max(measured.Height, (scale.Bottom - scale.Top) * 0.42);
                Rect bar = new(measured.X, scale.Bottom - height, measured.Width, height);
                context.DrawRectangle(contextRow ? ContextBarBrush : BrushFor(mechanism ?? bucket.DominantMechanism),
                    null, bar);
                if (viewModel.SelectedInterval == bucket.Interval)
                {
                    context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 2), bar.Inflate(1));
                }
            }

            if (bucket.Coverage != CoverageState.Covered)
            {
                // Unknown live coverage is a thin hatched strip, not a claimed loss spanning every observed bar.
                // A measured defect remains hatched through the row; neither changes the observed count.
                Rect coverage = bucket.Coverage == CoverageState.UnknownCoverage
                    ? new Rect(x1, row.Bottom - 5, width, 5)
                    : new Rect(x1, row.Top, width, row.Height);
                DrawCoverageGap(context, coverage);
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

        if (ShowingLanes && e.GetPosition(this).X < PlotLeft)
        {
            // The surrounding ScrollViewer owns vertical lane scrolling over the lane-name gutter (§6.2).
            return;
        }

        // A horizontal wheel, a two-finger horizontal scroll or Shift with the wheel pans a tenth of the span per notch;
        // the vertical wheel zooms at the pointer (§6.7).
        bool horizontal = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        if (horizontal || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            double notches = horizontal ? -e.Delta.X : -e.Delta.Y;
            if (notches != 0)
            {
                SetViewport(ViewportMath.PanByFraction(Viewport, (decimal)Math.Clamp(notches, -10, 10) * 0.1m,
                    viewModel.Snapshot.Extent));
            }

            e.Handled = true;
            return;
        }

        if (e.Delta.Y == 0)
        {
            return;
        }

        double pixel = Math.Clamp(e.GetPosition(this).X - PlotLeft, 0, PlotWidth);
        decimal factor = e.Delta.Y > 0 ? 1.25m : 0.8m;
        SetViewport(ViewportMath.ZoomAtPixel(Viewport, pixel, PlotWidth, factor, viewModel.Snapshot.Extent, MinimumSpanTicks));
        e.Handled = true;
    }

    /// <summary>§6.7: a double click zooms in by 2 around the pointer, keeping the instant under it where it is.</summary>
    private void ZoomAtDoubleClick(object? sender, TappedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (!OnPlot(position))
        {
            return;
        }

        SetViewport(ViewportMath.ZoomAtPixel(Viewport, position.X - PlotLeft, PlotWidth, DoubleClickZoom,
            viewModel.Snapshot.Extent, MinimumSpanTicks));
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
        if (ShowingLanes && point.Properties.IsLeftButtonPressed && point.Position.X < PlotLeft
            && LaneIndexAt(point.Position.Y) is { } laneIndex)
        {
            if (ShowingMechanismLanes)
            {
                viewModel.SelectTimelineLane(viewModel.Snapshot.MechanismLanes[laneIndex].Mechanism);
            }
            else if (laneIndex == 0)
            {
                viewModel.ClearProcessLaneFocus();
            }
            else if (laneIndex > 0 && ProcessLanesForViewport is { } lanes)
            {
                ProcessInstanceId processId = lanes[laneIndex - 1].ProcessId;
                viewModel.SelectedRung = viewModel.RungRows.FirstOrDefault(row => row.Key == processId.ToString());
                if (viewModel.SelectedRung is null)
                {
                    viewModel.SelectedProcess = viewModel.Snapshot.Processes.FirstOrDefault(node => node.Id == processId);
                }
            }
            e.Handled = true;
            return;
        }
        if (!OnPlot(point.Position)) return;
        if (hoverTick is not null)
        {
            // A press begins a gesture; its card would describe a bucket the gesture is about to change.
            hoverTick = null;
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }

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
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        if (brushAnchor is null)
        {
            // No press: the pointer only explains the bucket under it, and the card follows it across the plot.
            Point position = e.GetPosition(this);
            long? tick = OnPlot(position) && (!ShowingLanes || LaneIndexAt(position.Y) is not null)
                ? TickAt(position.X) : null;
            if (tick != hoverTick || (tick is not null && position != hoverPoint))
            {
                hoverTick = tick;
                hoverPoint = position;
                InvalidateVisual();
                HoverChanged?.Invoke(this, EventArgs.Empty);
            }

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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hoverTick is not null)
        {
            hoverTick = null;
            InvalidateVisual();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether a point lies on the plot, between the axis gutter, the right margin, the rate label and the axis.</summary>
    private bool OnPlot(Point point) =>
        point.X >= PlotLeft && point.X <= PlotLeft + PlotWidth && point.Y >= PlotTop
        && point.Y <= Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin);

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
        if (Navigate(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    /// <summary>
    /// Shared timeline/minimap keyboard navigation over one viewport and one retained extent (§6.7): arrows pan by a
    /// tenth of the span, or with Shift by one drawn bucket; + and - zoom around the analysis interval, else the
    /// viewport's centre; Home and End go to the extent's edges; 0 fits the analysis scope; [ and ] step to the previous
    /// or next record.
    /// </summary>
    internal bool Navigate(Key key, KeyModifiers modifiers = KeyModifiers.None)
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
            case Key.Up:
            case Key.Down:
            case Key.PageUp:
            case Key.PageDown:
                if (!ShowingLanes) return false;
                ScrollViewer? scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (scroller is null) return false;
                double distance = key is Key.PageUp or Key.PageDown
                    ? Math.Max(MinimumLaneHeight, scroller.Viewport.Height - MinimumLaneHeight)
                    : (Bounds.Height - PlotTop - PlotBottomMargin)
                        / Math.Max(1, LaneCount);
                double direction = key is Key.Up or Key.PageUp ? -1 : 1;
                double limit = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                scroller.Offset = new Vector(scroller.Offset.X,
                    Math.Clamp(scroller.Offset.Y + (direction * distance), 0, limit));
                return true;
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
            case Key.Right:
                next = modifiers.HasFlag(KeyModifiers.Shift)
                    ? PanByTicks(current, (key == Key.Left ? -1 : 1) * CellSpan(viewModel, current), extent)
                    : ViewportMath.PanByFraction(current, key == Key.Left ? -0.1m : 0.1m, extent);
                break;
            case Key.Add:
            case Key.OemPlus:
                next = ZoomAroundSelection(viewModel, current, 1.25m);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                next = ZoomAroundSelection(viewModel, current, 0.8m);
                break;
            case Key.OemOpenBrackets:
            case Key.OemCloseBrackets:
                return Step(viewModel, key == Key.OemCloseBrackets ? 1 : -1);
            default:
                return false;
        }

        SetViewport(next);
        return true;
    }

    /// <summary>
    /// Zooms around the analysis interval when there is one, so the range being studied stays where it is on screen,
    /// else around the viewport's centre (§6.7). An interval out of view is brought to the centre first.
    /// </summary>
    private TimeRange ZoomAroundSelection(WorkspaceViewModel viewModel, TimeRange current, decimal factor)
    {
        TimeRange extent = viewModel.Snapshot.Extent;
        if (viewModel.SelectedInterval is not { } selected)
        {
            return ViewportMath.ZoomAtPixel(current, PlotWidth / 2, PlotWidth, factor, extent, MinimumSpanTicks);
        }

        long middle = selected.StartTicks + (selected.SpanTicks / 2);
        if (!current.Contains(middle))
        {
            current = ViewportMath.CenterOnTick(current, middle, extent);
        }

        return ViewportMath.ZoomAtPixel(current, ViewportMath.PixelAtTick(current, middle, PlotWidth), PlotWidth, factor,
            extent, MinimumSpanTicks);
    }

    /// <summary>The span of one drawn bucket: the zoomed detail's where it has arrived, else the overview's.</summary>
    private static long CellSpan(WorkspaceViewModel viewModel, TimeRange visible)
    {
        IReadOnlyList<TimelineBucket> buckets = viewModel.TimelineDetail is { } detail && Intersects(detail.Interval, visible)
            ? detail.Buckets
            : viewModel.Snapshot.Timeline;
        return Math.Max(1, buckets.Count == 0 ? visible.SpanTicks / 10 : buckets[0].Interval.SpanTicks);
    }

    private static TimeRange PanByTicks(TimeRange viewport, long ticks, TimeRange extent) =>
        new TimeRange(checked(viewport.StartTicks + ticks), checked(viewport.EndTicks + ticks)).ClampInside(extent);

    /// <summary>
    /// [ and ] (§6.7): at the evidence rung, the previous or next record, which the timeline marks; elsewhere, the
    /// previous or next drawn bucket holding a record of the rung's focus - or of the machine at a rung without one -
    /// becomes the analysis interval. At L0 a selected mechanism lane supplies those buckets; at L1 a selected
    /// process supplies its canonical-owner buckets. Otherwise the rung's focus or whole machine does. The viewport
    /// follows a step that leaves it.
    /// </summary>
    private bool Step(WorkspaceViewModel viewModel, int direction)
    {
        TimeRange visible = Viewport;
        TimeRange extent = viewModel.Snapshot.Extent;
        if (viewModel.IsEvidenceRung)
        {
            if (!viewModel.StepEvidence(direction))
            {
                return false;
            }

            if (viewModel.SelectedEvidenceTick is { } tick && !visible.Contains(tick))
            {
                SetViewport(ViewportMath.CenterOnTick(visible, tick, extent));
            }

            return true;
        }

        bool zoomed = viewModel.TimelineDetail is { } detail && Intersects(detail.Interval, visible);
        IReadOnlyList<TimelineBucket> buckets = zoomed ? viewModel.TimelineDetail!.Buckets : viewModel.Snapshot.Timeline;
        if (viewModel.ShowsMechanismLanes && viewModel.SelectedTimelineMechanism is { } mechanism)
        {
            bool complete = zoomed && viewModel.TimelineDetail!.MechanismLanes.Count == viewModel.Snapshot.MechanismLanes.Count
                && viewModel.Snapshot.MechanismLanes.All(lane => viewModel.TimelineDetail.MechanismLanes
                    .Any(candidate => candidate.Mechanism == lane.Mechanism));
            buckets = (complete ? viewModel.TimelineDetail!.MechanismLanes : viewModel.Snapshot.MechanismLanes)
                .First(lane => lane.Mechanism == mechanism).Buckets;
            zoomed = complete;
        }
        IReadOnlyList<TimelineBucket>? focus = viewModel.TimelineShowsFocus ? viewModel.TimelineFocusBuckets : null;
        if (ShowingProcessLanes && viewModel.SelectedProcess is { } selectedProcess
            && ProcessLanesForViewport?.FirstOrDefault(lane => lane.ProcessId == selectedProcess.Id) is { } ownerLane)
        {
            buckets = ownerLane.Buckets;
            focus = null; // These buckets already contain only the selected canonical owner.
            zoomed = viewModel.TimelineDetail is { } ownerDetail
                && MatchingIntervals(ownerLane.Buckets, ownerDetail.Buckets);
        }
        HashSet<TimeRange>? held = focus?.Where(bucket => bucket.ObservationCount > 0).Select(bucket => bucket.Interval).ToHashSet();
        long anchor = viewModel.SelectedInterval is { } selected
            ? (direction > 0 ? selected.EndTicks : selected.StartTicks)
            : (direction > 0 ? visible.StartTicks : visible.EndTicks);
        TimelineBucket? found = direction > 0
            ? buckets.FirstOrDefault(bucket => bucket.Interval.StartTicks >= anchor && Holds(bucket))
            : buckets.LastOrDefault(bucket => bucket.Interval.EndTicks <= anchor && Holds(bucket));
        if (found is null)
        {
            // Nothing further is drawn in a zoomed view: page it on toward the extent's edge, where its own count will
            // show the next record. At the whole extent there is nothing further.
            bool atEdge = direction > 0 ? visible.EndTicks >= extent.EndTicks : visible.StartTicks <= extent.StartTicks;
            if (!zoomed || atEdge)
            {
                return false;
            }

            SetViewport(ViewportMath.PanByFraction(visible, direction, extent));
            return true;
        }

        viewModel.SelectInterval(found.Interval);
        if (!visible.Contains(found.Interval.StartTicks) || found.Interval.EndTicks > visible.EndTicks)
        {
            SetViewport(ViewportMath.CenterOnTick(visible, found.Interval.StartTicks + (found.Interval.SpanTicks / 2), extent));
        }

        InvalidateVisual();
        return true;

        bool Holds(TimelineBucket bucket) => held is null ? bucket.ObservationCount > 0 : held.Contains(bucket.Interval);
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
