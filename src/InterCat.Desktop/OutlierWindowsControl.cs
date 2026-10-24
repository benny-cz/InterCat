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
/// A judged row's windows as the outliers window draws them (`contracts/outliers-v1.md` §7): a bar per window of its
/// baseline, the earliest first, then apart and wider the compared interval's, all on one scale of records, with a line at
/// the most and at the fewest any window held, so a compared bar above or below every window shows as one past a line. A
/// person moves a cursor with Left, Right, Home and End, or a click, and the window states the bar chosen in words.
/// </summary>
internal sealed class OutlierWindowsControl : Control
{
    /// <summary>How tall the chart is drawn: its legend above, its bars beneath.</summary>
    public const double ChartHeight = 120;

    private const double LegendHeight = 20;
    private const double Inset = 6;

    /// <summary>How many window slots wide the compared interval's bar is, and how many are left empty before it.</summary>
    private const int ComparedSlots = 3, GapSlots = 1;

    /// <summary>The chart's own name, which the bar its cursor is on follows while it has the keyboard.</summary>
    internal const string SpokenName = "The chosen row's windows";

    private static readonly Dictionary<ThemeMode, Ink> Inks = [];
    private OutlierJudgement? judged;
    private IReadOnlyList<TimeRange> windows = [];
    private TimeRange compared;
    private Func<TimeRange, string> range = interval => WorkspaceTime.FormatHalfOpenRange(interval, CultureInfo.CurrentCulture);
    private int cursor;

    public OutlierWindowsControl()
    {
        Focusable = true;
        Say();
    }

    /// <summary>The bar the cursor is on moved, or the row it is on changed.</summary>
    public event EventHandler? CursorMoved;

    /// <summary>The bar the cursor is on: a window's, from the first, 0; or, one past the last window, the time scope's.</summary>
    public int ChosenBar => cursor;

    /// <summary>How many bars are drawn: a window's each, and the compared interval's; none before a row is shown.</summary>
    public int Bars => judged is null ? 0 : windows.Count + 1;

    /// <summary>
    /// Draws <paramref name="row"/>'s windows, <paramref name="baselineWindows"/>, beside <paramref name="comparedInterval"/>,
    /// each interval stated in <paramref name="words"/>; nothing with no row. The cursor starts on the compared interval's bar.
    /// </summary>
    public void Show(OutlierJudgement? row, IReadOnlyList<TimeRange> baselineWindows, TimeRange comparedInterval,
        Func<TimeRange, string> words)
    {
        ArgumentNullException.ThrowIfNull(baselineWindows);
        ArgumentNullException.ThrowIfNull(words);
        judged = row;
        windows = baselineWindows;
        compared = comparedInterval;
        range = words;
        cursor = Math.Max(Bars - 1, 0);
        Moved();
    }

    /// <summary>Moves the cursor to a bar, as the arrow keys or a click do; a test uses it.</summary>
    internal void MoveTo(int bar)
    {
        if (bar < 0 || bar >= Bars) return;
        cursor = bar;
        Moved();
    }

