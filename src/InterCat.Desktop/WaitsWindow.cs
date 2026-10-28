using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>A wait as the waits window lists it: its rank, caller and interface, then who served its calls and how long they took.</summary>
internal sealed record WaitRow(int Rank, CallWait Wait, string Title, string Took) : IAccessibleRow
{
    public string AccessibleName => $"{Title}: {Took}.";

    public override string ToString() => $"{Title} · {Took}";
}

/// <summary>The wait chosen in the waits window, and its caller and server, which the workspace selects together.</summary>
internal sealed record WaitChoice(CallWait Wait, IReadOnlyList<ProcessInstanceId> Processes);

/// <summary>
/// The time processes spent inside the RPC calls they made (`contracts/waits-v1.md` §7), in `icat waits`'s words: each
/// caller's calls by interface and by the process that served them, the most time first, with the server's own time over
/// the linked calls; the servers callers waited on; and what no time is said for. Opened with a process selected, it lists
/// that process's calls and the calls it served, as `icat waits --process` does, until asked for every caller's. Choosing
/// a wait closes the window with its caller and its server, which the workspace selects together (ADR-065); the window
/// itself changes nothing.
/// </summary>
internal sealed class WaitsWindow : Window, IDisposable
{
    /// <summary>Why the window's actions wait while the session is read.</summary>
    private const string Reading = "The session's calls are being read.";

    /// <summary>Why the window's actions are not offered when no reading could be shown.</summary>
    private const string NoReading = "No waits are shown; the status line says why.";

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly EvidencePolicy policy;
    private readonly ProcessInstanceId? process;
    private readonly TimeRange? interval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox waits = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock served = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock calls = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock took = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock serverRan = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock othersHeading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ListBox others = new();
    private readonly TextBlock notCounted = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly CheckBox only = new();
    private readonly Button choose = new() { Content = "Select caller and server" };
    private readonly Button close = new() { Content = "Close" };
    private WaitReading? reading;
    private WaitRow[] rows = [];
    private bool loading;
    private bool closed;
    private bool disposed;

    /// <summary>Why the window's actions are not offered when no reading could be shown; null while one is.</summary>
    private string? unshown;

