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

namespace InterCat.Desktop;

/// <summary>A baseline the comparison offers: an interval it names, or, without one, one typed in seconds.</summary>
internal sealed record BaselineChoice(string Label, TimeRange? Interval)
{
    public override string ToString() => Label;
}

/// <summary>
/// The window's comparison of the time scope with another interval (`contracts/comparison-v1.md`, plan M12): each
/// relationship, and each process's connections to another host, that had a record in either, by records or by bytes sent
/// across, as rates over each whole interval, the most changed first, in the words `icat compare` uses (R18). A change is
/// stated only where both intervals measured alike; elsewhere both values stand, with the reason beneath the rows. Enter
/// on a row closes the window with it, and the workspace opens what it compares. Nothing is written but a redacted report
/// a person asks for and saves where they choose.
/// </summary>
internal sealed class SessionCompareWindow : Window, IDisposable, IChoosingWindow
{
    private readonly string path;
    private readonly Guid sessionId;
    private readonly TimeRange compared;
    private readonly SessionClock clock;
    private readonly EvidencePolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox baseline = new() { MinWidth = 360 };
    private readonly TextBox from = new() { Watermark = "from, s", Width = 110 };
    private readonly TextBox to = new() { Watermark = "to, s", Width = 110 };
    private readonly StackPanel typed;
    private readonly ComboBox metric = new() { MinWidth = 260 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox rows = new();
    private readonly TextBlock reasons = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button compare = new() { Content = "Compare" };
    private readonly Button show = new() { Content = "Show in the window", Classes = { "primary" } };
    private readonly Button share = new() { Content = "Share redacted…" };
    private readonly Button close = new() { Content = "Close" };
    private readonly TextBlock saved = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private IReadOnlyList<ComparisonRow> listed = [];
    private readonly Grid heading;

    // Each value column's width, the widest of its heading and its cells: every line of the table, its heading's included,
    // is laid out at these, so the columns line up.
    private double[] widths = [0, 0, 0, 0, 0];
    private CancellationTokenSource? reading;
    private bool comparing;
    private bool sharing;
    private bool shownOnce;
    private bool closed;
    private bool disposed;

    /// <param name="compared">The time scope, which every baseline is compared with.</param>
    /// <param name="within">The time a baseline is chosen within: the recording, or else the span of the session's records.</param>
    /// <param name="recorded">Whether <paramref name="within"/> is the recording.</param>
    /// <param name="clock">The time base the window states its times in.</param>
    /// <param name="policy">The evidence policy the window counts by, which the comparison counts by too.</param>
    public SessionCompareWindow(string path, Guid sessionId, TimeRange compared, TimeRange within, bool recorded,
        SessionClock clock, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.path = path;
        this.sessionId = sessionId;
        this.compared = compared;
        this.clock = clock;
        this.policy = policy;
        Title = "InterCat · Compare two intervals";
        Width = 1000;
        Height = 680;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        CultureInfo culture = CultureInfo.CurrentCulture;
        baseline.ItemsSource = Baselines(compared, within, recorded, clock, culture);
        baseline.SelectedIndex = 0;
        TrimmedChoices.Apply(baseline);
        metric.ItemsSource = new[] { "Records per second", "Bytes sent across per second" };
        metric.SelectedIndex = 0;
        typed = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            IsVisible = false,
            Children = { from, new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center }, to,
                new TextBlock { Text = "seconds of session time", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 } },
        };

        AutomationProperties.SetName(baseline, "The baseline the analysis interval is compared with");
        AutomationProperties.SetName(from, "The baseline's start, in seconds of session time");
        AutomationProperties.SetName(to, "The baseline's end, in seconds of session time");
        AutomationProperties.SetName(metric, "What each row is compared by");
        AutomationProperties.SetName(status, "What the comparison found");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered over each interval");
        AutomationProperties.SetName(rows, "The rows compared, the most changed first. Enter shows the chosen row in the window.");
        AutomationProperties.SetName(reasons, "Why some rows cannot be compared");
        AutomationProperties.SetName(notes, "How the comparison reads");
        AutomationProperties.SetName(compare, "Compare again");
        AutomationProperties.SetName(show, "Show the chosen row in the window");
        AutomationProperties.SetName(share, "Share the comparison as a redacted report");
        AutomationProperties.SetName(close, "Close the comparison");
        AutomationProperties.SetName(saved, "What sharing the comparison did");
        AutomationProperties.SetLiveSetting(saved, AutomationLiveSetting.Polite);
        AccessibleItems.Name(rows);

