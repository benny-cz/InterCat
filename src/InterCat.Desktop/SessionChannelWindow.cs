using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// A bounded channel browser. Discovery covers the full admitted paired-TCP relation set, including sessions whose
/// overview intentionally omits its L3 projection. Choosing a channel closes the browser with that channel, and the
/// workspace descends to its source records; the browser itself shows no rows.
/// </summary>
internal sealed class SessionChannelWindow : Window, IDisposable
{
    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly ProcessInstanceId? processScope;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock caveat = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox rows = new();
    private readonly TextBlock selectedDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button inspect = new() { Content = "Show this channel's source records", IsEnabled = false };
    private readonly Button next = new() { Content = "Next 100 channels", IsEnabled = false };
    private IReadOnlyList<Channel> currentChannels = [];
    private string? nextCursor;
    private bool loading;
    private bool closed;
    private bool disposed;

    public SessionChannelWindow(string path, Guid expectedSessionId, long expectedGeneration,
        ProcessInstanceId? processScope)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.processScope = processScope;
        Title = "InterCat · Paired TCP channels";
        Width = 880;
        Height = 650;
        MinWidth = 650;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scope = new TextBlock
        {
            Text = (processScope is { } process
                ? $"Channels involving process instance {process}"
                : "All admitted paired TCP channels")
                + " · all session times (a time brush applies to the source records you open, not to this list)",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        status.Text = "Opening a verified channel generation…";
        caveat.Text = "This list is paired TCP only. One-sided and ambiguous observations remain in "
            + "whole-session source rows. Counts are observed records, not bytes or logical operations.";
        rows.SelectionChanged += (_, _) => UpdateSelection();
        next.Click += (_, _) =>
        {
            if (nextCursor is { } cursor) _ = LoadPageAsync(cursor);
        };
        inspect.Click += (_, _) => ChooseSelected();
        rows.DoubleTapped += (_, _) => ChooseSelected();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { inspect, next, close },
        };
        var header = new StackPanel { Spacing = 5, Children = { scope, status, caveat } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(rows, 1);
        Grid.SetRow(selectedDetail, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(header);
        grid.Children.Add(rows);
        grid.Children.Add(selectedDetail);
        grid.Children.Add(footer);
        Content = grid;

        Opened += (_, _) => _ = LoadPageAsync(null);
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

    private async Task LoadPageAsync(string? cursor)
    {
        if (loading || closed) return;
        loading = true;
        next.IsEnabled = false;
        nextCursor = null;
        currentChannels = [];
        rows.ItemsSource = Array.Empty<string>();
        selectedDetail.Text = string.Empty;
        inspect.IsEnabled = false;
        status.Text = "Reading a verified channel page…";
        try
        {
            CancellationToken token = lifetime.Token;
            SessionChannelPage page = await Task.Run(() => SessionChannelQuery.Read(
                SharedSessionStores.Open(path), processScope,
                pageSize: SessionChannelQuery.DefaultPageSize, cursor: cursor,
                cancellationToken: token), token);
            if (closed) return;
            if (page.SessionId != expectedSessionId || page.Generation < expectedGeneration)
            {
                status.Text = "This directory no longer holds the generation the workspace shows. Close this "
                    + "browser and reopen it from the current workspace; no page was shifted.";
                return;
            }

            if (page.RestartRequired)
            {
                // A live capture published between two pages. Channel keys are stable, but a page position is
                // not, so the list starts again rather than shifting; the user is told why.
                loading = false;
                await LoadPageAsync(null);
                if (!closed) status.Text += " · the list restarted because the session published a newer generation";
                return;
            }

            currentChannels = page.Channels;
            rows.ItemsSource = currentChannels.Select(channel =>
                $"{channel.Name} · {channel.ObservationCount:N0} observed records").ToArray();
            rows.SelectedIndex = currentChannels.Count > 0 ? 0 : -1;
            caveat.Text = page.Caveat;
            nextCursor = page.NextCursor;
            next.IsEnabled = nextCursor is not null;
            status.Text = page.TotalChannels == 0
                ? "No admitted paired TCP channel is in this scope. This is not proof of inactivity."
                : $"Generation {page.Generation:N0} · {page.TotalChannels:N0} channels in scope "
                    + $"· {currentChannels.Count:N0} on this page"
                    + (nextCursor is null ? " · end of result" : " · more pages available");
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight read without altering the source session.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) status.Text = "Could not read this channel page: " + exception.Message;
        }
        finally
        {
            loading = false;
            if (!closed) UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        int index = rows.SelectedIndex;
        bool selected = index >= 0 && index < currentChannels.Count;
        inspect.IsEnabled = selected && !loading;
        selectedDetail.Text = selected
            ? $"Stable channel key: {currentChannels[index].Key}\n"
                + $"Observed records: {currentChannels[index].ObservationCount:N0}. "
                + "Direction, byte total and complete capture coverage are not established."
            : string.Empty;
    }

    /// <summary>The channel the user chose, which the workspace opens at its evidence rung.</summary>
    private void ChooseSelected()
    {
        int index = rows.SelectedIndex;
        if (loading || index < 0 || index >= currentChannels.Count) return;
        Close(currentChannels[index]);
    }
}
