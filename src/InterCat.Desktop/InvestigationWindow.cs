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
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// An investigation over separately captured sessions (ADR-038, M4): its members, where each stands, its host and its
/// time, with the gestures that keep it whole - open a member, relink one that moved, add sessions, align one to the
/// investigation's time - the candidate joins between its captures (ADR-041), and a package of it with its sessions to share
/// (§8.4). Showing it opens each session as a viewer does and writes to none; only the investigation's own file is written.
/// </summary>
internal sealed class InvestigationWindow : Window, IDisposable
{
    private readonly string path;
    private readonly Func<string, Task<bool>>? openSession;
    private readonly Func<string, TimeRange, Task<bool>>? openSessionAt;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock heading = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, TextTrimming = TextTrimming.PathSegmentEllipsis };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock time = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock overlaps = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "caution" }, IsVisible = false };

    /// <summary>The overlaps, scrolled within about three lines, so an investigation of many runs keeps room for its pages.</summary>
    private readonly ScrollViewer overlapsView = new()
    {
        MaxHeight = 50,
        IsVisible = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock caveats = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } };

    /// <summary>What opening any of its sessions puts back of the window's panes, shown only when it keeps them (§26.3).</summary>
    private readonly TextBlock panes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly ListBox members = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock detail = new() { FontSize = 12 };
    private readonly TextBlock detailPath = new() { FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis };
    private readonly Button open = new() { Content = "Open in InterCat", IsEnabled = false };
    private readonly Button relink = new() { Content = "Relink…", IsEnabled = false };
    private readonly Button alignButton = new() { Content = "Align…", IsEnabled = false };
    private readonly Button withdraw = new() { Content = "Withdraw alignment", IsEnabled = false };
    private readonly Button oneHost = new() { Content = "One host…", IsEnabled = false };
    private readonly Button add = new() { Content = "Add sessions…" };
    private readonly Button refresh = new() { Content = "Refresh" };
    private readonly ListBox candidates = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock candidateSummary = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Text = "Candidate joins pair a connection one session holds one end of with its mirrored end in another, where "
            + "their lifetimes can overlap in the investigation's time. Finding them reads every session's connections.",
    };
    private readonly TextBlock candidateNotes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } };
    private readonly TextBlock candidateCoverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "caution" }, IsVisible = false };
    private readonly Button find = new() { Content = "Find candidate joins" };
    private readonly Button translations = new() { Content = "Known translations…" };
    private readonly Button acceptJoin = new() { Content = "Accept as one connection", IsEnabled = false };
    private readonly Button rejectJoin = new() { Content = "Reject", IsEnabled = false };
    private readonly Button withdrawJoin = new() { Content = "Withdraw decision", IsEnabled = false };
    private readonly TabControl tabs = new();
    private readonly InvestigationTimelineControl timelineChart = new();
    private readonly TextBlock timelineWords = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } };
    private readonly TextBlock timelineIntro = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Text = "Each session is a lane on the investigation's own time: its records where its alignment places them, each lane "
            + "scaled to its own busiest column. Hatching marks where its capture lost records or collected none, and a thin hatched "
            + "strip where what it covered is unknown, so an empty column there is no proof of inactivity. A session with no "
            + "alignment has no place.",
    };
    private readonly Button refreshTimeline = new() { Content = "Refresh the timeline" };
    private readonly ListBox notesList = new() { SelectionMode = SelectionMode.Single };
    private readonly Button addNote = new() { Content = "Add a note…" };
    private readonly Button rewordNote = new() { Content = "Reword…", IsEnabled = false };
    private readonly Button removeNote = new() { Content = "Remove", IsEnabled = false };
    private readonly Button showNote = new() { Content = "Show on the timeline", IsEnabled = false };
    private readonly TextBlock notesIntro = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Text = "Notes are your words on the investigation: about all of it, or pinned at an instant of a session, which its time "
            + "places and the timeline marks. Each is kept as a revision of the investigation's file, and none changes a session.",
    };
    private readonly Button compareInstants = new() { Content = "Compare instants…", IsEnabled = false };
    private readonly Button zoomIn = new() { Content = "Zoom in", IsEnabled = false };
    private readonly Button zoomOut = new() { Content = "Zoom out", IsEnabled = false };
    private readonly Button zoomWhole = new() { Content = "Whole investigation", IsEnabled = false };
    private readonly Button openColumn = new() { Content = "Open this column", IsEnabled = false };
    private readonly Button savedViews = new() { Content = "Views…" };
    private readonly TextBlock columnReadout = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private TimeRange? zoom;
    private TimeRange? whole;
    private readonly Button package = new() { Content = "Package…", IsEnabled = false };
    private CancellationTokenSource? packaging;
    private bool timelineLoaded;
    private Task? timelineLoad;
    private bool loading;
    private bool finding;
    private bool closed;
    private bool disposed;

    // What the window showing a member wrote of its layout, and of its panes, while the investigation was being read, which
    // that reading may have missed.
    private readonly Dictionary<Guid, WorkspaceLayout?> keptWhileReading = [];
    private (bool Written, WorkspacePanes? Panes) panesWhileReading;

    public InvestigationWindow(
        string path,
        Func<string, Task<bool>>? openSession,
        string? opening = null,
        Func<string, TimeRange, Task<bool>>? openSessionAt = null)
    {
        this.path = Path.GetFullPath(path);
        this.openSession = openSession;
        this.openSessionAt = openSessionAt;
        Title = $"InterCat · Investigation · {Path.GetFileName(this.path)}";
        Width = 940;
        Height = 660;
        MinWidth = 680;
        // The smallest size at which each page keeps two of its sessions, lanes or notes in view, with overlaps stated and a
        // session missing above them, and beneath its sessions the two lines that say what opening them puts back of the
        // window's panes.
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // The investigation's file in one line: a path too long for it gives up its middle folders, and its tooltip has it whole.
        heading.Text = this.path;
        ToolTip.SetTip(heading, this.path);

        AutomationProperties.SetName(members, "Sessions of this investigation; press Enter to open the selected one");
        AutomationProperties.SetHelpText(open, "Open the selected session in InterCat");
        AutomationProperties.SetName(relink, "Relink the selected session to where it is now");
        AutomationProperties.SetName(alignButton, "Align the selected session to the investigation's time");
        AutomationProperties.SetHelpText(withdraw, "Withdraw the selected session's alignment");
        AutomationProperties.SetHelpText(oneHost, "Say whether the selected session's host is one host with another");
        AutomationProperties.SetName(notesList, "Notes on this investigation");
        AutomationProperties.SetName(addNote, "Add a note, pinned at the timeline's chosen column when there is one");
        AutomationProperties.SetName(rewordNote, "Reword the selected note");
        AutomationProperties.SetName(removeNote, "Remove the selected note");
        AutomationProperties.SetHelpText(showNote, "Show the selected note on the timeline");
        AccessibleItems.Name(notesList);
        notesList.ItemTemplate = new FuncDataTemplate<InvestigationNoteRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Text, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Where, FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
            },
        });
        AutomationProperties.SetName(add, "Add sessions to this investigation");
        AutomationProperties.SetHelpText(refresh, "Look again where each session was last found");
        AutomationProperties.SetName(status, "Investigation status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(overlaps, "Sessions of one host that ran at once, or may have");
        AutomationProperties.SetName(candidates, "Candidate joins between the sessions; none is established");
        AutomationProperties.SetName(find, "Find candidate joins between the sessions");
        AutomationProperties.SetHelpText(translations, "State or withdraw a known address translation between the sessions");
        AutomationProperties.SetName(candidateSummary, "Candidate joins status");
        AutomationProperties.SetName(candidateCoverage, "What the compared captures did not cover");
        AutomationProperties.SetLiveSetting(candidateSummary, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(timelineWords, "The investigation's timeline, each session in words");
        AutomationProperties.SetHelpText(refreshTimeline, "Draw the investigation's timeline again");
        AutomationProperties.SetHelpText(compareInstants, "Compare an instant of one session with an instant of another");
        AutomationProperties.SetHelpText(zoomIn, "Zoom the timeline in around the chosen column");
        AutomationProperties.SetHelpText(zoomOut, "Zoom the timeline out");
        AutomationProperties.SetHelpText(zoomWhole, "Show the whole investigation on the timeline");
        AutomationProperties.SetHelpText(openColumn, "Open the chosen column's records in InterCat");
        AutomationProperties.SetHelpText(savedViews, "Save the view shown, or show a saved one");
        AutomationProperties.SetName(columnReadout, "The chosen column of the timeline");
        AutomationProperties.SetHelpText(acceptJoin, "Accept the selected candidate as one connection, as your decision");
        AutomationProperties.SetName(rejectJoin, "Reject the selected candidate, as your decision");
        AutomationProperties.SetHelpText(withdrawJoin, "Withdraw your decision about the selected candidate");
        AutomationProperties.SetName(package, PackageName);
        AccessibleItems.Name(members);
        AccessibleItems.Name(candidates);
        members.ItemTemplate = new FuncDataTemplate<InvestigationMemberRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Detail, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Time, FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                new TextBlock
                {
                    Text = row?.Kept,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = row?.Kept is not null,
                    Classes = { "muted" },
                },
                new TextBlock
                {
                    Text = row?.Reason,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = row?.Reason is not null,
                    Classes = { "caution" },
                },
            },
        });
        candidates.ItemTemplate = new FuncDataTemplate<InvestigationCandidateRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.First, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Second, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Evidence, FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
            },
        });

        members.SelectionChanged += (_, _) => ShowSelected();
        members.DoubleTapped += (_, _) => _ = OpenSelectedAsync();
        members.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            _ = OpenSelectedAsync();
            key.Handled = true;
        };
        open.Click += (_, _) => _ = OpenSelectedAsync();
        relink.Click += (_, _) => _ = PickRelinkAsync();
        alignButton.Click += (_, _) => _ = AlignSelectedAsync();
        withdraw.Click += (_, _) => _ = WithdrawSelectedAsync();
        oneHost.Click += (_, _) => _ = OneHostSelectedAsync();
        add.Click += (_, _) => _ = PickAddAsync();
        refresh.Click += (_, _) => _ = RefreshAsync();
        find.Click += (_, _) => _ = FindCandidatesAsync();
        translations.Click += (_, _) => _ = TranslationsAsync();
        refreshTimeline.Click += (_, _) => _ = ShowTimelineAsync();
        compareInstants.Click += (_, _) => _ = CompareInstantsAsync();
        zoomIn.Click += (_, _) => ZoomBy(0.5m);
        zoomOut.Click += (_, _) => ZoomBy(2m);
        zoomWhole.Click += (_, _) => _ = ZoomToAsync(null);
        savedViews.Click += (_, _) => _ = SavedViewsAsync();
        openColumn.Click += (_, _) =>
        {
            if (timelineChart.ChosenColumn is { } chosen) _ = OpenColumnAsync(chosen);
        };
        timelineChart.ZoomRequested += (_, range) => _ = ZoomToAsync(range);
        timelineChart.ColumnChosen += (_, chosen) => _ = OpenColumnAsync(chosen);
        timelineChart.CursorMoved += (_, _) =>
        {
            columnReadout.Text = timelineChart.Describe() ?? string.Empty;
            openColumn.IsEnabled = timelineChart.ChosenColumn is not null && openSessionAt is not null;
        };
        acceptJoin.Click += (_, _) => _ = DecideSelectedAsync(WorkspaceJoinDecision.Accepted);
        rejectJoin.Click += (_, _) => _ = DecideSelectedAsync(WorkspaceJoinDecision.Rejected);
        withdrawJoin.Click += (_, _) => _ = DecideSelectedAsync(WorkspaceJoinDecision.Withdrawn);
        package.Click += (_, _) => _ = PackageAsync();
        notesList.SelectionChanged += (_, _) => ShowSelectedNote();
        addNote.Click += (_, _) => _ = WriteNoteAsync(reword: false);
        rewordNote.Click += (_, _) => _ = WriteNoteAsync(reword: true);
        removeNote.Click += (_, _) => _ = RemoveSelectedNoteAsync();
        showNote.Click += (_, _) => _ = ShowSelectedNoteAsync();
        candidates.SelectionChanged += (_, _) => ShowSelectedCandidate();
        var close = new Button { Content = "Close" };
        AutomationProperties.SetName(close, "Close the investigation window");
        close.Click += (_, _) => Close();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var sessionActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { open, relink, alignButton, withdraw, oneHost, add, refresh })
        {
            button.Margin = new Thickness(8, 4, 0, 0);
            sessionActions.Children.Add(button);
        }

        var sessionsList = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = members, Padding = new Thickness(2) };

        // Where the selected session is, or was last found, in one line: the session whole, and its path giving up its middle
        // folders where it is too long, with its tooltip whole; its row says the rest.
        var where = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4 };
        Grid.SetColumn(detailPath, 1);
        where.Children.Add(detail);
        where.Children.Add(detailPath);
        // What opening any of them puts back of the window's panes, and what naming a session and grouping hosts can and
        // cannot say, stand with the sessions they are about.
        var sessionsPage = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(sessionsList, 0);
        Grid.SetRow(where, 1);
        Grid.SetRow(sessionActions, 2);
        Grid.SetRow(panes, 3);
        Grid.SetRow(caveats, 4);
        sessionsPage.Children.Add(sessionsList);
        sessionsPage.Children.Add(where);
        sessionsPage.Children.Add(sessionActions);
        sessionsPage.Children.Add(panes);
        sessionsPage.Children.Add(caveats);

        var candidatesHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(translations, 1);
        Grid.SetColumn(find, 2);
        translations.VerticalAlignment = VerticalAlignment.Top;
        translations.Margin = new Thickness(0, 0, 8, 0);
        find.VerticalAlignment = VerticalAlignment.Top;
        candidateSummary.Margin = new Thickness(0, 0, 12, 0);
        candidatesHeader.Children.Add(candidateSummary);
        candidatesHeader.Children.Add(translations);
        candidatesHeader.Children.Add(find);
        var candidatesList = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = candidates, Padding = new Thickness(2) };
        var decisions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { acceptJoin, rejectJoin, withdrawJoin })
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            decisions.Children.Add(button);
        }

        var candidatesPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 8 };
        Grid.SetRow(candidatesHeader, 0);
        Grid.SetRow(candidatesList, 1);
        Grid.SetRow(decisions, 2);
        Grid.SetRow(candidateCoverage, 3);
        Grid.SetRow(candidateNotes, 4);
        candidatesPage.Children.Add(candidatesHeader);
        candidatesPage.Children.Add(candidatesList);
        candidatesPage.Children.Add(decisions);
        candidatesPage.Children.Add(candidateCoverage);
        candidatesPage.Children.Add(candidateNotes);

        var timelineTools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { zoomIn, zoomOut, zoomWhole, savedViews, compareInstants, refreshTimeline })
        {
            button.Margin = new Thickness(8, 4, 0, 0);
            timelineTools.Children.Add(button);
        }

        // The chart and its lanes in words scroll together: where the page has room the chart is given all of it, and where
        // it has not, the tools above and the chosen column below stay in view however many lanes and notes there are.
        var drawing = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 8 };
        Grid.SetRow(timelineWords, 1);
        drawing.Children.Add(new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = timelineChart });
        drawing.Children.Add(timelineWords);
        var chart = new ScrollViewer
        {
            Content = drawing,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var chosenColumn = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        columnReadout.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(openColumn, 1);
        openColumn.VerticalAlignment = VerticalAlignment.Top;
        chosenColumn.Children.Add(columnReadout);
        chosenColumn.Children.Add(openColumn);
        var timelinePage = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(timelineTools, 1);
        Grid.SetRow(chart, 2);
        Grid.SetRow(chosenColumn, 3);
        timelinePage.Children.Add(timelineIntro);
        timelinePage.Children.Add(timelineTools);
        timelinePage.Children.Add(chart);
        timelinePage.Children.Add(chosenColumn);

        var noteActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { addNote, rewordNote, removeNote, showNote })
        {
            button.Margin = new Thickness(8, 4, 0, 0);
            noteActions.Children.Add(button);
        }

        var notesPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var notesBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = notesList, Padding = new Thickness(2) };
        Grid.SetRow(notesBorder, 1);
        Grid.SetRow(noteActions, 2);
        notesPage.Children.Add(notesIntro);
        notesPage.Children.Add(notesBorder);
        notesPage.Children.Add(noteActions);

        tabs.ItemsSource = new[]
        {
            new TabItem { Header = new TextBlock { Text = "Sessions", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = sessionsPage },
            new TabItem { Header = new TextBlock { Text = "Candidate joins", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = candidatesPage },
            new TabItem { Header = new TextBlock { Text = "Timeline", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = timelinePage },
            new TabItem { Header = new TextBlock { Text = "Notes", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = notesPage },
        };
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 2 && !timelineLoaded) _ = ShowTimelineAsync();
        };
        AutomationProperties.SetName(tabs, "Sessions, candidate joins, timeline and notes");

        overlapsView.Content = overlaps;
        var header = new StackPanel { Spacing = 4, Children = { heading, summary, time, overlapsView, status } };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { package, close },
        };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        Grid.SetRow(header, 0);
        Grid.SetRow(tabs, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(tabs);
        grid.Children.Add(footer);
        Content = grid;

        Opened += (_, _) => _ = RefreshAsync(opening);
        Closed += (_, _) => Dispose();
    }

    /// <summary>The investigation as last shown; null before it was first read, or when it could not be.</summary>
    internal InvestigationView? View { get; private set; }

    /// <summary>The candidate joins as last found; null before they were looked for.</summary>
    internal InvestigationCandidates? Candidates { get; private set; }

    private const string PackageName = "Package this investigation with its sessions, to share it";

    /// <summary>The timeline as last drawn; null before it was first shown.</summary>
    internal InvestigationTimelineView? Timeline { get; private set; }

    /// <summary>Each lane of the timeline in words, as last drawn.</summary>
    internal IReadOnlyList<string> TimelineSentences { get; private set; } = [];

    /// <summary>
    /// Draws the investigation's timeline, off the window's thread for its reading of every session. A request made while
    /// one is drawing waits for it and then draws again, so the timeline shows the latest zoom and notes asked for.
    /// </summary>
    internal async Task ShowTimelineAsync()
    {
        while (timelineLoad is { IsCompleted: false } running)
        {
            await running;
        }

        if (closed) return;
        timelineLoad = LoadTimelineAsync();
        await timelineLoad;
    }

    private async Task LoadTimelineAsync()
    {
        refreshTimeline.IsEnabled = false;
        timelineWords.Text = "Placing each session on the investigation's time…";
        try
        {
            CancellationToken token = lifetime.Token;
            (InvestigationTimelineView view, IReadOnlyList<string> labels, IReadOnlyList<string> sentences) =
                await Task.Run(() => InvestigationRows.Timeline(path, CultureInfo.CurrentCulture, 160, zoom, token), token);
            if (closed) return;
            Timeline = view;
            TimelineSentences = sentences;
            if (zoom is null)
            {
                whole = view.Interval;
            }

            timelineChart.Show(view, labels);
            zoomIn.IsEnabled = view.Interval is { } shown && shown.EndTicks - shown.StartTicks > 200;
            zoomOut.IsEnabled = zoomWhole.IsEnabled = zoom is not null;
            timelineWords.Text = string.Join("\n", sentences);
            timelineLoaded = true;
        }
        catch (OperationCanceledException) when (closed)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) timelineWords.Text = "The timeline could not be drawn: " + exception.Message;
        }
        finally
        {
            refreshTimeline.IsEnabled = true;
        }
    }

    /// <summary>The interval of the investigation's time the timeline shows; null when it shows the whole.</summary>
    internal TimeRange? Zoom => zoom;

    /// <summary>
    /// Shows <paramref name="range"/> of the investigation's time, within the whole of it, or the whole when it is null or
    /// reaches past it; the timeline is read again at its full column count there.
    /// </summary>
    internal async Task ZoomToAsync(TimeRange? range)
    {
        TimeRange? next = null;
        if (range is { } asked && whole is { } all)
        {
            long span = asked.EndTicks - asked.StartTicks;
            long start = Math.Clamp(asked.StartTicks, all.StartTicks, Math.Max(all.StartTicks, all.EndTicks - span));
            TimeRange within = new(start, Math.Min(checked(start + span), all.EndTicks));
            next = within.StartTicks <= all.StartTicks && within.EndTicks >= all.EndTicks ? null : within;
        }

        zoom = next;
        await ShowTimelineAsync();
    }

    private void ZoomBy(decimal factor)
    {
        if (Timeline?.Interval is not { } shown) return;
        long around = timelineChart.ChosenColumn is { } chosen && Timeline.Lanes[chosen.Lane].Buckets is { Count: > 0 } buckets
            ? buckets[chosen.Column].Interval.StartTicks + ((buckets[chosen.Column].Interval.EndTicks - buckets[chosen.Column].Interval.StartTicks) / 2)
            : shown.StartTicks + ((shown.EndTicks - shown.StartTicks) / 2);
        _ = ZoomToAsync(InvestigationTimelineControl.Zoomed(shown, around, factor));
    }

    /// <summary>The dialog of the investigation's saved views, with the interval shown now to save; a test uses it.</summary>
    internal InvestigationViewsWindow ViewsDialog() => new(path, Timeline?.Interval);

    private async Task SavedViewsAsync()
    {
        if (await ViewsDialog().ShowDialog<TimeRange?>(this) is { } chosen && !closed)
        {
            await ZoomToAsync(chosen);
        }
    }

    /// <summary>Chooses a lane's column on the timeline, as the arrow keys or a click do; a test uses it.</summary>
    internal void ChooseColumn(int lane, int column) => timelineChart.MoveTo(new(lane, column));

    /// <summary>What the timeline says of the chosen column.</summary>
    internal string ColumnReadout => columnReadout.Text ?? string.Empty;

    /// <summary>
    /// Why a column of none opens nothing, and, where its session's capture did not cover it, that none there is not proof
    /// of inactivity, as the session's own timeline hatches it (R21).
    /// </summary>
    internal static string NothingToOpen(int column, string name, CoverageState coverage) =>
        string.Create(CultureInfo.CurrentCulture, $"Column {column + 1:N0} holds no records of {name}") + coverage switch
        {
            CoverageState.Covered => ", so there is nothing of it to open: choose a column with records.",
            _ => ", and " + coverage switch
            {
                CoverageState.ReducedFidelity => "its capture was at reduced fidelity there",
                CoverageState.PartialGap => "its capture has a partial gap there",
                CoverageState.NotCollected => "its capture collected nothing there",
                _ => "what its capture covered there is unknown",
            } + ", so that is not proof of inactivity. There is nothing of it to open: choose a column with records.",
        };

    /// <summary>
    /// Opens a lane's column in InterCat's window: its session, its timeline zoomed to that column in the session's own
    /// time, and the column's interval selected, so its records are what the window shows.
    /// </summary>
    internal async Task<bool> OpenColumnAsync(TimelineColumn column)
    {
        if (Timeline is not { } timeline || openSessionAt is null || column.Lane >= timeline.Lanes.Count) return false;
        InvestigationLane lane = timeline.Lanes[column.Lane];
        if (column.Column >= lane.OwnIntervals.Count
            || View?.Members.FirstOrDefault(row => row.SessionId == lane.SessionId) is not { HoldsItsCapture: true } row)
        {
            return false;
        }

        TimeRange own = lane.OwnIntervals[column.Column];
        string name = row.Title.Split(',')[0];
        if (lane.Buckets[column.Column].ObservationCount == 0)
        {
            status.Text = NothingToOpen(column.Column, name, lane.Buckets[column.Column].Coverage);
            return false;
        }

        status.Text = $"Opening {name} at that column…";
        bool opened = await openSessionAt(row.FullPath, own);
        if (!closed)
        {
            status.Text = opened
                ? $"Opened {name} in the InterCat window, zoomed to {Seconds(own.StartTicks)} to {Seconds(own.EndTicks)} s of its own "
                    + "time, the column's interval selected."
                : $"{row.FullPath} could not be opened.";
        }

        return opened;
    }

    private static string Seconds(long ticks) => (ticks / 10_000_000m).ToString("0.0######", CultureInfo.CurrentCulture);

    /// <summary>Shows the sessions or the candidate joins, as choosing a tab does.</summary>
    internal void ShowTab(int index) => tabs.SelectedIndex = index;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>Reads the investigation again, resolving each session where it was last found, off the window's thread.</summary>
    internal async Task RefreshAsync(string? message = null)
    {
        if (loading || closed) return;
        loading = true;
        status.Text = message ?? "Looking for each session where it was last found…";
        Guid? selected = (members.SelectedItem as InvestigationMemberRow)?.SessionId;
        keptWhileReading.Clear();
        panesWhileReading = default;
        try
        {
            CancellationToken token = lifetime.Token;
            InvestigationView view = await Task.Run(() => InvestigationRows.Describe(path, CultureInfo.CurrentCulture, token), token);
            if (closed) return;
            view = keptWhileReading.Aggregate(view, (shown, kept) => WithKept(shown, kept.Key, kept.Value));
            if (panesWhileReading is (true, var written))
            {
                view = view with { Panes = InvestigationRows.PanesKept(written, CultureInfo.CurrentCulture) };
            }

            View = view;
            ShowPanes(view.Panes);
            summary.Text = view.Summary;
            time.Text = view.Time;
            overlaps.Text = string.Join("\n", view.Overlaps ?? []);
            overlaps.IsVisible = overlapsView.IsVisible = view.Overlaps is { Count: > 0 };
            caveats.Text = string.Join(" ", view.Caveats);
            members.ItemsSource = view.Members;
            Guid? noted = (notesList.SelectedItem as InvestigationNoteRow)?.NoteId;
            notesList.ItemsSource = view.Notes;
            notesList.SelectedItem = view.Notes.FirstOrDefault(row => row.NoteId == noted);
            ShowSelectedNote();
            package.IsEnabled = packaging is not null || view.Members.Count > 0;
            compareInstants.IsEnabled = view.Members.Count > 0;
            members.SelectedItem = view.Members.FirstOrDefault(row => row.SessionId == selected)
                ?? (view.Members.Count > 0 ? view.Members[0] : null);
            int unresolved = view.Members.Count(row => !row.HoldsItsCapture);
            status.Text = message ?? (unresolved == 0
                ? "Every session is where it was last found."
                : string.Create(CultureInfo.CurrentCulture,
                    $"{unresolved:N0} {(unresolved == 1 ? "session is" : "sessions are")} not where {(unresolved == 1 ? "it was" : "they were")} last found: relink {(unresolved == 1 ? "it" : "them")} to open {(unresolved == 1 ? "it" : "them")}."));
            ShowSelected();
        }
        catch (OperationCanceledException) when (closed)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (closed) return;
            View = null;
            ShowPanes(null);
            members.ItemsSource = Array.Empty<InvestigationMemberRow>();
            package.IsEnabled = packaging is not null;
            compareInstants.IsEnabled = false;
            summary.Text = string.Empty;
            time.Text = string.Empty;
            status.Text = "This investigation could not be read: " + exception.Message;
            ShowSelected();
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>
    /// Says what the investigation at <paramref name="investigation"/> now keeps of member <paramref name="sessionId"/>'s
    /// view, as the window showing it just wrote it (§26.3), in its row: without reading the investigation again, and kept
    /// over a reading under way that may have missed it.
    /// </summary>
    internal void LayoutKept(string investigation, Guid sessionId, WorkspaceLayout? layout)
    {
        if (closed || !string.Equals(Path.GetFullPath(investigation), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        if (loading)
        {
            keptWhileReading[sessionId] = layout;
        }

        if (View is not { } view) return;
        InvestigationView kept = WithKept(view, sessionId, layout);
        if (ReferenceEquals(kept, view)) return;
        Guid? selected = (members.SelectedItem as InvestigationMemberRow)?.SessionId;
        View = kept;
        members.ItemsSource = kept.Members;
        members.SelectedItem = kept.Members.FirstOrDefault(row => row.SessionId == selected);
    }

    /// <summary>
    /// Says what the investigation at <paramref name="investigation"/> now keeps of the window's panes, as the window
    /// showing one of its sessions just wrote them (§26.3): without reading the investigation again, and kept over a reading
    /// under way that may have missed them.
    /// </summary>
    internal void PanesKept(string investigation, WorkspacePanes? kept)
    {
        if (closed || !string.Equals(Path.GetFullPath(investigation), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        if (loading)
        {
            panesWhileReading = (true, kept);
        }

        if (View is not { } view) return;
        View = view with { Panes = InvestigationRows.PanesKept(kept, CultureInfo.CurrentCulture) };
        ShowPanes(View.Panes);
    }

    private void ShowPanes(string? kept)
    {
        panes.Text = kept;
        panes.IsVisible = kept is not null;
    }

    /// <summary>The view with member <paramref name="sessionId"/>'s row saying what <paramref name="layout"/> keeps; itself when it says so already.</summary>
    private static InvestigationView WithKept(InvestigationView view, Guid sessionId, WorkspaceLayout? layout)
    {
        string? kept = InvestigationRows.Kept(layout, CultureInfo.CurrentCulture);
        return view.Members.Any(row => row.SessionId == sessionId && row.Kept != kept)
            ? view with { Members = [.. view.Members.Select(row => row.SessionId == sessionId ? row with { Kept = kept } : row)] }
            : view;
    }

    /// <summary>Looks for candidate joins between the sessions, off the window's thread, and lists them with their evidence.</summary>
    internal async Task FindCandidatesAsync()
    {
        if (finding || closed) return;
        finding = true;
        find.IsEnabled = false;
        candidateSummary.Text = "Reading every session's connections and comparing their endpoints, mirrored…";
        try
        {
            CancellationToken token = lifetime.Token;
            InvestigationCandidates found = await Task.Run(() => InvestigationRows.Candidates(path, CultureInfo.CurrentCulture, token), token);
            if (closed) return;
            int selected = candidates.SelectedIndex;
            Candidates = found;
            candidates.ItemsSource = found.Rows;
            candidates.SelectedIndex = selected >= 0 && selected < found.Rows.Count ? selected : -1;
            candidateSummary.Text = found.Summary;

            // A capture that did not cover what a mirror is made of is said in the caution ink, before the rules every
            // candidate follows; one that covered both is said among them (R21).
            candidateCoverage.Text = found.CoverageShort ? string.Join(" ", found.Coverage) : null;
            candidateCoverage.IsVisible = found.CoverageShort;
            candidateNotes.Text = string.Join(" ", found.CoverageShort ? found.Notes : [.. found.Coverage, .. found.Notes]);
            ShowSelectedCandidate();
        }
        catch (OperationCanceledException) when (closed)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) candidateSummary.Text = "Candidate joins could not be found: " + exception.Message;
        }
        finally
        {
            finding = false;
            find.IsEnabled = true;
        }
    }

    /// <summary>
    /// Records your decision about the selected candidate - accepted as one connection, rejected, or withdrawn - as a kept
    /// revision of the investigation, and finds the candidates again to show it.
    /// </summary>
    internal async Task DecideSelectedAsync(WorkspaceJoinDecision decision)
    {
        if (candidates.SelectedItem is not InvestigationCandidateRow { FirstEnd: { } first, SecondEnd: { } second }) return;
        try
        {
            _ = await Task.Run(() => InvestigationWorkspace.Decide(path, first, second, decision, null, DateTimeOffset.UtcNow));
            await FindCandidatesAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) candidateSummary.Text = exception.Message;
        }
    }

    /// <summary>The dialog of the investigation's known address translations; a test uses it without showing it modally.</summary>
    internal InvestigationTranslationsWindow TranslationsDialog() => new(path);

    private async Task TranslationsAsync()
    {
        InvestigationTranslationsWindow dialog = TranslationsDialog();
        if (await dialog.ShowDialog<bool>(this) && !closed)
        {
            await FindCandidatesAsync();
        }
    }

    /// <summary>Selects a candidate, as choosing its row does; a test uses it.</summary>
    internal void SelectCandidate(int index) => candidates.SelectedIndex = index;

    private void ShowSelectedCandidate()
    {
        InvestigationCandidateRow? row = candidates.SelectedItem as InvestigationCandidateRow;
        acceptJoin.IsEnabled = row is not null && row.Decision != WorkspaceJoinDecision.Accepted;
        rejectJoin.IsEnabled = row is not null && row.Decision != WorkspaceJoinDecision.Rejected;
        withdrawJoin.IsEnabled = row?.Decision is not null;
    }

    /// <summary>Adds sessions by their folders, then shows the investigation again with what each addition did.</summary>
    internal async Task AddAsync(IReadOnlyList<string> directories)
    {
        List<string> refused = [];
        int added = 0;
        foreach (string directory in directories)
        {
            try
            {
                await Task.Run(() => InvestigationWorkspace.Add(path, directory, DateTimeOffset.UtcNow));
                added++;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException)
            {
                refused.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))} was not added: {exception.Message}");
            }
        }

        List<string> notes = [];
        if (added > 0)
        {
            notes.Add(string.Create(CultureInfo.CurrentCulture, $"{added:N0} {(added == 1 ? "session" : "sessions")} added."));
        }

        notes.AddRange(refused);
        await RefreshAsync(notes.Count == 0 ? null : string.Join(" ", notes));
    }

    /// <summary>Relinks a member to a folder, which must hold that member's session; says why when it does not.</summary>
    internal async Task RelinkAsync(Guid sessionId, string directory)
    {
        try
        {
            WorkspaceMember relinked = await Task.Run(() => InvestigationWorkspace.Relink(path, sessionId, directory, DateTimeOffset.UtcNow));
            await RefreshAsync(string.Create(CultureInfo.CurrentCulture,
                $"Session {sessionId.ToString("N")[..8]} relinked to {directory}, generation {relinked.Generation:N0}."));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) status.Text = exception.Message;
        }
    }

    /// <summary>The dialog that aligns the selected session; a test fills it in and aligns without showing it modally.</summary>
    internal InvestigationAlignWindow? AlignDialogForSelected()
    {
        if (View is not { } view || members.SelectedItem is not InvestigationMemberRow row || row.IsTimeReference || view.Members.Count < 2)
        {
            return null;
        }

        // Once the investigation has a time, a session is aligned to one placed in it, never to one aligned through itself.
        AlignmentReference[] references =
        [
            .. view.Members.Where(other => other.SessionId != row.SessionId
                    && (view.TimeReference is null || (other.IsPlaced && other.AlignedThrough?.Contains(row.SessionId) != true)))
                .Select(other => new AlignmentReference(other.SessionId, other.Title.Split(',')[0] + " · " + other.Detail
                    + (other.IsTimeReference ? " · the investigation's clock" : string.Empty))),
        ];
        return new InvestigationAlignWindow(path, row, references, view.TimeReference);
    }

    /// <summary>Withdraws the selected session's alignment, kept as a revision, and shows the investigation again.</summary>
    internal async Task WithdrawSelectedAsync()
    {
        if (members.SelectedItem is not InvestigationMemberRow { IsAligned: true } row) return;
        try
        {
            _ = await Task.Run(() => InvestigationWorkspace.Withdraw(path, row.SessionId, DateTimeOffset.UtcNow));
            await RefreshAsync($"{row.Title.Split(',')[0]} is not aligned any more; its earlier alignment is kept in the file.");
            if (timelineLoaded) await ShowTimelineAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) status.Text = exception.Message;
        }
    }

    /// <summary>
    /// Packages the investigation with its sessions (§8.4): what a package would hold is measured and stated first, the
    /// person chooses its sessions and where the new folder goes, and the package, verified before it appears, can be opened
    /// here. While it is made, the same button cancels it.
    /// </summary>
    private async Task PackageAsync()
    {
        if (packaging is { } running)
        {
            running.Cancel();
            return;
        }

        InvestigationPackagePreview preview;
        status.Text = "Measuring what a package of this investigation would hold…";
        try
        {
            CancellationToken token = lifetime.Token;
            preview = await Task.Run(() => InvestigationPackage.Preview(path, token), token);
        }
        catch (OperationCanceledException) when (closed)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) status.Text = "The investigation could not be measured to package it: " + exception.Message;
            return;
        }

        if (closed) return;
        if (preview.Copied.Count == 0)
        {
            status.Text = "No session of this investigation is where it was last found, so there is nothing to package. Relink "
                + "its sessions first.";
            return;
        }

        var prompt = new InvestigationPackageWindow(preview);
        if (!await prompt.ShowDialog<bool>(this) || closed)
        {
            if (!closed) status.Text = "Nothing was packaged.";
            return;
        }

        IReadOnlyList<Guid> chosen = prompt.Chosen;
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Choose where to save the investigation package",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || closed)
        {
            if (!closed) status.Text = "Nothing was packaged.";
            return;
        }

        string destination = MainWindow.NewPackageDirectory(
            folders[0].Path.LocalPath, DateTimeOffset.Now, Path.GetFileNameWithoutExtension(path) + "-package");
        InvestigationPackageResult? result = await WritePackageAsync(destination, chosen);
        if (result is not null && !closed && await ShowPackageResultAsync(result) && Owner is MainWindow main)
        {
            _ = main.ShowInvestigation(result.WorkspacePath);
        }
    }

    /// <summary>
    /// Makes the package off the window's thread, its progress in the status line, and returns null when it was cancelled
    /// or refused - the status line then says which, and nothing is saved.
    /// </summary>
    internal async Task<InvestigationPackageResult?> WritePackageAsync(string destination, IReadOnlyList<Guid>? chosen)
    {
        if (packaging is not null || closed) return null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        packaging = cancellation;
        package.Content = "Cancel packaging";
        package.IsEnabled = true;
        AutomationProperties.SetName(package, "Cancel packaging; nothing is saved");
        status.Text = "Copying and checking the sessions…";
        var progress = new Progress<InvestigationPackageProgress>(update =>
        {
            if (closed || packaging != cancellation) return;
            string stage = update.Stage == OriginalPackageStage.Copying ? "Copying and checking" : "Reopening and verifying";
            status.Text = string.Create(CultureInfo.CurrentCulture,
                $"{stage} session {update.Session:N0} of {update.Sessions:N0}… {(update.Total > 0 ? update.Done * 100 / update.Total : 100)}%");
        });
        try
        {
            InvestigationPackageResult result = await Task.Run(
                () => InvestigationPackage.Create(path, destination, chosen, progress, cancellation.Token), cancellation.Token);
            if (!closed) status.Text = Saved(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!closed) status.Text = "Packaging cancelled. Nothing was saved, and the investigation and its sessions are unchanged.";
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) status.Text = "The investigation could not be packaged: " + exception.Message;
            return null;
        }
        finally
        {
            packaging = null;
            if (!closed)
            {
                package.Content = "Package…";
                package.IsEnabled = View is { Members.Count: > 0 };
                AutomationProperties.SetName(package, PackageName);
            }
        }
    }

    /// <summary>What saving a package did, as the status line says it.</summary>
    internal static string Saved(InvestigationPackageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int copies = result.Members.Count(member => member.PackagedPath is not null);
        int references = result.Members.Count - copies;
        return string.Create(CultureInfo.CurrentCulture, $"Saved the investigation with {Spoken.Count(copies, "session")} to ")
            + $"{result.Directory}. Each copy was checked as it was copied, and the package was reopened and resolved before it "
            + "was saved."
            + (references == 0 ? string.Empty : references == 1
                ? " One session was not copied and stays a reference to relink."
                : string.Create(CultureInfo.CurrentCulture, $" {references:N0} sessions were not copied and stay references to relink."));
    }

    private async Task<bool> ShowPackageResultAsync(InvestigationPackageResult result) =>
        await PackageResultPrompt(result, offersOpen: Owner is MainWindow).ShowDialog<bool>(this);

    /// <summary>
    /// Says where the package is and what verified it; true when the person wants to open it here, which it offers only
    /// where a main window can open it.
    /// </summary>
    internal static Window PackageResultPrompt(InvestigationPackageResult result, bool offersOpen)
    {
        var prompt = new Window
        {
            Title = "Investigation package saved",
            Width = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var done = new Button { Content = "Done" };
        var open = new Button { Content = "Open it here", IsVisible = offersOpen };
        AutomationProperties.SetHelpText(open, "Open the package's investigation in a window of its own");
        done.Click += (_, _) => prompt.Close(false);
        open.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => done.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            prompt.Close(false);
            key.Handled = true;
        };
        int copies = result.Members.Count(member => member.PackagedPath is not null);
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        content.Children.Add(Paragraph(string.Create(CultureInfo.CurrentCulture,
            $"Saved {Path.GetFileName(result.WorkspacePath)} with exact copies of {Spoken.Count(copies, "session")} ")
            + string.Create(CultureInfo.CurrentCulture,
                $"({Spoken.Count(result.Files, "file")}, {RecentSessions.Size(result.Bytes, CultureInfo.CurrentCulture)}) to {result.Directory}.")));
        content.Children.Add(Paragraph("Each file was checked against the digest its generation recorded as it was copied, "
            + "each copy was reopened and hashed as a recipient would, and the investigation was reopened from its own file, "
            + "every copy found beside it, before the package was saved."));
        foreach (InvestigationPackageMember member in result.Members.Where(member => member.Note is not null))
        {
            content.Children.Add(Paragraph($"Session {member.SessionId.ToString("N")[..8]}: {member.Note}"));
        }

        content.Children.Add(Paragraph(InvestigationPackage.WarningFor(result.Source)));
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { done, open },
        });
        prompt.Content = content;
        return prompt;
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>
    /// The dialog that says whether the selected session's host is one host with another identity of the investigation;
    /// null when there is no other identity. A test decides in it without showing it modally.
    /// </summary>
    internal InvestigationHostWindow? HostDialogForSelected()
    {
        if (members.SelectedItem is not InvestigationMemberRow row) return null;
        InvestigationWorkspaceFile workspace;
        try
        {
            workspace = InvestigationWorkspace.Read(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            status.Text = "This investigation could not be read: " + exception.Message;
            return null;
        }

        IReadOnlyList<WorkspaceHost> hosts = InvestigationWorkspace.Hosts(workspace);
        HashSet<Guid> direct = [.. InvestigationWorkspace.EquivalencesInForce(workspace)
            .Where(equivalence => equivalence.First == row.HostId || equivalence.Second == row.HostId)
            .Select(equivalence => equivalence.First == row.HostId ? equivalence.Second : equivalence.First)];
        IReadOnlyList<Guid> reached = InvestigationWorkspace.OneHostWith(workspace, row.HostId);
        string Name(WorkspaceHost host) => host.Alias ?? "host " + host.HostId.ToString("N")[..8];
        HostChoice[] choices =
        [
            .. hosts.Where(host => host.HostId != row.HostId).Select(host => new HostChoice(
                host.HostId,
                $"{Name(host)} · {Spoken.Count(host.Members.Count, "session")}"
                    + (reached.Contains(host.HostId) && !direct.Contains(host.HostId) ? " - one host with it now, through another confirmation" : string.Empty),
                direct.Contains(host.HostId))),
        ];
        return choices.Length == 0
            ? null
            : new InvestigationHostWindow(path, row.HostId, Name(hosts.First(host => host.HostId == row.HostId)), choices);
    }

    private async Task OneHostSelectedAsync()
    {
        if (HostDialogForSelected() is not { } dialog) return;
        if (await dialog.ShowDialog<bool>(this) && !closed)
        {
            await RefreshAsync("Recorded as a revision of the investigation: its captures are compared as their hosts now read.");
            if (timelineLoaded) await ShowTimelineAsync();
        }
    }

    /// <summary>The dialog that compares two instants of the investigation's sessions; null before its sessions are read.</summary>
    internal InvestigationCompareWindow? CompareDialog()
    {
        if (View is not { Members.Count: > 0 } view) return null;
        CompareSession[] sessions =
        [
            .. view.Members.Select(row => new CompareSession(row.SessionId, row.Title.Split(',')[0] + " · "
                + Path.GetFileName(Path.TrimEndingDirectorySeparator(row.FullPath))
                + (row.IsTimeReference ? " · reference clock" : row.IsAligned ? string.Empty : " · not aligned"))),
        ];
        return new InvestigationCompareWindow(path, sessions);
    }

    private async Task CompareInstantsAsync()
    {
        if (CompareDialog() is { } dialog) await dialog.ShowDialog(this);
    }

    /// <summary>Selects a note, as choosing its row does; a test uses it.</summary>
    internal void SelectNote(int index) => notesList.SelectedIndex = index;

    /// <summary>
    /// The dialog that adds a note - pinned, by default, at the timeline's chosen column in its session's own time - or
    /// rewords the selected one; a test writes in it without showing it modally.
    /// </summary>
    internal InvestigationNoteWindow? NoteDialog(bool reword)
    {
        if (View is not { } view) return null;
        InvestigationNoteRow? existing = reword ? notesList.SelectedItem as InvestigationNoteRow : null;
        if (reword && existing is null) return null;
        CompareSession[] sessions =
        [
            .. view.Members.Select(row => new CompareSession(row.SessionId, row.Title.Split(',')[0] + " · "
                + Path.GetFileName(Path.TrimEndingDirectorySeparator(row.FullPath)))),
        ];
        (Guid, long)? suggested = null;
        if (Timeline is { } timeline && timelineChart.ChosenColumn is { } chosen && chosen.Column < timeline.Lanes[chosen.Lane].OwnIntervals.Count)
        {
            suggested = (timeline.Lanes[chosen.Lane].SessionId, timeline.Lanes[chosen.Lane].OwnIntervals[chosen.Column].StartTicks * 100);
        }

        return new InvestigationNoteWindow(path, sessions, existing, suggested);
    }

    private async Task WriteNoteAsync(bool reword)
    {
        if (NoteDialog(reword) is not { } dialog) return;
        if (await dialog.ShowDialog<bool>(this) && !closed)
        {
            await RefreshAsync(reword ? "The note is reworded; its earlier words are kept." : "The note is added.");
            if (timelineLoaded) await ShowTimelineAsync();
        }
    }

    /// <summary>Removes the selected note, kept as a revision, and shows the investigation again.</summary>
    internal async Task RemoveSelectedNoteAsync()
    {
        if (notesList.SelectedItem is not InvestigationNoteRow row) return;
        try
        {
            _ = await Task.Run(() => InvestigationWorkspace.RemoveNote(path, row.NoteId, DateTimeOffset.UtcNow));
            await RefreshAsync("The note is removed; its revisions are kept in the file.");
            if (timelineLoaded) await ShowTimelineAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) status.Text = exception.Message;
        }
    }

    /// <summary>Shows the selected pinned note on the timeline: zoomed around its instant, the cursor on its column.</summary>
    internal async Task ShowSelectedNoteAsync()
    {
        if (notesList.SelectedItem is not InvestigationNoteRow { At: not null } row) return;
        ShowTab(2);
        if (!timelineLoaded) await ShowTimelineAsync();
        if (Timeline?.Notes.FirstOrDefault(note => note.Note.NoteId == row.NoteId) is not { Lane: { } lane, Ticks: { } ticks }
            || (whole ?? Timeline.Interval) is not { } all)
        {
            status.Text = "That note's session has no place in the investigation's time, so the timeline cannot show it.";
            return;
        }

        await ZoomToAsync(InvestigationTimelineControl.Zoomed(all, ticks, 1m / 16));
        if (Timeline?.Lanes[lane].Buckets is { Count: > 0 } buckets)
        {
            int column = Math.Max(0, buckets.ToList().FindLastIndex(bucket => bucket.Interval.StartTicks <= ticks));
            timelineChart.MoveTo(new(lane, column));
        }
    }

    private void ShowSelectedNote()
    {
        InvestigationNoteRow? row = notesList.SelectedItem as InvestigationNoteRow;
        rewordNote.IsEnabled = removeNote.IsEnabled = row is not null;
        showNote.IsEnabled = row is { At: not null };
    }

    private async Task AlignSelectedAsync()
    {
        if (AlignDialogForSelected() is not { } dialog || members.SelectedItem is not InvestigationMemberRow row) return;
        if (await dialog.ShowDialog<bool>(this) && !closed)
        {
            await RefreshAsync($"{row.Title.Split(',')[0]} aligned to the investigation's time.");
            if (timelineLoaded) await ShowTimelineAsync();
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (members.SelectedItem is not InvestigationMemberRow { HoldsItsCapture: true } row || openSession is null) return;
        status.Text = $"Opening {row.FullPath}…";
        bool opened = await openSession(row.FullPath);
        if (!closed) status.Text = opened ? $"Opened {row.Title.Split(',')[0]} in the InterCat window." : $"{row.FullPath} could not be opened.";
    }

    private async Task PickRelinkAsync()
    {
        if (members.SelectedItem is not InvestigationMemberRow row) return;
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = $"Where is {row.Title.Split(',')[0]} now?",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && !closed) await RelinkAsync(row.SessionId, folders[0].Path.LocalPath);
    }

    private async Task PickAddAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Add sessions to this investigation",
            AllowMultiple = true,
        });
        if (folders.Count > 0 && !closed) await AddAsync([.. folders.Select(folder => folder.Path.LocalPath)]);
    }

    private void ShowSelected()
    {
        InvestigationMemberRow? row = members.SelectedItem as InvestigationMemberRow;
        open.IsEnabled = row is { HoldsItsCapture: true } && openSession is not null;
        relink.IsEnabled = row is not null;
        alignButton.IsEnabled = row is { IsTimeReference: false } && View is { Members.Count: > 1 };
        withdraw.IsEnabled = row is { IsAligned: true };
        oneHost.IsEnabled = row is not null && View is { } shown && shown.Members.Any(other => other.HostId != row.HostId);
        detail.Text = row is null ? string.Empty : $"{row.Title.Split(',')[0]} {(row.HoldsItsCapture ? "is at" : "was last found at")}";
        detailPath.Text = row?.FullPath;
        ToolTip.SetTip(detailPath, row?.FullPath);
    }
}