    /// <param name="policy">The evidence policy InterCat's window counts under, so the two count alike.</param>
    /// <param name="process">The process selected in InterCat's window, whose calls are listed first; none for every caller's.</param>
    /// <param name="interval">
    /// InterCat's window's time scope, in presentation ticks, whose calls alone - those that began in it - are listed; none for
    /// the whole session.
    /// </param>
    /// <param name="clock">InterCat's window's clock, which says the interval as that window says its time scope.</param>
    public WaitsWindow(string path, Guid expectedSessionId, long expectedGeneration, EvidencePolicy policy, ProcessInstanceId? process,
        TimeRange? interval = null, SessionClock? clock = null)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.policy = policy;
        this.process = process;
        this.interval = interval;
        Title = "InterCat · Time inside calls";
        Width = 1_000;
        Height = 700;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scope = new TextBlock
        {
            Text = interval is { } scoped
                ? "The time each process spent inside the RPC calls it began over the window's time scope, the most first · "
                    + ComparisonText.Interval(scoped, clock, CultureInfo.CurrentCulture)
                : "The time each process spent inside the RPC calls it made, the most first · all session times",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        status.Text = "Reading the session's calls…";
        AutomationProperties.SetName(status, "Waits status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered");
        var note = new TextBlock
        {
            Text = interval is null ? WaitText.Note : WaitText.Note + " " + WaitText.IntervalNote,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
        };
        AutomationProperties.SetName(notCounted, "What no time is said for");

        // A wait reads by its caller and interface, then by who served it and how long its calls took, each wrapping within
        // the list rather than running past its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(waits, ScrollBarVisibility.Disabled);
        waits.ItemTemplate = new FuncDataTemplate<WaitRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Took, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        });
        AccessibleItems.Name(waits);
        AutomationProperties.SetName(waits, "Time inside calls, the most first. Enter selects the chosen wait's caller and server.");
        waits.SelectionChanged += (_, _) => ShowSelected();
        waits.DoubleTapped += (_, _) => ChooseSelected();
        waits.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            ChooseSelected();
            key.Handled = true;
        };

        // The servers callers waited on, or with one process listed the calls it served, a text line's height a row.
        ScrollViewer.SetHorizontalScrollBarVisibility(others, ScrollBarVisibility.Disabled);
        others.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)),
                new Setter(Layoutable.MinHeightProperty, 0d),
            },
        });
        others.ItemTemplate = new FuncDataTemplate<string>((line, _) => new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(served, "Who served its calls");
        only.IsCheckedChanged += (_, _) => List();
        choose.Click += (_, _) => ChooseSelected();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var details = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 6 };
        Control[] parts = [heading, served, calls, took, serverRan, othersHeading, others];
        for (int row = 0; row < parts.Length; row++)
        {
            Grid.SetRow(parts[row], row);
            details.Children.Add(parts[row]);
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,6*"), ColumnSpacing = 14 };
        Grid.SetColumn(details, 1);
        body.Children.Add(waits);
        body.Children.Add(details);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { choose, close },
        };
        var footer = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(only, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(only);
        footer.Children.Add(actions);
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        var header = new StackPanel { Spacing = 5, Children = { scope, status, coverage } };
        var beneath = new StackPanel { Spacing = 4, Children = { note, notCounted } };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(beneath, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(beneath);
        grid.Children.Add(footer);
        Content = grid;

        // Until the session is read, its actions wait for it, saying so; a process selected is listed first.
        only.Content = "Only the selected process's calls";
        only.IsChecked = process is not null;
        loading = true;
        ShowSelected();
        Opened += (_, _) => ReadAsync().Watched();
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

    private async Task ReadAsync()
    {
        try
        {
            CancellationToken token = lifetime.Token;

            // A folder that now holds another session is refused as every read of the workspace's is.
            WaitReading read = await Task.Run(() => CallWaits.Read(SharedSessionStores.Open(path, expectedSessionId), policy, interval,
                token), token);
            if (closed) return;
            if (read.Generation < expectedGeneration)
            {
                unshown = NoReading;
                status.Text = "This folder no longer holds the generation InterCat's window shows. Close this list and open "
                    + "it again from that window.";
                return;
            }

            reading = read;
            coverage.Text = CoverageText.Describe(read.Coverage);
            notCounted.Text = WaitText.NotCounted(read) ?? string.Empty;
            if (process is { } one) only.Content = $"Only {WaitText.Named(read, one)}'s calls and the calls it served";
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight read without altering the session.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                unshown = NoReading;
                status.Text = "Could not read the session's calls: " + exception.Message;
            }
        }
        finally
        {
            loading = false;
            if (!closed)
            {
                List();
                KeepKeyboard();
            }
        }
    }

    /// <summary>Whether the list is one process's: a process was selected, and the list is asked to keep to it.</summary>
    private ProcessInstanceId? Kept => only.IsChecked == true ? process : null;

    /// <summary>
    /// Lists the reading's waits, or one process's where asked, keeping the chosen wait chosen where it is still listed, else
    /// choosing the first; and says how many calls the list counts.
    /// </summary>
    private void List()
    {
        CallWait? chosen = (waits.SelectedItem as WaitRow)?.Wait;
        rows = reading is not { } read ? [] :
        [
            .. read.Waits
                .Where(wait => Kept is not { } one || wait.Caller == one)
                .Select((wait, index) => Row(read, wait, index + 1)),
        ];
        waits.ItemsSource = rows;
        int again = Array.FindIndex(rows, row => row.Wait == chosen);
        waits.SelectedIndex = again >= 0 ? again : rows.Length > 0 ? 0 : -1;
        if (reading is { } shown && unshown is null)
        {
            CallWait[] made = [.. rows.Select(row => row.Wait)];
            int callers = made.Select(wait => wait.Caller).Distinct().Count();
            status.Text = string.Create(CultureInfo.CurrentCulture, $"Generation {shown.Generation:N0}")
                + (shown.Generation == expectedGeneration ? string.Empty
                    : string.Create(CultureInfo.CurrentCulture, $" (InterCat's window shows generation {expectedGeneration:N0}: ")
                        + "a process only the newer one holds is not selected there)")
                + " · " + (Kept is { } one
                    ? CountText.Of(made.Sum(wait => wait.Calls), "completed call", "completed calls") + " it made and "
                        + CountText.Of(shown.Waits.Where(wait => wait.Server == one).Sum(wait => wait.Calls), "call", "calls")
                        + " it served, the most time first"
                    : CountText.Of(made.Sum(wait => wait.Calls), "completed call", "completed calls")
                        + string.Create(CultureInfo.CurrentCulture, $" of {callers:N0} {CountText.Agree(callers, "process", "processes")}, ")
                        + "the most time first")
                + (shown.Policy == EvidencePolicy.IncludeCorrelated ? string.Empty
                    : $" · counted under the {CommandArgument.Typed(shown.Policy)} evidence policy, as InterCat's window counts");
        }

        ShowSelected();
    }

    /// <summary>A wait as the list says it: "1. app.exe · PID 400 → svcctl (Service Control Manager)", then who served it and how long.</summary>
    private static WaitRow Row(WaitReading reading, CallWait wait, int rank)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string ran = WaitText.ServerRan(wait.Linked, wait.Calls, wait.LinkedInCalls, wait.ServerRan, culture);
        return new(rank, wait,
            string.Create(culture, $"{rank:N0}. ") + WaitText.Named(reading, wait.Caller) + " → " + RpcInterfaceNames.Describe(wait.Interface),
            (wait.Server is null ? WaitText.Served(reading, wait) : "served by " + WaitText.Served(reading, wait))
                + " · " + Calls(wait, culture)
                + " · " + OperationText.Duration(wait.InCalls, culture) + " in calls"
                + (ran == "-" ? string.Empty : " · the server ran " + ran));
    }

    /// <summary>A wait's calls and how many failed: "2 calls, 1 failed", "1 call".</summary>
    private static string Calls(CallWait wait, IFormatProvider culture) =>
        CountText.Of(wait.Calls, "call", "calls") + (wait.Failed == 0 ? string.Empty : string.Create(culture, $", {wait.Failed:N0} failed"));

    /// <summary>
    /// Says who served the chosen wait's calls, how many there were and how long they took, beside the servers callers
    /// waited on or the calls the process served; with none chosen, why the list holds none; and offers the window's actions,
    /// or says why not.
    /// </summary>
    private void ShowSelected()
    {
        WaitRow? row = waits.SelectedItem as WaitRow;
        string? none = loading ? Reading : unshown ?? (reading?.Waits.Count == 0
            ? interval is null ? "This session holds no completed call." : "No completed call began in the interval."
            : null);
        Offering.Offer(only, none ?? (process is null ? "No process was selected in InterCat's window, so every caller's calls are listed." : null),
            "List only the selected process's calls and the calls it served, as icat waits --process does");
        ProcessInstanceId[] processes = row is null ? [] : Processes(row.Wait);
        choose.Content = processes.Length == 1 ? "Go to its caller" : "Select caller and server";
        Offering.Offer(choose, none ?? (row is null ? "Choose a wait first." : null),
            processes.Length == 1
                ? "Close this list and select the wait's caller in InterCat's window; no server is named for its calls."
                : "Close this list and select the wait's caller and server together in InterCat's window, as Ctrl+click does; "
                    + "Enter there lists their records.");

        served.IsVisible = calls.IsVisible = took.IsVisible = serverRan.IsVisible = row is not null;
        notCounted.IsVisible = !string.IsNullOrEmpty(notCounted.Text);
        ShowOthers();
        if (row is null || reading is not { } read)
        {
            heading.Text = reading is null ? string.Empty
                : reading.Waits.Count == 0 ? WaitText.None(reading)
                : (interval is null ? "It made no completed call" : "It began no completed call in the interval")
                    + "; unticking the box below lists every caller's.";
            return;
        }

        CallWait wait = row.Wait;
        CultureInfo culture = CultureInfo.CurrentCulture;
        heading.Text = string.Create(culture, $"Wait {row.Rank:N0}: ") + WaitText.Named(read, wait.Caller) + "'s calls to "
            + RpcInterfaceNames.Describe(wait.Interface);
        served.Text = "Served by: " + WaitText.Served(read, wait);
        calls.Text = "Calls: " + Calls(wait, culture);
        took.Text = "In calls: " + OperationText.Duration(wait.InCalls, culture)
            + " · median " + OperationText.Duration(wait.Durations.Median, culture)
            + " · 95th percentile " + OperationText.Duration(wait.Durations.Percentile95, culture)
            + " · longest " + OperationText.Duration(wait.Durations.Maximum, culture);
        string ran = WaitText.ServerRan(wait.Linked, wait.Calls, wait.LinkedInCalls, wait.ServerRan, culture);
        serverRan.Text = ran == "-"
            ? "The server's own time: not measured, as none of these calls is linked to a server call that completed."
            : "The server's own time: " + ran;
    }

    /// <summary>
    /// The servers callers waited on, each over every caller; or, listing one process's calls, the calls it served, each
    /// caller named.
    /// </summary>
    private void ShowOthers()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (reading is not { } read)
        {
            othersHeading.IsVisible = others.IsVisible = false;
            return;
        }

        string[] lines = Kept is { } one
            ? [.. read.Waits.Where(wait => wait.Server == one).Select(wait => WaitText.Named(read, wait.Caller) + " → "
                + RpcInterfaceNames.Describe(wait.Interface) + " · " + Calls(wait, culture) + " · "
                + OperationText.Duration(wait.InCalls, culture) + " in calls · it ran "
                + WaitText.ServerRan(wait.Linked, wait.Calls, wait.LinkedInCalls, wait.ServerRan, culture))]
            : [.. CallWaits.Servers(read).Select(server => WaitText.Named(read, server.Server) + " · "
                + CountText.Of(server.Callers, "caller") + " · " + CountText.Of(server.Calls, "call", "calls") + " · "
                + OperationText.Duration(server.InCalls, culture) + " in calls · it ran "
                + WaitText.ServerRan(server.Linked, server.Calls, server.LinkedInCalls, server.ServerRan, culture))];
        othersHeading.Text = Kept is not null
            ? lines.Length == 0 ? "Calls it served: no linked client call reached it." : "Calls it served"
            : lines.Length == 0 ? "The servers callers waited on: none is named." : "The servers callers waited on";
        othersHeading.IsVisible = true;
        others.IsVisible = lines.Length > 0;
        others.ItemsSource = lines;
        AutomationProperties.SetName(others, Kept is not null ? "Calls it served" : "The servers callers waited on");
    }

    /// <summary>A wait's caller and its server where one is named, each once.</summary>
    private static ProcessInstanceId[] Processes(CallWait wait) =>
        [.. new[] { wait.Caller, wait.Server }.OfType<ProcessInstanceId>().Distinct()];

    /// <summary>
    /// Gives the chosen wait's row the keyboard once the session is read - or Close, with no wait to choose - unless a
    /// control of the window has it. Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (waits.ContainerFromIndex(waits.SelectedIndex) ?? close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>The wait the user chose, whose caller and server the workspace selects.</summary>
    private void ChooseSelected()
    {
        if (!choose.IsEnabled || waits.SelectedItem is not WaitRow row) return;
        Close(new WaitChoice(row.Wait, Processes(row.Wait)));
    }
}
