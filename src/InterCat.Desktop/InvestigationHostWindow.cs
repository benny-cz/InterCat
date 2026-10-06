using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>Another host identity of an investigation, as the one-host dialog offers it.</summary>
internal sealed record HostChoice(Guid HostId, string Label, bool OneHostNow)
{
    public override string ToString() => Label + (OneHostNow ? " - one host with it now, by your confirmation" : string.Empty);
}

/// <summary>
/// Says whether a session's host is one host with another identity of the investigation (§8.3): equal identities are
/// evidence of one host, and different ones are one only by a person's word - a machine renamed or reinstalled, or a file
/// imported from it. The word is kept as a revision of the investigation's file, can be withdrawn, and changes no session.
/// </summary>
internal sealed class InvestigationHostWindow : Window
{
    private readonly string path;
    private readonly Guid hostId;
    private readonly ComboBox other = new() { MinWidth = 360 };
    private readonly TextBox note = new() { Watermark = "optional, such as: renamed before the second capture" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button confirm = new() { Content = "They are one host", Classes = { "primary" } };
    private readonly Button withdraw = new() { Content = "They are not one host" };
    private bool deciding;

    public InvestigationHostWindow(string path, Guid hostId, string label, IReadOnlyList<HostChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        this.path = path;
        this.hostId = hostId;
        Title = "One host?";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        other.ItemsSource = choices;
        other.SelectedIndex = choices.Count > 0 ? 0 : -1;
        AutomationProperties.SetName(other, "The other host identity");
        TrimmedChoices.Apply(other);
        AutomationProperties.SetName(note, "A note about this confirmation");
        AutomationProperties.SetName(status, "One host status");
        AutomationProperties.SetHelpText(confirm, "Confirm that the two identities are one host");
        AutomationProperties.SetHelpText(withdraw, "Withdraw your confirmation that the two identities are one host");
        other.SelectionChanged += (_, _) => ShowChoice();
        confirm.Click += (_, _) => _ = DecideAsync(WorkspaceHostDecision.Confirmed);
        withdraw.Click += (_, _) => _ = DecideAsync(WorkspaceHostDecision.Withdrawn);
        var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancel, "Cancel: nothing is recorded");
        cancel.Click += (_, _) => Close(false);
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close(false);
            key.Handled = true;
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Is {label} the same machine as another host of this investigation?", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "Equal identities are evidence of one host. Different ones are one host only by your word - a machine "
                        + "renamed or reinstalled, or a file imported from it - and then their captures are compared as one host's: "
                        + "for sessions that ran at once, and for loopback connections between them.",
                    TextWrapping = TextWrapping.Wrap,
                },
                other,
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Note", FontSize = 12 }, note } },
                new TextBlock
                {
                    Text = "Your word is kept as a revision of the investigation's file and changes no session; withdrawing it is "
                        + "kept too.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Classes = { "muted" },
                },
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, withdraw, confirm },
                },
            },
        };
        ShowChoice();
    }

    /// <summary>Chooses the other identity, as the list does; a test uses it.</summary>
    internal void Choose(Guid host) => other.SelectedItem = (other.ItemsSource as IEnumerable<HostChoice>)?.FirstOrDefault(choice => choice.HostId == host);

    /// <summary>Records the decision, and closes with true when it was; says in words why not when it was refused.</summary>
    internal async Task<bool> DecideAsync(WorkspaceHostDecision decision)
    {
        if (deciding || other.SelectedItem is not HostChoice choice) return false;
        deciding = true;
        confirm.IsEnabled = withdraw.IsEnabled = false;
        string? remark = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text;
        try
        {
            _ = await Task.Run(() => decision == WorkspaceHostDecision.Confirmed
                ? InvestigationWorkspace.ConfirmOneHost(path, hostId, choice.HostId, remark, DateTimeOffset.UtcNow)
                : InvestigationWorkspace.WithdrawOneHost(path, hostId, choice.HostId, DateTimeOffset.UtcNow));
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
            deciding = false;
            ShowChoice();
        }
    }

    private void ShowChoice()
    {
        bool oneNow = other.SelectedItem is HostChoice { OneHostNow: true };
        confirm.IsEnabled = other.SelectedItem is HostChoice && !oneNow;
        withdraw.IsEnabled = oneNow;
    }
}
