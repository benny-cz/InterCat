using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
/// The outliers window (`contracts/outliers-v1.md` §7, R18): the time scope's rows judged against the windows of its baseline
/// in `icat outliers`'s words, the chosen row's windows drawn and stated bar by bar, what is not judged and why, and the row
/// chosen shown in InterCat's window as a comparison's row is.
/// </summary>
public sealed class OutliersWindowTests
{
    private const string ShowIt = "Show in the window";
    private const string Listed = "The rows above or below every window of their baseline, the most records beyond first. Enter "
        + "shows the chosen row in the window.";
    private static readonly SessionClock Clock = SessionClock.Session(TimeZoneInfo.Utc);

    [AvaloniaFact(DisplayName = "R18: the outliers window judges the time scope against its baseline's windows in icat outliers's words, draws the chosen row's windows, and returns the row on Enter")]
    public async Task TheWindowJudgesTheTimeScope()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        OutlierReading read = ComparisonOutliers.Read(session.Store, new(0, 1_000), new(1_000, 1_050));
        CultureInfo culture = CultureInfo.CurrentCulture;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = Judging(session, overview, new(1_000, 1_050));

        // Until the rows are judged, Show in the window waits, saying so.
        Assert.Equal("The rows are being judged.", Offers.WhyNot(Labelled(window, ShowIt)));
        Task<ComparedRelationship?> chosen = window.ShowDialog<ComparedRelationship?>(owner);
        ListBox list = Named<ListBox>(window, Listed);
        await Until(() => list.ItemCount > 0);

