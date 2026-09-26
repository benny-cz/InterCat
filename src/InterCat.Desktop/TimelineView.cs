using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
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
    /// <summary>The mode the timeline draws in: the tokens' current one, so a change of theme reaches the canvas (§6.1).</summary>
    private static ThemeMode Mode => ThemeResources.CurrentMode;

    // Brushes are built once per theme mode and reused every frame (R11); a change of mode swaps them all at once.
    private static readonly Dictionary<ThemeMode, Ink> Inks = [];

    private static Ink Current => Inks.TryGetValue(Mode, out Ink? ink) ? ink : Inks[Mode] = new Ink(Mode);

    private static IBrush GapBrush => Current.GapBrush;
    private static IBrush SelectedBrush => Current.SelectedBrush;
    private static IBrush GridBrush => Current.GridBrush;
    private static IBrush TextBrush => Current.TextBrush;
    private static SolidColorBrush DimBrush => Current.DimBrush;

    /// <summary>A focused rung's rest of the machine: muted ink, so no bar of it is read as a mechanism's hue (§6.6).</summary>
    private static SolidColorBrush ContextBarBrush => Current.ContextBarBrush;

    private static Pen HoverPen => Current.HoverPen;

    /// <summary>The timeline's brushes in one theme mode, from its verified tokens (§6.6).</summary>
    private sealed class Ink
    {
        public Ink(ThemeMode mode)
        {
            SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
            GapBrush = Token(ThemePalette.TokensFor(mode, MechanismFamily.RemoteCall).Ink);
            SelectedBrush = Token(surfaces.Accent);
            GridBrush = Token(surfaces.Elevated);
            TextBrush = Token(surfaces.MutedInk);
            DimBrush = Token(surfaces.Plot);
            ContextBarBrush = new(ThemeResources.ToColor(surfaces.MutedInk), 0.4);
            HoverPen = new(Token(surfaces.Ink), 1.5);
        }

        public IBrush GapBrush { get; }
        public IBrush SelectedBrush { get; }
        public IBrush GridBrush { get; }
        public IBrush TextBrush { get; }
        public SolidColorBrush DimBrush { get; }
        public SolidColorBrush ContextBarBrush { get; }
        public Pen HoverPen { get; }

        private static SolidColorBrush Token(Srgb value) => new(ThemeResources.ToColor(value));
    }

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


    // Hover (§6.2): where the pointer rests while it is over this control outside a gesture, kept relative to the window.
    // What it hovers - an instant on the plot, or a live edge bin - is answered from that point on every read, against the
    // drawing as it is now, and the card from the bucket drawn there. A zoom, a pan, a resize, a lane scroll, a lane
    // change, a growing live edge, a pane moved by a banner or a newer generation under a resting pointer therefore never
    // leaves the card describing what used to be there (R13, P22). Hover never selects or brushes.
    private bool hovering;
    private Point hoverInWindow;
    private double peakRate;

    private readonly DispatcherTimer detailTimer;
    private TimeRange? viewport;
    private long? brushAnchor;
    private long? brushEnd;
    private double pressX;
    private bool brushing;
    private bool moved;
    private TimeRange panOrigin;

    /// <summary>The timeline as a screen reader meets it: its role, its keyboard path and table, and what it draws now.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "timeline",
        "Left and Right pan, with Shift by one bucket; plus and minus zoom; Home and End go to the session's edges; 0 "
        + "fits; [ and ] step to the previous or next record; Up and Down scroll lanes. T shows the interval table, which "
        + "lists what the timeline draws.",
        () => (DataContext as WorkspaceViewModel)?.TimelineCaption);

    public TimelineView()
    {
        detailTimer = new DispatcherTimer { Interval = DetailSettle };
        detailTimer.Tick += (_, _) => RequestDetailNow();
        DoubleTapped += ZoomAtDoubleClick;

        // Scrolled lanes, a banner or a resized pane move this control under a pointer that stays where it is.
        EffectiveViewportChanged += (_, _) =>
        {
            if (hovering)
            {
                InvalidateVisual();
                DrawingMovedUnderPointer();
            }
        };
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

    /// <summary>What a focused rung draws under its machine-context row (§3.2).</summary>
    private enum FocusRowKind
    {
        /// <summary>L1: one row per canonical owner of the group, in <see cref="WorkspaceViewModel.ProcessLaneDisplay"/> order.</summary>
        Owners,

        /// <summary>L2: one row per source direction, in <see cref="SessionTimelineQuery.LaneDirections"/> order.</summary>
        Directions,

        /// <summary>L3: one lane per channel end, first end first, banded by direction around its midline.</summary>
        ChannelEnds,
    }

    /// <summary>
    /// A focused rung's rows as drawn: row <c>i + 1</c> holds <c>Rows[i]</c>, the buckets its hit tests and hover read,
    /// and row 0 is the machine context on the same columns. Rows are indexed like the view model's lane list they came
    /// from, so the lane itself is found by the same index.
    /// </summary>
    private sealed record FocusRowSet(
        FocusRowKind Kind, IReadOnlyList<TimelineBucket> Context, IReadOnlyList<IReadOnlyList<TimelineBucket>> Rows);

    /// <summary>
    /// The rung's rows once they cover the viewport, with the machine buckets they were counted beside: the zoomed detail
    /// or the overview, whichever has their columns. Null while a new count is on its way, when the rung has none.
    /// </summary>
    private FocusRowSet? FocusRows
    {
        get
        {
            if (DataContext is not WorkspaceViewModel viewModel) return null;
            (FocusRowKind Kind, IReadOnlyList<TimelineBucket>[] Rows)? found =
                viewModel.ShowsProcessLanes ? (FocusRowKind.Owners, [.. viewModel.ProcessLaneDisplay.Select(lane => lane.Buckets)])
                : viewModel.ShowsDirectionLanes ? (FocusRowKind.Directions, [.. viewModel.TimelineDirectionLanes!.Select(lane => lane.Buckets)])
                : viewModel.ShowsChannelEndLanes ? (FocusRowKind.ChannelEnds, [.. viewModel.TimelineChannelEndLanes!.Select(end => end.Buckets)])
                : null;
            TimeRange visible = Viewport;
            if (found is not { Rows.Length: > 0 } rows || !rows.Rows.All(buckets => buckets.Count > 0
                && buckets[0].Interval.StartTicks <= visible.StartTicks && buckets[^1].Interval.EndTicks >= visible.EndTicks))
            {
                return null;
            }

            IReadOnlyList<TimelineBucket> first = rows.Rows[0];
            IReadOnlyList<TimelineBucket>? context = viewModel.TimelineDetail is { } detail && MatchingIntervals(first, detail.Buckets)
                ? detail.Buckets
                : MatchingIntervals(first, viewModel.Snapshot.Timeline) ? viewModel.Snapshot.Timeline : null;
            return context is null ? null : new(rows.Kind, context, rows.Rows);
        }
    }

    private bool ShowingLanes => ShowingMechanismLanes || FocusRows is not null;

    private int LaneCount => ShowingMechanismLanes
        ? ((WorkspaceViewModel)DataContext!).Snapshot.MechanismLanes.Count
        : FocusRows is { } rows ? rows.Rows.Count + 1 : 0;

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
        : FocusRows is not null ? ProcessPlotLeft : AggregatePlotLeft;

    /// <summary>The published plot's width: the whole plot, less the live edge while one is drawn.</summary>
    private double PlotWidth => Math.Max(1, Bounds.Width - PlotLeft - PlotRightMargin
        - (LiveEdgePlacement is { } live ? live.Width + LiveEdgeGap : 0));

    /// <summary>Space between the published plot and the live edge, where a dashed rule marks the published end.</summary>
    private const double LiveEdgeGap = 8;

    private const double MinimumLiveEdgeWidth = 36;

    /// <summary>
    /// Where the live edge is drawn: following a live capture at the whole extent, the time after the last published
    /// record gets the right-hand part of the plot, on the published scale where that leaves both parts legible (§12,
    /// §19.3). Null when the view is zoomed or panned, which is no longer following, or when there is nothing to preview.
    /// </summary>
    private LiveEdgeArea? LiveEdgePlacement
    {
        get
        {
            if (!IsFit || DataContext is not WorkspaceViewModel { LiveEdge: { Bins.Count: > 0 } edge } viewModel)
            {
                return null;
            }

            double available = Bounds.Width - PlotLeft - PlotRightMargin;
            if (available < 3 * MinimumLiveEdgeWidth)
            {
                return null;
            }

            TimeRange extent = viewModel.Snapshot.Extent;
            long end = Math.Max(edge.Bins[^1].Interval.EndTicks, extent.EndTicks + edge.Bins[^1].Interval.SpanTicks);
            double share = (double)(end - extent.EndTicks) / (end - extent.StartTicks);
            double width = Math.Clamp(available * share, MinimumLiveEdgeWidth, available * 0.4);
            return new(edge, new TimeRange(extent.EndTicks, end), PlotLeft + available - width, width);
        }
    }

    /// <summary>The live edge's place: its preview, the time it spans and where on the control it is drawn.</summary>
    private readonly record struct LiveEdgeArea(LiveEdge Edge, TimeRange Span, double Left, double Width)
    {
        /// <summary>A preview bin's columns; one earlier than the published end is drawn at the edge's start.</summary>
        public (double X1, double X2) Columns(LiveEdgeBin bin)
        {
            double x1 = X(bin.Interval.StartTicks);
            return (x1, Math.Max(x1 + 2, X(bin.Interval.EndTicks)));
        }

        private double X(long tick) =>
            Left + (Width * (Math.Clamp(tick, Span.StartTicks, Span.EndTicks) - Span.StartTicks) / Math.Max(1, Span.SpanTicks));
    }

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
        DrawingMovedUnderPointer();
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

            // The settled viewport is the ranking's scope while nothing is brushed (§6.4).
            viewModel.ShowVisibleRange(IsFit ? null : Viewport);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // A newer generation replaces the view model: the zoomed range stays, and its detail is asked for again.
        RefreshLaneLayout();
        RequestDetailNow();
        InvalidateVisual();
        DrawingMovedUnderPointer();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        DrawingMovedUnderPointer();
        detailTimer.Stop();
        detailTimer.Start();
    }

    /// <summary>
    /// Keep every L0–L2 row addressable. A long list grows inside the pane's vertical ScrollViewer instead of
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
        if (FocusRows is not { Kind: FocusRowKind.Owners } rows
            || DataContext is not WorkspaceViewModel { SelectedProcess: { } selected } viewModel) return;
        int index = viewModel.ProcessLaneDisplay.ToList().FindIndex(lane => lane.ProcessId == selected.Id);
        ScrollViewer? scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (index < 0 || scroller is null) return;
        double bottom = Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin);
        Rect row = LaneRow(index + 1, rows.Rows.Count + 1, PlotTop, bottom);
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
        double right = left + PlotWidth;
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
        FocusRowSet? rows = FocusRows;
        double maximumRate = ShowingMechanismLanes
            ? viewModel.Snapshot.MechanismLanes.SelectMany(lane => lane.Buckets)
                .Where(bucket => Intersects(bucket.Interval, visible)
                    && (detail is null || !Inside(bucket.Interval, detail.Interval)))
                .Concat(detail?.MechanismLanes.SelectMany(lane => lane.Buckets)
                    .Where(bucket => Intersects(bucket.Interval, visible)) ?? [])
                .Select(Rate).DefaultIfEmpty(0).Max()
            : rows is not null
                ? rows.Context.Concat(DrawnRowBuckets(viewModel, rows))
                    .Where(bucket => Intersects(bucket.Interval, visible))
                    .Select(Rate).DefaultIfEmpty(0).Max()
            : coarse.Concat(fine).Select(Rate).DefaultIfEmpty(0).Max();
        peakRate = maximumRate;

        // A focused rung draws every record as grey context and its own records in their mechanism's hue on the same
        // rate scale, inside the grey bar of the same interval: when the focus was active, against the machine (§3.2).
        bool focused = viewModel.TimelineShowsFocus;
        var scale = new BarScale(visible, left, plotWidth, top, bottom, maximumRate, focused);
        if (ShowingMechanismLanes || rows is not null)
        {
            switch (rows?.Kind)
            {
                case null:
                    DrawMechanismLanes(context, viewModel, detail, scale);
                    break;
                case FocusRowKind.Owners:
                    DrawProcessLanes(context, viewModel, rows.Context, viewModel.ProcessLaneDisplay, scale);
                    break;
                case FocusRowKind.Directions:
                    DrawDirectionLanes(context, viewModel, rows.Context, viewModel.TimelineDirectionLanes!, scale);
                    break;
                default:
                    DrawChannelEnds(context, viewModel, rows.Context, viewModel.TimelineChannelEndLanes!, scale);
                    break;
            }

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

        if (rows is null && focused && viewModel.TimelineFocusBuckets is { } focus)
        {
            DrawFocus(context, focus.Where(bucket => Intersects(bucket.Interval, visible)), scale);
        }

        if (LiveEdgePlacement is { } live)
        {
            DrawLiveEdge(context, viewModel, live, rows, top, bottom, maximumRate);
        }

        DrawSelection(context, viewModel, visible, left, plotWidth, top, bottom);
        DrawEvidenceMarks(context, viewModel, visible, left, plotWidth, top, bottom);
        if (viewModel.IsEmptyWorkspace)
        {
            // No session is shown, so there is no time axis: the placeholder extent is not an interval anyone recorded.
            DrawText(context, "No records yet", new(left, bottom + 7));
        }
        else
        {
            DrawText(context, WorkspaceTime.FormatInstant(visible.StartTicks, visible.SpanTicks, CultureInfo.CurrentCulture), new(left, bottom + 7));
            string end = WorkspaceTime.FormatInstant(visible.EndTicks, visible.SpanTicks, CultureInfo.CurrentCulture);
            DrawText(context, end, new(right - (6.5 * end.Length), bottom + 7));
            DrawText(context, RateText(maximumRate * WorkspaceTime.TicksPerSecond), new(4, top - 4));
        }

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

        if (HoveredLiveBin is { } hoveredLive && LiveEdgePlacement is { } liveArea)
        {
            (double x1, double x2) = liveArea.Columns(hoveredLive);
            Rect row = ShowingLanes && LaneIndexAt(HoverPoint.Y) is { } index
                ? LaneRow(index, LaneCount, top, bottom)
                : new Rect(liveArea.Left, top, liveArea.Width, bottom - top);
            using (context.PushOpacity(0.5))
            {
                context.DrawRectangle(Brushes.Transparent, HoverPen, new Rect(x1 - 1, row.Top, Math.Max(2, x2 - x1), row.Height));
            }
        }
    }

    /// <summary>The bucket drawn under the pointer, if it rests on the plot; hover never changes selection (§6.4).</summary>
    internal TimelineBucket? HoveredBucket
    {
        get
        {
            if (HoverTick is not { } tick || DataContext is not WorkspaceViewModel viewModel)
            {
                return null;
            }

            if (HoveredLaneIndex is not { } index)
            {
                return ShowingLanes ? null : BucketAt(viewModel, tick);
            }

            if (FocusRows is { } rows)
            {
                IReadOnlyList<TimelineBucket> buckets = index == 0 ? rows.Context : rows.Rows[index - 1];
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

    private int? HoveredLaneIndex => HoverTick is not null ? LaneIndexAt(HoverPoint.Y) : null;

    /// <summary>The instant under the resting pointer, on the published plot and on a lane where lanes are drawn.</summary>
    private long? HoverTick => hovering && HoverPoint is var point && !OverLiveEdge(point) && OnPlot(point)
        && (!ShowingLanes || LaneIndexAt(point.Y) is not null)
        ? TickAt(point.X)
        : null;

    /// <summary>Whether the resting pointer is on the live edge, where it hovers a preview bin.</summary>
    private bool HoverLive => hovering && OverLiveEdge(HoverPoint);

    /// <summary>
    /// Tells the hover layer that what lies under a resting pointer may have changed, because the drawing moved. The
    /// answer itself is read afresh; only the card's redraw needs the nudge.
    /// </summary>
    private void DrawingMovedUnderPointer()
    {
        if (hovering)
        {
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
            if (HoveredLiveBin is { } liveBin && DataContext is WorkspaceViewModel live)
            {
                return live.DescribeLiveEdgeHover(liveBin, ShowingMechanismLanes && LaneIndexAt(HoverPoint.Y) is { } lane
                    ? live.Snapshot.MechanismLanes[lane].Mechanism : null);
            }

            if (HoveredBucket is not { } bucket || DataContext is not WorkspaceViewModel viewModel)
                return null;
            int? index = HoveredLaneIndex;
            FocusRowKind? kind = FocusRows?.Kind;
            Mechanism? mechanism = ShowingMechanismLanes && index is { } mechanismIndex
                ? viewModel.Snapshot.MechanismLanes[mechanismIndex].Mechanism : null;
            ProcessNode? owner = kind == FocusRowKind.Owners && index is > 0
                ? viewModel.Snapshot.Processes.FirstOrDefault(process =>
                    process.Id == viewModel.ProcessLaneDisplay[index.Value - 1].ProcessId)
                : null;
            Direction? direction = kind == FocusRowKind.Directions && index is > 0
                ? viewModel.TimelineDirectionLanes![index.Value - 1].Direction : null;
            ChannelEndTimelineLane? end = kind == FocusRowKind.ChannelEnds && index is > 0
                ? viewModel.TimelineChannelEndLanes![index.Value - 1] : null;
            return viewModel.DescribeTimelineHover(bucket, peakRate * WorkspaceTime.TicksPerSecond,
                mechanism, owner, direction, end);
        }
    }

    /// <inheritdoc />
    /// <remarks>The resting pointer in this control's coordinates as it lies now, however the control moved under it.</remarks>
    public Point HoverPoint => TopLevel.GetTopLevel(this) is { } window && window.TranslatePoint(hoverInWindow, this) is { } here
        ? here
        : hoverInWindow;

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
        if (FocusRows is { } rows)
        {
            int rowIndex = rows.Rows.ToList().FindIndex(buckets =>
                buckets.Any(candidate => ReferenceEquals(candidate, bucket)));
            if (rowIndex >= 0) index = rowIndex + 1;
            else if (rows.Context.Any(candidate => ReferenceEquals(candidate, bucket))) index = 0;
        }

        double y = index >= 0
            ? LaneRow(index, LaneCount, PlotTop,
                Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin)).Center.Y
            : Math.Max(PlotTop, Bounds.Height - 40);
        return new(PlotLeft + ViewportMath.PixelAtTick(visible, Math.Clamp(middle, visible.StartTicks, visible.EndTicks - 1), PlotWidth),
            y);
    }

    /// <summary>A process ID disambiguates two owner rows whose bucket values and intervals happen to match.</summary>
    internal Point? PointOf(ProcessInstanceId processId, TimelineBucket bucket) =>
        DataContext is WorkspaceViewModel viewModel
            ? RowPoint(FocusRowKind.Owners, viewModel.ProcessLaneDisplay.ToList().FindIndex(lane => lane.ProcessId == processId), bucket)
            : null;

    /// <summary>The direction names the row, because empty buckets of two rows over one interval are equal values.</summary>
    internal Point? PointOf(Direction direction, TimelineBucket bucket) =>
        DataContext is WorkspaceViewModel { TimelineDirectionLanes: { } lanes }
            ? RowPoint(FocusRowKind.Directions, lanes.ToList().FindIndex(lane => lane.Direction == direction), bucket)
            : null;

    /// <summary>An end's lane by its number: both ends of a process connected to itself hold equal-looking buckets.</summary>
    internal Point? PointOfEnd(int end, TimelineBucket bucket) =>
        DataContext is WorkspaceViewModel { TimelineChannelEndLanes: { } ends }
            ? RowPoint(FocusRowKind.ChannelEnds, ends.ToList().FindIndex(lane => lane.End == end), bucket)
            : null;

    /// <summary>The centre of a bucket's column in focused row <paramref name="index"/>, when that row is drawn and holds it.</summary>
    private Point? RowPoint(FocusRowKind kind, int index, TimelineBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        if (FocusRows is not { } rows || rows.Kind != kind || index < 0 || index >= rows.Rows.Count
            || !rows.Rows[index].Contains(bucket))
        {
            return null;
        }

        TimeRange visible = Viewport;
        if (!Intersects(bucket.Interval, visible)) return null;
        long middle = bucket.Interval.StartTicks + (bucket.Interval.SpanTicks / 2);
        return new(PlotLeft + ViewportMath.PixelAtTick(visible,
                Math.Clamp(middle, visible.StartTicks, visible.EndTicks - 1), PlotWidth),
            LaneRow(index + 1, rows.Rows.Count + 1, PlotTop,
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
                : name == ProcessNode.PidName(process.ProcessId) ? name
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

    /// <summary>L2's exact source-direction partition; the machine row is context, never added to the owner total.</summary>
    private static void DrawDirectionLanes(DrawingContext context, WorkspaceViewModel viewModel,
        IReadOnlyList<TimelineBucket> machine, IReadOnlyList<DirectionTimelineLane> lanes, BarScale scale)
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

            DirectionTimelineLane lane = lanes[index - 1];
            TimelineBucket[] drawn = [.. lane.Buckets.Where(bucket => Intersects(bucket.Interval, scale.Visible))];

            // A row with nothing drawn still carries its coverage strip; naming it empty keeps that strip from being
            // read as records, and says the source reported no record of this direction here.
            string label = WorkspaceViewModel.DirectionLabel(lane.Direction)
                + (drawn.Any(bucket => bucket.ObservationCount > 0) ? string.Empty : " · none");
            if (viewModel.SelectedTimelineDirection == lane.Direction)
            {
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 1),
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }

            DrawText(context, label, new(9, row.Center.Y - 7));
            DrawLaneSeries(context, viewModel, null, drawn, rowScale, row);
        }
    }

    /// <summary>
    /// The live edge (§12, §19.3): the broker's preview counts for the chunks not yet published here, beyond a dashed rule
    /// at the published end, at half strength so they never read as published bars. At L0 each lane shows its own
    /// mechanism; a focused rung shows them only in its machine row, because a preview is not attributed to processes or
    /// channels yet. Heights share the published rate scale, and a bar above it is drawn full height rather than rescaling
    /// the published timeline every quarter second.
    /// </summary>
    private void DrawLiveEdge(DrawingContext context, WorkspaceViewModel viewModel, LiveEdgeArea live,
        FocusRowSet? rows, double top, double bottom, double maximumRate)
    {
        double scaleRate = maximumRate > 0
            ? maximumRate
            : live.Edge.Bins.Select(bin => (double)bin.Total / Math.Max(1, bin.Interval.SpanTicks)).DefaultIfEmpty(0).Max();
        double ruleX = live.Left - (LiveEdgeGap / 2);
        context.DrawLine(new Pen(TextBrush, 1, new DashStyle([3, 3], 0)), new(ruleX, top), new(ruleX, bottom));
        if (ShowingMechanismLanes)
        {
            // A mechanism first seen after the last publication has no lane until it publishes; the label names it, and
            // hovering the edge on any lane lists it, so no previewed record is silently undrawn.
            IReadOnlyList<MechanismTimelineLane> lanes = viewModel.Snapshot.MechanismLanes;
            Mechanism[] unlaned = [.. live.Edge.Bins.SelectMany(bin => bin.Counts.Select(count => count.Mechanism)).Distinct()
                .Where(mechanism => lanes.All(lane => lane.Mechanism != mechanism)).Order()];
            DrawText(context, unlaned.Length switch
            {
                0 => "live",
                1 => $"live · +{EvidenceRowText.MechanismName(unlaned[0])}",
                _ => $"live · +{unlaned.Length} mechanisms",
            }, new(live.Left, top - 16));
            for (int index = 0; index < lanes.Count; index++)
            {
                Rect row = LaneRow(index, lanes.Count, top, bottom);
                Mechanism mechanism = lanes[index].Mechanism;
                DrawLiveBars(context, live, row.Top + 3, row.Bottom - 5, scaleRate, 0.42, bin => (bin.CountOf(mechanism), mechanism));
            }
        }
        else if (rows is not null)
        {
            DrawText(context, "live", new(live.Left, top - 16));
            Rect machine = LaneRow(0, rows.Rows.Count + 1, top, bottom);
            DrawLiveBars(context, live, machine.Top + 3, machine.Bottom - 5, scaleRate, 0.42, bin => (bin.Total, null));
        }
        else
        {
            DrawText(context, "live", new(live.Left, top - 16));
            DrawLiveBars(context, live, top, bottom, scaleRate, 0, bin => (bin.Total, bin.Counts[0].Mechanism));
        }
    }

    /// <summary>One row of preview bars; a null hue draws them as the machine row's context grey.</summary>
    private static void DrawLiveBars(DrawingContext context, LiveEdgeArea live, double top, double bottom, double scaleRate,
        double floor, Func<LiveEdgeBin, (int Count, Mechanism? Hue)> read)
    {
        double height = Math.Max(1, bottom - top);
        foreach (LiveEdgeBin bin in live.Edge.Bins)
        {
            (int count, Mechanism? hue) = read(bin);
            if (count == 0)
            {
                continue;
            }

            (double x1, double x2) = live.Columns(bin);
            double rate = (double)count / Math.Max(1, bin.Interval.SpanTicks);
            double bar = Math.Clamp(height * rate / Math.Max(double.Epsilon, scaleRate), Math.Min(height, Math.Max(3, height * floor)), height);
            IBrush fill = hue is { } mechanism
                ? new SolidColorBrush(ThemeResources.FillOf(mechanism, Mode), 0.5)
                : new SolidColorBrush(ContextBarBrush.Color, 0.25);
            context.DrawRectangle(fill, null, new Rect(x1, bottom - bar, Math.Max(1, x2 - x1 - 1), bar));
        }
    }

    /// <summary>
    /// A direction band's bar: its rate's share of the half-lane on the shared scale, never under §6.2's occupied floor
    /// of 0.42 of the band, so a lone record in a quiet end stays visible and pointable.
    /// </summary>
    private static double BandHeight(TimelineBucket bucket, double half, double maximumRate) =>
        Math.Min(half, Math.Max(half * 0.42, half * Rate(bucket) / Math.Max(double.Epsilon, maximumRate)));

    /// <summary>The buckets a focused rung draws as bars: each row's, or at L3 each end's two direction bands.</summary>
    private static IEnumerable<TimelineBucket> DrawnRowBuckets(WorkspaceViewModel viewModel, FocusRowSet rows) =>
        rows.Kind == FocusRowKind.ChannelEnds
            ? viewModel.TimelineChannelEndLanes!.SelectMany(end => end.Outbound.Concat(end.Inbound))
            : rows.Rows.SelectMany(buckets => buckets);

    /// <summary>
    /// L3's two ends under the machine row (§3.2): outbound records rise above each end's midline and inbound records fall
    /// below it, on the shared rate scale, so the channel reads as a conversation. A record with no data direction, such
    /// as a disconnect, is a neutral mark on the midline rather than a bar on either side.
    /// </summary>
    private static void DrawChannelEnds(DrawingContext context, WorkspaceViewModel viewModel,
        IReadOnlyList<TimelineBucket> machine, IReadOnlyList<ChannelEndTimelineLane> ends, BarScale scale)
    {
        int count = ends.Count + 1;
        for (int index = 0; index < count; index++)
        {
            Rect row = LaneRow(index, count, scale.Top, scale.Bottom);
            context.DrawLine(new Pen(GridBrush, 0.7), new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            if (index == 0)
            {
                DrawText(context, "Machine · all records", new(9, row.Center.Y - 7));
                DrawLaneSeries(context, viewModel, null,
                    machine.Where(bucket => Intersects(bucket.Interval, scale.Visible)),
                    new BarScale(scale.Visible, scale.Left, scale.PlotWidth, row.Top + 3, row.Bottom - 5,
                        scale.MaximumRate, Focused: false),
                    row, contextRow: true);
                continue;
            }

            ChannelEndTimelineLane end = ends[index - 1];
            if (viewModel.SelectedChannelEnd == end.End)
            {
                context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 1),
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }

            // Two lines: who holds the end, then the end's own endpoint, which tells a looped process's ends apart.
            string holder = viewModel.ChannelEndHolder(end);
            DrawText(context, holder.Length > 24 ? holder[..23] + "…" : holder, new(9, row.Center.Y - 14));
            DrawText(context, end.Endpoint.Length > 24 ? end.Endpoint[..23] + "…" : end.Endpoint, new(9, row.Center.Y + 1));

            // The bands keep clear of the coverage strip along the row's foot; the arrows name their sides in place.
            double bandTop = row.Top + 3;
            double middle = (bandTop + row.Bottom - 6) / 2;
            double half = Math.Max(1, middle - bandTop - 1);
            context.DrawLine(new Pen(GridBrush, 0.7), new(scale.Left, middle), new(scale.Left + scale.PlotWidth, middle));
            DrawText(context, "↑", new(scale.Left - 12, middle - 13));
            DrawText(context, "↓", new(scale.Left - 12, middle - 1));
            for (int bucketIndex = 0; bucketIndex < end.Buckets.Count; bucketIndex++)
            {
                TimelineBucket total = end.Buckets[bucketIndex];
                if (!Intersects(total.Interval, scale.Visible)) continue;
                double x1 = scale.X(Math.Max(total.Interval.StartTicks, scale.Visible.StartTicks));
                double x2 = scale.X(Math.Min(total.Interval.EndTicks, scale.Visible.EndTicks));
                double width = Math.Max(1, x2 - x1 - 2);
                Rect? drawn = null;
                if (end.Outbound[bucketIndex] is { ObservationCount: > 0 } sent)
                {
                    double height = BandHeight(sent, half, scale.MaximumRate);
                    Rect bar = new(x1, middle - 1 - height, width, height);
                    context.DrawRectangle(BrushFor(sent.DominantMechanism), null, bar);
                    drawn = bar;
                }

                if (end.Inbound[bucketIndex] is { ObservationCount: > 0 } received)
                {
                    Rect bar = new(x1, middle + 1, width, BandHeight(received, half, scale.MaximumRate));
                    context.DrawRectangle(BrushFor(received.DominantMechanism), null, bar);
                    drawn = drawn is { } upper ? upper.Union(bar) : bar;
                }

                if (total.ObservationCount - end.Outbound[bucketIndex].ObservationCount
                    - end.Inbound[bucketIndex].ObservationCount > 0)
                {
                    Rect mark = new(x1, middle - 2.5, width, 5);
                    context.DrawRectangle(ContextBarBrush, new Pen(TextBrush, 0.8), mark);
                    drawn = drawn is { } union ? union.Union(mark) : mark;
                }

                if (drawn is { } selectedBar && viewModel.SelectedInterval == total.Interval)
                {
                    context.DrawRectangle(Brushes.Transparent, new Pen(SelectedBrush, 2), selectedBar.Inflate(1));
                }

                if (total.Coverage != CoverageState.Covered)
                {
                    DrawCoverageGap(context, total.Coverage == CoverageState.UnknownCoverage
                        ? new Rect(x1, row.Bottom - 5, width, 5)
                        : new Rect(x1, row.Top, width, row.Height));
                }
            }
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
            // A row's name is its table and step focus; the machine row's name returns to the rung's whole focus.
            switch (FocusRows?.Kind)
            {
                case null:
                    viewModel.SelectTimelineLane(viewModel.Snapshot.MechanismLanes[laneIndex].Mechanism);
                    break;
                case FocusRowKind.Owners when laneIndex == 0:
                    viewModel.ClearProcessLaneFocus();
                    break;
                case FocusRowKind.Owners:
                    ProcessInstanceId processId = viewModel.ProcessLaneDisplay[laneIndex - 1].ProcessId;
                    viewModel.SelectedRung = viewModel.RungRows.FirstOrDefault(row => row.Key == processId.ToString());
                    if (viewModel.SelectedRung is null)
                    {
                        viewModel.SelectedProcess = viewModel.Snapshot.Processes.FirstOrDefault(node => node.Id == processId);
                    }

                    break;
                case FocusRowKind.Directions:
                    viewModel.SelectDirectionLane(laneIndex == 0 ? null : viewModel.TimelineDirectionLanes![laneIndex - 1].Direction);
                    break;
                case FocusRowKind.ChannelEnds:
                    viewModel.SelectChannelEnd(laneIndex == 0 ? null : viewModel.TimelineChannelEndLanes![laneIndex - 1].End);
                    break;
            }

            e.Handled = true;
            return;
        }
        if (!OnPlot(point.Position)) return;
        if (hovering)
        {
            // A press begins a gesture; its card would describe a bucket the gesture is about to change.
            hovering = false;
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
            // No press: the pointer only explains the bucket - or live preview bin - under it, and the card follows it.
            long? tick = HoverTick;
            bool live = HoverLive;
            Point previous = hoverInWindow;
            hovering = true;
            hoverInWindow = e.GetPosition(TopLevel.GetTopLevel(this));
            if (HoverTick != tick || HoverLive != live || ((HoverTick is not null || HoverLive) && hoverInWindow != previous))
            {
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
        if (hovering)
        {
            hovering = false;
            InvalidateVisual();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether a point is on the live edge, on a row that draws a preview there.</summary>
    private bool OverLiveEdge(Point point) =>
        LiveEdgePlacement is { } live
        && point.X >= live.Left && point.X <= live.Left + live.Width
        && point.Y >= PlotTop && point.Y <= Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin)
        && (!ShowingLanes || LaneIndexAt(point.Y) is { } row && (ShowingMechanismLanes || row == 0));

    /// <summary>The preview bin drawn under the pointer on the live edge, snapping to the nearest within a few pixels.</summary>
    internal LiveEdgeBin? HoveredLiveBin
    {
        get
        {
            if (!HoverLive || LiveEdgePlacement is not { } live)
            {
                return null;
            }

            LiveEdgeBin? nearest = null;
            double best = 6;
            double x = HoverPoint.X;
            foreach (LiveEdgeBin bin in live.Edge.Bins)
            {
                (double x1, double x2) = live.Columns(bin);
                double distance = x < x1 ? x1 - x : x > x2 ? x - x2 : 0;
                if (distance < best)
                {
                    best = distance;
                    nearest = bin;
                }
            }

            return nearest;
        }
    }

    /// <summary>Where a preview bin is drawn on the live edge, for pointing at it; null when no live edge is drawn.</summary>
    internal Point? PointOfLive(LiveEdgeBin bin, int row = 0)
    {
        ArgumentNullException.ThrowIfNull(bin);
        if (LiveEdgePlacement is not { } live || !live.Edge.Bins.Contains(bin))
        {
            return null;
        }

        (double x1, double x2) = live.Columns(bin);
        double bottom = Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin);
        return new((x1 + x2) / 2, ShowingLanes ? LaneRow(row, LaneCount, PlotTop, bottom).Center.Y : (PlotTop + bottom) / 2);
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
    /// process supplies its canonical-owner buckets, at L2 a selected source direction its row's, and at L3 a selected
    /// channel end its own. Otherwise the rung's focus or whole machine does. The viewport follows a step that leaves it.
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

        // A chosen row - an owner, a direction or an end - is already an exact subset of the rung's focus.
        IReadOnlyList<TimelineBucket>? chosen = FocusRows?.Kind switch
        {
            FocusRowKind.Owners when viewModel.SelectedProcess is { } selectedProcess =>
                viewModel.ProcessLaneDisplay.FirstOrDefault(lane => lane.ProcessId == selectedProcess.Id)?.Buckets,
            FocusRowKind.Directions when viewModel.SelectedTimelineDirection is { } selectedDirection =>
                viewModel.TimelineDirectionLanes!.FirstOrDefault(lane => lane.Direction == selectedDirection)?.Buckets,
            FocusRowKind.ChannelEnds when viewModel.SelectedChannelEnd is { } selectedEnd =>
                viewModel.TimelineChannelEndLanes!.FirstOrDefault(lane => lane.End == selectedEnd)?.Buckets,
            _ => null,
        };
        if (chosen is not null)
        {
            buckets = chosen;
            focus = null;
            zoomed = viewModel.TimelineDetail is { } rowDetail && MatchingIntervals(chosen, rowDetail.Buckets);
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
