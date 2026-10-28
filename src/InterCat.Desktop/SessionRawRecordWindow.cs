using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// An exact original-envelope view. Body bytes remain hidden until the user requests a bounded preview. The record is
/// found by its stable raw identity, so it stays reachable while a live capture publishes newer generations. A redacted
/// package's record is its synthetic entry, and the window says so wherever it names the record.
/// </summary>
internal sealed class SessionRawRecordWindow : Window, IDisposable
{
    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly SessionEvidenceRecord selected;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock heading = new()
    {
        Text = "Original journal envelope for one selected normalized fact",
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock disclosure = new()
    {
        Text = "Body bytes may be sensitive. They are hidden until you choose Reveal; the preview is capped "
            + "at 256 bytes, inert hexadecimal, and is not a decoded message or export.",
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap };
    private readonly Button reveal = new() { Content = "Reveal up to 256 retained body bytes", IsEnabled = false };
    private readonly Button close = new() { Content = "Close" };
    private bool loading;
    private bool closed;
    private bool disposed;
    private bool revealed;
    private bool synthetic;

    /// <summary>Why Reveal waits while the record is read.</summary>
    private const string Reading = "The record is being read.";

    /// <param name="synthetic">
    /// Whether the record is a redacted package's synthetic entry, so the window never calls it an original - not while
    /// it is read, nor when it could not be. A record read as one is presented as one whatever the caller said.
    /// </param>
    public SessionRawRecordWindow(string path, Guid expectedSessionId, SessionEvidenceRecord selected,
        bool synthetic = false)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.selected = selected;
        Title = "InterCat · Original journal record";
        Width = 850;
        Height = 620;
        MinWidth = 650;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        status.Text = synthetic ? "Locating the synthetic record…" : "Locating the retained original record…";

        // The record a screen reader lands on is named for it; the buttons are named by what they say. What the window
        // finds is announced as its status line changes, rather than waiting to be read.
        AutomationProperties.SetName(detail, "The original journal record, field by field");
        AutomationProperties.SetName(status, "Original record status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        reveal.Click += (_, _) => LoadAsync(revealBytes: true).Watched();
        Offering.Offer(reveal, Reading);
        close.Click += (_, _) => Close();
        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { reveal, close },
        };
        var header = new StackPanel { Spacing = 5, Children = { heading, status, disclosure } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(detail, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(detail);
        grid.Children.Add(footer);
        Content = grid;
        if (synthetic) PresentAsSynthetic();

        Opened += (_, _) => LoadAsync(revealBytes: false).Watched();
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async Task LoadAsync(bool revealBytes)
    {
        if (loading || closed || (revealBytes && revealed)) return;
        loading = true;
        Offering.Offer(reveal, Reading);
        detail.Text = string.Empty;
        status.Text = revealBytes ? "Reading a bounded byte preview…"
            : synthetic ? "Verifying the synthetic record…" : "Verifying the original journal record…";
        try
        {
            CancellationToken token = lifetime.Token;
            // The row's raw identity is stable across generations, so a live publication since the row was read
            // does not strand it: the current generation's retained journals are searched and checked against it.
            SessionRawRecordDetail result = await Task.Run(() => SessionRawRecordQuery.ReadRetained(
                SharedSessionStores.Open(path, expectedSessionId), expectedSessionId,
                selected, revealBytes, cancellationToken: token), token);
            if (closed) return;
            // A record read as a package's entry is presented as one, found or not, whatever the caller said.
            if (result.Synthetic) PresentAsSynthetic();
            if (!result.Available)
            {
                // Every row of a package has its synthetic entry, checked as the package was saved, so one not found says
                // the package changed since - never that an original was released.
                status.Text = synthetic ? "Synthetic record unavailable in this package"
                    : "Original record unavailable in retained evidence";
                detail.Text = synthetic ? RawRecordText.SyntheticMissing : result.UnavailableReason;
                Offering.Offer(reveal, "The original record is not retained, so it has no body byte to reveal.");
                return;
            }

            revealed = revealBytes;
            status.Text = $"Verified generation {result.Generation:N0} · {result.JournalName}";

            detail.Text = Describe(result);
            Offering.Offer(reveal, revealed ? "The revealed bytes are shown at the end of the record."
                : result.RetainedBodyLength == 0
                    ? "No body bytes were retained; the record's disposition gives the recorded reason."
                    : null);
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight scan without altering evidence.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                status.Text = (synthetic ? "Could not verify the synthetic record: "
                    : "Could not verify the original record: ") + exception.Message;
                Offering.Offer(reveal,
                    "The record could not be verified, so none of its bytes are revealed; the status line says why.");
            }
        }
        finally
        {
            loading = false;
            if (!closed) KeepKeyboard();
        }
    }

    /// <summary>
    /// Names the record a redacted package's synthetic entry wherever the window names it - its title, heading, text and
    /// status - and drops Reveal: a package holds no original record and its entry carries no body, so neither is ever
    /// offered or presented as there.
    /// </summary>
    private void PresentAsSynthetic()
    {
        synthetic = true;
        Title = "InterCat · Synthetic record of a redacted package";
        heading.Text = "Synthetic record of a redacted package";
        disclosure.Text = RawRecordText.SyntheticDisclosure;
        AutomationProperties.SetName(detail, "The synthetic record, field by field");
        AutomationProperties.SetName(status, "Synthetic record status");
        reveal.IsVisible = false;
    }

    /// <summary>
    /// Gives the record's text the keyboard once it is read - or Close, when there is none to read - unless a control of
    /// the window has it: the window opened with it on nothing of its own, and Reveal, which waits disabled while it reads,
    /// let it go, so a screen reader said only the title, and the bytes revealed were reached only by Tab. Its caret reads
    /// the record a line at a time.
    /// </summary>
    private void KeepKeyboard()
    {
        if (FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (string.IsNullOrEmpty(detail.Text) ? close : (Control)detail).Focus(NavigationMethod.Tab);
    }

    /// <summary>The record's facts as icat raw lists them, a line each, and its revealed bytes, or why none are shown.</summary>
    private static string Describe(SessionRawRecordDetail result)
    {
        var lines = new List<string>(RawRecordText.Facts(result).Select(fact => $"{fact.Label}: {fact.Value}"));
        lines.AddRange(result.ExtendedItems.Select(item => "  " + RawRecordText.ExtendedItem(item)));
        if (result.BodyPreview is { } bytes)
        {
            lines.Add("Retained body preview · inert hexadecimal:");
            lines.AddRange(RawRecordText.PreviewRows(bytes).Select(row => "  " + row));
            if (result.BodyPreviewTruncated) lines.Add(RawRecordText.PreviewStopped);
        }
        else if (result.RetainedBodyLength > 0)
            lines.Add("Retained body bytes are hidden. Choose Reveal to inspect a bounded prefix.");
        else
            lines.Add(RawRecordText.NoBody);
        return string.Join(Environment.NewLine, lines);
    }
}
