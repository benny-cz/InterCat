using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// The window's pins (ADR-046, R18): keeping the open session's records from its time scope's start through every
/// release, within the size a person allows, removing a pin, and the size line's account of the pins standing - in the
/// words `icat pin` and `icat session` use.
/// </summary>
public partial class MainWindow
{
    private const long Mebibyte = 1024 * 1024;

    /// <summary>A pin is being placed or removed, so a second is not started behind it.</summary>
    private bool pinning;

    /// <summary>What a person chose in the pins dialog: a pin to place, or one standing to remove.</summary>
    internal abstract record PinChoice;

    /// <summary>Keep every record read from <paramref name="FromNanoseconds"/> on, letting the session hold <paramref name="AllowanceBytes"/>.</summary>
    internal sealed record PlacePin(long FromNanoseconds, long AllowanceBytes, string Reason) : PinChoice;

    /// <summary>Remove <paramref name="Pin"/>, which then keeps nothing.</summary>
    internal sealed record RemovePin(RetentionPin Pin) : PinChoice;

    /// <summary>
    /// What the pins dialog offers: the moment a pin would keep every record from, said in the view's time base, whether
    /// it is the time scope's start, the latest release's boundary when the scope began before it, what the session holds,
    /// and the pins standing with their moments said in the same time base.
    /// </summary>
    internal sealed record PinOffer(
        long FromNanoseconds,
        string Moment,
        bool FromScope,
        string? ReleasedBefore,
        long HeldBytes,
        IReadOnlyList<(RetentionPin Pin, string Statement)> Pins);

    private async void KeepRecords(object? sender, RoutedEventArgs eventArgs)
    {
        if (pinning || await PinOfferAsync() is not { } offer || closed)
        {
            return;
        }

        PinChoice? choice = await PinPrompt(offer).ShowDialog<PinChoice?>(this);
        if (closed)
        {
            return;
        }

        if (choice is PlacePin place)
        {
            _ = await PlacePinAsync(place.FromNanoseconds, place.AllowanceBytes, place.Reason);
        }
        else if (choice is RemovePin remove)
        {
            _ = await RemovePinAsync(remove.Pin);
        }
    }

    /// <summary>
    /// What the pins dialog offers for the open session: from its time scope's start, or its first record without one, but
    /// never before what a release already gave up. Null when no session is open, and, with why stated, when its pins or
    /// its generation cannot be read.
    /// </summary>
    internal async Task<PinOffer?> PinOfferAsync()
    {
        if (currentSessionPath is not { } path || displayedOverview is not { } overview
            || (workspace.ScopeInterval ?? overview.Extent) is not { } scope)
        {
            return null;
        }

        SessionClock clock = workspace.TimeBase;
        try
        {
            (IReadOnlyList<RetentionPin> pins, long held, long? released) = await Task.Run(() =>
            {
                SessionStore store = SharedSessionStores.Open(path, overview.SessionId);
                using EvidenceLease lease = store.AcquireLease();
                return (store.Pins(), lease.Manifest.HeldBytes(),
                    lease.Manifest.LatestRelease(RetentionExtentKind.Interval)?.Record.Interval?.BoundaryNanoseconds);
            });
            // A scope from the timeline's start - the first tick wholly after the latest release's boundary - or before it
            // keeps from the boundary itself, every record the session keeps, and the dialog says why it begins there.
            long? fromBoundary = released is { } boundary && scope.StartTicks <= (boundary + 99) / 100 ? boundary : null;
            long from = fromBoundary ?? checked(scope.StartTicks * 100);
            return new(
                from,
                clock.Moment(from, CultureInfo.CurrentCulture),
                workspace.ScopeInterval is not null,
                fromBoundary is { } kept ? clock.Moment(kept, CultureInfo.CurrentCulture) : null,
                held,
                [.. pins.Select(pin => (pin, RetentionPinText.Describe(pin, clock.Moment(pin.FromNanoseconds, CultureInfo.CurrentCulture))))]);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            CaptureStatus.Text = "This session's pins could not be read";
            CaptureDetail.Text = exception.Message;
            return null;
        }
    }

    /// <summary>
    /// Keeps every record of the open session read from <paramref name="fromNanoseconds"/> on through every release, and
    /// says so where the capture's status is, and in the size line; a refusal is said there instead. Returns the pin.
    /// </summary>
    internal async Task<RetentionPin?> PlacePinAsync(long fromNanoseconds, long allowanceBytes, string reason)
    {
        if (currentSessionPath is not { } path || displayedOverview is not { } overview || pinning)
        {
            return null;
        }

        pinning = true;
        UpdateEvidenceAction();
        try
        {
            (RetentionPin placed, IReadOnlyList<RetentionPin> pins) = await Task.Run(() =>
            {
                SessionStore store = SharedSessionStores.Open(path, overview.SessionId);
                RetentionPin pin = store.Pin(fromNanoseconds, allowanceBytes, reason, DateTimeOffset.UtcNow);
                return (pin, store.Pins());
            });
            CaptureStatus.Text = "Records kept";
            CaptureDetail.Text = $"Every record read from {workspace.TimeBase.Moment(placed.FromNanoseconds, CultureInfo.CurrentCulture)} "
                + "on is kept through every release while the pin stands, and the session may hold "
                + $"{ByteSizeText.Of(placed.AllowanceBytes)}. A follow keeping a rolling window stops before it holds more.";
            ShowPins(pins);
            return placed;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            CaptureStatus.Text = "No records were pinned";
            CaptureDetail.Text = exception.Message;
            return null;
        }
        finally
        {
            pinning = false;
            UpdateEvidenceAction();
        }
    }

