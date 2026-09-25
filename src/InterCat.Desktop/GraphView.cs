using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
public sealed class GraphView : Control
{
    private const ThemeMode Mode = ThemeMode.Dark;

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

    private static readonly IBrush NodeBrush = Token(ThemePalette.Surfaces(Mode).Elevated);
    private static readonly IBrush NodeBorderBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly IBrush SelectedBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly IBrush TextBrush = Token(ThemePalette.Surfaces(Mode).Ink);
    private static readonly IBrush MutedTextBrush = Token(ThemePalette.Surfaces(Mode).MutedInk);
    private static readonly Pen NodePen = new(NodeBorderBrush, 1.5);
    private static readonly Pen FocusPen = new(NodeBorderBrush, 3);
    private static readonly Pen SelectionPen = new(SelectedBrush, 3);
    private static readonly Pen HaloPen = new(SelectedBrush, 9) { LineCap = PenLineCap.Round };
    private static readonly Pen InternalRelationshipPen = new(SelectedBrush, 4);
    private static readonly Pen StackPen = new(NodeBorderBrush, 1);
    private static readonly Pen MutedPen = new(MutedTextBrush, 1.5);
    private static readonly Pen RemainderPen = new(MutedTextBrush, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
    private static readonly Pen QuietPen = new(MutedTextBrush, 1.5) { DashStyle = new DashStyle([1, 3], 0) };
    private static readonly Pen PartialSelectionPen = new(SelectedBrush, 2) { DashStyle = new DashStyle([3, 3], 0) };

    // Paint resources are cached per semantic key, so a frame allocates no brush or pen per edge (R11, §19.4).
    private static readonly Dictionary<Mechanism, SolidColorBrush> MechanismBrushes = [];
    private static readonly Dictionary<(Mechanism, RelationStrength, int), Pen> EdgePens = [];
    private readonly Dictionary<(string Text, double Width, double Size, bool Strong), FormattedText> labels = [];
    private int keyboardIndex;

    private static SolidColorBrush Token(Srgb value) => new(ThemeResources.ToColor(value));

    /// <summary>Mechanism owns hue; evidence quality owns the dash pattern, never a hue (section 6.6).</summary>
    private static SolidColorBrush MechanismBrush(Mechanism mechanism)
    {
        if (!MechanismBrushes.TryGetValue(mechanism, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ThemeResources.FillOf(mechanism, Mode));
            MechanismBrushes[mechanism] = brush;
        }

        return brush;
    }

    private static Pen EdgePen(Mechanism mechanism, RelationStrength strength, double thickness)
    {
        // Thickness is kept to a quarter pixel, which no eye resolves, so the cache stays small.
        int quarter = (int)Math.Round(thickness * 4);
        if (!EdgePens.TryGetValue((mechanism, strength, quarter), out Pen? pen))
        {
            pen = new Pen(MechanismBrush(mechanism), quarter / 4d)
            {
                DashStyle = strength switch
                {
                    RelationStrength.Direct => null,
                    RelationStrength.Correlated => new DashStyle([6, 3], 0),
                    _ => new DashStyle([2, 3], 0),
                },
            };
            EdgePens[(mechanism, strength, quarter)] = pen;
        }

        return pen;
    }

    /// <summary>§6.2's intensity: log2(1 + v) / log2(1 + vScale), zero for nothing observed.</summary>
    internal static double Intensity(long value, long scale) =>
        value <= 0 || scale <= 0 ? 0 : Math.Log2(1 + (double)value) / Math.Log2(1 + (double)scale);

    /// <summary>§6.3: 6 + 10 × intensity of the node's incident metric, so a quiet participant stays selectable.</summary>
    internal static double RadiusOf(GraphDisplayNode node, long scale) =>
        6 + (10 * Intensity(node.Observations, scale)) + (node.Kind == GraphNodeKind.Process ? 0 : 3);

    /// <summary>§6.3: 1.25 + 4.75 × intensity of the edge's metric.</summary>
    internal static double ThicknessOf(GraphDisplayEdge edge, long scale) => 1.25 + (4.75 * Intensity(edge.ObservationCount, scale));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        GraphDisplay display = viewModel.GraphDisplay;
        long nodeScale = display.Nodes.Count == 0 ? 0 : display.Nodes.Max(node => node.Observations);
        long edgeScale = display.Edges.Count == 0 ? 0 : display.Edges.Max(edge => edge.ObservationCount);
        var points = new Dictionary<string, Point>(display.Nodes.Count, StringComparer.Ordinal);
        foreach (GraphDisplayNode node in display.Nodes)
        {
            if (Position(node.Key, viewModel) is { } point) points[node.Key] = point;
        }

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

            // Hue states the mechanism, thickness states magnitude and the dash pattern states evidence quality. No
            // channel carries two meanings, and none of them is colour alone (section 6.6, R14).
            Pen pen = EdgePen(edge.Mechanism, edge.Strength, ThicknessOf(edge, edgeScale));

            // Under a brushed interval an edge with no records in it steps back rather than vanishing: the relationship
            // exists in the session, it is only quiet in the range being ranked (§3.4, §6.4).
            if (viewModel.IsRankedWithinInterval && edge.ObservationCount == 0)
            {
                using (context.PushOpacity(0.3))
                {
                    context.DrawLine(pen, source, target);
                }
            }
            else
            {
                context.DrawLine(pen, source, target);
            }

            if (edge.Strength is RelationStrength.Candidate or RelationStrength.Unresolved)
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

            if (node.Kind == GraphNodeKind.Process)
            {
                context.DrawEllipse(NodeBrush, node.Key == focused ? FocusPen : NodePen, point, radius, radius);
                continue;
            }

            // A stack: two offset rims behind the face say "several" without relying on colour (R14). The face's stroke
            // says which kind of several: a collapsed group, an opened group's other members, the cross-group remainder,
            // or processes with no relationship at all.
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

        DrawLabels(context, display, order, points, radii, selected, partlySelected, focused);
        if (viewModel.GraphLayoutProblem is { } problem)
        {
            context.DrawText(Text($"Layout unavailable: {problem}", 10, strong: false, Math.Max(1, Bounds.Width - 24)),
                new(12, Math.Max(0, Bounds.Height - 22)));
        }
    }