        // A row reads by its two ends, whose names can be long: it wraps within the list rather than running past its edge,
        // and its value columns are as wide as the heading's, measured from what they hold.
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        rows.ItemTemplate = new FuncDataTemplate<ComparisonRow>((row, _) => row is null ? new TextBlock() : Line(
            [row.Ends, row.Over, row.BaselineValue, row.ComparedValue, row.ChangeCell], FontWeight.Normal, widths));

        // Choosing an interval to type shows its two boxes, and the rows another baseline's comparison held go, the status
        // saying what to write.
        baseline.SelectionChanged += (_, _) =>
        {
            typed.IsVisible = Chosen is { Interval: null };
            _ = CompareAsync();
        };
        metric.SelectionChanged += (_, _) => _ = CompareAsync();
        compare.Click += (_, _) => _ = CompareAsync();
        show.Click += (_, _) => ChooseSelected();
        share.Click += (_, _) => _ = ShareAsync();
        close.Click += (_, _) => Close();
        rows.SelectionChanged += (_, _) => OfferShow();
        rows.DoubleTapped += (_, _) => ChooseSelected();
        rows.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                ChooseSelected();
                key.Handled = true;
            }
        };
        foreach (TextBox time in new[] { from, to })
        {
            time.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Enter)
                {
                    _ = CompareAsync();
                    key.Handled = true;
                }
            };
        }

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
        Place(fields, 1, "Baseline", new StackPanel { Spacing = 6, Children = { baseline, typed } });
        Place(fields, 2, "Compared by", metric);

        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 4 };
        heading = Line(Headings, FontWeight.SemiBold, widths);
        heading.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetRow(rows, 1);
        table.Children.Add(heading);
        table.Children.Add(rows);

        // What sharing did is said beside the buttons, so the comparison's own status keeps saying what it found.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { show, share, compare, close },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(saved);
        footer.Children.Add(buttons);
        var said = new StackPanel { Spacing = 6, Children = { reasons, notes } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(fields, 0);
        Grid.SetRow(status, 1);
        Grid.SetRow(coverage, 2);
        Grid.SetRow(table, 3);
        Grid.SetRow(said, 4);
        Grid.SetRow(footer, 5);
        grid.Children.Add(fields);
        grid.Children.Add(status);
        grid.Children.Add(coverage);
        grid.Children.Add(table);
        grid.Children.Add(said);
        grid.Children.Add(footer);
        Content = grid;

        Choices = new PathChoices(this);
        Offering.Offer(compare, null, CompareDoes);
        OfferShow();
        OfferShare();
        Opened += (_, _) =>
        {
            baseline.Focus();
            _ = CompareAsync();
        };
        Closed += (_, _) => Dispose();
    }

    /// <summary>The comparison shown, the latest one read; null before one is.</summary>
    public IntervalComparison? Shown { get; private set; }

    /// <summary>Where the window asks for the file a redacted report is saved to; a test answers in a person's place.</summary>
    internal PathChoices Choices { get; set; }

    /// <summary>What the window says sharing the comparison did.</summary>
    public string SharingSaid => saved.Text ?? string.Empty;

    /// <summary>Completes when the latest report asked for has been saved, declined or refused.</summary>
    public Task Shared { get; private set; } = Task.CompletedTask;

    /// <summary>The rows shown, as the list shows them.</summary>
    public IReadOnlyList<ComparisonRow> Rows => listed;

    /// <summary>What the status line says.</summary>
    public string Said => status.Text ?? string.Empty;

    /// <summary>Completes when the latest comparison asked for has been shown, refused or superseded.</summary>
    public Task Compared { get; private set; } = Task.CompletedTask;

    /// <summary>Chooses the baseline offered at <paramref name="index"/>, as the list chooses one.</summary>
    public void ChooseBaseline(int index) => baseline.SelectedIndex = index;

    /// <summary>Chooses what the rows are compared by: bytes sent across, or records.</summary>
    public void CompareByBytes(bool bytes) => metric.SelectedIndex = bytes ? 1 : 0;

    /// <summary>Types a baseline's two times, in seconds of session time, as a person types them.</summary>
    public void Enter(string start, string end)
    {
        from.Text = start;
        to.Text = end;
    }

    /// <summary>The baselines offered, in order.</summary>
    public IReadOnlyList<string> Offered => [.. ((IEnumerable<BaselineChoice>)baseline.ItemsSource!).Select(choice => choice.Label)];

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        reading?.Cancel();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private BaselineChoice? Chosen => baseline.SelectedItem as BaselineChoice;

    /// <summary>The table's headings, a column each.</summary>
    private static readonly string[] Headings = ["Relationship", "Over", "Baseline", "Compared", "Change"];

    /// <summary>The space between two columns.</summary>
    private const double Gap = 14;

    /// <summary>The most rows the list shows, the most changed; those past it are counted beneath them.</summary>
    internal const int Listed = 200;

    /// <summary>Why the chosen row cannot be shown with none chosen.</summary>
    private const string NoRowChosen = "Choose a row first.";

    /// <summary>What the status says while the baseline to type is not written, or names no interval.</summary>
    internal const string WriteBaseline = "Write the baseline's start and end in seconds of session time, the end after the start, "
        + "such as 12.5, then press Enter.";

    /// <summary>Why the window's actions wait while a comparison is read.</summary>
    private const string Comparing = "The two intervals are being compared.";

    /// <summary>What Compare does, offered.</summary>
    internal const string CompareDoes = "Read both intervals again, as the session holds them now.";

    /// <summary>Why there is nothing to share until a comparison is shown.</summary>
    internal const string NothingToShare = "Compare the two intervals first.";

    /// <summary>Why sharing waits while a report is saved.</summary>
    private const string Sharing = "A redacted report is being saved.";

    /// <summary>What Share redacted… does, offered.</summary>
    internal const string ShareDoes = "Save this comparison as a metadata-only, pseudonymized report to share, every row "
        + "compared in it: processes, executables and other hosts become random tokens, and no name, PID, address or port "
        + "is kept. What it keeps and leaves out is said before its file is chosen.";

    /// <summary>What Show in the window does, offered.</summary>
    internal const string ShowDoes = "Close the comparison and open what the chosen row compares: a relationship's process "
        + "and channel, or the process whose connections to another host it compares, whose inspector lists the hosts it reached.";

    /// <summary>
    /// The baselines a time scope is offered: the same length just before it and just after it, where that lies within
    /// the session's time; the whole recording, or the span of its records; and an interval typed in seconds.
    /// </summary>
    private static List<BaselineChoice> Baselines(TimeRange compared, TimeRange within, bool recorded, SessionClock clock,
        CultureInfo culture)
    {
        long span = compared.SpanTicks;
        string length = WorkspaceTime.FormatDuration(span, culture);
        var offered = new List<BaselineChoice>(4);
        if ((Int128)compared.StartTicks - span >= within.StartTicks)
        {
            var before = new TimeRange(compared.StartTicks - span, compared.StartTicks);
            offered.Add(new($"The {length} before it, {clock.HalfOpenRange(before, culture)}", before));
        }

        if ((Int128)compared.EndTicks + span <= within.EndTicks)
        {
            var after = new TimeRange(compared.EndTicks, compared.EndTicks + span);
            offered.Add(new($"The {length} after it, {clock.HalfOpenRange(after, culture)}", after));
        }

        offered.Add(new((recorded ? "The whole recording, " : "The whole session, ") + clock.HalfOpenRange(within, culture), within));
        offered.Add(new("Another interval, typed in seconds…", null));
        return offered;
    }

    /// <summary>
    /// Compares the time scope with the baseline chosen, by the metric chosen, reading the session afresh; a newer request
    /// lets an earlier one go, whose answer is dropped (ADR-057).
    /// </summary>
    public Task CompareAsync()
    {
        if (closed) return Task.CompletedTask;
        saved.Text = string.Empty;
        reading?.Cancel();
        var request = new CancellationTokenSource();
        reading = request;
        Compared = ReadAsync(request);
        return Compared;
    }

    private async Task ReadAsync(CancellationTokenSource request)
    {
        TimeRange? against = Chosen?.Interval ?? Typed();
        if (against is not { } chosenBaseline)
        {
            Clear();
            comparing = false;
            Offering.Offer(compare, null, CompareDoes);
            OfferShow();
            OfferShare();
            status.Text = WriteBaseline;
            return;
        }

        ComparisonMetric by = metric.SelectedIndex == 1 ? ComparisonMetric.BytesSent : ComparisonMetric.Records;
        status.Text = "Comparing the two intervals…";
        comparing = true;
        Offering.Offer(compare, Comparing, CompareDoes);
        OfferShow();
        OfferShare();
        try
        {
            CancellationToken token = request.Token;
            IntervalComparison comparison = await Task.Run(() => SessionComparison.Compare(
                SharedSessionStores.Open(path, sessionId), chosenBaseline, compared, by, policy, cancellationToken: token), token);
            if (closed || !ReferenceEquals(reading, request)) return;
            Show(comparison);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A newer request, or closing the window, let this one go.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed && ReferenceEquals(reading, request))
            {
                Clear();
                status.Text = "Could not compare the two intervals: " + exception.Message;
            }
        }
        finally
        {
            if (!closed && ReferenceEquals(reading, request))
            {
                comparing = false;
                Offering.Offer(compare, null, CompareDoes);
                OfferShow();
                OfferShare();
            }
        }
    }

    /// <summary>
    /// Saves the comparison shown as a redacted report to share (`comparison-v1` §8b): what such a report keeps and leaves
    /// out is said first, then its file is chosen - JSON, or CSV by its name - and it is written whole, every row compared
    /// in it, before it replaces anything; beside the buttons, where it went or why it could not be.
    /// </summary>
    public Task ShareAsync()
    {
        if (closed || sharing || comparing || Shown is null) return Task.CompletedTask;
        Shared = SaveReportAsync(Shown);
        return Shared;
    }

    private async Task SaveReportAsync(IntervalComparison comparison)
    {
        sharing = true;
        OfferShare();
        try
        {
            if (!await MainWindow.RedactedSharePrompt().ShowDialog<bool>(this) || closed) return;
            string? file = await Choices.SaveFileAsync(new()
            {
                Title = "Save redacted comparison report",
                SuggestedFileName = RedactedShareExport.SuggestedName("json"),
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new("JSON, self-describing") { Patterns = ["*.json"] },
                    new("CSV, one row per line") { Patterns = ["*.csv"] },
                ],
            });
            if (file is null || closed) return;
            ExportFormat format = file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Csv : ExportFormat.Json;
            string report = RedactedShareExport.Comparison(format, comparison, DateTimeOffset.UtcNow);
            await ExportFileWriter.WriteAsync(file, report, overwrite: true, lifetime.Token);
            if (!closed)
            {
                saved.Text = $"Saved a redacted report of {Spoken.Count(comparison.Relationships.Count, "row")} to {file}. "
                    + "Review it before sharing it.";
            }
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing the window let the report go; the file it would have replaced is as it was.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!closed) saved.Text = "Could not save the redacted report: " + exception.Message;
        }
        finally
        {
            sharing = false;
            if (!closed) OfferShare();
        }
    }

    /// <summary>Says where the window says what sharing did why the system's dialog to choose the file did not open.</summary>
    void IChoosingWindow.PickerFailed(string why) => saved.Text = why;

    /// <summary>Lets go of the comparison shown, its rows, coverage, reasons and notes.</summary>
    private void Clear()
    {
        Shown = null;
        listed = [];
        rows.ItemsSource = listed;
        coverage.Text = reasons.Text = notes.Text = string.Empty;
    }

    /// <summary>The interval typed in seconds, each bound taken outward to whole ticks; null when the two name none.</summary>
    private TimeRange? Typed()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (!decimal.TryParse(from.Text, NumberStyles.Number, culture, out decimal start)
            || !decimal.TryParse(to.Text, NumberStyles.Number, culture, out decimal end)
            || Math.Abs(start) > 900_000_000m || Math.Abs(end) > 900_000_000m)
        {
            return null;
        }

        long first = (long)Math.Floor(start * WorkspaceTime.TicksPerSecond);
        long last = (long)Math.Ceiling(end * WorkspaceTime.TicksPerSecond);
        return TimeRange.TryCreate(first, last, out TimeRange interval) ? interval : null;
    }

    /// <summary>Shows a comparison read: what it compared, each interval's coverage, its rows, their reasons and the notes.</summary>
    private void Show(IntervalComparison comparison)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        Shown = comparison;
        (IReadOnlyList<ComparisonRow> shownRows, IReadOnlyList<string> shownReasons) = ComparisonRows.Of(comparison, culture);
        listed = [.. shownRows.Take(Listed)];
        widths = Widths(listed);
        for (int column = 1; column < widths.Length; column++)
        {
            heading.ColumnDefinitions[column].Width = new GridLength(widths[column]);
        }

        rows.ItemsSource = listed;
        rows.SelectedIndex = listed.Count > 0 ? 0 : -1;
        int hosts = comparison.Relationships.Count(row => row.Host is not null);
        int between = comparison.Relationships.Count - hosts;
        string[] parts =
        [
            .. between > 0 ? [Spoken.Count(between, "relationship") + " between processes"] : Array.Empty<string>(),
            .. hosts > 0 ? [Spoken.Count(hosts, "process's connections to another host", "processes' connections to other hosts")]
                : Array.Empty<string>(),
        ];
        status.Text = comparison.Relationships.Count == 0
            ? "No relationship, and no process's connections to another host, has a record in either interval, so none is "
                + "compared."
            : string.Create(culture, $"Generation {comparison.Generation:N0} · ") + string.Join(" and ", parts)
                + " compared, the most changed first"
                + (comparison.Quiet > 0 ? string.Create(culture, $" · {comparison.Quiet:N0} quiet in both") : string.Empty);
        coverage.Text = string.Join("\n", new[]
        {
            ComparisonText.Coverage("the baseline", comparison.BaselineCoverage),
            ComparisonText.Coverage("the compared interval", comparison.ComparedCoverage),
        }.OfType<string>());
        reasons.Text = string.Join("\n", shownReasons.Select((reason, index) => string.Create(culture, $"[{index + 1}] {reason}")));
        notes.Text = comparison.Relationships.Count == 0
            ? string.Empty
            : string.Join("\n", [
                .. shownRows.Count > listed.Count
                    ? [string.Create(culture, $"{shownRows.Count - listed.Count:N0} more, less changed, are not listed.")]
                    : Array.Empty<string>(),
                .. ComparisonText.Notes([.. listed.Select(row => row.Compared)], comparison.Metric),
            ]);
        KeepKeyboard(firstShown: !shownOnce);
        shownOnce = true;
    }

    /// <summary>
    /// Gives the first row the keyboard once the window's first comparison is shown, the rows being what it was opened to
    /// read - or Close, with none. Later, a control of the window that has the keyboard keeps it, so after choosing a
    /// baseline or a metric the next choice is a key away; nothing of the window having it, the chosen row takes it.
    /// Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard(bool firstShown) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || !firstShown && FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (rows.ContainerFromIndex(rows.SelectedIndex) ?? (Control)close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>Offers sharing the comparison shown, or says why not: while one is read or saved, or with none shown.</summary>
    private void OfferShare() => Offering.Offer(share,
        comparing ? Comparing
        : sharing ? Sharing
        : Shown is null ? NothingToShare
        : null, ShareDoes);

    /// <summary>Offers showing the chosen row, or says why not: while a comparison is read, or with no row chosen.</summary>
    private void OfferShow() => Offering.Offer(show,
        comparing ? Comparing
        : rows.SelectedIndex < 0 || rows.SelectedIndex >= listed.Count ? NoRowChosen
        : null, ShowDoes);

    /// <summary>Closes the window with the chosen row, which the workspace opens.</summary>
    private void ChooseSelected()
    {
        int index = rows.SelectedIndex;
        if (index < 0 || index >= listed.Count) return;
        Close(listed[index].Compared);
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

    /// <summary>
    /// Each value column's width: the widest of its heading and of every cell it holds, with the gap before it, measured in
    /// the window's own font.
    /// </summary>
    private double[] Widths(IReadOnlyList<ComparisonRow> shownRows)
    {
        var measured = new double[Headings.Length];
        for (int column = 1; column < Headings.Length; column++)
        {
            double widest = TextWidth(Headings[column], FontWeight.SemiBold);
            foreach (ComparisonRow row in shownRows)
            {
                string cell = column switch
                {
                    1 => row.Over,
                    2 => row.BaselineValue,
                    3 => row.ComparedValue,
                    _ => row.ChangeCell,
                };
                widest = Math.Max(widest, TextWidth(cell, FontWeight.Normal));
            }

            measured[column] = Math.Ceiling(widest) + Gap;
        }

        return measured;
    }

    /// <summary>How wide <paramref name="text"/> is set in the window's font, at its size and in <paramref name="weight"/>.</summary>
    private double TextWidth(string text, FontWeight weight)
    {
        var probe = new TextBlock { Text = text, FontFamily = FontFamily, FontSize = FontSize, FontWeight = weight };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    /// <summary>
    /// One line of the table: its two ends wrapping in what is left, then its value columns at <paramref name="widths"/>,
    /// or as wide as they need before any is measured.
    /// </summary>
    private static Grid Line(string[] cells, FontWeight weight, double[] widths)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        for (int column = 0; column < cells.Length; column++)
        {
            if (column > 0 && widths[column] > 0) line.ColumnDefinitions[column].Width = new GridLength(widths[column]);
            var cell = new TextBlock
            {
                Text = cells[column],
                FontWeight = weight,
                TextWrapping = column == 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(column == 0 ? 0 : Gap, 0, 0, 0),
            };
            Grid.SetColumn(cell, column);
            line.Children.Add(cell);
        }

        return line;
    }
}
