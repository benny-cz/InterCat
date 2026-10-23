using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// A baseline the comparison offers: an interval it names - of this session, or of another capture, whose folder and
/// session it names too - or, without one, one typed in seconds, or another capture to choose.
/// </summary>
internal sealed record BaselineChoice(string Label, TimeRange? Interval)
{
    /// <summary>The folder of the other capture the interval is of; null for an interval of this session.</summary>
    public string? CapturePath { get; init; }

    /// <summary>The session the other capture's folder held when it was chosen.</summary>
    public Guid? CaptureSession { get; init; }

    /// <summary>Whether choosing it asks for another capture's folder.</summary>
    public bool ChoosesCapture { get; init; }

    public override string ToString() => Label;
}

/// <summary>
/// A saved comparison the Compare window opens on (`contracts/comparison-v1.md` §8c): its baseline chosen first, compared
/// by its metric and its rows of its grouping, with the folder its baseline capture is in where that is another capture,
/// and the investigation it was saved in, which it can be saved in again.
/// </summary>
internal sealed record SavedComparisonOpening(SavedComparison Comparison, string? CapturePath, string Investigation);

/// <summary>
/// The window's comparison of the time scope with another interval (`contracts/comparison-v1.md`, plan M12): each
/// relationship, and each process's connections to another host, that had a record in either, by records or by bytes sent
/// across, as rates over each whole interval, the most changed first, in the words `icat compare` uses (R18). A change is
/// stated only where both intervals measured alike; elsewhere both values stand, with the reason beneath the rows. Enter
/// on a row closes the window with it, and the workspace opens what it compares. Nothing is written but what a person asks
/// for: a redacted report saved where they choose, or the comparison saved in the investigation the session was opened
/// from (`contracts/workspace-v22.md` §7).
/// </summary>
internal sealed class SessionCompareWindow : Window, IDisposable, IChoosingWindow
{
    private readonly string path;
    private readonly Guid sessionId;
    private readonly TimeRange compared;
    private readonly SessionClock clock;
    private readonly EvidencePolicy policy;
    private readonly string? investigation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox baseline = new() { MinWidth = 360 };
    private readonly TextBox from = new() { Watermark = "from, s", Width = 110 };
    private readonly TextBox to = new() { Watermark = "to, s", Width = 110 };
    private readonly StackPanel typed;
    private readonly ComboBox metric = new() { MinWidth = 260 };
    private readonly ComboBox rowsOf = new() { MinWidth = 260 };
    private readonly CheckBox timelineDrawn = new() { Content = "Draw the chosen row column by column" };
    private readonly DifferenceTimelineControl chart = new();
    private readonly TextBlock chartSaid = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly StackPanel charted;
    private readonly ObservableCollection<BaselineChoice> baselines;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox rows = new();
    private readonly TextBlock reasons = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button compare = new() { Content = "Compare" };
    private readonly Button show = new() { Content = "Show in the window", Classes = { "primary" } };
    private readonly Button share = new() { Content = "Share redacted…" };
    private readonly Button save = new() { Content = "Save in the investigation…" };
    private readonly Button close = new() { Content = "Close" };
    private readonly TextBlock done = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private IReadOnlyList<ComparisonRow> listed = [];
    private readonly Grid heading;

    // The difference timeline read with the comparison shown, while the chosen row is drawn column by column; null otherwise.
    private ComparisonTimeline? timeline;

    // Each value column's width, the widest of its heading and its cells: every line of the table, its heading's included,
    // is laid out at these, so the columns line up.
    private double[] widths = [0, 0, 0, 0, 0];
    private CancellationTokenSource? reading;
    private bool comparing;
    private bool sharing;
    private bool saving;
    private int lastBaseline;
    private bool reverting;
    private bool shownOnce;
    private bool closed;
    private bool disposed;

