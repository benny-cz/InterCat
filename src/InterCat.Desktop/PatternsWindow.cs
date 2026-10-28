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

/// <summary>A pattern as the patterns window lists it: its rank and signature in words, then how often and who took part.</summary>
internal sealed record PatternRow(int Rank, CommunicationPattern Pattern, string Took) : IAccessibleRow
{
    public string Title => string.Create(CultureInfo.CurrentCulture, $"{Rank:N0}. ") + CommunicationPatterns.Describe(Pattern);

    public string AccessibleName => $"{Title}: {Took}.";

    public override string ToString() => $"{Title} · {Took}";
}

/// <summary>The pattern chosen in the patterns window, and its processes, which the workspace selects together.</summary>
internal sealed record PatternChoice(CommunicationPattern Pattern, IReadOnlyList<ProcessInstanceId> Processes);

/// <summary>
/// The session's recurring communication patterns (`contracts/patterns-v1.md` §7) in `icat patterns`'s words: each by its
/// structural signature, the heaviest first, with how often it occurred, who took part in each role and what no pattern
/// holds. Choosing a pattern closes the window with its processes, which the workspace selects together (§6.7); the window
/// itself changes nothing.
/// </summary>
internal sealed class PatternsWindow : Window, IDisposable
{
    /// <summary>Why the window's actions wait while the session is read.</summary>
    private const string Reading = "The session's patterns are being read.";

