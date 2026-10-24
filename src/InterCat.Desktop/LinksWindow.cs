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

namespace InterCat.Desktop;

/// <summary>A link as the links window lists it, from the side of the process it is centred on.</summary>
internal sealed record LinkRow(EvidenceLink Link, LinkEnd Other, string Role, string End, string Evidence, string Strength)
    : IAccessibleRow
{
    public string Title => $"{Role} · {End}";

    public string Detail => $"{Evidence} · {Strength}";

    public string AccessibleName => $"{Role}: {End}. {Evidence}, {Strength}.";

    public override string ToString() => $"{Title} · {Detail}";
}

/// <summary>
/// Causal navigation (`contracts/links-v1.md` §8) in `icat links`'s words: what led to a process and what it led to, each a
/// recorded fact with its evidence and strength; the links the evidence policy does not follow, apart; the processes
/// created near it that nothing links to it, as text, never as a link; and what of it links to nothing. Enter follows a
/// link to the process at its other end, Back returns along the path walked, and Select this process closes the window
/// with the process it is centred on, which the workspace selects; the window itself changes nothing.
/// </summary>
internal sealed class LinksWindow : Window, IDisposable
{
    /// <summary>Why the window's actions wait while the session is read.</summary>
    private const string Reading = "The session's links are being read.";

    /// <summary>Why the window's actions are not offered when no reading could be shown.</summary>
    private const string NoReading = "No links are shown; the status line says why.";

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly EvidencePolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Stack<ProcessInstanceId> walked = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock trail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox ledToIt = new();
    private readonly ListBox itLedTo = new();
    private readonly ListBox notFollowed = new() { MaxHeight = 120 };
    private readonly TextBlock noneLedToIt = new() { Text = "Nothing the evidence policy follows led to it.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock ledToNothing = new() { Text = "It led to nothing the evidence policy follows.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notFollowedNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock near = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock unlinked = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel notFollowedPanel = new() { Spacing = 4 };
    private readonly StackPanel unlinkedPanel = new() { Spacing = 4 };
    private readonly Button back = new() { Content = "Back" };
    private readonly Button follow = new() { Content = "Follow" };
    private readonly Button select = new() { Content = "Select this process" };
    private readonly Button close = new() { Content = "Close" };
    private LinkReading? reading;
    private ProcessInstanceId step;
    private bool loading = true;
    private bool closed;
    private bool disposed;

    /// <summary>Why the window's actions are not offered when no reading could be shown; null while one is.</summary>
    private string? unshown;

    /// <param name="step">The process to list the links of first: the one selected in InterCat's window.</param>
    /// <param name="policy">The evidence policy InterCat's window counts under, so the two follow alike.</param>
    public LinksWindow(string path, Guid expectedSessionId, long expectedGeneration, EvidencePolicy policy, ProcessInstanceId step)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.policy = policy;
        this.step = step;
        Title = "InterCat · Links";
        Width = 1_000;
        Height = 720;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        heading.Text = "Links";
        status.Text = "Reading the session's links…";
        AutomationProperties.SetName(heading, "Whose links these are");
        AutomationProperties.SetName(trail, "The links followed to here");
        AutomationProperties.SetName(status, "Links status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered");
        AutomationProperties.SetName(near, "Created near it in time, linked to it by nothing");
        AutomationProperties.SetName(unlinked, "What of it links to nothing");
        var note = new TextBlock { Text = LinkText.Note, TextWrapping = TextWrapping.Wrap, FontSize = 11 };

        foreach ((ListBox list, string name) in new[]
        {
            (ledToIt, "Led to it. Enter follows the selected link to its process."),
            (itLedTo, "It led to. Enter follows the selected link to its process."),
            (notFollowed, "Links the evidence policy does not follow"),
        })
        {
            // A link reads by what its other end is to the process and that end, then by its evidence and strength, each
            // wrapping within the list rather than running past its edge.
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.ItemTemplate = new FuncDataTemplate<LinkRow>((row, _) => new StackPanel
            {
                Margin = new Thickness(2, 3),
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = row?.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                },
            });
            AccessibleItems.Name(list);
            AutomationProperties.SetName(list, name);
            list.SelectionChanged += (_, _) =>
            {
                // One link is chosen at a time, whichever list holds it, so Follow says what it would follow.
                if (list.SelectedItem is not null)
                {
                    foreach (ListBox other in new[] { ledToIt, itLedTo, notFollowed })
                    {
                        if (other != list) other.SelectedItem = null;
                    }
                }

                Offer();
            };
            list.DoubleTapped += (_, _) => FollowChosen();
            list.KeyDown += (_, key) =>
            {
                if (key.Key != Key.Enter) return;
                FollowChosen();
                key.Handled = true;
            };
        }

