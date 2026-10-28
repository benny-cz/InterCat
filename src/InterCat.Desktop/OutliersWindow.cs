using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>A row the outliers window lists: its rank and ends, then where its records fall among its windows'.</summary>
internal sealed record OutlierRow(int Rank, OutlierJudgement Judged, string Title, string Said) : IAccessibleRow
{
    public string AccessibleName => $"{Title}: {Said}.";

    public override string ToString() => $"{Title} · {Said}";
}

/// <summary>A baseline the outliers window offers: all the session keeps before the time scope, or all after it.</summary>
internal sealed record OutlierBaseline(string Label, bool Before)
{
    public override string ToString() => Label;
}

/// <summary>
/// Outliers (`contracts/outliers-v1.md` §7) in `icat outliers`'s words: each row of the time scope judged against the windows
/// of its baseline - all the session keeps before the scope, or all after it - every window as long as the scope; the rows
/// above or below every window listed, the most records beyond first, the chosen one's windows drawn beside it; how often
/// chance alone would put a row there; and what is not judged, with why. Show in the window closes it with the row chosen,
/// which the workspace opens as a comparison's row is opened; the window itself changes nothing.
/// </summary>
internal sealed class OutliersWindow : Window, IDisposable
{
    /// <summary>Why the window's actions wait while the session is read.</summary>
    private const string Reading = "The rows are being judged.";

    private readonly string path;
    private readonly Guid sessionId;
    private readonly long expectedGeneration;
    private readonly TimeRange compared;
    private readonly TimeRange within;
    private readonly SessionClock clock;
    private readonly EvidencePolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox baseline = new() { MinWidth = 360 };
    private readonly ComboBox rowsOf = new() { MinWidth = 260 };
    private readonly TextBlock baselineSaid = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox outliers = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock held = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock windowsHeld = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock beyond = new() { TextWrapping = TextWrapping.Wrap };
    private readonly OutlierWindowsControl chart = new();
    private readonly TextBlock chartSaid = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock chance = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock notJudged = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button show = new() { Content = "Show in the window", Classes = { "primary" } };
    private readonly Button close = new() { Content = "Close" };
    private CancellationTokenSource? reading;
    private OutlierReading? judged;
    private OutlierRow[] rows = [];
    private bool loading;
    private bool closed;
    private bool disposed;