    /// <summary>Why the window's actions are not offered when no reading could be shown.</summary>
    private const string NoReading = "No patterns are shown; the status line says why.";

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly EvidencePolicy policy;
    private readonly TimeRange? interval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox patterns = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock madeOf = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock occurred = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock signature = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock took = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox ends = new();
    private readonly TextBlock hosts = new() { Text = PatternText.HostNote, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock apart = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly CheckBox once = new() { Content = "Include those that occurred once" };
    private readonly Button choose = new() { Content = "Select its processes" };
    private readonly Button close = new() { Content = "Close" };
    private PatternReading? reading;
    private PatternRow[] rows = [];
    private bool loading;
    private bool closed;
    private bool disposed;

    /// <summary>Why the window's actions are not offered when no reading could be shown; null while one is.</summary>
    private string? unshown;

    /// <param name="policy">The evidence policy InterCat's window counts under, so the two count alike.</param>
    /// <param name="interval">
    /// InterCat's window's time scope, in presentation ticks, whose connections, flows and calls alone make the patterns
    /// listed; none for the whole session.
    /// </param>
    /// <param name="clock">InterCat's window's clock, which says the interval as that window says its time scope.</param>
    public PatternsWindow(string path, Guid expectedSessionId, long expectedGeneration, EvidencePolicy policy,
        TimeRange? interval = null, SessionClock? clock = null)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.policy = policy;
        this.interval = interval;
        Title = "InterCat · Recurring patterns";
        Width = 1_000;
        Height = 700;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scope = new TextBlock
        {
            Text = interval is { } scoped
                ? "What recurs in this session's communication over the window's time scope, the heaviest first · "
                    + ComparisonText.Interval(scoped, clock, CultureInfo.CurrentCulture)
                : "What recurs in this session's communication, the heaviest first · all session times",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        status.Text = "Reading the session's recurring patterns…";
        AutomationProperties.SetName(status, "Patterns status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered");
        var note = new TextBlock { Text = PatternText.Note(interval), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        AutomationProperties.SetName(apart, "What no pattern holds");

        // A pattern reads by its signature, then by how often it occurred and who took part, each wrapping within the list
        // rather than running past its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(patterns, ScrollBarVisibility.Disabled);
        patterns.ItemTemplate = new FuncDataTemplate<PatternRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Took, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        });
        AccessibleItems.Name(patterns);
        AutomationProperties.SetName(patterns, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
        patterns.SelectionChanged += (_, _) => ShowSelected();
        patterns.DoubleTapped += (_, _) => ChooseSelected();
        patterns.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            ChooseSelected();
            key.Handled = true;
        };

        // Every end of the chosen pattern, its clients first, in a list that holds as many as took part, its rows a text
        // line's height rather than a menu row's.
        ScrollViewer.SetHorizontalScrollBarVisibility(ends, ScrollBarVisibility.Disabled);
        ends.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)),
                new Setter(Layoutable.MinHeightProperty, 0d),
            },
        });
        ends.ItemTemplate = new FuncDataTemplate<string>((end, _) => new TextBlock { Text = end, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(ends, "Who took part in the chosen pattern");
        AutomationProperties.SetName(took, "Its clients and servers");
        once.IsCheckedChanged += (_, _) => List();
        choose.Click += (_, _) => ChooseSelected();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var details = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 6 };
        Control[] parts = [heading, madeOf, occurred, signature, took, ends, hosts];
        for (int row = 0; row < parts.Length; row++)
        {
            Grid.SetRow(parts[row], row);
            details.Children.Add(parts[row]);
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,6*"), ColumnSpacing = 14 };
        Grid.SetColumn(details, 1);
        body.Children.Add(patterns);
        body.Children.Add(details);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { choose, close },
        };
        var footer = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(once, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(once);
        footer.Children.Add(actions);
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        var header = new StackPanel { Spacing = 5, Children = { scope, status, coverage } };
        var beneath = new StackPanel { Spacing = 4, Children = { note, apart } };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(beneath, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(beneath);
        grid.Children.Add(footer);
        Content = grid;

        // Until the session is read, its actions wait for it, saying so.
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
            PatternReading read = await Task.Run(() => CommunicationPatterns.Read(
                SharedSessionStores.Open(path, expectedSessionId), policy, interval, token), token);
            if (closed) return;
            if (read.Generation < expectedGeneration)
            {
                unshown = NoReading;
                status.Text = "This folder no longer holds the generation InterCat's window shows. Close this list and open "
                    + "it again from that window.";
                return;
            }

            reading = read;
            status.Text = string.Create(CultureInfo.CurrentCulture, $"Generation {read.Generation:N0}")
                + (read.Generation == expectedGeneration ? string.Empty
                    : string.Create(CultureInfo.CurrentCulture, $" (InterCat's window shows generation {expectedGeneration:N0}: ")
                        + "a process only the newer one holds is not selected there)")
                + " · " + PatternText.Counted(read)
                + (read.Policy == EvidencePolicy.IncludeCorrelated ? string.Empty
                    : $" · counted under the {CommandArgument.Typed(read.Policy)} evidence policy, as InterCat's window counts");
            coverage.Text = CoverageText.Describe(read.Coverage);
            apart.Text = PatternText.Apart(read, CultureInfo.CurrentCulture);
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
                status.Text = "Could not read the session's patterns: " + exception.Message;
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

    /// <summary>
    /// Lists the reading's patterns, those that occurred once too where asked, keeping the chosen pattern chosen where it is
    /// still listed, else choosing the first.
    /// </summary>
    private void List()
    {
        string? chosen = (patterns.SelectedItem as PatternRow)?.Pattern.Key;
        rows = reading is not { } read ? [] :
        [
            .. read.Patterns
                .Select((pattern, index) => new PatternRow(index + 1, pattern, Took(read, pattern)))
                .Where(row => row.Pattern.Recurring || once.IsChecked == true),
        ];
        patterns.ItemsSource = rows;
        int again = Array.FindIndex(rows, row => row.Pattern.Key == chosen);
        patterns.SelectedIndex = again >= 0 ? again : rows.Length > 0 ? 0 : -1;
        ShowSelected();
    }

    /// <summary>How often a pattern occurred and who took part, as the list says it beneath the pattern.</summary>
    private static string Took(PatternReading reading, CommunicationPattern pattern) =>
        PatternText.Occurred(pattern, CultureInfo.CurrentCulture)
        + " · clients: " + PatternText.Took(reading, pattern, PatternRole.Client)
        + " · servers: " + PatternText.Took(reading, pattern, PatternRole.Server)
        + string.Create(CultureInfo.CurrentCulture, $" · in {pattern.Tenths:N0} of {CommunicationPatterns.Parts:N0} tenths");

    /// <summary>
    /// Says what the chosen pattern is made of, when it occurred and every end that took part, or, with none chosen, why the
    /// list holds none; and offers the window's actions, or says why not.
    /// </summary>
    private void ShowSelected()
    {
        PatternRow? row = patterns.SelectedItem as PatternRow;
        string? none = loading ? Reading : unshown
            ?? (reading?.Patterns.Count == 0 ? (interval is null ? "This session" : "The interval") + " holds no pattern." : null);
        bool anyOnce = reading?.Patterns.Any(pattern => !pattern.Recurring) == true;
        Offering.Offer(once,
            none ?? (anyOnce ? null : "Every pattern in " + (interval is null ? "this session" : "the interval")
                + " occurred more than once, so the list holds them all."),
            "List the patterns that occurred only once as well, after those that recur");
        ProcessInstanceId[] processes = row is null ? [] : Processes(row.Pattern);
        choose.Content = processes.Length == 1 ? "Go to its process" : "Select its processes";
        Offering.Offer(choose, none
            ?? (row is null ? "Choose a pattern first."
                : processes.Length == 0 ? "No process of this machine took part in this pattern; its ends are other hosts."
                : null),
            processes.Length == 1
                ? "Close this list and select the pattern's process in InterCat's window."
                : "Close this list and select the pattern's processes together in InterCat's window, as Ctrl+click does; Enter "
                    + "there lists their records.");

        madeOf.IsVisible = occurred.IsVisible = signature.IsVisible = took.IsVisible = ends.IsVisible = row is not null;
        hosts.IsVisible = false;
        apart.IsVisible = !string.IsNullOrEmpty(apart.Text);
        if (row is null || reading is not { } read)
        {
            heading.Text = reading is not { } shown ? string.Empty
                : shown.Patterns.Count == 0
                    ? PatternText.None(shown)
                    : "No pattern occurred more than once; Include those that occurred once lists those that did.";
            ends.ItemsSource = Array.Empty<string>();
            return;
        }

        CommunicationPattern pattern = row.Pattern;
        heading.Text = string.Create(CultureInfo.CurrentCulture, $"Pattern {row.Rank:N0}: ") + CommunicationPatterns.Describe(pattern);
        madeOf.Text = "Made of: " + CommunicationPatterns.Explain(pattern);
        occurred.Text = "Occurred: " + PatternText.When(read, pattern, CultureInfo.CurrentCulture);
        signature.Text = "Signature: " + pattern.Key;
        took.Text = "Clients: " + PatternText.Took(read, pattern, PatternRole.Client)
            + " · servers: " + PatternText.Took(read, pattern, PatternRole.Server);
        ends.ItemsSource = pattern.Clients.Select(end => End(read, pattern, end, "Client"))
            .Concat(pattern.Servers.Select(end => End(read, pattern, end, "Server")))
            .ToArray();
        hosts.IsVisible = pattern.Clients.Concat(pattern.Servers).Any(end => end.Address is not null);
    }

    /// <summary>
    /// One end as the list of who took part says it: "Client · browser.exe · PID 3000 · 2 connections", its program named
    /// where the process's own name does not say it, or that no record witnessed its executable.
    /// </summary>
    private static string End(PatternReading reading, CommunicationPattern pattern, PatternParticipant end, string role)
    {
        ProcessNode? node = end.Process is { } id && reading.Processes.TryGetValue(id, out ProcessNode? found) ? found : null;
        string? program = PatternText.Program(reading, end);
        return $"{role} · {PatternText.Named(reading, end)}"
            + (node?.GroupKey == ProcessGroup.UnwitnessedExecutableKey ? " · executable not witnessed"
                : program is not null && !string.Equals(program, node?.Name, StringComparison.Ordinal) ? $" · program {program}"
                : string.Empty)
            + " · " + PatternText.Count(pattern, end.Role, end.Occurrences);
    }

    /// <summary>The processes that took part in a pattern, in either role, each once.</summary>
    private static ProcessInstanceId[] Processes(CommunicationPattern pattern) =>
        [.. pattern.Clients.Concat(pattern.Servers).Select(end => end.Process).OfType<ProcessInstanceId>().Distinct()];

    /// <summary>
    /// Gives the chosen pattern's row the keyboard once the session is read - or Close, with no pattern to choose - unless a
    /// control of the window has it. Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (patterns.ContainerFromIndex(patterns.SelectedIndex) ?? close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>The pattern the user chose, whose processes the workspace selects.</summary>
    private void ChooseSelected()
    {
        if (!choose.IsEnabled || patterns.SelectedItem is not PatternRow row) return;
        Close(new PatternChoice(row.Pattern, Processes(row.Pattern)));
    }
}
