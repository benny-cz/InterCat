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
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>A record the search found, as the window lists it: when, whose and what it is, then how often it holds the text.</summary>
internal sealed record ContentSearchRow(ContentSearchHit Hit, string Title, string Detail, string? Preview) : IAccessibleRow
{
    public string AccessibleName => Preview is null
        ? $"{Title}: {Detail}."
        : $"{Title}: {Detail}. Around its first match: {Preview}";

    public override string ToString() => $"{Title} · {Detail}";
}

/// <summary>
/// Finds a text in the content a session kept, only when the person asks (`contracts/content-v1.md` §4), as
/// `icat content --find` does: only the bytes kept with consent to inspect them, each record's on its own, within its
/// budgets, saying what it did not search; the records holding the text are listed, in reading order, without a byte of
/// them unless the person asks to see the bytes around each match, and then as inert text. A record found opens in the
/// content viewer, whose bytes stay hidden until asked there. Nothing found is kept.
/// </summary>
internal sealed class ContentSearchWindow : Window, IDisposable
{
    /// <summary>Why the window's actions wait while a search runs.</summary>
    private const string Searching = "A search is running; it stops at its budget, or when you stop it, keeping what it found.";

    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Courier New");

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly ProcessNode? process;
    private readonly SessionClock clock;
    private readonly EvidencePolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox text = new() { Watermark = "Text to find" };
    private readonly Button find = new() { Content = "Find" };
    private readonly Button stop = new() { Content = "Stop" };
    private readonly Button searchOn = new() { Content = "Search on" };
    private readonly CheckBox ignoreCase = new() { Content = "Letters A to Z in either case" };
    private readonly CheckBox only = new();
    private readonly CheckBox previews = new() { Content = "Show the bytes around each match" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Text = "Type a text and press Find." };
    private readonly ListBox results = new();
    private readonly Button inspect = new() { Content = "Inspect its content" };
    private readonly Button export = new() { Content = "Export…" };
    private readonly Button close = new() { Content = "Close" };
    private ContentSearchRow[] rows = [];
    private CancellationTokenSource? stopping;
    private bool searched;
    private bool searching;
    private bool exporting;
    private ContentSearchResult? found;
    private ContentSearchRequest? asked;
    private bool closed;
    private bool disposed;

