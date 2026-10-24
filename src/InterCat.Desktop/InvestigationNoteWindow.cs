using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// The saved comparison a new note is about (`contracts/workspace-v23.md` §7): its name, and the row chosen in it where one
/// is - by the key it is noted by, and in the words the comparison states it in, such as "row 2: browser.exe · PID 100 →
/// 10.0.0.20".
/// </summary>
internal sealed record ComparisonNoteSubject(string Comparison, string? RowKey = null, string? Row = null);

/// <summary>
/// Writes a note on an investigation (§8.4): its words, and - for a new note - an instant of a session it is pinned at,
/// which the investigation's time places as it places any other, or the saved comparison it is about, as a whole or by its
/// chosen row. Rewording a note keeps where it is pinned and what it is about. Every revision is kept, and none changes a
/// session.
/// </summary>
internal sealed class InvestigationNoteWindow : Window
{
    private readonly string path;
    private readonly Guid? editing;
    private readonly ComparisonNoteSubject? about;
    private readonly RadioButton aboutWhole = new() { GroupName = "about" };
    private readonly RadioButton aboutRow = new() { GroupName = "about" };
    private readonly TextBox words = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
    private readonly CheckBox pin = new() { Content = "Pin it at an instant of a session" };
    private readonly ComboBox session = new() { MinWidth = 300 };
    private readonly TextBox at = new() { Watermark = "seconds", Width = 140 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button save = new() { Classes = { "primary" } };
    private bool saving;

    /// <summary>What saving does: adds the note, or saves its new words.</summary>
    private readonly string saves;

    public InvestigationNoteWindow(
        string path,
        IReadOnlyList<CompareSession> sessions,
        InvestigationNoteRow? existing = null,
        (Guid SessionId, long Nanoseconds)? suggested = null,
        ComparisonNoteSubject? about = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        this.path = path;
        editing = existing?.NoteId;
        this.about = about;
        Title = existing is not null ? "Reword a note" : about is not null ? "Add a note about a comparison" : "Add a note";
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
        AutomationProperties.SetHelpText(pin, "Pin the note at an instant of a session");
        AutomationProperties.SetName(session, "The session the note is pinned in");
        TrimmedChoices.Apply(session);
        AutomationProperties.SetName(at, "The instant it is pinned at, in seconds of that session's time");
        AutomationProperties.SetName(status, "Note status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        saves = existing is null ? "Add the note" : "Save the note's new words";
        Offering.Offer(save, null, saves);
        // The session takes the room the instant leaves it, so a long session name never pushes the instant out of the window.
        var atCaption = new TextBlock { Text = "at", VerticalAlignment = VerticalAlignment.Center };
        var place = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(28, 0, 0, 0) };
        Grid.SetColumn(atCaption, 1);
        Grid.SetColumn(at, 2);
        session.HorizontalAlignment = HorizontalAlignment.Stretch;
        place.Children.Add(session);
        place.Children.Add(atCaption);
        place.Children.Add(at);
        // Where the note is pinned is chosen once it is to be pinned; until then each part of it says so.
        void ShowPlace()
        {
            string? unpinned = pin.IsChecked == true ? null : "Choose Pin it at an instant of a session to say where.";
            Offering.Offer(session, unpinned);
            Offering.Offer(at, unpinned);
        }

        pin.IsCheckedChanged += (_, _) => ShowPlace();
        ShowPlace();

        // A note about a comparison is about it as a whole until its chosen row is chosen instead: a row is noted by what it
        // compares, and only once a person chose it in the comparison's list.
        if (about is { } subject)
        {
            Choice(aboutWhole, $"About the comparison '{subject.Comparison}' as a whole");
            Choice(aboutRow, subject.Row is { } row ? "About its " + row : "About its chosen row");
            aboutWhole.IsChecked = true;
            Offering.Offer(aboutRow, subject.RowKey is null ? "Choose a row in the comparison's list first." : null,
                "Note the row chosen in the comparison, by what it compares");
        }

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

        // Ctrl+Enter saves the note, as Save does: its words take Enter as a new line, before the window would see it.
        AddHandler(KeyDownEvent, (_, key) =>
        {
            if (key.Key != Key.Enter || key.KeyModifiers != KeyModifiers.Control) return;
            _ = SaveAsync();
            key.Handled = true;
        }, RoutingStrategies.Tunnel);

        var body = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Words", FontWeight = FontWeight.SemiBold });
        body.Children.Add(words);
        if (existing is not null)
        {
            body.Children.Add(new TextBlock { Text = existing.Where + "; rewording it keeps that.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        else if (about is not null)
        {
            body.Children.Add(new TextBlock { Text = "About", FontWeight = FontWeight.SemiBold });
            body.Children.Add(aboutWhole);
            body.Children.Add(aboutRow);
            body.Children.Add(new TextBlock
            {
                Text = "A row is noted by what it compares, never by its place in the list, which comparing again may change; "
                    + "the comparison says the note each time it is opened.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Classes = { "muted" },
            });
        }
        else
        {
            body.Children.Add(pin);
            body.Children.Add(place);
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

    /// <summary>The note the window wrote; null until it has.</summary>
    public WorkspaceNote? Written { get; private set; }

    /// <summary>Chooses what a note about a comparison is about, as a person would: its chosen row, or the whole of it.</summary>
    internal void AboutTheRow(bool row) => (row ? aboutRow : aboutWhole).IsChecked = true;

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

        // What a note about a comparison is about is read here, where its choice is, before the note is written elsewhere.
        WorkspaceNoteSubject? subject = about is { } comparison
            ? new WorkspaceNoteSubject(comparison.Comparison, aboutRow.IsChecked == true ? comparison.RowKey : null)
            : null;
        saving = true;
        Offering.Offer(save, "The note is being written to the investigation.", saves);
        try
        {
            Written = await Task.Run(() => editing is { } noteId
                ? InvestigationWorkspace.EditNote(path, noteId, text, DateTimeOffset.UtcNow)
                : subject is not null
                    ? InvestigationWorkspace.AddNoteAbout(path, text, subject, DateTimeOffset.UtcNow)
                    : InvestigationWorkspace.AddNote(path, text, anchor, DateTimeOffset.UtcNow));
            Close(true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            status.Text = (editing is null ? "The note was not added: " : "The note was not reworded: ")
                + InvestigationTrouble.Why(path, exception);
            return false;
        }
        finally
        {
            saving = false;
            Offering.Offer(save, null, saves);
        }
    }

    /// <summary>Words a choice of what the note is about, wrapping rather than running past the window's edge.</summary>
    private static void Choice(RadioButton choice, string words)
    {
        choice.Content = new TextBlock { Text = words, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(choice, words);
    }
}
