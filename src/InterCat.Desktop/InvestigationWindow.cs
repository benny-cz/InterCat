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
/// investigation's time - and the candidate joins between its captures (ADR-041). Showing it opens each session as a
/// viewer does and writes to none; only the investigation's own file is written.
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
    private readonly TextBlock caveats = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } };
    private readonly ListBox members = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button open = new() { Content = "Open in InterCat", IsEnabled = false };
    private readonly Button relink = new() { Content = "Relink…", IsEnabled = false };
    private readonly Button alignButton = new() { Content = "Align…", IsEnabled = false };
    private readonly Button withdraw = new() { Content = "Withdraw alignment", IsEnabled = false };
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
    private readonly TabControl tabs = new();
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
        AutomationProperties.SetName(add, "Add sessions to this investigation");
        AutomationProperties.SetName(refresh, "Look again where each session was last found");
        AutomationProperties.SetName(status, "Investigation status");
        AutomationProperties.SetName(candidates, "Candidate joins between the sessions; none is established");
        AutomationProperties.SetName(find, "Find candidate joins between the sessions");
        AutomationProperties.SetName(candidateSummary, "What finding candidate joins found");
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
        add.Click += (_, _) => _ = PickAddAsync();
        refresh.Click += (_, _) => _ = RefreshAsync();
        find.Click += (_, _) => _ = FindCandidatesAsync();
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
        foreach (Button button in new[] { open, relink, alignButton, withdraw, add, refresh })
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
        var candidatesPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        Grid.SetRow(candidatesHeader, 0);
        Grid.SetRow(candidatesList, 1);
        Grid.SetRow(candidateNotes, 2);
        candidatesPage.Children.Add(candidatesHeader);
        candidatesPage.Children.Add(candidatesList);
        candidatesPage.Children.Add(candidateNotes);

        tabs.ItemsSource = new[]
        {
            new TabItem { Header = new TextBlock { Text = "Sessions", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = sessionsPage },
            new TabItem { Header = new TextBlock { Text = "Candidate joins", FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = candidatesPage },
        };
        AutomationProperties.SetName(tabs, "Sessions and candidate joins");

        var header = new StackPanel { Spacing = 4, Children = { heading, summary, time, status } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(close, 1);
        close.VerticalAlignment = VerticalAlignment.Bottom;
        caveats.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(caveats);
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
            caveats.Text = string.Join(" ", view.Caveats);
            members.ItemsSource = view.Members;
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
            Candidates = found;
            candidates.ItemsSource = found.Rows;
            candidateSummary.Text = found.Summary;
            candidateNotes.Text = string.Join(" ", found.Notes);
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

        AlignmentReference[] references =
        [
            .. view.Members.Where(other => other.SessionId != row.SessionId)
                .Select(other => new AlignmentReference(other.SessionId, other.Title.Split(',')[0] + " · " + other.Detail)),
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
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed) status.Text = exception.Message;
        }
    }

    private async Task AlignSelectedAsync()
    {
        if (AlignDialogForSelected() is not { } dialog || members.SelectedItem is not InvestigationMemberRow row) return;
        if (await dialog.ShowDialog<bool>(this) && !closed)
        {
            await RefreshAsync($"{row.Title.Split(',')[0]} aligned to the investigation's time.");
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
        detail.Text = row is null
            ? string.Empty
            : $"{row.Title}: {row.FullPath}. {row.Time}." + (row.Reason is null ? string.Empty : $" {row.Reason}");
    }
}
