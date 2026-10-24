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
    private const string Searching = "A search is running; it stops at its budget, or when this window closes.";

    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Courier New");

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly int? process;
    private readonly SessionClock clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox text = new() { Watermark = "Text to find" };
    private readonly Button find = new() { Content = "Find" };
    private readonly CheckBox ignoreCase = new() { Content = "Letters A to Z in either case" };
    private readonly CheckBox only = new();
    private readonly CheckBox previews = new() { Content = "Show the bytes around each match" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Text = "Type a text and press Find." };
    private readonly ListBox results = new();
    private readonly Button inspect = new() { Content = "Inspect its content" };
    private readonly Button close = new() { Content = "Close" };
    private ContentSearchRow[] rows = [];
    private bool searched;
    private bool searching;
    private bool closed;
    private bool disposed;

    /// <param name="process">The PID of the process selected in InterCat's window, whose records alone may be searched; none for every one's.</param>
    /// <param name="clock">InterCat's window's clock, which says when each record found was raised, as its rows do.</param>
    public ContentSearchWindow(string path, Guid expectedSessionId, int? process, SessionClock clock)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.process = process;
        this.clock = clock;
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
        only.Content = process is { } pid ? string.Create(CultureInfo.CurrentCulture, $"Only the records of PID {pid}") : "Only the selected process's records";
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
        results.DoubleTapped += (_, _) => _ = InspectAsync();
        results.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            _ = InspectAsync();
            key.Handled = true;
        };
        text.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            _ = FindAsync();
            key.Handled = true;
        };
        text.TextChanged += (_, _) => Offer();
        find.Click += (_, _) => _ = FindAsync();
        inspect.Click += (_, _) => _ = InspectAsync();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var query = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(find, 1);
        query.Children.Add(text);
        query.Children.Add(find);
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Children = { ignoreCase, only, previews } };
        foreach (Control option in options.Children)
        {
            option.Margin = new Thickness(0, 0, 18, 0);
        }

        var header = new StackPanel { Spacing = 8, Children = { heading, disclosure, query, options, status } };
        var footer = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { inspect, close } };
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

    /// <summary>Offers Find and Inspect its content, or says why not on each (R15); offered, each says what it does.</summary>
    private void Offer()
    {
        Offering.Offer(find, searching ? Searching : string.IsNullOrEmpty(text.Text) ? "Type the text to find first." : null,
            "Search the kept content for the text");
        Offering.Offer(inspect, searching ? Searching
            : results.SelectedItem is not null ? null
            : !searched ? "Find a text first; a record holding it can then be inspected."
            : rows.Length == 0 ? "No record holding the text is listed." : "Choose a record first.",
            "Open the chosen record's kept content, its bytes hidden until you ask there");
    }

    /// <summary>
    /// Searches the kept content for the text typed, off the window's thread, as the options ask, and lists what it found;
    /// a search already running is let finish, since it stops at its budget.
    /// </summary>
    private async Task FindAsync()
    {
        if (closed || searching || string.IsNullOrEmpty(text.Text)) return;
        var request = new ContentSearchRequest
        {
            Text = text.Text,
            IgnoreCase = ignoreCase.IsChecked == true,
            ProcessId = only.IsChecked == true ? process : null,
            Previews = previews.IsChecked == true,
        };
        searching = true;
        rows = [];
        results.ItemsSource = rows;
        status.Text = "Searching the kept content…";
        Offer();
        try
        {
            CancellationToken token = lifetime.Token;
            ContentSearchResult found = await Task.Run(() => ContentSearch.Find(
                SharedSessionStores.Open(path, expectedSessionId), expectedSessionId, request, token), token);
            if (closed) return;
            CultureInfo culture = CultureInfo.CurrentCulture;
            rows = [.. found.Hits.Select(hit => Row(hit, culture))];
            results.ItemsSource = rows;
            searched = true;
            status.Text = Said(found, culture);
            if (rows.Length > 0)
            {
                results.SelectedIndex = 0;
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
            + string.Create(culture, $", first at byte {hit.FirstAt:N0}");
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
                + "consent to inspect " + CountText.Agree(found.WithoutConsent, "its bytes was", "their bytes were") + " not searched");
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
            new SessionEvidenceRecord(hit.ObservationId, hit.Segment, hit.Row, hit.Observation));
        await viewer.ShowDialog(this);
    }
}
