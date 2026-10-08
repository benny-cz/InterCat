using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.CaptureBroker;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What a content capture keeps, asked before anything is asked of the broker (§11.1, ADR-049): the person's own
/// processes whose messages to keep - each by name, ID and start - the most one message and the whole capture may keep, how
/// long it may record, and whether they may see what it keeps. The broker then prepares it, and a second dialog reviews
/// what it would keep before it starts. It opens on Cancel, so a reflexive Enter asks nothing.
/// </summary>
internal sealed class ContentCaptureWindow : Window
{
    /// <summary>The most one message may keep: its first bytes when it is longer, which the capture records as cut.</summary>
    internal static readonly IReadOnlyList<(string Label, int Bytes)> RecordLimits =
        [("4 KiB", 4 * 1024), ("64 KiB", 64 * 1024), ("1 MiB", 1024 * 1024)];

    /// <summary>The most the capture may keep in all: reaching it stops the capture (stop-at-limit).</summary>
    internal static readonly IReadOnlyList<(string Label, long Bytes)> SessionLimits =
        [("16 MiB", 16L * 1024 * 1024), ("256 MiB", 256L * 1024 * 1024), ("1 GiB", 1024L * 1024 * 1024)];

    /// <summary>How long the capture may record before it stops of itself.</summary>
    internal static readonly IReadOnlyList<(string Label, int Seconds)> Durations =
        [("1 minute", 60), ("10 minutes", 600), ("1 hour", 3_600)];

    /// <summary>The most processes a content request names (`contracts/content-v1.md` §5.1).</summary>
    internal const int MaximumProcesses = 64;

    private readonly Func<IReadOnlyList<SeenProcess>>? refresh;
    private readonly StackPanel processes = new() { Spacing = 2 };
    private readonly Dictionary<int, (SeenProcess Process, CheckBox Box)> rows = [];
    private readonly TextBox filter = new() { Watermark = "Filter by name or process ID" };
    private readonly TextBlock chosenText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock emptyText = new()
    {
        Text = "No process of yours matches. A process that has not started yet is listed once it runs: refresh the list.",
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };

    private readonly ComboBox recordLimit = Choices(RecordLimits.Select(limit => limit.Label), 1);
    private readonly ComboBox sessionLimit = Choices(SessionLimits.Select(limit => limit.Label), 0);
    private readonly ComboBox duration = Choices(Durations.Select(length => length.Label), 1);
    private readonly CheckBox inspection = new()
    {
        Content = new TextBlock { Text = "Let me see what is kept, as hex and text, when I ask", TextWrapping = TextWrapping.Wrap },
    };

    private readonly Button proceed = new() { Content = "Review with the broker…", Classes = { "primary" } };

