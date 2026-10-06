using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>A known address translation in force, as the dialog lists it.</summary>
internal sealed record TranslationRow(WorkspaceAddressTranslation Translation)
{
    public override string ToString() => string.Create(CultureInfo.CurrentCulture,
        $"{Translation.Seen} is {Translation.Is} · revision {Translation.Revision:N0}")
        + (Translation.Note is { } note ? " · " + note : string.Empty);
}

/// <summary>
/// The investigation's known address translations (§8.3): a person's statements that an endpoint one capture sees - a port
/// forward's, a NAT's or a proxy's - is an endpoint the other holds. Candidate joins mirror through them and say so. Each
/// statement and withdrawal is kept as a revision of the investigation's file; no session changes.
/// </summary>
internal sealed class InvestigationTranslationsWindow : Window
{
    private readonly string path;
    private readonly ListBox known = new() { SelectionMode = SelectionMode.Single, MinHeight = 90, MaxHeight = 220 };
    private readonly TextBox seen = new() { Watermark = "such as 203.0.113.7:8443" };
    private readonly TextBox actual = new() { Watermark = "such as 10.0.0.5:443" };
    private readonly TextBox note = new() { Watermark = "optional, such as: the router forwards 8443 to the web server" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button state = new() { Content = "State translation", Classes = { "primary" } };
    private readonly Button withdraw = new() { Content = "Withdraw", IsEnabled = false };
    private bool busy;

    public InvestigationTranslationsWindow(string path)
    {
        this.path = path;
        Title = "Known address translations";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(known, "Address translations in force");
        AutomationProperties.SetName(seen, "The endpoint as one capture sees it");
        AutomationProperties.SetName(actual, "The endpoint it is, as the other capture holds it");
        AutomationProperties.SetName(note, "A note about this translation");
        AutomationProperties.SetName(status, "Translation status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetHelpText(state, "State that the seen endpoint is the other");
        AutomationProperties.SetName(withdraw, "Withdraw the selected translation");
        known.SelectionChanged += (_, _) => withdraw.IsEnabled = known.SelectedItem is TranslationRow && !busy;
        state.Click += (_, _) => _ = StateAsync();
        withdraw.Click += (_, _) => _ = WithdrawSelectedAsync();
        var close = new Button { Content = "Close" };
        AutomationProperties.SetName(close, "Close the translations");
        close.Click += (_, _) => Close(Changed);
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close(Changed);
            key.Handled = true;
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "When one capture dials a port forward's, a NAT's or a proxy's endpoint and the other holds the endpoint "
                        + "behind it, their connections mirror only through what you know of it. State it here: candidate joins "
                        + "then mirror through it, and each says it rests on your statement. Both endpoints have a port, or "
                        + "both are an address alone, whose ports pass through; a loopback address is never translated.",
                    TextWrapping = TextWrapping.Wrap,
                },
                known,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { withdraw } },
                Field("Seen as", seen),
                Field("is", actual),
                Field("Note", note),
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { close, state },
                },
            },
        };
        Opened += (_, _) =>
        {
            Load();
            seen.Focus();
        };
    }

    /// <summary>Whether a translation was stated or withdrawn while the dialog was open.</summary>
    internal bool Changed { get; private set; }

    /// <summary>The translations in force, as listed.</summary>
    internal IReadOnlyList<WorkspaceAddressTranslation> Listed =>
        [.. (known.ItemsSource as IEnumerable<TranslationRow> ?? []).Select(row => row.Translation)];

    /// <summary>Fills in a translation, as a person would; a test uses it.</summary>
    internal void Enter(string from, string to, string? remark = null)
    {
        seen.Text = from;
        actual.Text = to;
        note.Text = remark;
    }

    /// <summary>Selects a listed translation, as a click does; a test uses it.</summary>
    internal void Select(int index) => known.SelectedIndex = index;

    /// <summary>States the translation entered; says in words why not when it is refused.</summary>
    internal async Task<bool> StateAsync()
    {
        if (busy) return false;
        string from = seen.Text ?? string.Empty;
        string to = actual.Text ?? string.Empty;
        string? remark = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text;
        return await RunAsync(() => InvestigationWorkspace.StateTranslation(path, from, to, remark, DateTimeOffset.UtcNow), clear: true);
    }

    /// <summary>Withdraws the selected translation, kept as a revision.</summary>
    internal async Task<bool> WithdrawSelectedAsync()
    {
        if (busy || known.SelectedItem is not TranslationRow row) return false;
        return await RunAsync(() => InvestigationWorkspace.WithdrawTranslation(path, row.Translation.Seen, row.Translation.Is, DateTimeOffset.UtcNow), clear: false);
    }

    /// <summary>Lists the translations in force; a test calls it where the dialog is not opened.</summary>
    internal void Load()
    {
        try
        {
            known.ItemsSource = InvestigationWorkspace.TranslationsInForce(InvestigationWorkspace.Read(path)).Select(translation => new TranslationRow(translation)).ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            status.Text = "The investigation could not be read: " + exception.Message;
        }
    }

    private async Task<bool> RunAsync(Func<WorkspaceAddressTranslation> operation, bool clear)
    {
        busy = true;
        state.IsEnabled = withdraw.IsEnabled = false;
        try
        {
            WorkspaceAddressTranslation done = await Task.Run(operation);
            Changed = true;
            status.Text = done.Decision == WorkspaceTranslationDecision.Stated
                ? $"Stated: {done.Seen} is {done.Is}. Candidate joins will mirror through it."
                : $"Withdrawn: {done.Seen} and {done.Is} are two endpoints again; the statement is kept in the file.";
            if (clear)
            {
                seen.Text = actual.Text = note.Text = string.Empty;
            }

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
            state.IsEnabled = true;
            withdraw.IsEnabled = known.SelectedItem is TranslationRow;
        }
    }

    private static Grid Field(string label, TextBox box)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*"), ColumnSpacing = 8 };
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 1);
        row.Children.Add(caption);
        row.Children.Add(box);
        return row;
    }
}
