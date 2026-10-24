using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>A saved comparison as the dialog lists it: its name, and what it compares in icat compare's words.</summary>
internal sealed record SavedComparisonRow(SavedComparison Comparison) : IAccessibleRow
{
    /// <summary>What it compares, in icat compare's words.</summary>
    public string Compares => ComparisonText.Saved(Comparison, CultureInfo.CurrentCulture);

    public string AccessibleName => $"{Comparison.Name}: {Compares}.";

    public override string ToString() => $"{Comparison.Name} · {Compares}";
}

/// <summary>
/// The investigation's saved comparisons (`contracts/workspace-v23.md` §7): each in force, by its name and what it
/// compares, to open in InterCat's window - its compared session at its interval, and the Compare window on it as saved -
/// or to remove. A comparison is saved from the Compare window of a session opened from the investigation, or by `icat
/// workspace comparison`; removing one is a revision of the investigation's file, which keeps the earlier ones.
/// </summary>
internal sealed class InvestigationComparisonsWindow : Window
{
    private readonly string path;
    private readonly ListBox comparisons = new() { SelectionMode = SelectionMode.Single, MinHeight = 90, MaxHeight = 260 };

    /// <summary>What the list says in its place while it holds no comparison, so an empty list never reads as one loading.</summary>
    private readonly TextBlock none = new()
    {
        Text = "No comparison is saved yet. Compare with… in InterCat's window saves one when its session was opened from "
            + "this investigation, and so does icat workspace comparison.",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(10, 8),
        IsHitTestVisible = false,
        IsVisible = false,
        Classes = { "muted" },
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button open = new() { Content = "Open", IsEnabled = false, Classes = { "primary" } };
    private readonly Button remove = new() { Content = "Remove", IsEnabled = false };
    private bool busy;

    /// <summary>Why no comparison is listed when the investigation could not be read; null once it is.</summary>
    private string? unread;

    public InvestigationComparisonsWindow(string path)
    {
        this.path = path;
        Title = "Saved comparisons";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(comparisons, "Comparisons saved in the investigation");
        AutomationProperties.SetHelpText(comparisons, "Enter or a double click opens the chosen comparison in InterCat's window.");
        AutomationProperties.SetName(status, "Saved comparisons status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(open, "Open the selected comparison in InterCat's window");
        AutomationProperties.SetName(remove, "Remove the selected comparison");
        AccessibleItems.Name(comparisons);

        // A row is its name, then what it compares, wrapping within the list rather than running past its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(comparisons, ScrollBarVisibility.Disabled);
        comparisons.ItemTemplate = new FuncDataTemplate<SavedComparisonRow>((row, _) => new StackPanel
        {
            Margin = new Thickness(2, 4),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = row?.Comparison.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Compares, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        });
        comparisons.SelectionChanged += (_, _) => ShowChoice();
        comparisons.DoubleTapped += (_, _) => OpenSelected();
        open.Click += (_, _) => OpenSelected();
        remove.Click += (_, _) => _ = RemoveSelectedAsync();
        var close = new Button { Content = "Close" };
        AutomationProperties.SetName(close, "Close the saved comparisons");
        close.Click += (_, _) => Close(null);
        KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                Close(null);
                key.Handled = true;
            }
            else if (key.Key == Key.Enter && comparisons.IsKeyboardFocusWithin)
            {
                // Enter on a comparison opens it, as a double click does.
                OpenSelected();
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
                    Text = "A saved comparison keeps two intervals of the investigation's sessions - one session's two, or two "
                        + "captures' - what they are compared by and what each row is of, never the answer. Opening one shows "
                        + "its compared session at its interval in InterCat's window, and compares it as saved, reading the "
                        + "sessions as they are now. Each interval is in its own session's time.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new Grid { Children = { comparisons, none } },
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { remove, open, close },
                },
            },
        };
        Opened += (_, _) =>
        {
            Load();

            // The first comparison has the keyboard, chosen, so Enter opens it; with none, Close.
            if (comparisons.ItemCount > 0)
            {
                comparisons.SelectedIndex = 0;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => comparisons.ContainerFromIndex(0)?.Focus(NavigationMethod.Tab), Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                close.Focus(NavigationMethod.Tab);
            }
        };
    }

    /// <summary>The comparisons listed, as the list holds them.</summary>
    internal IReadOnlyList<SavedComparisonRow> Listed => [.. (comparisons.ItemsSource as IEnumerable<SavedComparisonRow>) ?? []];

    /// <summary>What the dialog says of the last thing done or refused, as its status line shows it.</summary>
    internal string Status => status.Text ?? string.Empty;

    /// <summary>Selects a listed comparison, as a click does; a test uses it.</summary>
    internal void Select(int index) => comparisons.SelectedIndex = index;

    /// <summary>Lists the comparisons in force; a test calls it where the dialog is not opened.</summary>
    internal void Load()
    {
        try
        {
            SavedComparisonRow[] rows = [.. InvestigationWorkspace.ComparisonsInForce(InvestigationWorkspace.Read(path))
                .Select(comparison => new SavedComparisonRow(comparison))];
            comparisons.ItemsSource = rows;
            none.IsVisible = rows.Length == 0;
            unread = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            comparisons.ItemsSource = Array.Empty<SavedComparisonRow>();
            status.Text = "The investigation could not be read: " + InvestigationTrouble.Why(path, exception);
            unread = "The investigation could not be read; the status line says why.";
        }

        ShowChoice();
    }

    /// <summary>Removes the selected comparison, kept as a revision.</summary>
    internal async Task<bool> RemoveSelectedAsync()
    {
        if (busy || comparisons.SelectedItem is not SavedComparisonRow row) return false;
        busy = true;
        try
        {
            _ = await Task.Run(() => InvestigationWorkspace.RemoveComparison(path, row.Comparison.Name, DateTimeOffset.UtcNow));
            status.Text = $"'{row.Comparison.Name}' is removed; its revisions are kept in the file.";
            Load();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            status.Text = $"'{row.Comparison.Name}' was not removed: " + InvestigationTrouble.Why(path, exception);
            return false;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>Closes the dialog with the chosen comparison, which the investigation's window opens.</summary>
    private void OpenSelected()
    {
        if (comparisons.SelectedItem is SavedComparisonRow row) Close(row.Comparison);
    }

    /// <summary>Offers opening and removing the chosen comparison, or says why not on each: none is saved, read or chosen.</summary>
    private void ShowChoice()
    {
        string? whyNot = comparisons.SelectedItem is SavedComparisonRow ? null
            : comparisons.ItemCount > 0 ? "Choose a comparison first."
            : unread ?? "No comparison is saved yet.";
        Offering.Offer(open, whyNot);
        Offering.Offer(remove, whyNot);
    }
}
