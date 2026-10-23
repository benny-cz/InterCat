using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Avalonia.Threading;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Draws the graph as projected (§6.3, <see cref="GraphProjection"/>): a circle for each process shown on its own and a
/// stack for each aggregate - solid for a collapsed group, muted for an opened group's other members, dashed for the
/// cross-group remainder and dotted for processes with no relationship. Sizes and thicknesses follow §6.2's intensity,
/// hue is the mechanism, and the dash pattern of an edge is its evidence quality. Aggregates state their counts in
/// selection detail and in their canvas label whenever that label is placed.
/// </summary>
public sealed class GraphView : Control, IHoverCardSource, ICustomHitTest
{
    /// <summary>The mode the graph draws in: the tokens' current one, so a change of theme reaches the canvas (§6.1).</summary>
    private static ThemeMode Mode => ThemeResources.CurrentMode;

    /// <summary>Below this pane width the graph drops secondary labels instead of overlapping them.</summary>
    private const double NarrowPaneWidth = 620;

    /// <summary>Half the drawn label width, kept clear on both sides so no name is clipped at an edge.</summary>
    private const double LabelInset = 52;

    /// <summary>Beyond this many nodes only the most important are labelled; the rest are named on selection and in the table.</summary>
    private const int LabelBudget = 40;

    /// <summary>A graph of at most this many nodes names each process's PID beneath its name, where the pane is wide enough.</summary>
    private const int DetailBudget = 24;

    /// <summary>A label's widest line; a longer name ends in an ellipsis and is read in full on selection and in the table.</summary>
    private const double MaximumLabelWidth = 150;

    /// <summary>§19.4: a node is hit within its radius plus this many logical pixels.</summary>
    private const double HitSlop = 4;

    // Paint resources are built once per theme mode and cached per semantic key, so a frame allocates no brush or pen per
    // edge (R11, §19.4) and a change of mode swaps every one of them at once.
    private static readonly Dictionary<ThemeMode, Ink> Inks = [];

    private static Ink Current => Inks.TryGetValue(Mode, out Ink? ink) ? ink : Inks[Mode] = new Ink(Mode);

    private static IBrush NodeBrush => Current.NodeBrush;
    private static IBrush SelectedBrush => Current.SelectedBrush;
    private static IBrush TextBrush => Current.TextBrush;
    private static IBrush MutedTextBrush => Current.MutedTextBrush;
    private static IBrush PlotBrush => Current.PlotBrush;
    private static Pen NodePen => Current.NodePen;
    private static Pen FocusPen => Current.FocusPen;
    private static Pen SelectionPen => Current.SelectionPen;
    private static Pen HaloPen => Current.HaloPen;
    private static Pen InternalRelationshipPen => Current.InternalRelationshipPen;
    private static Pen StackPen => Current.StackPen;
    private static Pen MutedPen => Current.MutedPen;
    private static Dashes RemainderDashes => Current.RemainderDashes;
    private static Dashes QuietDashes => Current.QuietDashes;
    private static Dashes PartialSelectionDashes => Current.PartialSelectionDashes;
    private static Pen HoverPen => Current.HoverPen;
    private static Pen HoverHaloPen => Current.HoverHaloPen;
    private static Pen PinHeadPen => Current.PinHeadPen;
    private static Pen MatchPen => Current.MatchPen;
    private static Pen MatchHaloPen => Current.MatchHaloPen;
    private static Pen ChosenHitPen => Current.ChosenHitPen;
    private static Dashes PartlyChosenHitDashes => Current.PartlyChosenHitDashes;
    private static Dashes ContextDashes => Current.ContextDashes;
    private static Pen ContextRimPen => Current.ContextRimPen;

    /// <summary>A press moves this far before it is a drag that pins, so a slightly unsteady click still only selects.</summary>
    private const double DragThreshold = 4;

    // A drag in progress: the node, where the press began, whether it has moved enough, and where it is now.
    private string? dragKey;
    private IPointer? dragPointer;
    private Point dragFrom;
    private Point dragAt;
    private bool dragging;

    // Whether the last press ended as a plain click, neither a node's drag nor cancelled. Only then does a second press at
    // its place make the double click that opens what is there (§6.7): a drag given up with Esc and begun again opens
    // nothing. A press records its end when it ends, since the double click is raised after the second press's handling.
    private bool clickedLast;
    private bool pressCancelled;

    // Hover (§6.2): the node or edge under the pointer, where the pointer is, and its card - described once per key and
    // drawing, not on every move. Hover only highlights; it never changes selection or filters.
    private string? hovered;
    // The resting pointer relative to the window, so a pane moved under it is asked about the point it is really over.
    private Point hoverInWindow;
    private HoverCard? hoverCard;
    private GraphDisplay? hoverCardDisplay;
    private bool hoverCardPinned;
    private GraphComparisonOverlay? hoverCardOverlay;

    // Label text carries its brush, so it is laid out per theme mode as well as per text, and drawn without allocating.
    private readonly PaneText labels = new(512);

    // What a repaint gathers: where each node is drawn and how large, the labels chosen and the boxes they took, each
    // node's second label line, and the keyboard's order for the drawing it belongs to (R11).
    private readonly Dictionary<string, Point> drawnPoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> drawnRadii = new(StringComparer.Ordinal);
    private readonly List<GraphDisplayNode> labelCandidates = [];
    private readonly List<Rect> placedLabels = [];
    private readonly List<string> placedLabelKeys = [];
    private readonly List<LabelRequest> labelRequests = [];
    private readonly List<LabelPlacement> labelPlacements = [];
    private readonly List<Rect> chosenLabels = [];
    private readonly List<(Point From, Point To)> drawnLeaders = [];
    private readonly List<Rect> drawnHeads = [];
    private readonly Dictionary<string, (Point At, GraphComparisonMarked Marked)> drawnMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<GraphDisplayNode, string> details = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GraphDisplayNode, string> names = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GraphDisplayNode> repeats = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> placedLabelNames = [];
    private GraphDisplay? traversalFor;
    private IReadOnlyList<GraphDisplayNode> traversal = [];
    private string? problemFor;
    private string? problemText;

    // The node the keyboard is on, and the selected node it last followed. A node newly selected anywhere - by a click, an
    // arrow, a ranked row or a search - takes the keyboard, as a list's focus follows its selected row; Ctrl+arrows move it
    // on without choosing, so Ctrl+Space can add a node other than the selected one to the multi-selection (§6.7).
    private string? keyboardKey;
    private string? keyboardFollowed;
    private WorkspaceViewModel? observed;

    /// <summary>How much wider than its place a selection's ring is drawn as it starts to settle onto a node (§6.8).</summary>
    internal const double RingTravel = 12;

    // §6.8's motion: where each node is drawn while it moves to its place, the drawing whose places it last took, and when
    // each selected node was ringed. Each is read at one moment per frame, from a clock a test can set.
    private readonly GraphMotion motion = new();
    private WorkspaceViewModel? motionOf;
    private readonly Dictionary<string, TimeSpan> ringedSince = new(StringComparer.Ordinal);
    private readonly List<string> unringed = [];
    private IReadOnlySet<string>? ringsFor;
    private TimeSpan ringsSettled = TimeSpan.MinValue;
    private bool wasMoving;
    private bool frameRequested;
    private readonly Action<TimeSpan> nextFrame;
    private readonly Action refreshHover;
    private TimeProvider clock = TimeProvider.System;
    private long origin = TimeProvider.System.GetTimestamp();

    /// <summary>The graph's brushes and pens in one theme mode, from its verified tokens (§6.6).</summary>
    private sealed class Ink
    {
        public Ink(ThemeMode mode)
        {
            SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
            NodeBrush = Token(surfaces.Elevated);
            IBrush border = Token(surfaces.Accent);
            SelectedBrush = Token(surfaces.Accent);
            TextBrush = Token(surfaces.Ink);
            MutedTextBrush = Token(surfaces.MutedInk);
            PlotBrush = Token(surfaces.Plot);
            NodePen = new(border, 1.5);
            FocusPen = new(border, 3);
            SelectionPen = new(SelectedBrush, 3);
            HaloPen = new(SelectedBrush, 9) { LineCap = PenLineCap.Round };
            InternalRelationshipPen = new(SelectedBrush, 4);
            StackPen = new(border, 1);
            MutedPen = new(MutedTextBrush, 1.5);
            RemainderDashes = new(MutedPen, 4, 3);
            QuietDashes = new(MutedPen, 1, 3);
            PartialSelectionDashes = new(new Pen(SelectedBrush, 2), 3, 3);
            HoverPen = new(TextBrush, 1.5);
            HoverHaloPen = new(TextBrush, 7) { LineCap = PenLineCap.Round };
            PinHeadPen = new(PlotBrush, 1.5);
            MatchPen = new(TextBrush, 1.5);
            MatchHaloPen = new(PlotBrush, 1.5);
            ChosenHitPen = new(TextBrush, 2.5);
            PartlyChosenHitDashes = new(new Pen(TextBrush, 2), 3, 3);
            ContextDashes = new(MutedPen, 7, 4);
            ContextRimPen = new(MutedTextBrush, 1);
            SampleRingPen = new(TextBrush, 2);
            LeaderPen = new(MutedTextBrush, 1);
            ComparisonMarkPen = new(TextBrush, 1.25);
            SharedComparisonDashes = new(ComparisonMarkPen, 2, 1.6);
        }

        public IBrush NodeBrush { get; }
        public IBrush SelectedBrush { get; }
        public IBrush TextBrush { get; }
        public IBrush MutedTextBrush { get; }
        public IBrush PlotBrush { get; }
        public Pen NodePen { get; }
        public Pen FocusPen { get; }
        public Pen SelectionPen { get; }
        public Pen HaloPen { get; }
        public Pen InternalRelationshipPen { get; }
        public Pen StackPen { get; }
        public Pen MutedPen { get; }
        public Dashes RemainderDashes { get; }
        public Dashes QuietDashes { get; }
        public Dashes PartialSelectionDashes { get; }
        public Pen HoverPen { get; }
        public Pen HoverHaloPen { get; }
        public Pen PinHeadPen { get; }
        public Pen MatchPen { get; }
        public Pen MatchHaloPen { get; }
        public Pen ChosenHitPen { get; }
        public Dashes PartlyChosenHitDashes { get; }
        public Dashes ContextDashes { get; }
        public Pen ContextRimPen { get; }

        /// <summary>The hairline that ties a name set away from its node to the node.</summary>
        public Pen LeaderPen { get; }

        /// <summary>The ring a comparison's mark on an edge is drawn in (§8e), and its broken form for a mark of more than the edge.</summary>
        public Pen ComparisonMarkPen { get; }

        public Dashes SharedComparisonDashes { get; }


        /// <summary>Mechanism fills and edge pens, cached per semantic key within this mode.</summary>
        public Dictionary<Mechanism, SolidColorBrush> MechanismBrushes { get; } = [];

        public Dictionary<(Mechanism, int), Pen> EdgePens { get; } = [];

        /// <summary>The open ring a weak relation carries at its middle, in its mechanism's hue.</summary>
        public Dictionary<Mechanism, Pen> RingPens { get; } = [];

        /// <summary>The evidence key's stroke: body ink, in each strength's pattern.</summary>
        public Pen SamplePen => samplePen ??= new(TextBrush, 2);

        /// <summary>The unmeasured key's band: body ink, as thin as the band's own sides.</summary>
        public Pen SampleHatchPen => sampleHatchPen ??= new(TextBrush, 1);

        private Pen? sampleHatchPen;

        private Pen? samplePen;

        public Pen SampleRingPen { get; }

        private static SolidColorBrush Token(Srgb value) => new(ThemeResources.ToColor(value));
    }

    /// <summary>
    /// A dashed stroke: a solid pen and its pattern in multiples of the pen's thickness. The pattern is drawn as cached
    /// geometry under that pen, because a dashed pen makes the drawing layer allocate a dash effect for every stroke of
    /// every frame (R11). The dashes start where the stroke starts, as the layer's own would.
    /// </summary>
    internal readonly record struct Dashes(Pen Stroke, double On, double Off);

    // Dash geometry is built once per shape and reused: a ring per (radius, pattern) placed by a translation, an edge's
    // strip per (length, pattern) placed by a rotation and a translation. Both are bounded, so a long session of
    // relayouts cannot grow them without end.
    private static readonly Dictionary<(double Radius, double On, double Off), StreamGeometry> RingDashes = [];
    private static readonly Dictionary<string, (double Length, double On, double Off, StreamGeometry Geometry)> EdgeDashes =
        new(StringComparer.Ordinal);

