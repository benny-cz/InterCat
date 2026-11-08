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

/// <summary>A channel epoch as the messages window lists it: its name and epoch, then what it holds.</summary>
internal sealed record MessageChannelRow(CooperativeChannel Channel) : IAccessibleRow
{
    public string Title => CooperativeMessageText.Channel(Channel);

    public string Held => CooperativeMessageText.Counted(Channel);

    public string AccessibleName => $"{Title}: {Held}.";

    public override string ToString() => $"{Title} · {Held}";
}

/// <summary>The channel epoch chosen in the messages window, and the processes that published or read its messages.</summary>
internal sealed record MessageChoice(CooperativeChannel Channel, IReadOnlyList<ProcessInstanceId> Processes);

/// <summary>
/// What applications instrumented with InterCat's SDK said of their channels (`contracts/cooperative-messages-v1.md`), in
/// `icat messages`'s words: each channel epoch by the name its opening gives it, counted by what became of its messages,
/// and the chosen epoch's messages, each with who published it and who read it. Choosing an epoch closes the window with
/// the processes that published or read its messages, which the workspace selects together; the window itself changes
/// nothing. What an application says is its own word, which the window keeps saying.
/// </summary>
internal sealed class MessagesWindow : Window, IDisposable
{
    /// <summary>How many of the chosen epoch's messages the window lists, in sequence order.</summary>
    public const int Listed = 500;

    /// <summary>Why the window's actions wait while the session is read.</summary>
    private const string Reading = "The session's messages are being read.";

    /// <summary>Why the window's actions are not offered when no reading could be shown.</summary>
    private const string NoReading = "No messages are shown; the status line says why.";

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly EvidencePolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox channels = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock identity = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock held = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock lag = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock shown = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox messages = new();
    private readonly TextBlock apart = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button choose = new() { Content = "Select its processes" };
    private readonly Button close = new() { Content = "Close" };
    private CooperativeMessageReading? reading;
    private bool loading;
    private bool closed;
    private bool disposed;

    /// <summary>Why the window's actions are not offered when no reading could be shown; null while one is.</summary>
    private string? unshown;

