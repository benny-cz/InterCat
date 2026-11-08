using System.Globalization;
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
/// The operations window (`contracts/cooperative-operations-v1.md`, R18): the operations applications instrumented with
/// InterCat's SDK said they started, in `icat messages --operations`'s words - each under the one it was started under, with
/// what became of it - the chosen one's identity, parent and times, and its processes made the workspace's selection.
/// </summary>
public sealed class OperationsWindowTests
{
    private const string Tree = "Operations, each under the one it was started under. Enter selects the processes the chosen "
        + "operation and those under it are the application's in.";

    private const string SelectThem = "Select its processes";
    private const string GoToIt = "Go to its process";
    private const string Reading = "The session's operations are being read.";

    private static readonly Guid Request = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");
    private static readonly Guid Serving = Guid.Parse("1a2b3c4d-5e6f-4a0b-9c1d-2e3f4a5b6c7d");
    private static readonly Guid Answer = Guid.Parse("2b3c4d5e-6f7a-4b1c-8d2e-3f4a5b6c7d8e");
    private static readonly Guid Elsewhere = Guid.Parse("3c4d5e6f-7a8b-4c2d-9e3f-4a5b6c7d8e9f");
    private static readonly Guid Unrecorded = Guid.Parse("4d5e6f7a-8b9c-4d3e-8f4a-5b6c7d8e9fa0");
    private static readonly Guid Pending = Guid.Parse("5e6f7a8b-9cad-4e4f-9a5b-6c7d8e9fa0b1");

