using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterCat.Application;
using InterCat.Desktop.Presentation;

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
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock heading = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock time = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock overlaps = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "caution" }, IsVisible = false };
    private readonly TextBlock caveats = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } };
    private readonly ListBox members = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
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
    private readonly Button find = new() { Content = "Find candidate joins" };
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
            + "scaled to its own busiest column. A session with no alignment has no place.",
    };
    private readonly Button refreshTimeline = new() { Content = "Refresh the timeline" };
    private readonly Button package = new() { Content = "Package…", IsEnabled = false };
    private CancellationTokenSource? packaging;
    private bool timelineLoaded;
    private bool timelineLoading;
    private bool loading;
    private bool finding;
    private bool closed;
    private bool disposed;

    public InvestigationWindow(string path, Func<string, Task<bool>>? openSession, string? opening = null)
    {
        this.path = Path.GetFullPath(path);
        this.openSession = openSession;
        Title = $"InterCat · Investigation · {Path.GetFileName(this.path)}";
        Width = 940;
        Height = 660;
        MinWidth = 680;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        heading.Text = this.path;

        AutomationProperties.SetName(members, "Sessions of this investigation; press Enter to open the selected one");
        AutomationProperties.SetName(open, "Open the selected session in InterCat");
        AutomationProperties.SetName(relink, "Relink the selected session to where it is now");
        AutomationProperties.SetName(alignButton, "Align the selected session to the investigation's time");
        AutomationProperties.SetName(withdraw, "Withdraw the selected session's alignment");
        AutomationProperties.SetName(oneHost, "Say whether the selected session's host is one host with another");
        AutomationProperties.SetName(add, "Add sessions to this investigation");
        AutomationProperties.SetName(refresh, "Look again where each session was last found");
        AutomationProperties.SetName(status, "Investigation status");
        AutomationProperties.SetName(overlaps, "Sessions of one host that ran at once, or may have");
        AutomationProperties.SetName(candidates, "Candidate joins between the sessions; none is established");
        AutomationProperties.SetName(find, "Find candidate joins between the sessions");
        AutomationProperties.SetName(candidateSummary, "What finding candidate joins found");
        AutomationProperties.SetName(timelineWords, "The investigation's timeline, each session in words");
        AutomationProperties.SetName(refreshTimeline, "Draw the investigation's timeline again");
        AutomationProperties.SetName(acceptJoin, "Accept the selected candidate as one connection, as your decision");
        AutomationProperties.SetName(rejectJoin, "Reject the selected candidate, as your decision");
        AutomationProperties.SetName(withdrawJoin, "Withdraw your decision about the selected candidate");
        AutomationProperties.SetName(package, PackageName);
        AccessibleItems.Name(members);
        AccessibleItems.Name(candidates);
        members.ItemTemplate = new FuncDataTemplate<InvestigationMemberRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = row?.Detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = row?.Time, FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
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
        refreshTimeline.Click += (_, _) => _ = ShowTimelineAsync();
        acceptJoin.Click += (_, _) => _ = DecideSelectedAsync(WorkspaceJoinDecision.Accepted);
        rejectJoin.Click += (_, _) => _ = DecideSelectedAsync(WorkspaceJoinDecision.Rejected);
        withdrawJoin.Click += (_, _) => _ = DecideSelectedAsync(WorkspaceJoinDecision.Withdrawn);
        package.Click += (_, _) => _ = PackageAsync();
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
        var sessionsPage = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(sessionsList, 0);
        Grid.SetRow(detail, 1);
        Grid.SetRow(sessionActions, 2);
        sessionsPage.Children.Add(sessionsList);
        sessionsPage.Children.Add(detail);
        sessionsPage.Children.Add(sessionActions);

        var candidatesHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(find, 1);
        find.VerticalAlignment = VerticalAlignment.Top;
        candidateSummary.Margin = new Thickness(0, 0, 12, 0);
        candidatesHeader.Children.Add(candidateSummary);
        candidatesHeader.Children.Add(find);
        var candidatesList = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = candidates, Padding = new Thickness(2) };
        var decisions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { acceptJoin, rejectJoin, withdrawJoin })
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            decisions.Children.Add(button);
        }

        var candidatesPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8 };
        Grid.SetRow(candidatesHeader, 0);
        Grid.SetRow(candidatesList, 1);
        Grid.SetRow(decisions, 2);
        Grid.SetRow(candidateNotes, 3);
        candidatesPage.Children.Add(candidatesHeader);
        candidatesPage.Children.Add(candidatesList);
        candidatesPage.Children.Add(decisions);
        candidatesPage.Children.Add(candidateNotes);

        var timelineHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(refreshTimeline, 1);
        refreshTimeline.VerticalAlignment = VerticalAlignment.Top;
        timelineIntro.Margin = new Thickness(0, 0, 12, 0);
        timelineHeader.Children.Add(timelineIntro);
        timelineHeader.Children.Add(refreshTimeline);
        var timelinePage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        var chart = new ScrollViewer
        {
            Content = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = timelineChart },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(timelineHeader, 0);
        Grid.SetRow(chart, 1);
        Grid.SetRow(timelineWords, 2);
        timelinePage.Children.Add(timelineHeader);
        timelinePage.Children.Add(chart);
        timelinePage.Children.Add(timelineWords);

        tabs.ItemsSource = new[]
        {
            new TabItem { Header = new TextBlock { Text = "Sessions", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = sessionsPage },
            new TabItem { Header = new TextBlock { Text = "Candidate joins", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = candidatesPage },
            new TabItem { Header = new TextBlock { Text = "Timeline", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = timelinePage },
        };
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 2 && !timelineLoaded) _ = ShowTimelineAsync();
        };
        AutomationProperties.SetName(tabs, "Sessions and candidate joins");

        var header = new StackPanel { Spacing = 4, Children = { heading, summary, time, overlaps, status } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(package, 1);
        Grid.SetColumn(close, 2);
        package.VerticalAlignment = VerticalAlignment.Bottom;
        package.Margin = new Thickness(0, 0, 8, 0);
        close.VerticalAlignment = VerticalAlignment.Bottom;
        caveats.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(caveats);
        footer.Children.Add(package);
        footer.Children.Add(close);
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

    /// <summary>Draws the investigation's timeline, off the window's thread for its reading of every session.</summary>
    internal async Task ShowTimelineAsync()
    {
        if (timelineLoading || closed) return;
        timelineLoading = true;
        refreshTimeline.IsEnabled = false;
        timelineWords.Text = "Placing each session on the investigation's time…";
        try
        {
            CancellationToken token = lifetime.Token;
            (InvestigationTimelineView view, IReadOnlyList<string> labels, IReadOnlyList<string> sentences) =
                await Task.Run(() => InvestigationRows.Timeline(path, CultureInfo.CurrentCulture, 160, token), token);
            if (closed) return;
            Timeline = view;
            TimelineSentences = sentences;
            timelineChart.Show(view, labels);
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
            timelineLoading = false;
            refreshTimeline.IsEnabled = true;
        }
    }

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
        try
        {
            CancellationToken token = lifetime.Token;
            InvestigationView view = await Task.Run(() => InvestigationRows.Describe(path, CultureInfo.CurrentCulture, token), token);
            if (closed) return;
            View = view;
            summary.Text = view.Summary;
            time.Text = view.Time;
            overlaps.Text = string.Join("\n", view.Overlaps ?? []);
            overlaps.IsVisible = view.Overlaps is { Count: > 0 };
            caveats.Text = string.Join(" ", view.Caveats);
            members.ItemsSource = view.Members;
            package.IsEnabled = packaging is not null || view.Members.Count > 0;
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
            members.ItemsSource = Array.Empty<InvestigationMemberRow>();
            package.IsEnabled = packaging is not null;
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
            candidateNotes.Text = string.Join(" ", found.Notes);
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

    /// <summary>Says where the package is and what verified it; true when the person wants to open it here.</summary>
    private async Task<bool> ShowPackageResultAsync(InvestigationPackageResult result)
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
        var open = new Button { Content = "Open it here", IsVisible = Owner is MainWindow };
        AutomationProperties.SetName(open, "Open the package's investigation in a window of its own");
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
        return await prompt.ShowDialog<bool>(this);
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
        detail.Text = row is null
            ? string.Empty
            : $"{row.Title}: {row.FullPath}. {row.Time}." + (row.Reason is null ? string.Empty : $" {row.Reason}");
    }
}
