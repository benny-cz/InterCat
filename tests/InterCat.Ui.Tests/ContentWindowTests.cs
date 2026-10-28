using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The content viewer (§3.7, ADR-036): its facts first, its bytes hidden until the person asks, then the chosen range as
/// inert hexadecimal and, where the source declares text, as that text; content kept without consent is never shown.
/// </summary>
public sealed class ContentWindowTests
{
    /// <summary>InterCat's decoders, as Decode names them where none reads a record.</summary>
    private const string Decoders = "InterCat's decoders, each at its version: InterCat's content fixture decoder "
        + "(intercat-content-fixture, version 1) reads one record's message from InterCat's own content fixture, events 1 and 2 "
        + "of InterCat-Fixture-Content; InterCat's HTTP/1.1 head decoder (http1-head, version 1) reads a whole HTTP/1.1 request "
        + "or response head WinINet's capture kept, events 2001 and 2003, read as its part.";

    [AvaloniaFact(DisplayName = "§3.7: the content viewer states its facts, hides the bytes until asked, then shows the chosen range")]
    public async Task TheViewerShowsBytesOnlyWhenAsked()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8),
        [
            Content(rows[0], Encoding.UTF8.GetBytes("GET /x"), 8),
            Content(rows[1], Encoding.UTF8.GetBytes("0123456789ABCDEF"), 8),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);

        using var window = new SessionContentWindow(session.Path, page.SessionId, page.Records[1]);

        // Until the record's content is read, Copy and Save wait, each saying so on itself.
        Assert.Equal("The record's kept content is being read.", Offers.WhyNot(Labelled(window, "Copy as hex")));
        Assert.Equal("The record's kept content is being read.", Offers.WhyNot(Labelled(window, "Save bytes…")));
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));

        // The facts come first: how much of the message was kept, and which bytes are missing.
        Assert.Contains("Bytes 0 to 7 (8 bytes), cut by the 8-byte record limit", Texts(window));
        Assert.Contains("Bytes 8 to 15 (8 bytes). They were never recorded, and nothing stands in for them", Texts(window));

        // The bytes stay hidden until the person asks, the keyboard on the button that shows them.
        Button reveal = Named<Button>(window, "Show the kept bytes");
        Assert.True(reveal.IsEffectivelyVisible && reveal.IsEnabled);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ListBox>(), list => list.IsEffectivelyVisible);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(reveal, window.FocusManager?.GetFocusedElement());

        // Copy and Save say to show the bytes first; while they are read, those and the button that shows them wait.
        Button copy = Named<Button>(window, "Copy as hex");
        Button save = Named<Button>(window, "Save bytes…");
        Assert.Equal("Show the kept bytes first.", Offers.WhyNot(copy));
        Assert.Equal("Show the kept bytes first.", Offers.WhyNot(save));
        List<(bool, string?)> revealSaid = Offers.Heard(reveal);
        List<(bool, string?)> copySaid = Offers.Heard(copy);
        List<(bool, string?)> saveSaid = Offers.Heard(save);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        WaitFor(() => window.GetVisualDescendants().OfType<ListBox>().Any(list =>
            AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
        ListBox hex = Named<ListBox>(window, "Hex view of the chosen bytes");
        Assert.False(reveal.IsEffectivelyVisible);

        // Shown, the bytes can be copied and saved, each button saying what it does; no decoder reads them, which Decode
        // says, with the decoders InterCat has.
        Assert.Equal("No decoder reads this record's content. " + Decoders, Offers.WhyNot(Labelled(window, "Decode")));
        const string Reading = "The kept bytes are being read and checked.";
        Assert.Equal([(false, Reading)], revealSaid);
        Assert.Equal([(false, Reading), (true, "Copy the chosen bytes as hex")], copySaid);
        Assert.Equal([(false, Reading), (true, "Save the chosen bytes to a file")], saveSaid);
        Assert.Equal("Copy the chosen bytes as hex", Offers.Offered(copy));

        // The keyboard moves from the vanished button to the first line of bytes, where the arrows read on.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Control firstLine = Assert.IsAssignableFrom<Control>(hex.ContainerFromIndex(0));
        Assert.Same(firstLine, window.FocusManager?.GetFocusedElement());
        Assert.Equal(ContentBytesView.Rows("01234567"u8, 0).Single().Line, Lines(hex).Single());

        // The source declares UTF-8, so the same bytes read as text on the view's other tab.
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        SelectableTextBlock text = Named<SelectableTextBlock>(window, "The chosen bytes as the text their source declares");
        Assert.Equal("01234567", text.Text);
        Assert.StartsWith("Chosen: bytes 0 to 7 (8 bytes), of the 8 kept", Named<TextBlock>(window, "Which bytes are shown").Text,
            StringComparison.Ordinal);

        // A typed range chooses the bytes; one outside the kept bytes is refused in words, and the view keeps its bytes.
        TextBox first = Named<TextBox>(window, "First byte of the range");
        TextBox last = Named<TextBox>(window, "Last byte of the range");
        first.Text = "2";
        last.Text = "0x5";
        Named<Button>(window, "Show this range of bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(ContentBytesView.Rows("2345"u8, 2).Single().Line, Lines(hex).Single());
        Assert.Equal("2345", text.Text);

        // Copy takes exactly the chosen bytes, as the hex view shows them.
        string dump = ContentBytesView.Dump("2345"u8, 2);
        Named<Button>(window, "Copy as hex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(dump, await window.Clipboard!.TryGetTextAsync());
        last.Text = "9";
        last.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("The kept bytes are 0 to 7; choose a range within them.", Texts(window));
        Assert.Equal(ContentBytesView.Rows("2345"u8, 2).Single().Line, Lines(hex).Single());

        // Escape closes it, as it closes InterCat's prompts.
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
    }

    [AvaloniaFact(DisplayName = "§3.7: the text view says what it shows otherwise than the bytes hold it - marks, replacement characters and where it stops - in words that agree with each count")]
    public void TheTextSaysWhatItShowsOtherwise()
    {
        using var session = new TemporarySession();

        // One control character, one byte no UTF-8 reads and more text than the view shows; then two of each; then plain text.
        byte[][] messages =
        [
            [0x00, 0xFF, .. Enumerable.Repeat((byte)'a', ContentBytesView.MaximumTextCharacters)],
            [0x01, 0x02, 0xFE, 0xFF, (byte)'b'],
            "plain"u8.ToArray(),
        ];
        ObservationRowV1[] rows = [.. messages.Select((message, index) =>
            Transfer(10 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, message.Length, 100, (ulong)(index + 1))
                .Between("127.0.0.1:50000", "127.0.0.1:8080"))];
        int limit = messages[0].Length;
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: limit), [.. rows.Select((row, index) => Content(row, messages[index], limit))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);

        // What the viewer says of a record's text, its bytes shown and its Text tab chosen.
        List<string> Said(int record)
        {
            using var window = new SessionContentWindow(session.Path, page.SessionId, page.Records[record]);
            window.Show();
            WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-", StringComparison.Ordinal)));
            Named<Button>(window, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => window.GetVisualDescendants().OfType<ListBox>().Any(list =>
                AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
            window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            List<string> said = Texts(window);
            window.Close();
            return said;
        }

        string stops = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{ContentBytesView.MaximumTextCharacters:N0}");
        Assert.Contains("1 control or invisible character is shown as a mark, like ␀ or ⟨U+202E⟩; 1 shows as �, where the bytes are not "
            + $"this encoding - as a range cut mid-character is not; the text stops after {stops} characters.", Said(0));
        Assert.Contains("2 control or invisible characters are shown as marks, like ␀ or ⟨U+202E⟩; 2 show as �, where the bytes are not "
            + "this encoding - as a range cut mid-character is not.", Said(1));
        Assert.Contains("The text exactly as the bytes hold it.", Said(2));
    }

    [AvaloniaFact(DisplayName = "§3.7: Save bytes writes exactly the chosen bytes to the file chosen, nothing when none is, and says why it could not")]
    public async Task SavingWritesTheChosenBytes()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 16),
        [
            Content(rows[0], Encoding.UTF8.GetBytes("GET /x"), 16),
            Content(rows[1], Encoding.UTF8.GetBytes("0123456789ABCDEF"), 16),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        string folder = Directory.CreateDirectory(Path.Combine(session.Path, "..", "saved-" + Guid.NewGuid().ToString("N"))).FullName;
        using var window = new SessionContentWindow(session.Path, page.SessionId, page.Records[1]);
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));
        Named<Button>(window, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Named<Button>(window, "Save bytes…").IsEnabled);
        Named<TextBox>(window, "First byte of the range").Text = "2";
        Named<TextBox>(window, "Last byte of the range").Text = "5";
        Named<Button>(window, "Show this range of bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // None chosen writes nothing and says nothing; the file suggested names the record and the bytes.
        List<string> before = Texts(window);
        choices.Answer(null);
        Named<Button>(window, "Save bytes…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => choices.Asked == 1);
        Assert.Equal("Save the chosen bytes", choices.Saves[0].Title);
        Assert.EndsWith("-bytes-2-5.bin", choices.Saves[0].SuggestedFileName, StringComparison.Ordinal);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, Texts(window));

        // A dialog the system could not show writes nothing either, and the window says why.
        choices.Fail(new InvalidOperationException("No dialog service is available"));
        Named<Button>(window, "Save bytes…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Texts(window).Contains("The system could not show its dialog to choose a file: No dialog service is "
            + "available. Nothing was saved."));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));

        // Chosen, exactly the chosen bytes are written, and the window says which went where.
        string file = Path.Combine(folder, "bytes.bin");
        choices.Answer(file);
        Named<Button>(window, "Save bytes…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Texts(window).Any(text => text.EndsWith($" to {file}.", StringComparison.Ordinal)));
        Assert.Equal("2345"u8.ToArray(), await File.ReadAllBytesAsync(file));
        Assert.Contains(Texts(window), text => text.StartsWith("Saved bytes 2 to 5 (4 bytes) of content-0000000001.icatc to ",
            StringComparison.Ordinal));

        // A file it cannot write is said.
        choices.Answer(folder);
        Named<Button>(window, "Save bytes…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Could not save the bytes: ", StringComparison.Ordinal)));
        Directory.Delete(folder, recursive: true);
    }

    [AvaloniaFact(DisplayName = "§3.7: at its smallest the content viewer shows the chosen bytes ten lines at a time, its facts scrolling above them")]
    public void TheSmallestViewerKeepsRoomForTheBytes()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = [Rows()[0], Rows()[1] with { ByteValue = 2_048 }];
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 4_096), [Content(rows[1], new byte[2_048], 4_096)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);

        using var window = new SessionContentWindow(session.Path, page.SessionId, page.Records[1]);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));
        ScrollViewer facts = Named<ScrollViewer>(window, "What was kept of the record's message, and under which policy");
        TabControl views = window.GetVisualDescendants().OfType<TabControl>().Single();

        // Until the bytes are shown the facts are all there is to read, and keep all the room they need.
        Settle(window);
        Assert.True(double.IsPositiveInfinity(facts.MaxHeight));
        Assert.True(facts.Extent.Height <= facts.Viewport.Height + 0.5);

        // Shown, the bytes keep the tabs and ten lines; the facts give up the rest, scrolled within what they keep, and
        // every one of them is still there to scroll to.
        Named<Button>(window, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => window.GetVisualDescendants().OfType<ListBox>().Any(list =>
            AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
        Settle(window);
        ListBox hex = Named<ListBox>(window, "Hex view of the chosen bytes");
        double line = Assert.IsAssignableFrom<Control>(hex.ContainerFromIndex(0)).Bounds.Height;
        Assert.True(hex.Bounds.Height >= 10 * line, $"The bytes show {hex.Bounds.Height / line:F1} lines.");
        Assert.True(views.Bounds.Height >= SessionContentWindow.MinimumBytesHeight - 0.5, $"The bytes keep {views.Bounds.Height:F0} px.");
        Assert.True(facts.Extent.Height > facts.Viewport.Height + 0.5, "At this size the facts scroll.");
        Assert.True(facts.Bounds.Height >= SessionContentWindow.MinimumFactsHeight - 0.5, $"The facts keep {facts.Bounds.Height:F0} px.");
        Assert.Contains("Stored in", Texts(window));

        // A taller window gives the facts back their whole height, and the bytes the room it has left.
        window.Height = 760;
        Settle(window);
        Assert.True(facts.Extent.Height <= facts.Viewport.Height + 0.5, "A taller window shows every fact.");
        Assert.True(views.Bounds.Height >= SessionContentWindow.MinimumBytesHeight - 0.5);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "ADR-036: content kept without consent to inspect it is stated in the viewer, and never shown")]
    public void ContentWithoutConsentIsNeverShown()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8, inspection: ContentInspectionV1.Disabled),
            [Content(rows[1], Encoding.UTF8.GetBytes("0123456789ABCDEF"), 8)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);

        using var window = new SessionContentWindow(session.Path, page.SessionId, page.Records[1]);
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));

        Assert.False(Named<Button>(window, "Show the kept bytes").IsEffectivelyVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ListBox>(), list => list.IsEffectivelyVisible);
        Assert.Contains(Texts(window), text => text.StartsWith("The capture kept these bytes without consent to inspect them",
            StringComparison.Ordinal));
        const string NotConsented = "These bytes were kept without consent to inspect them, so they are never copied or saved "
            + "one record at a time.";
        Assert.Equal(NotConsented, Offers.WhyNot(Named<Button>(window, "Copy as hex")));
        Assert.Equal(NotConsented, Offers.WhyNot(Named<Button>(window, "Save bytes…")));

        // With nothing to show, Close has the keyboard.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(Named<Button>(window, "Close"), window.FocusManager?.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.7: a buffer of an HTTP part offers its part: whole as one run, or else with its gaps in place, never saved")]
    public async Task APartIsShownWholeOrWithItsGaps()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = [Http(10, 1), Http(11, 2), Http(12, 3), Http(20, 4), Http(22, 6)];
        (long Exchange, long Sequence, long Flags)[] parts = [(7, 0, 1), (7, 1, 0), (7, 2, 2), (8, 0, 1), (8, 2, 2)];
        SourceFieldRowV1[] fields =
        [
            .. parts.SelectMany((part, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, part.Exchange),
                Field(rows[index], SourceField.ContentBufferSequence, part.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, part.Flags),
            }),
        ];
        // Declared text, so the text view is offered for a buffer and a whole part, and not among a part's gaps.
        string[] messages = ["hel", "lo ", "", "ab", ""];
        Publish(session.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8),
            [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 8, ContentEncodingV1.Utf8))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);

        using var window = new SessionContentWindow(session.Path, page.SessionId, page.Records[1]);
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-", StringComparison.Ordinal)));
        Named<Button>(window, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Texts(window).Any(text => text.StartsWith("The response body of exchange 7", StringComparison.Ordinal)));
        Assert.Contains("The response body of exchange 7: 3 buffers, from its first to its last, all kept whole - 6 bytes.", Texts(window));

        // The whole part replaces the buffer in the view, and back.
        Button toggle = Labelled(window, "Show its whole part");
        Assert.Equal(SessionContentWindow.PartToggleDoes, Offers.Offered(toggle));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ListBox hex = Named<ListBox>(window, "Hex view of the chosen bytes");
        Assert.Equal(ContentBytesView.Rows("hello "u8, 0).Single().Line, Lines(hex).Single());
        Assert.StartsWith("Chosen: bytes 0 to 5 (6 bytes), of the 6 bytes of its whole part", Named<TextBlock>(window, "Which bytes are shown").Text,
            StringComparison.Ordinal);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(ContentBytesView.Rows("lo "u8, 0).Single().Line, Lines(hex).Single());
        window.Close();

        // Exchange 8's body lacks its middle buffer: it is named, and its part is offered with its gaps, never whole.
        using var gapped = new SessionContentWindow(session.Path, page.SessionId, page.Records[3]);
        gapped.Show();
        WaitFor(() => Texts(gapped).Any(text => text.StartsWith("Checked content-", StringComparison.Ordinal)));
        Named<Button>(gapped, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Texts(gapped).Any(text => text.Contains("buffer 1 was not recorded", StringComparison.Ordinal)));
        Button part = Labelled(gapped, "Show its part, with its gaps");
        Assert.True(part.IsEnabled);
        part.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Buffer by buffer, each gap a line of its own; chosen by buffer, in hex alone, and never saved as one.
        ListBox lines = Named<ListBox>(gapped, "Hex view of the chosen bytes");
        Assert.Equal(
        [
            "-- Buffer 0, the part's first: 2 bytes, kept whole; bytes 0 to 1 of the part",
            ContentBytesView.Rows("ab"u8, 0).Single().Line,
            "-- Buffer 1 was not recorded: its length is not known, and nothing stands in for it",
            "-- Buffer 2, the part's last: no bytes",
        ], Lines(lines));
        Assert.Contains("Buffers from", Texts(gapped));
        Assert.StartsWith("Chosen: buffers 0 to 2 of the response body of exchange 8, from its first buffer recorded to its last.",
            Named<TextBlock>(gapped, "Which bytes are shown").Text, StringComparison.Ordinal);
        Assert.Equal("A part that is not whole is never saved as one: save a buffer from its own record.",
            Offers.WhyNot(Named<Button>(gapped, "Save bytes…")));
        Assert.Equal("Copy the chosen bytes as hex", Offers.Offered(Named<Button>(gapped, "Copy as hex")));
        TabItem textView = gapped.GetVisualDescendants().OfType<TabItem>().Single(tab => Equals(tab.Header, "Text (UTF-8)"));
        Assert.False(textView.IsVisible);
        Assert.Equal("Show this buffer only", part.Content);

        // The gap's line reads as itself, apart from the bytes: in the caution colour, to a screen reader by its words.
        const string GapWords = "Buffer 1 was not recorded: its length is not known, and nothing stands in for it";
        TextBlock gapLine = lines.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "-- " + GapWords);
        Assert.Contains("caution", gapLine.Classes);
        Assert.Equal(GapWords, AutomationProperties.GetName(gapLine));
        TextBlock heading = lines.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(("Buffer 0, the part's first: 2 bytes, kept whole; bytes 0 to 1 of the part", FontWeight.SemiBold),
            (AutomationProperties.GetName(heading), heading.FontWeight));
        Assert.Equal(ContentBytesView.Rows("ab"u8, 0).Single().Line,
            AutomationProperties.GetName(lines.GetVisualDescendants().OfType<TextBlock>().ElementAt(1)));

        // A typed run of buffers shows those alone; one past the recorded buffers is refused in words, the view kept.
        TextBox from = Named<TextBox>(gapped, "First buffer to show");
        TextBox to = Named<TextBox>(gapped, "Last buffer to show");
        from.Text = "1";
        to.Text = "1";
        Named<Button>(gapped, "Show these buffers").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["-- Buffer 1 was not recorded: its length is not known, and nothing stands in for it"], Lines(lines));

        // The line in the view's first row was a heading; it is now the gap, drawn as a gap, whatever drew the heading.
        TextBlock shownGap = lines.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible);
        Assert.Equal(("-- " + GapWords, true, FontWeight.Normal),
            (shownGap.Text, shownGap.Classes.Contains("caution"), shownGap.FontWeight));
        to.Text = "5";
        to.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("The part's recorded buffers are 0 to 2; choose buffers within them.", Texts(gapped));
        Assert.Single(Lines(lines));

        // Copy takes the lines shown, the gap among them; every buffer comes back with All buffers.
        Named<Button>(gapped, "Copy as hex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("-- Buffer 1 was not recorded: its length is not known, and nothing stands in for it" + Environment.NewLine,
            await gapped.Clipboard!.TryGetTextAsync());
        Named<Button>(gapped, "Show every recorded buffer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, Lines(lines).Count());

        // Back to the buffer: its bytes, chosen by offset again, and saved as they are.
        part.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(ContentBytesView.Rows("ab"u8, 0).Single().Line, Lines(lines).Single());
        Assert.Equal("Save the chosen bytes to a file", Offers.Offered(Named<Button>(gapped, "Save bytes…")));
        Assert.Contains("Bytes from", Texts(gapped));
        Assert.Equal("Show its part, with its gaps", part.Content);
        Assert.True(textView.IsVisible);
        Named<TextBox>(gapped, "First byte of the range");
        gapped.Close();
    }

    [AvaloniaFact(DisplayName = "§11.2: the content viewer decodes a fixture message only when asked, listing each field with the bytes it came from and what it did not decode")]
    public void AFixtureMessageIsDecodedOnlyWhenAsked()
    {
        using var session = new TemporarySession();
        ObservationRowV1 fixture = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 99, 100, 1) with
        {
            ProviderId = System.Diagnostics.Tracing.EventSource.GetGuid(typeof(ContentFixtureEventSource)),
            EventId = ContentFixtureEventSource.MessageSentId,
            Mechanism = Mechanism.ApplicationSdk,
            Layer = ObservationLayer.Application,
            ByteDomain = ByteDomain.ApplicationPayload,
        };
        ObservationRowV1[] rows = [fixture, Rows()[1]];
        const string Message = "InterCat content fixture message 4 on conversation 2. The quick brown fox jumps over the lazy dog. ";
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 64),
        [
            Content(rows[0], Encoding.ASCII.GetBytes(Message), 64, ContentEncodingV1.Binary),
            Content(rows[1], Encoding.UTF8.GetBytes("0123456789ABCDEF"), 64),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord Record(ulong ordinal) => page.Records.Single(record => record.Observation.RawRecordOrdinal == ordinal);

        using var window = new SessionContentWindow(session.Path, page.SessionId, Record(1));
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));
        Assert.Contains(Texts(window), text => text.Contains("nothing is decoded unless you ask", StringComparison.Ordinal));

        // Until its bytes are shown nothing offers to decode them; shown, the decoder that reads them is offered, and
        // nothing is decoded until the person asks.
        Button decode = Named<Button>(window, "Decode the kept bytes");
        Assert.False(decode.IsEffectivelyVisible);
        Named<Button>(window, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => window.GetVisualDescendants().OfType<ListBox>().Any(list =>
            AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
        Assert.True(decode.IsEffectivelyVisible && decode.IsEnabled);
        Assert.StartsWith("Reads the kept bytes with InterCat's content fixture decoder", AutomationProperties.GetHelpText(decode),
            StringComparison.Ordinal);
        TabItem decoded = window.GetVisualDescendants().OfType<TabItem>().Single(tab => Equals(tab.Header, "Decoded"));
        Assert.False(decoded.IsVisible);

        // Asked, it names itself and its version and the bytes it read, lists each field with the bytes it came from - a
        // value read aloud with its field and bytes - and says what it did not decode, on a tab of its own. Decode waits
        // disabled while it decodes, and gives the keyboard to that tab, from where Tab goes on into what it lists.
        Assert.True(decode.Focus(NavigationMethod.Tab));
        string? reads = Offers.Offered(decode);
        List<(bool, string?)> decodeSaid = Offers.Heard(decode);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        WaitFor(() => decoded.IsVisible && decoded.IsSelected);
        Settle(window);
        Assert.Same(decoded, window.FocusManager?.GetFocusedElement());
        Assert.Equal([(false, "The kept bytes are being decoded."), (true, reads)], decodeSaid);
        Assert.Equal("Decoded 3 fields; the Decoded tab lists them and what was not decoded.",
            Named<TextBlock>(window, "Content status").Text);
        ScrollViewer view = Named<ScrollViewer>(window, "What the decoder made of the kept bytes");
        List<string> read =
        [
            .. view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text ?? string.Empty),
        ];
        Assert.Contains("Decoded by InterCat's content fixture decoder, version 1", read);
        Assert.Contains("It read bytes 0 to 63 (64 bytes) of the record's message.", read);
        Assert.Equal(
        [
            "message, bytes 33 to 33 (1 byte): 4",
            "conversation, bytes 51 to 51 (1 byte): 2",
            "filler, bytes 54 to 63 (10 bytes): the fixture's filler sentence, repeated (10 bytes)",
        ], view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(AutomationProperties.GetName));
        Assert.Contains($"Not decoded: The capture kept the first 64 of the message's {Message.Length} bytes; the rest was never "
            + "kept, so it is not decoded.", read);
        Assert.Empty(AccessibilityAuditTests.Unheard(window, out _));
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.True(window.FocusManager?.GetFocusedElement() is Visual inView && view.IsVisualAncestorOf(inView));

        // A field's bytes are a button away: the hex view shows them, chosen as a typed range is, the keyboard on them.
        Named<Button>(window, "Show its bytes: the conversation field").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        ListBox hex = Named<ListBox>(window, "Hex view of the chosen bytes");
        Assert.Equal(0, window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
        Assert.Equal(ContentBytesView.Rows("2"u8, 51).Single().Line, Lines(hex).Single());
        Assert.Equal(("51", "51"), (Named<TextBox>(window, "First byte of the range").Text, Named<TextBox>(window, "Last byte of the range").Text));
        Assert.Equal("Showing the bytes of the conversation field, bytes 51 to 51 (1 byte).", Named<TextBlock>(window, "Content status").Text);
        Assert.Same(hex.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

        // A range chosen while the decoding is in view is shown in the hex view, where its bytes are.
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedItem = decoded;
        Settle(window);
        Named<Button>(window, "All kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(0, window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
        Assert.Equal(4, Lines(hex).Count());
        window.Close();

        // A record no decoder reads offers no decoding, saying why and which decoders InterCat has.
        using var other = new SessionContentWindow(session.Path, page.SessionId, Record(2));
        other.Show();
        WaitFor(() => Texts(other).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));
        Named<Button>(other, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => other.GetVisualDescendants().OfType<ListBox>().Any(list =>
            AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
        Assert.Equal("No decoder reads this record's content. " + Decoders, Offers.WhyNot(Named<Button>(other, "Decode the kept bytes")));
        other.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Copy and Save say why they are not offered: no content kept of the record, no byte of its message, none in it, or none read")]
    public void CopyAndSaveSayWhyTheyAreNotOffered()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            .. Rows(),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 300, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        ];
        (ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes) limited = Content(rows[2], new byte[300], 8);
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 8),
        [
            Content(rows[1], ReadOnlyMemory<byte>.Empty, 8),
            (limited.Fragment with { Disposition = ContentDispositionV1.OmittedBySessionLimit, Kept = 0 }, ReadOnlyMemory<byte>.Empty),
            Content(rows[3], Encoding.UTF8.GetBytes("GET /x"), 8),
        ]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord Record(ulong ordinal) => page.Records.Single(record => record.Observation.RawRecordOrdinal == ordinal);

        // The session keeps no content of the first record; the second's message held no bytes; the session's limit kept
        // no byte of the third's. Each says so on Copy and Save alike.
        Assert.Equal("This record has no kept content in the current generation.",
            WhyNeitherIsOffered(session.Path, page.SessionId, Record(1), "No kept content in the current generation"));
        Assert.Equal("The message held no bytes, so none can be copied or saved.",
            WhyNeitherIsOffered(session.Path, page.SessionId, Record(2), "Checked "));
        Assert.Equal("No byte of this message was kept, so none can be copied or saved.",
            WhyNeitherIsOffered(session.Path, page.SessionId, Record(3), "Checked "));

        // A record whose content could not be read - its folder holds another session - says the status line says why.
        const string Unread = "The record's kept content could not be read; the status line says why.";
        Assert.Equal(Unread, WhyNeitherIsOffered(session.Path, Guid.NewGuid(), Record(2), "Could not read the record's kept content: "));

        // Its bytes not read when asked for - the session gone from its folder since - Show the kept bytes is offered
        // again, and Copy and Save say why they are not.
        using var window = new SessionContentWindow(session.Path, page.SessionId, Record(4));
        window.Show();
        TextBlock status = Named<TextBlock>(window, "Content status");
        WaitFor(() => status.Text?.StartsWith("Checked ", StringComparison.Ordinal) == true);
        session.Store.ReleaseSegmentReaders();
        SharedSessionStores.Open(session.Path).ReleaseSegmentReaders();
        foreach (string file in Directory.EnumerateFiles(session.Path))
        {
            File.Delete(file);
        }

        Button reveal = Named<Button>(window, "Show the kept bytes");
        reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => status.Text?.StartsWith("Could not read the record's kept content: ", StringComparison.Ordinal) == true);
        Assert.Null(Offers.Offered(reveal));
        Assert.Equal(Unread, Offers.WhyNot(Named<Button>(window, "Copy as hex")));
        Assert.Equal(Unread, Offers.WhyNot(Named<Button>(window, "Save bytes…")));
        window.Close();
    }

    /// <summary>
    /// Why the viewer of <paramref name="record"/>, once its status begins <paramref name="status"/>, offers neither Copy nor
    /// Save, which say the same.
    /// </summary>
    [AvaloniaFact(DisplayName = "P2: the content viewer decodes a buffer's whole HTTP head only when asked, shows a field's bytes in the part, and never decodes a head that is not whole")]
    public void AWholeHttpHeadIsDecodedAsItsPart()
    {
        // Exchange 7's request head in two buffers and its response body; exchange 8's request head, a buffer never recorded.
        using var session = new TemporarySession();
        ObservationRowV1[] rows = [Head(10, 1), Head(11, 2), Head(20, 3), Head(21, 4), Http(30, 5)];
        (int Row, long Exchange, long Sequence, long Flags)[] parts = [(0, 7, 0, 1), (1, 7, 1, 2), (2, 8, 0, 1), (3, 8, 2, 2), (4, 7, 0, 3)];
        string[] messages = ["GET /index.html HTTP/1.1\r\nHo", "st: example\r\n\r\n", "GET /a HTTP/1.1\r\n", "\r\n", "hello"];
        Publish(session.Store, rows,
            fields: [.. parts.SelectMany(part => new[]
            {
                Field(rows[part.Row], SourceField.HttpExchangeId, part.Exchange),
                Field(rows[part.Row], SourceField.ContentBufferSequence, part.Sequence),
                Field(rows[part.Row], SourceField.ContentBufferFlags, part.Flags),
            })],
            content: (ContentHeader(recordLimit: 64),
                [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 64, ContentEncodingV1.Binary))]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord Record(ulong ordinal) => page.Records.Single(record => record.Observation.RawRecordOrdinal == ordinal);
        SessionContentWindow Shown(ulong ordinal)
        {
            var opened = new SessionContentWindow(session.Path, page.SessionId, Record(ordinal));
            opened.Show();
            WaitFor(() => Texts(opened).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));
            Named<Button>(opened, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => opened.GetVisualDescendants().OfType<ListBox>().Any(list =>
                AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
            return opened;
        }

        // Shown, a head's buffer offers its part's decoding, not its own: the decoder reads the head whole.
        using SessionContentWindow window = Shown(2);
        Button decode = Labelled(window, "Decode its part");
        Assert.True(decode.IsEffectivelyVisible && decode.IsEnabled);
        Assert.Equal("Decode its part, read whole", AutomationProperties.GetName(decode));
        Assert.StartsWith("Reads the request head of exchange 7 whole with InterCat's HTTP/1.1 head decoder", Offers.Offered(decode),
            StringComparison.Ordinal);
        TabItem decoded = window.GetVisualDescendants().OfType<TabItem>().Single(tab => Equals(tab.Header, "Decoded"));
        Assert.False(decoded.IsVisible);

        // Asked, it lists the head's fields with their bytes in the part, a field across the two buffers whole.
        decode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => decoded.IsVisible && decoded.IsSelected);
        Settle(window);
        Assert.Equal("Decoded 4 fields; the Decoded tab lists them.", Named<TextBlock>(window, "Content status").Text);
        ScrollViewer view = Named<ScrollViewer>(window, "What the decoder made of the kept bytes");
        Assert.Contains("It read bytes 0 to 42 (43 bytes) of the request head of exchange 7.", Texts(window));
        Assert.Equal(
        [
            "method, bytes 0 to 2 (3 bytes): GET",
            "target, bytes 4 to 14 (11 bytes): /index.html",
            "version, bytes 16 to 23 (8 bytes): HTTP/1.1",
            "Host, bytes 26 to 38 (13 bytes): example",
        ], view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(AutomationProperties.GetName));
        Assert.Empty(AccessibilityAuditTests.Unheard(window, out _));

        // A field's bytes are in the part, which the view then shows, and keeps showing for the next field.
        Named<Button>(window, "Show its bytes: the Host field").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        ListBox hex = Named<ListBox>(window, "Hex view of the chosen bytes");
        Assert.Equal("Show this buffer only", Labelled(window, "Show this buffer only").Content);
        Assert.Equal(ContentBytesView.Rows("Host: example"u8, 26).Select(row => row.Line), Lines(hex));
        Assert.Equal("Showing the bytes of the Host field, bytes 26 to 38 (13 bytes) of the request head of exchange 7.",
            Named<TextBlock>(window, "Content status").Text);
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedItem = decoded;
        Settle(window);
        Named<Button>(window, "Show its bytes: the target field").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal(("4", "14"), (Named<TextBox>(window, "First byte of the range").Text, Named<TextBox>(window, "Last byte of the range").Text));
        Assert.Equal(ContentBytesView.Rows("/index.html"u8, 4).Select(row => row.Line), Lines(hex));
        window.Close();

        // A head that is not whole is never decoded, which Decode its part says; asked even so, nothing is decoded.
        using SessionContentWindow gapped = Shown(3);
        Button refused = Labelled(gapped, "Decode its part");
        Assert.True(refused.IsEffectivelyVisible);
        Assert.False(refused.IsEnabled);
        Assert.Equal("The request head of exchange 8 is not whole, so it is never decoded: what it lacks could hold any of its lines.",
            Offers.WhyNot(refused));
        refused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(gapped);
        Assert.False(gapped.GetVisualDescendants().OfType<TabItem>().Single(tab => Equals(tab.Header, "Decoded")).IsVisible);
        gapped.Close();

        // A body's buffer offers no decoding, saying why and which decoders InterCat has.
        using SessionContentWindow body = Shown(5);
        Button undecoded = Labelled(body, "Decode");
        Assert.True(undecoded.IsEffectivelyVisible);
        Assert.Equal("No decoder reads the response body of exchange 7: InterCat decodes an HTTP request or response head, never "
            + "a body. " + Decoders, Offers.WhyNot(undecoded));
        body.Close();

        static ObservationRowV1 Head(long ticks, ulong ordinal) =>
            Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 1, null, ordinal) with
            {
                Mechanism = Mechanism.Http,
                Layer = ObservationLayer.Application,
                EventId = 2001,
                HeaderProcessId = 4_242,
                Direction = Direction.Outbound,
                ByteDomain = ByteDomain.ApplicationPayload,
            };
    }

    private static string? WhyNeitherIsOffered(string path, Guid sessionId, SessionEvidenceRecord record, string status)
    {
        using var window = new SessionContentWindow(path, sessionId, record);
        window.Show();
        WaitFor(() => Named<TextBlock>(window, "Content status").Text?.StartsWith(status, StringComparison.Ordinal) == true);
        string? why = Offers.WhyNot(Named<Button>(window, "Copy as hex"));
        Assert.Equal(why, Offers.WhyNot(Named<Button>(window, "Save bytes…")));
        window.Close();
        return why;
    }

    /// <summary>The window's button labelled <paramref name="label"/>, shown or not yet.</summary>
    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    /// <summary>A WinINet response body record of process 4242, whose payload names no owner.</summary>
    private static ObservationRowV1 Http(long ticks, ulong ordinal) =>
        Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = 2004,
            HeaderProcessId = 4_242,
            Direction = Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        };

    private static ObservationRowV1[] Rows() =>
    [
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 16, 100, 2).Between("127.0.0.1:50000", "127.0.0.1:8080"),
    ];

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }

    private static List<string> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)
            .Select(text => text.Text ?? string.Empty),
    ];

    /// <summary>Lays the window out and draws it until what it fits to its size has settled.</summary>
    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _ = window.CaptureRenderedFrame();
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The one control of its kind named <paramref name="name"/>: given that name, or heard by it, as a button is by the
    /// label it shows.
    /// </summary>
    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static IEnumerable<string> Lines(ListBox hex) =>
        ((IEnumerable<ContentLine>)hex.ItemsSource!).Select(row => row.Line);
}
