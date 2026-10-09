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

/// <summary>A session an alignment may take as the investigation's time, as the dialog offers it.</summary>
internal sealed record AlignmentReference(Guid SessionId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Aligns one session of an investigation to its time (ADR-039): exactly, when both captures recorded one boot; by their
/// wall clocks, with how closely they agreed, which only a person can say; or by an instant the person reads in both. Each
/// is a kept revision of the investigation's file, and none changes a session.
/// </summary>
internal sealed class InvestigationAlignWindow : Window
{
    private readonly string path;
    private readonly Guid sessionId;
    private readonly ComboBox reference = new() { MinWidth = 320 };
    private readonly RadioButton byBoot = new() { Content = "By their boot: exact, when both captures recorded one boot", GroupName = "alignment" };
    private readonly RadioButton byWallClock = new() { Content = "By their wall clocks", GroupName = "alignment" };
    private readonly RadioButton byInstant = new() { Content = "By one or two instants I read in both", GroupName = "alignment" };
    private readonly TextBox agreement = new() { Text = "10 ms", Width = 120 };
    private readonly TextBox wallDrift = new() { Text = "50", Width = 120 };
    private readonly TextBox memberAt = new() { Watermark = "seconds", Width = 160 };
    private readonly TextBox referenceAt = new() { Watermark = "seconds", Width = 160 };
    private readonly TextBox within = new() { Text = "1 ms", Width = 120 };
    private readonly TextBox instantDrift = new() { Watermark = "not stated", Width = 120 };
    private readonly TextBox secondMemberAt = new() { Watermark = "optional", Width = 160 };
    private readonly TextBox secondReferenceAt = new() { Watermark = "optional", Width = 160 };
    private readonly TextBox note = new() { Watermark = "optional" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    /// <summary>The alignment in force, said before the form that replaces it; hidden for a session with none.</summary>
    private readonly TextBlock now = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly StackPanel wallClockFields;
    private readonly StackPanel instantFields;
    private readonly Button align = new() { Content = "Align", Classes = { "primary" } };
    private bool aligning;

    public InvestigationAlignWindow(string path, InvestigationMemberRow member, IReadOnlyList<AlignmentReference> references, Guid? timeReference)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(references);
        this.path = path;
        sessionId = member.SessionId;
        string name = member.Title.Split(',')[0];
        Title = $"Align {name}";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        reference.ItemsSource = references;
        reference.SelectedItem = references.FirstOrDefault(choice => choice.SessionId == timeReference)
            ?? (references.Count > 0 ? references[0] : null);
        reference.IsEnabled = references.Count > 1;
        byInstant.IsChecked = true;
        StartFrom(member, references);
        AutomationProperties.SetName(reference, "The session to align to: the investigation's clock, or a session placed in it");
        TrimmedChoices.Apply(reference);
        AutomationProperties.SetHelpText(byBoot, "Align exactly by the boot both captures recorded");
        AutomationProperties.SetHelpText(byWallClock, "Align by the two captures' wall clocks");
        AutomationProperties.SetHelpText(byInstant, "Align by one or two instants read in both sessions");
        AutomationProperties.SetName(agreement, "How closely the two wall clocks agreed, with its unit");
        AutomationProperties.SetName(wallDrift, "How fast the two clocks drift apart at most, in parts per million");
        AutomationProperties.SetName(memberAt, "The instant in this session, in seconds");
        AutomationProperties.SetName(referenceAt, "The same instant in the reference session, in seconds");
        AutomationProperties.SetName(within, "How sure the instant is, as a duration with its unit");
        AutomationProperties.SetName(instantDrift, "How fast the two clocks drift apart at most, in parts per million, if known");
        AutomationProperties.SetName(secondMemberAt, "A second instant in this session, in seconds, to measure the clocks' rate; optional");
        AutomationProperties.SetName(secondReferenceAt, "The same second instant in the reference session, in seconds");
        AutomationProperties.SetName(note, "A note about this alignment");
        AutomationProperties.SetName(status, "Alignment status");
        AutomationProperties.SetName(now, "The alignment in force");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(align, "Align this session");

        wallClockFields = Fields(
            ("The wall clocks agreed within", agreement, "No sample can measure it: say what synchronized them, such as 10 ms for NTP."),
            ("The clocks drift at most (ppm)", wallDrift, "50 ppm bounds a common crystal."));
        instantFields = Fields(
            ($"Its instant ({name}), in seconds", memberAt, "Read it in this session, such as the moment a connection opened."),
            ("is the reference's instant, in seconds", referenceAt, "The same moment, read in the reference session."),
            ("within", within, "How sure you are that each pair is one instant."),
            ("A second instant of it, in seconds", secondMemberAt, "Optional. Well apart from the first, it measures how fast the two clocks run against each other."),
            ("is the reference's second, in seconds", secondReferenceAt, "The same second moment, read in the reference session."),
            ("The clocks drift at most (ppm)", instantDrift, "With a second instant, how far their rate may wander. Leave it empty if unknown: away from the instants, nothing is then ordered."));
        void ShowFields()
        {
            wallClockFields.IsVisible = byWallClock.IsChecked == true;
            instantFields.IsVisible = byInstant.IsChecked == true;
        }

        byBoot.IsCheckedChanged += (_, _) => ShowFields();
        byWallClock.IsCheckedChanged += (_, _) => ShowFields();
        byInstant.IsCheckedChanged += (_, _) => ShowFields();
        ShowFields();

        align.Click += (_, _) => _ = AlignAsync();
        var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancel, "Cancel the alignment");
        cancel.Click += (_, _) => Close(false);
        KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                Close(false);
                key.Handled = true;
            }
            else if (key.Key == Key.Enter)
            {
                // Enter in a field aligns, as Align does; a button or a choice the keyboard is on, and an open list of
                // sessions, take their own Enter first.
                _ = AlignAsync();
                key.Handled = true;
            }
        };

        // The way chosen to align has the keyboard as the dialog opens, which a screen reader says; Tab goes on to what it
        // asks for. The dialog opened with the keyboard on nothing of its own.
        Opened += (_, _) => new[] { byBoot, byWallClock, byInstant }.FirstOrDefault(choice => choice.IsChecked == true)?.Focus(NavigationMethod.Tab);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"Place {name}'s instants in the investigation's time by aligning them to this session's:",
                    TextWrapping = TextWrapping.Wrap,
                },
                now,
                reference,
                byBoot,
                byWallClock,
                wallClockFields,
                byInstant,
                instantFields,
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Note", FontSize = 12 }, note } },
                new TextBlock
                {
                    Text = "An alignment is kept as a revision of the investigation's file and changes no session; a later one "
                        + "replaces it, and withdrawing it is kept too.",
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
                    Children = { cancel, align },
                },
            },
        };
    }

    /// <summary>
    /// A session aligned already opens on how it is aligned - its method, its reference and what was stated - and says the
    /// alignment in force, so aligning it again starts from that alignment, never from a blank form that reads as none.
    /// </summary>
    private void StartFrom(InvestigationMemberRow member, IReadOnlyList<AlignmentReference> references)
    {
        if (member.Alignment is not { } alignment)
        {
            return;
        }

        now.Text = $"Now: {member.Time}. Aligning it again replaces this; withdrawing it is in the investigation's window.";
        now.IsVisible = true;
        if (references.FirstOrDefault(choice => choice.SessionId == alignment.ReferenceSessionId) is { } aligned)
        {
            reference.SelectedItem = aligned;
        }

        Choose(alignment.Mode);
        if (alignment.Mode == WorkspaceAlignmentMode.WallClock)
        {
            agreement.Text = alignment.SynchronizationNanoseconds is { } agreed ? InvestigationInput.WriteDuration(agreed, CultureInfo.CurrentCulture) : agreement.Text;
            wallDrift.Text = alignment.DriftPartsPerMillion is { } drift ? InvestigationInput.WritePartsPerMillion(drift, CultureInfo.CurrentCulture) : wallDrift.Text;
        }
        else if (alignment.Mode == WorkspaceAlignmentMode.Manual)
        {
            memberAt.Text = alignment.SessionNanoseconds is { } at ? InvestigationInput.WriteSeconds(at, CultureInfo.CurrentCulture) : null;
            referenceAt.Text = alignment.ReferenceNanoseconds is { } referenceTime ? InvestigationInput.WriteSeconds(referenceTime, CultureInfo.CurrentCulture) : null;
            within.Text = alignment.WithinNanoseconds is { } bound ? InvestigationInput.WriteDuration(bound, CultureInfo.CurrentCulture) : within.Text;
            secondMemberAt.Text = alignment.SecondSessionNanoseconds is { } secondAt ? InvestigationInput.WriteSeconds(secondAt, CultureInfo.CurrentCulture) : null;
            secondReferenceAt.Text = alignment.SecondReferenceNanoseconds is { } secondReference
                ? InvestigationInput.WriteSeconds(secondReference, CultureInfo.CurrentCulture)
                : null;
            instantDrift.Text = alignment.DriftPartsPerMillion is { } drift ? InvestigationInput.WritePartsPerMillion(drift, CultureInfo.CurrentCulture) : null;
        }
    }

    /// <summary>Chooses how to align, as the radio buttons do; a test sets it directly.</summary>
    internal void Choose(WorkspaceAlignmentMode mode)
    {
        byBoot.IsChecked = mode == WorkspaceAlignmentMode.SameBoot;
        byWallClock.IsChecked = mode == WorkspaceAlignmentMode.WallClock;
        byInstant.IsChecked = mode == WorkspaceAlignmentMode.Manual;
    }

    /// <summary>Aligns as the dialog is filled in, and closes with true; says in words what is missing or refused.</summary>
    internal async Task<bool> AlignAsync()
    {
        if (aligning) return false;
        if (reference.SelectedItem is not AlignmentReference to)
        {
            status.Text = "Choose the session to align to.";
            return false;
        }

        string? remark = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text;
        Func<WorkspaceAlignment>? operation = null;
        if (byBoot.IsChecked == true)
        {
            operation = () => InvestigationWorkspace.AlignSameBoot(path, sessionId, to.SessionId, remark, DateTimeOffset.UtcNow);
        }
        else if (byWallClock.IsChecked == true)
        {
            if (InvestigationInput.Duration(agreement.Text) is not { } agreed)
            {
                status.Text = "Write how closely the wall clocks agreed as a duration with its unit, such as 10 ms.";
                return false;
            }

            if (InvestigationInput.PartsPerMillion(wallDrift.Text) is not { } drift)
            {
                status.Text = "Write how fast the clocks drift apart at most, in parts per million, such as 50.";
                return false;
            }

            operation = () => InvestigationWorkspace.AlignByWallClock(path, sessionId, to.SessionId, agreed, drift, remark, DateTimeOffset.UtcNow);
        }
        else
        {
            if (InvestigationInput.Seconds(memberAt.Text) is not { } at || InvestigationInput.Seconds(referenceAt.Text) is not { } referenceTime)
            {
                status.Text = "Write both instants in seconds of their own session's time, such as 12.5.";
                return false;
            }

            if (InvestigationInput.Duration(within.Text) is not { } bound)
            {
                status.Text = "Write how sure the instant is as a duration with its unit, such as 1 ms.";
                return false;
            }

            (long, long)? second = null;
            bool secondGiven = !string.IsNullOrWhiteSpace(secondMemberAt.Text);
            if (secondGiven || !string.IsNullOrWhiteSpace(secondReferenceAt.Text))
            {
                if (!secondGiven || InvestigationInput.Seconds(secondMemberAt.Text) is not { } secondAt
                    || InvestigationInput.Seconds(secondReferenceAt.Text) is not { } secondReference)
                {
                    status.Text = "Write both second instants in seconds of their own session's time, or leave both empty.";
                    return false;
                }

                second = (secondAt, secondReference);
            }

            double? drift = null;
            if (!string.IsNullOrWhiteSpace(instantDrift.Text))
            {
                drift = InvestigationInput.PartsPerMillion(instantDrift.Text);
                if (drift is null)
                {
                    status.Text = "Write the drift in parts per million, such as 50, or leave it empty.";
                    return false;
                }
            }

            operation = () => InvestigationWorkspace.Align(
                path, sessionId, at, to.SessionId, referenceTime, bound, drift, remark, DateTimeOffset.UtcNow, second);
        }

        aligning = true;
        align.IsEnabled = false;
        status.Text = "Aligning…";
        try
        {
            _ = await Task.Run(operation);
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
            aligning = false;
            align.IsEnabled = true;
        }
    }

    private static StackPanel Fields(params (string Label, TextBox Box, string Hint)[] fields)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };
        foreach ((string label, TextBox box, string hint) in fields)
        {
            // The hint takes what the label and the box leave, and wraps there rather than past the window's edge.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,140,*"), ColumnSpacing = 8 };
            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var explanation = new TextBlock
            {
                Text = hint,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "muted" },
            };
            box.Width = double.NaN;

            // A box keeps its own height beside a hint of several lines, as every other box does.
            box.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(box, 1);
            Grid.SetColumn(explanation, 2);
            row.Children.Add(caption);
            row.Children.Add(box);
            row.Children.Add(explanation);
            panel.Children.Add(row);
        }

        return panel;
    }
}