    /// <summary>
    /// Labels are a last pass, so no circle overdraws a name, and each is placed where it collides with no label already
    /// placed: below its node, above, right or left, avoiding other nodes where it can. The selection and keyboard focus
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
        GraphDisplayNode[] candidates = [.. required, .. order
            .Where(node => !required.Contains(node))
            .OrderBy(node => partlySelected.Contains(node.Key) ? 0 : 1)
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
    /// Where a label block fits beside its node: the first side, in reading order, that stays in the pane and overlaps
    /// no placed label, preferring one that covers no other node. A label that must show takes the first side in the pane.
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
        const double Gap = 3;
        Rect[] sides =
        [
            new(point.X - (width / 2), point.Y + radius + Gap, width, height),
            new(point.X - (width / 2), point.Y - radius - Gap - height, width, height),
            new(point.X + radius + Gap + 3, point.Y - (height / 2), width, height),
            new(point.X - radius - Gap - 3 - width, point.Y - (height / 2), width, height),
        ];
        var pane = new Rect(Bounds.Size);
        Rect[] inPane = [.. sides.Where(side => pane.Contains(side))];
        Rect[] clear = [.. inPane.Where(side => !placed.Any(other => other.Intersects(side)))];
        foreach (Rect side in clear)
        {
            if (!points.Any(entry => entry.Key != key && Covers(side, entry.Value, radii[entry.Key])))
            {
                return side;
            }
        }

        return clear.Length > 0 ? clear[0] : mustShow && inPane.Length > 0 ? inPane[0] : null;
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

    /// <summary>The keyboard's order: busiest first, then by name, so arrows walk from what matters most.</summary>
    internal static IReadOnlyList<GraphDisplayNode> Traversal(GraphDisplay display) =>
    [
        .. display.Nodes
            .OrderByDescending(node => node.Observations)
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
        InvalidateVisual();
        e.Handled = true;
    }

    public GraphView() => DoubleTapped += OpenGroup;

    private void OpenGroup(object? sender, TappedEventArgs e)
    {
        // Opening a group draws its members one by one (§6.3's explicit expansion): the double click is Enter on its row.
        if (DataContext is WorkspaceViewModel viewModel && HitTest(viewModel, e.GetPosition(this)) is { } key)
        {
            e.Handled = viewModel.OpenGraphGroup(key);
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
        long scale = display.Nodes.Count == 0 ? 0 : display.Nodes.Max(node => node.Observations);
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
        if (!labels.TryGetValue((text, width, size, strong), out FormattedText? formatted))
        {
            if (labels.Count > 512) labels.Clear();
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new("Segoe UI"), size,
                strong ? TextBrush : MutedTextBrush)
            {
                MaxTextWidth = width,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            labels[(text, width, size, strong)] = formatted;
        }

        return formatted;
    }
}