        // The rows above or below every window, the most records beyond first, each by its ends, then its records, its
        // windows', how far beyond and where it falls, as icat outliers says them; the lines above say the baseline and its
        // windows, how many rows were judged and found, and the coverage of each interval.
        Assert.Equal("InterCat · Outliers", window.Title);
        Assert.Equal(
            [
                "1. PID 300 and PID 400 · TCP · 50 records · its windows 10 to 12, median 10 · 38 more than the most · above every "
                    + "window of its baseline",
                "2. PID 500 and PID 600 · TCP · 0 records · its windows 10 to 12, median 10 · 10 fewer than the fewest · below "
                    + "every window of its baseline",
                "3. PID 700 and PID 800 · TCP · 4 records · its windows 0 in each · 4 more than the most · above every window of its "
                    + "baseline",
            ],
            list.Items.Select(item => item?.ToString()));
        Assert.Equal(string.Create(culture, $"Generation {overview.Generation:N0} · ") + OutlierText.Found(read, culture),
            Named<TextBlock>(window, "Outliers status").Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Named<TextBlock>(window, "Outliers status")));
        Assert.Equal(OutlierText.Baseline(read, Clock, culture), Named<TextBlock>(window, "The baseline and its windows").Text);
        Assert.Equal(["All the session keeps before it"],
            Named<ComboBox>(window, "The baseline the time scope is judged against").Items.Select(item => item?.ToString()));
        Assert.Equal("Coverage in the baseline: covered for TCP and UDP · no other mechanism collected" + Environment.NewLine
            + "Coverage in the time scope: covered for TCP and UDP · no other mechanism collected",
            Named<TextBlock>(window, "What the capture covered over each interval").Text);
        Assert.Equal(OutlierText.Chance(read, culture),
            Named<TextBlock>(window, "How often chance alone puts a row outside its windows' range").Text);
        Assert.Equal(OutlierText.NotJudged(read, culture).Single(), Named<TextBlock>(window, "What is not judged").Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == OutlierText.Note);

        // The first row has the keyboard, and a screen reader hears each row whole.
        Dispatch();
        Assert.Same(list.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        Assert.Equal("1. PID 300 and PID 400 · TCP: 50 records · its windows 10 to 12, median 10 · 38 more than the most · above "
            + "every window of its baseline.", AutomationProperties.GetName(list.ContainerFromIndex(0)!));

        // The chosen row says its records in the time scope and in its windows, and how far beyond; its windows are drawn,
        // the compared interval's bar chosen first and stated in words.
        Assert.Equal("Row 1: PID 300 and PID 400 over TCP", Starting(window, "Row ").Text);
        Assert.Equal("In the time scope: 50 records, above every window of its baseline",
            Named<TextBlock>(window, "Its records in the time scope").Text);
        Assert.Equal("In its 20 windows: 10 to 12, median 10", Starting(window, "In its ").Text);
        Assert.Equal("Beyond: 38 more than the most", Starting(window, "Beyond: ").Text);
        OutlierWindowsControl chart = window.GetLogicalDescendants().OfType<OutlierWindowsControl>().Single();
        TextBlock said = Named<TextBlock>(window, "The windows chart's chosen bar");
        Assert.Equal((21, 20), (chart.Bars, chart.ChosenBar));
        Assert.Equal("The time scope, " + Clock.HalfOpenRange(new(1_000, 1_050), culture)
            + ": 50 records, above every window of its baseline, 38 more than the most.", said.Text);
        Assert.Equal(OutlierWindowsControl.SpokenName + ", " + said.Text, AutomationProperties.GetName(chart));

        // Left, Home and End move along the bars, each window stated with its interval and records.
        Assert.True(chart.Focus());
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal("Window 20 of 20, " + Clock.HalfOpenRange(new(950, 1_000), culture) + ": 10 records", said.Text);
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal("Window 1 of 20, " + Clock.HalfOpenRange(new(0, 50), culture) + ": 12 records", said.Text);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(0, chart.ChosenBar);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal("Window 2 of 20, " + Clock.HalfOpenRange(new(50, 100), culture) + ": 10 records", said.Text);
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        chart.MoveTo(21);
        Assert.Equal(20, chart.ChosenBar);

        // The row below every window says so, its chart from its compared bar; one within would say no more than within.
        list.SelectedIndex = 1;
        Assert.Equal("Beyond: 10 fewer than the fewest", Starting(window, "Beyond: ").Text);
        Assert.Equal("The time scope, " + Clock.HalfOpenRange(new(1_000, 1_050), culture)
            + ": 0 records, below every window of its baseline, 10 fewer than the fewest.", said.Text);
        Assert.Equal("Close this window and show the chosen row in InterCat's window, as a comparison's row is shown",
            Offers.Offered(Labelled(window, ShowIt)));

        // With none chosen, the details go and Show in the window says why; Enter on the first closes the window with it.
        list.SelectedIndex = -1;
        Assert.Equal(OutlierText.NoneOutside, Offers.WhyNot(Labelled(window, ShowIt)));
        Assert.Equal((false, 0), (chart.IsVisible, chart.Bars));
        list.SelectedIndex = 0;
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(list.ContainerFromIndex(0)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        await Until(() => chosen.IsCompleted);
        ComparedRelationship row = Assert.IsType<ComparedRelationship>(await chosen);
        Assert.Equal((300, 400), (overview.Nodes.Single(node => node.Id == row.Relationship!.SourceId).ProcessId,
            overview.Nodes.Single(node => node.Id == row.Relationship!.TargetId).ProcessId));

        // Escape closes the window with nothing chosen, as it cancels InterCat's prompts.
        using var again = Judging(session, overview, new(1_000, 1_050));
        Task<ComparedRelationship?> cancelled = again.ShowDialog<ComparedRelationship?>(owner);
        await Until(() => Named<ListBox>(again, Listed).ItemCount > 0);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        await Until(() => cancelled.IsCompleted);
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the outliers window says why it judges nothing, judges against the time after the scope, and by executable on request")]
    public async Task TheWindowSaysWhyItJudgesNothing()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        // A scope at the session's start has nothing before it; the time after it is offered and judged against, its windows
        // spread across it.
        using (var window = Judging(session, overview, new(0, 50)))
        {
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            TextBlock status = Named<TextBlock>(window, "Outliers status");
            await Until(() => status.Text?.StartsWith("Nothing", StringComparison.Ordinal) == true);
            Assert.Equal("Nothing the session keeps is before the time scope; judge it against the time after it, or choose a "
                + "later scope.", status.Text);
            Assert.Equal("No rows are judged; the status line says why.", Offers.WhyNot(Labelled(window, ShowIt)));
            Dispatch();
            Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
            ComboBox baseline = Named<ComboBox>(window, "The baseline the time scope is judged against");
            Assert.Equal(["All the session keeps before it", "All after it, to the session's last record"],
                baseline.Items.Select(item => item?.ToString()));
            baseline.SelectedIndex = 1;
            await Until(() => status.Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.EndsWith("as 19 windows of 5.0 µs, spread evenly across it", Named<TextBlock>(window, "The baseline and its windows").Text,
                StringComparison.Ordinal);

            // By executable, no process ran a witnessed executable, so no row is judged, and what no executable groups is said.
            ComboBox rowsOf = Named<ComboBox>(window, "What each row is: one process, or one executable's processes together");
            rowsOf.SelectedIndex = 1;
            await Until(() => status.Text?.EndsWith("no row judged", StringComparison.Ordinal) == true);
            OutlierReading byExecutable = ComparisonOutliers.Read(session.Store, new(50, overview.Extent!.Value.EndTicks), new(0, 50),
                grouping: ComparisonGrouping.Executable);
            Assert.Equal(OutlierText.NoRow(byExecutable.Comparison), Starting(window, "No relationship").Text);
            Assert.StartsWith("Each row is an executable's processes together", Named<TextBlock>(window, "What is not judged").Text,
                StringComparison.Ordinal);
            window.Close();
        }

        // A scope whose baseline holds too few windows is refused in the command line's words; the time after it, too.
        using (var window = Judging(session, overview, new(500, 1_000)))
        {
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            TextBlock status = Named<TextBlock>(window, "Outliers status");
            await Until(() => status.Text?.StartsWith("The baseline", StringComparison.Ordinal) == true);
            Assert.StartsWith("The baseline holds only 1 window as long as the compared interval, and at least 19 are needed",
                status.Text, StringComparison.Ordinal);
            Assert.Equal((false, false), (Named<TextBlock>(window, "How often chance alone puts a row outside its windows' range").IsVisible,
                Named<TextBlock>(window, "What is not judged").IsVisible));
            window.Close();
        }

        // Every row within its windows' range lists none and says so; rows not judged are counted.
        using (var window = Judging(session, overview, new(950, 1_000)))
        {
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => Named<TextBlock>(window, "Outliers status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal(0, Named<ListBox>(window, Listed).ItemCount);
            Assert.Equal(OutlierText.NoneOutside, Starting(window, "No row is above").Text);
            Assert.Equal(OutlierText.NoneOutside, Offers.WhyNot(Labelled(window, ShowIt)));
            window.Close();
        }

        // Counted under another evidence policy, the status says which.
        using (var window = new OutliersWindow(session.Path, overview.SessionId, overview.Generation, new(1_000, 1_050), Within(overview),
            overview.Recording is not null, Clock, EvidencePolicy.DirectOnly))
        {
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => Named<TextBlock>(window, "Outliers status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.EndsWith(" · counted under the direct-only evidence policy, as InterCat's window counts",
                Named<TextBlock>(window, "Outliers status").Text, StringComparison.Ordinal);
            window.Close();
        }

        // A row InterCat's window cannot show says why on Show in the window, and Enter leaves it open.
        using (var window = new OutliersWindow(session.Path, overview.SessionId, overview.Generation, new(1_000, 1_050), Within(overview),
            overview.Recording is not null, Clock, EvidencePolicy.IncludeCorrelated) { WhyNotShown = _ => "Not here." })
        {
            Task<ComparedRelationship?> chosen = window.ShowDialog<ComparedRelationship?>(owner);
            ListBox list = Named<ListBox>(window, Listed);
            await Until(() => list.ItemCount > 0);
            Assert.Equal("Not here.", Offers.WhyNot(Labelled(window, ShowIt)));
            _ = window.CaptureRenderedFrame();
            Dispatch();
            Assert.True(list.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.False(chosen.IsCompleted);
            window.Close();
        }

        // A folder holding an older generation than InterCat's window shows is refused, and one holding another session.
        using (var window = new OutliersWindow(session.Path, overview.SessionId, overview.Generation + 1, new(1_000, 1_050),
            Within(overview), overview.Recording is not null, Clock, EvidencePolicy.IncludeCorrelated))
        {
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => Named<TextBlock>(window, "Outliers status").Text?.StartsWith("This folder", StringComparison.Ordinal) == true);
            Assert.Equal("This folder no longer holds the generation InterCat's window shows. Close this window and open it again "
                + "from that window.", Named<TextBlock>(window, "Outliers status").Text);
            Assert.Equal("No rows are judged; the status line says why.", Offers.WhyNot(Labelled(window, ShowIt)));
            window.Close();
        }

        using (var window = new OutliersWindow(session.Path, Guid.NewGuid(), overview.Generation, new(1_000, 1_050), Within(overview),
            overview.Recording is not null, Clock, EvidencePolicy.IncludeCorrelated))
        {
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => Named<TextBlock>(window, "Outliers status").Text?.StartsWith("Could not", StringComparison.Ordinal) == true);
            Assert.StartsWith("Could not judge the rows: ", Named<TextBlock>(window, "Outliers status").Text, StringComparison.Ordinal);
            window.Close();
        }

        owner.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Outliers… judges the brushed range and shows the row chosen in the window, as a comparison's row is shown")]
    public async Task TheRowChosenIsShownInTheWindow()
    {
        using var session = Session();
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            await workspace.LayoutReady;

            // The whole session is no interval to judge: the button is not offered until a range is brushed.
            Button button = window.GetControl<Button>("OutliersButton");
            Assert.False(button.IsVisible);
            workspace.SelectInterval(new TimeRange(1_000, 1_050));
            Dispatch();
            Assert.True(button.IsVisible);
            Assert.Equal("List the relationships whose records in this range are above or below every window of the time before it, "
                + "each window as long as this range, with how often chance alone puts one there", AutomationProperties.GetHelpText(button));

            // The first row, shown in the window, opens its relationship as a double click on its edge does.
            int depth = workspace.Crumbs.Count;
            OutliersWindow outliers = Opened(window, button);
            await Until(() => Named<ListBox>(outliers, Listed).ItemCount > 0);
            Labelled(outliers, ShowIt).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.Crumbs.Count > depth);
            Assert.Contains(workspace.Crumbs, crumb => crumb.Label.Contains("PID 300", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the outliers window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = Judging(session, overview, new(1_000, 1_050));
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        _ = window.ShowDialog<ComparedRelationship?>(owner);
        ListBox list = Named<ListBox>(window, Listed);
        await Until(() => list.ItemCount > 0);
        var problems = new List<string>();
        foreach (int index in new[] { 0, 1, 2 })
        {
            list.SelectedIndex = index;
            problems.AddRange(LegibleTextTests.CutOff(window, $"outliers, row {index + 1} chosen"));
            problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"outliers, row {index + 1}: {problem}"));
        }

        window.Close();
        owner.Close();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static TemporarySession Session()
    {
        var temporary = new TemporarySession();
        Publish(temporary.Store, OutlierRows(), coverage: OutlierCoverage());
        temporary.Store.ReleaseSegmentReaders();
        return temporary;
    }

    /// <summary>The time a baseline after the scope runs to, as InterCat's window gives it: the recording, or the records' span.</summary>
    private static TimeRange Within(SessionOverviewBundle overview) => overview.Recording ?? overview.Extent!.Value;

    private static OutliersWindow Judging(TemporarySession session, SessionOverviewBundle overview, TimeRange compared) =>
        new(session.Path, overview.SessionId, overview.Generation, compared, Within(overview), overview.Recording is not null, Clock,
            EvidencePolicy.IncludeCorrelated);

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static OutliersWindow Opened(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Dispatch();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 500 && !window.OwnedWindows.OfType<OutliersWindow>().Any(); wait++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        return window.OwnedWindows.OfType<OutliersWindow>().Single();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static TextBlock Starting(Window window, string start) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.IsVisible
            && block.Text?.StartsWith(start, StringComparison.Ordinal) == true);

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
