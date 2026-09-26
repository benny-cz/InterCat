using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
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
    private const int LabelBudget = 24;

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
    private static Pen RemainderPen => Current.RemainderPen;
    private static Pen QuietPen => Current.QuietPen;
    private static Pen PartialSelectionPen => Current.PartialSelectionPen;
    private static Pen HoverPen => Current.HoverPen;
    private static Pen HoverHaloPen => Current.HoverHaloPen;
    private static Pen PinHeadPen => Current.PinHeadPen;
    private static Pen ContextPen => Current.ContextPen;
    private static Pen ContextRimPen => Current.ContextRimPen;

    /// <summary>A press moves this far before it is a drag that pins, so a slightly unsteady click still only selects.</summary>
    private const double DragThreshold = 4;

    // A drag in progress: the node, where the press began, whether it has moved enough, and where it is now.
    private string? dragKey;
    private Point dragFrom;
    private Point dragAt;
    private bool dragging;

    // Hover (§6.2): the node or edge under the pointer, where the pointer is, and its card - described once per key and
    // drawing, not on every move. Hover only highlights; it never changes selection or filters.
    private string? hovered;
    // The resting pointer relative to the window, so a pane moved under it is asked about the point it is really over.
    private Point hoverInWindow;
    private HoverCard? hoverCard;
    private GraphDisplay? hoverCardDisplay;
    private bool hoverCardPinned;

    // Label text carries its brush, so it is cached per theme mode as well as per text.
    private readonly Dictionary<(string Text, double Width, double Size, bool Strong, ThemeMode Mode), FormattedText> labels = [];
    private int keyboardIndex;

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
            RemainderPen = new(MutedTextBrush, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
            QuietPen = new(MutedTextBrush, 1.5) { DashStyle = new DashStyle([1, 3], 0) };
            PartialSelectionPen = new(SelectedBrush, 2) { DashStyle = new DashStyle([3, 3], 0) };
            HoverPen = new(TextBrush, 1.5);
            HoverHaloPen = new(TextBrush, 7) { LineCap = PenLineCap.Round };
            PinHeadPen = new(PlotBrush, 1.5);
            ContextPen = new(MutedTextBrush, 1.5) { DashStyle = new DashStyle([7, 4], 0) };
            ContextRimPen = new(MutedTextBrush, 1);
            SampleRingPen = new(TextBrush, 2);
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
        public Pen RemainderPen { get; }
        public Pen QuietPen { get; }
        public Pen PartialSelectionPen { get; }
        public Pen HoverPen { get; }
        public Pen HoverHaloPen { get; }
        public Pen PinHeadPen { get; }
        public Pen ContextPen { get; }
        public Pen ContextRimPen { get; }

        /// <summary>Mechanism fills and edge pens, cached per semantic key within this mode.</summary>
        public Dictionary<Mechanism, SolidColorBrush> MechanismBrushes { get; } = [];

        public Dictionary<(Mechanism, RelationStrength, int), Pen> EdgePens { get; } = [];

        /// <summary>The evidence key's pens: body ink in each strength's pattern, and the ring a weak relation carries.</summary>
        public Dictionary<RelationStrength, Pen> SamplePens { get; } = [];

        public Pen SampleRingPen { get; }

        private static SolidColorBrush Token(Srgb value) => new(ThemeResources.ToColor(value));
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

    private static Pen EdgePen(Mechanism mechanism, RelationStrength strength, double thickness)
    {
        // Thickness is kept to a quarter pixel, which no eye resolves, so the cache stays small.
        int quarter = (int)Math.Round(thickness * 4);
        if (!Current.EdgePens.TryGetValue((mechanism, strength, quarter), out Pen? pen))
        {
            pen = new Pen(MechanismBrush(mechanism), quarter / 4d) { DashStyle = EvidenceDash(strength) };
            Current.EdgePens[(mechanism, strength, quarter)] = pen;
        }

        return pen;
    }

    /// <summary>
    /// Evidence quality's dash pattern (§6.6), in multiples of the pen's thickness: solid for a direct relation, dashed
    /// for a correlated one, dotted for anything weaker. The inspector's key draws with this same pattern.
    /// </summary>
    internal static DashStyle? EvidenceDash(RelationStrength strength) => strength switch
    {
        RelationStrength.Direct => null,
        RelationStrength.Correlated => new DashStyle([6, 3], 0),
        _ => new DashStyle([2, 3], 0),
    };

    /// <summary>A candidate or unresolved relation also carries an open ring at its middle, so it is never read as asserted.</summary>
    internal static bool RingsMiddle(RelationStrength strength) =>
        strength is RelationStrength.Candidate or RelationStrength.Unresolved;

    /// <summary>
    /// One edge of <paramref name="strength"/> across <paramref name="bounds"/>, as the graph draws it but in body ink:
    /// hue belongs to the mechanism, and a key to evidence quality must show only the pattern (§6.6).
    /// </summary>
    internal static void DrawEdgeSample(DrawingContext context, RelationStrength strength, Rect bounds)
    {
        if (!Current.SamplePens.TryGetValue(strength, out Pen? pen))
        {
            pen = new Pen(Current.TextBrush, 2) { DashStyle = EvidenceDash(strength) };
            Current.SamplePens[strength] = pen;
        }

        Point start = new(bounds.Left + 1, bounds.Center.Y);
        Point end = new(bounds.Right - 1, bounds.Center.Y);
        context.DrawLine(pen, start, end);
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

        GraphDisplay display = viewModel.GraphDisplay;
        long nodeScale = GraphEncoding.NodeScale(display);
        long edgeScale = display.Edges.Count == 0 ? 0 : display.Edges.Max(edge => edge.ObservationCount);
        var points = new Dictionary<string, Point>(display.Nodes.Count, StringComparer.Ordinal);
        foreach (GraphDisplayNode node in display.Nodes)
        {
            if (Position(node.Key, viewModel) is { } point) points[node.Key] = point;
        }

        if (dragging && dragKey is { } moving && points.ContainsKey(moving))
        {
            points[moving] = dragAt;
        }

        IReadOnlySet<string> pinned = viewModel.PinnedGraphNodeKeys;

        string? highlightedRelationship = viewModel.HighlightedEdgeKey;
        string? highlighted = highlightedRelationship is { } relationship
            ? display.EdgeOf(relationship)?.Key
            : null;
        string? highlightedNode = highlightedRelationship is { } internalRelationship
            ? display.NodeOfRelationship(internalRelationship)?.Key
            : null;
        foreach (GraphDisplayEdge edge in display.Edges)
        {
            if (!points.TryGetValue(edge.SourceKey, out Point source) || !points.TryGetValue(edge.TargetKey, out Point target))
            {
                continue;
            }

            if (edge.Key == highlighted)
            {
                // The edge that contributes to the rung is haloed in the accent under its own stroke, so its hue,
                // thickness and dash keep their meanings (section 3.2 L3 and L5, section 6.6).
                context.DrawLine(HaloPen, source, target);
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
            Pen pen = EdgePen(edge.Mechanism, edge.Strength, ThicknessOf(edge, edgeScale));

            // Under a brushed interval an edge with no records in it steps back rather than vanishing: the relationship
            // exists in the session, it is only quiet in the range being ranked (§3.4, §6.4). A relationship that leads
            // out of a focused rung's neighbourhood to the rest of the machine steps back too: it is context (§3.2).
            bool leadsOut = display.Node(edge.SourceKey)?.Kind == GraphNodeKind.Context
                || display.Node(edge.TargetKey)?.Kind == GraphNodeKind.Context;
            double opacity = (viewModel.IsRankedWithinInterval && edge.ObservationCount == 0 ? 0.3 : 1) * (leadsOut ? 0.45 : 1);
            if (opacity < 1)
            {
                using (context.PushOpacity(opacity))
                {
                    context.DrawLine(pen, source, target);
                }
            }
            else
            {
                context.DrawLine(pen, source, target);
            }

            if (RingsMiddle(edge.Strength))
            {
                Point middle = new((source.X + target.X) / 2, (source.Y + target.Y) / 2);
                context.DrawEllipse(Brushes.Transparent, new Pen(MechanismBrush(edge.Mechanism), 2), middle, 5, 5);
            }
        }

        IReadOnlySet<string> selected = viewModel.SelectedGraphNodeKeys;
        IReadOnlySet<string> partlySelected = viewModel.PartlySelectedGraphNodeKeys;
        IReadOnlyList<GraphDisplayNode> order = Traversal(display);
        string? focused = IsFocused && order.Count > 0 ? order[Math.Clamp(keyboardIndex, 0, order.Count - 1)].Key : null;
        var radii = new Dictionary<string, double>(display.Nodes.Count, StringComparer.Ordinal);
        foreach (GraphDisplayNode node in display.Nodes)
        {
            if (!points.TryGetValue(node.Key, out Point point)) continue;
            double radius = RadiusOf(node, nodeScale);
            radii[node.Key] = radius;
            if (selected.Contains(node.Key))
            {
                context.DrawEllipse(Brushes.Transparent, SelectionPen, point, radius + 6, radius + 6);
            }
            else if (partlySelected.Contains(node.Key))
            {
                // Part of the selection is inside this aggregate among other processes: a broken ring, never the solid
                // one, so an aggregate is not mistaken for the selected process or group itself.
                context.DrawEllipse(Brushes.Transparent, PartialSelectionPen, point, radius + 6, radius + 6);
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

            if (node.Kind == GraphNodeKind.Process)
            {
                context.DrawEllipse(NodeBrush, node.Key == focused ? FocusPen : NodePen, point, radius, radius);
            }
            else if (node.Kind == GraphNodeKind.Context)
            {
                // §6.3's context node stands for what this rung does not draw and adds to no total, so its face is the
                // pane itself under a dashed muted outline. The face still hides the rims behind it and the ends of the
                // faded edges into it, so the rims read as a stack that says "several", never as tangled rings.
                context.DrawEllipse(PlotBrush, ContextRimPen, new Point(point.X + 5, point.Y - 5), radius, radius);
                context.DrawEllipse(PlotBrush, ContextRimPen, new Point(point.X + 2.5, point.Y - 2.5), radius, radius);
                context.DrawEllipse(PlotBrush, node.Key == focused ? FocusPen : ContextPen, point, radius, radius);
            }
            else
            {
                // A stack: two offset rims behind the face say "several" without relying on colour (R14). The face's
                // stroke says which kind of several: a collapsed group, an opened group's other members, the cross-group
                // remainder, or processes with no relationship at all.
                context.DrawEllipse(NodeBrush, StackPen, new Point(point.X + 5, point.Y - 5), radius, radius);
                context.DrawEllipse(NodeBrush, StackPen, new Point(point.X + 2.5, point.Y - 2.5), radius, radius);
                context.DrawEllipse(NodeBrush, node.Key == focused ? FocusPen : node.Kind switch
                {
                    GraphNodeKind.Group => NodePen,
                    GraphNodeKind.OtherMembers => MutedPen,
                    GraphNodeKind.Remainder => RemainderPen,
                    _ => QuietPen,
                }, point, radius, radius);
            }

            if (pinned.Contains(node.Key) || (dragging && node.Key == dragKey))
            {
                // A pin's head at the upper left, clear of an aggregate's rims: this node stays where it was placed.
                context.DrawEllipse(SelectedBrush, PinHeadPen,
                    new Point(point.X - (radius * 0.75), point.Y - (radius * 0.75)), 3.5, 3.5);
            }
        }

        DrawLabels(context, display, order, points, radii, selected, partlySelected, focused);
        if (viewModel.GraphLayoutProblem is { } problem)
        {
            context.DrawText(Text($"Layout unavailable: {problem}", 10, strong: false, Math.Max(1, Bounds.Width - 24)),
                new(12, Math.Max(0, Bounds.Height - 22)));
        }

    }

    /// <summary>The node or edge under the pointer, if any; hover never changes selection (§6.4).</summary>
    internal string? HoveredKey => hovered;

    /// <inheritdoc />
    public Point HoverPoint => TopLevel.GetTopLevel(this) is { } window && window.TranslatePoint(hoverInWindow, this) is { } here
        ? here
        : hoverInWindow;

    /// <inheritdoc />
    public event EventHandler? HoverChanged;

    /// <summary>Where a drawn node is, in this control's coordinates; null when it is not drawn.</summary>
    internal Point? PointOf(string nodeKey) => DataContext is WorkspaceViewModel viewModel ? Position(nodeKey, viewModel) : null;

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
            if (hoverCard is null || !ReferenceEquals(hoverCardDisplay, viewModel.GraphDisplay) || hoverCardPinned != pinned)
            {
                hoverCard = viewModel.DescribeGraphHover(hovered);
                hoverCardDisplay = viewModel.GraphDisplay;
                hoverCardPinned = pinned;
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
        RefreshHover();
    }

    /// <summary>The drawn edge under a point, within half its thickness plus the hit padding; nearest first.</summary>
    private string? EdgeHitTest(WorkspaceViewModel viewModel, Point pointer)
    {
        GraphDisplay display = viewModel.GraphDisplay;
        long scale = display.Edges.Count == 0 ? 0 : display.Edges.Max(edge => edge.ObservationCount);
        string? best = null;
        double bestDistance = double.MaxValue;
        foreach (GraphDisplayEdge edge in display.Edges)
        {
            if (Position(edge.SourceKey, viewModel) is not { } source || Position(edge.TargetKey, viewModel) is not { } target)
            {
                continue;
            }

            double distance = DistanceToSegment(pointer, source, target);
            if (distance <= (ThicknessOf(edge, scale) / 2) + HitSlop && distance < bestDistance)
            {
                best = edge.Key;
                bestDistance = distance;
            }
        }

        return best;
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
    /// Labels are a last pass, so no circle overdraws a name, and each is placed where it collides with no label already
    /// placed: nearest cardinal slots first, then wider and diagonal slots, avoiding other nodes where it can. The selection and keyboard focus
    /// are placed first, then the busiest nodes, up to the label budget; a node left unlabelled is named when selected or
    /// focused and in the ranked table (R15). Names take one line and end in an ellipsis rather than wrap mid-word.
    /// </summary>
    private void DrawLabels(
        DrawingContext context,
        GraphDisplay display,
        IReadOnlyList<GraphDisplayNode> order,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii,
        IReadOnlySet<string> selected,
        IReadOnlySet<string> partlySelected,
        string? focused)
    {
        bool detailForProcesses = Bounds.Width >= NarrowPaneWidth && display.Nodes.Count <= LabelBudget;
        GraphDisplayNode[] required = [.. order.Where(node => selected.Contains(node.Key) || node.Key == focused)];
        // After the selection come aggregates, whose counts are the compaction's disclosure (§6.3), then the busiest.
        GraphDisplayNode[] candidates = [.. required, .. order
            .Where(node => !required.Contains(node))
            .OrderBy(node => partlySelected.Contains(node.Key) ? 0 : node.Kind != GraphNodeKind.Process ? 1 : 2)
            .Take(Math.Max(0, LabelBudget - required.Length))];

        // The focused node, or a single selected one, is always named even beside another label. A selection of many
        // nodes, such as an opened group's members, is named first but never at the cost of overlapping text.
        HashSet<string> forced = [.. required.Where(node => node.Key == focused || selected.Count == 1).Select(node => node.Key)];
        var placed = new List<Rect>(candidates.Length);
        foreach (GraphDisplayNode node in candidates)
        {
            if (!points.TryGetValue(node.Key, out Point point)) continue;
            double radius = radii[node.Key];
            FormattedText name = Text(node.Label, 12, strong: true, MaximumLabelWidth);

            // A process's PID is optional detail; an aggregate's count is part of the compaction disclosure and is kept
            // whenever its name fits (§6.3).
            FormattedText? detail = node.Kind != GraphNodeKind.Process || detailForProcesses
                ? Text(Detail(node), 10, strong: false, MaximumLabelWidth)
                : null;
            double width = Math.Max(name.Width, detail?.Width ?? 0);
            double height = name.Height + (detail?.Height ?? 0);
            bool mustShow = forced.Contains(node.Key);
            Rect? where = Place(point, radius, width, height, node.Key, points, radii, placed, mustShow);
            if (where is null && detail is not null)
            {
                // Without room for the second line, the name alone may still fit.
                detail = null;
                where = Place(point, radius, name.Width, name.Height, node.Key, points, radii, placed, mustShow);
            }

            if (where is not { } box)
            {
                continue;
            }

            placed.Add(box);
            context.DrawText(name, new(box.X + ((box.Width - name.Width) / 2), box.Y));
            if (detail is not null)
            {
                context.DrawText(detail, new(box.X + ((box.Width - detail.Width) / 2), box.Y + name.Height));
            }
        }
    }

    /// <summary>
    /// Where a label block fits beside its node. The first ring is the familiar below/above/right/left placement; two
    /// wider rings and diagonal positions let a busy hub still be named when every immediate side is occupied by peers.
    /// A clear label never hides another node or label. A selected/focused label may fall back to a merely in-pane slot,
    /// because the user explicitly asked which node has focus (§6.3, R15).
    /// </summary>
    private Rect? Place(
        Point point,
        double radius,
        double width,
        double height,
        string key,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii,
        List<Rect> placed,
        bool mustShow)
    {
        return FindLabelBox(new Rect(Bounds.Size), point, radius, width, height, key, points, radii, placed, mustShow);
    }

    /// <summary>
    /// Pure label-placement core, exposed internally so the crowded-hub fallback has a deterministic regression test.
    /// Candidates stay close to the node first, then search outward; this is label placement only and never moves a node.
    /// </summary>
    internal static Rect? FindLabelBox(
        Rect pane,
        Point point,
        double radius,
        double width,
        double height,
        string key,
        IReadOnlyDictionary<string, Point> points,
        IReadOnlyDictionary<string, double> radii,
        IReadOnlyList<Rect> placed,
        bool mustShow)
    {
        const double Gap = 3;
        const double SideBias = 3;
        ReadOnlySpan<double> rings = [0, 18, 42];
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

        for (int i = 0; i < candidateCount; i++)
        {
            Rect candidate = candidates[i];
            if (!pane.Contains(candidate)) continue;
            bool overlapsLabel = false;
            for (int placedIndex = 0; placedIndex < placed.Count; placedIndex++)
            {
                if (!placed[placedIndex].Intersects(candidate)) continue;
                overlapsLabel = true;
                break;
            }

            if (overlapsLabel) continue;

            bool coversNode = false;
            foreach ((string nodeKey, Point nodePoint) in points)
            {
                if (nodeKey == key || !radii.TryGetValue(nodeKey, out double otherRadius)
                    || !Covers(candidate, nodePoint, otherRadius)) continue;
                coversNode = true;
                break;
            }

            if (!coversNode) return candidate;
        }

        if (!mustShow) return null;

        for (int i = 0; i < candidateCount; i++)
        {
            Rect candidate = candidates[i];
            if (!pane.Contains(candidate)) continue;
            bool overlapsLabel = false;
            for (int placedIndex = 0; placedIndex < placed.Count; placedIndex++)
            {
                if (!placed[placedIndex].Intersects(candidate)) continue;
                overlapsLabel = true;
                break;
            }

            if (!overlapsLabel) return candidate;
        }

        for (int i = 0; i < candidateCount; i++)
        {
            if (pane.Contains(candidates[i])) return candidates[i];
        }

        return null;
    }

    private static bool Covers(Rect box, Point centre, double radius) =>
        box.Inflate(radius).Contains(centre);

    /// <summary>The secondary line: a process's PID, or how many processes and relationships an aggregate stands for.</summary>
    private static string Detail(GraphDisplayNode node)
    {
        if (node.ProcessId is { } pid)
        {
            return string.Create(CultureInfo.InvariantCulture, $"PID {pid}");
        }

        string members = string.Create(CultureInfo.CurrentCulture,
            $"{node.Members.Count:N0} {(node.Members.Count == 1 ? "process" : "processes")}");
        return node.Kind == GraphNodeKind.Quiet || node.Relationships == 0
            ? members
            : string.Create(CultureInfo.CurrentCulture,
                $"{members} · {node.Relationships:N0} {(node.Relationships == 1 ? "relationship" : "relationships")}");
    }

    /// <summary>
    /// The keyboard's order: busiest first, then by name, so arrows walk from what matters most. A focused rung's context
    /// node comes last, however much the rest of the machine holds, so the keyboard starts on the focus itself.
    /// </summary>
    internal static IReadOnlyList<GraphDisplayNode> Traversal(GraphDisplay display) =>
    [
        .. display.Nodes
            .OrderBy(node => node.Kind == GraphNodeKind.Context)
            .ThenByDescending(node => node.Observations)
            .ThenByDescending(node => node.Members.Count)
            .ThenBy(node => node.Label, StringComparer.CurrentCulture)
            .ThenBy(node => node.Key, StringComparer.Ordinal),
    ];

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (DataContext is not WorkspaceViewModel viewModel || HitTest(viewModel, e.GetPosition(this)) is not { } key)
        {
            return;
        }

        IReadOnlyList<GraphDisplayNode> order = Traversal(viewModel.GraphDisplay);
        keyboardIndex = Math.Max(0, order.ToList().FindIndex(node => node.Key == key));
        viewModel.SelectGraphNode(key);

        // A press may become a drag that pins the node where it is dropped; until it moves, it is only a selection.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            dragKey = key;
            dragFrom = e.GetPosition(this);
            dragging = false;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragKey is not { } key)
        {
            return;
        }

        bool dropped = dragging;
        Point at = e.GetPosition(this);
        dragKey = null;
        dragging = false;
        e.Pointer.Capture(null);
        if (dropped && DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.PinGraphNode(key, GraphPointAt(at));
            e.Handled = true;
        }

        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        // A drag cancelled by the system leaves the node where it was: nothing is pinned by a gesture that did not end.
        base.OnPointerCaptureLost(e);
        dragKey = null;
        dragging = false;
        InvalidateVisual();
    }

    /// <summary>The graph coordinates of a point in this control: the inverse of <see cref="Position"/>, kept in [0,1].</summary>
    private GraphPoint GraphPointAt(Point point) => new(
        Math.Clamp((point.X - LabelInset) / Math.Max(1, Bounds.Width - (LabelInset * 2)), 0, 1),
        Math.Clamp((point.Y - 28) / Math.Max(1, Bounds.Height - 72), 0, 1));

    /// <summary>
    /// The whole pane answers the pointer, not only its ink. The graph draws no background, so a press on empty space
    /// never reached it and never gave it the keyboard focus its arrow keys, P and L need; and a pointer over empty space
    /// was invisible to it, so its hover depended on the ink under the pointer changing.
    /// </summary>
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public GraphView()
    {
        DoubleTapped += OpenGroup;

        // A banner or a resized pane moves the graph under a pointer that stays where it is.
        EffectiveViewportChanged += (_, _) => RefreshHover();
    }

    /// <summary>The graph as a screen reader meets it: its role, its keyboard path and table, and what it draws now.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "graph",
        "Arrow keys move between drawn nodes; Enter opens a group or process; P pins or unpins the selected node; L lays "
        + "the graph out again, keeping pins. T shows the relationship table, which lists every relationship drawn here.",
        () => (DataContext as WorkspaceViewModel)?.GraphSummary);

    private void OpenGroup(object? sender, TappedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        // Opening a group draws its members one by one (§6.3's explicit expansion): the double click is Enter on its row.
        // A double click on an edge opens that relationship's channel view (§6.7).
        Point pointer = e.GetPosition(this);
        if (HitTest(viewModel, pointer) is { } key)
        {
            e.Handled = viewModel.OpenGraphGroup(key);
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
        IReadOnlyList<GraphDisplayNode> order = Traversal(viewModel.GraphDisplay);
        if (viewModel.SelectedGraphNodeKey is { } selected)
        {
            int selectedIndex = order.ToList().FindIndex(node => node.Key == selected);
            if (selectedIndex >= 0)
            {
                keyboardIndex = selectedIndex;
            }
        }

        if (delta != 0)
        {
            keyboardIndex = (keyboardIndex + delta + order.Count) % order.Count;
            viewModel.SelectGraphNode(order[keyboardIndex].Key);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = viewModel.OpenGraphGroup(order[Math.Clamp(keyboardIndex, 0, order.Count - 1)].Key);
        }
    }

    /// <summary>The drawn node under a point, nearest first; null when the point is on no node.</summary>
    private string? HitTest(WorkspaceViewModel viewModel, Point pointer)
    {
        GraphDisplay display = viewModel.GraphDisplay;
        long scale = GraphEncoding.NodeScale(display);
        string? best = null;
        double bestDistance = double.MaxValue;
        foreach (GraphDisplayNode node in display.Nodes)
        {
            if (Position(node.Key, viewModel) is not { } point) continue;
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
    /// keeps its name readable instead of having it clipped (§6.8 legibility).
    /// </summary>
    private Point? Position(string key, WorkspaceViewModel viewModel)
    {
        if (!viewModel.DisplayPositions.TryGetValue(key, out GraphPoint graph)) return null;
        return new Point(
            LabelInset + (graph.X * Math.Max(0, Bounds.Width - (LabelInset * 2))),
            // Reserve room for the largest node above and the label below, keeping bands legible at the minimum size.
            28 + (graph.Y * Math.Max(0, Bounds.Height - 72)));
    }

    /// <summary>One line of label text, trimmed with an ellipsis at <paramref name="width"/>; cached, as layout is costly.</summary>
    private FormattedText Text(string text, double size, bool strong, double width)
    {
        if (!labels.TryGetValue((text, width, size, strong, Mode), out FormattedText? formatted))
        {
            if (labels.Count > 512) labels.Clear();
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new("Segoe UI"), size,
                strong ? TextBrush : MutedTextBrush)
            {
                MaxTextWidth = width,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            labels[(text, width, size, strong, Mode)] = formatted;
        }

        return formatted;
    }
}
