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
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
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
    private readonly Button close = new() { Content = "Close" };
    private readonly Func<ProcessInstanceId, string?> processName;
    private IReadOnlyList<Channel> currentChannels = [];
    private string? nextCursor;
    private bool loading;
    private bool closed;
    private bool disposed;

    /// <param name="processName">
    /// Names a process instance as the workspace names it, by name and PID, so a channel reads by the processes it joins
    /// rather than by endpoints alone; null for one the workspace does not hold.
    /// </param>
    public SessionChannelWindow(string path, Guid expectedSessionId, long expectedGeneration,
        ProcessInstanceId? processScope, Func<ProcessInstanceId, string?>? processName = null)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.processScope = processScope;
        this.processName = processName ?? (_ => null);
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

        // A channel reads by the processes it joins, whose names can be long: a row wraps within the list rather than
        // running past its edge, where the end of a name and the channel's records were cut off.
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        rows.ItemTemplate = new FuncDataTemplate<string>((row, _) => new TextBlock { Text = row, TextWrapping = TextWrapping.Wrap });

        // Enter opens the selected channel, as it opens a ranked row in the workspace; a double click does too.
        AutomationProperties.SetName(rows, "Paired TCP channels. Enter shows the selected channel's source records.");
        AutomationProperties.SetName(status, "Channels status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        rows.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                ChooseSelected();
                key.Handled = true;
            }
        };
        next.Click += (_, _) =>
        {
            if (nextCursor is { } cursor) _ = LoadPageAsync(cursor);
        };
        inspect.Click += (_, _) => ChooseSelected();
        rows.DoubleTapped += (_, _) => ChooseSelected();
        close.Click += (_, _) => Close();
        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };
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
            rows.ItemsSource = currentChannels.Select(Describe).ToArray();
            rows.SelectedIndex = currentChannels.Count > 0 ? 0 : -1;
            nextCursor = page.NextCursor;
            next.IsEnabled = nextCursor is not null;

            // What the capture covered of TCP is said once (R21): as the reason an empty list holds none, where it is
            // announced, and beside the caveat under a list of channels.
            string coverage = SessionCoverage.Sentence(page.Coverage[0], "the session");
            caveat.Text = page.TotalChannels == 0 ? page.Caveat : page.Caveat + " " + coverage;
            status.Text = page.TotalChannels == 0
                ? "No admitted paired TCP channel is in this scope. " + coverage
                : $"Generation {page.Generation:N0} · {Spoken.Count(page.TotalChannels, "channel")} in scope "
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
            if (!closed)
            {
                UpdateSelection();
                KeepKeyboard();
            }
        }
    }

    /// <summary>
    /// Gives the chosen channel's row the keyboard once a page is read - or Close, with no channel to choose - unless a
    /// control of the window has it: the browser opened with it on nothing of its own, and Next, which waits disabled
    /// while the next page is read, let it go, so Up, Down and Enter did nothing until Tab found the list. Posted, once the
    /// rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (rows.ContainerFromIndex(rows.SelectedIndex) ?? close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    private void UpdateSelection()
    {
        int index = rows.SelectedIndex;
        bool selected = index >= 0 && index < currentChannels.Count;
        inspect.IsEnabled = selected && !loading;
        selectedDetail.Text = selected
            ? $"{currentChannels[index].Name} · {currentChannels[index].ObservationCount:N0} records at its two ends\n"
                + "Enter shows its source records. Its channel rung, one step below either process's, states what was "
                + $"sent across it and each end's records by direction.\nStable channel key: {currentChannels[index].Key}"
            : string.Empty;
    }

    /// <summary>
    /// A channel as the list reads it: the processes at its two ends, first end first, then its endpoints compacted and
    /// its records - "queue.exe · PID 29632 ↔ worker.exe · PID 66820 · :2711 ↔ :2756 on 127.0.0.1 · 128 records".
    /// </summary>
    private string Describe(Channel channel)
    {
        string first = channel.FirstHolder is { } firstHolder ? processName(firstHolder) ?? "an unlisted process" : "an unknown process";
        string second = channel.SecondHolder is { } secondHolder ? processName(secondHolder) ?? "an unlisted process" : "an unknown process";
        return string.Create(CultureInfo.CurrentCulture,
            $"{first} ↔ {second} · {ChannelNames.Compact(channel.Name)} · {Spoken.Count(channel.ObservationCount, "record")}");
    }

    /// <summary>The channel the user chose, which the workspace opens at its evidence rung.</summary>
    private void ChooseSelected()
    {
        int index = rows.SelectedIndex;
        if (loading || index < 0 || index >= currentChannels.Count) return;
        Close(currentChannels[index]);
    }
}
