using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>A session an instant is read in, as the compare dialog offers it.</summary>
internal sealed record CompareSession(Guid SessionId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Compares two instants of an investigation's sessions (§8.2): each placed in the investigation's time with its
/// uncertainty, or said to have no place and why, and their order stated only beyond the pair's uncertainty - exactly on
/// one clock, and as a shared alignment allows. Nothing is written.
/// </summary>
internal sealed class InvestigationCompareWindow : Window
{
    private readonly string path;
    private readonly ComboBox firstSession = new() { MinWidth = 300 };
    private readonly TextBox firstAt = new() { Watermark = "seconds", Width = 140 };
    private readonly ComboBox secondSession = new() { MinWidth = 300 };
    private readonly TextBox secondAt = new() { Watermark = "seconds", Width = 140 };
    private readonly TextBlock statement = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock placements = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button compare = new() { Content = "Compare", Classes = { "primary" } };
    private bool comparing;

    public InvestigationCompareWindow(string path, IReadOnlyList<CompareSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        this.path = path;
        Title = "Compare two instants";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        firstSession.ItemsSource = sessions;
        secondSession.ItemsSource = sessions;
        firstSession.SelectedIndex = sessions.Count > 0 ? 0 : -1;
        secondSession.SelectedIndex = sessions.Count > 1 ? 1 : firstSession.SelectedIndex;
        AutomationProperties.SetName(firstSession, "The first instant's session");
        TrimmedChoices.Apply(firstSession);
        TrimmedChoices.Apply(secondSession);
        AutomationProperties.SetName(firstAt, "The first instant, in seconds of its session's time");
        AutomationProperties.SetName(secondSession, "The second instant's session");
        AutomationProperties.SetName(secondAt, "The second instant, in seconds of its session's time");
        AutomationProperties.SetName(statement, "What may be said of their order");
        AutomationProperties.SetName(placements, "Where each instant falls in the investigation's time");
        AutomationProperties.SetName(compare, "Compare the two instants");
        compare.Click += (_, _) => _ = CompareAsync();
        var close = new Button { Content = "Close" };
        AutomationProperties.SetName(close, "Close the comparison");
        close.Click += (_, _) => Close();
        Opened += (_, _) => firstAt.Focus();
        KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                Close();
                key.Handled = true;
            }
            else if (key.Key == Key.Enter)
            {
                _ = CompareAsync();
                key.Handled = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Read an instant in each session - such as the moment a connection opened - in seconds of that "
                        + "session's own time. Their order is stated only beyond what their alignments leave uncertain.",
                    TextWrapping = TextWrapping.Wrap,
                },
                Row("First", firstSession, firstAt),
                Row("Second", secondSession, secondAt),
                statement,
                placements,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { close, compare },
                },
            },
        };
    }

    /// <summary>The comparison as last made; null before one was, or when the input was not two instants.</summary>
    internal WorkspaceComparison? Comparison { get; private set; }

    /// <summary>What the dialog says: the order, then where each instant falls.</summary>
    internal string Said => string.Join("\n", new[] { statement.Text, placements.Text }.Where(text => !string.IsNullOrEmpty(text)));

    /// <summary>Fills in both instants, as a person would; a test uses it.</summary>
    internal void Enter(Guid first, string firstSeconds, Guid second, string secondSeconds)
    {
        firstSession.SelectedItem = (firstSession.ItemsSource as IEnumerable<CompareSession>)?.FirstOrDefault(session => session.SessionId == first);
        secondSession.SelectedItem = (secondSession.ItemsSource as IEnumerable<CompareSession>)?.FirstOrDefault(session => session.SessionId == second);
        firstAt.Text = firstSeconds;
        secondAt.Text = secondSeconds;
    }

    /// <summary>Compares the two instants as entered, off the window's thread, and says what may be said of them.</summary>
    internal async Task<WorkspaceComparison?> CompareAsync()
    {
        if (comparing) return null;
        if (firstSession.SelectedItem is not CompareSession first || secondSession.SelectedItem is not CompareSession second)
        {
            statement.Text = "Choose each instant's session.";
            return null;
        }

        if (InvestigationInput.Seconds(firstAt.Text) is not { } a || InvestigationInput.Seconds(secondAt.Text) is not { } b)
        {
            statement.Text = "Write both instants in seconds of their own session's time, such as 12.5.";
            placements.Text = string.Empty;
            return null;
        }

        comparing = true;
        compare.IsEnabled = false;
        try
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            WorkspaceComparison result = await Task.Run(() =>
                InvestigationWorkspace.Compare(InvestigationWorkspace.Read(path), first.SessionId, a, second.SessionId, b));
            Comparison = result;
            statement.Text = result.Statement(culture);
            placements.Text = InvestigationRows.Placed("The first", result.First, culture) + "\n"
                + InvestigationRows.Placed("The second", result.Second, culture);
            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            statement.Text = "They could not be compared: " + exception.Message;
            placements.Text = string.Empty;
            return null;
        }
        finally
        {
            comparing = false;
            compare.IsEnabled = true;
        }
    }

    private static Grid Row(string label, ComboBox session, TextBox at)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,Auto,Auto"), ColumnSpacing = 8 };
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var atCaption = new TextBlock { Text = "at", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(session, 1);
        Grid.SetColumn(atCaption, 2);
        Grid.SetColumn(at, 3);
        row.Children.Add(caption);
        row.Children.Add(session);
        row.Children.Add(atCaption);
        row.Children.Add(at);
        return row;
    }
}
