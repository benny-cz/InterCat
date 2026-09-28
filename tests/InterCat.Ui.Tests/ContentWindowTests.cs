using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
            AutomationProperties.GetName(list) == "Hex view of the chosen bytes" && list.ItemsSource is IEnumerable<ContentHexRow>));
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

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static IEnumerable<string> Lines(ListBox hex) =>
        ((IEnumerable<ContentHexRow>)hex.ItemsSource!).Select(row => row.Line);
}