        back.Click += (_, _) => GoBack();
        follow.Click += (_, _) => FollowChosen();
        select.Click += (_, _) => SelectStep();
        close.Click += (_, _) => Close();
        // Escape closes the window, as it cancels InterCat's prompts.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        // Alt+Left goes back along the path, as a browser does: taken on its way down, before a list takes Left for itself.
        AddHandler(KeyDownEvent, (_, key) =>
        {
            if (key.Key != Key.Left || key.KeyModifiers != KeyModifiers.Alt) return;
            GoBack();
            key.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
        lists.Children.Add(Column("Led to it", ledToIt, noneLedToIt, 0));
        lists.Children.Add(Column("It led to", itLedTo, ledToNothing, 1));
        notFollowedPanel.Children.Add(Label("Not followed"));
        notFollowedPanel.Children.Add(notFollowedNote);
        notFollowedPanel.Children.Add(notFollowed);
        unlinkedPanel.Children.Add(Label("Not linked"));
        unlinkedPanel.Children.Add(unlinked);
        var beneath = new StackPanel
        {
            Spacing = 8,
            Children = { notFollowedPanel, Label("Near in time"), near, unlinkedPanel, note },
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { back, follow, select, close },
        };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10 };
        var header = new StackPanel { Spacing = 4, Children = { heading, trail, status, coverage } };
        var below = new ScrollViewer { Content = beneath, MaxHeight = 260 };
        Grid.SetRow(header, 0);
        Grid.SetRow(lists, 1);
        Grid.SetRow(below, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(header);
        grid.Children.Add(lists);
        grid.Children.Add(below);
        grid.Children.Add(footer);
        Content = grid;

        Show(null);
        Opened += (_, _) => _ = ReadAsync();
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

    /// <summary>The process the window is centred on now: the one it opened on, or the last one a link was followed to.</summary>
    internal ProcessInstanceId Step => step;

    /// <summary>A column of links under its label, with what it says where it holds none in the list's place.</summary>
    private static DockPanel Column(string label, ListBox list, TextBlock none, int column)
    {
        var panel = new DockPanel();
        TextBlock title = Label(label);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        panel.Children.Add(new Panel { Children = { list, none } });
        Grid.SetColumn(panel, column);
        return panel;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    private async Task ReadAsync()
    {
        try
        {
            CancellationToken token = lifetime.Token;

            // A folder that now holds another session is refused as every read of the workspace's is.
            LinkReading read = await Task.Run(() => EvidenceLinks.Read(
                SharedSessionStores.Open(path, expectedSessionId), policy, token), token);
            if (closed) return;
            if (read.Generation < expectedGeneration)
            {
                unshown = NoReading;
                status.Text = "This folder no longer holds the generation InterCat's window shows. Close this list and open "
                    + "it again from that window.";
                return;
            }

            if (!read.Processes.ContainsKey(step))
            {
                unshown = NoReading;
                status.Text = "The session's current generation holds no such process, so it has no links to list.";
                return;
            }

            reading = read;
            coverage.Text = CoverageText.Describe(read.Coverage);
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
                status.Text = "Could not read the session's links: " + exception.Message;
            }
        }
        finally
        {
            loading = false;
            if (!closed) Show(reading is null ? null : step);
        }
    }

    /// <summary>
    /// Centres the window on <paramref name="centre"/>: its links in each list, the path walked to it, what is near it and
    /// what of it links to nothing; then gives the first link the keyboard, or Close where it has none.
    /// </summary>
    private void Show(ProcessInstanceId? centre)
    {
        if (reading is not { } read || centre is not { } process)
        {
            foreach (ListBox list in new[] { ledToIt, itLedTo, notFollowed }) list.ItemsSource = Array.Empty<LinkRow>();
            noneLedToIt.IsVisible = ledToNothing.IsVisible = notFollowedPanel.IsVisible = unlinkedPanel.IsVisible = false;
            Offer();
            return;
        }

        StepLinks links = EvidenceLinks.Of(read, process);
        CultureInfo culture = CultureInfo.CurrentCulture;
        string named = LinkText.Named(read, LinkEnd.Of(process));
        heading.Text = "Links of " + named;
        trail.Text = walked.Count == 0 ? string.Empty
            : "Followed from " + string.Join(" › ", walked.Reverse().Append(process).Select(each => LinkText.Named(read, LinkEnd.Of(each))));
        trail.IsVisible = walked.Count > 0;
        status.Text = string.Create(culture, $"Generation {read.Generation:N0}")
            + (read.Generation == expectedGeneration ? string.Empty
                : string.Create(culture, $" (InterCat's window shows generation {expectedGeneration:N0})"))
            + " · follows " + WorkspaceLayout.Series([.. EvidenceLinks.Followed(read.Policy).Select(LinkText.Strength)])
            + " links · " + CountText.Of(links.LedToIt.Count, "link", "links") + " led to it, "
            + CountText.Of(links.ItLedTo.Count, "link", "links") + " it led to";
        ledToIt.ItemsSource = Rows(read, links.LedToIt, process);
        itLedTo.ItemsSource = Rows(read, links.ItLedTo, process);
        notFollowed.ItemsSource = Rows(read, links.NotFollowed, process);
        noneLedToIt.IsVisible = links.LedToIt.Count == 0;
        ledToNothing.IsVisible = links.ItLedTo.Count == 0;
        notFollowedPanel.IsVisible = links.NotFollowed.Count > 0;
        notFollowedNote.Text = $"The {CommandArgument.Typed(read.Policy)} evidence policy does not admit these links' strength: each "
            + "is possible, not established, and is never followed.";
        near.Text = !links.CreationWitnessed
            ? "Its creation was not witnessed, so no process is said to have been created near it."
            : links.Near.Count == 0
                ? "No other process was created within a second of it without a link to it."
                : "Created within a second of it, and linked to it by nothing; nearness in time is never a link: "
                    + string.Join("; ", links.Near.Select(each => LinkText.Named(read, LinkEnd.Of(each.Process)) + ", "
                        + LinkText.Near(each, culture))) + ".";
        unlinked.Text = LinkText.Unlinked(links.Unlinked) ?? string.Empty;
        unlinkedPanel.IsVisible = unlinked.Text.Length > 0;
        Offer();
        KeepKeyboard();
    }

    private static LinkRow[] Rows(LinkReading reading, IReadOnlyList<EvidenceLink> links, ProcessInstanceId step) =>
    [
        .. links.Select(link => new LinkRow(link, LinkText.Other(link, step), LinkText.Role(link, step),
            LinkText.Named(reading, LinkText.Other(link, step)), LinkText.Evidence(link, step, CultureInfo.CurrentCulture),
            LinkText.Strength(link.Strength))),
    ];

    /// <summary>The link chosen in whichever list holds it, and whether the policy follows it.</summary>
    private (LinkRow Row, bool Followed)? Chosen =>
        ledToIt.SelectedItem is LinkRow up ? (up, true)
        : itLedTo.SelectedItem is LinkRow down ? (down, true)
        : notFollowed.SelectedItem is LinkRow unfollowed ? (unfollowed, false)
        : null;

    /// <summary>Offers Back, Follow and Select this process, or says on each why not.</summary>
    private void Offer()
    {
        string? none = loading ? Reading : unshown;
        Offering.Offer(back, none ?? (walked.Count == 0 ? "No link has been followed to here yet." : null),
            walked.Count == 0 ? null : "Return to " + LinkText.Named(reading!, LinkEnd.Of(walked.Peek())) + " (Alt+Left)");
        Offering.Offer(follow, none ?? Chosen switch
        {
            null => "Choose a link first.",
            { Followed: false } => "The evidence policy does not admit this link's strength, so it is not followed.",
            { Row.Other.Process: null } => "Its other end is an address, not a process of the capture: nothing is followed "
                + "through it.",
            _ => null,
        }, Chosen is { Row.Other.Process: { } next } ? "List the links of " + LinkText.Named(reading!, LinkEnd.Of(next)) + " (Enter)" : null);
        Offering.Offer(select, none, reading is null ? null
            : "Close this list and select " + LinkText.Named(reading, LinkEnd.Of(step)) + " in InterCat's window");
    }

    /// <summary>Follows the chosen link to the process at its other end, where the policy follows it.</summary>
    private void FollowChosen()
    {
        if (!follow.IsEnabled || Chosen is not { Row.Other.Process: { } next }) return;
        walked.Push(step);
        step = next;
        Show(step);
    }

    /// <summary>Returns along the path walked, to the process the last link was followed from.</summary>
    private void GoBack()
    {
        if (!back.IsEnabled || walked.Count == 0) return;
        step = walked.Pop();
        Show(step);
    }

    /// <summary>Closes the window with the process it is centred on, which InterCat's window selects.</summary>
    private void SelectStep()
    {
        if (!select.IsEnabled) return;
        Close(step);
    }

    /// <summary>
    /// Gives the first link the keyboard - what it led to first, else what led to it - or Close, with none. Posted, once the
    /// rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed) return;
        ListBox? first = itLedTo.ItemCount > 0 ? itLedTo : ledToIt.ItemCount > 0 ? ledToIt : null;
        if (first is not null) first.SelectedIndex = 0;
        (first?.ContainerFromIndex(0) ?? (Control)close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);
}
