using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>A saved view as the dialog lists it: its name and interval, and whether it is of the investigation's time now.</summary>
internal sealed record ViewRow(WorkspaceView View, bool Current)
{
    public override string ToString() => $"{View.Name} · {WorkspaceTime.FormatRange(View.Interval!.Value, CultureInfo.CurrentCulture)}"
        + (Current ? string.Empty : " · saved on another time reference, so not shown");
}

/// <summary>
/// The investigation's saved views (§8.4): named intervals of its time a person keeps, to show on the timeline again. The
/// interval shown now is saved under a name, replacing a view of that name; a view saved on another time reference is
/// kept, and never shown on this clock. Each change is a revision of the investigation's file.
/// </summary>
internal sealed class InvestigationViewsWindow : Window
{
    private readonly string path;
    private readonly TimeRange? shown;
    private readonly ListBox views = new() { SelectionMode = SelectionMode.Single, MinHeight = 90, MaxHeight = 240 };

    /// <summary>What the list says in its place while it holds no view, so an empty list never reads as one loading.</summary>
    private readonly TextBlock none = new()
    {
        Text = "No view is saved yet: name the interval the timeline shows, below, to show it again.",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(10, 8),
        IsHitTestVisible = false,
        IsVisible = false,
        Classes = { "muted" },
    };
    private readonly TextBox name = new() { Watermark = "such as: the upload stalls" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button show = new() { Content = "Show", IsEnabled = false, Classes = { "primary" } };
    private readonly Button remove = new() { Content = "Remove", IsEnabled = false };
    private readonly Button save = new() { Content = "Save the view shown" };
    private bool busy;

    public InvestigationViewsWindow(string path, TimeRange? shown)
    {
        this.path = path;
        this.shown = shown;
        Title = "Saved views";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(views, "Saved views of the investigation's time");
        AutomationProperties.SetHelpText(views, "Enter or a double click shows the chosen view on the timeline.");
        AutomationProperties.SetName(name, "A name for the view shown now");
        AutomationProperties.SetHelpText(name, "Enter saves the interval the timeline shows now under this name.");
        AutomationProperties.SetName(status, "Saved views status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(show, "Show the selected view on the timeline");
        AutomationProperties.SetName(remove, "Remove the selected view");
        AutomationProperties.SetHelpText(save, "Save the interval the timeline shows now under this name");
        save.IsEnabled = shown is not null;
        views.SelectionChanged += (_, _) => ShowChoice();
        views.DoubleTapped += (_, _) => ShowSelected();
        show.Click += (_, _) => ShowSelected();
        remove.Click += (_, _) => _ = RemoveSelectedAsync();
        save.Click += (_, _) => _ = SaveAsync();
        var close = new Button { Content = "Close" };
        AutomationProperties.SetName(close, "Close the saved views");
        close.Click += (_, _) => Close(null);
        KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                Close(null);
                key.Handled = true;
            }
            else if (key.Key == Key.Enter && views.IsKeyboardFocusWithin)
            {
                // Enter on a view shows it, as a double click does. The list has already chosen the row with the keyboard
                // by the time the key reaches the window, so an unchosen row is shown as a chosen one is.
                ShowSelected();
                key.Handled = true;
            }
            else if (key.Key == Key.Enter && name.IsKeyboardFocusWithin && save.IsEnabled)
            {
                // Enter in the name saves the view shown under it, as the button beside it does.
                _ = SaveAsync();
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
                    Text = "A view is a named interval of the investigation's time, to show on the timeline again. It is in its "
                        + "reference session's clock: one saved before the time reference changed is kept, and not shown.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new Grid { Children = { views, none } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { remove, show } },
                new TextBlock
                {
                    Text = shown is { } now
                        ? $"The timeline shows {WorkspaceTime.FormatRange(now, CultureInfo.CurrentCulture)} now."
                        : "The timeline shows nothing yet, so there is no view to save.",
                    FontSize = 12,
                },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children = { name, Column(save, 1) },
                },
                status,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } },
            },
        };
        Opened += (_, _) =>
        {
            Load();

            // The first view has the keyboard, chosen, so Enter shows it; with none, the name to save the view shown under, or
            // with nothing to save, Close. The dialog opened with the keyboard on nothing of its own.
            if (views.ItemCount > 0)
            {
                views.SelectedIndex = 0;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => views.ContainerFromIndex(0)?.Focus(NavigationMethod.Tab), Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                (save.IsEnabled ? (Control)name : close).Focus(NavigationMethod.Tab);
            }
        };
    }

    /// <summary>The views listed, as the list holds them.</summary>
    internal IReadOnlyList<ViewRow> Listed => [.. (views.ItemsSource as IEnumerable<ViewRow>) ?? []];

    /// <summary>What the dialog says of the last thing done or refused, as its status line shows it.</summary>
    internal string Status => status.Text ?? string.Empty;

    /// <summary>Selects a listed view, as a click does; a test uses it.</summary>
    internal void Select(int index) => views.SelectedIndex = index;

    /// <summary>Names the view to save, as typing does; a test uses it.</summary>
    internal void NameIt(string text) => name.Text = text;

    /// <summary>Lists the views; a test calls it where the dialog is not opened.</summary>
    internal void Load()
    {
        try
        {
            InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
            ViewRow[] rows = [.. InvestigationWorkspace.ViewsInForce(workspace)
                .Select(view => new ViewRow(view, InvestigationWorkspace.ViewIsCurrent(workspace, view)))];
            views.ItemsSource = rows;
            none.IsVisible = rows.Length == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            status.Text = "The investigation could not be read: " + exception.Message;
        }
    }

    /// <summary>Saves the interval shown now under the name given; says in words why not when it is refused.</summary>
    internal async Task<bool> SaveAsync()
    {
        if (busy || shown is not { } interval) return false;
        string named = name.Text ?? string.Empty;
        busy = true;
        try
        {
            WorkspaceView saved = await Task.Run(() => InvestigationWorkspace.SaveView(path, named, interval, DateTimeOffset.UtcNow));
            status.Text = $"Saved as '{saved.Name}'.";
            name.Text = string.Empty;
            Load();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            status.Text = exception.Message;
            return false;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>Removes the selected view, kept as a revision.</summary>
    internal async Task<bool> RemoveSelectedAsync()
    {
        if (busy || views.SelectedItem is not ViewRow row) return false;
        busy = true;
        try
        {
            _ = await Task.Run(() => InvestigationWorkspace.RemoveView(path, row.View.Name, DateTimeOffset.UtcNow));
            status.Text = $"'{row.View.Name}' is removed; its revisions are kept in the file.";
            Load();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            status.Text = exception.Message;
            return false;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>Shows the chosen view on the timeline, closing the dialog; one this time reference cannot show says why.</summary>
    private void ShowSelected()
    {
        switch (views.SelectedItem)
        {
            case ViewRow { Current: true } row:
                Close(row.View.Interval);
                break;
            case ViewRow row:
                status.Text = $"'{row.View.Name}' was saved on another time reference, so this one cannot show it.";
                break;
        }
    }

    private void ShowChoice()
    {
        ViewRow? row = views.SelectedItem as ViewRow;
        show.IsEnabled = row is { Current: true };
        remove.IsEnabled = row is not null;
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
