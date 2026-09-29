using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// Writes a note on an investigation (§8.4): its words, and - for a new note - an instant of a session it is pinned at,
/// which the investigation's time places as it places any other. Rewording a note keeps where it is pinned. Every revision
/// is kept, and none changes a session.
/// </summary>
internal sealed class InvestigationNoteWindow : Window
{
    private readonly string path;
    private readonly Guid? editing;
    private readonly TextBox words = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
    private readonly CheckBox pin = new() { Content = "Pin it at an instant of a session" };
    private readonly ComboBox session = new() { MinWidth = 300 };
    private readonly TextBox at = new() { Watermark = "seconds", Width = 140 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button save = new() { Classes = { "primary" } };
    private bool saving;

    public InvestigationNoteWindow(
        string path,
        IReadOnlyList<CompareSession> sessions,
        InvestigationNoteRow? existing = null,
        (Guid SessionId, long Nanoseconds)? suggested = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        this.path = path;
        editing = existing?.NoteId;
        Title = existing is null ? "Add a note" : "Reword a note";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        save.Content = existing is null ? "Add note" : "Save words";
        words.Text = existing?.Text;
        session.ItemsSource = sessions;
        session.SelectedItem = sessions.FirstOrDefault(choice => choice.SessionId == suggested?.SessionId) ?? (sessions.Count > 0 ? sessions[0] : null);
        at.Text = suggested is { } where ? (where.Nanoseconds / 1_000_000_000m).ToString("0.#########", System.Globalization.CultureInfo.CurrentCulture) : null;
        pin.IsChecked = suggested is not null;
        AutomationProperties.SetName(words, "The note's words");
        AutomationProperties.SetName(pin, "Pin the note at an instant of a session");
        AutomationProperties.SetName(session, "The session the note is pinned in");
        AutomationProperties.SetName(at, "The instant it is pinned at, in seconds of that session's time");
        AutomationProperties.SetName(status, "Note status");
        AutomationProperties.SetName(save, existing is null ? "Add the note" : "Save the note's new words");
        var place = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(28, 0, 0, 0),
            Children = { session, new TextBlock { Text = "at", VerticalAlignment = VerticalAlignment.Center }, at },
        };
        void ShowPlace() => place.IsEnabled = pin.IsChecked == true;
        pin.IsCheckedChanged += (_, _) => ShowPlace();
        ShowPlace();
        save.Click += (_, _) => _ = SaveAsync();
        var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancel, "Cancel: nothing is written");
        cancel.Click += (_, _) => Close(false);
        Opened += (_, _) => words.Focus();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close(false);
            key.Handled = true;
        };

        var body = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Words", FontWeight = FontWeight.SemiBold });
        body.Children.Add(words);
        if (existing is null)
        {
            body.Children.Add(pin);
            body.Children.Add(place);
        }
        else
        {
            body.Children.Add(new TextBlock { Text = existing.Where + "; rewording it keeps that.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }

        body.Children.Add(new TextBlock
        {
            Text = "A note is kept as a revision of the investigation's file, as its earlier words are, and changes no session.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Classes = { "muted" },
        });
        body.Children.Add(status);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, save },
        });
        Content = body;
    }

    /// <summary>Fills in the note, as a person would; a test uses it.</summary>
    internal void Enter(string text, Guid? pinnedIn = null, string? seconds = null)
    {
        words.Text = text;
        pin.IsChecked = pinnedIn is not null;
        if (pinnedIn is { } chosen)
        {
            session.SelectedItem = (session.ItemsSource as IEnumerable<CompareSession>)?.FirstOrDefault(choice => choice.SessionId == chosen);
            at.Text = seconds;
        }
    }

    /// <summary>Writes the note and closes with true; says in words what is missing or refused.</summary>
    internal async Task<bool> SaveAsync()
    {
        if (saving) return false;
        string text = words.Text ?? string.Empty;
        WorkspaceNoteAnchor? anchor = null;
        if (editing is null && pin.IsChecked == true)
        {
            if (session.SelectedItem is not CompareSession chosen || InvestigationInput.Seconds(at.Text) is not { } nanoseconds)
            {
                status.Text = "Choose the session and write the instant in seconds of its own time, such as 12.5, or leave the note unpinned.";
                return false;
            }

            anchor = new WorkspaceNoteAnchor(chosen.SessionId, nanoseconds);
        }

        saving = true;
        save.IsEnabled = false;
        try
        {
            _ = await Task.Run(() => editing is { } noteId
                ? InvestigationWorkspace.EditNote(path, noteId, text, DateTimeOffset.UtcNow)
                : InvestigationWorkspace.AddNote(path, text, anchor, DateTimeOffset.UtcNow));
            Close(true);
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
            saving = false;
            save.IsEnabled = true;
        }
    }
}