    /// <summary>A dashed circle of <paramref name="radius"/> around <paramref name="center"/>, filled first when asked.</summary>
    private static void DrawDashedCircle(DrawingContext context, IBrush? fill, Dashes dashes, Point center, double radius)
    {
        if (fill is not null)
        {
            context.DrawEllipse(fill, null, center, radius, radius);
        }

        double on = dashes.On * dashes.Stroke.Thickness;
        double off = dashes.Off * dashes.Stroke.Thickness;
        double quantized = Math.Round(radius * 4) / 4;
        if (!RingDashes.TryGetValue((quantized, on, off), out StreamGeometry? geometry))
        {
            if (RingDashes.Count > 256)
            {
                RingDashes.Clear();
            }

            geometry = RingGeometry(quantized, on, off);
            RingDashes[(quantized, on, off)] = geometry;
        }

        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawGeometry(null, dashes.Stroke, geometry);
        }
    }

    /// <summary>Dashes of <paramref name="on"/> px every <paramref name="on"/> + <paramref name="off"/> px round a circle.</summary>
    private static StreamGeometry RingGeometry(double radius, double on, double off)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext figures = geometry.Open();
        double circumference = 2 * Math.PI * Math.Max(radius, 0.5);
        for (double along = 0; along < circumference; along += on + off)
        {
            double from = along / radius;
            double to = Math.Min(circumference, along + on) / radius;
            figures.BeginFigure(new Point(radius * Math.Cos(from), radius * Math.Sin(from)), isFilled: false);
            figures.ArcTo(new Point(radius * Math.Cos(to), radius * Math.Sin(to)), new Size(radius, radius), 0,
                isLargeArc: false, SweepDirection.Clockwise);
            figures.EndFigure(isClosed: false);
        }

        return geometry;
    }

    /// <summary>
    /// An edge as its magnitude says: a stroke of its thickness in the evidence pattern, or, when what it carried was not
    /// measured, §6.6's unmeasured band in the same hue and pattern; and an arrowhead at the end its connections ran to
    /// where the capture saw them open - at both ends where they ran both ways, at neither where it saw none open
    /// (`contracts/relations-v1.md` §5) - or, on an RPC edge, at the process that served its calls, each linked to the call
    /// that served it (`contracts/operations-v1.md` §5c), or on an edge to the other hosts, at the host its processes
    /// connected to and at the process that accepted from it (§5d). <paramref name="sourceReach"/> and <paramref name="targetReach"/> are how far
    /// the nodes at its ends reach along it, which an arrowhead stands just off; each head drawn is kept in
    /// <paramref name="heads"/>, which labels keep clear of where they can.
    /// </summary>
    internal static void DrawEdgeMark(DrawingContext context, GraphDisplayEdge edge, Pen pen, Point source, Point target,
        (double On, double Off)? pattern, double sourceReach, double targetReach, List<Rect>? heads = null)
    {
        // An edge that carries one head needs room for it alone; one that carries two, for both and a stroke between.
        double width = edge.Unmeasured ? GraphEncoding.UnmeasuredBand : pen.Thickness;
        bool both = edge.MadeFromSource > 0 && edge.MadeFromTarget > 0;
        (Point Tip, Point Left, Point Right)? toward = edge.MadeFromSource > 0
            ? Arrowhead(source, target, sourceReach + ArrowGap, targetReach + ArrowGap, width, both)
            : null;
        (Point Tip, Point Left, Point Right)? back = edge.MadeFromTarget > 0
            ? Arrowhead(target, source, targetReach + ArrowGap, sourceReach + ArrowGap, width, both)
            : null;

        // The stroke stops under an arrowhead's base, so the head reads as one against its disc, not as barbs on a line.
        Point start = back is { } tail ? StrokeEnd(tail) : source;
        Point end = toward is { } head ? StrokeEnd(head) : target;
        if (edge.Unmeasured)
        {
            DrawUnmeasuredBand(context, EdgePen(edge.Mechanism, 1), start, end, pattern, edge.Key);
        }
        else
        {
            DrawStroke(context, pen, start, end, pattern, edge.Key);
        }

        if (toward is { } towardHead)
        {
            DrawArrowhead(context, MechanismBrush(edge.Mechanism), towardHead);
            heads?.Add(HeadBox(towardHead));
        }

        if (back is { } backHead)
        {
            DrawArrowhead(context, MechanismBrush(edge.Mechanism), backHead);
            heads?.Add(HeadBox(backHead));
        }
    }

    /// <summary>The box an arrowhead is drawn in: its tip's and both base corners'.</summary>
    internal static Rect HeadBox((Point Tip, Point Left, Point Right) head) => new(
        new Point(Math.Min(head.Tip.X, Math.Min(head.Left.X, head.Right.X)), Math.Min(head.Tip.Y, Math.Min(head.Left.Y, head.Right.Y))),
        new Point(Math.Max(head.Tip.X, Math.Max(head.Left.X, head.Right.X)), Math.Max(head.Tip.Y, Math.Max(head.Left.Y, head.Right.Y))));

    /// <summary>How far an arrowhead's tip stands off the disc it points at: clear of the disc's rim.</summary>
    private const double ArrowGap = 1;

    /// <summary>How far an arrowhead's base shows beyond its stroke at either side, so a thick stroke never hides it.</summary>
    private const double ArrowMargin = 3;

    /// <summary>An arrowhead's length for its base's width.</summary>
    private const double ArrowAspect = 1.2;

    /// <summary>The least stroke an edge shows between its arrowheads, were it to carry one at each end.</summary>
    private const double ArrowShaft = 6;

    /// <summary>
    /// An arrowhead along the line from <paramref name="from"/> to <paramref name="to"/>, pointing at <paramref name="to"/>:
    /// its tip <paramref name="toStandOff"/> short of it, and its base's corners, as wide as a stroke of
    /// <paramref name="width"/> and a margin either side. Null where the line, less both stand-offs, is too short to hold
    /// it with a stroke behind it - and where the edge carries a head at each end (<paramref name="both"/>), to hold both
    /// with a stroke between them, so the heads of an edge's two ends never meet.
    /// </summary>
    internal static (Point Tip, Point Left, Point Right)? Arrowhead(Point from, Point to, double fromStandOff, double toStandOff,
        double width, bool both = true)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        double across = width + (2 * ArrowMargin);
        double along = across * ArrowAspect;
        if (length - fromStandOff - toStandOff < ((both ? 2 : 1) * along) + ArrowShaft)
        {
            return null;
        }

        double ux = dx / length;
        double uy = dy / length;
        Point tip = new(to.X - (ux * toStandOff), to.Y - (uy * toStandOff));
        Point root = new(tip.X - (ux * along), tip.Y - (uy * along));
        double half = across / 2;
        return (tip, new Point(root.X - (uy * half), root.Y + (ux * half)), new Point(root.X + (uy * half), root.Y - (ux * half)));
    }

    /// <summary>
    /// Where a stroke under <paramref name="head"/> ends: a little past the middle of its base, so no seam shows between
    /// them, and well short of its tip, where the head is narrower than the stroke.
    /// </summary>
    private static Point StrokeEnd((Point Tip, Point Left, Point Right) head)
    {
        Point root = new((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2);
        return root + ((head.Tip - root) * 0.15);
    }

    /// <summary>
    /// A filled arrowhead: one cached unit triangle placed by the transform that takes its tip and base corners to
    /// <paramref name="head"/>'s, so a frame allocates nothing for it (R11).
    /// </summary>
    private static void DrawArrowhead(DrawingContext context, IBrush fill, (Point Tip, Point Left, Point Right) head)
    {
        Point root = new((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2);
        Vector along = head.Tip - root;
        Vector across = head.Left - head.Right;
        using (context.PushTransform(new Matrix(along.X, along.Y, across.X, across.Y, head.Tip.X, head.Tip.Y)))
        {
            context.DrawGeometry(fill, null, UnitArrowhead);
        }
    }

    /// <summary>The unit triangle every arrowhead is placed from: its tip at the origin, its base a unit back, a unit wide.</summary>
    private static StreamGeometry UnitArrowhead => unitArrowhead ??= ArrowheadGeometry();

    private static StreamGeometry? unitArrowhead;

    private static StreamGeometry ArrowheadGeometry()
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext figures = geometry.Open();
        figures.BeginFigure(new Point(0, 0), isFilled: true);
        figures.LineTo(new Point(-1, 0.5));
        figures.LineTo(new Point(-1, -0.5));
        figures.EndFigure(isClosed: true);
        return geometry;
    }

    /// <summary>
    /// How far the node drawn at <paramref name="center"/> at <paramref name="radius"/> reaches along an edge whose lane
    /// ends at <paramref name="end"/> and runs toward <paramref name="toward"/>: a host's square to its side, which is
    /// further toward a corner, and any other node's disc.
    /// </summary>
    internal static double Reach(GraphDisplayNode node, double radius, Point center, Point end, Point toward) =>
        node.Kind is GraphNodeKind.OtherHosts or GraphNodeKind.Host
            ? SquareReach(radius, center, end, toward)
            : DiscReach(radius, center, end);

    /// <summary>
    /// How far a disc of <paramref name="radius"/> at <paramref name="center"/> reaches along an edge whose lane ends at
    /// <paramref name="end"/>, beside the centre when the edge is drawn in a lane of its own.
    /// </summary>
    internal static double DiscReach(double radius, Point center, Point end)
    {
        Vector aside = end - center;
        return Math.Sqrt(Math.Max((radius * radius) - aside.SquaredLength, 0));
    }

    /// <summary>
    /// How far a square <paramref name="half"/> a side across from <paramref name="center"/> reaches along a line from
    /// <paramref name="end"/> toward <paramref name="toward"/>: to the side the line leaves it by. Nothing where the line
    /// starts outside it, or goes nowhere.
    /// </summary>
    internal static double SquareReach(double half, Point center, Point end, Point toward)
    {
        Vector along = toward - end;
        double length = along.Length;
        double x = end.X - center.X;
        double y = end.Y - center.Y;
        if (length == 0 || Math.Abs(x) > half || Math.Abs(y) > half)
        {
            return 0;
        }

        double ux = along.X / length;
        double uy = along.Y / length;
        double across = ux > 0 ? (half - x) / ux : ux < 0 ? (-half - x) / ux : double.PositiveInfinity;
        double down = uy > 0 ? (half - y) / uy : uy < 0 ? (-half - y) / uy : double.PositiveInfinity;
        return Math.Min(across, down);
    }

    /// <summary>The direction key's sample: an edge in body ink with an arrowhead at its end, as the graph draws one.</summary>
    internal static void DrawDirectionSample(DrawingContext context, Rect bounds)
    {
        Point start = new(bounds.Left + 1, bounds.Center.Y);
        Point end = new(bounds.Right - 1, bounds.Center.Y);
        if (Arrowhead(start, end, 0, 0, Current.SamplePen.Thickness, both: false) is { } head)
        {
            context.DrawLine(Current.SamplePen, start, StrokeEnd(head));
            DrawArrowhead(context, Current.TextBrush, head);
        }
    }

    /// <summary>
    /// §6.6's unmeasured value on an edge: an open band as wide as the widest edge, its two sides in the mechanism's hue and
    /// the evidence pattern, cross-hatched inside and never filled. The relationship is there; what it carried is unknown,
    /// not zero, so it is neither a hairline nor a thickness (R3).
    /// </summary>
    internal static void DrawUnmeasuredBand(DrawingContext context, Pen pen, Point source, Point target,
        (double On, double Off)? pattern, string key)
    {
        double length = Math.Sqrt(Math.Pow(target.X - source.X, 2) + Math.Pow(target.Y - source.Y, 2));
        if (length < 1)
        {
            return;
        }

        const double Half = GraphEncoding.UnmeasuredBand / 2;
        Matrix placement = Matrix.CreateRotation(Math.Atan2(target.Y - source.Y, target.X - source.X))
            * Matrix.CreateTranslation(source.X, source.Y);
        using (context.PushTransform(placement))
        {
            // Both sides are as long as the edge, so they share its cached dash strip.
            DrawStroke(context, pen, new Point(0, -Half), new Point(length, -Half), pattern, key);
            DrawStroke(context, pen, new Point(0, Half), new Point(length, Half), pattern, key);
            CrossHatch(context, pen, new Rect(0, -Half, length, 2 * Half));
        }
    }

    /// <summary>
    /// §6.6's cross-hatch: both diagonals across a rectangle, clipped to it - the pattern of a value not measured, which the
    /// timeline's byte lanes draw too.
    /// </summary>
    internal static void CrossHatch(DrawingContext context, Pen pen, Rect area)
    {
        const double Spacing = 5;
        using (context.PushClip(area))
        {
            for (double offset = -area.Height; offset < area.Width; offset += Spacing)
            {
                context.DrawLine(pen, new Point(area.X + offset, area.Bottom), new Point(area.X + offset + area.Height, area.Top));
                context.DrawLine(pen, new Point(area.X + offset, area.Top), new Point(area.X + offset + area.Height, area.Bottom));
            }
        }
    }

    /// <summary>
    /// §6.6's cross-hatch inside a disc, drawn as chords of both diagonals, so a node needs no clip geometry of its own and
    /// a frame allocates nothing (R11).
    /// </summary>
    private static void CrossHatchCircle(DrawingContext context, Pen pen, Point center, double radius)
    {
        const double Spacing = 4;
        const double Diagonal = 0.70710678118654757;
        for (double offset = -radius + (Spacing / 2); offset < radius; offset += Spacing)
        {
            double half = Math.Sqrt((radius * radius) - (offset * offset)) * Diagonal;
            double across = offset * Diagonal;
            context.DrawLine(pen, new Point(center.X + across - half, center.Y - across - half),
                new Point(center.X + across + half, center.Y - across + half));
            context.DrawLine(pen, new Point(center.X + across - half, center.Y + across + half),
                new Point(center.X + across + half, center.Y + across - half));
        }
    }

    /// <summary>The legend's key of an unmeasured value: a short band drawn by the graph's own routine, in body ink.</summary>
    internal static void DrawUnmeasuredSample(DrawingContext context, Rect area)
    {
        double middle = area.Y + (area.Height / 2);
        DrawUnmeasuredBand(context, Current.SampleHatchPen, new Point(area.X + 1, middle), new Point(area.Right - 1, middle),
            null, "legend-unmeasured");
    }

    /// <summary>
    /// A stroke from <paramref name="source"/> to <paramref name="target"/>: solid, or in <paramref name="pattern"/>'s
    /// dashes from a strip cached under <paramref name="key"/> and rebuilt only when its length or pattern changes.
    /// </summary>
    private static void DrawStroke(DrawingContext context, Pen pen, Point source, Point target, (double On, double Off)? pattern,
        string key)
    {
        if (pattern is not { } dashes)
        {
            context.DrawLine(pen, source, target);
            return;
        }

        double on = dashes.On * pen.Thickness;
        double off = dashes.Off * pen.Thickness;
        double length = Math.Round(Math.Sqrt(Math.Pow(target.X - source.X, 2) + Math.Pow(target.Y - source.Y, 2)) * 2) / 2;
        if (!EdgeDashes.TryGetValue(key, out var strip) || strip.Length != length || strip.On != on || strip.Off != off)
        {
            if (EdgeDashes.Count > 2048)
            {
                EdgeDashes.Clear();
            }

            strip = (length, on, off, StripGeometry(length, on, off));
            EdgeDashes[key] = strip;
        }

        Matrix placement = Matrix.CreateRotation(Math.Atan2(target.Y - source.Y, target.X - source.X))
            * Matrix.CreateTranslation(source.X, source.Y);
        using (context.PushTransform(placement))
        {
            context.DrawGeometry(null, pen, strip.Geometry);
        }
    }

    /// <summary>Dashes of <paramref name="on"/> px every <paramref name="on"/> + <paramref name="off"/> px along +x.</summary>
    private static StreamGeometry StripGeometry(double length, double on, double off)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext figures = geometry.Open();
        for (double along = 0; along < length; along += on + off)
        {
            figures.BeginFigure(new Point(along, 0), isFilled: false);
            figures.LineTo(new Point(Math.Min(length, along + on), 0));
            figures.EndFigure(isClosed: false);
        }

        return geometry;
    }

    /// <summary>Mechanism owns hue; evidence quality owns the dash pattern, never a hue (section 6.6).</summary>
    private static SolidColorBrush MechanismBrush(Mechanism mechanism)
    {
        if (!Current.MechanismBrushes.TryGetValue(mechanism, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ThemeResources.FillOf(mechanism, Mode));
            Current.MechanismBrushes[mechanism] = brush;
        }

        return brush;
    }

    private static Pen EdgePen(Mechanism mechanism, double thickness)
    {
        // Thickness is kept to a quarter pixel, which no eye resolves, so the cache stays small.
        int quarter = (int)Math.Round(thickness * 4);
        if (!Current.EdgePens.TryGetValue((mechanism, quarter), out Pen? pen))
        {
            pen = new Pen(MechanismBrush(mechanism), quarter / 4d);
            Current.EdgePens[(mechanism, quarter)] = pen;
        }

        return pen;
    }

    /// <summary>The open ring a candidate or unresolved relation carries, in its mechanism's hue.</summary>
    private static Pen RingPen(Mechanism mechanism)
    {
        if (!Current.RingPens.TryGetValue(mechanism, out Pen? pen))
        {
            pen = new Pen(MechanismBrush(mechanism), 2);
            Current.RingPens[mechanism] = pen;
        }

        return pen;
    }

    /// <summary>
    /// Evidence quality's dash pattern (§6.6), in multiples of the pen's thickness: solid (none) for a direct relation,
    /// dashed for a correlated one, dotted for anything weaker. The inspector's key draws with this same pattern.
    /// </summary>
    internal static (double On, double Off)? EvidencePattern(RelationStrength strength) => strength switch
    {
        RelationStrength.Direct => null,
        RelationStrength.Correlated => (6, 3),
        _ => (2, 3),
    };

    /// <summary>The evidence key's strips are cached under keys no relationship uses.</summary>
    private static readonly string[] SampleKeys = [.. Enumerable.Range(0, 8).Select(strength => $"\u0000sample{strength}")];

    /// <summary>
    /// A relation weaker than correlated - a candidate, an unresolved one, or one whose evidence conflicts - also carries an
    /// open ring at its middle, so none is read as asserted. A conflicting one is drawn only when a policy admits every
    /// strength, and it is no more asserted than a candidate (ADR-055).
    /// </summary>
    internal static bool RingsMiddle(RelationStrength strength) =>
        strength is RelationStrength.Candidate or RelationStrength.Unresolved or RelationStrength.Conflicting;

    /// <summary>
    /// One edge of <paramref name="strength"/> across <paramref name="bounds"/>, as the graph draws it but in body ink:
    /// hue belongs to the mechanism, and a key to evidence quality must show only the pattern (§6.6).
    /// </summary>
    internal static void DrawEdgeSample(DrawingContext context, RelationStrength strength, Rect bounds)
    {
        Point start = new(bounds.Left + 1, bounds.Center.Y);
        Point end = new(bounds.Right - 1, bounds.Center.Y);
        DrawStroke(context, Current.SamplePen, start, end, EvidencePattern(strength), SampleKeys[(int)strength]);
        if (RingsMiddle(strength))
        {
            context.DrawEllipse(Brushes.Transparent, Current.SampleRingPen, bounds.Center, 5, 5);
        }
    }

    /// <summary>The usable pane height the layout's spacing is designed for (GraphLayout's pixels per unit).</summary>
    private const double DesignHeight = 250;

    /// <summary>
    /// §6.3's radius, shared with the layout (<see cref="GraphEncoding"/>), so the discs it keeps apart are these. Graph
    /// positions are fractions of the pane, so in a pane shorter than the layout's design height every disc shrinks by
    /// the same factor, down to half: relative sizes, and so the magnitude encoding, are kept, and discs do not overlap.
    /// </summary>
    private double RadiusOf(GraphDisplayNode node, long scale) =>
        GraphEncoding.NodeRadius(node, scale) * Math.Clamp((Bounds.Height - 72) / DesignHeight, 0.5, 1);

    private static double ThicknessOf(GraphDisplayEdge edge, long scale) => GraphEncoding.EdgeThickness(edge, scale);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        // Everything a repaint gathers lives in maps and buffers this view keeps, so a frame allocates nothing (R11).
        GraphDisplay display = viewModel.GraphDisplay;
        long nodeScale = GraphEncoding.NodeScale(display);
        long edgeScale = GraphEncoding.EdgeScale(display);

        TimeSpan now = Now;
        FollowPlaces(viewModel, now);
        Dictionary<string, Point> points = drawnPoints;
        points.Clear();
        for (int index = 0; index < display.Nodes.Count; index++)
        {
            string key = display.Nodes[index].Key;
            if (Position(key, now) is { } point) points[key] = point;
        }

        if (dragging && dragKey is { } moving && points.ContainsKey(moving))
        {
            points[moving] = dragAt;
        }

        string? highlightedRelationship = viewModel.HighlightedEdgeKey;
        string? highlighted = highlightedRelationship is { } relationship
            ? display.EdgeOf(relationship)?.Key
            : null;
        string? highlightedNode = highlightedRelationship is { } internalRelationship
            ? display.NodeOfRelationship(internalRelationship)?.Key
            : null;

        // A chosen timeline cell's relationships, or the processes whose records it counts, are highlighted in the
        // selection's accent at under half its strength, so a highlight never reads as the selection itself (§6.4).
        IReadOnlySet<string> cellEdges = viewModel.CellGraphEdges;
        IReadOnlySet<string> cellNodes = viewModel.CellGraphNodes;
        List<Rect> heads = drawnHeads;
        heads.Clear();
        GraphComparisonOverlay? overlay = viewModel.ComparisonOverlay;
        Dictionary<string, (Point At, GraphComparisonMarked Marked)> marks = drawnMarks;
        marks.Clear();
        for (int edgeIndex = 0; edgeIndex < display.Edges.Count; edgeIndex++)
        {
            GraphDisplayEdge edge = display.Edges[edgeIndex];
            if (!points.TryGetValue(edge.SourceKey, out Point from) || !points.TryGetValue(edge.TargetKey, out Point to))
            {
                continue;
            }

            (Point source, Point target) = InLane(from, to, edge);
            if (edge.Key == highlighted)
            {
                // The edge that contributes to the rung is haloed in the accent under its own stroke, so its hue,
                // thickness and dash keep their meanings (section 3.2 L3 and L5, section 6.6).
                context.DrawLine(HaloPen, source, target);
            }
            else if (cellEdges.Contains(edge.Key))
            {
                using (context.PushOpacity(CellHighlightOpacity))
                {
                    context.DrawLine(HaloPen, source, target);
                }
            }
            else if (edge.Key == hovered)
            {
                // Hover highlights in the ink, lighter than the selection's accent, under the edge's own stroke.
                using (context.PushOpacity(0.35))
                {
                    context.DrawLine(HoverHaloPen, source, target);
                }
            }

            // Hue states the mechanism, thickness states magnitude and the dash pattern states evidence quality. No
            // channel carries two meanings, and none of them is colour alone (section 6.6, R14).
            Pen pen = EdgePen(edge.Mechanism, ThicknessOf(edge, edgeScale));
            (double On, double Off)? pattern = EvidencePattern(edge.Strength);

            // Under a brushed interval an edge with no records in it steps back rather than vanishing: the relationship
            // exists in the session, it is only quiet in the range being ranked (§3.4, §6.4). A relationship that leads
            // out of a focused rung's neighbourhood to the rest of the machine steps back too: it is context (§3.2).
            bool leadsOut = display.Node(edge.SourceKey)?.Kind == GraphNodeKind.Context
                || display.Node(edge.TargetKey)?.Kind == GraphNodeKind.Context;
            double opacity = (viewModel.IsRankedWithinInterval && edge.ObservationCount == 0 ? 0.3 : 1) * (leadsOut ? 0.45 : 1);
            double sourceReach = display.Node(edge.SourceKey) is { } sourceNode
                ? Reach(sourceNode, RadiusOf(sourceNode, nodeScale), from, source, target)
                : 0;
            double targetReach = display.Node(edge.TargetKey) is { } targetNode
                ? Reach(targetNode, RadiusOf(targetNode, nodeScale), to, target, source)
                : 0;
            if (opacity < 1)
            {
                using (context.PushOpacity(opacity))
                {
                    DrawEdgeMark(context, edge, pen, source, target, pattern, sourceReach, targetReach, heads);
                }
            }
            else
            {
                DrawEdgeMark(context, edge, pen, source, target, pattern, sourceReach, targetReach, heads);
            }

            if (RingsMiddle(edge.Strength))
            {
                Point middle = new((source.X + target.X) / 2, (source.Y + target.Y) / 2);
                context.DrawEllipse(Brushes.Transparent, RingPen(edge.Mechanism), middle, 5, 5);
            }

            if (overlay is not null && overlay.Edges.TryGetValue(edge.Key, out GraphComparisonMarked? marked))
            {
                marks[edge.Key] = (ComparisonMarkAt(source, target, sourceReach, targetReach, edge.Lane, RingsMiddle(edge.Strength)),
                    marked);
            }
        }

        IReadOnlySet<string> selected = viewModel.SelectedGraphNodeKeys;
        IReadOnlySet<string> partlySelected = viewModel.PartlySelectedGraphNodeKeys;
        IReadOnlySet<string> searched = viewModel.SearchedGraphNodeKeys;
        IReadOnlySet<string> partlySearched = viewModel.PartlySearchedGraphNodeKeys;
        IReadOnlySet<string> chosenHit = viewModel.ChosenHitGraphNodeKeys;
        IReadOnlySet<string> partlyChosenHit = viewModel.PartlyChosenHitGraphNodeKeys;
        FollowSelection(selected, now);
        IReadOnlyList<GraphDisplayNode> order = TraversalOf(display);
        string? focused = IsFocused && order.Count > 0 ? order[KeyboardIndex(order, viewModel)].Key : null;
        Dictionary<string, double> radii = drawnRadii;
        radii.Clear();
        for (int nodeIndex = 0; nodeIndex < display.Nodes.Count; nodeIndex++)
        {
            GraphDisplayNode node = display.Nodes[nodeIndex];
            if (!points.TryGetValue(node.Key, out Point point)) continue;
            double radius = RadiusOf(node, nodeScale);
            radii[node.Key] = radius;
            if (selected.Contains(node.Key))
            {
                // A node that joins the selection is ringed from wider, and the ring settles onto it, so the eye finds the
                // node chosen anywhere - in a table, by a key or a search - wherever it is drawn (§6.8).
                double ring = radius + SelectionHalo + RingTravelAt(node.Key, now);
                context.DrawEllipse(Brushes.Transparent, SelectionPen, point, ring, ring);
            }
            else if (partlySelected.Contains(node.Key))
            {
                // Part of the selection is inside this aggregate among other processes: a broken ring, never the solid
                // one, so an aggregate is not mistaken for the selected process or group itself.
                DrawDashedCircle(context, null, PartialSelectionDashes, point, radius + SelectionHalo);
            }
            else if (cellNodes.Contains(node.Key))
            {
                using (context.PushOpacity(CellHighlightOpacity))
                {
                    context.DrawEllipse(Brushes.Transparent, SelectionPen, point, radius + SelectionHalo, radius + SelectionHalo);
                }
            }

            if (chosenHit.Contains(node.Key))
            {
                // The hit chosen among a search's hits is ringed in the ink the search marks nodes in, outside where a
                // selection's ring is drawn, as a find marks the match it is on among the others (§6.7); broken where the
                // node holds the hit's processes among others.
                context.DrawEllipse(Brushes.Transparent, ChosenHitPen, point, radius + ChosenHitHalo, radius + ChosenHitHalo);
            }
            else if (partlyChosenHit.Contains(node.Key))
            {
                DrawDashedCircle(context, null, PartlyChosenHitDashes, point, radius + ChosenHitHalo);
            }

            if (node.Key == highlightedNode)
            {
                // A selected relationship folded inside this node has no honest arrow to draw. Ring the node that owns
                // it instead, so evidence navigation still has a visible graph counterpart without inventing topology.
                context.DrawEllipse(Brushes.Transparent, InternalRelationshipPen, point, radius + 3, radius + 3);
            }
            else if (node.Key == hovered)
            {
                context.DrawEllipse(Brushes.Transparent, HoverPen, point, radius + 3, radius + 3);
            }

            // A node whose relationships' sends recorded no size has no size to draw: its face is open and cross-hatched,
            // §6.6's unmeasured value, at the smallest size rather than read as zero (R3). A context node reads no metric.
            IBrush face = node.Unmeasured && node.Kind != GraphNodeKind.Context ? PlotBrush : NodeBrush;
            if (node.Kind == GraphNodeKind.Process)
            {
                context.DrawEllipse(face, node.Key == focused ? FocusPen : NodePen, point, radius, radius);
                if (node.Unmeasured)
                {
                    CrossHatchCircle(context, StackPen, point, radius);
                }
            }
            else if (node.IsHost)
            {
                // Other hosts and each host it was opened into: a resource, drawn square with a globe inside.
                DrawOtherHosts(context, point, radius, node.Key == focused);
            }
            else if (node.Kind == GraphNodeKind.Context)
            {
                // §6.3's context node stands for what this rung does not draw and adds to no total, so its face is the
                // pane itself under a dashed muted outline. The face still hides the rims behind it and the ends of the
                // faded edges into it, so the rims read as a stack that says "several", never as tangled rings.
                context.DrawEllipse(PlotBrush, ContextRimPen, new Point(point.X + 5, point.Y - 5), radius, radius);
                context.DrawEllipse(PlotBrush, ContextRimPen, new Point(point.X + 2.5, point.Y - 2.5), radius, radius);
                if (node.Key == focused)
                {
                    context.DrawEllipse(PlotBrush, FocusPen, point, radius, radius);
                }
                else
                {
                    DrawDashedCircle(context, PlotBrush, ContextDashes, point, radius);
                }
            }
            else
            {
                // A stack: two offset rims behind the face say "several" without relying on colour (R14). The face's
                // stroke says which kind of several: a collapsed group, an opened group's other members, the cross-group
                // remainder, or processes with no relationship at all; processes that only reach other hosts carry the
                // Other hosts node's globe on a muted face.
                context.DrawEllipse(NodeBrush, StackPen, new Point(point.X + 5, point.Y - 5), radius, radius);
                context.DrawEllipse(NodeBrush, StackPen, new Point(point.X + 2.5, point.Y - 2.5), radius, radius);
                if (node.Key == focused || node.Kind is GraphNodeKind.Group or GraphNodeKind.OtherMembers or GraphNodeKind.OtherHostsOnly)
                {
                    context.DrawEllipse(face, node.Key == focused ? FocusPen
                        : node.Kind == GraphNodeKind.Group ? NodePen : MutedPen, point, radius, radius);
                    if (node.Kind == GraphNodeKind.OtherHostsOnly)
                    {
                        DrawGlobe(context, point, radius * 0.6);
                    }
                }
                else
                {
                    DrawDashedCircle(context, face, node.Kind == GraphNodeKind.Remainder ? RemainderDashes : QuietDashes,
                        point, radius);
                }

                if (node.Unmeasured)
                {
                    CrossHatchCircle(context, StackPen, point, radius);
                }
            }

            if (viewModel.IsGraphNodePinned(node.Key) || (dragging && node.Key == dragKey))
            {
                // A pin's head at the upper left, clear of an aggregate's rims: this node stays where it was placed.
                context.DrawEllipse(SelectedBrush, PinHeadPen,
                    new Point(point.X - (radius * 0.75), point.Y - (radius * 0.75)), 3.5, 3.5);
            }

            if (searched.Contains(node.Key))
            {
                // A node the search typed finds carries a mark at its upper right, in the ink a found lane is barred in
                // (§6.7), ringed in the pane's colour so it reads over an edge: filled where every process it holds is
                // found, and the Other hosts node where an address its connections ran to is.
                context.DrawEllipse(TextBrush, MatchHaloPen, SearchMarkAt(point, radius), SearchMarkRadius, SearchMarkRadius);
            }
            else if (partlySearched.Contains(node.Key))
            {
                // Open where some of the processes it holds are found, so an aggregate is not read as wholly found.
                context.DrawEllipse(PlotBrush, MatchPen, SearchMarkAt(point, radius), SearchMarkRadius, SearchMarkRadius);
            }
        }

        DrawLabels(context, display, order, points, radii, new(selected, partlySelected, chosenHit, partlyChosenHit),
            focused);

        // A comparison's marks are drawn last, over the labels, so none is hidden: a person asked for them, and Clear takes
        // them away (§8e).
        foreach ((Point at, GraphComparisonMarked marked) in marks.Values)
        {
            DrawComparisonMark(context, at, marked);
        }

        if (viewModel.GraphLayoutProblem is { } problem)
        {
            if (!string.Equals(problemFor, problem, StringComparison.Ordinal))
            {
                problemFor = problem;
                problemText = $"Layout unavailable: {problem}";
            }

            Text(problemText!, 10, strong: false, Math.Max(1, Bounds.Width - 24)).Draw(context, new(12, Math.Max(0, Bounds.Height - 22)));
        }

        KeepMoving(now);
    }

    /// <summary>The radius of the mark a node the search finds carries.</summary>
    internal const double SearchMarkRadius = 3.5;

    /// <summary>Where the mark a node the search finds carries is drawn: at its upper right, opposite a pin's head.</summary>
    internal static Point SearchMarkAt(Point center, double radius) =>
        new(center.X + (radius * 0.75), center.Y - (radius * 0.75));

    /// <summary>The radius of a comparison's mark on an edge (§8e).</summary>
    internal const double ComparisonMarkRadius = 7.5;

    /// <summary>How far apart along their strokes the marks of edges drawn beside one another are, per lane between them.</summary>
    private const double ComparisonMarkStagger = 18;

    /// <summary>
    /// Where a comparison's mark is drawn on an edge whose lane runs from <paramref name="source"/> to
    /// <paramref name="target"/> (§8e): on its stroke at its middle, off a middle ring where the edge carries one, which it
    /// must not hide, and staggered along the stroke by its <paramref name="lane"/>, so the marks of edges drawn beside one
    /// another never overlap - kept off the nodes at its ends, <paramref name="sourceReach"/> and
    /// <paramref name="targetReach"/> along it, where the stroke shows enough of itself.
    /// </summary>
    internal static Point ComparisonMarkAt(Point source, Point target, double sourceReach, double targetReach, double lane,
        bool ringed)
    {
        double dx = target.X - source.X;
        double dy = target.Y - source.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1)
        {
            return source;
        }

        double shift = (lane * ComparisonMarkStagger) + (ringed ? (lane < 0 ? -1 : 1) * (5 + ComparisonMarkRadius + 1) : 0);
        double low = sourceReach + ComparisonMarkRadius + 1;
        double high = length - targetReach - ComparisonMarkRadius - 1;
        double at = low <= high ? Math.Clamp((length / 2) + shift, low, high) : length / 2;
        return new Point(source.X + (dx / length * at), source.Y + (dy / length * at));
    }

    /// <summary>
    /// A comparison's mark on an edge (§8e): a disc of the pane's own surface ringed in the ink - broken where the mark is of
    /// more than the edge, an executable's processes together - holding the mark's glyph, laid out once like the labels, so
    /// it reads over any stroke in either theme and never by colour alone (R14).
    /// </summary>
    internal void DrawComparisonMark(DrawingContext context, Point at, GraphComparisonMarked marked)
    {
        if (marked.Shared)
        {
            DrawDashedCircle(context, PlotBrush, Current.SharedComparisonDashes, at, ComparisonMarkRadius);
        }
        else
        {
            context.DrawEllipse(PlotBrush, Current.ComparisonMarkPen, at, ComparisonMarkRadius, ComparisonMarkRadius);
        }

        TextLayout glyph = labels.Get(GraphComparisonOverlay.Glyph(marked.Mark), 10, TextBrush);
        glyph.Draw(context, new Point(at.X - (glyph.Width / 2), at.Y - (glyph.Height / 2)));
    }

    /// <summary>Where the last frame drew each comparison mark, by its edge's key; a test reads it.</summary>
    internal IReadOnlyDictionary<string, (Point At, GraphComparisonMarked Marked)> DrawnComparisonMarks => drawnMarks;

    /// <summary>
    /// §6.3's Other hosts node: a resource, not a process, so it is drawn square rather than round, with a globe's lines
    /// inside, and outlined as a process is. Its size is fixed, since its edges carry no traffic value.
    /// </summary>
    private static void DrawOtherHosts(DrawingContext context, Point point, double radius, bool focused)
    {
        Rect face = new(point.X - radius, point.Y - radius, 2 * radius, 2 * radius);
        context.DrawRectangle(NodeBrush, focused ? FocusPen : NodePen, face, radius / 4, radius / 4);
        DrawGlobe(context, point, radius * 0.55);
    }

    /// <summary>A globe's lines - its rim, a meridian and the equator - in the stack's ink: what reaches other hosts.</summary>
    private static void DrawGlobe(DrawingContext context, Point point, double globe)
    {
        context.DrawEllipse(null, StackPen, point, globe, globe);
        context.DrawEllipse(null, StackPen, point, globe * 0.45, globe);
        context.DrawLine(StackPen, new Point(point.X - globe, point.Y), new Point(point.X + globe, point.Y));
    }

    /// <summary>The node or edge under the pointer, if any; hover never changes selection (§6.4).</summary>
    internal string? HoveredKey => hovered;

    /// <inheritdoc />
    public Point HoverPoint => TopLevel.GetTopLevel(this) is { } window && window.TranslatePoint(hoverInWindow, this) is { } here
        ? here
        : hoverInWindow;

    /// <inheritdoc />
    public event EventHandler? HoverChanged;

    /// <summary>Where a node is drawn now, in this control's coordinates - on its way to its place while it moves; null when not drawn.</summary>
    internal Point? PointOf(string nodeKey)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return null;
        }

        TimeSpan now = Now;
        FollowPlaces(viewModel, now);
        return Position(nodeKey, now);
    }

    /// <summary>How wide a selected node's ring is drawn now; null when the node is not drawn selected.</summary>
    internal double? RingOf(string nodeKey)
    {
        if (DataContext is not WorkspaceViewModel viewModel || !viewModel.SelectedGraphNodeKeys.Contains(nodeKey)
            || !drawnRadii.TryGetValue(nodeKey, out double radius))
        {
            return null;
        }

        TimeSpan now = Now;
        FollowSelection(viewModel.SelectedGraphNodeKeys, now);
        return radius + SelectionHalo + RingTravelAt(nodeKey, now);
    }

    /// <summary>
    /// The clock motion is read by, which a test sets to read a motion at moments it chooses. Whatever was moving by the
    /// clock it replaces is drawn where it ends: no time has passed for it on the new one.
    /// </summary>
    internal TimeProvider Clock
    {
        get => clock;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            clock = value;
            origin = value.GetTimestamp();
            motion.Finish();
            unringed.Clear();
            unringed.AddRange(ringedSince.Keys);
            foreach (string key in unringed)
            {
                ringedSince[key] = -ThemePalette.Motion.Selection;
            }

            ringsSettled = TimeSpan.MinValue;
        }
    }

    private TimeSpan Now => clock.GetElapsedTime(origin);

    /// <summary>
    /// Takes the view model's places once they change. Within one drawing every node moves from where it is drawn now to
    /// its new place; a later publication's or another session's drawing is adopted, a node going on moving only toward
    /// the place it still has (§6.8).
    /// </summary>
    private void FollowPlaces(WorkspaceViewModel viewModel, TimeSpan now)
    {
        IReadOnlyDictionary<string, GraphPoint> places = viewModel.DisplayPositions;
        if (ReferenceEquals(motion.Places, places))
        {
            return;
        }

        if (ReferenceEquals(motionOf, viewModel))
        {
            motion.MoveTo(places, now, ThemePalette.Motion.LevelChange, ReducedMotion.IsOn);
        }
        else
        {
            motionOf = viewModel;
            motion.Adopt(places, now);
        }
    }

    /// <summary>
    /// Notes when each selected node joined the selection, so its ring settles onto it over the selection's duration. A
    /// node that leaves the selection loses its ring at once, one still selected in a later drawing keeps it as it is, and
    /// under reduced motion a ring is drawn in its place at once.
    /// </summary>
    private void FollowSelection(IReadOnlySet<string> selected, TimeSpan now)
    {
        if (ReferenceEquals(selected, ringsFor))
        {
            return;
        }

        ringsFor = selected;
        unringed.Clear();
        foreach (string key in ringedSince.Keys)
        {
            if (!selected.Contains(key))
            {
                unringed.Add(key);
            }
        }

        foreach (string key in unringed)
        {
            _ = ringedSince.Remove(key);
        }

        TimeSpan length = ThemePalette.Motion.Selection;
        bool reduced = ReducedMotion.IsOn;
        foreach (string key in selected)
        {
            if (!ringedSince.ContainsKey(key))
            {
                TimeSpan since = reduced ? now - length : now;
                ringedSince[key] = since;
                if (since + length > ringsSettled)
                {
                    ringsSettled = since + length;
                }
            }
        }
    }

    /// <summary>How much wider than its place a selected node's ring is drawn at <paramref name="now"/>: none once settled.</summary>
    private double RingTravelAt(string key, TimeSpan now) => ringedSince.TryGetValue(key, out TimeSpan since)
        ? RingTravel * (1 - GraphMotion.EaseOut((now - since) / ThemePalette.Motion.Selection))
        : 0;

    /// <summary>
    /// While a node moves or a ring settles, asks for the next frame and answers the hover again under a resting pointer as
    /// the drawing moves beneath it (P22); once everything is still, answers it a last time where all came to rest.
    /// </summary>
    private void KeepMoving(TimeSpan now)
    {
        bool moving = motion.IsMoving(now) || now < ringsSettled;
        if (moving && !frameRequested && TopLevel.GetTopLevel(this) is { } window)
        {
            frameRequested = true;
            window.RequestAnimationFrame(nextFrame);
        }
        else if (!moving && wasMoving)
        {
            Dispatcher.UIThread.Post(refreshHover);
        }

        wasMoving = moving;
    }

    /// <summary>A drag that ends in no drop carries its node back to its place from where the pointer left it (§6.8).</summary>
    private void ReturnDragged()
    {
        if (dragging && dragKey is { } key)
        {
            motion.Return(key, GraphPointAt(dragAt), Now, ThemePalette.Motion.LevelChange, ReducedMotion.IsOn);
        }
    }

    /// <summary>How large a node was drawn in the last frame; null when it was not drawn.</summary>
    internal double? RadiusOf(string nodeKey) => drawnRadii.TryGetValue(nodeKey, out double radius) ? radius : null;

    /// <summary>The card for the hovered mark, described once per key and drawing - a brush re-describes it.</summary>
    public HoverCard? HoverCard
    {
        get
        {
            if (hovered is null || DataContext is not WorkspaceViewModel viewModel)
            {
                return null;
            }

            bool pinned = viewModel.IsGraphNodePinned(hovered);
            if (hoverCard is null || !ReferenceEquals(hoverCardDisplay, viewModel.GraphDisplay) || hoverCardPinned != pinned
                || !ReferenceEquals(hoverCardOverlay, viewModel.ComparisonOverlay))
            {
                hoverCard = viewModel.DescribeGraphHover(hovered);
                hoverCardDisplay = viewModel.GraphDisplay;
                hoverCardPinned = pinned;
                hoverCardOverlay = viewModel.ComparisonOverlay;
            }

            return hoverCard;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        Point pointer = e.GetPosition(this);
        if (dragKey is not null)
        {
            // A drag carries its node under the pointer. It is the one gesture on this pane, so hover waits for it to end.
            if (!dragging && Math.Sqrt(Math.Pow(pointer.X - dragFrom.X, 2) + Math.Pow(pointer.Y - dragFrom.Y, 2)) < DragThreshold)
            {
                return;
            }

            dragging = true;
            dragAt = pointer;
            hovered = null;
            hoverCard = null;
            InvalidateVisual();
            HoverChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        string? under = HitTest(viewModel, pointer) ?? EdgeHitTest(viewModel, pointer);
        if (under != hovered)
        {
            hovered = under;
            hoverCard = null;
            hoverInWindow = e.GetPosition(TopLevel.GetTopLevel(this));
            InvalidateVisual();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (under is not null)
        {
            // The card follows the pointer across the mark it describes.
            hoverInWindow = e.GetPosition(TopLevel.GetTopLevel(this));
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hovered is not null)
        {
            hovered = null;
            hoverCard = null;
            InvalidateVisual();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Answers the hover again at the resting pointer once the drawing has moved under it: a layout, a pin, a resize or
    /// a newer generation. The card then describes the mark under the pointer now - another one, or none - never one
    /// that moved away (R13, P22). A pointer resting on empty space starts no hover; it only moves.
    /// </summary>
    internal void RefreshHover()
    {
        if (hovered is null || dragKey is not null || DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        Point resting = HoverPoint;
        string? under = HitTest(viewModel, resting) ?? EdgeHitTest(viewModel, resting);
        if (under == hovered)
        {
            return;
        }

        hovered = under;
        hoverCard = null;
        InvalidateVisual();
        HoverChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RefreshHover();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (observed is not null)
        {
            observed.PropertyChanged -= OnViewModelChanged;
        }

        observed = DataContext as WorkspaceViewModel;
        if (observed is not null)
        {
            observed.PropertyChanged += OnViewModelChanged;
        }

        // A publication replaces the view model with its selection restated: the keyboard stays on the node it was on.
        keyboardFollowed = observed?.SelectedGraphNodeKey;
        RefreshHover();
        SayNode();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        SayNode();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        SayNode();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.SelectedGraphNodeKey) or nameof(WorkspaceViewModel.GraphDisplay))
        {
            FollowSelectedNode();
            SayNode();
        }
    }

    /// <summary>
    /// Puts the keyboard on a node newly selected. A selection restated as it was - by a new drawing, a publication -
    /// leaves the keyboard where Ctrl+arrows moved it.
    /// </summary>
    private void FollowSelectedNode()
    {
        string? selected = observed?.SelectedGraphNodeKey;
        if (selected is not null && !string.Equals(selected, keyboardFollowed, StringComparison.Ordinal))
        {
            keyboardKey = selected;
        }

        keyboardFollowed = selected;
    }

    /// <summary>
    /// Where the keyboard is in a drawing's order: on its node while that is drawn, else on the selected node, else on the
    /// first.
    /// </summary>
    private int KeyboardIndex(IReadOnlyList<GraphDisplayNode> order, WorkspaceViewModel viewModel)
    {
        int index = IndexOf(order, keyboardKey);
        return index >= 0 ? index : Math.Max(IndexOf(order, viewModel.SelectedGraphNodeKey), 0);
    }

    private static int IndexOf(IReadOnlyList<GraphDisplayNode> order, string? key)
    {
        for (int index = 0; key is not null && index < order.Count; index++)
        {
            if (string.Equals(order[index].Key, key, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    /// <summary>
    /// Names the graph by the node its keyboard is on while it has the keyboard, in the words its card titles and describes
    /// the node with, as a screen reader names a list's focused row (R15), and says a node in the multi-selection is in it,
    /// as its ranked row does, and a node the search typed finds is found, as its mark shows (§6.7). The arrows move the
    /// selection from node to node, Ctrl+arrows the keyboard alone, and the canvas has no item for each node to take the
    /// focus, so neither said anything. Elsewhere the graph keeps its own name, and describes no node it is not on.
    /// </summary>
    private void SayNode()
    {
        string name = SpokenName;
        if (IsKeyboardFocusWithin && DataContext is WorkspaceViewModel { GraphDisplay.Nodes.Count: > 0 } viewModel)
        {
            IReadOnlyList<GraphDisplayNode> order = TraversalOf(viewModel.GraphDisplay);
            string key = order[KeyboardIndex(order, viewModel)].Key;
            if (viewModel.DescribeGraphHover(key) is { Lines.Count: > 0 } card)
            {
                string share = !viewModel.HasMultiSelection ? string.Empty
                    : viewModel.SelectedGraphNodeKeys.Contains(key) ? ", in the selection"
                    : viewModel.PartlySelectedGraphNodeKeys.Contains(key) ? ", partly in the selection"
                    : string.Empty;
                string found = viewModel.SearchedGraphNodeKeys.Contains(key) ? ", found by the search"
                    : viewModel.PartlySearchedGraphNodeKeys.Contains(key) ? ", partly found by the search"
                    : string.Empty;
                string chosen = viewModel.ChosenHitGraphNodeKeys.Contains(key) ? ", the hit chosen in the search"
                    : viewModel.PartlyChosenHitGraphNodeKeys.Contains(key) ? ", holding the hit chosen in the search"
                    : string.Empty;
                name = $"{SpokenName}: {card.Title}, {card.Lines[0]}{share}{found}{chosen}";
            }
        }

        CanvasAutomationPeer.Rename(this, name);
    }

    /// <summary>The drawn edge under a point, within half its thickness plus the hit padding; nearest first.</summary>
    private string? EdgeHitTest(WorkspaceViewModel viewModel, Point pointer)
    {
        // Run on every pointer move, so it scans by index and allocates nothing (R11). It answers for the edges as drawn
        // now, moving ones included (R13).
        GraphDisplay display = viewModel.GraphDisplay;
        long scale = GraphEncoding.EdgeScale(display);
        TimeSpan now = Now;
        FollowPlaces(viewModel, now);

        string? best = null;
        double bestDistance = double.MaxValue;
        for (int index = 0; index < display.Edges.Count; index++)
        {
            GraphDisplayEdge edge = display.Edges[index];
            if (Position(edge.SourceKey, now) is not { } from || Position(edge.TargetKey, now) is not { } to)
            {
                continue;
            }

            (Point source, Point target) = InLane(from, to, edge);
            double distance = DistanceToSegment(pointer, source, target);
            if (distance <= (ThicknessOf(edge, scale) / 2) + HitSlop && distance < bestDistance)
            {
                best = edge.Key;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>How far apart, in logical pixels, a node's edges to the other hosts run side by side.</summary>
    private const double HostLaneSpacing = 4;

    /// <summary>
    /// How far apart, in logical pixels, the relationships between two nodes run side by side: the widest stroke and a gap,
    /// so neither the thickest edge nor a chosen one's halo covers its neighbour.
    /// </summary>
    private const double PairLaneSpacing = 8;

    /// <summary>
    /// The line an edge is drawn and hit along: between its nodes, or beside that line by its lane, so a node's TCP and UDP
    /// to the other hosts, or two processes' TCP, UDP and RPC, run side by side rather than one over another.
    /// </summary>
    internal static (Point Source, Point Target) InLane(Point source, Point target, GraphDisplayEdge edge)
    {
        double dx = target.X - source.X;
        double dy = target.Y - source.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (edge.Lane == 0 || length < 1)
        {
            return (source, target);
        }

        double offset = edge.Lane * (edge.ToHosts ? HostLaneSpacing : PairLaneSpacing) / length;
        Vector across = new(-dy * offset, dx * offset);
        return (source + across, target + across);
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        double vx = end.X - start.X;
        double vy = end.Y - start.Y;
        double length = (vx * vx) + (vy * vy);
        double t = length <= 0 ? 0 : Math.Clamp((((point.X - start.X) * vx) + ((point.Y - start.Y) * vy)) / length, 0, 1);
        double dx = start.X + (t * vx) - point.X;
        double dy = start.Y + (t * vy) - point.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Labels are a last pass, so no circle overdraws a name. Where each goes is <see cref="PlaceLabels"/>'s: the names a
    /// frame with nothing chosen draws, placed as it places them, then the selection's and the keyboard's, so choosing a
    /// node or moving the keyboard leaves every other name where it stood. A node left unlabelled is named when selected or
    /// focused and in the ranked table (R15). Names take one line and end in an ellipsis rather than wrap mid-word.
    /// </summary>
    private void DrawLabels(
        DrawingContext context,
        GraphDisplay display,
        IReadOnlyList<GraphDisplayNode> order,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii,
        NodeRings rings,
        string? focused)
    {
        bool detailForProcesses = Bounds.Width >= NarrowPaneWidth && display.Nodes.Count <= DetailBudget;
        List<LabelRequest> requests = labelRequests;
        requests.Clear();
        for (int index = 0; index < order.Count; index++)
        {
            GraphDisplayNode node = order[index];
            if (!points.TryGetValue(node.Key, out Point point)) continue;
            (TextLayout name, TextLayout? detail) = LabelText(node, detailForProcesses, display);
            Size alone = new(name.Width, name.Height);
            requests.Add(new(index, node.Key, point, radii[node.Key], LabelRank(node, repeats), alone,
                detail is null ? alone : new(Math.Max(name.Width, detail.Width), name.Height + detail.Height)));
        }

        PlaceLabels(new Rect(Bounds.Size), requests, LabelBudget, rings, focused, points, radii, drawnHeads,
            labelPlacements, placedLabels, chosenLabels);
        placedLabelKeys.Clear();
        placedLabelNames.Clear();
        drawnLeaders.Clear();
        for (int index = 0; index < labelPlacements.Count; index++)
        {
            (int nodeIndex, string key, Rect box, bool whole, _) = labelPlacements[index];
            placedLabelKeys.Add(key);
            placedLabelNames.Add(NameOf(order[nodeIndex], display));
            (TextLayout name, TextLayout? detail) = LabelText(order[nodeIndex], detailForProcesses, display);
            double clearance = radii[key] + rings.Clearance(key);

            // A name set away from its node, where the slots beside it were taken, or nearer another node, is tied to it by
            // a hairline, so in a crowded graph no name reads as its neighbour's.
            if (LeaderFor(box, key, points[key], clearance, points, radii) is { } leader)
            {
                context.DrawLine(Current.LeaderPen, leader.From, leader.To);
                drawnLeaders.Add(leader);
            }

            GraphDisplayNode node = order[nodeIndex];
            Haloed(context, Halo(NameOf(node, display), 12), name, new(box.X + ((box.Width - name.Width) / 2), box.Y));
            if (whole && detail is not null)
            {
                Haloed(context, Halo(DetailOf(node), 10), detail,
                    new(box.X + ((box.Width - detail.Width) / 2), box.Y + name.Height));
            }
        }
    }

    /// <summary>A label's letters in the pane's colour, laid out as its own are, to draw round them.</summary>
    private TextLayout Halo(string text, double size) =>
        labels.Get(text, size, PlotBrush, MaximumLabelWidth, lines: 1, TextTrimming.CharacterEllipsis);

    /// <summary>
    /// Where a name's halo is laid round its letters: a pixel every way, and the four corners twice. A letter's corner
    /// pixel has one copy alone over its diagonal, which covers it only as far as the letter's anti-aliased edge reaches,
    /// so an edge passing there showed through by nearly half; laid twice, it shows a fifth, darker than any edge's ink.
    /// </summary>
    private static readonly Vector[] HaloOffsets =
    [
        new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1),
        new(-1, -1), new(1, -1), new(-1, 1), new(1, 1),
    ];

    /// <summary>
    /// Draws a name over its <paramref name="halo"/>, its letters in the pane's colour a pixel round them, so an edge
    /// passing beneath breaks round each letter rather than run through it - "PID 2010" read as struck through on a
    /// busy rung - and still meets its node on either side (§6.3).
    /// </summary>
    private static void Haloed(DrawingContext context, TextLayout halo, TextLayout text, Point at)
    {
        foreach (Vector offset in HaloOffsets)
        {
            halo.Draw(context, at + offset);
        }

        text.Draw(context, at);
    }

    /// <summary>
    /// A node's name and its second line: a process's PID is optional detail, but none under a name that is its PID
    /// already; an aggregate's count is part of the compaction disclosure and is kept whenever its name fits (§6.3). Laid
    /// out once per text and theme mode (R11).
    /// </summary>
    private (TextLayout Name, TextLayout? Detail) LabelText(GraphDisplayNode node, bool detailForProcesses, GraphDisplay display)
    {
        string name = NameOf(node, display);
        return (
            Text(name, 12, strong: true, MaximumLabelWidth),
            string.Equals(name, node.Label, StringComparison.Ordinal)
                && (node.Kind != GraphNodeKind.Process || detailForProcesses) && DetailOf(node) is { Length: > 0 } line
                ? Text(line, 10, strong: false, MaximumLabelWidth)
                : null);
    }

    /// <summary>
    /// The name a node is drawn with (§6.3): an opened group's own process by its PID, as the rung's table and lanes name
    /// it - the rung's title names the program they share, which each of its names repeated - and any other node by its
    /// label.
    /// </summary>
    internal static string DrawnName(GraphDisplayNode node, GraphDisplay display)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(display);
        return node.Kind == GraphNodeKind.Process && display.ExpandedGroup is { } opened
            && string.Equals(node.GroupKey, opened, StringComparison.Ordinal) && node.PidLabel is { } pid
            ? pid
            : node.Label;
    }

    /// <summary>
    /// A node a frame may name, in the keyboard's order (<see cref="LabelRequest.Index"/>): where it is drawn and how large,
    /// its rank among the names drawn with nothing chosen, and the room its name takes alone and with its second line.
    /// </summary>
    internal readonly record struct LabelRequest(int Index, string Key, Point Point, double Radius, int Rank, Size Alone, Size Whole);

    /// <summary>A name placed: whose, where, with its second line or alone, and whether its node is chosen.</summary>
    internal readonly record struct LabelPlacement(int Index, string Key, Rect Box, bool Whole, bool Chosen);

    /// <summary>
    /// Where a frame's names go (§6.3, §6.7). First the names a frame with nothing chosen draws, placed as it places
    /// them: aggregates, whose counts are the compaction's disclosure, then the busiest, up to
    /// <paramref name="budget"/>, each where it keeps clear of the names already placed (<see cref="FindLabelBox"/>).
    /// Then the selection, the aggregates it falls in, the search's chosen hit and the keyboard's
    /// <paramref name="focused"/> node: a chosen name already placed stays unless it stands across a ring drawn round
    /// its node (<paramref name="rings"/>); one not placed takes a slot free of every name, or else the slot it prefers
    /// among the chosen names alone, where the unchosen names it crowds step aside - left undrawn while it is chosen -
    /// rather than move. The focused node, or a single selected one, is always named. <paramref name="boxes"/> is kept
    /// in step with <paramref name="placed"/>, its boxes in its order; <paramref name="chosen"/> is room to gather the
    /// chosen names' boxes in, so a frame allocates nothing (R11).
    /// </summary>
    internal static void PlaceLabels(
        Rect pane,
        IReadOnlyList<LabelRequest> order,
        int budget,
        NodeRings rings,
        string? focused,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii,
        IReadOnlyList<Rect> heads,
        List<LabelPlacement> placed,
        List<Rect> boxes,
        List<Rect> chosen)
    {
        placed.Clear();
        boxes.Clear();
        for (int rank = 0; rank < 4 && budget > 0; rank++)
        {
            for (int index = 0; index < order.Count && budget > 0; index++)
            {
                LabelRequest request = order[index];
                if (request.Rank != rank) continue;
                budget--;
                if (Fit(pane, request, request.Radius, points, radii, boxes, heads, mustShow: false) is { } fit)
                {
                    Keep(placed, boxes, new(request.Index, request.Key, fit.Box, fit.Whole, Chosen: false));
                }
            }
        }

        for (int index = 0; index < order.Count; index++)
        {
            LabelRequest request = order[index];
            double ring = rings.Clearance(request.Key);
            if (ring == 0 && request.Key != focused) continue;
            double clearance = request.Radius + ring;
            int drawn = placed.Count - 1;
            while (drawn >= 0 && !string.Equals(placed[drawn].Key, request.Key, StringComparison.Ordinal))
            {
                drawn--;
            }

            if (drawn >= 0)
            {
                // A name already placed stays where it stood, unless it stands across a ring round its node: a selected
                // node's name, or the search's chosen hit's, stands outside the ring, never across it.
                if (GapTo(boxes[drawn], request.Point, clearance) >= 0)
                {
                    placed[drawn] = placed[drawn] with { Chosen = true };
                    continue;
                }

                placed.RemoveAt(drawn);
                boxes.RemoveAt(drawn);
            }

            if (Fit(pane, request, clearance, points, radii, boxes, heads, mustShow: false, crowding: false) is { } free)
            {
                Keep(placed, boxes, new(request.Index, request.Key, free.Box, free.Whole, Chosen: true));
                continue;
            }

            // No slot keeps its room from every name: it takes the one it prefers among the chosen names alone, and the
            // unchosen names it crowds there step aside rather than stand against it.
            chosen.Clear();
            for (int label = 0; label < placed.Count; label++)
            {
                if (placed[label].Chosen) chosen.Add(placed[label].Box);
            }

            bool mustShow = request.Key == focused
                || (rings.Selected.Count == 1 && rings.Selected.Contains(request.Key));
            if (Fit(pane, request, clearance, points, radii, chosen, heads, mustShow) is not { } taken) continue;
            for (int label = placed.Count - 1; label >= 0; label--)
            {
                if (placed[label].Chosen || !Crowds(taken.Box, boxes[label], NameSpacing)) continue;
                placed.RemoveAt(label);
                boxes.RemoveAt(label);
            }

            Keep(placed, boxes, new(request.Index, request.Key, taken.Box, taken.Whole, Chosen: true));
        }
    }

    /// <summary>
    /// A name's box with its second line where both fit, or alone where only the name does; none where neither. Without
    /// <paramref name="crowding"/> a slot nearer another name than <see cref="NameSpacing"/> is none.
    /// </summary>
    private static (Rect Box, bool Whole)? Fit(
        Rect pane,
        LabelRequest request,
        double clearance,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii,
        IReadOnlyList<Rect> avoid,
        IReadOnlyList<Rect> heads,
        bool mustShow,
        bool crowding = true)
    {
        if (FindLabelBox(pane, request.Point, clearance, request.Whole.Width, request.Whole.Height, request.Key, points, radii,
                avoid, heads, mustShow, crowding) is { } whole)
        {
            return (whole, true);
        }

        return request.Whole == request.Alone
            ? null
            : FindLabelBox(pane, request.Point, clearance, request.Alone.Width, request.Alone.Height, request.Key, points, radii,
                avoid, heads, mustShow, crowding) is { } alone ? (alone, false) : null;
    }

    private static void Keep(List<LabelPlacement> placed, List<Rect> boxes, LabelPlacement label)
    {
        placed.Add(label);
        boxes.Add(label.Box);
    }

    /// <summary>How far outside a node its selection's ring is drawn, and a selected node's label kept clear of it.</summary>
    internal const double SelectionHalo = 6;

    /// <summary>
    /// The rings a frame draws round nodes, which their names stand outside (§6.3, §6.7): the selection's, whole or
    /// broken, and outside it the search's chosen hit's, whole or broken.
    /// </summary>
    internal readonly record struct NodeRings(
        IReadOnlySet<string> Selected,
        IReadOnlySet<string> PartlySelected,
        IReadOnlySet<string> Hit,
        IReadOnlySet<string> PartlyHit)
    {
        /// <summary>How far outside its rim the outermost ring round a node is drawn; 0 where none is.</summary>
        public double Clearance(string key) =>
            Hit.Contains(key) || PartlyHit.Contains(key) ? ChosenHitHalo
            : Selected.Contains(key) || PartlySelected.Contains(key) ? SelectionHalo
            : 0;
    }

    /// <summary>How far outside a node's rim the ring of a search's chosen hit is drawn, outside a selection's.</summary>
    internal const double ChosenHitHalo = SelectionHalo + 4;

    /// <summary>How strongly a chosen timeline cell's relationships and processes are highlighted, against the selection's whole.</summary>
    private const double CellHighlightOpacity = ThemeResources.CellHighlightStrength;

    /// <summary>The label boxes drawn in the last frame, in the order placed.</summary>
    internal IReadOnlyList<Rect> PlacedLabels => placedLabels;

    /// <summary>Whose each of <see cref="PlacedLabels"/> is: the key of the node it names, in the same order.</summary>
    internal IReadOnlyList<string> PlacedLabelKeys => placedLabelKeys;

    /// <summary>What each of <see cref="PlacedLabels"/> reads: the name drawn there, in the same order.</summary>
    internal IReadOnlyList<string> PlacedLabelNames => placedLabelNames;

    /// <summary>The boxes of the arrowheads drawn in the last frame, which its labels kept clear of where they could.</summary>
    internal IReadOnlyList<Rect> DrawnHeads => drawnHeads;

    /// <summary>The leaders drawn in the last frame, each from a node's rim to its name set away from it.</summary>
    internal IReadOnlyList<(Point From, Point To)> DrawnLeaders => drawnLeaders;

    /// <summary>How far from a node's rim, or its selection's ring, its name may stand before a leader ties it to the node.</summary>
    internal const double LeaderGap = 10;

    /// <summary>
    /// The leader from a node to its name set away from it: from the rim at <paramref name="clearance"/>, toward the nearest
    /// point of the name's box, ending on it. None where the name stands within <see cref="LeaderGap"/> of the rim, as one in
    /// a slot beside its node does, unless it stands nearer another node (<paramref name="nearerAnother"/>); and none where
    /// it overlaps the rim, which nothing can tie.
    /// </summary>
    internal static (Point From, Point To)? LeaderOf(Point centre, double clearance, Rect box, bool nearerAnother = false)
    {
        Point near = new(Math.Clamp(centre.X, box.Left, box.Right), Math.Clamp(centre.Y, box.Top, box.Bottom));
        double dx = near.X - centre.X;
        double dy = near.Y - centre.Y;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        return distance <= clearance || (distance - clearance <= LeaderGap && !nearerAnother)
            ? null
            : (new Point(centre.X + (dx / distance * clearance), centre.Y + (dy / distance * clearance)), near);
    }

    /// <summary>The leader a drawn name takes (<see cref="LeaderOf"/>): tied as one set away from its node, or nearer another.</summary>
    internal static (Point From, Point To)? LeaderFor(
        Rect box,
        string key,
        Point point,
        double clearance,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii) =>
        LeaderOf(point, clearance, box, Neighbours(box, key, point, clearance, points, radii).NearerAnother);

    /// <summary>
    /// Whether a label's box hides another node, and whether it stands nearer another node's rim than its own node's, where
    /// it would read as that node's name.
    /// </summary>
    internal static (bool CoversNode, bool NearerAnother) Neighbours(
        Rect box,
        string key,
        Point point,
        double radius,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii)
    {
        double own = GapTo(box, point, radius);
        bool nearerAnother = false;
        foreach ((string nodeKey, Point nodePoint) in points)
        {
            if (nodeKey == key || !radii.TryGetValue(nodeKey, out double otherRadius)) continue;
            if (Covers(box, nodePoint, otherRadius)) return (true, nearerAnother);
            nearerAnother |= GapTo(box, nodePoint, otherRadius) < own;
        }

        return (false, nearerAnother);
    }

    /// <summary>How far a box stands from a node's rim: from the nearest point of the box to the centre, less the radius.</summary>
    private static double GapTo(Rect box, Point centre, double radius)
    {
        double dx = Math.Clamp(centre.X, box.Left, box.Right) - centre.X;
        double dy = Math.Clamp(centre.Y, box.Top, box.Bottom) - centre.Y;
        return Math.Sqrt((dx * dx) + (dy * dy)) - radius;
    }

    /// <summary>
    /// A name's rank among those drawn with nothing chosen: Other hosts', the hub every connection to another host meets
    /// at, whose second line says how many processes reached one; then an aggregate's; then a process's whose name no node
    /// named before it bears, the busiest of a program's first; then one whose name is drawn already (<paramref
    /// name="repeats"/>), so a busy machine's names say which programs are there before they say one twice.
    /// </summary>
    internal static int LabelRank(GraphDisplayNode node, IReadOnlySet<GraphDisplayNode> repeats) => node.Kind switch
    {
        GraphNodeKind.OtherHosts => 0,
        GraphNodeKind.Process => repeats.Contains(node) ? 3 : 2,
        _ => 1,
    };

    /// <summary>
    /// The processes of <paramref name="order"/> whose name another node bears before them: an aggregate's, or a busier
    /// process's, in the keyboard's order, which is the busiest first.
    /// </summary>
    internal static void FindRepeats(IReadOnlyList<GraphDisplayNode> order, GraphDisplay display, HashSet<GraphDisplayNode> repeats)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(repeats);
        repeats.Clear();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphDisplayNode node in order)
        {
            if (node.Kind != GraphNodeKind.Process)
            {
                named.Add(DrawnName(node, display));
            }
        }

        foreach (GraphDisplayNode node in order)
        {
            if (node.Kind == GraphNodeKind.Process && !named.Add(DrawnName(node, display)))
            {
                repeats.Add(node);
            }
        }
    }

    /// <summary>The name a node is drawn with (<see cref="DrawnName"/>), formatted once per node of the drawing shown.</summary>
    private string NameOf(GraphDisplayNode node, GraphDisplay display)
    {
        if (!names.TryGetValue(node, out string? name))
        {
            name = DrawnName(node, display);
            names[node] = name;
        }

        return name;
    }

    /// <summary>A node's second label line, formatted once per node of the drawing shown.</summary>
    private string DetailOf(GraphDisplayNode node)
    {
        if (!details.TryGetValue(node, out string? detail))
        {
            detail = Detail(node);
            details[node] = detail;
        }

        return detail;
    }

    /// <summary>The keyboard's order for the drawing shown, sorted once per drawing rather than on every repaint (R11).</summary>
    private IReadOnlyList<GraphDisplayNode> TraversalOf(GraphDisplay display)
    {
        if (!ReferenceEquals(traversalFor, display))
        {
            traversalFor = display;
            traversal = Traversal(display);
            details.Clear();
            names.Clear();
            FindRepeats(traversal, display, repeats);
        }

        return traversal;
    }

    /// <summary>
    /// How far out each ring of label positions lies. A static array: a span of constants is built from a field handle
    /// on every call where the JIT does not optimise, and this runs for every label of every repaint (R11).
    /// </summary>
    private static readonly double[] LabelRings = [0, 18, 42];

    /// <summary>
    /// Where a label block fits beside its node. The first ring is the familiar below/above/right/left placement; two
    /// wider rings and diagonal positions let a busy hub still be named when every immediate side is occupied by peers.
    /// A clear label never hides another node or label, nor an arrowhead where a slot clear of every head is left, and
    /// keeps its room from the other names (<see cref="NameSpacing"/>) where a slot leaves it, standing nearer one only
    /// with <paramref name="crowding"/> where none does, and even then never against one (<see cref="CrowdedSpacing"/>).
    /// A selected/focused label may fall back to a merely in-pane slot, because the user explicitly asked which node has
    /// focus (§6.3, R15). Label placement only: it never moves a node.
    /// </summary>
    internal static Rect? FindLabelBox(
        Rect pane,
        Point point,
        double radius,
        double width,
        double height,
        string key,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii,
        IReadOnlyList<Rect> placed,
        IReadOnlyList<Rect> heads,
        bool mustShow,
        bool crowding = true)
    {
        const double Gap = 3;
        const double SideBias = 3;
        ReadOnlySpan<double> rings = LabelRings;
        Span<Rect> candidates = stackalloc Rect[rings.Length * 8];
        int candidateCount = 0;
        foreach (double ring in rings)
        {
            double vertical = radius + Gap + ring;
            double horizontal = radius + Gap + SideBias + ring;
            candidates[candidateCount++] = new Rect(point.X - (width / 2), point.Y + vertical, width, height);
            candidates[candidateCount++] = new Rect(point.X - (width / 2), point.Y - vertical - height, width, height);
            candidates[candidateCount++] = new Rect(point.X + horizontal, point.Y - (height / 2), width, height);
            candidates[candidateCount++] = new Rect(point.X - horizontal - width, point.Y - (height / 2), width, height);

            // Diagonal callouts make a dense star nameable without taking a cardinal slot from one of its leaves.
            candidates[candidateCount++] = new Rect(point.X + horizontal, point.Y + vertical, width, height);
            candidates[candidateCount++] = new Rect(point.X - horizontal - width, point.Y + vertical, width, height);
            candidates[candidateCount++] = new Rect(point.X + horizontal, point.Y - vertical - height, width, height);
            candidates[candidateCount++] = new Rect(point.X - horizontal - width, point.Y - vertical - height, width, height);
        }

        // Clear of every label, node and arrowhead, and nearer its own node than any other, so it is read as that node's;
        // failing that, nearer another node, which a leader then ties to its own (LeaderOf); failing both, the same over a
        // head, since a label over a head is better than none. Each keeps its room from the other names where a slot
        // leaves it, and else only a word's space, since a name close to another is better than none - but not one
        // against it, which reads as one word with it.
        for (int pass = 0; pass < 8; pass++)
        {
            bool overHeads = pass >= 4;
            bool spaced = pass % 4 < 2;
            bool nearerAllowed = pass % 2 == 1;
            if (!spaced && !crowding) continue;
            for (int i = 0; i < candidateCount; i++)
            {
                Rect candidate = candidates[i];
                if (!pane.Contains(candidate) || Crowds(candidate, placed, spaced ? NameSpacing : CrowdedSpacing)
                    || (!overHeads && Overlaps(candidate, heads))) continue;
                (bool coversNode, bool nearerAnother) = Neighbours(candidate, key, point, radius, points, radii);
                if (!coversNode && (nearerAllowed || !nearerAnother)) return candidate;
            }
        }

        if (!mustShow) return null;

        for (int pass = 0; pass < (crowding ? 2 : 1); pass++)
        {
            for (int i = 0; i < candidateCount; i++)
            {
                Rect candidate = candidates[i];
                if (pane.Contains(candidate) && !Crowds(candidate, placed, pass == 0 ? NameSpacing : CrowdedSpacing))
                {
                    return candidate;
                }
            }
        }

        for (int i = 0; i < candidateCount; i++)
        {
            if (pane.Contains(candidates[i])) return candidates[i];
        }

        return null;
    }

    private static bool Covers(Rect box, Point centre, double radius) =>
        box.Inflate(radius).Contains(centre);

    /// <summary>
    /// The least room kept between two names, across and down: names standing against each other read as one word on a
    /// busy machine's graph - "SearchHost.exespoolsv.exe" - and two blocks as one.
    /// </summary>
    internal static readonly Thickness NameSpacing = new(6, 2);

    /// <summary>
    /// The least room kept between two names where a name stands nearer another than <see cref="NameSpacing"/>, the slots
    /// that keep it taken: a word's space across and a pixel down, so they still never run together as one word -
    /// "svchost.exeOneDrive.exe" at the smallest window - nor one's line read as the other's.
    /// </summary>
    internal static readonly Thickness CrowdedSpacing = new(4, 1);

    /// <summary>
    /// Whether a name in <paramref name="box"/> would stand nearer one of <paramref name="names"/> than
    /// <paramref name="spacing"/> lets it.
    /// </summary>
    private static bool Crowds(Rect box, IReadOnlyList<Rect> names, Thickness spacing)
    {
        for (int index = 0; index < names.Count; index++)
        {
            if (Crowds(box, names[index], spacing))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Crowds(Rect box, Rect name, Thickness spacing) => box.Inflate(spacing).Intersects(name);

    /// <summary>Whether <paramref name="box"/> overlaps any of <paramref name="boxes"/>.</summary>
    private static bool Overlaps(Rect box, IReadOnlyList<Rect> boxes)
    {
        for (int index = 0; index < boxes.Count; index++)
        {
            if (boxes[index].Intersects(box))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The secondary line: a process's PID, a reused PID's holder numbered ("PID 100 #2"), how many processes and
    /// relationships an aggregate stands for, or how many processes' connections run to the Other hosts node, which way as
    /// far as the capture saw them open - "from 21 processes" that made them, "to 2 processes" that accepted every one, "with
    /// 3 processes" where both. A process no executable names is labelled by its PID already, and has none unless its PID
    /// was reused: its node read "PID 100" over "PID 100".
    /// </summary>
    internal static string Detail(GraphDisplayNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.PidLabel is { } line)
        {
            return string.Equals(node.Label, line, StringComparison.Ordinal) ? string.Empty : line;
        }

        if (node.IsHost)
        {
            // A host drawn on its own says its address's scope, a multicast group's say, before what reached it.
            string scope = node.Kind == GraphNodeKind.Host && AddressScope.Of(node.Label) is { } named ? named + " · " : string.Empty;
            string toward = node.HostsConnected == 0 ? "from" : node.HostsConnected == node.HostConnections ? "to" : "with";
            return scope + string.Create(CultureInfo.CurrentCulture,
                $"{toward} {node.ConnectedProcesses:N0} {(node.ConnectedProcesses == 1 ? "process" : "processes")}");
        }

        string members = string.Create(CultureInfo.CurrentCulture,
            $"{node.Members.Count:N0} {(node.Members.Count == 1 ? "process" : "processes")}");
        return node.Kind == GraphNodeKind.Quiet || node.Relationships == 0
            ? members
            : string.Create(CultureInfo.CurrentCulture,
                $"{members} · {node.Relationships:N0} {(node.Relationships == 1 ? "relationship" : "relationships")}");
    }

    /// <summary>
    /// The keyboard's order: busiest first, then by name, so arrows walk from what matters most. The hosts Other hosts was
    /// opened into come after every process and aggregate, the busiest first, and then Other hosts, since they are
    /// resources the processes' connections run to; a focused rung's context node comes last, however much the rest of the
    /// machine holds, so the keyboard starts on the focus itself.
    /// </summary>
    internal static IReadOnlyList<GraphDisplayNode> Traversal(GraphDisplay display) =>
    [
        .. display.Nodes
            .OrderBy(node => node.Kind switch
            {
                GraphNodeKind.Context => 3,
                GraphNodeKind.OtherHosts => 2,
                GraphNodeKind.Host => 1,
                _ => 0,
            })
            .ThenByDescending(node => node.Observations)
            .ThenByDescending(node => node.Members.Count)
            .ThenBy(node => node.Label, StringComparer.CurrentCulture)
            .ThenBy(node => node.Key, StringComparer.Ordinal),
    ];

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        pressCancelled = false;
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        if (HitTest(viewModel, e.GetPosition(this)) is not { } key)
        {
            // A click on an edge chooses the relationship it stands for, as its row in the table does (§6.7). The press is
            // left unhandled, so a second one still makes the double click that opens it.
            if (EdgeHitTest(viewModel, e.GetPosition(this)) is { } edge && viewModel.SelectGraphEdge(edge))
            {
                InvalidateVisual();
            }

            return;
        }

        keyboardKey = key;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // §6.7's Ctrl+click adds the node's processes to the multi-selection or removes them; it never starts a drag.
            viewModel.ToggleGraphNodeInSelection(key);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        viewModel.SelectGraphNode(key);

        // A press may become a drag that pins the node where it is dropped; until it moves, it is only a selection.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            dragKey = key;
            dragFrom = e.GetPosition(this);
            dragging = false;
            dragPointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        clickedLast = !pressCancelled && !dragging;
        if (dragKey is not { } key)
        {
            return;
        }

        bool dropped = dragging;
        Point at = e.GetPosition(this);
        dragKey = null;
        dragging = false;
        dragPointer = null;
        e.Pointer.Capture(null);
        if (dropped && DataContext is WorkspaceViewModel viewModel)
        {
            // The node is drawn where it was dropped from now on; it never travels back to where it was carried from.
            GraphPoint place = GraphPointAt(at);
            if (viewModel.PinGraphNode(key, place))
            {
                TimeSpan now = Now;
                FollowPlaces(viewModel, now);
                motion.Place(key, place, now);
            }

            e.Handled = true;
        }

        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        // A drag cancelled by the system takes the node back to where it was: nothing is pinned by a gesture that did not
        // end. A release lets its capture go only once it has recorded its end.
        base.OnPointerCaptureLost(e);
        if (dragKey is not null)
        {
            pressCancelled = true;
            clickedLast = false;
            ReturnDragged();
        }

        dragKey = null;
        dragging = false;
        dragPointer = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Cancels a node's press or drag in progress, as Esc does (§6.7): the node goes back to where it was and nothing is
    /// pinned; the press's selection stands. False when none is in progress.
    /// </summary>
    internal bool CancelDrag()
    {
        if (dragKey is null)
        {
            return false;
        }

        pressCancelled = true;
        clickedLast = false;
        ReturnDragged();
        dragKey = null;
        dragging = false;
        IPointer? pointer = dragPointer;
        dragPointer = null;
        pointer?.Capture(null);
        InvalidateVisual();
        return true;
    }

    /// <summary>The graph coordinates of a point in this control: the inverse of <see cref="PlaceOf"/>, kept in [0,1].</summary>
    private GraphPoint GraphPointAt(Point point) => new(
        Math.Clamp((point.X - LabelInset) / Math.Max(1, Bounds.Width - (LabelInset * 2)), 0, 1),
        Math.Clamp((point.Y - 28) / Math.Max(1, Bounds.Height - 72), 0, 1));

    /// <summary>
    /// The whole pane answers the pointer, not only its ink. The graph draws no background, so a press on empty space
    /// never reached it and never gave it the keyboard focus its arrow keys, P and L need; and a pointer over empty space
    /// was invisible to it, so its hover depended on the ink under the pointer changing.
    /// </summary>
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>The graph's own name, which the node its keyboard is on follows while the graph has the keyboard.</summary>
    internal const string SpokenName = "Communication graph";

    public GraphView()
    {
        nextFrame = _ =>
        {
            frameRequested = false;
            InvalidateVisual();
            RefreshHover();
        };
        refreshHover = RefreshHover;
        AutomationProperties.SetName(this, SpokenName);
        DoubleTapped += OpenGroup;

        // A banner or a resized pane moves the graph under a pointer that stays where it is.
        EffectiveViewportChanged += (_, _) => RefreshHover();
    }

    /// <summary>The graph as a screen reader meets it: its role, its keyboard path and table, and what it draws now.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "graph",
        "Arrow keys move between drawn nodes, choosing each; with Ctrl they move without choosing, and Ctrl+Space adds the "
        + "node to the selection or takes it out. Enter opens a group or process, and on Other hosts or a host draws the "
        + "busiest hosts on their own or folds them back; P pins or unpins the selected node; L "
        + "lays the graph out again, keeping pins. T shows the relationship table, which lists every relationship drawn here. "
        + "F6 moves to the next pane, Shift+F6 to the one before.",
        () => (DataContext as WorkspaceViewModel)?.GraphSummary);

    private void OpenGroup(object? sender, TappedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        // Opening a group draws its members one by one (§6.3's explicit expansion), and opening a process shows its
        // channels: the double click is Enter on its row. On Other hosts or a host it draws the busiest hosts on their own,
        // or folds them back. A double click on an edge opens that relationship's channel view (§6.7).
        Point pointer = e.GetPosition(this);
        if (!clickedLast)
        {
            return;
        }

        if (HitTest(viewModel, pointer) is { } key)
        {
            e.Handled = viewModel.OpenGraphNode(key);
        }
        else if (EdgeHitTest(viewModel, pointer) is { } edge)
        {
            e.Handled = viewModel.OpenGraphEdge(edge);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not WorkspaceViewModel viewModel || viewModel.GraphDisplay.Nodes.Count == 0)
        {
            return;
        }

        int delta = e.Key switch
        {
            Key.Right or Key.Down => 1,
            Key.Left or Key.Up => -1,
            _ => 0,
        };
        IReadOnlyList<GraphDisplayNode> order = TraversalOf(viewModel.GraphDisplay);
        int index = KeyboardIndex(order, viewModel);
        if (delta != 0)
        {
            keyboardKey = order[(index + delta + order.Count) % order.Count].Key;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl+arrows move the keyboard on without choosing the node, as they move a list's focus (§6.7), so
                // Ctrl+Space can add it to the multi-selection; the graph says the node it is now on.
                SayNode();
            }
            else
            {
                viewModel.SelectGraphNode(keyboardKey);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // The keyboard equivalent of Ctrl+click: the node the keyboard is on joins or leaves the multi-selection.
            viewModel.ToggleGraphNodeInSelection(order[index].Key);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = viewModel.OpenGraphNode(order[index].Key);
        }
    }

    /// <summary>The node drawn under a point now - a moving one where it is drawn (R13) - nearest first; null when none.</summary>
    private string? HitTest(WorkspaceViewModel viewModel, Point pointer)
    {
        GraphDisplay display = viewModel.GraphDisplay;
        long scale = GraphEncoding.NodeScale(display);
        TimeSpan now = Now;
        FollowPlaces(viewModel, now);
        string? best = null;
        double bestDistance = double.MaxValue;
        for (int index = 0; index < display.Nodes.Count; index++)
        {
            GraphDisplayNode node = display.Nodes[index];
            if (Position(node.Key, now) is not { } point) continue;
            double distance = Math.Sqrt(Math.Pow(point.X - pointer.X, 2) + Math.Pow(point.Y - pointer.Y, 2));
            if (distance <= RadiusOf(node, scale) + HitSlop && distance < bestDistance)
            {
                best = node.Key;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Places a node so that its label fits inside the pane. The inset is the label half-width, so a node near an edge
    /// keeps its name readable instead of having it clipped (§6.8 legibility). A node is placed where it is drawn at
    /// <paramref name="now"/>, which is its place unless it is moving there.
    /// </summary>
    private Point? Position(string key, TimeSpan now) => motion.At(key, now) is { } graph ? PlaceOf(graph) : null;

    /// <summary>Where a place in the graph's fractions of the pane is in this control: the inverse of <see cref="GraphPointAt"/>.</summary>
    internal Point PlaceOf(GraphPoint graph) => new(
        LabelInset + (graph.X * Math.Max(0, Bounds.Width - (LabelInset * 2))),
        // Reserve room for the largest node above and the label below, keeping bands legible at the minimum size.
        28 + (graph.Y * Math.Max(0, Bounds.Height - 72)));

    /// <summary>One line of label text, trimmed with an ellipsis at <paramref name="width"/>; cached, as layout is costly.</summary>
    private TextLayout Text(string text, double size, bool strong, double width) =>
        labels.Get(text, size, strong ? TextBrush : MutedTextBrush, width, lines: 1, TextTrimming.CharacterEllipsis);
}