    /// <param name="compared">The time scope, which is judged.</param>
    /// <param name="within">The time a baseline after the scope runs to: the recording, or else the span of the session's records.</param>
    /// <param name="clock">The time base the window states its times in.</param>
    /// <param name="policy">The evidence policy InterCat's window counts under, so the two count alike.</param>
    public OutliersWindow(string path, Guid sessionId, long expectedGeneration, TimeRange compared, TimeRange within, bool recorded,
        SessionClock clock, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.path = path;
        this.sessionId = sessionId;
        this.expectedGeneration = expectedGeneration;
        this.compared = compared;
        this.within = within;
        this.clock = clock;
        this.policy = policy;
        Title = "InterCat · Outliers";
        Width = 1_000;
        Height = 720;
        MinWidth = 760;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CultureInfo culture = CultureInfo.CurrentCulture;

        // Before the scope is all the session keeps there; after it runs to the recording's end, where some time is left.
        var baselines = new List<OutlierBaseline> { new("All the session keeps before it", Before: true) };
        if (compared.EndTicks < within.EndTicks)
        {
            baselines.Add(new((recorded ? "All after it, to the end of the recording" : "All after it, to the session's last record"),
                Before: false));
        }

        baseline.ItemsSource = baselines;
        baseline.SelectedIndex = 0;
        rowsOf.ItemsSource = new[] { "Each process", "Each executable, its processes together" };
        rowsOf.SelectedIndex = 0;
        AutomationProperties.SetName(baseline, "The baseline the time scope is judged against");
        AutomationProperties.SetName(baselineSaid, "The baseline and its windows");
        AutomationProperties.SetName(rowsOf, "What each row is: one process, or one executable's processes together");
        AutomationProperties.SetName(status, "Outliers status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered over each interval");
        AutomationProperties.SetName(chartSaid, "The windows chart's chosen bar");
        AutomationProperties.SetLiveSetting(chartSaid, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(chance, "How often chance alone puts a row outside its windows' range");
        AutomationProperties.SetName(notJudged, "What is not judged");
        AutomationProperties.SetName(held, "Its records in the time scope");
        var note = new TextBlock { Text = OutlierText.Note, TextWrapping = TextWrapping.Wrap, FontSize = 11 };

        // A row reads by its two ends, then by where its records fall among its windows', each wrapping within the list.
        ScrollViewer.SetHorizontalScrollBarVisibility(outliers, ScrollBarVisibility.Disabled);
        outliers.ItemTemplate = new FuncDataTemplate<OutlierRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Said, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        });
        AccessibleItems.Name(outliers);
        AutomationProperties.SetName(outliers, "The rows above or below every window of their baseline, the most records beyond "
            + "first. Enter shows the chosen row in the window.");
        outliers.SelectionChanged += (_, _) => ShowSelected();
        outliers.DoubleTapped += (_, _) => ChooseSelected();
        outliers.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            ChooseSelected();
            key.Handled = true;
        };
        baseline.SelectionChanged += (_, _) => JudgeAsync().Watched();
        rowsOf.SelectionChanged += (_, _) => JudgeAsync().Watched();
        chart.CursorMoved += (_, _) => chartSaid.Text = chart.Describe() ?? string.Empty;
        show.Click += (_, _) => ChooseSelected();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 6,
        };
        Place(fields, 0, "Compared", new TextBlock
        {
            Text = ComparisonText.Interval(compared, clock, culture) + ", the window's time scope",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Place(fields, 1, "Baseline", new StackPanel { Spacing = 4, Children = { baseline, baselineSaid } });
        Place(fields, 2, "Rows", rowsOf);

        // The chosen row's details scroll within their column, so its chart never runs over what is said beneath.
        var details = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 6, Children = { heading, held, windowsHeld, beyond, chart, chartSaid } },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,6*"), ColumnSpacing = 14 };
        Grid.SetColumn(details, 1);
        body.Children.Add(outliers);
        body.Children.Add(details);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { show, close },
        };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        var header = new StackPanel { Spacing = 6, Children = { fields, status, coverage } };
        var beneath = new StackPanel { Spacing = 4, Children = { chance, notJudged, note } };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(beneath, 2);
        Grid.SetRow(actions, 3);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(beneath);
        grid.Children.Add(actions);
        Content = grid;

        // Until the rows are judged, the window's actions wait for them, saying so.
        loading = true;
        ShowSelected();
        Opened += (_, _) => JudgeAsync().Watched();
        Closed += (_, _) => Dispose();
    }

    /// <summary>
    /// Why InterCat's window cannot show a row - an executable's rung where it groups processes otherwise - said on Show in
    /// the window; null where it can.
    /// </summary>
    public Func<ComparedRelationship, string?>? WhyNotShown { get; init; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        reading?.Cancel();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>
    /// Judges the time scope against the baseline chosen, its rows of the kind chosen, reading the session afresh; a newer
    /// request lets an earlier one go, whose answer is dropped (ADR-057).
    /// </summary>
    private async Task JudgeAsync()
    {
        if (closed) return;
        reading?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        reading = request;
        bool before = (baseline.SelectedItem as OutlierBaseline)?.Before != false;
        ComparisonGrouping grouping = rowsOf.SelectedIndex == 1 ? ComparisonGrouping.Executable : ComparisonGrouping.Process;
        loading = true;
        judged = null;
        status.Text = "Judging the time scope's rows against the windows of its baseline…";
        baselineSaid.Text = string.Empty;
        List();
        // The baseline is read with the rows, so where it begins is the session's as it is now; one too short is refused in
        // the command line's words.
        (OutlierReading? Read, string? Refusal) Judge()
        {
            SessionStore store = SharedSessionStores.Open(path, sessionId);
            TimeRange? against = before ? ComparisonOutliers.Before(store, compared) : new TimeRange(compared.EndTicks, within.EndTicks);
            if (against is not { } chosen)
            {
                return (null, "Nothing the session keeps is before the time scope; judge it against the time after it, or choose "
                    + "a later scope.");
            }

            return ComparisonOutliers.Windows(chosen, compared, out string? unwindowed, CultureInfo.CurrentCulture) is null
                ? (null, unwindowed)
                : (ComparisonOutliers.Read(store, chosen, compared, policy, grouping, request.Token), null);
        }

        string? refusal = null;
        OutlierReading? read = null;
        try
        {
            (read, refusal) = await Task.Run(Judge, request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A newer request, or closing, let this one go: its answer is dropped.
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            refusal = "Could not judge the rows: " + exception.Message;
        }

        if (closed || !ReferenceEquals(reading, request)) return;
        loading = false;
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (read is null)
        {
            status.Text = refusal;
        }
        else if (read.Comparison.Generation < expectedGeneration)
        {
            status.Text = "This folder no longer holds the generation InterCat's window shows. Close this window and open it "
                + "again from that window.";
        }
        else
        {
            judged = read;
            status.Text = string.Create(culture, $"Generation {read.Comparison.Generation:N0}")
                + (read.Comparison.Generation == expectedGeneration ? string.Empty
                    : string.Create(culture, $" (InterCat's window shows generation {expectedGeneration:N0}: ")
                        + "a row only the newer one holds is not shown there)")
                + " · " + OutlierText.Found(read, culture)
                + (read.Comparison.Policy == EvidencePolicy.IncludeCorrelated ? string.Empty
                    : $" · counted under the {CommandArgument.Typed(read.Comparison.Policy)} evidence policy, as InterCat's window counts");
            baselineSaid.Text = OutlierText.Baseline(read, clock, culture);
            coverage.Text = string.Join(Environment.NewLine, new[]
            {
                ComparisonText.Coverage("the baseline", read.Comparison.BaselineCoverage),
                ComparisonText.Coverage("the time scope", read.Comparison.ComparedCoverage),
            }.OfType<string>());
        }

        List();
        KeepKeyboard();
    }

    /// <summary>Lists the rows above or below every window, choosing the first, and says what chance and the unjudged rows are.</summary>
    private void List()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        rows = judged is not { } read ? [] :
        [
            .. read.Outliers.Select((row, index) => new OutlierRow(index + 1, row,
                string.Create(culture, $"{index + 1:N0}. ") + ComparisonText.Ends(row.Row, read.Comparison, "and") + " · "
                    + ComparisonText.Over(row.Row),
                CountText.Of(row.Row.Compared.Records, "record") + " · its windows " + OutlierText.Windows(row, culture) + " · "
                    + OutlierText.Beyond(row, culture) + " · " + OutlierText.Side(row.Side))),
        ];
        outliers.ItemsSource = rows;
        outliers.SelectedIndex = rows.Length > 0 ? 0 : -1;
        chance.Text = judged is { } shown ? OutlierText.Chance(shown, culture) ?? string.Empty : string.Empty;
        notJudged.Text = judged is { } any
            ? string.Join(Environment.NewLine, OutlierText.NotJudged(any, culture).Concat(ComparisonText.Grouping(any.Comparison, culture)))
            : string.Empty;
        chance.IsVisible = !string.IsNullOrEmpty(chance.Text);
        notJudged.IsVisible = !string.IsNullOrEmpty(notJudged.Text);
        ShowSelected();
    }

    /// <summary>
    /// Says the chosen row's records in the time scope and in its windows and how far beyond, drawing its windows; with none
    /// chosen, why the list holds none; and offers Show in the window, or says why not.
    /// </summary>
    private void ShowSelected()
    {
        OutlierRow? row = outliers.SelectedItem as OutlierRow;
        CultureInfo culture = CultureInfo.CurrentCulture;
        string? why = loading ? Reading
            : judged is null ? "No rows are judged; the status line says why."
            : row is null ? (judged.Judged.Count == 0 ? OutlierText.NoneJudged : OutlierText.NoneOutside)
            : WhyNotShown?.Invoke(row.Judged.Row);
        Offering.Offer(show, why, "Close this window and show the chosen row in InterCat's window, as a comparison's row is shown");
        held.IsVisible = windowsHeld.IsVisible = beyond.IsVisible = chart.IsVisible = chartSaid.IsVisible = row is not null;
        if (row is null || judged is not { } read)
        {
            heading.Text = judged is not { } shown ? string.Empty
                : shown.Comparison.Relationships.Count == 0 ? OutlierText.NoRow(shown.Comparison)
                : shown.Judged.Count == 0 ? OutlierText.NoneJudged
                : OutlierText.NoneOutside;
            chart.Show(null, [], compared, Range);
            return;
        }

        OutlierJudgement judgement = row.Judged;
        heading.Text = string.Create(culture, $"Row {row.Rank:N0}: ") + ComparisonText.Ends(judgement.Row, read.Comparison, "and")
            + " over " + ComparisonText.Over(judgement.Row);
        held.Text = "In the time scope: " + CountText.Of(judgement.Row.Compared.Records, "record") + ", "
            + OutlierText.Side(judgement.Side);
        windowsHeld.Text = string.Create(culture, $"In its {read.Windows.Count:N0} windows: ") + OutlierText.Windows(judgement, culture);
        beyond.Text = "Beyond: " + OutlierText.Beyond(judgement, culture);
        chart.Show(judgement, read.Windows, compared, Range);
    }

    /// <summary>An interval in the window's time base, as the chart states the bar chosen.</summary>
    private string Range(TimeRange interval) => clock.HalfOpenRange(interval, CultureInfo.CurrentCulture);

    /// <summary>
    /// Gives the chosen row the keyboard once the rows are judged - or Close, with none to choose - unless a control of the
    /// window has it. Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (outliers.ContainerFromIndex(outliers.SelectedIndex) ?? close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>The row chosen, which the workspace shows as a comparison's row.</summary>
    private void ChooseSelected()
    {
        if (!show.IsEnabled || outliers.SelectedItem is not OutlierRow row) return;
        Close(row.Judged.Row);
    }

    /// <summary>A label and its control on one row of the fields.</summary>
    private static void Place(Grid fields, int row, string label, Control control)
    {
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        Grid.SetRow(name, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        fields.Children.Add(name);
        fields.Children.Add(control);
    }
}