    public ContentCaptureWindow(IReadOnlyList<SeenProcess> running, Func<IReadOnlyList<SeenProcess>>? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(running);
        this.refresh = refresh;
        Title = "Keep the content of your processes' messages";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancel, "Cancel: nothing is asked of the broker");
        AutomationProperties.SetHelpText(proceed,
            "Ask the broker to prepare the capture, then review what it would keep before anything is recorded");
        AutomationProperties.SetName(filter, "Filter your processes by name or process ID");
        AutomationProperties.SetName(processes, "Your processes, each by name, process ID and start");
        AutomationProperties.SetName(recordLimit, "The most one message keeps");
        AutomationProperties.SetName(sessionLimit, "The most the capture keeps in all");
        AutomationProperties.SetName(duration, "How long the capture records");
        AutomationProperties.SetHelpText(inspection,
            "Without it, what is kept stays with the session but its bytes are never shown");
        AutomationProperties.SetName(chosenText, "Processes chosen");
        AutomationProperties.SetLiveSetting(chosenText, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(emptyText, "No process matches");
        cancel.Click += (_, _) => Close(false);
        proceed.Click += (_, _) =>
        {
            if (Chosen is not null)
            {
                Close(true);
            }
        };
        // At once, as each character is typed: a list of a few hundred processes narrows faster than a person types.
        filter.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
            {
                Narrow();
            }
        };
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close(false);
            key.Handled = true;
        };

        var refreshButton = new Button { Content = "Refresh the list", IsVisible = refresh is not null };
        AutomationProperties.SetHelpText(refreshButton, "List your processes as they run now; what you chose stays chosen");
        refreshButton.Click += (_, _) => List(this.refresh!());

        var limits = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 6,
        };
        foreach ((string label, ComboBox choice, int row) in new[]
        {
            ("Each message", recordLimit, 0),
            ("In all", sessionLimit, 1),
            ("Records for", duration, 2),
        })
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(choice, row);
            Grid.SetColumn(choice, 1);
            limits.Children.Add(text);
            limits.Children.Add(choice);
            choice.SelectionChanged += (_, _) => Restate();
        }

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = Intro,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "Processes", FontWeight = FontWeight.SemiBold },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children = { filter, Column(refreshButton, 1) },
                },
                new ScrollViewer { Content = processes, Height = 220, Padding = new Thickness(0, 0, 12, 0) },
                emptyText,
                chosenText,
                new TextBlock { Text = "Limits", FontWeight = FontWeight.SemiBold },
                limits,
                inspection,
            },
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, proceed },
        };
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        Grid.SetRow(actions, 1);
        layout.Children.Add(body);
        layout.Children.Add(actions);
        Content = layout;
        List(running);
    }

    /// <summary>What the dialog says first: what is kept, whose, and what happens before anything is recorded.</summary>
    internal const string Intro =
        "InterCat keeps the HTTP messages WinINet raises in the processes you choose - each request and response, its "
        + "headers and body; over HTTPS as their plaintext, cookies and authorization included - within the limits below. "
        + "Only your own processes can be kept: the capture broker reads each one and refuses another user's, another "
        + "sign-in's or an elevated one. Windows asks for approval to start the broker, and you review what it would keep "
        + "before anything is recorded.";

    /// <summary>The capture chosen, or null while none can be asked for: no process chosen, or more than a request names.</summary>
    internal ContentCaptureChoice? Chosen
    {
        get
        {
            SeenProcess[] chosen = [.. rows.Values.Where(row => row.Box.IsChecked == true).Select(row => row.Process)];
            return chosen.Length is 0 or > MaximumProcesses
                ? null
                : new(
                    chosen,
                    RecordLimits[recordLimit.SelectedIndex].Bytes,
                    SessionLimits[sessionLimit.SelectedIndex].Bytes,
                    inspection.IsChecked == true ? ContentInspectionMode.HexAndText : ContentInspectionMode.Disabled,
                    Durations[duration.SelectedIndex].Seconds);
        }
    }

    /// <summary>What the dialog says of the processes chosen, as its live line says it.</summary>
    internal string ChosenStatement => chosenText.Text ?? string.Empty;

    /// <summary>The processes listed now, in their order, each as its box names it; those hidden by the filter left out.</summary>
    internal IReadOnlyList<string> Listed =>
    [
        .. processes.Children.OfType<CheckBox>().Where(box => box.IsVisible)
            .Select(box => AutomationProperties.GetName(box) ?? string.Empty),
    ];

    /// <summary>Whether asking the broker is offered: only with a process chosen, and no more than a request names.</summary>
    internal bool CanProceed => proceed.IsEnabled;

    /// <summary>Chooses a process, or leaves it out, as its box does; a test uses it.</summary>
    internal void Choose(int processId, bool chosen) => rows[processId].Box.IsChecked = chosen;

    /// <summary>Narrows the list as typing into the filter does; a test uses it.</summary>
    internal void Filter(string text) => filter.Text = text;

    /// <summary>Consents to seeing what is kept, or not, as the box does; a test uses it.</summary>
    internal void Consent(bool shown) => inspection.IsChecked = shown;

    /// <summary>Chooses the limits and the length by their index in each list, as the selectors do; a test uses it.</summary>
    internal void Limit(int record, int session, int length)
    {
        recordLimit.SelectedIndex = record;
        sessionLimit.SelectedIndex = session;
        duration.SelectedIndex = length;
    }

    /// <summary>
    /// Lists <paramref name="running"/>, keeping what was chosen of the processes still listed; a process that is gone from
    /// the list is chosen no more.
    /// </summary>
    private void List(IReadOnlyList<SeenProcess> running)
    {
        var chosen = rows.Values.Where(row => row.Box.IsChecked == true).Select(row => row.Process).ToHashSet();
        rows.Clear();
        processes.Children.Clear();
        foreach (SeenProcess process in running)
        {
            string name = Named(process);
            var box = new CheckBox
            {
                IsChecked = chosen.Contains(process),
                Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetName(box, name);
            box.IsCheckedChanged += (_, _) => Restate();
            rows[process.ProcessId] = (process, box);
            processes.Children.Add(box);
        }

        Narrow();
    }

    /// <summary>Shows the processes the filter matches by name or ID, hiding none that is chosen.</summary>
    private void Narrow()
    {
        string text = filter.Text?.Trim() ?? string.Empty;
        foreach ((SeenProcess process, CheckBox box) in rows.Values)
        {
            box.IsVisible = box.IsChecked == true || text.Length == 0
                || (process.Image?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || process.ProcessId.ToString(CultureInfo.InvariantCulture).StartsWith(text, StringComparison.Ordinal);
        }

        emptyText.IsVisible = !rows.Values.Any(row => row.Box.IsVisible);
        Restate();
    }

    private void Restate()
    {
        int count = rows.Values.Count(row => row.Box.IsChecked == true);
        chosenText.Text = count switch
        {
            0 => "Choose the processes whose messages to keep.",
            > MaximumProcesses => string.Create(CultureInfo.CurrentCulture,
                $"{count:N0} processes are chosen; a content capture names at most {MaximumProcesses}."),
            _ => $"{CountText.Of(count, "process", "processes")} chosen. The capture keeps up to "
                + $"{SessionLimits[Math.Max(sessionLimit.SelectedIndex, 0)].Label} in all, the first "
                + $"{RecordLimits[Math.Max(recordLimit.SelectedIndex, 0)].Label} of each message, for up to "
                + $"{Durations[Math.Max(duration.SelectedIndex, 0)].Label}.",
        };
        proceed.IsEnabled = Chosen is not null;
    }

    /// <summary>A process as the list names it: "notepad, process 4242, started 2026-10-08 11:00:00.123".</summary>
    private static string Named(SeenProcess process)
    {
        string described = BrokerContentReview.Describe(process, TimeZoneInfo.Local);
        int comma = described.IndexOf(", ", StringComparison.Ordinal);
        return string.Create(CultureInfo.CurrentCulture, $"{described[..comma]}, process {process.ProcessId}")
            + described[comma..];
    }

    private static ComboBox Choices(IEnumerable<string> labels, int selected) => new()
    {
        ItemsSource = labels.ToArray(),
        SelectedIndex = selected,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MinHeight = 28,
    };

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