    /// <param name="process">
    /// The process selected in InterCat's window, whose records alone may be searched - those bound to it under
    /// <paramref name="policy"/>, never every record of a PID another process may have held too; none for every one's.
    /// </param>
    /// <param name="clock">InterCat's window's clock, which says when each record found was raised, as its rows do.</param>
    /// <param name="policy">
    /// The evidence policy InterCat's window counts under, which a record found binds to the selected process and opens
    /// its content under.
    /// </param>
    public ContentSearchWindow(
        string path, Guid expectedSessionId, ProcessNode? process, SessionClock clock, EvidencePolicy policy = EvidencePolicy.IncludeCorrelated)
    {
        this.policy = policy;
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.process = process;
        this.clock = clock;
        Choices = new PathChoices(this);
        Title = "InterCat · Find in kept content";
        Width = 920;
        Height = 660;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = "Find a text in the content this session kept",
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        };
        var disclosure = new TextBlock
        {
            Text = "Message bytes may be sensitive. Only bytes kept with consent to inspect them are searched, each record's "
                + "on its own, within 256 MiB and 10 seconds. The records holding the text are listed without a byte of them "
                + "unless you ask to see the bytes around each match, and nothing found is kept.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        var exactness = new TextBlock { Text = ContentSearch.Exactness, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(text, "Text to find");
        AutomationProperties.SetName(status, "Search status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        only.Content = process is { } selected ? "Only the records of " + selected.NameWithPid : "Only the selected process's records";
        Offering.Offer(only, process is null ? "Select a process in InterCat's window first: its records are then searched alone." : null,
            "Search only the records of the process selected in InterCat's window");
        Offering.Offer(previews, null, "Show, beside each record found, the kept bytes around its first match, as inert text");
        Offering.Offer(ignoreCase, null, "Match each letter A to Z in either case; every other character matches only itself");

        // A record found reads by when, whose and what it is, then how often its bytes hold the text and, when asked, the
        // bytes around its first match, which wrap within the list rather than running past its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        results.ItemTemplate = new FuncDataTemplate<ContentSearchRow>((row, _) =>
        {
            var lines = new StackPanel
            {
                Margin = new Thickness(2, 4),
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = row?.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                },
            };
            if (row?.Preview is { } preview)
            {
                lines.Children.Add(new TextBlock { Text = preview, FontFamily = Monospace, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }

            return lines;
        });
        AccessibleItems.Name(results);
        AutomationProperties.SetName(results, "Records holding the text, in reading order. Enter inspects the chosen one's content.");
        results.SelectionChanged += (_, _) => Offer();
        results.DoubleTapped += (_, _) => InspectAsync().Watched();
        results.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            InspectAsync().Watched();
            key.Handled = true;
        };
        text.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            FindAsync().Watched();
            key.Handled = true;
        };
        text.TextChanged += (_, _) => Offer();
        find.Click += (_, _) => FindAsync().Watched();
        stop.Click += (_, _) => stopping?.Cancel();
        searchOn.Click += (_, _) => SearchOnAsync().Watched();
        inspect.Click += (_, _) => InspectAsync().Watched();
        export.Click += (_, _) => ExportAsync().Watched();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var query = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(find, 1);
        Grid.SetColumn(stop, 2);
        Grid.SetColumn(searchOn, 3);
        query.Children.Add(text);
        query.Children.Add(find);
        query.Children.Add(stop);
        query.Children.Add(searchOn);
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Children = { ignoreCase, only, previews } };
        foreach (Control option in options.Children)
        {
            option.Margin = new Thickness(0, 0, 18, 0);
        }

        var header = new StackPanel { Spacing = 8, Children = { heading, disclosure, query, options, status } };
        var footer = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { inspect, export, close } };
        DockPanel.SetDock(actions, Dock.Right);
        exactness.VerticalAlignment = VerticalAlignment.Center;
        exactness.Margin = new Thickness(0, 0, 16, 0);
        footer.Children.Add(actions);
        footer.Children.Add(exactness);
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        Grid.SetRow(header, 0);
        Grid.SetRow(results, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(results);
        grid.Children.Add(footer);
        Content = grid;
        Offer();

        Opened += (_, _) => text.Focus(NavigationMethod.Tab);
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

    /// <summary>Picks the file a table of the records found is written to; a test answers it as a person would.</summary>
    internal PathChoices Choices { get; set; }

    /// <summary>Offers Find, Stop, Inspect its content and Export, or says why not on each (R15); offered, each says what it does.</summary>
    private void Offer()
    {
        Offering.Offer(find, searching ? Searching : string.IsNullOrEmpty(text.Text) ? "Type the text to find first." : null,
            "Search the kept content for the text");
        Offering.Offer(stop, searching ? null : "No search is running.", "Stop the search before the next record, keeping what it has found");
        Offering.Offer(searchOn, searching ? Searching
            : found is null ? "Find a text first; a search that ends before reading every record can then search on."
            : found.Continuation is null ? "The search read every record it could, so there is nothing to search on." : null,
            "Read on from where the search ended, adding what it finds after the records listed");
        Offering.Offer(inspect, searching ? Searching
            : results.SelectedItem is not null ? null
            : !searched ? "Find a text first; a record holding it can then be inspected."
            : rows.Length == 0 ? "No record holding the text is listed." : "Choose a record first.",
            "Open the chosen record's kept content, its bytes hidden until you ask there");
        Offering.Offer(export, searching ? Searching
            : found is null ? "Find a text first; the records found can then be exported."
            : rows.Length == 0 ? "No record holding the text is listed."
            : exporting ? "The records found are being exported." : null,
            "Write the records listed to a CSV table"
                + (asked?.Previews == true ? ", with the bytes around each first match, which makes it content" : string.Empty));
    }

    /// <summary>
    /// Writes the records listed to a table the person chooses (`contracts/content-v1.md` §4): with the bytes around each
    /// first match only where the search showed them, and then only once the window has said the table is content.
    /// </summary>
    private async Task ExportAsync()
    {
        if (exporting || closed || searching || found is not { Hits.Count: > 0 } listed || asked is not { } request) return;
        exporting = true;
        Offer();
        try
        {
            if (request.Previews && (!await PreviewsPrompt().ShowDialog<bool>(this) || closed)) return;
            string? destination = await Choices.SaveFileAsync(new()
            {
                Title = "Export the records found",
                SuggestedFileName = "kept-content-search.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [new("CSV, one line per record") { Patterns = ["*.csv"] }],
            });
            if (destination is null || closed) return;
            await ExportFileWriter.WriteAsync(destination, ContentSearchTable.Csv(listed, request), overwrite: true);
            if (!closed)
            {
                status.Text = $"Exported the {CountText.Of(listed.Hits.Count, "record")} listed to {destination}"
                    + (request.Previews
                        ? ", each with the bytes around its first match. It holds content: share it only as you would that content."
                        : ".");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) status.Text = "Could not export the records found: " + exception.Message;
        }
        finally
        {
            exporting = false;
            if (!closed) Offer();
        }
    }

    /// <summary>What a table of the records found with their previews holds, asked before its file is chosen; true to choose it.</summary>
    internal static Window PreviewsPrompt()
    {
        var prompt = new Window
        {
            Title = "Export the records with the bytes around each match?", Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var cancel = new Button { Content = "Cancel" };
        var proceed = new Button { Content = "Choose table file" };
        cancel.Click += (_, _) => prompt.Close(false);
        proceed.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => cancel.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            prompt.Close(false);
            key.Handled = true;
        };
        prompt.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = PreviewsHold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = PreviewsAreContent, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, proceed } },
            },
        };
        return prompt;
    }

    /// <summary>What a table of the records found with their previews holds, as its prompt says it first.</summary>
    internal const string PreviewsHold = "This writes a table of the records listed, one line each, with the bytes around "
        + "each first match as the search showed them. A value a spreadsheet would evaluate begins with an apostrophe.";

    /// <summary>Why the table is content, as its prompt says it.</summary>
    internal const string PreviewsAreContent = "Those bytes are read from the content this session kept with consent to "
        + "inspect it, so the table is content: share it only as you would that content.";

    /// <summary>
    /// Searches the kept content for the text typed, off the window's thread, as the options ask, and lists what it found;
    /// a search already running is let finish, since it stops at its budget, or when Stop stops it.
    /// </summary>
    private Task FindAsync()
    {
        if (closed || searching || string.IsNullOrEmpty(text.Text)) return Task.CompletedTask;
        return SearchAsync(new ContentSearchRequest
        {
            Text = text.Text,
            IgnoreCase = ignoreCase.IsChecked == true,
            ProcessInstance = only.IsChecked == true ? process?.Id : null,
            Policy = policy,
            Previews = previews.IsChecked == true,
        }, null);
    }

    /// <summary>
    /// Reads on from where the last search ended, as it was asked, and adds what it finds after the records listed: the list
    /// and what the window says are then the whole search's so far.
    /// </summary>
    private Task SearchOnAsync() =>
        closed || searching || found?.Continuation is not { } from ? Task.CompletedTask : SearchAsync(from.Request, from);

    /// <summary>
    /// Searches as <paramref name="request"/> asks, or reads on from <paramref name="from"/>, off the window's thread, and
    /// lists what it found: alone for a new search, after the records listed for one read on.
    /// </summary>
    private async Task SearchAsync(ContentSearchRequest request, ContentSearchContinuation? from)
    {
        ContentSearchResult? earlier = from is null ? null : found;
        searching = true;
        if (earlier is null)
        {
            rows = [];
            found = null;
            asked = request;
            results.ItemsSource = rows;
        }

        status.Text = earlier is null ? "Searching the kept content…" : "Searching on from where the search ended…";
        using var stopped = new CancellationTokenSource();
        stopping = stopped;
        Offer();
        try
        {
            CancellationToken token = lifetime.Token;
            CancellationToken stop = stopped.Token;
            ContentSearchResult result = await Task.Run(() => from is null
                ? ContentSearch.Find(SharedSessionStores.Open(path, expectedSessionId), expectedSessionId, request, token, stop)
                : ContentSearch.Continue(SharedSessionStores.Open(path, expectedSessionId), expectedSessionId, from, token, stop), token);
            if (closed) return;
            CultureInfo culture = CultureInfo.CurrentCulture;
            int listed = rows.Length;
            found = earlier is null ? result : earlier.Then(result);
            rows = [.. rows, .. result.Hits.Select(hit => Row(hit, culture))];
            results.ItemsSource = rows;
            searched = true;
            status.Text = Said(found, culture);
            if (rows.Length > listed)
            {
                results.SelectedIndex = listed;
            }
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels a search in flight; nothing found was kept.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) status.Text = "Could not search the kept content: " + exception.Message;
        }
        finally
        {
            searching = false;
            stopping = null;
            if (!closed)
            {
                Offer();
                KeepKeyboard();
            }
        }
    }

    /// <summary>A record found as the list says it: when it was raised and by whom, then how often it holds the text and where.</summary>
    private ContentSearchRow Row(ContentSearchHit hit, CultureInfo culture)
    {
        ObservationRowV1 observation = hit.Observation;
        string title = clock.Record(observation.SessionRelativeTicks, culture) + " · "
            + string.Create(culture, $"PID {(observation.OwnerProcessId ?? observation.HeaderProcessId).ToString(CultureInfo.InvariantCulture)}")
            + " · " + MechanismText.Name(observation.Mechanism)
            + string.Create(CultureInfo.InvariantCulture, $" event {observation.EventId}");
        string detail = (hit.Matches == 1 ? "Holds it once" : "Holds it " + CountText.Of(hit.Matches, "time") + ", none overlapping")
            + (hit.FirstAt >= 0 ? ", first at " : hit.Matches == 1 ? ", beginning " : ", the first beginning ")
            + ContentSearch.FirstAtText(hit, culture);
        return new(hit, title, detail, hit.Preview is { } preview ? preview.Before + "⟦" + preview.Match + "⟧" + preview.After : null);
    }

    /// <summary>What the search found and read, and what it did not, in the words `icat content --find` uses.</summary>
    private static string Said(ContentSearchResult found, CultureInfo culture)
    {
        if (found.NoneKept is { } none)
        {
            return none;
        }

        string held = found.Hits.Count == 0 ? "No record holds it"
            : found.More ? "More than " + CountText.Of(found.Hits.Count, "record") + " hold it; the first "
                + found.Hits.Count.ToString("N0", culture) + " are listed"
            : CountText.Of(found.Hits.Count, "record") + " " + CountText.Agree(found.Hits.Count, "holds", "hold") + " it";
        string read = "Searched the kept bytes of " + CountText.Of(found.RecordsRead, "record")
            + string.Create(culture, $" ({found.BytesRead:N0} B)")
            + (found.WithoutConsent == 0 ? string.Empty : "; " + CountText.Of(found.WithoutConsent, "record") + " kept without "
                + "consent to inspect " + CountText.Agree(found.WithoutConsent, "its bytes was", "their bytes were") + " not searched")
            + (found.Omitted == 0 ? string.Empty : "; " + CountText.Of(found.Omitted, "record") + " the capture kept no byte of, "
                + "having reached its content limit, " + CountText.Agree(found.Omitted, "was", "were") + " not searched")
            + (found.NotAdmitted == 0 ? string.Empty : "; " + CountText.Of(found.NotAdmitted, "record") + " bound to the process "
                + "more weakly than the window's evidence policy admits " + CountText.Agree(found.NotAdmitted, "was", "were")
                + " not searched");
        return held + ". " + read + "." + (found.Unreadable is { } unreadable ? " " + unreadable : string.Empty)
            + (found.Stopped is { } stopped ? " " + stopped : string.Empty);
    }

    /// <summary>
    /// Gives the keyboard to the first record found once a search has listed any, else back to the text, unless a control
    /// of the window has it. Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed) return;
        if (rows.Length > 0 && results.ContainerFromIndex(results.SelectedIndex) is Control row)
        {
            row.Focus(NavigationMethod.Tab);
            return;
        }

        if (FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused) && focused is not Button) return;
        text.Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>Opens the chosen record's kept content in the content viewer, whose bytes stay hidden until asked there.</summary>
    private async Task InspectAsync()
    {
        if (closed || !inspect.IsEnabled || results.SelectedItem is not ContentSearchRow row) return;
        ContentSearchHit hit = row.Hit;
        using var viewer = new SessionContentWindow(path, expectedSessionId,
            new SessionEvidenceRecord(hit.ObservationId, hit.Segment, hit.Row, hit.Observation), policy, clock);
        await viewer.ShowDialog(this);
    }
}
