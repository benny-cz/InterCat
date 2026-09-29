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

/// <summary>
/// The confirmation an investigation package needs (§8.4, §11.3): each session, to copy or not - one that is not where it
/// was last found cannot be, and stays a reference - and what the chosen copies and the investigation's file hold and
/// expose, stated again whenever the choice changes. It starts on Cancel, so a reflexive Enter saves nothing.
/// </summary>
internal sealed class InvestigationPackageWindow : Window
{
    private readonly InvestigationPackagePreview preview;
    private readonly Dictionary<Guid, CheckBox> choices = [];
    private readonly StackPanel statements = new() { Spacing = 10 };
    private readonly Button save = new() { Classes = { "primary" } };

    public InvestigationPackageWindow(InvestigationPackagePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        this.preview = preview;
        Title = "Package this investigation with its sessions?";
        Width = 660;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancel, "Cancel: nothing is saved");
        AutomationProperties.SetName(save, "Save the package in a new folder you choose");
        AutomationProperties.SetName(statements, "What the package holds and exposes");
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) => Close(true);
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close(false);
            key.Handled = true;
        };

        var sessions = new StackPanel { Spacing = 4 };
        foreach (InvestigationPackageMemberPreview member in preview.Members)
        {
            var choice = new CheckBox
            {
                IsChecked = member.Selected,
                IsEnabled = member.Measured is not null,
                Content = new TextBlock { Text = Label(member), TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetName(choice, $"Copy session {Short(member.SessionId)}, found in {member.Folder}");
            choice.IsCheckedChanged += (_, _) => Restate();
            choices.Add(member.SessionId, choice);
            sessions.Children.Add(choice);
        }

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Sessions to copy", FontWeight = FontWeight.SemiBold },
                sessions,
                statements,
            },
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, save },
        };

        // Many sessions scroll; the actions stay in view.
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        var scroller = new ScrollViewer { Content = body, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(actions, 1);
        layout.Children.Add(scroller);
        layout.Children.Add(actions);
        Content = layout;
        Restate();
    }

    /// <summary>The sessions chosen to copy, in the investigation's order.</summary>
    internal IReadOnlyList<Guid> Chosen =>
        [.. preview.Members.Where(member => choices[member.SessionId].IsChecked == true).Select(member => member.SessionId)];

    /// <summary>What the dialog states for the sessions chosen now, a paragraph each, the warning last.</summary>
    internal IReadOnlyList<string> Statements { get; private set; } = [];

    /// <summary>Whether saving is offered: only when a session is chosen.</summary>
    internal bool CanSave => save.IsEnabled;

    /// <summary>Chooses a session, or leaves it out, as its box does; a test uses it.</summary>
    internal void Choose(Guid sessionId, bool copied) => choices[sessionId].IsChecked = copied;

    private void Restate()
    {
        InvestigationPackagePreview chosen = InvestigationPackage.Select(preview, Chosen);
        string warning = InvestigationPackage.WarningFor(chosen);
        Statements = InvestigationPackage.Disclosure(chosen, CultureInfo.CurrentCulture);
        statements.Children.Clear();
        foreach (string text in Statements)
        {
            statements.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = text == warning && chosen.Copied.Count > 0 ? FontWeight.SemiBold : FontWeight.Normal,
            });
        }

        save.IsEnabled = chosen.Copied.Count > 0;
        save.Content = chosen.Unredacted ? "Save unredacted package…" : "Save package…";
    }

    /// <summary>A session as its box names it: its folder and identity, and what its copy holds or why there is none.</summary>
    private static string Label(InvestigationPackageMemberPreview member)
    {
        string state = member.Resolution.State.ToString().ToLowerInvariant();
        string name = $"{member.Folder} · session {Short(member.SessionId)}";
        return member.Measured is not { } copy
            ? $"{name} · {state}: it cannot be copied, and stays a reference to relink"
            : $"{name} · {Spoken.Count(copy.Rows, "record")} · {RecentSessions.Size(copy.Bytes, CultureInfo.CurrentCulture)}"
                + (copy.Redacted ? " · a redacted package" : string.Empty)
                + (member.Resolution.State == WorkspaceMemberState.Present
                    ? string.Empty
                    : string.Create(CultureInfo.CurrentCulture, $" · {state}: its copy holds generation {copy.Generation:N0}, not the one selected"));
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];
}
