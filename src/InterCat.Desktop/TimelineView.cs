using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
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

    private static IBrush SelectedBrush => Current.SelectedBrush;
    private static IBrush GridBrush => Current.GridBrush;
    private static IBrush TextBrush => Current.TextBrush;
    private static SolidColorBrush DimBrush => Current.DimBrush;

    /// <summary>A focused rung's rest of the machine: muted ink, so no bar of it is read as a mechanism's hue (§6.6).</summary>
    private static SolidColorBrush ContextBarBrush => Current.ContextBarBrush;

    private static Pen HoverPen => Current.HoverPen;
    private static Pen GridPen => Current.GridPen;
    private static Pen RulePen => Current.RulePen;
    private static Pen SelectionPen => Current.SelectionPen;
    private static Pen RowSelectionPen => Current.RowSelectionPen;
    private static Pen HighlightPen => Current.HighlightPen;
    private static Pen MarkPen => Current.MarkPen;
    private static Pen TextPen => Current.TextPen;
    private static Pen PinHeadPen => Current.PinHeadPen;
    private static IBrush MatchBrush => Current.MatchBrush;

    /// <summary>The timeline's brushes in one theme mode, from its verified tokens (§6.6).</summary>
    private sealed class Ink
    {
        public Ink(ThemeMode mode)
        {
            SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
            // The hatch is caution ink: a coverage defect must never read as a mechanism's hue (§6.6).
            GapPen = new(Token(ThemePalette.Status(mode).Caution), 1);
            SelectedBrush = Token(surfaces.Accent);
            GridBrush = Token(surfaces.Elevated);
            TextBrush = Token(surfaces.MutedInk);
            DimBrush = Token(surfaces.Plot);
            ContextBarBrush = new(ThemeResources.ToColor(surfaces.MutedInk), 0.4);
            HoverPen = new(Token(surfaces.Ink), 1.5);
            GridPen = new(GridBrush, 1);
            RulePen = new(GridBrush, 0.7);
            SelectionPen = new(SelectedBrush, 2);
            RowSelectionPen = new(SelectedBrush, 1);
            HighlightPen = new(SelectedBrush, 2);
            MarkPen = new(TextBrush, 0.8);
            TextPen = new(TextBrush, 1);
            PinHeadPen = new(DimBrush, 1.5);
            MatchBrush = Token(surfaces.Ink);
            OutsideBrush = new(DimBrush.Color, 0.6);
            LiveContextBrush = new(ContextBarBrush.Color, 0.25);
            FailedBrush = Token(ThemePalette.Status(mode).Caution);
            this.mode = mode;
        }

        /// <summary>A call that completed with a failure status: caution ink, never a mechanism's hue (§6.6).</summary>
        public SolidColorBrush FailedBrush { get; }

        private readonly ThemeMode mode;
        private readonly Dictionary<Mechanism, SolidColorBrush> liveBrushes = [];
        private readonly Dictionary<Mechanism, SolidColorBrush> fillBrushes = [];
        private readonly Dictionary<Mechanism, Pen> hatchPens = [];
        private Pen? contextHatchPen;

        /// <summary>
        /// §6.6's unmeasured cell: its sides and cross-hatch in the mechanism's hue at a pixel, as the graph draws its
        /// unmeasured band, built once per mode (R11).
        /// </summary>
        public Pen HatchPen(Mechanism mechanism)
        {
            if (!hatchPens.TryGetValue(mechanism, out Pen? pen))
            {
                pen = new Pen(FillBrush(mechanism), 1);
                hatchPens[mechanism] = pen;
            }

            return pen;
        }

        /// <summary>The machine context row's unmeasured cell: muted ink, as its bars are, never a mechanism's hue.</summary>
        public Pen ContextHatchPen => contextHatchPen ??= new Pen(TextBrush, 1);

        /// <summary>A mechanism's fill, built once per mode and reused for every bar (R11).</summary>
        public SolidColorBrush FillBrush(Mechanism mechanism)
        {
            if (!fillBrushes.TryGetValue(mechanism, out SolidColorBrush? brush))
            {
                brush = new SolidColorBrush(ThemeResources.FillOf(mechanism, mode));
                fillBrushes[mechanism] = brush;
            }

            return brush;
        }

        public Pen GapPen { get; }
        public IBrush SelectedBrush { get; }
        public IBrush GridBrush { get; }
        public IBrush TextBrush { get; }
        public SolidColorBrush DimBrush { get; }
        public SolidColorBrush ContextBarBrush { get; }
        public Pen HoverPen { get; }
        public Pen GridPen { get; }
        public Pen RulePen { get; }
        public Pen SelectionPen { get; }
        public Pen RowSelectionPen { get; }

        /// <summary>The outline around the selection's share of a bar (§6.4, §6.6).</summary>
        public Pen HighlightPen { get; }

        /// <summary>The outline of an L3 record with no data direction, on the midline.</summary>
        public Pen MarkPen { get; }

        /// <summary>Muted ink at a pixel: evidence marks and the live edge's rule.</summary>
        public Pen TextPen { get; }

        /// <summary>The rim of a pinned lane's pin head, in the plot's own colour, as the graph rims a pinned node's.</summary>
        public Pen PinHeadPen { get; }

        /// <summary>The mark of a lane a search finds: the strongest ink, never a mechanism's hue or the selection's accent.</summary>
        public IBrush MatchBrush { get; }

        /// <summary>What lies outside the analysis interval steps back under this.</summary>
        public SolidColorBrush OutsideBrush { get; }

        /// <summary>A live preview bar of the machine row, fainter than the published context grey.</summary>
        public SolidColorBrush LiveContextBrush { get; }

        /// <summary>A live preview bar of one mechanism: its fill at half strength, so it never reads as published.</summary>
        public SolidColorBrush LiveBrush(Mechanism mechanism)
        {
            if (!liveBrushes.TryGetValue(mechanism, out SolidColorBrush? brush))
            {
                brush = new SolidColorBrush(ThemeResources.FillOf(mechanism, mode), 0.5);
                liveBrushes[mechanism] = brush;
            }

            return brush;
        }

        private static SolidColorBrush Token(Srgb value) => new(ThemeResources.ToColor(value));
    }

    /// <summary>A press that moves at least this far brushes a range; a shorter one selects the bucket under it.</summary>
    private const double BrushThreshold = 4;

    // What one repaint gathers and formats, kept between repaints so a frame that draws the same thing allocates nothing
    // (R11, §19.4). A label is formatted again only when the value it states changes.
    private readonly List<TimelineBucket> coarseBuckets = [];
    private readonly List<TimelineBucket> fineBuckets = [];
    private readonly Memo<long> generationNote = new();
    private readonly Memo<long> byteGenerationNote = new();
    private readonly Memo<(long, long, SessionClock)> startLabel = new();
    private readonly Memo<(long, long, SessionClock)> endLabel = new();
    private readonly Memo<double> rateLabel = new();
    private readonly Memo<double> byteRateLabel = new();
    private readonly Memo<(LiveEdge, IReadOnlyList<MechanismTimelineLane>, bool)> liveLabel = new();
    private readonly Memo<(DateTimeOffset?, SessionClock, (long, long))> timeBaseLabel = new();

    /// <summary>A row's labels by the row they name; rows are immutable, so a label is formatted once per row.</summary>
    private static readonly Dictionary<(object Row, int Part), string> RowLabels = new(RowKeyComparer.Instance);

    private static readonly Dictionary<(Direction Direction, bool None), string> DirectionLabels = [];

    /// <summary>The plot's margins: the axis label gutter on the left and a little air on the right.</summary>
    private const double AggregatePlotLeft = 38;
    private const double LanePlotLeft = 118;
    private const double ProcessPlotLeft = 170;
    private const double PlotRightMargin = 14;
    internal const double PlotTop = 24;
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

    /// <summary>
    /// Each drawn lane row's own busiest rate, in the rows' order, when each lane is read against its own (§6.2's
    /// normalization scope): the first <see cref="rowPeakCount"/> are this frame's, in an array kept from frame to frame so
    /// a repaint allocates nothing (R11). None under one shared scale.
    /// </summary>
    private double[] rowPeaks = new double[16];

    private int rowPeakCount;

    private readonly DispatcherTimer detailTimer;
    private TimeRange? viewport;
    private long? brushAnchor;
    private long? brushEnd;
    private double pressX;
    private double pressY;
    /// <summary>The call or exchange a press began on, which a click on it selects.</summary>
    private object? pressedCall;
    private bool brushing;
    private bool moved;
    private TimeRange panOrigin;

    // Whether the last press ended as a plain click, neither a pan, a brush nor cancelled. Only then does a second press at
    // its place make the double click that zooms (§6.7): two quick pans begun at one place are two pans. A press records
    // its end when it ends, since the double click is raised after the second press's own handling.
    private bool clickedLast;

    /// <summary>The pointer a press captured, released when its gesture ends or is cancelled.</summary>
    private IPointer? pressPointer;

    /// <summary>
    /// The timeline as a screen reader meets it: its role, its keyboard path and table, and what it draws now, with the
    /// analysis interval where one is chosen, the range in view and the time base it is counted in. The axis showed that
    /// range only to the eye, so after + or an arrow the timeline read the same words wherever the view had moved, and the
    /// interval a step, a click or a moment gone to chose was drawn and never said (R15).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "timeline",
        "Left and Right pan, with Shift by one bucket; plus and minus zoom; Home and End go to the session's edges; 0 "
        + "fits the analysis interval, or the whole session when none is brushed; [ and ] step to the previous or next "
        + "record; Up and Down scroll lanes. P pins the selected process's lane at the top of its group's lanes, or unpins "
        + "it. A search typed at a group marks the lanes it finds and shows the first. T shows the interval table, which "
        + "lists what the timeline draws.",
        () => DataContext is WorkspaceViewModel viewModel
            ? viewModel.TimelineCaption
                + (viewModel.SelectedInterval is { } chosen
                    ? " · analysis interval " + viewModel.TimeBase.Range(chosen, CultureInfo.CurrentCulture)
                    : string.Empty)
                + " · " + ViewportWords(Viewport, IsFit, viewModel.Snapshot.Extent, viewModel.TimeBase)
                + " · " + viewModel.TimeBase.Base(Viewport, viewModel.Snapshot.Began, CultureInfo.CurrentCulture)
            : null);

    /// <summary>
    /// The range in view as the timeline and minimap say it: the whole session while it is fitted, else the range
    /// shown and the session's own, so a zoom and a pan read as the axis draws them - in the time base it reads (§6.2).
    /// </summary>
    internal static string ViewportWords(TimeRange viewport, bool fit, TimeRange extent, SessionClock? clock = null)
    {
        SessionClock reads = clock ?? SessionClock.Session(TimeZoneInfo.Local);
        return fit
            ? "the whole session in view, " + reads.Range(extent, CultureInfo.CurrentCulture)
            : "in view " + reads.Range(viewport, CultureInfo.CurrentCulture) + " of the session's "
                + reads.Range(extent, CultureInfo.CurrentCulture);
    }

    public TimelineView()
    {
        detailTimer = new DispatcherTimer { Interval = DetailSettle };
        detailTimer.Tick += (_, _) => RequestDetailNow();
        DoubleTapped += ZoomAtDoubleClick;

        // Scrolled lanes, a banner or a resized pane move this control under a pointer that stays where it is.
        EffectiveViewportChanged += (_, e) =>
        {
            // Only the rows in view are drawn (§6.2). A scroll moves this control, which draws it again; a view that grows or
            // moves without moving it - the pane made taller, or filling the column - draws again here once it shows a row
            // the last repaint did not draw.
            effectiveViewport = e.EffectiveViewport;
            if (hovering || !DrawnRowsHold(e.EffectiveViewport))
            {
                InvalidateVisual();
            }

            if (hovering)
            {
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

        /// <summary>L3 of an RPC channel: one lane of its calls, each a bar from its start to its stop.</summary>
        Calls,
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
            // Read many times a frame (the plot's left edge, the lane count, hit tests), and rebuilt only when the rung's
            // rows, the detail, the overview or the viewport it is checked against change (R11).
            if (DataContext is not WorkspaceViewModel viewModel) return null;
            object? lanes = viewModel.ShowsProcessLanes ? viewModel.ProcessLaneDisplay
                : viewModel.ShowsDirectionLanes ? viewModel.TimelineDirectionLanes
                : viewModel.ShowsChannelEndLanes ? viewModel.TimelineChannelEndLanes
                : viewModel.ShowsRpcCallLane ? viewModel.RpcCallSpans
                : viewModel.ShowsHttpExchangeLane ? viewModel.HttpExchangeSpans
                : null;
            var key = new FocusRowsKey(viewModel, lanes, viewModel.TimelineDetail, viewModel.Snapshot.Timeline, Viewport,
                viewModel.ShowsProcessLanes ? viewModel.FoldedLane : null);
            if (focusRowsKey != key)
            {
                focusRowsKey = key;
                focusRows = lanes is null ? null : ComputeFocusRows(viewModel);
            }

            return focusRows;
        }
    }

    private FocusRowsKey? focusRowsKey;
    private FocusRowSet? focusRows;

    /// <summary>What <see cref="FocusRows"/> depends on, by identity: each is replaced, never changed in place.</summary>
    private readonly record struct FocusRowsKey(object ViewModel, object? Lanes, object? Detail, object Timeline, TimeRange Visible,
        object? Folded)
    {
        public bool Equals(FocusRowsKey other) => ReferenceEquals(ViewModel, other.ViewModel) && ReferenceEquals(Lanes, other.Lanes)
            && ReferenceEquals(Detail, other.Detail) && ReferenceEquals(Timeline, other.Timeline) && Visible == other.Visible
            && ReferenceEquals(Folded, other.Folded);

        public override int GetHashCode() => HashCode.Combine(Visible);
    }

    private FocusRowSet? ComputeFocusRows(WorkspaceViewModel viewModel)
    {
        (FocusRowKind Kind, IReadOnlyList<TimelineBucket>[] Rows)? found =
            viewModel.ShowsProcessLanes
                ? (FocusRowKind.Owners,
                    [.. viewModel.ProcessLaneDisplay.Select(lane => lane.Buckets), .. viewModel.FoldedLane is { } folded ? [folded.Buckets] : Array.Empty<IReadOnlyList<TimelineBucket>>()])
            : viewModel.ShowsDirectionLanes ? (FocusRowKind.Directions, [.. viewModel.TimelineDirectionLanes!.Select(lane => lane.Buckets)])
            : viewModel.ShowsChannelEndLanes ? (FocusRowKind.ChannelEnds, [.. viewModel.TimelineChannelEndLanes!.Select(end => end.Buckets)])
            : viewModel.ShowsRpcCallLane || viewModel.ShowsHttpExchangeLane
                ? (FocusRowKind.Calls, [MachineBuckets(viewModel, Viewport)])
            : null;
        TimeRange visible = Viewport;
        if (found is not { Rows.Length: > 0 } rows || !rows.Rows.All(buckets => buckets.Count > 0
            && buckets[0].Interval.StartTicks <= visible.StartTicks && buckets[^1].Interval.EndTicks >= visible.EndTicks))
        {
            return null;
        }

        IReadOnlyList<TimelineBucket> first = rows.Rows[0];

        // A group's lanes past the cell budget are counted in fewer, wider columns over the machine row's own span, in the
        // same query (§6.2): they share its span, not its columns, and each row is drawn at its own resolution on the
        // shared rate scale.
        bool coarserLanes = rows.Kind == FocusRowKind.Owners;
        IReadOnlyList<TimelineBucket>? context = viewModel.TimelineDetail is { } detail
            && (MatchingIntervals(first, detail.Buckets) || (coarserLanes && SameSpan(first, detail.Buckets)))
            ? detail.Buckets
            : MatchingIntervals(first, viewModel.Snapshot.Timeline) ? viewModel.Snapshot.Timeline : null;
        return context is null ? null : new(rows.Kind, context, rows.Rows);
    }

    /// <summary>Whether two rows of columns cover exactly one span, whatever their resolutions.</summary>
    private static bool SameSpan(IReadOnlyList<TimelineBucket> left, IReadOnlyList<TimelineBucket> right) =>
        left.Count > 0 && right.Count > 0
        && left[0].Interval.StartTicks == right[0].Interval.StartTicks
        && left[^1].Interval.EndTicks == right[^1].Interval.EndTicks;

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

    /// <summary>Shows the view a moment gone to is centred in (§6.7), and asks for its count at once rather than once it settles.</summary>
    private void OnViewportRequested(object? sender, TimeRange range)
    {
        SetViewport(range);
        RequestDetailNow();
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
        if (observed is not null)
        {
            observed.PropertyChanged -= OnViewModelChanged;
            observed.ViewportRequested -= OnViewportRequested;
        }

        observed = DataContext as WorkspaceViewModel;
        if (observed is not null)
        {
            observed.PropertyChanged += OnViewModelChanged;
            observed.ViewportRequested += OnViewportRequested;
        }

        cardKey = null;

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

    /// <summary>
    /// What of this control its scroller shows, in the control's own coordinates, as layout last reported it; null until
    /// it reports, when every row is drawn.
    /// </summary>
    private Rect? effectiveViewport;

    /// <summary>The lane rows the last repaint drew, by index.</summary>
    private RowBand drawnBand = RowBand.All;

    /// <summary>How many lane rows the last repaint drew: those in view, not every row of a long list.</summary>
    internal int RowsDrawn { get; private set; }

    /// <summary>The first and last lane rows the last repaint could draw, by index; row 0 is a focused rung's machine row.</summary>
    internal (int First, int Last) DrawnRowRange => (drawnBand.First, Math.Min(drawnBand.Last, Math.Max(0, LaneCount - 1)));

    /// <summary>
    /// The lane rows a repaint draws, by index, first to last (§6.2: virtualized rows): every row meeting the view. A group's
    /// lanes can be two hundred rows, of which a view shows a few; drawing them all made a frame three times as long as a
    /// group of a few lanes takes, and its drawing allocate twenty times as much.
    /// </summary>
    private readonly record struct RowBand(int First, int Last)
    {
        /// <summary>Every row: where no view is known, as before the control is laid out in its scroller.</summary>
        public static RowBand All { get; } = new(0, int.MaxValue);

        public bool Holds(int row) => row >= First && row <= Last;
    }

    /// <summary>The rows meeting <paramref name="from"/> to <paramref name="to"/>, in this control's coordinates.</summary>
    private RowBand RowsBetween(double from, double to)
    {
        int count = LaneCount;
        double top = PlotTop;
        double height = (Math.Max(top + 1, Bounds.Height - PlotBottomMargin) - top) / Math.Max(1, count);
        return count == 0 ? RowBand.All
            : new(Math.Clamp((int)Math.Floor((from - top) / height), 0, count - 1),
                Math.Clamp((int)Math.Floor((to - top) / height), 0, count - 1));
    }

    /// <summary>The rows the next repaint draws: those in view, or all of them while no view is known.</summary>
    private RowBand BandToDraw() => effectiveViewport is { Height: > 0 } view ? RowsBetween(view.Top, view.Bottom) : RowBand.All;

    /// <summary>Whether the rows <paramref name="view"/> shows were drawn by the last repaint.</summary>
    private bool DrawnRowsHold(Rect view)
    {
        if (view.Height <= 0 || LaneCount == 0)
        {
            return true;
        }

        RowBand shown = RowsBetween(view.Top, view.Bottom);
        return drawnBand.Holds(shown.First) && drawnBand.Holds(shown.Last);
    }

    /// <summary>A ranked-table or graph selection should reveal its L1 row, not merely outline it off-screen.</summary>
    internal void BringSelectedProcessLaneIntoView()
    {
        if (DataContext is WorkspaceViewModel { SelectedProcess: { } selected })
        {
            BringLaneIntoView(selected.Id);
        }
    }

    /// <summary>Scrolls <paramref name="process"/>'s L1 row into view, as a selection or a search that finds it asks.</summary>
    internal void BringLaneIntoView(ProcessInstanceId process)
    {
        if (FocusRows is not { Kind: FocusRowKind.Owners } rows || DataContext is not WorkspaceViewModel viewModel) return;
        int index = viewModel.ProcessLaneDisplay.ToList().FindIndex(lane => lane.ProcessId == process);
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
        context.DrawLine(GridPen, new(left, bottom), new(right, bottom));
        for (int line = 1; !ShowingLanes && line <= 3; line++)
        {
            double y = top + (plotHeight * line / 4);
            context.DrawLine(RulePen, new(left, y), new(right, y));
        }

        // The overview's coarse buckets stand wherever the viewport's own count has not arrived; the detail replaces
        // them over the interval it answers. Heights are rates, records per tick, so a coarse bucket and a fine one
        // side by side are drawn on one honest scale rather than by counts over unequal widths (§6.2).
        SessionTimelineDetail? detail = viewModel.TimelineDetail is { } answered && Intersects(answered.Interval, visible)
            ? answered : null;
        if (ShowingMechanismLanes && detail is not null && !CoversEveryLane(detail, viewModel.Snapshot.MechanismLanes))
        {
            // A carried or older detail with only some lanes cannot replace an interval for every row or share an
            // honest peak scale with the remaining coarse rows. Keep the complete overview until detail catches up.
            detail = null;
        }
        // The buckets drawn are gathered into buffers this view keeps, so a repaint allocates nothing (R11).
        List<TimelineBucket> coarse = coarseBuckets;
        List<TimelineBucket> fine = fineBuckets;
        coarse.Clear();
        fine.Clear();
        IReadOnlyList<TimelineBucket> overview = viewModel.Snapshot.Timeline;
        for (int index = 0; index < overview.Count; index++)
        {
            TimelineBucket bucket = overview[index];
            if (Intersects(bucket.Interval, visible) && (detail is null || !Inside(bucket.Interval, detail.Interval)))
            {
                coarse.Add(bucket);
            }
        }

        if (detail is not null)
        {
            for (int index = 0; index < detail.Buckets.Count; index++)
            {
                if (Intersects(detail.Buckets[index].Interval, visible))
                {
                    fine.Add(detail.Buckets[index]);
                }
            }
        }

        FocusRowSet? rows = FocusRows;

        // Under a byte ranking the machine rung's lanes, and a group's with the machine row above them, plot bytes per
        // tick, on a scale of their own (§6.2).
        TimelineByteLayer? bytes = ShowingMechanismLanes ? viewModel.TimelineBytes : null;
        ProcessLaneByteLayer? groupBytes = rows?.Kind == FocusRowKind.Owners ? viewModel.ProcessLaneBytes : null;
        DirectionLaneByteLayer? directionBytes = rows?.Kind == FocusRowKind.Directions ? viewModel.DirectionLaneBytes : null;
        bool plotsBytes = bytes is not null || groupBytes is not null || directionBytes is not null;
        double maximumRate = bytes is not null
            ? LaneBytePeak(viewModel.Snapshot.MechanismLanes, bytes, visible)
            : groupBytes is not null
            ? RowsBytePeak(groupBytes.Measures.Machine, groupBytes.Measures.Lanes, groupBytes.Metric, visible)
            : directionBytes is not null
            ? RowsBytePeak(directionBytes.Measures.Machine, directionBytes.Measures.Lanes, directionBytes.Metric, visible)
            : ShowingMechanismLanes
            ? MechanismLanePeak(viewModel.Snapshot.MechanismLanes, detail, visible)
            : rows is not null
                ? FocusRowPeak(viewModel, rows, visible)
                : Math.Max(PeakRate(coarse, visible), PeakRate(fine, visible));
        peakRate = maximumRate;

        // Each lane against its own busiest bar, when a person chose so (§6.2): one peak per row drawn, found as the shared
        // one is found over all of them.
        rowPeakCount = viewModel.ScalesEachLane ? FillRowPeaks(viewModel, rows, detail, visible, bytes, groupBytes, directionBytes) : 0;
        ReadOnlySpan<double> peaks = rowPeaks.AsSpan(0, rowPeakCount);

        // A focused rung draws every record as grey context and its own records in their mechanism's hue on the same
        // rate scale, inside the grey bar of the same interval: when the focus was active, against the machine (§3.2).
        bool focused = viewModel.TimelineShowsFocus;
        var scale = new BarScale(visible, left, plotWidth, top, bottom, maximumRate, focused);
        RowBand band = drawnBand = BandToDraw();
        RowsDrawn = 0;
        if (ShowingMechanismLanes || rows is not null)
        {
            switch (rows?.Kind)
            {
                case null:
                    RowsDrawn = DrawMechanismLanes(context, viewModel, detail, scale, bytes, peaks, band);
                    break;
                case FocusRowKind.Owners:
                    RowsDrawn = DrawProcessLanes(context, viewModel, rows.Context, viewModel.ProcessLaneDisplay, scale, groupBytes,
                        peaks, band);
                    break;
                case FocusRowKind.Directions:
                    RowsDrawn = DrawDirectionLanes(context, viewModel, rows.Context, viewModel.TimelineDirectionLanes!, scale,
                        directionBytes, peaks, band);
                    break;
                case FocusRowKind.ChannelEnds:
                    RowsDrawn = DrawChannelEnds(context, viewModel, rows.Context, viewModel.TimelineChannelEndLanes!, scale, peaks,
                        band);
                    break;
                default:
                    if (viewModel.ShowsHttpExchangeLane)
                    {
                        DrawHttpExchanges(context, viewModel, rows.Context, scale);
                    }
                    else
                    {
                        DrawRpcCalls(context, viewModel, rows.Context, scale);
                    }

                    break;
            }

            if (plotsBytes)
            {
                // The bars are bytes: what they were read from is what the note names, a paused view's newer generation
                // or an earlier publication's standing in until this one's arrive.
                long? generation = groupBytes is not null
                    ? groupBytes.Measures.Generation != viewModel.DisplayedGeneration ? (long?)groupBytes.Measures.Generation : null
                    : directionBytes is not null
                    ? directionBytes.Measures.Generation != viewModel.DisplayedGeneration
                        ? (long?)directionBytes.Measures.Generation : null
                    : bytes!.Overview.Generation != viewModel.DisplayedGeneration ? (long?)bytes.Overview.Generation
                    : bytes.Zoomed is { } zoomed && zoomed.Generation != viewModel.DisplayedGeneration ? zoomed.Generation
                    : null;
                if (generation is { } read)
                {
                    string note = byteGenerationNote.Get(read, static generation =>
                        string.Create(CultureInfo.CurrentCulture, $"bytes from generation {generation:N0}"));
                    DrawText(context, note, new(right - (5.6 * note.Length), top - 18));
                }
            }
            else if (detail is not null && detail.Generation != viewModel.DisplayedGeneration)
            {
                string note = generationNote.Get(detail.Generation, static generation =>
                    string.Create(CultureInfo.CurrentCulture, $"zoomed detail from generation {generation:N0}"));
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
                string note = generationNote.Get(detail.Generation, static generation =>
                    string.Create(CultureInfo.CurrentCulture, $"zoomed detail from generation {generation:N0}"));
                DrawText(context, note, new(right - (5.6 * note.Length), top - 18));
            }
        }

        if (rows is null && focused && viewModel.TimelineFocusBuckets is { } focus)
        {
            DrawFocus(context, focus, scale);
        }

        if (rows is null && !ShowingMechanismLanes && viewModel.TimelineHighlightBuckets is { } highlighted)
        {
            DrawHighlight(context, highlighted, scale);
        }

        if (LiveEdgePlacement is { } live)
        {
            // A preview counts records, so beside byte lanes it is drawn on its own records scale, never the bytes'.
            DrawLiveEdge(context, viewModel, live, rows, top, bottom, plotsBytes ? 0 : maximumRate, plotsBytes,
                plotsBytes ? default : peaks);
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
            // Only the drawn ticks are formatted, and only when their instant or the span changes (§19.4); each is measured,
            // not estimated, so the end's last digit stays in the plot.
            SessionClock clock = viewModel.TimeBase;
            string start = startLabel.Get((visible.StartTicks, visible.SpanTicks, clock), static instant =>
                instant.Item3.Instant(instant.Item1, instant.Item2, CultureInfo.CurrentCulture));
            DrawText(context, start, new(left, bottom + 7));
            AxisStartText = start;
            string end = endLabel.Get((visible.EndTicks, visible.SpanTicks, clock), static instant =>
                instant.Item3.Instant(instant.Item1, instant.Item2, CultureInfo.CurrentCulture));
            double endWidth = Labels.Get(end, 10, TextBrush).Width;
            DrawText(context, end, new(right - endWidth, bottom + 7));

            // Between them, the time base the instants count in, at all times (§6.2): session time, with the wall clock the
            // capture began at where it recorded one, or the wall clock with the days in view and its offset; where there
            // is no room for all of it, its name alone. Session time's is formatted once; the wall clock's as the view moves.
            string timeBase = timeBaseLabel.Get(
                (viewModel.Snapshot.Began, clock, clock.IsWallClock ? (visible.StartTicks, visible.EndTicks) : (0L, 1L)),
                static basis => basis.Item2.Base(new TimeRange(basis.Item3.Item1, basis.Item3.Item2), basis.Item1,
                    CultureInfo.CurrentCulture));
            double from = left + Labels.Get(start, 10, TextBrush).Width + AxisLabelGap;
            double to = right - endWidth - AxisLabelGap;
            TimeBaseText = FittingTimeBase(timeBase, to - from, clock.Name);
            if (TimeBaseText is { } said)
            {
                DrawText(context, said, new(((from + to) / 2) - (Labels.Get(said, 10, TextBrush).Width / 2), bottom + 7));
            }
            // Under each lane's own scale no one rate tops the axis; each lane's own is in its cards (§6.2).
            string peak = rowPeakCount > 0 ? OwnPeaksLabel
                : plotsBytes
                ? byteRateLabel.Get(maximumRate * WorkspaceTime.TicksPerSecond,
                    static rate => WorkspaceRowBuilder.DescribeByteRate(rate))
                : rateLabel.Get(maximumRate * WorkspaceTime.TicksPerSecond, static rate => RateText(rate));
            AxisPeakText = peak;
            DrawText(context, peak, RateLabelBounds(peak).TopLeft);
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

        if (HoveredOperation is { } hoveredCall && CallRect(hoveredCall) is { } callRect)
        {
            using (context.PushOpacity(0.7))
            {
                context.DrawRectangle(Brushes.Transparent, HoverPen, callRect.Inflate(1.5));
            }
        }

        if (HoveredDensityColumn is { } hoveredColumn && DensityShape(viewModel) is { } hoveredDensity
            && DensityColumnSpan(hoveredDensity.Interval, hoveredDensity.Columns, hoveredColumn, scale) is { } columnSpan)
        {
            Rect callRow = LaneRow(1, 2, top, bottom);
            using (context.PushOpacity(0.5))
            {
                context.DrawRectangle(Brushes.Transparent, HoverPen,
                    new Rect(columnSpan.X1 - 1, callRow.Top, Math.Max(2, columnSpan.X2 - columnSpan.X1), callRow.Height));
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

            return LaneBucketAt(viewModel, index, tick);
        }
    }

    /// <summary>
    /// The bucket lane <paramref name="index"/> draws at a tick: a focused rung's row - its machine context first - or a
    /// mechanism's lane at the machine rung, from the zoomed detail where it covers every lane. Null on a call lane, whose
    /// bars are calls.
    /// </summary>
    private TimelineBucket? LaneBucketAt(WorkspaceViewModel viewModel, int index, long tick)
    {
        if (FocusRows is { } rows)
        {
            return rows.Kind == FocusRowKind.Calls && index == 1
                ? null
                : BucketContaining(index == 0 ? rows.Context : rows.Rows[index - 1], tick);
        }

        Mechanism mechanism = viewModel.Snapshot.MechanismLanes[index].Mechanism;
        IReadOnlyList<MechanismTimelineLane> lanes = viewModel.TimelineDetail is { } detail
            && detail.Interval.Contains(tick)
            && CoversEveryLane(detail, viewModel.Snapshot.MechanismLanes)
                ? detail.MechanismLanes : viewModel.Snapshot.MechanismLanes;
        return BucketContaining(LaneOf(lanes, mechanism)?.Buckets, tick);
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
            // A card is described once for what lies under the pointer and redrawn as the pointer moves over it (R11).
            // Any change of the view model's state may change its words, so that forgets it.
            if (HoveredLiveBin is { } liveBin && DataContext is WorkspaceViewModel live)
            {
                Mechanism? laneMechanism = ShowingMechanismLanes && LaneIndexAt(HoverPoint.Y) is { } lane
                    ? live.Snapshot.MechanismLanes[lane].Mechanism : null;
                var liveKey = new CardKey(live, liveBin, laneMechanism, -1, 0, ThemeResources.CurrentMode);
                if (cardKey != liveKey)
                {
                    cardKey = liveKey;
                    card = live.DescribeLiveEdgeHover(liveBin, laneMechanism);
                }

                return card;
            }

            if (HoveredRpcCall is { } call && DataContext is WorkspaceViewModel callModel)
            {
                var callKey = new CardKey(callModel, call, Mechanism.Rpc, 1, 0, ThemeResources.CurrentMode);
                if (cardKey != callKey)
                {
                    cardKey = callKey;
                    card = callModel.DescribeRpcCallHover(call);
                }

                return card;
            }

            if (HoveredRpcDensityColumn is { } column && DataContext is WorkspaceViewModel { RpcCallDensity: { } density } densityModel)
            {
                // The column is told apart by the peak slot, which a density card does not otherwise use.
                var densityKey = new CardKey(densityModel, density, Mechanism.Rpc, 1, column, ThemeResources.CurrentMode);
                if (cardKey != densityKey)
                {
                    cardKey = densityKey;
                    card = densityModel.DescribeRpcDensityHover(column);
                }

                return card;
            }

            if (HoveredHttpExchange is { } exchange && DataContext is WorkspaceViewModel exchangeModel)
            {
                var exchangeKey = new CardKey(exchangeModel, exchange, Mechanism.Http, 1, 0, ThemeResources.CurrentMode);
                if (cardKey != exchangeKey)
                {
                    cardKey = exchangeKey;
                    card = exchangeModel.DescribeHttpExchangeHover(exchange);
                }

                return card;
            }

            if (HoveredHttpDensityColumn is { } httpColumn
                && DataContext is WorkspaceViewModel { HttpExchangeDensity: { } httpDensity } httpModel)
            {
                var httpKey = new CardKey(httpModel, httpDensity, Mechanism.Http, 1, httpColumn, ThemeResources.CurrentMode);
                if (cardKey != httpKey)
                {
                    cardKey = httpKey;
                    card = httpModel.DescribeHttpDensityHover(httpColumn);
                }

                return card;
            }

            if (HoveredBucket is not { } bucket || DataContext is not WorkspaceViewModel viewModel)
                return null;
            int? index = HoveredLaneIndex;
            FocusRowKind? kind = FocusRows?.Kind;
            Mechanism? mechanism = ShowingMechanismLanes && index is { } mechanismIndex
                ? viewModel.Snapshot.MechanismLanes[mechanismIndex].Mechanism : null;
            double peak = index is { } row && row < rowPeakCount ? rowPeaks[row] : peakRate;
            var key = new CardKey(viewModel, bucket, mechanism, index ?? -1, peak, ThemeResources.CurrentMode);
            if (cardKey == key)
            {
                return card;
            }

            ProcessNode? owner = kind == FocusRowKind.Owners && index is > 0 ? OwnerOf(viewModel, index.Value - 1) : null;
            Direction? direction = kind == FocusRowKind.Directions && index is > 0
                ? viewModel.TimelineDirectionLanes![index.Value - 1].Direction : null;
            ChannelEndTimelineLane? end = kind == FocusRowKind.ChannelEnds && index is > 0
                ? viewModel.TimelineChannelEndLanes![index.Value - 1] : null;
            cardKey = key;
            card = viewModel.DescribeTimelineHover(bucket, peak * WorkspaceTime.TicksPerSecond,
                mechanism, owner, direction, end, foldedLane: index is { } hovered && IsFoldedRow(viewModel, kind, hovered));
            return card;
        }
    }

    /// <summary>
    /// The process an owner row stands for. Kept out of <see cref="HoverCard"/>, whose repeated reads must not allocate:
    /// a lambda's captured locals are allocated where the method begins, whether or not the lambda runs.
    /// </summary>
    private static ProcessNode? OwnerOf(WorkspaceViewModel viewModel, int row)
    {
        if (row >= viewModel.ProcessLaneDisplay.Count)
        {
            // The folded lane's row, past every lane of its own.
            return null;
        }

        ProcessInstanceId id = viewModel.ProcessLaneDisplay[row].ProcessId;
        return viewModel.Snapshot.Processes.FirstOrDefault(process => process.Id == id);
    }

    /// <summary>Whether lane row <paramref name="index"/> of a group's rows, row 0 its machine context, is its folded lane.</summary>
    private static bool IsFoldedRow(WorkspaceViewModel viewModel, FocusRowKind? kind, int index) =>
        kind == FocusRowKind.Owners && viewModel.FoldedLane is not null && index > viewModel.ProcessLaneDisplay.Count;

    private CardKey? cardKey;
    private HoverCard? card;
    private WorkspaceViewModel? observed;

    /// <summary>What a card describes: the mark, its row and the scale it was read against, the mark compared by identity.</summary>
    private readonly record struct CardKey(object ViewModel, object Target, Mechanism? Mechanism, int Row, double Peak, ThemeMode Mode)
    {
        public bool Equals(CardKey other) => ReferenceEquals(ViewModel, other.ViewModel) && ReferenceEquals(Target, other.Target)
            && Mechanism == other.Mechanism && Row == other.Row && Peak.Equals(other.Peak) && Mode == other.Mode;

        public override int GetHashCode() => HashCode.Combine(Row, Peak);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => cardKey = null;

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

    /// <summary>Where a bucket of a group's folded lane is drawn, the row after every lane of its own (§6.2: collapse groups).</summary>
    internal Point? PointOfFolded(TimelineBucket bucket) =>
        DataContext is WorkspaceViewModel { FoldedLane: not null } viewModel
            ? RowPoint(FocusRowKind.Owners, viewModel.ProcessLaneDisplay.Count, bucket)
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

    /// <summary>
    /// Where the axis's peak rate is written: at the left of the band above the plot, whose notes stand at its right, so
    /// however wide a rate reads - 10,000,000/s, or a byte rate - it never lies over a bar or a lane's name.
    /// </summary>
    internal static Rect RateLabelBounds(string text)
    {
        Avalonia.Media.TextFormatting.TextLayout layout = Labels.Get(text, 10, TextBrush);
        return new(4, PlotTop - 18, layout.Width, layout.Height);
    }

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
    private static void DrawFocus(DrawingContext context, IReadOnlyList<TimelineBucket> buckets, BarScale scale)
    {
        for (int index = 0; index < buckets.Count; index++)
        {
            TimelineBucket bucket = buckets[index];
            if (bucket.ObservationCount > 0 && Intersects(bucket.Interval, scale.Visible))
            {
                context.DrawRectangle(BrushFor(bucket.DominantMechanism), null, scale.Bar(bucket));
            }
        }
    }

    /// <summary>
    /// The selection's share of each bar (§6.4): a rounded outline in the accent token around the height its own records
    /// would draw on the same scale, §6.6's encoding for selection. It sits just outside the bar's sides, so it reads
    /// against the plot whatever the bar's hue, and its top crosses the bar where the selection's share ends. It adds no
    /// fill, so it never reads as a mechanism's hue; it marks part of a bar, so it is not the selected interval's frame
    /// around a whole one, nor the pointer's full-height outline.
    /// </summary>
    private static void DrawHighlight(DrawingContext context, IReadOnlyList<TimelineBucket> buckets, BarScale scale)
    {
        for (int index = 0; index < buckets.Count; index++)
        {
            TimelineBucket bucket = buckets[index];
            if (bucket.ObservationCount > 0 && Intersects(bucket.Interval, scale.Visible))
            {
                context.DrawRectangle(null, HighlightPen, scale.Bar(bucket).Inflate(1.5), 2, 2);
            }
        }
    }

    /// <summary>The overview's bars from the buffers <see cref="Render"/> gathered, already limited to the viewport.</summary>
    private static void DrawBuckets(
        DrawingContext context, WorkspaceViewModel viewModel, List<TimelineBucket> buckets, BarScale scale)
    {
        double plotHeight = scale.Bottom - scale.Top;
        for (int index = 0; index < buckets.Count; index++)
        {
            TimelineBucket bucket = buckets[index];
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
                    context.DrawRectangle(Brushes.Transparent, SelectionPen, rectangle.Inflate(2));
                }
            }

            if (bucket.Coverage != CoverageState.Covered)
            {
                DrawCoverageGap(context, new(x1, scale.Top, width, plotHeight));
            }
        }
    }

    /// <summary>The most rows a call lane stacks overlapping calls into; any further overlap shares the last row.</summary>
    private const int MaximumCallRows = 6;

    /// <summary>
    /// §6.2's occupied floor: the share of its lane any occupied column keeps, so a lone record or call stays visible and
    /// pointable, and only growth above it tracks magnitude.
    /// </summary>
    private const double OccupiedFloor = 0.42;

    /// <summary>The machine row an RPC call lane stands under: the zoomed detail where it covers the view, else the overview.</summary>
    private static IReadOnlyList<TimelineBucket> MachineBuckets(WorkspaceViewModel viewModel, TimeRange visible) =>
        viewModel.TimelineDetail is { Buckets.Count: > 0 } detail
        && detail.Buckets[0].Interval.StartTicks <= visible.StartTicks
        && detail.Buckets[^1].Interval.EndTicks >= visible.EndTicks
            ? detail.Buckets
            : viewModel.Snapshot.Timeline;

    private (object? Spans, TimeRange Visible, Size Size, double Left, double Width)? callLayoutKey;
    private List<(object Span, Rect Rect)> callLayout = [];

    /// <summary>
    /// Where each call or exchange the lane draws lies: from its start to its stop, at least 2 px, in the first stacked row
    /// it does not overlap. A call open at capture end runs to the view's edge; a call with only one record is a mark at it;
    /// an exchange runs from its first buffer to its response's end, or its last buffer. Computed once per set of spans,
    /// view and size, and read by drawing, hover and clicks alike (R11).
    /// </summary>
    private List<(object Span, Rect Rect)> CallLayout(WorkspaceViewModel viewModel)
    {
        TimeRange visible = Viewport;
        object? source = viewModel.ShowsHttpExchangeLane ? viewModel.HttpExchangeSpans : viewModel.RpcCallSpans;
        var key = (Spans: source, visible, Bounds.Size, PlotLeft, PlotWidth);
        if (callLayoutKey is { } known && ReferenceEquals(known.Spans, key.Spans) && known.Visible == visible
            && known.Size == Bounds.Size && known.Left.Equals(PlotLeft) && known.Width.Equals(PlotWidth))
        {
            return callLayout;
        }

        callLayoutKey = key;
        callLayout = [];
        List<(object Span, long First, long Last)> spans = source switch
        {
            IReadOnlyList<RpcCallSpanView> calls => [.. calls.Select(call => ((object)call, call.FirstTicks,
                call.EndTicks ?? (call.State == RpcCallState.OpenAtCaptureEnd ? long.MaxValue : call.FirstTicks)))],
            IReadOnlyList<HttpExchangeSpanView> exchanges => [.. exchanges.Select(exchange =>
                ((object)exchange, exchange.FirstTicks, exchange.LastTicks))],
            _ => [],
        };
        if (spans.Count == 0)
        {
            return callLayout;
        }

        double bottom = Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin);
        Rect row = LaneRow(1, 2, PlotTop, bottom);
        double laneTop = row.Top + 4;
        double laneHeight = Math.Max(4, row.Height - 10);
        // Rows are packed by time, not pixels: a call shorter than a pixel still shares its row with the calls before it,
        // so a stack always means calls that ran at the same time.
        var placed = new List<(object Span, double X1, double X2, int Row)>(spans.Count);
        var rowEnds = new List<long>();
        foreach ((object span, long first, long last) in spans)
        {
            double x1 = PlotLeft + ViewportMath.PixelAtTick(visible, Math.Clamp(first, visible.StartTicks, visible.EndTicks), PlotWidth);
            double x2 = PlotLeft + ViewportMath.PixelAtTick(visible, Math.Clamp(last, visible.StartTicks, visible.EndTicks), PlotWidth);
            x2 = Math.Max(x2, x1 + 2);
            int index = rowEnds.FindIndex(end => end < first);
            if (index < 0)
            {
                index = rowEnds.Count < MaximumCallRows ? rowEnds.Count : MaximumCallRows - 1;
                if (index == rowEnds.Count) rowEnds.Add(last);
            }

            rowEnds[index] = Math.Max(rowEnds[index], last);
            placed.Add((span, x1, x2, index));
        }

        double pitch = laneHeight / rowEnds.Count;
        // A bar grows with the lane up to 28 px, so a tall window's calls are not specks in an empty band.
        double height = Math.Max(2, Math.Min(28, (pitch * 0.6) - 1));
        foreach ((object span, double x1, double x2, int index) in placed)
        {
            callLayout.Add((span, new Rect(x1, laneTop + (index * pitch) + ((pitch - height) / 2), x2 - x1, height)));
        }

        return callLayout;
    }

    /// <summary>The call or exchange drawn under a point on the lane, the one drawn last where bars overlap.</summary>
    private object? CallAt(Point point)
    {
        if (DataContext is not WorkspaceViewModel viewModel) return null;
        List<(object Span, Rect Rect)> layout = CallLayout(viewModel);
        for (int index = layout.Count - 1; index >= 0; index--)
        {
            if (layout[index].Rect.Inflate(new Thickness(2, 1)).Contains(point)) return layout[index].Span;
        }

        return null;
    }

    /// <summary>Where the lane draws a call or an exchange, if it draws it.</summary>
    private Rect? CallRect(object span)
    {
        if (DataContext is not WorkspaceViewModel viewModel) return null;
        foreach ((object drawn, Rect rect) in CallLayout(viewModel))
        {
            if (ReferenceEquals(drawn, span)) return rect;
        }

        return null;
    }

    /// <summary>Where on the call lane a call is drawn, in this control's coordinates, for pointing at it.</summary>
    internal Point? PointOf(RpcCallSpanView span) => CallRect(span) is { } rect ? rect.Center : null;

    /// <summary>Where on the exchange lane an exchange is drawn, in this control's coordinates, for pointing at it.</summary>
    internal Point? PointOf(HttpExchangeSpanView span) => CallRect(span) is { } rect ? rect.Center : null;

    /// <summary>The density column under a resting pointer, when the call lane draws its calls as density (§6.2).</summary>
    internal int? HoveredRpcDensityColumn => HoverTick is { } tick && FocusRows is { Kind: FocusRowKind.Calls }
        && HoveredLaneIndex == 1
        && DataContext is WorkspaceViewModel { RpcCallDensity: { } density }
        && density.Interval.Contains(tick)
            ? RpcCallDensity.ColumnOf(density.Interval, density.Columns, tick)
            : null;

    /// <summary>The density column under a resting pointer, when the exchange lane draws its exchanges as density.</summary>
    internal int? HoveredHttpDensityColumn => HoverTick is { } tick && FocusRows is { Kind: FocusRowKind.Calls }
        && HoveredLaneIndex == 1
        && DataContext is WorkspaceViewModel { HttpExchangeDensity: { } density }
        && density.Interval.Contains(tick)
            ? RpcCallDensity.ColumnOf(density.Interval, density.Columns, tick)
            : null;

    /// <summary>The density column under a resting pointer on whichever lane draws its spans as density.</summary>
    private int? HoveredDensityColumn => HoveredRpcDensityColumn ?? HoveredHttpDensityColumn;

    /// <summary>The interval and columns of the density the lane draws, an RPC channel's or an HTTP channel's; null when none.</summary>
    private static (TimeRange Interval, int Columns)? DensityShape(WorkspaceViewModel viewModel) =>
        viewModel.RpcCallDensity is { } calls ? (calls.Interval, calls.Columns)
        : viewModel.HttpExchangeDensity is { } exchanges ? (exchanges.Interval, exchanges.Columns)
        : null;

    /// <summary>Where on the lane a density column is drawn, in this control's coordinates, for pointing at it.</summary>
    internal Point? PointOfDensityColumn(int column)
    {
        if (DataContext is not WorkspaceViewModel viewModel || DensityShape(viewModel) is not { } density
            || column < 0 || column >= density.Columns)
        {
            return null;
        }

        TimeRange visible = Viewport;
        TimeRange interval = RpcCallDensity.ColumnIntervalOf(density.Interval, density.Columns, column);
        if (!Intersects(interval, visible))
        {
            return null;
        }

        double x1 = PlotLeft + ViewportMath.PixelAtTick(visible, Math.Max(interval.StartTicks, visible.StartTicks), PlotWidth);
        double x2 = PlotLeft + ViewportMath.PixelAtTick(visible, Math.Min(interval.EndTicks, visible.EndTicks), PlotWidth);
        Rect row = LaneRow(1, 2, PlotTop, Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin));

        // The lower half of the lane, where every occupied column is drawn whatever its height.
        return new Point((x1 + x2) / 2, row.Center.Y + (row.Height / 4));
    }

    /// <summary>The call or exchange under a resting pointer on the lane.</summary>
    private object? HoveredOperation => HoverTick is not null && FocusRows is { Kind: FocusRowKind.Calls }
        && HoveredLaneIndex == 1
            ? CallAt(HoverPoint)
            : null;

    /// <summary>The call under a resting pointer on the call lane.</summary>
    internal RpcCallSpanView? HoveredRpcCall => HoveredOperation as RpcCallSpanView;

    /// <summary>The exchange under a resting pointer on the exchange lane.</summary>
    internal HttpExchangeSpanView? HoveredHttpExchange => HoveredOperation as HttpExchangeSpanView;

    /// <summary>
    /// L3 of an RPC channel: the machine's records as grey context, then the channel's calls as bars from start to stop -
    /// the RPC hue for a success, caution ink for a failure, a faint bar for a call still open at capture end, and a mark
    /// for one whose other record is not in the evidence. The selected call is outlined.
    /// </summary>
    private void DrawRpcCalls(DrawingContext context, WorkspaceViewModel viewModel, IReadOnlyList<TimelineBucket> machine, BarScale scale)
    {
        Rect callRow = DrawOperationFrame(context, viewModel, machine, scale, "Calls", viewModel.RpcCallLaneNote);
        if (viewModel.RpcCallDensity is { } density)
        {
            DrawCallDensity(context, density.Interval, density.Running, density.Failed, density.Maximum,
                BrushFor(Mechanism.Rpc), Current.FailedBrush, callRow, scale);
            return;
        }

        string? selected = viewModel.SelectedRpcCallKey;
        SolidColorBrush success = BrushFor(Mechanism.Rpc);
        foreach ((object drawn, Rect rect) in CallLayout(viewModel))
        {
            var span = (RpcCallSpanView)drawn;
            IBrush fill = span.Failed ? Current.FailedBrush
                : span.State == RpcCallState.Completed ? success
                : span.State == RpcCallState.OpenAtCaptureEnd ? Current.LiveBrush(Mechanism.Rpc)
                : TextBrush;
            context.DrawRectangle(fill, span.Key == selected ? SelectionPen : null, rect);
        }
    }

    /// <summary>
    /// L3 of a process's HTTP exchanges: the machine's records as grey context, then each exchange as a bar from its first
    /// buffer to its response's end - the HTTP hue for one recorded whole, and faint for one any part of which was not, its
    /// response's end included. The selected exchange is outlined.
    /// </summary>
    private void DrawHttpExchanges(
        DrawingContext context, WorkspaceViewModel viewModel, IReadOnlyList<TimelineBucket> machine, BarScale scale)
    {
        Rect exchangeRow = DrawOperationFrame(context, viewModel, machine, scale, "Exchanges", viewModel.HttpExchangeLaneNote);
        if (viewModel.HttpExchangeDensity is { } density)
        {
            DrawCallDensity(context, density.Interval, density.Running, density.Incomplete, density.Maximum,
                BrushFor(Mechanism.Http), Current.LiveBrush(Mechanism.Http), exchangeRow, scale);
            return;
        }

        string? selected = viewModel.SelectedHttpExchangeKey;
        SolidColorBrush whole = BrushFor(Mechanism.Http);
        foreach ((object drawn, Rect rect) in CallLayout(viewModel))
        {
            var exchange = (HttpExchangeSpanView)drawn;
            context.DrawRectangle(exchange.Complete ? whole : Current.LiveBrush(Mechanism.Http),
                exchange.Key == selected ? SelectionPen : null, rect);
        }
    }

    /// <summary>
    /// What an operation lane draws around its marks: the machine's records as grey context above, and the lane's name and
    /// note at its left. Returns the lane's row.
    /// </summary>
    private static Rect DrawOperationFrame(
        DrawingContext context, WorkspaceViewModel viewModel, IReadOnlyList<TimelineBucket> machine, BarScale scale,
        string name, string note)
    {
        Rect machineRow = LaneRow(0, 2, scale.Top, scale.Bottom);
        context.DrawLine(RulePen, new(scale.Left, machineRow.Bottom), new(scale.Left + scale.PlotWidth, machineRow.Bottom));
        DrawText(context, "Machine · all records", new(9, machineRow.Center.Y - 7));
        DrawLaneSeries(context, viewModel, null, machine,
            new BarScale(scale.Visible, scale.Left, scale.PlotWidth, machineRow.Top + 3, machineRow.Bottom - 5,
                scale.MaximumRate, Focused: false),
            machineRow, contextRow: true);

        Rect operationRow = LaneRow(1, 2, scale.Top, scale.Bottom);
        DrawText(context, name, new(9, operationRow.Center.Y - 14));
        DrawText(context, Shortened(note, 24), new(9, operationRow.Center.Y + 1));
        return operationRow;
    }

    /// <summary>
    /// An operation lane with more calls or exchanges in view than it draws one by one (§6.2): a column per share of the
    /// read interval, its height the spans running in it on a log scale above the occupied floor, so a lone one stays
    /// visible, and its flagged share on top in its own brush - an RPC channel's failures in caution ink, an HTTP channel's
    /// exchanges not recorded whole faint - so it never disappears when the view zooms out.
    /// </summary>
    private static void DrawCallDensity(
        DrawingContext context,
        TimeRange interval,
        IReadOnlyList<long> running,
        IReadOnlyList<long> flagged,
        long maximum,
        IBrush fill,
        IBrush flaggedFill,
        Rect row,
        BarScale scale)
    {
        double laneTop = row.Top + 4;
        double laneHeight = Math.Max(4, row.Height - 10);
        double bottom = laneTop + laneHeight;
        double peak = Math.Log2(1 + maximum);
        for (int column = 0; column < running.Count; column++)
        {
            long count = running[column];
            if (count == 0 || DensityColumnSpan(interval, running.Count, column, scale) is not { } span)
            {
                continue;
            }

            double width = Math.Max(1, span.X2 - span.X1 - 1);
            double intensity = peak <= 0 ? 1 : Math.Log2(1 + count) / peak;
            double height = laneHeight * (OccupiedFloor + ((1 - OccupiedFloor) * intensity));

            // The flagged share sits on top, at least 2 px, and the rest beneath it: stacked, never overlaid, so a faint
            // share is faint over the plot rather than lost over the solid fill.
            long share = flagged[column];
            double top = share > 0 ? Math.Min(height, Math.Max(2, height * share / count)) : 0;
            if (height - top > 0)
            {
                context.DrawRectangle(fill, null, new Rect(span.X1, bottom - height + top, width, height - top));
            }

            if (top > 0)
            {
                context.DrawRectangle(flaggedFill, null, new Rect(span.X1, bottom - height, width, top));
            }
        }
    }

    /// <summary>Where a density column lies on the plot, clipped to the view; null when it is outside it.</summary>
    private static (double X1, double X2)? DensityColumnSpan(TimeRange density, int columns, int column, BarScale scale)
    {
        TimeRange interval = RpcCallDensity.ColumnIntervalOf(density, columns, column);
        return Intersects(interval, scale.Visible)
            ? (scale.X(Math.Max(interval.StartTicks, scale.Visible.StartTicks)),
                scale.X(Math.Min(interval.EndTicks, scale.Visible.EndTicks)))
            : null;
    }

    private static Rect LaneRow(int index, int count, double top, double bottom)
    {
        double height = (bottom - top) / Math.Max(1, count);
        return new Rect(0, top + (index * height), 1, height);
    }

    /// <summary>
    /// One L0 row per observed mechanism, all on the same visible rate scale and time columns: records per tick, or under a
    /// byte ranking the bytes it measures per tick, over the counted columns' coverage.
    /// </summary>
    private static int DrawMechanismLanes(
        DrawingContext context, WorkspaceViewModel viewModel, SessionTimelineDetail? detail, BarScale scale,
        TimelineByteLayer? bytes, ReadOnlySpan<double> peaks, RowBand band)
    {
        IReadOnlyList<MechanismTimelineLane> lanes = viewModel.Snapshot.MechanismLanes;
        int drawn = 0;
        for (int index = 0; index < lanes.Count; index++)
        {
            if (!band.Holds(index))
            {
                continue;
            }

            drawn++;
            MechanismTimelineLane lane = lanes[index];
            Rect row = LaneRow(index, lanes.Count, scale.Top, scale.Bottom);
            double baseline = row.Bottom - 5;
            context.DrawLine(RulePen, new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            if (viewModel.SelectedTimelineMechanism == lane.Mechanism)
            {
                context.DrawRectangle(Brushes.Transparent, RowSelectionPen,
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }
            context.DrawRectangle(BrushFor(lane.Mechanism), null, new Rect(8, row.Center.Y - 3, 6, 6));
            DrawText(context, EvidenceRowText.MechanismName(lane.Mechanism), new(19, row.Center.Y - 7));
            if (bytes?.Overview.Of(lane.Mechanism) is { } measured && !PlotsAnything(measured, bytes.Metric))
            {
                // A lane none of whose records in the session carries the ranking's size is empty for that reason, which
                // its name says beneath it, in the ranked table's words, rather than leaving it to look quiet.
                DrawText(context, NothingToPlot(bytes.Metric), new(19, row.Center.Y + 4));
            }
            var laneScale = new BarScale(scale.Visible, scale.Left, scale.PlotWidth,
                row.Top + 3, baseline, PeakOf(peaks, index, scale.MaximumRate), Focused: false);

            // Byte bars come first, so a coverage hatch crosses them as it crosses a record bar. The selection's share is
            // a count of records, which has no height on a byte scale, so bytes draw none.
            bool coverageOnly = bytes is not null;
            if (bytes is not null)
            {
                DrawLaneBytes(context, viewModel, lane.Mechanism, bytes, laneScale, row);
            }

            IReadOnlyList<TimelineBucket> coarse = lane.Buckets;
            MechanismTimelineLane? detailLane = LaneOf(detail?.MechanismLanes, lane.Mechanism);
            if (detail is null || detailLane is null)
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, coarse, laneScale, row, coverageOnly: coverageOnly);
                if (!coverageOnly)
                {
                    DrawLaneHighlight(context, viewModel, lane.Mechanism, laneScale);
                }

                continue;
            }

            double x1 = laneScale.X(Math.Max(detail.Interval.StartTicks, scale.Visible.StartTicks));
            double x2 = laneScale.X(Math.Min(detail.Interval.EndTicks, scale.Visible.EndTicks));
            using (context.PushClip(new Rect(scale.Left, row.Top, Math.Max(0, x1 - scale.Left), row.Height)))
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, coarse, laneScale, row, coverageOnly: coverageOnly);
            }

            using (context.PushClip(new Rect(x2, row.Top, Math.Max(0, scale.Left + scale.PlotWidth - x2), row.Height)))
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, coarse, laneScale, row, coverageOnly: coverageOnly);
            }

            using (context.PushClip(new Rect(x1, row.Top, Math.Max(0, x2 - x1), row.Height)))
            {
                DrawLaneSeries(context, viewModel, lane.Mechanism, detailLane.Buckets, laneScale, row, coverageOnly: coverageOnly);
            }

            if (!coverageOnly)
            {
                DrawLaneHighlight(context, viewModel, lane.Mechanism, laneScale);
            }
        }

        return drawn;
    }

    /// <summary>Whether any of a lane's columns holds a record the metric takes, measured or not.</summary>
    private static bool PlotsAnything(SessionIntervalByteMeasures lane, RankingMetric metric)
    {
        for (int column = 0; column < lane.Columns.Count; column++)
        {
            if (lane.Columns[column].ValueOf(metric) is not (null, _, 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a lane with no record the metric takes says beneath its name: the ranked table's words for it.</summary>
    private static string NothingToPlot(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => "no sends",
        RankingMetric.BytesReceived => "no receives",
        _ => "no transfers",
    };

    /// <summary>
    /// A mechanism lane's bars under a byte ranking: one per byte column the viewport shows, the zoomed view's own columns
    /// where they were read and the overview's elsewhere, as a lane's counted columns are drawn.
    /// </summary>
    private static void DrawLaneBytes(DrawingContext context, WorkspaceViewModel viewModel, Mechanism mechanism,
        TimelineByteLayer bytes, BarScale scale, Rect row)
    {
        SessionIntervalByteMeasures? coarse = bytes.Overview.Of(mechanism);
        SessionIntervalByteMeasures? fine = bytes.Zoomed?.Of(mechanism);
        var hue = new ByteHue(mechanism, null, Context: false);
        if (fine is null || !Intersects(fine.Interval, scale.Visible))
        {
            if (coarse is not null)
            {
                DrawByteColumns(context, viewModel, coarse, bytes.Metric, scale, hue);
            }

            return;
        }

        double x1 = scale.X(Math.Max(fine.Interval.StartTicks, scale.Visible.StartTicks));
        double x2 = scale.X(Math.Min(fine.Interval.EndTicks, scale.Visible.EndTicks));
        if (coarse is not null)
        {
            using (context.PushClip(new Rect(scale.Left, row.Top, Math.Max(0, x1 - scale.Left), row.Height)))
            {
                DrawByteColumns(context, viewModel, coarse, bytes.Metric, scale, hue);
            }

            using (context.PushClip(new Rect(x2, row.Top, Math.Max(0, scale.Left + scale.PlotWidth - x2), row.Height)))
            {
                DrawByteColumns(context, viewModel, coarse, bytes.Metric, scale, hue);
            }
        }

        using (context.PushClip(new Rect(x1, row.Top, Math.Max(0, x2 - x1), row.Height)))
        {
            DrawByteColumns(context, viewModel, fine, bytes.Metric, scale, hue);
        }
    }

    /// <summary>
    /// What a byte column is drawn in: its lane's mechanism; for a process lane, the hue its records' most frequent
    /// mechanism gives the counted bucket around it, as the records view draws it; for the machine row, the context grey.
    /// </summary>
    private readonly record struct ByteHue(Mechanism? Fixed, IReadOnlyList<TimelineBucket>? Counted, bool Context)
    {
        public SolidColorBrush Fill(long tick) => Context ? ContextBarBrush : BrushFor(MechanismAt(tick));

        public Pen Hatch(long tick) => Context ? Current.ContextHatchPen : Current.HatchPen(MechanismAt(tick));

        /// <summary>The lane's mechanism, or the most frequent mechanism of the counted bucket holding the tick.</summary>
        private Mechanism MechanismAt(long tick)
        {
            if (Fixed is { } mechanism)
            {
                return mechanism;
            }

            // The counted buckets are in time order, so the one holding the tick is found by halving (R11: no allocation).
            IReadOnlyList<TimelineBucket>? buckets = Counted;
            int low = 0;
            int high = (buckets?.Count ?? 0) - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                TimeRange interval = buckets![middle].Interval;
                if (tick < interval.StartTicks) high = middle - 1;
                else if (tick >= interval.EndTicks) low = middle + 1;
                else return buckets[middle].DominantMechanism;
            }

            return Mechanism.Tcp;
        }
    }

    /// <summary>
    /// One lane's byte columns in the viewport. A measured sum is a bar of its rate on the shared scale, never under the
    /// lane's occupied floor, so a small transfer stays visible and pointable. A column whose records declared sizes none of
    /// them recorded is §6.6's open cross-hatched cell at that floor: unknown, never a bar of zero (R3). A column with
    /// nothing measured to plot draws nothing.
    /// </summary>
    private static void DrawByteColumns(DrawingContext context, WorkspaceViewModel viewModel,
        SessionIntervalByteMeasures lane, RankingMetric metric, BarScale scale, ByteHue hue)
    {
        double plot = scale.Bottom - scale.Top;
        double floor = plot * OccupiedFloor;
        for (int column = 0; column < lane.Columns.Count; column++)
        {
            TimeRange interval = lane.IntervalOf(column);
            if (!Intersects(interval, scale.Visible))
            {
                continue;
            }

            (long? value, _, long unmeasured) = lane.Columns[column].ValueOf(metric);
            if (value is not > 0 && (value is not null || unmeasured == 0))
            {
                continue;
            }

            double x1 = scale.X(Math.Max(interval.StartTicks, scale.Visible.StartTicks));
            double x2 = scale.X(Math.Min(interval.EndTicks, scale.Visible.EndTicks));
            double width = Math.Max(1, x2 - x1 - 2);
            Rect bar;
            long middle = interval.StartTicks + (interval.SpanTicks / 2);
            if (value is { } sum)
            {
                double height = Math.Max(floor,
                    plot * ((double)sum / interval.SpanTicks) / Math.Max(double.Epsilon, scale.MaximumRate));
                bar = new(x1, scale.Bottom - height, width, height);
                context.DrawRectangle(hue.Fill(middle), null, bar);
            }
            else
            {
                bar = new(x1, scale.Bottom - floor, width, floor);
                Pen pen = hue.Hatch(middle);
                context.DrawRectangle(null, pen, bar);
                GraphView.CrossHatch(context, pen, bar);
            }

            if (viewModel.SelectedInterval == interval)
            {
                context.DrawRectangle(Brushes.Transparent, SelectionPen, bar.Inflate(1));
            }
        }
    }

    /// <summary>A mechanism lane's share of the selection's highlight, when the selection holds records of that mechanism.</summary>
    private static void DrawLaneHighlight(DrawingContext context, WorkspaceViewModel viewModel, Mechanism mechanism, BarScale scale)
    {
        if (LaneOf(viewModel.TimelineHighlightLanes, mechanism) is { } marked)
        {
            DrawHighlight(context, marked.Buckets, scale);
        }
    }

    /// <summary>
    /// L1's exact owner rows, with one non-additive machine context row above them: records per tick, or under a byte
    /// ranking the bytes it measures per tick, over the counted rows' coverage.
    /// </summary>
    private static int DrawProcessLanes(DrawingContext context, WorkspaceViewModel viewModel,
        IReadOnlyList<TimelineBucket> machine, IReadOnlyList<ProcessTimelineLane> lanes, BarScale scale,
        ProcessLaneByteLayer? bytes, ReadOnlySpan<double> peaks, RowBand band)
    {
        FoldedProcessLane? folded = viewModel.FoldedLane;
        int count = lanes.Count + 1 + (folded is null ? 0 : 1);
        bool coverageOnly = bytes is not null;
        int drawn = 0;
        for (int index = 0; index < count; index++)
        {
            if (!band.Holds(index))
            {
                continue;
            }

            drawn++;
            Rect row = LaneRow(index, count, scale.Top, scale.Bottom);
            context.DrawLine(RulePen, new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            var rowScale = new BarScale(scale.Visible, scale.Left, scale.PlotWidth,
                row.Top + 3, row.Bottom - 5, PeakOf(peaks, index, scale.MaximumRate), Focused: false);
            if (index == 0)
            {
                DrawText(context, bytes is null ? "Machine · all records" : MachineBytesLabel(bytes.Metric), new(9, row.Center.Y - 7));
                if (bytes is not null)
                {
                    // Byte bars first, so the coverage hatch crosses them as it crosses a record bar.
                    DrawByteColumns(context, viewModel, bytes.Measures.Machine, bytes.Metric, rowScale,
                        new ByteHue(null, null, Context: true));
                }

                DrawLaneSeries(context, viewModel, null,
                    machine, rowScale, row, contextRow: true, coverageOnly: coverageOnly);
                continue;
            }

            if (index > lanes.Count && folded is not null)
            {
                // The members past the lanes drawn, together beneath them (§6.2: collapse groups), named by how many.
                DrawText(context, viewModel.FoldedLaneLabel, new(9, row.Center.Y - 7));
                if (bytes is not null)
                {
                    // A byte ranking measures each lane's own process; the folded members' bytes are summed in no lane.
                    DrawText(context, "Bytes not plotted for folded members", new(9, row.Center.Y + 4));
                }

                DrawLaneSeries(context, viewModel, null, folded.Buckets, rowScale, row, coverageOnly: coverageOnly);
                continue;
            }

            ProcessTimelineLane lane = lanes[index - 1];
            string label = RowLabel(lane, 0, viewModel, static (row, _, model) => OwnerLabel((ProcessTimelineLane)row, model));
            if (viewModel.SelectedProcess?.Id == lane.ProcessId)
            {
                context.DrawRectangle(Brushes.Transparent, RowSelectionPen,
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }

            if (viewModel.IsLaneSearched(lane.ProcessId))
            {
                // A bar at the gutter's edge marks a lane the search typed finds (§6.2: search lane names).
                context.DrawRectangle(MatchBrush, null, SearchMark(row));
            }

            DrawText(context, label, new(9, row.Center.Y - 7));
            if (viewModel.IsLanePinned(lane.ProcessId))
            {
                // A pin's head just after the name, as the graph marks a pinned node (§6.2).
                Point head = PinHead(row, Labels.Get(label, 10, TextBrush).Width, scale.Left);
                context.DrawEllipse(SelectedBrush, PinHeadPen, head, PinHeadRadius, PinHeadRadius);
            }

            if (bytes?.Measures.Of(lane.ProcessId) is { } measured)
            {
                DrawByteColumns(context, viewModel, measured, bytes.Metric, rowScale, new ByteHue(null, lane.Buckets, Context: false));
                if (!PlotsAnything(measured, bytes.Metric))
                {
                    // A process none of whose records here carries the ranking's size is empty for that reason.
                    DrawText(context, NothingToPlot(bytes.Metric), new(9, row.Center.Y + 4));
                }
            }

            DrawLaneSeries(context, viewModel, null, lane.Buckets, rowScale, row, coverageOnly: coverageOnly);
        }

        return drawn;
    }

    /// <summary>Where the mark of a lane a search finds is drawn: a bar at the gutter's edge, clear of the name and its outline.</summary>
    internal static Rect SearchMark(Rect row) => new(0, row.Top + 3, 2.5, Math.Max(1, row.Height - 6));

    /// <summary>The radius of a pinned lane's pin head, a pinned graph node's.</summary>
    private const double PinHeadRadius = 3.5;

    /// <summary>
    /// Where a pinned lane's pin head is drawn: just after its row's name, level with it, and short of the plot however
    /// long the name runs.
    /// </summary>
    private static Point PinHead(Rect row, double nameWidth, double plotLeft) =>
        new(Math.Min(9 + nameWidth + 9, plotLeft - 10), row.Center.Y);

    /// <summary>Where lane row <paramref name="row"/>'s pin head is drawn while it is pinned; row 0 is the machine context.</summary>
    internal Point LanePinHead(int row)
    {
        var viewModel = (WorkspaceViewModel)DataContext!;
        string label = OwnerLabel(viewModel.ProcessLaneDisplay[row - 1], viewModel);
        return PinHead(RowBounds(row), Labels.Get(label, 10, TextBrush).Width, PlotLeft);
    }

    /// <summary>The machine row's name above a group's byte lanes: every record's bytes of the ranking's kind.</summary>
    private static string MachineBytesLabel(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => "Machine · all sends",
        RankingMetric.BytesReceived => "Machine · all receives",
        _ => "Machine · all transfers",
    };

    /// <summary>
    /// L2's exact source-direction partition; the machine row is context, never added to the owner total. Under a byte
    /// ranking each row plots the bytes it measures per tick, over the counted rows' coverage.
    /// </summary>
    private static int DrawDirectionLanes(DrawingContext context, WorkspaceViewModel viewModel,
        IReadOnlyList<TimelineBucket> machine, IReadOnlyList<DirectionTimelineLane> lanes, BarScale scale,
        DirectionLaneByteLayer? bytes, ReadOnlySpan<double> peaks, RowBand band)
    {
        int count = lanes.Count + 1;
        bool coverageOnly = bytes is not null;
        int drawn = 0;
        for (int index = 0; index < count; index++)
        {
            if (!band.Holds(index))
            {
                continue;
            }

            drawn++;
            Rect row = LaneRow(index, count, scale.Top, scale.Bottom);
            context.DrawLine(RulePen, new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            var rowScale = new BarScale(scale.Visible, scale.Left, scale.PlotWidth,
                row.Top + 3, row.Bottom - 5, PeakOf(peaks, index, scale.MaximumRate), Focused: false);
            if (index == 0)
            {
                DrawText(context, bytes is null ? "Machine · all records" : MachineBytesLabel(bytes.Metric), new(9, row.Center.Y - 7));
                if (bytes is not null)
                {
                    // Byte bars first, so the coverage hatch crosses them as it crosses a record bar.
                    DrawByteColumns(context, viewModel, bytes.Measures.Machine, bytes.Metric, rowScale,
                        new ByteHue(null, null, Context: true));
                }

                DrawLaneSeries(context, viewModel, null,
                    machine, rowScale, row, contextRow: true, coverageOnly: coverageOnly);
                continue;
            }

            DirectionTimelineLane lane = lanes[index - 1];

            // A row with nothing drawn still carries the capture's hatch wherever its coverage was not complete; naming
            // it empty keeps that hatch from being read as records, and says the source reported no record of this
            // direction here.
            bool none = !AnyObserved(lane.Buckets, scale.Visible);
            string label = DirectionLabels.TryGetValue((lane.Direction, none), out string? known) ? known
                : DirectionLabels[(lane.Direction, none)] = WorkspaceViewModel.DirectionLabel(lane.Direction) + (none ? " · none" : string.Empty);
            if (viewModel.SelectedTimelineDirection == lane.Direction)
            {
                context.DrawRectangle(Brushes.Transparent, RowSelectionPen,
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }

            DrawText(context, label, new(9, row.Center.Y - 7));
            if (bytes?.Measures.Of(lane.Direction) is { } measured)
            {
                DrawByteColumns(context, viewModel, measured, bytes.Metric, rowScale, new ByteHue(null, lane.Buckets, Context: false));
                if (!none && !PlotsAnything(measured, bytes.Metric))
                {
                    // A direction none of whose records here carries the ranking's size is empty for that reason.
                    DrawText(context, NothingToPlot(bytes.Metric), new(9, row.Center.Y + 4));
                }
            }

            DrawLaneSeries(context, viewModel, null, lane.Buckets, rowScale, row, coverageOnly: coverageOnly);
        }

        return drawn;
    }

    /// <summary>
    /// The live edge (§12, §19.3): the broker's preview counts for the chunks not yet published here, beyond a dashed rule
    /// at the published end, at half strength so they never read as published bars. At L0 each lane shows its own
    /// mechanism; a focused rung shows them only in its machine row, because a preview is not attributed to processes or
    /// channels yet. Heights share the published rate scale, and a bar above it is drawn full height rather than rescaling
    /// the published timeline every quarter second.
    /// </summary>
    private void DrawLiveEdge(DrawingContext context, WorkspaceViewModel viewModel, LiveEdgeArea live,
        FocusRowSet? rows, double top, double bottom, double maximumRate, bool besideBytes, ReadOnlySpan<double> peaks)
    {
        double scaleRate = maximumRate > 0 ? maximumRate : LivePeak(live.Edge.Bins);
        double ruleX = live.Left - (LiveEdgeGap / 2);
        DrawDashedRule(context, TextPen, ruleX, top, bottom);
        if (ShowingMechanismLanes)
        {
            // A mechanism first seen after the last publication has no lane until it publishes; the label names it, and
            // hovering the edge on any lane lists it, so no previewed record is silently undrawn. Beside byte lanes it
            // says it counts records.
            IReadOnlyList<MechanismTimelineLane> lanes = viewModel.Snapshot.MechanismLanes;
            DrawText(context, liveLabel.Get((live.Edge, lanes, besideBytes),
                    static key => LiveLabel(key.Item1, key.Item2, key.Item3)),
                new(live.Left, top - 16));
            for (int index = 0; index < lanes.Count; index++)
            {
                Rect row = LaneRow(index, lanes.Count, top, bottom);
                // Each lane's own scale, where each lane has one and it published a record in view (§6.2).
                double laneRate = index < peaks.Length && peaks[index] > 0 ? peaks[index] : scaleRate;
                DrawLiveBars(context, live, row.Top + 3, row.Bottom - 5, laneRate, OccupiedFloor, LiveBars.Lane, lanes[index].Mechanism);
            }
        }
        else if (rows is not null)
        {
            DrawText(context, besideBytes ? "live records" : "live", new(live.Left, top - 16));
            Rect machine = LaneRow(0, rows.Rows.Count + 1, top, bottom);
            double machineRate = peaks.Length > 0 && peaks[0] > 0 ? peaks[0] : scaleRate;
            DrawLiveBars(context, live, machine.Top + 3, machine.Bottom - 5, machineRate, OccupiedFloor, LiveBars.Machine, default);
        }
        else
        {
            DrawText(context, "live", new(live.Left, top - 16));
            DrawLiveBars(context, live, top, bottom, scaleRate, 0, LiveBars.Dominant, default);
        }
    }

    /// <summary>
    /// The live edge's label at L0: it names a mechanism first seen after the last publication, which has no lane until it
    /// publishes, so no previewed record is silently undrawn. Formatted only when the preview or the lanes change.
    /// </summary>
    private static string LiveLabel(LiveEdge edge, IReadOnlyList<MechanismTimelineLane> lanes, bool records)
    {
        Mechanism[] unlaned = [.. edge.Bins.SelectMany(bin => bin.Counts.Select(count => count.Mechanism)).Distinct()
            .Where(mechanism => lanes.All(lane => lane.Mechanism != mechanism)).Order()];
        string live = records ? "live records" : "live";
        return unlaned.Length switch
        {
            0 => live,
            1 => $"{live} · +{EvidenceRowText.MechanismName(unlaned[0])}",
            _ => $"{live} · +{unlaned.Length} mechanisms",
        };
    }

    /// <summary>The preview's own peak rate, the scale it is drawn on while nothing published is visible.</summary>
    private static double LivePeak(IReadOnlyList<LiveEdgeBin> bins)
    {
        double peak = 0;
        for (int index = 0; index < bins.Count; index++)
        {
            peak = Math.Max(peak, (double)bins[index].Total / Math.Max(1, bins[index].Interval.SpanTicks));
        }

        return peak;
    }

    /// <summary>What a row of preview bars counts and in which hue.</summary>
    private enum LiveBars
    {
        /// <summary>An L0 lane: its own mechanism's records, in its hue.</summary>
        Lane,

        /// <summary>A focused rung's machine row: every record, in the context grey.</summary>
        Machine,

        /// <summary>The aggregate plot: every record, in the bin's first mechanism's hue.</summary>
        Dominant,
    }

    /// <summary>One row of preview bars.</summary>
    private static void DrawLiveBars(DrawingContext context, LiveEdgeArea live, double top, double bottom, double scaleRate,
        double floor, LiveBars bars, Mechanism lane)
    {
        double height = Math.Max(1, bottom - top);
        IReadOnlyList<LiveEdgeBin> bins = live.Edge.Bins;
        for (int index = 0; index < bins.Count; index++)
        {
            LiveEdgeBin bin = bins[index];
            (int count, Mechanism? hue) = bars switch
            {
                LiveBars.Lane => (bin.CountOf(lane), lane),
                LiveBars.Machine => (bin.Total, (Mechanism?)null),
                _ => (bin.Total, bin.Counts[0].Mechanism),
            };
            if (count == 0)
            {
                continue;
            }

            (double x1, double x2) = live.Columns(bin);
            double rate = (double)count / Math.Max(1, bin.Interval.SpanTicks);
            double bar = Math.Clamp(height * rate / Math.Max(double.Epsilon, scaleRate), Math.Min(height, Math.Max(3, height * floor)), height);
            IBrush fill = hue is { } mechanism ? Current.LiveBrush(mechanism) : Current.LiveContextBrush;
            context.DrawRectangle(fill, null, new Rect(x1, bottom - bar, Math.Max(1, x2 - x1 - 1), bar));
        }
    }

    /// <summary>
    /// A direction band's bar: its rate's share of the half-lane on the shared scale, never under §6.2's occupied floor
    /// of 0.42 of the band, so a lone record in a quiet end stays visible and pointable.
    /// </summary>
    private static double BandHeight(TimelineBucket bucket, double half, double maximumRate) =>
        Math.Min(half, Math.Max(half * OccupiedFloor, half * Rate(bucket) / Math.Max(double.Epsilon, maximumRate)));

    /// <summary>
    /// L3's two ends under the machine row (§3.2): outbound records rise above each end's midline and inbound records fall
    /// below it, on the shared rate scale, so the channel reads as a conversation. A record with no data direction, such
    /// as a disconnect, is a neutral mark on the midline rather than a bar on either side.
    /// </summary>
    private static int DrawChannelEnds(DrawingContext context, WorkspaceViewModel viewModel,
        IReadOnlyList<TimelineBucket> machine, IReadOnlyList<ChannelEndTimelineLane> ends, BarScale scale,
        ReadOnlySpan<double> peaks, RowBand band)
    {
        int count = ends.Count + 1;
        int rows = 0;
        for (int index = 0; index < count; index++)
        {
            if (!band.Holds(index))
            {
                continue;
            }

            rows++;
            Rect row = LaneRow(index, count, scale.Top, scale.Bottom);
            context.DrawLine(RulePen, new(scale.Left, row.Bottom),
                new(scale.Left + scale.PlotWidth, row.Bottom));
            if (index == 0)
            {
                DrawText(context, "Machine · all records", new(9, row.Center.Y - 7));
                DrawLaneSeries(context, viewModel, null,
                    machine,
                    new BarScale(scale.Visible, scale.Left, scale.PlotWidth, row.Top + 3, row.Bottom - 5,
                        PeakOf(peaks, 0, scale.MaximumRate), Focused: false),
                    row, contextRow: true);
                continue;
            }

            ChannelEndTimelineLane end = ends[index - 1];
            if (viewModel.SelectedChannelEnd == end.End)
            {
                context.DrawRectangle(Brushes.Transparent, RowSelectionPen,
                    new Rect(3, row.Top + 1, scale.Left - 7, Math.Max(1, row.Height - 2)));
            }

            // Two lines: who holds the end, then the end's own endpoint, which tells a looped process's ends apart.
            DrawText(context, RowLabel(end, 0, viewModel, static (row, _, model) =>
                Shortened(model.ChannelEndHolder((ChannelEndTimelineLane)row), 24)), new(9, row.Center.Y - 14));
            DrawText(context, RowLabel(end, 1, viewModel, static (row, _, _) =>
                EndpointText.Abbreviated(((ChannelEndTimelineLane)row).Endpoint, 24)), new(9, row.Center.Y + 1));

            // The bands keep clear of the coverage strip along the row's foot; the arrows name their sides in place.
            double bandTop = row.Top + 3;
            double middle = (bandTop + row.Bottom - 6) / 2;
            double half = Math.Max(1, middle - bandTop - 1);
            double endRate = PeakOf(peaks, index, scale.MaximumRate);
            context.DrawLine(RulePen, new(scale.Left, middle), new(scale.Left + scale.PlotWidth, middle));
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
                    double height = BandHeight(sent, half, endRate);
                    Rect bar = new(x1, middle - 1 - height, width, height);
                    context.DrawRectangle(BrushFor(sent.DominantMechanism), null, bar);
                    drawn = bar;
                }

                if (end.Inbound[bucketIndex] is { ObservationCount: > 0 } received)
                {
                    Rect bar = new(x1, middle + 1, width, BandHeight(received, half, endRate));
                    context.DrawRectangle(BrushFor(received.DominantMechanism), null, bar);
                    drawn = drawn is { } upper ? upper.Union(bar) : bar;
                }

                if (total.ObservationCount - end.Outbound[bucketIndex].ObservationCount
                    - end.Inbound[bucketIndex].ObservationCount > 0)
                {
                    Rect mark = new(x1, middle - 2.5, width, 5);
                    context.DrawRectangle(ContextBarBrush, MarkPen, mark);
                    drawn = drawn is { } union ? union.Union(mark) : mark;
                }

                if (drawn is { } selectedBar && viewModel.SelectedInterval == total.Interval)
                {
                    context.DrawRectangle(Brushes.Transparent, SelectionPen, selectedBar.Inflate(1));
                }

                if (total.Coverage != CoverageState.Covered)
                {
                    DrawCoverageGap(context, total.Coverage == CoverageState.UnknownCoverage
                        ? new Rect(x1, row.Bottom - 5, width, 5)
                        : new Rect(x1, row.Top, width, row.Height));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// One row's bars and coverage for the buckets of <paramref name="buckets"/> that the viewport shows; with
    /// <paramref name="coverageOnly"/>, only their coverage, beneath bars that plot something other than their records.
    /// </summary>
    private static void DrawLaneSeries(DrawingContext context, WorkspaceViewModel viewModel,
        Mechanism? mechanism, IReadOnlyList<TimelineBucket> buckets, BarScale scale, Rect row,
        bool contextRow = false, bool coverageOnly = false)
    {
        for (int index = 0; index < buckets.Count; index++)
        {
            TimelineBucket bucket = buckets[index];
            if (!Intersects(bucket.Interval, scale.Visible))
            {
                continue;
            }

            double x1 = scale.X(Math.Max(bucket.Interval.StartTicks, scale.Visible.StartTicks));
            double x2 = scale.X(Math.Min(bucket.Interval.EndTicks, scale.Visible.EndTicks));
            double width = Math.Max(1, x2 - x1 - 2);
            if (bucket.ObservationCount > 0 && !coverageOnly)
            {
                Rect measured = scale.Bar(bucket);
                double height = Math.Max(measured.Height, (scale.Bottom - scale.Top) * OccupiedFloor);
                Rect bar = new(measured.X, scale.Bottom - height, measured.Width, height);
                context.DrawRectangle(contextRow ? ContextBarBrush : BrushFor(mechanism ?? bucket.DominantMechanism),
                    null, bar);
                if (viewModel.SelectedInterval == bucket.Interval)
                {
                    context.DrawRectangle(Brushes.Transparent, SelectionPen, bar.Inflate(1));
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
        IBrush dim = Current.OutsideBrush;
        context.DrawRectangle(dim, null, new Rect(left, top, Math.Max(0, x1 - left), bottom - top));
        context.DrawRectangle(dim, null, new Rect(x2, top, Math.Max(0, left + plotWidth - x2), bottom - top));
        Pen edge = SelectionPen;
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

        Pen pen = TextPen;
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
            context.DrawLine(SelectionPen, new(x, top), new(x, bottom));
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
        Pen pen = Current.GapPen;
        context.DrawRectangle(Brushes.Transparent, pen, cell);
        using (context.PushClip(cell))
        {
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
        if (DataContext is not WorkspaceViewModel viewModel || !clickedLast)
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
                case FocusRowKind.Owners when laneIndex > viewModel.ProcessLaneDisplay.Count:
                    // The folded lane's name selects no process: it stands for many.
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
                case FocusRowKind.Calls:
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
        pressY = point.Position.Y;
        pressedCall = FocusRows is { Kind: FocusRowKind.Calls } && LaneIndexAt(point.Position.Y) == 1
            ? CallAt(point.Position) : null;
        brushing = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || point.Properties.IsMiddleButtonPressed;
        moved = false;
        panOrigin = Viewport;
        brushAnchor = TickAt(pressX);
        brushEnd = null;
        pressPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>
    /// Cancels the press or drag in progress, as Esc does (§6.7): a pan returns to the view it began from, a brush being
    /// drawn is dropped, and the press selects nothing when released. False when no press is in progress.
    /// </summary>
    internal bool CancelDrag()
    {
        if (brushAnchor is null)
        {
            return false;
        }

        bool panned = moved && !brushing;
        brushAnchor = null;
        brushEnd = null;
        pressedCall = null;
        clickedLast = false;
        IPointer? pointer = pressPointer;
        pressPointer = null;
        pointer?.Capture(null);
        if (panned)
        {
            SetViewport(panOrigin);
        }

        InvalidateVisual();
        return true;
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
        clickedLast = wasClick;
        brushAnchor = null;
        brushEnd = null;
        pressPointer = null;
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
        else if (wasClick && pressedCall is { } call)
        {
            // A call or an exchange is an operation, not an interval: a click on one selects its row, where its records are
            // one step away.
            _ = call switch
            {
                RpcCallSpanView rpc => viewModel.SelectRpcCall(rpc.Key),
                HttpExchangeSpanView http => viewModel.SelectHttpExchange(http.Key),
                _ => false,
            };
        }
        else if (wasClick && FocusRows is { Kind: FocusRowKind.Calls } && LaneIndexAt(pressY) == 1)
        {
            // A density column is an interval, which a click selects as it would a bar's; between drawn spans there is
            // nothing to select.
            if (DensityShape(viewModel) is { } density && density.Interval.Contains(anchor))
            {
                viewModel.SelectInterval(RpcCallDensity.ColumnIntervalOf(density.Interval, density.Columns,
                    RpcCallDensity.ColumnOf(density.Interval, density.Columns, anchor)));
            }
        }
        else if (wasClick && LaneIndexAt(pressY) is { } lane && LaneBucketAt(viewModel, lane, anchor) is { } cell)
        {
            // A click chooses the cell it landed on - its lane's own, which a group's lanes counted coarser than the view
            // hold wider than the machine column beneath - and the inspector explains it (§6.8, R13).
            ChooseCell(viewModel, lane, cell);
        }
        else if (wasClick && BucketAt(viewModel, anchor) is { } bucket)
        {
            viewModel.ChooseTimelineCell(bucket);
        }

        pressedCall = null;

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Chooses a lane's cell by the lane it lies in, as its hover card names the lane.</summary>
    private void ChooseCell(WorkspaceViewModel viewModel, int index, TimelineBucket cell)
    {
        FocusRowKind? kind = FocusRows?.Kind;
        viewModel.ChooseTimelineCell(cell,
            ShowingMechanismLanes ? viewModel.Snapshot.MechanismLanes[index].Mechanism : null,
            kind == FocusRowKind.Owners && index > 0 ? OwnerOf(viewModel, index - 1) : null,
            kind == FocusRowKind.Directions && index > 0 ? viewModel.TimelineDirectionLanes![index - 1].Direction : null,
            kind == FocusRowKind.ChannelEnds && index > 0 ? viewModel.TimelineChannelEndLanes![index - 1] : null,
            foldedLane: IsFoldedRow(viewModel, kind, index));
    }

    /// <summary>The finest bucket drawn at a tick: the zoomed detail where it has arrived, else the overview's.</summary>
    private static TimelineBucket? BucketAt(WorkspaceViewModel viewModel, long tick) =>
        BucketContaining(viewModel.TimelineDetail is { } detail && detail.Interval.Contains(tick)
            ? detail.Buckets
            : viewModel.Snapshot.Timeline, tick);

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
            IReadOnlyList<LiveEdgeBin> bins = live.Edge.Bins;
            for (int index = 0; index < bins.Count; index++)
            {
                LiveEdgeBin bin = bins[index];
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

        // Lost mid-gesture, the press ended as no click; a release lets its capture go only once it has recorded its end.
        if (brushAnchor is not null)
        {
            clickedLast = false;
        }

        brushAnchor = null;
        brushEnd = null;
        pressPointer = null;
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

    /// <summary>The last string formatted for a key: a repaint with the same key reuses it rather than formatting anew.</summary>
    private sealed class Memo<TKey>
    {
        private TKey? key;
        private string? text;

        public string Get(TKey value, Func<TKey, string> format)
        {
            if (text is null || !EqualityComparer<TKey>.Default.Equals(key, value))
            {
                key = value;
                text = format(value);
            }

            return text;
        }
    }

    /// <summary>Rows are compared by identity: two rows with equal values are still two rows with their own labels.</summary>
    private sealed class RowKeyComparer : IEqualityComparer<(object Row, int Part)>
    {
        public static readonly RowKeyComparer Instance = new();

        public bool Equals((object Row, int Part) x, (object Row, int Part) y) =>
            ReferenceEquals(x.Row, y.Row) && x.Part == y.Part;

        public int GetHashCode((object Row, int Part) obj) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Row), obj.Part);
    }

    /// <summary>Part <paramref name="part"/> of a row's label, formatted the first time that row is drawn.</summary>
    private static string RowLabel(object row, int part, WorkspaceViewModel viewModel,
        Func<object, int, WorkspaceViewModel, string> format)
    {
        if (!RowLabels.TryGetValue((row, part), out string? label))
        {
            if (RowLabels.Count > 512)
            {
                RowLabels.Clear();
            }

            label = format(row, part, viewModel);
            RowLabels[(row, part)] = label;
        }

        return label;
    }

    /// <summary>
    /// An owner row's name: its executable and PID, a reused PID's holder numbered as the ranked table numbers it, or its
    /// instance when the process left no name.
    /// </summary>
    internal static string OwnerLabel(ProcessTimelineLane lane, WorkspaceViewModel viewModel)
    {
        ProcessNode? process = viewModel.Snapshot.Processes.FirstOrDefault(node => node.Id == lane.ProcessId);
        string name = process?.Name ?? "Process";
        string shortName = Shortened(name, 14);
        return process is null ? $"{shortName} · {lane.ProcessId.Value.ToString("N")[..6]}"
            : name == ProcessNode.PidName(process.ProcessId) ? process.PidLabel
            : $"{shortName} · {process.PidLabel}";
    }

    /// <summary><paramref name="text"/>, cut to <paramref name="length"/> characters with an ellipsis when longer.</summary>
    private static string Shortened(string text, int length) => text.Length > length ? text[..(length - 1)] + "…" : text;

    /// <summary>Whether a detail answers every overview lane, so it may replace their columns and share their scale.</summary>
    private static bool CoversEveryLane(SessionTimelineDetail detail, IReadOnlyList<MechanismTimelineLane> lanes)
    {
        if (detail.MechanismLanes.Count != lanes.Count)
        {
            return false;
        }

        for (int index = 0; index < lanes.Count; index++)
        {
            if (LaneOf(detail.MechanismLanes, lanes[index].Mechanism) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static MechanismTimelineLane? LaneOf(IReadOnlyList<MechanismTimelineLane>? lanes, Mechanism mechanism)
    {
        for (int index = 0; lanes is not null && index < lanes.Count; index++)
        {
            if (lanes[index].Mechanism == mechanism)
            {
                return lanes[index];
            }
        }

        return null;
    }

    /// <summary>What the axis says above the plot when each lane has its own scale: there is no one peak to state.</summary>
    private const string OwnPeaksLabel = "each lane to its own peak";

    /// <summary>The rate row <paramref name="index"/> is read against: its own peak where each lane has one, else the shared.</summary>
    private static double PeakOf(ReadOnlySpan<double> peaks, int index, double shared) =>
        index < peaks.Length ? peaks[index] : shared;

    /// <summary>What the axis said above the plot when last drawn: the shared peak rate, or that each lane has its own.</summary>
    internal string? AxisPeakText { get; private set; }

    /// <summary>The instant the axis labels its start with, as last drawn, in the time base the view reads.</summary>
    internal string? AxisStartText { get; private set; }

    /// <summary>The time base drawn under the axis, between its start and end, as last drawn; null where none fitted.</summary>
    internal string? TimeBaseText { get; private set; }

    /// <summary>The least room between the time base and the instants either side of it.</summary>
    private const double AxisLabelGap = 12;

    /// <summary>
    /// What of <paramref name="timeBase"/> the axis has <paramref name="room"/> logical px for between its instants: all of
    /// it, else its name alone (<paramref name="name"/>, session time unless the wall clock is read), which every time base
    /// begins with, else nothing.
    /// </summary>
    internal static string? FittingTimeBase(string timeBase, double room, string name = WorkspaceTime.SessionTimeWords) =>
        Labels.Get(timeBase, 10, TextBrush).Width <= room ? timeBase
        : Labels.Get(name, 10, TextBrush).Width <= room ? name
        : null;

    /// <summary>
    /// The rate, per second, row <paramref name="row"/>'s heights were last drawn against: its own busiest bar where each
    /// lane has its own scale, else the busiest every row shares (§6.2).
    /// </summary>
    internal double RowScale(int row) => (row < rowPeakCount ? rowPeaks[row] : peakRate) * WorkspaceTime.TicksPerSecond;

    /// <summary>Where lane row <paramref name="row"/> is drawn, in rows' order, the machine context first on a focused rung.</summary>
    internal Rect RowBounds(int row) => LaneRow(row, LaneCount, PlotTop, Math.Max(PlotTop + 1, Bounds.Height - PlotBottomMargin));

    /// <summary>
    /// Fills <see cref="rowPeaks"/> with each drawn lane row's own busiest rate in view, in the rows' order, found as the
    /// shared peak is found over all of them: what each row is read against when each lane has its own scale (§6.2).
    /// Returns how many rows it filled - none where no lanes are drawn, or where the lane is an operation's, whose calls
    /// keep a density scale of their own.
    /// </summary>
    private int FillRowPeaks(WorkspaceViewModel viewModel, FocusRowSet? rows, SessionTimelineDetail? detail, TimeRange visible,
        TimelineByteLayer? bytes, ProcessLaneByteLayer? groupBytes, DirectionLaneByteLayer? directionBytes)
    {
        if (rows is null)
        {
            if (!ShowingMechanismLanes)
            {
                return 0;
            }

            IReadOnlyList<MechanismTimelineLane> lanes = viewModel.Snapshot.MechanismLanes;
            EnsureRowPeaks(lanes.Count);
            SessionMechanismByteMeasures? zoomed = bytes?.Zoomed is { } fine && Intersects(fine.Interval, visible) ? fine : null;
            for (int index = 0; index < lanes.Count; index++)
            {
                Mechanism mechanism = lanes[index].Mechanism;
                if (bytes is not null)
                {
                    // As the lane draws them: the zoomed view's own columns where they were read, the overview's elsewhere.
                    SessionIntervalByteMeasures? fineLane = zoomed?.Of(mechanism);
                    double peak = bytes.Overview.Of(mechanism) is { } coarse
                        ? BytePeak(coarse, bytes.Metric, visible, fineLane?.Interval) : 0;
                    rowPeaks[index] = fineLane is null ? peak : Math.Max(peak, BytePeak(fineLane, bytes.Metric, visible, null));
                }
                else
                {
                    MechanismTimelineLane? detailLane = LaneOf(detail?.MechanismLanes, mechanism);
                    rowPeaks[index] = detail is null || detailLane is null
                        ? PeakRate(lanes[index].Buckets, visible)
                        : Math.Max(PeakRate(lanes[index].Buckets, visible, detail.Interval), PeakRate(detailLane.Buckets, visible));
                }
            }

            return lanes.Count;
        }

        if (rows.Kind is not (FocusRowKind.Owners or FocusRowKind.Directions or FocusRowKind.ChannelEnds))
        {
            return 0;
        }

        int count = rows.Rows.Count + 1;
        EnsureRowPeaks(count);
        RankingMetric? metric = groupBytes?.Metric ?? directionBytes?.Metric;
        SessionIntervalByteMeasures? machine = groupBytes?.Measures.Machine ?? directionBytes?.Measures.Machine;
        rowPeaks[0] = machine is not null && metric is { } machineMetric
            ? BytePeak(machine, machineMetric, visible, null)
            : PeakRate(rows.Context, visible);
        for (int index = 1; index < count; index++)
        {
            rowPeaks[index] = rows.Kind switch
            {
                FocusRowKind.ChannelEnds => Math.Max(
                    PeakRate(viewModel.TimelineChannelEndLanes![index - 1].Outbound, visible),
                    PeakRate(viewModel.TimelineChannelEndLanes![index - 1].Inbound, visible)),
                FocusRowKind.Owners when groupBytes is not null =>
                    index <= viewModel.ProcessLaneDisplay.Count
                        && groupBytes.Measures.Of(viewModel.ProcessLaneDisplay[index - 1].ProcessId) is { } owner
                        ? BytePeak(owner, groupBytes.Metric, visible, null) : 0,
                FocusRowKind.Directions when directionBytes is not null =>
                    directionBytes.Measures.Of(viewModel.TimelineDirectionLanes![index - 1].Direction) is { } direction
                        ? BytePeak(direction, directionBytes.Metric, visible, null) : 0,
                _ => PeakRate(rows.Rows[index - 1], visible),
            };
        }

        return count;
    }

    /// <summary>Grows <see cref="rowPeaks"/> to hold <paramref name="count"/> rows, which a repaint of as many never needs.</summary>
    private void EnsureRowPeaks(int count)
    {
        if (rowPeaks.Length < count)
        {
            rowPeaks = new double[Math.Max(count, rowPeaks.Length * 2)];
        }
    }

    /// <summary>The highest rate among the buckets the viewport shows, leaving out any inside <paramref name="except"/>.</summary>
    private static double PeakRate(IReadOnlyList<TimelineBucket> buckets, TimeRange visible, TimeRange? except = null)
    {
        double peak = 0;
        for (int index = 0; index < buckets.Count; index++)
        {
            TimelineBucket bucket = buckets[index];
            if (Intersects(bucket.Interval, visible) && (except is not { } skipped || !Inside(bucket.Interval, skipped)))
            {
                peak = Math.Max(peak, Rate(bucket));
            }
        }

        return peak;
    }

    /// <summary>
    /// The byte lanes' shared scale: the highest rate any lane's byte columns in the viewport plot, the overview's outside
    /// the zoomed view's own columns and those inside them, in bytes per tick.
    /// </summary>
    private static double LaneBytePeak(IReadOnlyList<MechanismTimelineLane> lanes, TimelineByteLayer bytes, TimeRange visible)
    {
        SessionMechanismByteMeasures? zoomed = bytes.Zoomed is { } fine && Intersects(fine.Interval, visible) ? fine : null;
        double peak = 0;
        for (int index = 0; index < lanes.Count; index++)
        {
            Mechanism mechanism = lanes[index].Mechanism;
            SessionIntervalByteMeasures? fineLane = zoomed?.Of(mechanism);
            if (bytes.Overview.Of(mechanism) is { } coarse)
            {
                peak = Math.Max(peak, BytePeak(coarse, bytes.Metric, visible, fineLane?.Interval));
            }

            if (fineLane is not null)
            {
                peak = Math.Max(peak, BytePeak(fineLane, bytes.Metric, visible, null));
            }
        }

        return peak;
    }

    /// <summary>A focused rung's byte scale: the highest rate the machine row or any of its rows plots in the viewport.</summary>
    private static double RowsBytePeak(SessionIntervalByteMeasures machine, IReadOnlyList<SessionIntervalByteMeasures> rows,
        RankingMetric metric, TimeRange visible)
    {
        double peak = BytePeak(machine, metric, visible, null);
        for (int index = 0; index < rows.Count; index++)
        {
            peak = Math.Max(peak, BytePeak(rows[index], metric, visible, null));
        }

        return peak;
    }

    /// <summary>The highest measured byte rate among one lane's columns in view, leaving out any inside <paramref name="except"/>.</summary>
    private static double BytePeak(SessionIntervalByteMeasures lane, RankingMetric metric, TimeRange visible, TimeRange? except)
    {
        double peak = 0;
        for (int column = 0; column < lane.Columns.Count; column++)
        {
            TimeRange interval = lane.IntervalOf(column);
            if (Intersects(interval, visible) && (except is not { } skipped || !Inside(interval, skipped))
                && lane.Columns[column].ValueOf(metric).Value is > 0 and long sum)
            {
                peak = Math.Max(peak, (double)sum / interval.SpanTicks);
            }
        }

        return peak;
    }

    /// <summary>L0's shared scale: every lane's overview columns outside the detail, and the detail's own columns.</summary>
    private static double MechanismLanePeak(
        IReadOnlyList<MechanismTimelineLane> lanes, SessionTimelineDetail? detail, TimeRange visible)
    {
        double peak = 0;
        for (int index = 0; index < lanes.Count; index++)
        {
            peak = Math.Max(peak, PeakRate(lanes[index].Buckets, visible, detail?.Interval));
        }

        for (int index = 0; detail is not null && index < detail.MechanismLanes.Count; index++)
        {
            peak = Math.Max(peak, PeakRate(detail.MechanismLanes[index].Buckets, visible));
        }

        return peak;
    }

    /// <summary>A focused rung's shared scale: the machine context and the bars its rows draw, at L3 each end's two bands.</summary>
    private static double FocusRowPeak(WorkspaceViewModel viewModel, FocusRowSet rows, TimeRange visible)
    {
        double peak = PeakRate(rows.Context, visible);
        if (rows.Kind == FocusRowKind.ChannelEnds)
        {
            IReadOnlyList<ChannelEndTimelineLane> ends = viewModel.TimelineChannelEndLanes!;
            for (int index = 0; index < ends.Count; index++)
            {
                peak = Math.Max(peak, Math.Max(PeakRate(ends[index].Outbound, visible), PeakRate(ends[index].Inbound, visible)));
            }

            return peak;
        }

        for (int index = 0; index < rows.Rows.Count; index++)
        {
            peak = Math.Max(peak, PeakRate(rows.Rows[index], visible));
        }

        return peak;
    }

    /// <summary>Whether any bucket the viewport shows holds a record.</summary>
    private static bool AnyObserved(IReadOnlyList<TimelineBucket> buckets, TimeRange visible)
    {
        for (int index = 0; index < buckets.Count; index++)
        {
            if (buckets[index].ObservationCount > 0 && Intersects(buckets[index].Interval, visible))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bucket whose half-open interval holds <paramref name="tick"/>, if any.</summary>
    private static TimelineBucket? BucketContaining(IReadOnlyList<TimelineBucket>? buckets, long tick)
    {
        for (int index = 0; buckets is not null && index < buckets.Count; index++)
        {
            if (buckets[index].Interval.Contains(tick))
            {
                return buckets[index];
            }
        }

        return null;
    }

    /// <summary>Hue comes from the mechanism's family token; nothing else may assign it (section 6.6, R5).</summary>
    private static SolidColorBrush BrushFor(Mechanism mechanism) => Current.FillBrush(mechanism);

    /// <summary>Labels are laid out once per text and theme and drawn without allocating (R11).</summary>
    private static readonly PaneText Labels = new(256);

    internal static void DrawText(DrawingContext context, string text, Point origin) =>
        Labels.Get(text, 10, TextBrush).Draw(context, origin);

    /// <summary>
    /// A vertical rule dashed 3 px on and 3 px off, drawn as solid segments: a dashed pen makes the drawing layer allocate
    /// a dash effect on every stroke (R11).
    /// </summary>
    private static void DrawDashedRule(DrawingContext context, Pen pen, double x, double top, double bottom)
    {
        for (double y = top; y < bottom; y += 6)
        {
            context.DrawLine(pen, new(x, y), new(x, Math.Min(bottom, y + 3)));
        }
    }
}
