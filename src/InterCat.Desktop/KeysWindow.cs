using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// One key a window takes: where it acts, the key or keys, and what it does (§6.7). The first row of each place heads it,
/// and says the place as a screen reader enters it.
/// </summary>
internal sealed record WindowKey(string Where, string Keys, string Does) : IAccessibleRow
{
    /// <summary>The keys in words, where the sheet shows symbols a screen reader would spell: "left or right bracket".</summary>
    public string? SpokenKeys { get; init; }

    /// <summary>Whether the row is the first of its place, under that place's heading.</summary>
    public bool Heads { get; init; }

    /// <summary>The place's heading, as the window's other headings are set: in capitals.</summary>
    public string Heading => Where.ToUpperInvariant();

    /// <summary>What the keys do as the sheet's column shows it, a sentence of its own: "List the keys this window takes".</summary>
    public string Shown => string.Concat(Does[..1].ToUpperInvariant(), Does[1..]);

    public string AccessibleName => (Heads ? Where + ". " : string.Empty) + $"{SpokenKeys ?? Keys}: {Does}.";
}

/// <summary>
/// Every key InterCat's window takes, by where it acts (§6.7, R15), as the sheet F1 opens lists them: a key no person can
/// find is a key nobody uses. Each also has a button, a menu item or a pointer gesture.
/// </summary>
internal static class WindowKeys
{
    /// <summary>What the sheet says above the window's keys.</summary>
    public const string Intro = "Every key this window takes, by where it acts. Each has a button, a menu item or a pointer "
        + "gesture as well; Tab reaches every control, and F6 moves the keyboard from pane to pane.";

    public static IReadOnlyList<WindowKey> All { get; } = Headed(
    [
        new("Anywhere", "F1", "list the keys this window takes"),
        new("Anywhere", "F6 or Shift+F6", "move the keyboard to the next or previous pane: the ranked table, the graph, "
            + "the timeline and the inspector, or the tables while they are shown"),
        new("Anywhere", "Tab or Shift+Tab", "move through the window's controls in reading order"),
        new("Anywhere", "Ctrl+F", "search for a group, process, channel or lane, or type a time to go to it"),
        new("Anywhere", "Enter", "open the chosen row's level"),
        new("Anywhere", "E", "list the records behind what is chosen, within the analysis interval"),
        new("Anywhere", "Esc", "go back a level to where you were, and at the top clear the selection; during a drag, "
            + "cancel it"),
        new("Anywhere", "Alt+Left or Alt+Right", "go back or forward through the levels visited"),
        new("Anywhere", "T", "show or hide the relationship and interval tables"),
        new("Anywhere", "F11", "let the graph or the timeline, whichever has the keyboard, fill the column, or give the "
            + "column back"),
        new("Anywhere", "M", "load the next page of records, calls or exchanges"),
        new("Anywhere", "L", "lay the graph out afresh, keeping pinned nodes where they are"),
        new("Anywhere", "Delete", "remove the chosen filter"),
        new("Anywhere", "Ctrl+E", "export the view shown"),
        new("Anywhere", "Ctrl+R", "start the capture chosen, as Start exploring does"),
        new("Anywhere", "F", "pause the live view, or follow it again"),
        new("Anywhere", "F5", "show the newer view a live capture holds back while records are read"),
        new("Ranked table", "Up or Down", "choose the previous or next row"),
        new("Ranked table", "Ctrl+Space", "add the row's processes to the multi-selection, or take them out"),
        new("Ranked table", "O", "on an RPC call linked to another, open the call at its other end"),
        new("Graph", "Arrow keys", "move to the next or previous node"),
        new("Graph", "Enter", "open the node's group or process"),
        new("Graph", "Ctrl+Space", "add the node's processes to the multi-selection, or take them out"),
        new("Graph", "P", "pin the chosen node where it is drawn, or release it"),
        new("Timeline", "Left or Right", "pan by a tenth of the view; with Shift, by one cell"),
        new("Timeline", "+ or -", "zoom in or out around the analysis interval") { SpokenKeys = "Plus or minus" },
        new("Timeline", "0", "fit the analysis interval, or else the whole session") { SpokenKeys = "Zero" },
        new("Timeline", "Home or End", "go to the session's start or end"),
        new("Timeline", "Up, Down, Page Up or Page Down", "scroll the lanes"),
        new("Timeline", "[ or ]", "step to the previous or next moment holding records")
        {
            SpokenKeys = "Left or right bracket",
        },
        new("Timeline", "P", "pin the chosen process's lane at the top of its group, or unpin it"),
        new("Minimap", "Left, Right, Home, End, +, - or 0", "pan and zoom the view, as in the timeline")
        {
            SpokenKeys = "Left, Right, Home, End, plus, minus or zero",
        },
        new("Relationship table", "Enter", "open the chosen relationship, as a double click on its edge does"),
        new("Interval table", "Up, Down, Home or End", "choose a moment, which becomes the analysis interval"),
        new("Interval table", "Enter", "list the moment's records"),
        new("Search box", "Enter", "open the chosen hit"),
        new("Search box", "Down", "move to the hits"),
        new("Search box", "Esc", "clear the search, and give the keyboard back to the ranked table"),
        new("Inspector", "Enter", "on one of a chosen cell's records, open it among the level's records, chosen there"),
        new("A level's records", "Enter", "show the chosen record as the capture recorded it"),
        new("A level's records", "C", "inspect the chosen record's content"),
    ]);

    /// <summary>Marks the first row of each place, which heads it.</summary>
    internal static WindowKey[] Headed(WindowKey[] keys) =>
        [.. keys.Select((key, index) => key with { Heads = index == 0 || keys[index - 1].Where != key.Where })];
}

