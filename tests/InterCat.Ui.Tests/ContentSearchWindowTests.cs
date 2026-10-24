using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Find in kept content (`contracts/content-v1.md` §4): the window's search of the content a session kept with consent to
/// inspect it lists the records holding a text, without a byte of them unless asked, says what it did not search, and
/// opens a record found in the content viewer.
/// </summary>
public sealed class ContentSearchWindowTests
{
    private static readonly SessionClock Clock = SessionClock.Session(TimeZoneInfo.Utc);

    [AvaloniaFact(DisplayName = "P2: Find in kept content lists the records holding a text without a byte of them unless asked, and a record found opens in the content viewer")]
    public async Task ARecordFoundOpensInTheContentViewer()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            using var window = new ContentSearchWindow(session.Path, session.Store.SessionId, 4_242, Clock);
            _ = window.ShowDialog(owner);
            await Until(() => window.FocusManager?.GetFocusedElement() == Box(window));
            Assert.Equal("Type the text to find first.", Offers.WhyNot(Labelled(window, "Find")));
            Assert.Equal("Find a text first; a record holding it can then be inspected.", Offers.WhyNot(Labelled(window, "Inspect its content")));

            // Enter in the text finds it: the records holding it, in reading order, how often and where first, and no byte.
            Box(window).Text = "example.com";
            Box(window).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => Status(window).Contains("hold it", StringComparison.Ordinal));
            Assert.Equal("2 records hold it. Searched the kept bytes of 3 records (89 B).", Status(window));
            ContentSearchRow[] rows = Rows(window);
            Assert.Equal(["+0.000001 s · PID 4242 · TCP event 10", "+0.000002 s · PID 4242 · TCP event 10"], rows.Select(row => row.Title));
            Assert.Equal(["Holds it once, first at byte 22", "Holds it 2 times, none overlapping, first at byte 0"], rows.Select(row => row.Detail));
            Assert.All(rows, row => Assert.Null(row.Preview));
            Assert.DoesNotContain(Texts(window), line => line.Contains('⟦', StringComparison.Ordinal));
            await Until(() => window.FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: ContentSearchRow first } && first == rows[0]);

            // Asked for, the bytes around each first match are shown, inert; letters match in either case; the selected
            // process's records keep to it.
            Check(window, "Show the bytes around each match");
            Check(window, "Letters A to Z in either case");
            Check(window, "Only the records of PID 4242");
            Box(window).Text = "EXAMPLE.COM";
            Labelled(window, "Find").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(window).Contains("hold it", StringComparison.Ordinal) && Rows(window).Length > 0);
            rows = Rows(window);
            Assert.Equal(2, rows.Length);
            Assert.Equal("HTTP/1.1\\x0D\\x0AHost: ⟦example.com⟧\\x0D\\x0A\\x0D\\x0A", rows[0].Preview);
            Assert.Contains("HTTP/1.1\\x0D\\x0AHost: ⟦example.com⟧\\x0D\\x0A\\x0D\\x0A", Texts(window));
            Assert.StartsWith("+0.000001 s · PID 4242 · TCP event 10: Holds it once, first at byte 22. Around its first match: ",
                rows[0].AccessibleName, StringComparison.Ordinal);

            // Enter on a record found opens its kept content, its bytes hidden until asked there.
            ListBox list = window.GetLogicalDescendants().OfType<ListBox>().Single();
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => window.OwnedWindows.OfType<SessionContentWindow>().Any());
            SessionContentWindow viewer = window.OwnedWindows.OfType<SessionContentWindow>().Single();
            await Until(() => viewer.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Content, "Show the kept bytes")
                && button.IsEffectivelyVisible && button.IsEffectivelyEnabled));
            viewer.Close();
            await Until(() => window.OwnedWindows.Count == 0);

            // A text no record holds says so, and nothing is offered to inspect.
            Box(window).Text = "absent";
            Labelled(window, "Find").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(window).StartsWith("No record holds it", StringComparison.Ordinal));
            Assert.Equal("No record holding the text is listed.", Offers.WhyNot(Labelled(window, "Inspect its content")));
            window.Close();
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact(DisplayName = "P2: Find in kept content never searches bytes kept without consent and says how many it left, says when more hold it than it lists and when none is kept, and with no process selected offers no narrowing")]
    public async Task ContentKeptWithoutConsentIsNotSearched()
    {
        using var session = Session(ContentInspectionV1.Disabled);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            using var window = new ContentSearchWindow(session.Path, session.Store.SessionId, null, Clock);
            _ = window.ShowDialog(owner);
            CheckBox only = window.GetLogicalDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "Only the selected process's records"));
            Assert.False(only.IsEnabled);
            Assert.Equal("Select a process in InterCat's window first: its records are then searched alone.", Offers.WhyNot(only));
            Box(window).Text = "example.com";
            Labelled(window, "Find").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(window).StartsWith("No record holds it", StringComparison.Ordinal));
            Assert.Equal("No record holds it. Searched the kept bytes of 0 records (0 B); 3 records kept without consent to "
                + "inspect their bytes were not searched.", Status(window));
            window.Close();

            // More records holding it than the window lists say so; a session keeping no content says that instead.
            using var many = new TemporarySession();
            ObservationRowV1[] records = [.. Enumerable.Range(1, 51).Select(index => Record(index, (ulong)index, 4_242))];
            Publish(many.Store, records, content: (ContentHeader(64),
                [.. records.Select(record => Content(record, Encoding.ASCII.GetBytes("example.com"), 64, ContentEncodingV1.Binary))]));
            many.Store.ReleaseSegmentReaders();
            Assert.Equal("More than 50 records hold it; the first 50 are listed. Searched the kept bytes of 51 records (561 B).",
                await Searched(owner, many, "example.com"));
            using var none = new TemporarySession();
            Publish(none.Store, [Record(10, 1, 4_242)]);
            none.Store.ReleaseSegmentReaders();
            Assert.Equal("The session keeps no content, so there was nothing to search.", await Searched(owner, none, "example.com"));
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>What the window says once it has searched <paramref name="session"/> for <paramref name="text"/>.</summary>
    private static async Task<string> Searched(Window owner, TemporarySession session, string text)
    {
        using var window = new ContentSearchWindow(session.Path, session.Store.SessionId, null, Clock);
        _ = window.ShowDialog(owner);
        Box(window).Text = text;
        Labelled(window, "Find").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !Status(window).StartsWith("Searching", StringComparison.Ordinal)
            && !Status(window).StartsWith("Type", StringComparison.Ordinal));
        string said = Status(window);
        window.Close();
        return said;
    }

    [AvaloniaFact(DisplayName = "§6.7: Find in kept content opens from its button and from Ctrl+Shift+F, and F1's sheet lists the key")]
    public async Task TheSearchOpensFromTheMainWindow()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        var window = new MainWindow { Width = 1_280, Height = 800 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)));
            Dispatch();
            Button button = window.GetControl<Button>("FindContentButton");
            await Until(() => button.IsEffectivelyEnabled);
            Assert.StartsWith("Find a text in the content this session kept with consent to inspect it", ToolTip.GetTip(button) as string,
                StringComparison.Ordinal);
            Assert.Equal("Find a text in the session's kept content", AutomationProperties.GetHelpText(button));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.OfType<ContentSearchWindow>().Any());
            window.OwnedWindows.OfType<ContentSearchWindow>().Single().Close();
            await Until(() => window.OwnedWindows.Count == 0);

            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);
            await Until(() => window.OwnedWindows.OfType<ContentSearchWindow>().Any());
            window.OwnedWindows.OfType<ContentSearchWindow>().Single().Close();
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Contains(WindowKeys.All, key => (key.Where, key.Keys) == ("Anywhere", "Ctrl+Shift+F"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the content search window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var problems = new List<string>();
        try
        {
            using var window = new ContentSearchWindow(session.Path, session.Store.SessionId, 4_242, Clock);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            _ = window.ShowDialog(owner);
            Dispatch();
            Look("before a search");
            Check(window, "Show the bytes around each match");
            Box(window).Text = "example.com";
            Labelled(window, "Find").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(window).Contains("hold it", StringComparison.Ordinal) && Rows(window).Length > 0);
            Look("with records found and their bytes shown");
            window.Close();

            void Look(string where)
            {
                Dispatch();
                problems.AddRange(LegibleTextTests.CutOff(window, where));
                problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"{where}: {problem}"));
            }
        }
        finally
        {
            owner.Close();
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Process 4242's head naming the host and its record naming it twice; process 5151's record naming it capitalised;
    /// each kept up to 64 bytes under <paramref name="inspection"/>.
    /// </summary>
    private static TemporarySession Session(ContentInspectionV1 inspection)
    {
        ObservationRowV1[] rows =
        [
            Record(10, 1, 4_242), Record(20, 2, 4_242), Record(30, 3, 5_151),
        ];
        string[] messages = ["GET / HTTP/1.1\r\nHost: example.com\r\n\r\n", "example.com, example.com", "nothing here but EXAMPLE.COM"];
        var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1_000_000), .. rows], content: (ContentHeader(64, inspection),
            [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 64, ContentEncodingV1.Binary))]));
        session.Store.ReleaseSegmentReaders();
        return session;
    }

    private static ObservationRowV1 Record(long ticks, ulong ordinal, int process) =>
        Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 10, process, ordinal) with { SessionRelativeTicks = ticks * 100 };

    private static TextBox Box(Window window) => window.GetLogicalDescendants().OfType<TextBox>().Single();

    private static void Check(Window window, string label)
    {
        CheckBox box = window.GetLogicalDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, label));
        Assert.True(box.IsEnabled, label);
        box.IsChecked = true;
    }

    private static ContentSearchRow[] Rows(Window window) =>
        [.. window.GetLogicalDescendants().OfType<ListBox>().Single().Items.OfType<ContentSearchRow>()];

    private static string Status(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => AutomationProperties.GetName(block) == "Search status").Text ?? string.Empty;

    private static string[] Texts(Window window)
    {
        Dispatch();
        return [.. window.GetVisualDescendantsText()];
    }

    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}

/// <summary>The visible text a window draws, its list items' included, as a person reads it.</summary>
internal static class VisibleText
{
    public static IEnumerable<string> GetVisualDescendantsText(this Window window) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text)).Select(block => block.Text!);
}
