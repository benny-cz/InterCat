using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The processes an aggregate counts, listed in the window's inspector while it is chosen (§6.3), read whole and opened
/// by the keyboard.
/// </summary>
public sealed class AggregateMembersWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: an aggregate chosen in the window lists its processes in the inspector, each read whole; the one the keyboard chooses E lists, and Enter on one opens its rung")]
    public async Task TheInspectorListsAnAggregatesProcesses()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Control panel = window.GetControl<Control>("MembersPanel");
            ListBox members = window.GetControl<ListBox>("MemberList");
            Assert.False(panel.IsVisible);

            // Chosen, Other hosts only lists its two programs beneath its description, the busier first, each said whole.
            workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key);
            Dispatch();
            Assert.True(panel.IsVisible);
            Assert.Equal("Its 2 processes, the busiest first", window.GetControl<TextBlock>("MembersHeadingText").Text);
            Assert.False(window.GetControl<TextBlock>("MembersNoteText").IsVisible);
            Assert.Equal(2, members.ItemCount);
            Assert.Equal("program1.exe · PID 101, 4 records · 1 TCP connection to 1 host. Press E to list its records, or Enter "
                + "to open it.", AutomationProperties.GetName(members.ContainerFromIndex(0)!));
            Save(window, "inspector-aggregate-members.png");

            // The keyboard moving down the list chooses the second: the card counts its records, and, once a brush has
            // restated the list with the process still chosen, E lists them, which hides the list, so the keyboard goes to
            // their table rather than nowhere.
            Assert.True(members.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("program0.exe · PID 100", workspace.ChosenMemberRow?.Name);
            Assert.Equal("program0.exe · PID 100", Assert.IsType<MemberRow>(members.SelectedItem).Name);
            Assert.Equal("Selected process", window.GetControl<TextBlock>("EvidenceHeadingText").Text);

            // A brush restates the list, the process chosen still chosen among its rows.
            long start = workspace.Snapshot.Extent.StartTicks;
            workspace.SelectInterval(new TimeRange(start, start + 50));
            await workspace.IntervalReady;
            Dispatch();
            Assert.Equal("program0.exe · PID 100", workspace.ChosenMemberRow?.Name);
            Assert.Equal(workspace.ChosenMemberRow, Assert.IsType<MemberRow>(members.SelectedItem));
            Assert.EndsWith(" · whole session", workspace.ChosenMemberRow!.Detail, StringComparison.Ordinal);

            // E lists its records in the brushed interval, its start alone.
            Assert.True(members.ContainerFromIndex(1)!.Focus());
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Dispatch();
            Assert.True(workspace.IsEvidenceRung);
            Assert.False(panel.IsEffectivelyVisible);
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Single(workspace.RungRows);
            Assert.True(window.GetControl<ListBox>("RungList").IsKeyboardFocusWithin);

            // Esc comes back to the list, the process chosen in it with the keyboard, not to the ranked table E's records
            // were listed in.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await workspace.LayoutReady;
            Dispatch();
            Assert.True(panel.IsVisible);
            Assert.Equal("program0.exe · PID 100", workspace.ChosenMemberRow?.Name);
            Assert.Same(members.ContainerFromItem(workspace.ChosenMemberRow!), window.FocusManager?.GetFocusedElement());

            // Enter on the first, still in the brush's order, opens its rung, through its group, and the list goes with the
            // aggregate's choice.
            workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key);
            Dispatch();
            Assert.Equal("program0.exe · PID 100", Assert.IsType<MemberRow>(members.Items[0]).Name);
            Assert.True(members.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(3, workspace.Crumbs.Count);
            Assert.EndsWith("program0.exe · PID 100", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
            Assert.False(panel.IsVisible);

            // Esc twice climbs back to where the way down began: the list, program0.exe chosen in it with the keyboard.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await workspace.LayoutReady;
            Dispatch();
            Assert.True(panel.IsVisible);
            Assert.Equal("program0.exe · PID 100", workspace.ChosenMemberRow?.Name);
            Assert.Same(members.ContainerFromItem(workspace.ChosenMemberRow!), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: a double click or Enter on an aggregate in the graph gives the keyboard to its processes in the inspector")]
    public async Task OpeningAnAggregateGivesItsListTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            _ = window.CaptureRenderedFrame();
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            ListBox members = window.GetControl<ListBox>("MemberList");
            string hostOnly = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key;

            // A double click on Other hosts only chooses it and gives the keyboard to its list's first process.
            Point at = graph.TranslatePoint(graph.PointOf(hostOnly)!.Value, window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatch();
            Assert.Equal(hostOnly, workspace.SelectedCluster?.Key);
            Assert.Same(members.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

            // Back on the graph, on the same node, Enter does the same.
            Assert.True(graph.Focus());
            Dispatch();
            Assert.False(members.IsKeyboardFocusWithin);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Same(members.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R7/R15: a newer generation published while recording keeps the process chosen in an aggregate's list, with the keyboard on its row")]
    public async Task ANewerGenerationKeepsTheListsKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox members = window.GetControl<ListBox>("MemberList");
            workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key);
            Dispatch();
            Assert.True(members.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("program0.exe · PID 100", workspace.ChosenMemberRow?.Name);

            // The next generation replaces the workspace: the list keeps program0.exe chosen, and the keyboard its row.
            Publish(session.Store, [Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 100, 109, 5_000)
                .Between("192.168.1.10:52009", "52.0.0.10:443") with { SessionRelativeTicks = 500_000 }]);
            window.ApplyCaptureUpdate(Recording(session));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.NotSame(workspace, next);
            await next.LayoutReady;
            Dispatch();
            Assert.Equal("program0.exe · PID 100", next.ChosenMemberRow?.Name);
            Assert.Same(members.ContainerFromItem(next.ChosenMemberRow!), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A recording's publication of the session's newest generation.</summary>
    private static CaptureUiUpdate Recording(TemporarySession session) => new(CaptureUiPhase.Recording, "Recording",
        "A published generation.", SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// Ten programs of one process each, program0.exe (PID 100) to program9.exe (PID 109), each connecting to a host of its
    /// own and sending to it once more than the one before: the two quietest are counted in Other hosts only.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>();
        for (int index = 0; index < 10; index++)
        {
            rows.Add(Lifecycle(1 + index, ObservationKind.Create, 100 + index, ordinal++)
                with { ResourceName = $@"C:\Apps\program{index}.exe", SessionRelativeTicks = 100 * (1 + index) });
        }

        for (int index = 0; index < 10; index++)
        {
            string local = $"192.168.1.10:{52_000 + index}";
            string remote = $"52.0.0.{index + 1}:443";
            rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100 + index, ordinal)
                .Between(local, remote) with { SessionRelativeTicks = 100 * (100 + (long)ordinal) });
            ordinal++;
            for (int send = 0; send <= index; send++)
            {
                rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 100 + index, ordinal)
                    .Between(local, remote) with { SessionRelativeTicks = 100 * (100 + (long)ordinal) });
                ordinal++;
            }
        }

        return [.. rows];
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }
}