    /// <param name="policy">The evidence policy InterCat's window counts under, so the two name processes alike.</param>
    public MessagesWindow(string path, Guid expectedSessionId, long expectedGeneration, EvidencePolicy policy)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.policy = policy;
        Title = "InterCat · Logical messages";
        Width = 1_000;
        Height = 700;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scope = new TextBlock
        {
            Text = "What applications instrumented with InterCat's SDK said of their channels, by channel epoch · all session times",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        status.Text = "Reading the session's logical messages…";
        AutomationProperties.SetName(status, "Messages status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered");
        var note = new TextBlock { Text = CooperativeMessageText.Note, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        AutomationProperties.SetName(apart, "What no channel holds");

        // An epoch reads by its channel's name and epoch, then by what it holds, each wrapping within the list rather than
        // running past its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(channels, ScrollBarVisibility.Disabled);
        channels.ItemTemplate = new FuncDataTemplate<MessageChannelRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Held, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        });
        AccessibleItems.Name(channels);
        AutomationProperties.SetName(channels, "Channel epochs. Enter selects the processes that published or read the chosen epoch's messages.");
        channels.SelectionChanged += (_, _) => ShowSelected();
        channels.DoubleTapped += (_, _) => ChooseSelected();
        channels.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            ChooseSelected();
            key.Handled = true;
        };

        // The chosen epoch's messages in sequence order, its rows a text line's height rather than a menu row's.
        ScrollViewer.SetHorizontalScrollBarVisibility(messages, ScrollBarVisibility.Disabled);
        messages.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)),
                new Setter(Layoutable.MinHeightProperty, 0d),
            },
        });
        messages.ItemTemplate = new FuncDataTemplate<string>((message, _) => new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(messages, "The chosen epoch's messages");
        AutomationProperties.SetName(held, "What the chosen epoch holds");
        AutomationProperties.SetName(lag, "How long after their publications the chosen epoch's consumptions were said");
        choose.Click += (_, _) => ChooseSelected();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var details = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 6 };
        Control[] parts = [heading, identity, held, lag, shown, messages];
        for (int row = 0; row < parts.Length; row++)
        {
            Grid.SetRow(parts[row], row);
            details.Children.Add(parts[row]);
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,7*"), ColumnSpacing = 14 };
        Grid.SetColumn(details, 1);
        body.Children.Add(channels);
        body.Children.Add(details);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { choose, close },
        };
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
        Grid.SetRow(actions, 3);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(beneath);
        grid.Children.Add(actions);
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
            CooperativeMessageReading read = await Task.Run(() => CooperativeMessageText.Read(
                SharedSessionStores.Open(path, expectedSessionId), policy, token), token);
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
                + " · " + CountText.Of(read.Messages, "message") + " in " + CountText.Of(read.Index.Channels.Count, "channel epoch")
                + (read.Policy == EvidencePolicy.IncludeCorrelated ? string.Empty
                    : $" · processes named under the {CommandArgument.Typed(read.Policy)} evidence policy, as InterCat's window names them");
            coverage.Text = CoverageText.Describe(read.Coverage);
            apart.Text = read.Index.WithoutMessage == 0 ? string.Empty
                : "In no channel: " + CountText.Of(read.Index.WithoutMessage, "record") + " of the SDK's named no channel or message, "
                    + "a value it is keyed by not kept.";
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
                status.Text = "Could not read the session's logical messages: " + exception.Message;
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

    /// <summary>Lists the reading's channel epochs, choosing the first.</summary>
    private void List()
    {
        MessageChannelRow[] rows = reading is not { } read ? [] : [.. read.Index.Channels.Select(channel => new MessageChannelRow(channel))];
        channels.ItemsSource = rows;
        channels.SelectedIndex = rows.Length > 0 ? 0 : -1;
        ShowSelected();
    }

    /// <summary>
    /// Says what the chosen epoch is and holds and lists its messages, or, with none chosen, why the list holds none; and
    /// offers the window's actions, or says why not.
    /// </summary>
    private void ShowSelected()
    {
        MessageChannelRow? row = channels.SelectedItem as MessageChannelRow;
        string? none = loading ? Reading : unshown
            ?? (reading?.Index.Channels.Count == 0 ? "This session holds no channel or message of the SDK's." : null);
        ProcessInstanceId[] processes = row is null || reading is null ? [] : Processes(reading, row.Channel);
        choose.Content = processes.Length == 1 ? "Go to its process" : "Select its processes";
        Offering.Offer(choose, none
            ?? (row is null ? "Choose a channel epoch first."
                : processes.Length == 0 ? "No process the evidence policy admits published or read this epoch's messages."
                : null),
            processes.Length == 1
                ? "Close this list and select the process that published and read the epoch's messages in InterCat's window."
                : "Close this list and select the processes that published or read the epoch's messages together in InterCat's "
                    + "window, as Ctrl+click does; Enter there lists their records.");

        identity.IsVisible = held.IsVisible = lag.IsVisible = shown.IsVisible = messages.IsVisible = row is not null;
        apart.IsVisible = !string.IsNullOrEmpty(apart.Text);
        if (row is null || reading is not { } read)
        {
            heading.Text = reading is not { } shownReading ? string.Empty
                : shownReading.Index.Channels.Count == 0
                    ? "This session holds no channel or message of the SDK's: no application said one through it while the capture "
                        + "ran, or the capture did not record the SDK; the coverage above says which."
                    : string.Empty;
            messages.ItemsSource = Array.Empty<string>();
            return;
        }

        CooperativeChannel channel = row.Channel;
        heading.Text = CooperativeMessageText.Channel(channel);
        identity.Text = "Channel " + channel.Channel
            + (channel.Kind is { } kind
                ? " · carried through " + CooperativeMessageText.Kind(kind) + ", as its first opening says"
                : " · no opening recorded, so its name and kind are not known")
            + string.Create(CultureInfo.CurrentCulture, $" · opened {channel.Openings:N0} and closed {channel.Closings:N0} times");
        held.Text = CooperativeMessageText.Counted(channel);
        lag.Text = CooperativeMessageText.Lag(channel);
        IReadOnlyList<CooperativeMessage> all = read.Index.MessagesOf(channel);
        string[] listed = [.. all.Take(Listed).Select(message => CooperativeMessageText.Message(read, message))];
        // A run of messages said together in batches is one row, its records saying each of its messages alike.
        string rows = all.Count == channel.Messages ? "messages" : "rows, a run said in batches one row,";
        shown.Text = all.Count == 0 ? "No message of this epoch was recorded."
            : all.Count <= Listed ? $"Its {rows} in sequence order:"
            : string.Create(CultureInfo.CurrentCulture, $"Its first {Listed:N0} of {all.Count:N0} {rows} in sequence order; ")
                + "icat messages --channel lists more.";
        messages.ItemsSource = listed;
    }

    /// <summary>The processes the evidence policy admits that published or read a channel epoch's messages, each once.</summary>
    private static ProcessInstanceId[] Processes(CooperativeMessageReading reading, CooperativeChannel channel) =>
    [
        .. reading.Index.MessagesOf(channel)
            .SelectMany(message => message.Consumptions.Select(consumption => (CooperativeMessageRecord?)consumption.Record)
                .Prepend(message.Publication))
            .OfType<CooperativeMessageRecord>()
            .Where(record => record.Process.IsAdmittedUnder(reading.Policy))
            .Select(record => reading.Index.Processes.Instances[record.Process.Instance].Id)
            .Distinct(),
    ];

    /// <summary>
    /// Gives the chosen epoch's row the keyboard once the session is read - or Close, with no epoch to choose - unless a
    /// control of the window has it. Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (channels.ContainerFromIndex(channels.SelectedIndex) ?? close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>The epoch the user chose, whose processes the workspace selects.</summary>
    private void ChooseSelected()
    {
        if (!choose.IsEnabled || channels.SelectedItem is not MessageChannelRow row || reading is null) return;
        Close(new MessageChoice(row.Channel, Processes(reading, row.Channel)));
    }
}
