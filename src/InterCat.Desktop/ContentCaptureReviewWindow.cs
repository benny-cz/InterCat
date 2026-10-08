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
/// A content capture as the broker prepared it, reviewed before it starts (§11.1, ADR-049): what it keeps of whose
/// messages - each process by what it runs and the start the broker pinned - within which limits and under which consent,
/// what its sources collect, and how long it records. Nothing is recorded until Start; it opens on Cancel, so a reflexive
/// Enter starts nothing.
/// </summary>
internal sealed class ContentCaptureReviewWindow : Window
{
    public ContentCaptureReviewWindow(
        BrokerEffectiveCaptureSummary summary,
        Mechanism mechanism,
        IReadOnlyDictionary<int, SeenProcess> seen)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(seen);
        Title = "Start this content capture?";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Lines =
        [
            .. BrokerContentReview.Lines(summary, mechanism, seen, TimeZoneInfo.Local),
            ("Records for", Length(summary.Quota)),
        ];
        Statements = summary.CollectionStatement.Contains(summary.Disclosure, StringComparison.Ordinal)
            ? [summary.CollectionStatement]
            : [summary.CollectionStatement, summary.Disclosure];

        var cancel = new Button { Content = "Cancel" };
        var start = new Button { Content = "Start capture", Classes = { "primary" } };
        AutomationProperties.SetName(cancel, "Cancel: nothing is recorded");
        AutomationProperties.SetHelpText(start, "Start recording what this review says, until its limits stop it or you do");
        cancel.Click += (_, _) => Close(false);
        start.Click += (_, _) => Close(true);
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close(false);
            key.Handled = true;
        };

        var table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            RowSpacing = 6,
        };
        for (int index = 0; index < Lines.Count; index++)
        {
            (string label, string value) = Lines[index];
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };

            // Heard as one fact: what the row is and what it says.
            AutomationProperties.SetName(text, label + ": " + value);
            Grid.SetRow(name, index);
            Grid.SetRow(text, index);
            Grid.SetColumn(text, 1);
            table.Children.Add(name);
            table.Children.Add(text);
        }

        var body = new StackPanel { Spacing = 12, Children = { table } };
        foreach (string statement in Statements)
        {
            body.Children.Add(new TextBlock { Text = statement, TextWrapping = TextWrapping.Wrap });
        }

        body.Children.Add(new TextBlock
        {
            Text = "Nothing is recorded until you start it. Stopping it keeps what it has recorded.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, start },
        };
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        var scroller = new ScrollViewer { Content = body, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(actions, 1);
        layout.Children.Add(scroller);
        layout.Children.Add(actions);
        Content = layout;
    }

    /// <summary>What the review states, row by row: what is kept, each process, the limits, the consent and the length.</summary>
    internal IReadOnlyList<(string Label, string Value)> Lines { get; }

    /// <summary>What the capture's sources collect and its scope discloses, a paragraph each.</summary>
    internal IReadOnlyList<string> Statements { get; }

    /// <summary>How long the capture records and what else stops it, in the card's words.</summary>
    private static string Length(BrokerCaptureQuota quota) =>
        string.Create(CultureInfo.CurrentCulture, $"up to {Minutes(quota.MaximumDurationSeconds)}, or ")
        + $"{ByteSizeText.Of(quota.MaximumJournalBytes)} of journal, keeping {ByteSizeText.Of(quota.MinimumFreeDiskBytes)} "
        + "free on the recording volume";

    private static string Minutes(int seconds) => seconds % 3_600 == 0
        ? CountText.Of(seconds / 3_600, "hour")
        : seconds % 60 == 0
            ? CountText.Of(seconds / 60, "minute")
            : CountText.Of(seconds, "second");
}