    /// <summary>Removes <paramref name="pin"/> from the open session, which then keeps nothing, and says so. Returns it.</summary>
    internal async Task<RetentionPin?> RemovePinAsync(RetentionPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (currentSessionPath is not { } path || displayedOverview is not { } overview || pinning)
        {
            return null;
        }

        pinning = true;
        UpdateEvidenceAction();
        try
        {
            (RetentionPin? removed, IReadOnlyList<RetentionPin> pins) = await Task.Run(() =>
            {
                SessionStore store = SharedSessionStores.Open(path, overview.SessionId);
                return (store.Unpin(pin.Id), store.Pins());
            });
            CaptureStatus.Text = removed is null ? "That pin was already removed" : "Pin removed";
            CaptureDetail.Text = removed is null
                ? "Another command removed it meanwhile."
                : $"It no longer keeps the records read from {workspace.TimeBase.Moment(pin.FromNanoseconds, CultureInfo.CurrentCulture)}: "
                    + "the next release that reaches them may give them up, unless another pin keeps them.";
            ShowPins(pins);
            return removed;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            CaptureStatus.Text = "No pin was removed";
            CaptureDetail.Text = exception.Message;
            return null;
        }
        finally
        {
            pinning = false;
            UpdateEvidenceAction();
        }
    }

    /// <summary>States the pins now standing in the size line, until the session's next generation states them again.</summary>
    private void ShowPins(IReadOnlyList<RetentionPin> pins)
    {
        if (growth is ({ } size, var began, var retained, _, _))
        {
            growth = (size, began, retained, RetentionPinText.SizeLine(pins), pins);
            UpdateSessionGrowth();
        }
    }

    /// <summary>Offers to keep records while a session is shown, and says why not while none is or it is still opening.</summary>
    private void UpdatePinAction(bool session)
    {
        KeepRecordsButton.IsEnabled = session && !openingSession && !pinning;
        ToolTip.SetTip(KeepRecordsButton, KeepRecordsButton.IsEnabled
            ? "Keep every record from the time scope's start - or the session's first, without one - through every release, "
                + "within the size you allow; or remove a pin."
            : pinning ? "A pin is being placed or removed."
            : session ? "Wait for the session to finish opening."
            : "Open or record a session first.");
    }

    /// <summary>
    /// Asks what to keep: every record from the offered moment on, why, and how large the session may grow meanwhile -
    /// at least what it holds - with the pins standing listed, each removable. Closes with a <see cref="PinChoice"/>, or
    /// null when cancelled.
    /// </summary>
    internal static Window PinPrompt(PinOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var prompt = new Window
        {
            Title = "Keep this session's records", Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        decimal least = Math.Max(1, (offer.HeldBytes + Mebibyte - 1) / Mebibyte);
        var reason = new TextBox
        {
            Name = "PinReason", MaxLength = RetentionPin.MaximumReasonLength,
            Watermark = "Why keep them, such as the failover at 14:03",
        };
        AutomationProperties.SetName(reason, "Why keep these records");
        var allowance = new NumericUpDown
        {
            Name = "PinAllowance", Minimum = least, Maximum = 1L << 30, Value = least, Increment = 64, FormatString = "N0",
            Width = 160,
        };
        AutomationProperties.SetName(allowance, "Let the session hold up to, in MiB");
        var cancel = new Button { Content = "Cancel" };
        var keep = new Button { Name = "PlacePin", Content = "Keep records", IsEnabled = false };
        reason.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
            {
                keep.IsEnabled = !string.IsNullOrWhiteSpace(reason.Text);
            }
        };
        cancel.Click += (_, _) => prompt.Close(null);
        keep.Click += (_, _) => prompt.Close(new PlacePin(offer.FromNanoseconds,
            checked((long)(allowance.Value ?? least) * Mebibyte), reason.Text!.Trim()));
        prompt.Opened += (_, _) => reason.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(null);
                key.Handled = true;
            }
        };

        var standing = new StackPanel { Spacing = 6, IsVisible = offer.Pins.Count > 0 };
        standing.Children.Add(new TextBlock { Text = "PINS STANDING", Classes = { "eyebrow" } });
        foreach ((RetentionPin pin, string statement) in offer.Pins)
        {
            var remove = new Button { Name = "RemovePin" + pin.ShortId, Content = "Remove", VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetHelpText(remove, "Remove the pin that " + statement);
            remove.Click += (_, _) => prompt.Close(new RemovePin(pin));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(Paragraph("Pin " + pin.ShortId + " " + statement));
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            standing.Children.Add(row);
        }

        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                Paragraph($"A pin keeps every record read from {offer.Moment} on - "
                    + (offer.FromScope ? "the start of the time scope" : "the session's first record")
                    + " - through every release, until it is removed."
                    + (offer.ReleasedBefore is { } released
                        ? $" The records read before {released} were released already, so it keeps from there."
                        : string.Empty)),
                Paragraph("A release asked for past it stops there, and none by record number is made. A follow keeping a "
                    + "rolling window keeps the session past it, and stops once the session holds more than the pin allows."),
                reason,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Let the session hold up to", VerticalAlignment = VerticalAlignment.Center },
                        allowance,
                        new TextBlock
                        {
                            Text = $"MiB; it holds {ByteSizeText.Of(offer.HeldBytes)} now", VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                standing,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, keep },
                },
            },
        };
        return prompt;
    }
}