    /// <param name="compared">The time scope, which every baseline is compared with.</param>
    /// <param name="within">The time a baseline is chosen within: the recording, or else the span of the session's records.</param>
    /// <param name="recorded">Whether <paramref name="within"/> is the recording.</param>
    /// <param name="clock">The time base the window states its times in.</param>
    /// <param name="policy">The evidence policy the window counts by, which the comparison counts by too.</param>
    /// <param name="investigation">
    /// The investigation the session shown was opened from, which the comparison can be saved in; null for a session opened
    /// on its own.
    /// </param>
    /// <param name="opening">A saved comparison to open on, its baseline chosen first; null to choose one.</param>
    public SessionCompareWindow(string path, Guid sessionId, TimeRange compared, TimeRange within, bool recorded,
        SessionClock clock, EvidencePolicy policy, string? investigation = null, SavedComparisonOpening? opening = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.path = path;
        this.sessionId = sessionId;
        this.compared = compared;
        this.clock = clock;
        this.policy = policy;
        this.investigation = investigation;
        Title = "InterCat · Compare two intervals";
        Width = 1000;
        Height = 680;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        CultureInfo culture = CultureInfo.CurrentCulture;
        baselines = new(Baselines(compared, within, recorded, clock, culture));

        // A saved comparison opens on its baseline, offered first and chosen, compared by what it was saved with: chosen
        // before any choice is listened to, so the comparison is read once, as it was saved. Another capture's baseline is
        // read from its folder, without which it is not offered.
        ComparisonMetric openingMetric = ComparisonMetric.Records;
        ComparisonGrouping openingRows = ComparisonGrouping.Process;
        string? openedAs = null;
        if (opening?.Comparison is { Baseline: { Interval: { } kept } before, Metric: { } savedMetric, GroupedBy: { } savedRows } saved
            && (!saved.AcrossCaptures || opening.CapturePath is not null))
        {
            baselines.Insert(0, new BaselineChoice(SavedLabel(saved, kept, opening.CapturePath, clock, culture), kept)
            {
                CapturePath = saved.AcrossCaptures ? opening.CapturePath : null,
                CaptureSession = saved.AcrossCaptures ? before.SessionId : null,
            });
            (openingMetric, openingRows, openedAs) = (savedMetric, savedRows, saved.Name);
        }

        baseline.ItemsSource = baselines;
        baseline.SelectedIndex = 0;
        TrimmedChoices.Apply(baseline);
        metric.ItemsSource = new[] { "Records per second", "Bytes sent across per second" };
        metric.SelectedIndex = openingMetric == ComparisonMetric.BytesSent ? 1 : 0;
        rowsOf.ItemsSource = new[] { "Each process", "Each executable, its processes together" };
        rowsOf.SelectedIndex = openingRows == ComparisonGrouping.Executable ? 1 : 0;
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
        AutomationProperties.SetName(rowsOf, "What each row compares: one process, or one executable's processes together");
        AutomationProperties.SetName(timelineDrawn, "Draw the chosen row's difference timeline");
        AutomationProperties.SetName(chartSaid, "The difference timeline's chosen column");
        AutomationProperties.SetLiveSetting(chartSaid, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(status, "What the comparison found");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered over each interval");
        AutomationProperties.SetName(rows, "The rows compared, the most changed first. Enter shows the chosen row in the window.");
        AutomationProperties.SetName(reasons, "Why some rows cannot be compared");
        AutomationProperties.SetName(notes, "How the comparison reads");
        AutomationProperties.SetName(compare, "Compare again");
        AutomationProperties.SetName(show, "Show the chosen row in the window");
        AutomationProperties.SetName(share, "Share the comparison as a redacted report");
        AutomationProperties.SetName(save, "Save the comparison in the investigation");
        AutomationProperties.SetName(close, "Close the comparison");
        AutomationProperties.SetName(done, "What the window's last action did");
        AutomationProperties.SetLiveSetting(done, AutomationLiveSetting.Polite);
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
            if (Chosen is { ChoosesCapture: true })
            {
                typed.IsVisible = false;
                _ = ChooseCaptureAsync();
                return;
            }

            lastBaseline = baseline.SelectedIndex;
            typed.IsVisible = Chosen is { Interval: null };
            OfferRows();

            // Put back after another capture was declined or refused, the comparison shown is still this baseline's.
            if (!reverting) _ = CompareAsync();
        };
        metric.SelectionChanged += (_, _) => _ = CompareAsync();
        rowsOf.SelectionChanged += (_, _) => _ = CompareAsync();

        // Drawn, the comparison is read again with its columns, which the chart draws for the chosen row; no longer drawn,
        // the chart goes and the comparison shown stays.
        charted = new StackPanel { Spacing = 4, IsVisible = false, Children = { chart, chartSaid } };
        timelineDrawn.IsCheckedChanged += (_, _) =>
        {
            charted.IsVisible = timelineDrawn.IsChecked == true;
            if (charted.IsVisible && timeline is null) _ = CompareAsync();
            else ShowChart();
        };
        chart.CursorMoved += (_, _) => chartSaid.Text = ChartSaid();
        compare.Click += (_, _) => _ = CompareAsync();
        show.Click += (_, _) => ChooseSelected();
        share.Click += (_, _) => _ = ShareAsync();
        save.Click += (_, _) => _ = SaveInInvestigationAsync();
        close.Click += (_, _) => Close();
        rows.SelectionChanged += (_, _) =>
        {
            OfferShow();
            ShowChart();
        };
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
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 6,
        };
        Place(fields, 0, "Compared", new TextBlock
        {
            Text = ComparisonText.Interval(compared, clock, culture)
                + (openedAs is null ? ", the window's time scope" : $", as saved in '{openedAs}'"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Place(fields, 1, "Baseline", new StackPanel { Spacing = 6, Children = { baseline, typed } });
        Place(fields, 2, "Compared by", metric);
        Place(fields, 3, "Rows", rowsOf);
        Place(fields, 4, "Timeline", timelineDrawn);

        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 4 };
        heading = Line(Headings, FontWeight.SemiBold, widths);
        heading.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetRow(rows, 1);
        table.Children.Add(heading);
        table.Children.Add(rows);

        // What sharing, or choosing another capture, did is said beside the buttons, so the comparison's own status keeps
        // saying what it found.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { show, save, share, compare, close },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(done);
        footer.Children.Add(buttons);
        var said = new StackPanel { Spacing = 6, Children = { reasons, notes } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(fields, 0);
        Grid.SetRow(status, 1);
        Grid.SetRow(coverage, 2);
        Grid.SetRow(table, 3);
        Grid.SetRow(charted, 4);
        Grid.SetRow(said, 5);
        Grid.SetRow(footer, 6);
        grid.Children.Add(fields);
        grid.Children.Add(status);
        grid.Children.Add(coverage);
        grid.Children.Add(table);
        grid.Children.Add(charted);
        grid.Children.Add(said);
        grid.Children.Add(footer);
        Content = grid;

        Choices = new PathChoices(this);
        OfferRows();
        Offering.Offer(compare, null, CompareDoes);
        OfferShow();
        OfferShare();
        OfferSave();
        Opened += (_, _) =>
        {
            baseline.Focus();
            _ = CompareAsync();
        };
        Closed += (_, _) => Dispose();
    }

    /// <summary>The comparison shown, the latest one read; null before one is.</summary>
    public IntervalComparison? Shown { get; private set; }

    /// <summary>Where the window asks for a redacted report's file or another capture's folder; a test answers in a person's place.</summary>
    internal PathChoices Choices { get; set; }

    /// <summary>What the window says its last action did, beside its buttons: sharing the comparison, or choosing another capture.</summary>
    public string Done => done.Text ?? string.Empty;

    /// <summary>Completes when the latest report asked for has been saved, declined or refused.</summary>
    public Task Shared { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the latest save in the investigation asked for has been made, declined or refused.</summary>
    public Task SavedIn { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Why the window that opened the comparison cannot open what a row compares, which Show in the window then says; null
    /// where every row can be opened.
    /// </summary>
    public Func<ComparedRelationship, string?>? WhyNotShown { get; init; }

    /// <summary>Completes when the latest other capture asked for has been chosen, declined or refused.</summary>
    public Task CaptureChosen { get; private set; } = Task.CompletedTask;

    /// <summary>Chooses what each row compares: each executable's processes together, or each process.</summary>
    public void CompareByExecutable(bool executables) => rowsOf.SelectedIndex = executables ? 1 : 0;

    /// <summary>Whether the rows compare executables, rather than processes.</summary>
    public bool ComparesExecutables => rowsOf.SelectedIndex == 1;

    /// <summary>The rows shown, as the list shows them.</summary>
    public IReadOnlyList<ComparisonRow> Rows => listed;

    /// <summary>What the status line says.</summary>
    public string Said => status.Text ?? string.Empty;

    /// <summary>Completes when the latest comparison asked for has been shown, refused or superseded.</summary>
    public Task Compared { get; private set; } = Task.CompletedTask;

    /// <summary>Draws the chosen row's difference timeline, or stops drawing it, as its check box does.</summary>
    public void DrawTimeline(bool drawn) => timelineDrawn.IsChecked = drawn;

    /// <summary>The difference timeline read with the comparison shown; null while the chosen row is not drawn.</summary>
    public ComparisonTimeline? Timeline => timeline;

    /// <summary>What the line beneath the chart says of its chosen column, or why it draws none.</summary>
    public string TimelineSaid => chartSaid.Text ?? string.Empty;

    /// <summary>Chooses a column of the chart, as the arrow keys or a click do.</summary>
    public void ChooseColumn(int column) => chart.MoveTo(column);

    /// <summary>Chooses a listed row, as a click does.</summary>
    public void ChooseRow(int index) => rows.SelectedIndex = index;

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

    /// <summary>How many columns the chosen row's difference timeline cuts each interval into, where each is a tick long.</summary>
    internal const int TimelineColumns = 20;

    /// <summary>What the line beneath the chart says while the comparison and its columns are read.</summary>
    internal const string ReadingColumns = "The two intervals are being compared column by column.";

    /// <summary>What the line beneath the chart says with no row chosen.</summary>
    internal const string ChooseRowToDraw = "Choose a row to draw its columns.";

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

    /// <summary>Why the comparison cannot be saved where the session shown was not opened from an investigation.</summary>
    internal const string SaveNeedsInvestigation = "A comparison is kept in an investigation: open this session from one to "
        + "save it there.";

    /// <summary>Why saving waits while the comparison is saved.</summary>
    private const string Saving = "The comparison is being saved in the investigation.";

    /// <summary>What Save in the investigation… does, offered.</summary>
    internal const string SaveDoes = "Keep this comparison in the investigation under a name, to open again from its "
        + "Comparisons… or run with icat compare --saved: its two intervals, what it compares by and what its rows are of, "
        + "never its answer, which is read afresh each time.";

    /// <summary>What Share redacted… does, offered.</summary>
    internal const string ShareDoes = "Save this comparison as a metadata-only, pseudonymized report to share, every row "
        + "compared in it: processes, executables and other hosts become random tokens, and no name, PID, address or port "
        + "is kept. What it keeps and leaves out is said before its file is chosen.";

    /// <summary>The baseline that asks for another capture's folder.</summary>
    internal const string ChooseCapture = "Another saved capture…";

    /// <summary>Why the rows compare executables while the baseline is another capture's.</summary>
    internal const string AcrossCaptures = "Two captures compare by executable: a process of one capture is never another's.";

    /// <summary>What the Rows choice does, offered.</summary>
    internal const string RowsDoes = "Compare each process, or each executable's processes together - grouped by the path their "
        + "image was witnessed at, so a process that restarted stays one row.";

    /// <summary>What Show in the window does, offered.</summary>
    internal const string ShowDoes = "Close the comparison and open what the chosen row compares: a relationship's process "
        + "and channel, or the process whose connections to another host it compares, whose inspector lists the hosts it reached.";

    /// <summary>
    /// What a saved comparison's baseline is offered as: its name and interval, of another capture's folder where it is one,
    /// such as "As saved in 'before the update', [0.0, 10.0) s".
    /// </summary>
    private static string SavedLabel(SavedComparison saved, TimeRange interval, string? capturePath, SessionClock clock,
        CultureInfo culture) =>
        $"As saved in '{saved.Name}', " + (saved.AcrossCaptures && capturePath is not null
            ? WorkspaceTime.FormatHalfOpenRange(interval, culture) + " of " + Path.GetFileName(Path.TrimEndingDirectorySeparator(capturePath))
            : clock.HalfOpenRange(interval, culture));

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
        offered.Add(new(ChooseCapture, null) { ChoosesCapture = true });
        return offered;
    }

    /// <summary>
    /// Compares the time scope with the baseline chosen, by the metric chosen, reading the session afresh; a newer request
    /// lets an earlier one go, whose answer is dropped (ADR-057).
    /// </summary>
    public Task CompareAsync()
    {
        if (closed) return Task.CompletedTask;
        done.Text = string.Empty;
        reading?.Cancel();
        var request = new CancellationTokenSource();
        reading = request;
        Compared = ReadAsync(request);
        return Compared;
    }

    /// <summary>
    /// Asks for another saved capture's folder and offers its whole recording, or the span of its records, as the baseline,
    /// rows by executable; a folder that holds no session, or this one, is said, and the baseline chosen before stays.
    /// </summary>
    private Task ChooseCaptureAsync()
    {
        CaptureChosen = ChooseAsync();
        return CaptureChosen;

        async Task ChooseAsync()
        {
            string? folder = await Choices.OpenFolderAsync(new FolderPickerOpenOptions { Title = "Choose the saved capture to compare with" });
            if (closed) return;
            if (folder is null)
            {
                Revert();
                return;
            }

            try
            {
                (Guid session, TimeRange? whole, bool recordedThere) = await Task.Run(() =>
                {
                    SessionOverviewBundle overview = SessionOverviewProjector.Project(SharedSessionStores.Open(folder), policy,
                        cancellationToken: lifetime.Token);
                    return (overview.SessionId, overview.Recording ?? overview.Extent, overview.Recording is not null);
                }, lifetime.Token);
                if (closed) return;
                string? refused = session == sessionId
                    ? "That folder holds the session shown: choose another capture, or an interval of this one."
                    : whole is not { SpanTicks: > 0 } ? "That capture holds no records to compare with." : null;
                if (refused is not null)
                {
                    Revert();
                    done.Text = refused;
                    return;
                }

                CultureInfo culture = CultureInfo.CurrentCulture;
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                var chosen = new BaselineChoice((recordedThere ? "The whole recording of " : "The whole session of ") + name + ", "
                    + WorkspaceTime.FormatHalfOpenRange(whole!.Value, culture), whole)
                {
                    CapturePath = folder,
                    CaptureSession = session,
                };

                // One other capture is offered at a time, the latest chosen, just before the choice that asks for another.
                if (baselines.FirstOrDefault(choice => choice.CapturePath is not null) is { } earlier) baselines.Remove(earlier);
                baselines.Insert(baselines.Count - 1, chosen);
                baseline.SelectedItem = chosen;
            }
            catch (OperationCanceledException) when (closed)
            {
                // Closing the window let the capture go.
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException)
            {
                if (closed) return;
                Revert();
                done.Text = "That folder holds no session to compare with: " + exception.Message;
            }
        }

        // The baseline chosen before is chosen again, its comparison still the one shown.
        void Revert()
        {
            reverting = true;
            baseline.SelectedIndex = lastBaseline;
            reverting = false;
        }
    }

    /// <summary>Offers choosing what rows compare, or says why not: two captures compare by executable alone.</summary>
    private void OfferRows()
    {
        bool across = Chosen is { CapturePath: not null };
        if (across) rowsOf.SelectedIndex = 1;
        Offering.Offer(rowsOf, across ? AcrossCaptures : null, RowsDoes);
    }

    private async Task ReadAsync(CancellationTokenSource request)
    {
        BaselineChoice? choice = Chosen;
        TimeRange? against = choice?.Interval ?? Typed();
        if (against is not { } chosenBaseline)
        {
            Clear();
            comparing = false;
            Offering.Offer(compare, null, CompareDoes);
            OfferShow();
            OfferShare();
            OfferSave();
            status.Text = WriteBaseline;
            return;
        }

        ComparisonMetric by = metric.SelectedIndex == 1 ? ComparisonMetric.BytesSent : ComparisonMetric.Records;
        ComparisonGrouping grouping = rowsOf.SelectedIndex == 1 ? ComparisonGrouping.Executable : ComparisonGrouping.Process;
        bool drawn = timelineDrawn.IsChecked == true;
        status.Text = "Comparing the two intervals…";
        comparing = true;
        Offering.Offer(compare, Comparing, CompareDoes);
        OfferShow();
        OfferShare();
        OfferSave();
        try
        {
            CancellationToken token = request.Token;
            (IntervalComparison comparison, ComparisonTimeline? columns) =
                await Task.Run(() => Read(choice, chosenBaseline, by, grouping, drawn, token), token);
            if (closed || !ReferenceEquals(reading, request)) return;
            timeline = columns;
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
                OfferSave();
            }
        }
    }

    /// <summary>
    /// The comparison of the time scope with <paramref name="against"/>, of this session or of another capture's chosen,
    /// and while the chosen row is drawn its difference timeline too, read with it at one generation (`comparison-v1`
    /// §8d): each interval cut into as many columns as a tick allows, up to <see cref="TimelineColumns"/>, the listed rows'
    /// columns kept.
    /// </summary>
    private (IntervalComparison Comparison, ComparisonTimeline? Timeline) Read(BaselineChoice? choice, TimeRange against,
        ComparisonMetric by, ComparisonGrouping grouping, bool drawn, CancellationToken token)
    {
        SessionStore store = SharedSessionStores.Open(path, sessionId);
        int columns = (int)Math.Min(TimelineColumns, Math.Min(against.SpanTicks, compared.SpanTicks));
        if (choice is { CapturePath: { } other, CaptureSession: { } otherSession })
        {
            SessionStore baselineStore = SharedSessionStores.Open(other, otherSession);
            if (!drawn) return (SessionComparison.CompareCaptures(baselineStore, against, store, compared, by, policy, token), null);
            ComparisonTimeline across = SessionComparison.CaptureTimeline(baselineStore, against, store, compared, columns, by, policy,
                Listed, token);
            return (across.Whole, across);
        }

        if (!drawn) return (SessionComparison.Compare(store, against, compared, by, policy, grouping, token), null);
        ComparisonTimeline within = SessionComparison.Timeline(store, against, compared, columns, by, policy, grouping, Listed, token);
        return (within.Whole, within);
    }

    /// <summary>
    /// Draws the chosen row's columns, while it is drawn: each baseline column stated in this session's time base or, of
    /// another capture, in that capture's own session time, as the baselines offered state them.
    /// </summary>
    private void ShowChart()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        bool across = Shown?.BaselineSessionId is not null;
        ComparedRelationship? chosen = rows.SelectedIndex >= 0 && rows.SelectedIndex < listed.Count ? listed[rows.SelectedIndex].Compared : null;
        chart.Show(timelineDrawn.IsChecked == true ? timeline : null, chosen,
            range => across ? WorkspaceTime.FormatHalfOpenRange(range, culture) : clock.HalfOpenRange(range, culture),
            range => clock.HalfOpenRange(range, culture));
        chartSaid.Text = ChartSaid();
    }

    /// <summary>What the line beneath the chart says: its chosen column in words, or why it draws none.</summary>
    private string ChartSaid() => chart.Describe()
        ?? (timeline is null ? ReadingColumns : ChooseRowToDraw);

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
                done.Text = $"Saved a redacted report of {Spoken.Count(comparison.Relationships.Count, "row")} to {file}. "
                    + "Review it before sharing it.";
            }
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing the window let the report go; the file it would have replaced is as it was.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!closed) done.Text = "Could not save the redacted report: " + exception.Message;
        }
        finally
        {
            sharing = false;
            if (!closed) OfferShare();
        }
    }

    /// <summary>
    /// Saves the comparison shown in the investigation the session was opened from, under a name asked for
    /// (`contracts/workspace-v22.md` §7): its two intervals, what it compares by and what its rows are of, never its answer,
    /// replacing a comparison of that name; beside the buttons, the name it was saved under, or why it was not.
    /// </summary>
    public Task SaveInInvestigationAsync()
    {
        if (closed || saving || comparing || Shown is null || investigation is null) return Task.CompletedTask;
        SavedIn = SaveComparisonAsync(Shown, investigation);
        return SavedIn;
    }

    private async Task SaveComparisonAsync(IntervalComparison comparison, string file)
    {
        saving = true;
        OfferSave();
        string named = Path.GetFileName(file);
        var before = new SavedComparisonInterval
        {
            SessionId = comparison.BaselineSessionId ?? comparison.SessionId,
            StartTicks = comparison.Baseline.StartTicks,
            EndTicks = comparison.Baseline.EndTicks,
        };
        var after = new SavedComparisonInterval
        {
            SessionId = comparison.SessionId,
            StartTicks = comparison.Compared.StartTicks,
            EndTicks = comparison.Compared.EndTicks,
        };
        try
        {
            // The investigation is read first: another capture it does not hold cannot be saved there, and the names it keeps
            // comparisons under are the ones saving under replaces, which the prompt says as a name is typed.
            (bool holds, IReadOnlyList<string> names) = await Task.Run(() =>
            {
                InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(file);
                return (workspace.Members.Any(member => member.SessionId == before.SessionId),
                    (IReadOnlyList<string>)[.. InvestigationWorkspace.ComparisonsInForce(workspace).Select(kept => kept.Name)]);
            }, lifetime.Token);
            if (closed) return;
            if (!holds)
            {
                done.Text = $"The comparison was not saved: the baseline capture, session {before.SessionId.ToString("N")[..8]}, is not "
                    + $"a session of {named}. Add it to the investigation, then save.";
                return;
            }

            var unsaved = new SavedComparison
            {
                Revision = 0,
                Name = string.Empty,
                Baseline = before,
                Compared = after,
                Metric = comparison.Metric,
                GroupedBy = comparison.Grouping,
                RecordedUtc = DateTimeOffset.UtcNow,
            };
            string? name = await SavePrompt(named, ComparisonText.Saved(unsaved, CultureInfo.CurrentCulture), names)
                .ShowDialog<string?>(this);
            if (name is null || closed) return;
            SavedComparison saved = await Task.Run(() => InvestigationWorkspace.SaveComparison(file, name, before, after,
                comparison.Metric, comparison.Grouping, DateTimeOffset.UtcNow), lifetime.Token);
            if (!closed) done.Text = $"Saved in {named} as '{saved.Name}'; the investigation's Comparisons… opens it again.";
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing the window let the save go; the investigation is as it was.
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            if (!closed) done.Text = "The comparison was not saved: " + InvestigationTrouble.Why(file, exception);
        }
        finally
        {
            saving = false;
            if (!closed) OfferSave();
        }
    }

    /// <summary>
    /// Asks the name to save the comparison under in <paramref name="investigation"/>, saying what is saved and, as a name is
    /// typed, whether saving under it replaces a comparison the investigation keeps; closes with the name, or with null
    /// when declined.
    /// </summary>
    internal static Window SavePrompt(string investigation, string described, IReadOnlyList<string> names)
    {
        var prompt = new Window
        {
            Title = "Save the comparison in the investigation",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var name = new TextBox { Watermark = "such as: before the update", MaxLength = InvestigationWorkspace.MostComparisonNameCharacters };
        var said = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var cancel = new Button { Content = "Cancel" };
        var keep = new Button { Content = "Save", Classes = { "primary" } };
        AutomationProperties.SetName(name, "The name to save the comparison under");
        AutomationProperties.SetName(said, "What saving under this name does");
        AutomationProperties.SetLiveSetting(said, AutomationLiveSetting.Polite);

        // Saving under a name the investigation keeps replaces that comparison, which is said before it is done.
        void Say()
        {
            string typed = name.Text?.Trim() ?? string.Empty;
            string? kept = names.FirstOrDefault(existing => string.Equals(existing, typed, StringComparison.OrdinalIgnoreCase));
            said.Text = typed.Length == 0 ? "Name it to save it."
                : kept is not null ? $"Replaces the comparison saved as '{kept}'; its earlier revisions stay in the file."
                : $"Saves it as '{typed}'.";
            Offering.Offer(keep, typed.Length == 0 ? "Name the comparison first." : null, "Save the comparison under this name");
        }

        void Keep()
        {
            if (keep.IsEnabled) prompt.Close(name.Text!.Trim());
        }

        name.TextChanged += (_, _) => Say();
        cancel.Click += (_, _) => prompt.Close(null);
        keep.Click += (_, _) => Keep();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(null);
                key.Handled = true;
            }
            else if (key.Key == Key.Enter && name.IsKeyboardFocusWithin)
            {
                Keep();
                key.Handled = true;
            }
        };
        prompt.Opened += (_, _) => name.Focus();
        Say();
        prompt.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"{investigation} keeps this comparison under a name: {described}. It keeps what to compare and "
                        + "never the answer, which is read afresh each time it is opened.",
                    TextWrapping = TextWrapping.Wrap,
                },
                name,
                said,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, keep },
                },
            },
        };
        return prompt;
    }

    /// <summary>Says where the window says what sharing did why the system's dialog to choose the file did not open.</summary>
    void IChoosingWindow.PickerFailed(string why) => done.Text = why;

    /// <summary>Lets go of the comparison shown, its rows, coverage, reasons and notes.</summary>
    private void Clear()
    {
        Shown = null;
        timeline = null;
        listed = [];
        rows.ItemsSource = listed;
        coverage.Text = reasons.Text = notes.Text = string.Empty;
        ShowChart();
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
        int hosts = comparison.Relationships.Count(row => row.Host is not null || row.ExecutableHost is not null);
        int between = comparison.Relationships.Count - hosts;
        bool executables = comparison.Grouping == ComparisonGrouping.Executable;
        string[] parts =
        [
            .. between > 0 ? [Spoken.Count(between, "relationship") + (executables ? " between executables" : " between processes")]
                : Array.Empty<string>(),
            .. hosts > 0
                ? [executables
                    ? Spoken.Count(hosts, "executable's connections to another host", "executables' connections to other hosts")
                    : Spoken.Count(hosts, "process's connections to another host", "processes' connections to other hosts")]
                : Array.Empty<string>(),
        ];
        string generation = comparison.BaselineGeneration is { } baselineGeneration
            ? string.Create(culture, $"Generation {comparison.Generation:N0}, the baseline capture's {baselineGeneration:N0} · ")
            : string.Create(culture, $"Generation {comparison.Generation:N0} · ");
        status.Text = comparison.Relationships.Count == 0
            ? "No relationship, and no process's connections to another host, has a record in either interval, so none is "
                + "compared."
            : generation + string.Join(" and ", parts)
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
                .. ComparisonText.Grouping(comparison, culture),
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

    /// <summary>
    /// Offers saving the comparison shown in the investigation, or says why not: the session shown was not opened from one,
    /// a comparison is read or saved, or none is shown.
    /// </summary>
    private void OfferSave() => Offering.Offer(save,
        investigation is null ? SaveNeedsInvestigation
        : comparing ? Comparing
        : saving ? Saving
        : Shown is null ? NothingToShare
        : null, SaveDoes);

    /// <summary>Offers sharing the comparison shown, or says why not: while one is read or saved, or with none shown.</summary>
    private void OfferShare() => Offering.Offer(share,
        comparing ? Comparing
        : sharing ? Sharing
        : Shown is null ? NothingToShare
        : null, ShareDoes);

    /// <summary>
    /// Offers showing the chosen row, or says why not: while a comparison is read, with no row chosen, or where the window
    /// that opened the comparison cannot open what the row compares.
    /// </summary>
    private void OfferShow() => Offering.Offer(show, WhyNotShow(), ShowDoes);

    private string? WhyNotShow() =>
        comparing ? Comparing
        : rows.SelectedIndex < 0 || rows.SelectedIndex >= listed.Count ? NoRowChosen
        : WhyNotShown?.Invoke(listed[rows.SelectedIndex].Compared);

    /// <summary>Closes the window with the chosen row, which the workspace opens, where it can.</summary>
    private void ChooseSelected()
    {
        if (WhyNotShow() is not null) return;
        Close(listed[rows.SelectedIndex].Compared);
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
