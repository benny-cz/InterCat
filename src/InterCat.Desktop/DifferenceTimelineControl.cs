using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A comparison row's difference timeline as the Compare window draws it (`contracts/comparison-v1.md` §8d): a slot per
/// column pair, the baseline's bar beside the compared interval's on one rate scale, every column pair that cannot be
/// compared hatched as the timeline hatches what the capture did not cover, so a quiet bar there never reads as nothing
/// happening. A person moves a column cursor with Left, Right, Home and End, or a click, and the window states the
/// column chosen in `icat compare`'s words.
/// </summary>
internal sealed class DifferenceTimelineControl : Control
{
    /// <summary>How tall the chart is drawn: its legend above, its bars beneath.</summary>
    public const double ChartHeight = 118;

    private const double LegendHeight = 20;
    private const double Inset = 6;

    /// <summary>The chart's own name, which the column its cursor is on follows while it has the keyboard.</summary>
    internal const string SpokenName = "The chosen row's difference timeline";

    private static readonly Dictionary<ThemeMode, Ink> Inks = [];
    private ComparisonTimeline? timeline;
    private ComparedRelationship? row;
    private Func<TimeRange, string> baselineRange = range => WorkspaceTime.FormatHalfOpenRange(range, CultureInfo.CurrentCulture);
    private Func<TimeRange, string> comparedRange = range => WorkspaceTime.FormatHalfOpenRange(range, CultureInfo.CurrentCulture);
    private int cursor;

    public DifferenceTimelineControl()
    {
        Focusable = true;
        Say();
    }

    /// <summary>The column the cursor is on moved, or the row or timeline it is on changed.</summary>
    public event EventHandler? CursorMoved;

    /// <summary>The column the cursor is on, from the first, 0.</summary>
    public int ChosenColumn => cursor;

    /// <summary>The row's columns drawn, in order; none before a row with a timeline is shown.</summary>
    public IReadOnlyList<ComparedRelationship> Cells => timeline is { } shown && row is { } chosen ? shown.Of(chosen) : [];

    /// <summary>
    /// Draws <paramref name="chosen"/>'s columns of <paramref name="shown"/>, each interval stated by its own range's words;
    /// nothing with either missing. The cursor keeps its column as far as the new row has it.
    /// </summary>
    public void Show(ComparisonTimeline? shown, ComparedRelationship? chosen, Func<TimeRange, string> baselineWords,
        Func<TimeRange, string> comparedWords)
    {
        ArgumentNullException.ThrowIfNull(baselineWords);
        ArgumentNullException.ThrowIfNull(comparedWords);
        timeline = shown;
        row = chosen;
        baselineRange = baselineWords;
        comparedRange = comparedWords;
        cursor = Math.Clamp(cursor, 0, Math.Max(Cells.Count - 1, 0));
        Moved();
    }

    /// <summary>Moves the cursor to a column, as the arrow keys or a click do; a test uses it.</summary>
    internal void MoveTo(int column)
    {
        if (column < 0 || column >= Cells.Count) return;
        cursor = column;
        Moved();
    }

