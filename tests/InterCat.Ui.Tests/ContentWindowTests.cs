using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
        window.Show();
        WaitFor(() => Texts(window).Any(text => text.StartsWith("Checked content-0000000001.icatc", StringComparison.Ordinal)));

        // The facts come first: how much of the message was kept, and which bytes are missing.
        Assert.Contains("Bytes 0 to 7 (8 bytes), cut by the 8-byte record limit", Texts(window));
        Assert.Contains("Bytes 8 to 15 (8 bytes). They were never recorded, and nothing stands in for them", Texts(window));

        // The bytes stay hidden until the person asks.
        Button reveal = Named<Button>(window, "Show the kept bytes");
        Assert.True(reveal.IsEffectivelyVisible && reveal.IsEnabled);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ListBox>(), list => list.IsEffectivelyVisible);
        reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => window.GetVisualDescendants().OfType<ListBox>().Any(list =>
            AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentLine>));
        ListBox hex = Named<ListBox>(window, "Hex view of the chosen bytes");
        Assert.False(reveal.IsEffectivelyVisible);

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
        Named<Button>(window, "Copy the chosen bytes as hex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        Assert.False(Named<Button>(window, "Save the chosen bytes to a file").IsEnabled);
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
        Button toggle = Named<Button>(window, "Switch between this buffer and its part");
        Assert.True(toggle.IsEnabled);
        Assert.Equal("Show its whole part", toggle.Content);
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
        Button part = Named<Button>(gapped, "Switch between this buffer and its part");
        Assert.True(part.IsEnabled);
        Assert.Equal("Show its part, with its gaps", part.Content);
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
        Assert.False(Named<Button>(gapped, "Save the chosen bytes to a file").IsEnabled);
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
        Named<Button>(gapped, "Copy the chosen bytes as hex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        Assert.True(Named<Button>(gapped, "Save the chosen bytes to a file").IsEnabled);
        Assert.Contains("Bytes from", Texts(gapped));
        Assert.Equal("Show its part, with its gaps", part.Content);
        Assert.True(textView.IsVisible);
        Named<TextBox>(gapped, "First byte of the range");
        gapped.Close();
    }

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

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static IEnumerable<string> Lines(ListBox hex) =>
        ((IEnumerable<ContentLine>)hex.ItemsSource!).Select(row => row.Line);
}
