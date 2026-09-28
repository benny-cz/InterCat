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
/// time, with the gestures that keep it whole - open a member, relink one that moved, add sessions. Showing it opens each
/// session as a viewer does and writes to none; only the investigation's own file is written.
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
    private readonly Button add = new() { Content = "Add sessions…" };
    private readonly Button refresh = new() { Content = "Refresh" };
    private bool loading;
    private bool closed;
    private bool disposed;

    public InvestigationWindow(string path, Func<string, Task<bool>>? openSession, string? opening = null)
    {
        this.path = Path.GetFullPath(path);
        this.openSession = openSession;
        Title = $"InterCat · Investigation · {Path.GetFileName(this.path)}";
        Width = 900;
        Height = 620;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        heading.Text = this.path;

        AutomationProperties.SetName(members, "Sessions of this investigation; press Enter to open the selected one");
        AutomationProperties.SetName(open, "Open the selected session in InterCat");
        AutomationProperties.SetName(relink, "Relink the selected session to where it is now");
        AutomationProperties.SetName(add, "Add sessions to this investigation");
        AutomationProperties.SetName(refresh, "Look again where each session was last found");
        AutomationProperties.SetName(status, "Investigation status");
        AccessibleItems.Name(members);
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
        add.Click += (_, _) => _ = PickAddAsync();
        refresh.Click += (_, _) => _ = RefreshAsync();
        var close = new Button { Content = "Close" };
        AutomationProperties.SetName(close, "Close the investigation window");
        close.Click += (_, _) => Close();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var header = new StackPanel { Spacing = 4, Children = { heading, summary, time, status } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { open, relink, add, refresh, close },
        };
        Grid.SetColumn(actions, 1);
        footer.Children.Add(caveats);
        footer.Children.Add(actions);
        caveats.Margin = new Thickness(0, 0, 12, 0);
        var list = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = members, Padding = new Thickness(2) };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(list, 1);
        Grid.SetRow(detail, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(header);
        grid.Children.Add(list);
        grid.Children.Add(detail);
        grid.Children.Add(footer);
        Content = grid;

        Opened += (_, _) => _ = RefreshAsync(opening);
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>The investigation as last shown; null before it was first read, or when it could not be.</summary>
    internal InvestigationView? View { get; private set; }

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
        detail.Text = row is null
            ? string.Empty
            : $"{row.Title}: {row.FullPath}. {row.Time}." + (row.Reason is null ? string.Empty : $" {row.Reason}");
    }
}
