using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The window's pins (ADR-046, R18): it keeps the open session's records from its time scope's start through every
/// release, within the size a person allows, removes a pin, and states the pins standing in its size line - in the words
/// `icat pin` and `icat session` use.
/// </summary>
public sealed class RetentionPinWindowTests
{
    private const long Mebibyte = 1024 * 1024;

    [AvaloniaFact(DisplayName = "I18: the window keeps a session's records from its time scope's start, says so in its size line, and removes the pin")]
    public async Task TheWindowKeepsASessionsRecords()
    {
        // Three sends, at 1, 2 and 3 µs of session time.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 2_000 },
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 3_000 },
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Null(overview.Pinned);
        session.Store.ReleaseSegmentReaders();
        long held = session.Store.Current!.HeldBytes();
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            Button keep = window.GetControl<Button>("KeepRecordsButton");
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            TextBlock growth = window.GetControl<TextBlock>("SessionGrowthText");
            Assert.False(keep.IsEnabled);
            Assert.Equal("Open or record a session first.", ToolTip.GetTip(keep));
            Assert.Null(await window.PinOfferAsync());

            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: overview), forceOverview: true);
            Dispatch();
            Assert.True(keep.IsEnabled);
            Assert.StartsWith("Keep every record from the time scope's start", (string)ToolTip.GetTip(keep)!, StringComparison.Ordinal);

            // With no time scope, a pin would keep from the session's first record; the session holds what its manifest says.
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            MainWindow.PinOffer whole = Assert.IsType<MainWindow.PinOffer>(await window.PinOfferAsync());
            Assert.Equal((1_000L, false, (string?)null, held, 0), (whole.FromNanoseconds, whole.FromScope, whole.ReleasedBefore,
                whole.HeldBytes, whole.Pins.Count));
            Assert.Equal(workspace.TimeBase.Moment(1_000, System.Globalization.CultureInfo.CurrentCulture), whole.Moment);

            // Brushed from 2 µs, it keeps from the brush's start.
            workspace.SelectInterval(new TimeRange(20, 31));
            Dispatch();
            MainWindow.PinOffer offer = Assert.IsType<MainWindow.PinOffer>(await window.PinOfferAsync());
            Assert.Equal((2_000L, true), (offer.FromNanoseconds, offer.FromScope));

            RetentionPin placed = Assert.IsType<RetentionPin>(await window.PlacePinAsync(offer.FromNanoseconds, 64 * Mebibyte, "the second send"));
            Dispatch();
            Assert.Equal((2_000L, 64 * Mebibyte, "the second send"), (placed.FromNanoseconds, placed.AllowanceBytes, placed.Reason));
            Assert.Equal([placed], session.Store.Pins());
            Assert.Equal("Records kept", status.Text);
            Assert.Equal($"Every record read from {offer.Moment} on is kept through every release while the pin stands, and the "
                + "session may hold 64 MiB. A follow keeping a rolling window stops before it holds more.", detail.Text);
            string pinned = RetentionPinText.SizeLine([placed])!;
            Assert.Equal("Pinned from " + SessionTimeText.Seconds(2_000, System.Globalization.CultureInfo.CurrentCulture)
                + " (the second send): every record from then on is kept through every release, and the session may hold 64 MiB.",
                pinned);
            Assert.EndsWith("\n" + pinned, growth.Text, StringComparison.Ordinal);

            // The next projection states the pins as the size line does, and the dialog lists them in icat pin's words.
            Assert.Equal(pinned, SessionOverviewProjector.Project(session.Store).Pinned);
            session.Store.ReleaseSegmentReaders();
            MainWindow.PinOffer again = Assert.IsType<MainWindow.PinOffer>(await window.PinOfferAsync());
            (RetentionPin Pin, string Statement) listed = Assert.Single(again.Pins);
            Assert.Equal((placed, RetentionPinText.Describe(placed, offer.Moment)), listed);

            // A pin that would allow less than the session holds is refused, in the store's words, and nothing changes.
            Assert.Null(await window.PlacePinAsync(offer.FromNanoseconds, 1, "too little"));
            Assert.Equal("No records were pinned", status.Text);
            Assert.StartsWith($"This session holds {ByteSizeText.Of(held)}, and a pin allowing it 1 B would allow less", detail.Text,
                StringComparison.Ordinal);
            Assert.Single(session.Store.Pins());

            // Removed, it keeps nothing, and the size line no longer states it.
            Assert.Equal(placed, await window.RemovePinAsync(placed));
            Dispatch();
            Assert.Equal("Pin removed", status.Text);
            Assert.Equal($"It no longer keeps the records read from {offer.Moment}: the next release that reaches them may give them "
                + "up, unless another pin keeps them.", detail.Text);
            Assert.DoesNotContain("Pinned from", growth.Text, StringComparison.Ordinal);
            Assert.Empty(session.Store.Pins());
            Assert.Null(await window.RemovePinAsync(placed));
            Assert.Equal("That pin was already removed", status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "I18: the window keeps from a release's boundary when its scope began before it, states several pins by the earliest and the least allowed, and says when pins cannot be read")]
    public async Task TheWindowKeepsFromWhatARelease()
    {
        // Sends at 1 and 2 µs, then one at 5 µs; the records read before 3 µs are released, the first send kept as evidence.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 2_000 },
        ]);
        Publish(session.Store,
        [
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 5_000 },
        ]);
        Assert.Equal(2_001L, InterCat.Analysis.IntervalRelease.Release(session.Store, 3_000, "older than the window", DateTimeOffset.UtcNow)
            .Preview.BoundaryNanoseconds);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: overview), forceOverview: true);
            Dispatch();
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            string boundary = workspace.TimeBase.Moment(2_001, System.Globalization.CultureInfo.CurrentCulture);

            // The session's first record is the evidence the release kept: a pin keeps from the boundary, and says why.
            MainWindow.PinOffer offer = Assert.IsType<MainWindow.PinOffer>(await window.PinOfferAsync());
            Assert.Equal((2_001L, boundary, false, boundary), (offer.FromNanoseconds, offer.Moment, offer.FromScope, offer.ReleasedBefore));

            // Two pins: the size line names the earliest, counts the rest, and states the least any allows.
            RetentionPin first = Assert.IsType<RetentionPin>(await window.PlacePinAsync(offer.FromNanoseconds, 64 * Mebibyte, "the first"));
            _ = Assert.IsType<RetentionPin>(await window.PlacePinAsync(5_000, 32 * Mebibyte, "the second"));
            Dispatch();
            string pinned = "Pinned from " + SessionTimeText.Seconds(2_001, System.Globalization.CultureInfo.CurrentCulture)
                + " (the first) and by 1 more pin: every record from then on is kept through every release, and the session may "
                + "hold 32 MiB.";
            Assert.Equal(pinned, RetentionPinText.SizeLine(session.Store.Pins()));
            Assert.EndsWith("\n" + pinned, window.GetControl<TextBlock>("SessionGrowthText").Text, StringComparison.Ordinal);
            Assert.Equal(2, Assert.IsType<MainWindow.PinOffer>(await window.PinOfferAsync()).Pins.Count);
            Assert.Equal(first, (await window.PinOfferAsync())!.Pins[0].Pin);

            // Pins that cannot be read are said, as the size line and the dialog would say them, and nothing is offered.
            File.WriteAllText(Path.Combine(session.Path, RetentionPinsV1.FileName), "{");
            string unreadable = "This session's pins, in retention-pins.json, could not be read: it is not a pins file this InterCat "
                + "can read.";
            Assert.Equal(unreadable + " No release is made until they can be read.", SessionOverviewProjector.Project(session.Store).Pinned);
            session.Store.ReleaseSegmentReaders();
            Assert.Null(await window.PinOfferAsync());
            Assert.Equal(("This session's pins could not be read", unreadable), (window.GetControl<TextBlock>("CaptureStatus").Text,
                window.GetControl<TextBlock>("CaptureDetail").Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "I18: the pins dialog asks why, and how large the session may grow - at least what it holds - and lists each pin standing to remove")]
    public async Task ThePinsDialogAsksWhyAndHowLarge()
    {
        var pin = new RetentionPin
        {
            Id = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000001"), FromNanoseconds = 300_000_000, AllowanceBytes = 8 * Mebibyte,
            PlacedUtc = new DateTimeOffset(2026, 10, 8, 14, 5, 0, TimeSpan.Zero), Reason = "the failover",
        };
        string statement = RetentionPinText.Describe(pin, "0.300 s");
        var offer = new MainWindow.PinOffer(500_000_000, "0.500 s", FromScope: true, ReleasedBefore: null, HeldBytes: (3 * Mebibyte) + 1,
            Pins: [(pin, statement)]);
        var owner = new Window();
        owner.Show();
        try
        {
            // Nothing is kept until a reason is given; the session may hold at least what it holds, in whole MiB.
            Window prompt = MainWindow.PinPrompt(offer);
            Task<MainWindow.PinChoice?> chosen = prompt.ShowDialog<MainWindow.PinChoice?>(owner);
            Dispatch();
            Assert.Equal("Keep this session's records", prompt.Title);
            Assert.Contains(Texts(prompt), text => text == "A pin keeps every record read from 0.500 s on - the start of the time "
                + "scope - through every release, until it is removed.");
            Assert.Contains(Texts(prompt), text => text == $"MiB; it holds {ByteSizeText.Of((3 * Mebibyte) + 1)} now");
            Button keep = Named<Button>(prompt, "PlacePin");
            NumericUpDown allowance = Named<NumericUpDown>(prompt, "PinAllowance");
            TextBox reason = Named<TextBox>(prompt, "PinReason");
            Assert.Equal(("Keep records", false, 4m, 4m), (keep.Content, keep.IsEnabled, allowance.Value, allowance.Minimum));
            reason.Text = "   ";
            Assert.False(keep.IsEnabled);
            reason.Text = "  the burst ";
            Assert.True(keep.IsEnabled);
            allowance.Value = 64;
            keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new MainWindow.PlacePin(500_000_000, 64 * Mebibyte, "the burst"), await chosen);

            // Each pin standing is listed in icat pin's words and can be removed from here.
            Window listing = MainWindow.PinPrompt(offer);
            Task<MainWindow.PinChoice?> removal = listing.ShowDialog<MainWindow.PinChoice?>(owner);
            Dispatch();
            Assert.Contains(Texts(listing), text => text == "Pin 1a2b3c4d " + statement);
            Named<Button>(listing, "RemovePin1a2b3c4d").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new MainWindow.RemovePin(pin), await removal);

            // Without a time scope it keeps from the session's first record, and where a release came first, from there.
            Window first = MainWindow.PinPrompt(offer with { FromScope = false, ReleasedBefore = "0.400 s", Pins = [] });
            Task<MainWindow.PinChoice?> cancelled = first.ShowDialog<MainWindow.PinChoice?>(owner);
            Dispatch();
            Assert.Contains(Texts(first), text => text == "A pin keeps every record read from 0.500 s on - the session's first "
                + "record - through every release, until it is removed. The records read before 0.400 s were released already, so "
                + "it keeps from there.");
            Assert.DoesNotContain(Texts(first), text => text == "PINS STANDING");
            first.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await cancelled);
        }
        finally
        {
            owner.Close();
        }

        static IEnumerable<string?> Texts(Window window) =>
            window.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text);

        static T Named<T>(Window window, string name) where T : Control =>
            window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