    /// <summary>
    /// The chosen column in `icat compare`'s words: its two intervals, each one's value, and how it changed - or why it
    /// cannot be said, so a column the capture did not collect is not heard as a quiet one (R21).
    /// </summary>
    internal string? Describe()
    {
        if (timeline is not { } shown || Cells is not { Count: > 0 } cells) return null;
        CultureInfo culture = CultureInfo.CurrentCulture;
        ComparedRelationship cell = cells[cursor];
        ComparisonColumn column = shown.Columns[cursor];
        ComparisonMetric metric = shown.Whole.Metric;
        return string.Create(culture, $"Column {cursor + 1:N0} of {cells.Count:N0}: ")
            + baselineRange(column.Baseline) + " " + ComparisonText.Value(cell.Baseline, column.Baseline, metric, culture)
            + " against " + comparedRange(column.Compared) + " " + ComparisonText.Value(cell.Compared, column.Compared, metric, culture)
            + ", " + ComparisonText.Change(cell, column.Baseline, column.Compared, metric, culture)
            + (cell.Reason is { } reason ? ": " + reason : ".");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "difference timeline",
        "Left and Right choose a column, Home and End the first and last; the line below states the column chosen in words.",
        Describe);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, ChartHeight);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        int last = Cells.Count - 1;
        if (last < 0) return;
        switch (e.Key)
        {
            case Key.Left:
                MoveTo(Math.Max(0, cursor - 1));
                break;
            case Key.Right:
                MoveTo(Math.Min(last, cursor + 1));
                break;
            case Key.Home:
                MoveTo(0);
                break;
            case Key.End:
                MoveTo(last);
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
        if (ColumnAt(e.GetPosition(this).X) is { } column) MoveTo(column);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        Ink ink = Inks.TryGetValue(ThemeResources.CurrentMode, out Ink? known) ? known : Inks[ThemeResources.CurrentMode] = new(ThemeResources.CurrentMode);
        Rect plot = Plot();
        context.FillRectangle(ink.Plot, plot);

        // The legend: which bar is which interval's.
        double x = plot.X;
        foreach ((IBrush brush, string label) in new[] { (ink.Baseline, "Baseline"), (ink.Compared, "Compared") })
        {
            context.FillRectangle(brush, new Rect(x, 5, 10, 10));
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink.Text);
            context.DrawText(text, new Point(x + 14, 3));
            x += 14 + text.Width + 14;
        }

        if (timeline is not { } shown || Cells is not { Count: > 0 } cells) return;

        // One rate scale for both intervals, so a taller bar is a higher rate whichever interval it is of.
        double highest = 0;
        for (int index = 0; index < cells.Count; index++)
        {
            highest = Math.Max(highest, Math.Max(cells[index].Baseline.PerSecond(shown.Columns[index].Baseline) ?? 0,
                cells[index].Compared.PerSecond(shown.Columns[index].Compared) ?? 0));
        }

        double slot = plot.Width / cells.Count;
        double gap = Math.Clamp(slot * 0.12, 0.5, 4);
        double bar = Math.Max((slot - (3 * gap)) / 2, 1);
        for (int index = 0; index < cells.Count; index++)
        {
            Rect area = new(plot.X + (index * slot), plot.Y, slot, plot.Height);
            Bar(context, ink.Baseline, area.X + gap, bar, plot, cells[index].Baseline.PerSecond(shown.Columns[index].Baseline), highest);
            Bar(context, ink.Compared, area.X + (2 * gap) + bar, bar, plot, cells[index].Compared.PerSecond(shown.Columns[index].Compared),
                highest);

            // Where a column pair cannot be compared it is hatched across its bars, as an uncovered column is, never erasing them.
            if (cells[index].Change == ComparisonChange.NotComparable) TimelineView.DrawCoverageGap(context, area.Deflate(1));
            if (index > 0) context.DrawLine(ink.Divider, new Point(area.X, plot.Y), new Point(area.X, plot.Bottom));
        }

        context.DrawRectangle(Brushes.Transparent, IsFocused ? ink.Cursor : ink.Chosen,
            new Rect(plot.X + (cursor * slot), plot.Y, slot, plot.Height).Deflate(0.75));
    }

    /// <summary>A bar of a rate, from the plot's foot, as tall as its share of the highest; none for no rate.</summary>
    private static void Bar(DrawingContext context, IBrush brush, double left, double width, Rect plot, double? rate, double highest)
    {
        if (rate is not > 0 || highest <= 0) return;
        double height = Math.Max(2, (plot.Height - Inset) * rate.Value / highest);
        context.FillRectangle(brush, new Rect(left, plot.Bottom - height, width, height));
    }

    private Rect Plot() => new(Inset, LegendHeight, Math.Max(Bounds.Width - (2 * Inset), 10), Math.Max(Bounds.Height - LegendHeight - Inset, 10));

    private int? ColumnAt(double x)
    {
        Rect plot = Plot();
        int count = Cells.Count;
        if (count == 0 || x < plot.X || x >= plot.Right) return null;
        return Math.Min(count - 1, (int)((x - plot.X) / (plot.Width / count)));
    }

    private void Moved()
    {
        Say();
        InvalidateVisual();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void Say() => CanvasAutomationPeer.Rename(this, Describe() is { } column ? SpokenName + ", " + column
        : SpokenName + ": choose a row to draw its columns");

    // Brushes are built once per theme mode and reused every frame (R11), as the other timelines' are.
    private sealed class Ink(ThemeMode mode)
    {
        public IBrush Plot { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Plot));
        public IBrush Baseline { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).MutedInk));
        public IBrush Compared { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Accent));
        public IBrush Text { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink));
        public Pen Divider { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Divider)), 1);
        public Pen Cursor { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink)), 1.5);
        public Pen Chosen { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).MutedInk)), 1);
    }
}