    /// <summary>
    /// The chosen bar in `icat outliers`'s words: a window's interval and its records, or the time scope's - the compared
    /// interval - with where they fall among the windows'.
    /// </summary>
    internal string? Describe()
    {
        if (judged is not { } row) return null;
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (cursor < windows.Count)
        {
            return string.Create(culture, $"Window {cursor + 1:N0} of {windows.Count:N0}, ") + range(windows[cursor])
                + ": " + CountText.Of(row.Windows[cursor], "record");
        }

        return "The time scope, " + range(compared) + ": " + CountText.Of(row.Row.Compared.Records, "record") + ", "
            + OutlierText.Side(row.Side) + (row.Side == OutlierSide.Within ? "." : ", " + OutlierText.Beyond(row, culture) + ".");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this, "windows chart",
        "Left and Right choose a bar, Home the first window and End the time scope; the line below states the bar "
        + "chosen in words.", Describe);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, ChartHeight);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        int last = Bars - 1;
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
        if (BarAt(e.GetPosition(this).X) is { } bar) MoveTo(bar);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        Ink ink = Inks.TryGetValue(ThemeResources.CurrentMode, out Ink? known) ? known : Inks[ThemeResources.CurrentMode] = new(ThemeResources.CurrentMode);
        Rect plot = Plot();
        context.FillRectangle(ink.Plot, plot);

        // The legend: which bars are the windows', which the compared interval's, and what the two lines mark.
        double x = plot.X;
        foreach ((IBrush brush, string label) in new[] { (ink.Window, "Its windows"), (ink.Compared, "The time scope") })
        {
            context.FillRectangle(brush, new Rect(x, 5, 10, 10));
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink.Text);
            context.DrawText(text, new Point(x + 14, 3));
            x += 14 + text.Width + 14;
        }

        context.DrawLine(ink.Line, new Point(x, 10), new Point(x + 16, 10));
        context.DrawText(new FormattedText("their most and fewest", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, ink.Text), new Point(x + 20, 3));
        if (judged is not { } row || windows.Count == 0) return;

        // One scale of records for every bar, so a taller bar is more records whichever interval it is of.
        long highest = Math.Max(1, Math.Max(row.Most, row.Row.Compared.Records));
        double slot = plot.Width / (windows.Count + GapSlots + ComparedSlots);
        double gap = Math.Clamp(slot * 0.15, 0.3, 3);
        for (int index = 0; index < windows.Count; index++)
        {
            Bar(context, ink.Window, plot.X + (index * slot) + (gap / 2), Math.Max(slot - gap, 0.5), plot, row.Windows[index], highest);
        }

        double comparedLeft = plot.X + ((windows.Count + GapSlots) * slot);
        Bar(context, ink.Compared, comparedLeft + (gap / 2), Math.Max((ComparedSlots * slot) - gap, 1), plot, row.Row.Compared.Records,
            highest);
        context.DrawLine(ink.Divider, new Point(comparedLeft - (GapSlots * slot / 2), plot.Y),
            new Point(comparedLeft - (GapSlots * slot / 2), plot.Bottom));

        // The windows' range, across every bar, so the compared bar is read against it.
        foreach (long level in new[] { row.Most, row.Fewest }.Distinct())
        {
            double y = plot.Bottom - ((plot.Height - Inset) * level / highest);
            context.DrawLine(ink.Line, new Point(plot.X, y), new Point(plot.Right, y));
        }

        Rect chosen = cursor < windows.Count
            ? new Rect(plot.X + (cursor * slot), plot.Y, slot, plot.Height)
            : new Rect(comparedLeft, plot.Y, ComparedSlots * slot, plot.Height);
        context.DrawRectangle(Brushes.Transparent, IsFocused ? ink.Cursor : ink.Chosen, chosen.Deflate(0.75));
    }

    /// <summary>A bar of records, from the plot's foot, as tall as its share of the highest; none for none.</summary>
    private static void Bar(DrawingContext context, IBrush brush, double left, double width, Rect plot, long records, long highest)
    {
        if (records <= 0) return;
        double height = Math.Max(2, (plot.Height - Inset) * records / highest);
        context.FillRectangle(brush, new Rect(left, plot.Bottom - height, width, height));
    }

    private Rect Plot() => new(Inset, LegendHeight, Math.Max(Bounds.Width - (2 * Inset), 10), Math.Max(Bounds.Height - LegendHeight - Inset, 10));

    /// <summary>The bar a point across the chart is over: a window's, the compared interval's, or none in the gap between.</summary>
    private int? BarAt(double x)
    {
        Rect plot = Plot();
        if (Bars == 0 || x < plot.X || x >= plot.Right) return null;
        int slot = (int)((x - plot.X) / (plot.Width / (windows.Count + GapSlots + ComparedSlots)));
        return slot < windows.Count ? slot : slot < windows.Count + GapSlots ? null : windows.Count;
    }

    private void Moved()
    {
        Say();
        InvalidateVisual();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void Say() => CanvasAutomationPeer.Rename(this, Describe() is { } bar ? SpokenName + ", " + bar
        : SpokenName + ": choose a row to draw its windows");

    // Brushes are built once per theme mode and reused every frame (R11), as the other charts' are.
    private sealed class Ink(ThemeMode mode)
    {
        public IBrush Plot { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Plot));
        public IBrush Window { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).MutedInk));
        public IBrush Compared { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Accent));
        public IBrush Text { get; } = new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink));
        public Pen Divider { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Divider)), 1);
        public Pen Line { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink)), 1)
        {
            DashStyle = new DashStyle([3, 3], 0),
        };
        public Pen Cursor { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink)), 1.5);
        public Pen Chosen { get; } = new(new SolidColorBrush(ThemeResources.ToColor(ThemePalette.Surfaces(mode).MutedInk)), 1);
    }
}