/// <summary>
/// Every key the investigation window takes, by where it acts (§8.2, R15), as the sheet F1 opens there lists them. Its
/// timeline's spoken help named its own, and nothing named them where a sighted person could find them.
/// </summary>
internal static class InvestigationKeys
{
    /// <summary>What the sheet says above the investigation window's keys.</summary>
    public const string Intro = "Every key this window takes, by where it acts. Each has a button or a pointer gesture as "
        + "well; Tab reaches every control, and Ctrl+Tab shows the next page.";

    public static IReadOnlyList<WindowKey> All { get; } = WindowKeys.Headed(
    [
        new("Anywhere", "F1", "list the keys this window takes"),
        new("Anywhere", "Tab or Shift+Tab", "move through the window's controls in reading order"),
        new("Anywhere", "Ctrl+Tab or Ctrl+Shift+Tab", "show the next or previous page: the sessions, the candidate joins, "
            + "the timeline or the notes"),
        new("Anywhere", "Ctrl+Page Down or Ctrl+Page Up", "show the next or previous page, as Ctrl+Tab does"),
        new("Anywhere", "Esc", "close the window"),
        new("A page's tab", "Left or Right", "show the previous or next page"),
        new("Sessions", "Up or Down", "choose the previous or next session"),
        new("Sessions", "Enter", "open the chosen session in InterCat"),
        new("Candidate joins", "Up or Down", "choose the previous or next candidate"),
        new("Timeline", "Left or Right", "choose the previous or next column"),
        new("Timeline", "Home or End", "choose the lane's first or last column"),
        new("Timeline", "Up or Down", "move to the previous or next session's lane"),
        new("Timeline", "Enter", "open the column's records in InterCat"),
        new("Timeline", "+ or -", "zoom in or out around the chosen column") { SpokenKeys = "Plus or minus" },
        new("Timeline", "0", "show the whole investigation") { SpokenKeys = "Zero" },
        new("Notes", "Up or Down", "choose the previous or next note"),
        new("Notes", "Enter", "show the chosen note on the timeline, where it is pinned"),
        new("A dialog", "Enter", "in one of its fields, do what the dialog is for: align, compare, state a translation or save "
            + "a view"),
        new("A dialog", "Ctrl+Enter", "save a note, whose words take Enter as a new line"),
        new("A dialog", "Esc", "close it"),
    ]);
}

/// <summary>
/// The keys sheet (§6.7, R15): every key a window takes, by where it acts, one row each, which a screen reader reads row by
/// row - InterCat's window's, or the investigation window's. F1 opens it from anywhere in the window, as its Keys button
/// does, and Esc or F1 closes it.
/// </summary>
internal sealed class KeysWindow : Window
{
    /// <summary>The width of the keys' column, wide enough for the longest key on one line or two.</summary>
    private const double KeysColumn = 190;

    private readonly ListBox keys = new()
    {
        SelectionMode = SelectionMode.Single,
        Background = Brushes.Transparent,
    };

    private readonly TextBlock intro = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>The sheet of InterCat's window's keys.</summary>
    public KeysWindow()
        : this(WindowKeys.All, WindowKeys.Intro)
    {
    }

    /// <summary>The sheet of <paramref name="listed"/>, the keys of the window it opens over, said after <paramref name="introduced"/>.</summary>
    internal KeysWindow(IReadOnlyList<WindowKey> listed, string introduced)
    {
        keys.ItemsSource = listed;
        intro.Text = introduced;
        Title = "Keys";
        Width = 680;
        Height = 620;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(keys, "Keys this window takes, by where they act");
        AutomationProperties.SetHelpText(keys, "Up and Down read the keys one at a time. Esc or F1 closes the list.");
        keys.ItemTemplate = new FuncDataTemplate<WindowKey>((_, _) => Row());
        AccessibleItems.Name(keys);
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, "Close the keys");
        close.Click += (_, _) => Close();
        KeyDown += (_, key) =>
        {
            if (key.Key is Key.Escape or Key.F1)
            {
                Close();
                key.Handled = true;
            }
        };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18), RowSpacing = 10 };
        layout.Children.Add(intro);
        Grid.SetRow(keys, 1);
        layout.Children.Add(keys);
        Grid.SetRow(close, 2);
        layout.Children.Add(close);
        Content = layout;

        // The list has the keyboard as the sheet opens, on its first row, so Up and Down read it from the start.
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => keys.ContainerFromIndex(0)?.Focus(NavigationMethod.Tab),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The sheet's list; a test reads its rows' names and where the keyboard is.</summary>
    internal ListBox List => keys;

    /// <summary>What the sheet says above its keys.</summary>
    internal string Intro => intro.Text ?? string.Empty;

    /// <summary>
    /// A key's row, bound to the key it is given: its place's heading above the first of its place, then the keys and what
    /// they do. The list builds a row before it has a key for it, and gives it another as it scrolls.
    /// </summary>
    private static StackPanel Row()
    {
        var heading = new TextBlock { Classes = { "eyebrow" }, Margin = new Thickness(0, 6, 0, 0) };
        heading.Bind(TextBlock.TextProperty, new Binding(nameof(WindowKey.Heading)));
        heading.Bind(Visual.IsVisibleProperty, new Binding(nameof(WindowKey.Heads)));
        var keysText = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        keysText.Bind(TextBlock.TextProperty, new Binding(nameof(WindowKey.Keys)));
        var does = new TextBlock { TextWrapping = TextWrapping.Wrap };
        does.Bind(TextBlock.TextProperty, new Binding(nameof(WindowKey.Shown)));
        Grid.SetColumn(does, 1);
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                heading,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions($"{KeysColumn},*"),
                    ColumnSpacing = 12,
                    Children = { keysText, does },
                },
            },
        };
    }
}