    [AvaloniaFact(DisplayName = "R18: the operations window lists each operation in icat messages --operations's words, each under the one it was started under, the chosen one's identity, parent and what was started under it, and returns its processes on Enter")]
    public async Task TheWindowListsTheTree()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        CooperativeOperationReading read = CooperativeOperationText.Read(session.Store);
        session.Store.ReleaseSegmentReaders();
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new OperationsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);

        // Until the session is read, the window's action waits, saying so on itself.
        Assert.Equal(Reading, Offers.WhyNot(Labelled(window, SelectThem)));
        Task<OperationChoice?> chosen = window.ShowDialog<OperationChoice?>(owner);
        ListBox list = Named<ListBox>(window, Tree);
        await Until(() => list.ItemCount > 0);

        // Each operation in the tree's order, in icat messages --operations's words; the lines about the list say how many
        // there are and what the capture covered.
        Assert.Equal("InterCat · Operations", window.Title);
        Assert.Equal(
        [
            "request-1 · client.exe · 100 · succeeded in 40.0 µs",
            "serve-1 · server.exe · 200 · succeeded in 20.0 µs",
            "answer-1 · client.exe · 100 · succeeded in 5.0 µs",
            "request-2 · client.exe · 100 · open: no completion recorded",
            "serve-9 · server.exe · 200 · failed with status 3 in 5.0 µs",
        ], list.Items.Select(item => item?.ToString()));
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 5 operations · 4 completed (1 failed), 1 open"),
            Named<TextBlock>(window, "Operations status").Text);
        Assert.Equal(CoverageText.Describe(read.Coverage), Named<TextBlock>(window, "What the capture covered").Text);
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == CooperativeOperationText.Note);
        Assert.False(Named<TextBlock>(window, "What no operation holds").IsVisible);

        // The first operation has the keyboard, and a screen reader hears each row whole: its level, and a group's heading
        // with the first of the group.
        Dispatch();
        Assert.Same(list.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        Assert.Equal("Level 1: request-1 · client.exe · 100 · succeeded in 40.0 µs.", AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        Assert.Equal($"Under operation {Unrecorded}, which this capture did not record: another capture's, or one begun before it. "
            + "Level 2: serve-9 · server.exe · 200 · failed with status 3 in 5.0 µs.", AutomationProperties.GetName(list.ContainerFromIndex(4)!));

        // The server's serving says its identity, the operation it was started under, what became of it and what was started
        // under it.
        list.SelectedIndex = 1;
        Assert.Single(window.GetLogicalDescendants().OfType<TextBlock>(), block => block.IsVisible && block.Text == "serve-1");
        Assert.Equal($"Operation {Serving} · started under operation {Request}, request-1", Starting(window, "Operation ").Text);
        Assert.Equal("server.exe · 200 · succeeded in 20.0 µs", Named<TextBlock>(window, "What became of the chosen operation").Text);
        Assert.StartsWith("Started at ", Named<TextBlock>(window, "When the chosen operation started and completed").Text, StringComparison.Ordinal);
        Assert.Equal("1 operation started under it, listed beneath it.", Named<TextBlock>(window, "What was started under the chosen operation").Text);

        // One under a parent never recorded says so; one never completed says so.
        list.SelectedIndex = 4;
        Assert.Equal($"Operation {Elsewhere} · started under operation {Unrecorded}, which this capture did not record", Starting(window, "Operation ").Text);
        list.SelectedIndex = 3;
        Assert.EndsWith(" · no completion recorded", Named<TextBlock>(window, "When the chosen operation started and completed").Text, StringComparison.Ordinal);
        Assert.Equal("Close this list and select the process the operation and those under it are the application's in, in InterCat's window.",
            Offers.Offered(Labelled(window, GoToIt)));

        // With none chosen, the window says to choose one; Enter on the client's request closes it with both processes, the
        // request's and those of the operations under it.
        list.SelectedIndex = -1;
        Assert.Equal("Choose an operation first.", Offers.WhyNot(Labelled(window, SelectThem)));
        list.SelectedIndex = 0;
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(list.ContainerFromIndex(0)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        await Until(() => chosen.IsCompleted);
        OperationChoice choice = Assert.IsType<OperationChoice>(await chosen);
        Assert.Equal(Request, choice.Operation.Operation);
        Assert.Equal([100, 200], choice.Processes.Select(id => snapshot.Processes.Single(process => process.Id == id).ProcessId).Order());

        // Escape closes the window with nothing chosen, as it cancels InterCat's prompts.
        using var again = new OperationsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        Task<OperationChoice?> cancelled = again.ShowDialog<OperationChoice?>(owner);
        ListBox listed = Named<ListBox>(again, Tree);
        await Until(() => listed.ItemCount > 0);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        await Until(() => cancelled.IsCompleted);
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the operations window says why it lists none - a session holding no operation of the SDK's, the coverage saying whether one could have been seen")]
    public async Task TheWindowSaysWhyItListsNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Apps\client.exe", SessionRelativeTicks = 100 }]);
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new OperationsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        _ = window.ShowDialog<OperationChoice?>(owner);
        await Until(() => Named<TextBlock>(window, "Operations status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 0 operations · 0 completed (0 failed), 0 open"),
            Named<TextBlock>(window, "Operations status").Text);
        Assert.Equal("This session holds no operation of the SDK's: no application said one through it while the capture ran, or "
            + "the capture did not record the SDK; the coverage above says which.", Starting(window, "This session").Text);
        Assert.Equal("This session holds no operation of the SDK's.", Offers.WhyNot(Labelled(window, SelectThem)));
        Dispatch();
        Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
        window.Close();
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Operations… selects the processes the chosen operation and those under it are the application's in, or goes to its one process, and the graph has the keyboard")]
    public async Task AnOperationsProcessesBecomeTheSelection()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Button button = window.GetControl<Button>("OperationsButton");
            Assert.True(button.IsEnabled);
            Assert.Equal("List the operations applications said they started", AutomationProperties.GetHelpText(button));
            Assert.Equal("List the operations applications instrumented with InterCat's SDK said they started - each under the one "
                + "it was started under, with what became of it - and select an operation's processes.", ToolTip.GetTip(button));

            // The client's request and what was started under it are two processes, which become the selection together, and
            // the graph has the keyboard.
            OperationsWindow operations = Opened(window, button);
            ListBox list = Named<ListBox>(operations, Tree);
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = 0;
            Labelled(operations, SelectThem).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.HasMultiSelection);
            Assert.Equal([100, 200], workspace.ChosenProcesses.Select(process => process.ProcessId).Order());
            Assert.True(window.GetControl<GraphView>("GraphSurface").IsFocused);

            // An operation of one process goes to it, in place of the set.
            operations = Opened(window, button);
            list = Named<ListBox>(operations, Tree);
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = 3;
            Labelled(operations, GoToIt).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.SelectedProcess is not null);
            Assert.Equal((100, false), (workspace.SelectedProcess!.ProcessId, workspace.HasMultiSelection));

            // Among the source records, the list waits, saying how to leave them.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Equal("Esc leaves the source records; operations can be listed from any other level.", Offers.WhyNot(button));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the operations window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new OperationsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        _ = window.ShowDialog<OperationChoice?>(owner);
        ListBox list = Named<ListBox>(window, Tree);
        await Until(() => list.ItemCount > 0);
        var problems = new List<string>();
        foreach (int index in new[] { 0, 1, 2, 3, 4 })
        {
            list.SelectedIndex = index;
            problems.AddRange(LegibleTextTests.CutOff(window, $"operations, operation {index + 1} chosen"));
            problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"operations, operation {index + 1}: {problem}"));
        }

        window.Close();
        owner.Close();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The client's request, served by the server under it and answered by the client under the serving; a serving under a
    /// request of another capture's, which failed; and a request still open when the capture ended.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        ulong ordinal = 10;
        ObservationRowV1 Start(long ticks, int process, Guid operation, Guid? parent, string name) =>
            SdkRecord(ticks, CooperativeSdk.OperationStarted, process, ++ordinal, operation) with
            {
                Kind = ObservationKind.RequestStart,
                RelatedActivityId = parent,
                ResourceName = name,
            };
        ObservationRowV1 Complete(long ticks, int process, Guid operation, long status) =>
            SdkRecord(ticks, CooperativeSdk.OperationCompleted, process, ++ordinal, operation) with
            {
                Kind = ObservationKind.RequestEnd,
                StatusCode = status,
                StatusAvailability = FieldAvailability.Present,
            };
        return
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
            Start(10, 100, Request, null, "request-1"),
            Start(20, 200, Serving, Request, "serve-1"),
            Start(30, 100, Answer, Serving, "answer-1"),
            Complete(35, 100, Answer, 0),
            Complete(40, 200, Serving, 0),
            Complete(50, 100, Request, 0),
            Start(60, 200, Elsewhere, Unrecorded, "serve-9"),
            Complete(65, 200, Elsewhere, 3),
            Start(70, 100, Pending, null, "request-2"),
        ];
    }

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static OperationsWindow Opened(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Dispatch();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 500 && !window.OwnedWindows.OfType<OperationsWindow>().Any(); wait++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        return window.OwnedWindows.OfType<OperationsWindow>().Single();
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
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
