using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

public sealed class GraphView : Control
{
    private static readonly IBrush DirectBrush = Brush.Parse("#58B7F5");
    private static readonly IBrush CorrelatedBrush = Brush.Parse("#70D7B7");
    private static readonly IBrush CandidateBrush = Brush.Parse("#EFC36E");
    private static readonly IBrush NodeBrush = Brush.Parse("#173A52");
    private static readonly IBrush NodeBorderBrush = Brush.Parse("#65C8F5");
    private static readonly IBrush SelectedBrush = Brush.Parse("#F6D27C");
    private static readonly IBrush TextBrush = Brush.Parse("#E8F0FA");
    private static readonly IBrush MutedTextBrush = Brush.Parse("#91A4BA");
    private int keyboardIndex;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not WorkspaceViewModel viewModel || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        IReadOnlyList<ProcessNode> nodes = viewModel.Snapshot.Processes;
        var positions = nodes.ToDictionary(node => node.Id, Position);
        foreach (CommunicationEdge edge in viewModel.Snapshot.Edges)
        {
            Point source = positions[edge.SourceId];
            Point target = positions[edge.TargetId];
            IBrush brush = edge.Strength switch
            {
                RelationStrength.Direct => DirectBrush,
                RelationStrength.Correlated => CorrelatedBrush,
                _ => CandidateBrush,
            };
            double thickness = edge.Strength == RelationStrength.Direct ? 3 : 1.5;
            context.DrawLine(new Pen(brush, thickness), source, target);
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
            DrawText(context, node.Name, new(point.X - 36, point.Y + 31), 12, TextBrush, 76);
            DrawText(context, $"PID {node.ProcessId}", new(point.X - 31, point.Y + 47), 10, MutedTextBrush, 70);
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
            Point point = Position(node);
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

    private Point Position(ProcessNode node) => new(
        48 + (node.X * Math.Max(0, Bounds.Width - 96)),
        38 + (node.Y * Math.Max(0, Bounds.Height - 98)));

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
