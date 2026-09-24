using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

public sealed class GraphView : Control
{
    private const ThemeMode Mode = ThemeMode.Dark;

    /// <summary>Below this pane width the graph drops secondary labels instead of overlapping them.</summary>
    private const double NarrowPaneWidth = 620;

    /// <summary>Half the drawn label width, kept clear on both sides so no name is clipped at an edge.</summary>
    private const double LabelInset = 52;

    private static readonly IBrush NodeBrush = Token(ThemePalette.Surfaces(Mode).Elevated);
    private static readonly IBrush NodeBorderBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly IBrush SelectedBrush = Token(ThemePalette.Surfaces(Mode).Accent);
    private static readonly IBrush TextBrush = Token(ThemePalette.Surfaces(Mode).Ink);
    private static readonly IBrush MutedTextBrush = Token(ThemePalette.Surfaces(Mode).MutedInk);

    private static SolidColorBrush Token(Srgb value) => new SolidColorBrush(ThemeResources.ToColor(value));

    /// <summary>Mechanism owns hue; evidence quality owns the dash pattern, never a hue (section 6.6).</summary>
    private static SolidColorBrush MechanismBrush(Mechanism mechanism) =>
        new SolidColorBrush(ThemeResources.FillOf(mechanism, Mode));

    private static Pen EvidencePen(Mechanism mechanism, RelationStrength strength, double thickness)
    {
        var pen = new Pen(MechanismBrush(mechanism), thickness);
        pen.DashStyle = strength switch
        {
            RelationStrength.Direct => null,
            RelationStrength.Correlated => new DashStyle([6, 3], 0),
            _ => new DashStyle([2, 3], 0),
        };

        return pen;
    }
    private int keyboardIndex;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        IReadOnlyList<ProcessNode> nodes = viewModel.Snapshot.Processes;
        var positions = nodes.ToDictionary(node => node.Id, node => Position(node, viewModel));
        string? highlighted = viewModel.HighlightedEdgeKey;
        foreach (CommunicationEdge edge in viewModel.Snapshot.Edges)
        {
            Point source = positions[edge.SourceId];
            Point target = positions[edge.TargetId];
            if (edge.Key == highlighted)
            {
                // The edge that contributes to the rung is haloed in the accent under its own stroke, so its hue,
                // thickness and dash keep their meanings (section 3.2 L3 and L5, section 6.6).
                context.DrawLine(new Pen(SelectedBrush, 9) { LineCap = PenLineCap.Round }, source, target);
            }

            // Hue states the mechanism, thickness states magnitude and the dash pattern states evidence
            // quality. No channel carries two meanings, and none of them is colour alone (section 6.6, R14).
            SolidColorBrush brush = MechanismBrush(edge.Mechanism);
            double thickness = edge.Strength == RelationStrength.Direct ? 3 : 1.5;
            context.DrawLine(EvidencePen(edge.Mechanism, edge.Strength, thickness), source, target);
            Point middle = new((source.X + target.X) / 2, (source.Y + target.Y) / 2);
            if (edge.Strength is RelationStrength.Candidate or RelationStrength.Unresolved)
            {
                context.DrawEllipse(Brushes.Transparent, new Pen(brush, 2), middle, 5, 5);
            }
        }

        for (int index = 0; index < nodes.Count; index++)
        {
            ProcessNode node = nodes[index];
            Point point = positions[node.Id];
            bool selected = viewModel.SelectedProcess?.Id == node.Id;
            if (selected)
            {
                context.DrawEllipse(Brushes.Transparent, new Pen(SelectedBrush, 3), point, 30, 30);
            }

            context.DrawEllipse(NodeBrush, new Pen(NodeBorderBrush, index == keyboardIndex && IsFocused ? 3 : 1.5), point, 24, 24);
        }

        // Labels are a second pass so a later node's circle can never overdraw an earlier node's name.
        bool roomForDetail = Bounds.Width >= NarrowPaneWidth;
        foreach (ProcessNode node in nodes)
        {
            Point point = positions[node.Id];
            // At the minimum window height, a label under the upper band can collide with a
            // lower-band node. Place upper-band labels above their circles and lower-band labels
            // below; the chosen side is a presentation fact, not a graph or evidence change.
            bool labelAbove = viewModel.GraphPositions[node.Id].Y < 0.5;
            int nameOffset = labelAbove ? (roomForDetail ? -61 : -42) : 31;
            DrawText(context, node.Name,
                new(point.X - 36, point.Y + nameOffset),
                12, TextBrush, 76);

            // A narrow pane drops the secondary line rather than letting two labels collide. The value is
            // never lost: the ranked list and the relationship table still carry the process id (R15).
            if (roomForDetail)
            {
                DrawText(context, $"PID {node.ProcessId}",
                    new(point.X - 31, point.Y + (labelAbove ? -45 : 47)),
                    10, MutedTextBrush, 70);
            }
        }

        if (viewModel.GraphLayoutProblem is { } problem)
        {
            DrawText(context, $"Layout unavailable: {problem}",
                new(12, Math.Max(0, Bounds.Height - 22)), 10, MutedTextBrush,
                Math.Max(0, Bounds.Width - 24));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        Point pointer = e.GetPosition(this);
        for (int index = 0; index < viewModel.Snapshot.Processes.Count; index++)
        {
            ProcessNode node = viewModel.Snapshot.Processes[index];
            Point point = Position(node, viewModel);
            double dx = point.X - pointer.X;
            double dy = point.Y - pointer.Y;
            if ((dx * dx) + (dy * dy) <= 30 * 30)
            {
                keyboardIndex = index;
                viewModel.SelectProcess(node.Id);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not WorkspaceViewModel viewModel || viewModel.Snapshot.Processes.Count == 0)
        {
            return;
        }

        int delta = e.Key switch
        {
            Key.Right or Key.Down => 1,
            Key.Left or Key.Up => -1,
            _ => 0,
        };
        if (delta != 0)
        {
            keyboardIndex = (keyboardIndex + delta + viewModel.Snapshot.Processes.Count) % viewModel.Snapshot.Processes.Count;
            viewModel.SelectProcess(viewModel.Snapshot.Processes[keyboardIndex].Id);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Places a node so that its label fits inside the pane. The inset is the label half-width, so a node
    /// near an edge keeps its name readable instead of having it clipped (§6.8 legibility).
    /// </summary>
    private Point Position(ProcessNode node, WorkspaceViewModel viewModel)
    {
        GraphPoint graph = viewModel.GraphPositions[node.Id];
        return new(
            LabelInset + (graph.X * Math.Max(0, Bounds.Width - (LabelInset * 2))),
            // Reserve the node radius above and the two-line label below. Using the intervening
            // height keeps separate group bands legible even at the minimum window size.
            24 + (graph.Y * Math.Max(0, Bounds.Height - 72)));
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new("Segoe UI"), size, brush)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment.Center,
        };
        context.DrawText(formatted, origin);
    }
}
