using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
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
/// The window's Other hosts node (§6.3): drawn and named where a process's connections run to other hosts, its edges a
/// transport each, side by side, hovered and clicked as any mark is, and reached by the keyboard after every process.
/// </summary>
public sealed class OtherHostsWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: the window draws Other hosts, named, its edges a transport each that a hover describes, a click chooses the process of and a double click opens")]
    public async Task TheWindowDrawsOtherHosts()
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
            GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
            Point at = graph.PointOf(hub.Key)!.Value;

            // It is named where it is drawn, by what it stands for and how many drawn processes reach it.
            Assert.Equal("from 1 process", GraphView.Detail(hub));
            Assert.Contains(graph.PlacedLabels, box => Math.Abs(box.Center.X - at.X) < 80 && Math.Abs(box.Center.Y - at.Y) < 80);
            Save(window, "graph-other-hosts.png");

            // A hover over it describes it, and selects nothing.
            ProcessNode? selected = workspace.SelectedProcess;
            window.MouseMove(At(graph, window, at));
            Dispatch();
            Assert.Equal(hub.Key, graph.HoveredKey);
            Assert.Equal("Other hosts", Assert.IsType<HoverCard>(graph.HoverCard).Title);
            Assert.Same(selected, workspace.SelectedProcess);

            // The browser's TCP and UDP run side by side; a hover on either finds that one.
            GraphDisplayEdge tcp = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Tcp);
            GraphDisplayEdge udp = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Udp);
            Assert.Equal((-0.5, 0.5), (tcp.Lane, udp.Lane));
            foreach (GraphDisplayEdge edge in new[] { tcp, udp })
            {
                window.MouseMove(At(graph, window, OnLane(graph, edge)));
                Dispatch();
                Assert.Equal(edge.Key, graph.HoveredKey);
                Assert.Equal("browser.exe · PID 300 ↔ Other hosts", Assert.IsType<HoverCard>(graph.HoverCard).Title);
            }

            // A click on one chooses the browser; a double click opens it, whose rung lists each connection.
            Point onUdp = At(graph, window, OnLane(graph, udp));
            window.MouseDown(onUdp, MouseButton.Left);
            window.MouseUp(onUdp, MouseButton.Left);
            Dispatch();
            Assert.Equal(300, workspace.SelectedProcess?.ProcessId);
            window.MouseDown(onUdp, MouseButton.Left);
            window.MouseUp(onUdp, MouseButton.Left);
            Dispatch();
            Assert.EndsWith("browser.exe · PID 300", workspace.Crumbs[^1].Label, StringComparison.Ordinal);

            // There the keyboard reaches the browser, then Other hosts, then the rest of the machine, and says each.
            await workspace.LayoutReady;
            Dispatch();
            Assert.Equal([GraphNodeKind.Process, GraphNodeKind.OtherHosts, GraphNodeKind.Context],
                GraphView.Traversal(workspace.GraphDisplay).Select(node => node.Kind));
            Assert.True(graph.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
            Dispatch();
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(graph);
            Assert.Equal($"{GraphView.SpokenName}: Other hosts, Connections of 1 drawn process with no process of this capture "
                + "at their other end, most often another machine's", peer.GetName());
            Save(window, "graph-other-hosts-rung.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The middle of an edge as it is drawn: beside the line between its nodes by its lane.</summary>
    private static Point OnLane(GraphView graph, GraphDisplayEdge edge)
    {
        Point source = graph.PointOf(edge.SourceKey)!.Value;
        Point target = graph.PointOf(edge.TargetKey)!.Value;
        Vector along = target - source;
        double length = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
        Vector across = new(-along.Y * edge.Lane * 4 / length, along.X * edge.Lane * 4 / length);
        return new Point((source.X + target.X) / 2, (source.Y + target.Y) / 2) + across;
    }

    /// <summary>
    /// client.exe and server.exe related over loopback; browser.exe's TCP to another host and its UDP to a resolver; and
    /// idle.exe, which does nothing the graph draws.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Tools\idle.exe", SessionRelativeTicks = 140 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 6)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 7)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 8)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 220 },
        Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 11)
            .Between("192.168.1.10:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 300 },
    ];

    [AvaloniaFact(DisplayName = "§6.3: a double click on Other hosts draws its hosts on their own, each named where it is drawn, and one on a host folds them back")]
    public async Task ADoubleClickOpensOtherHosts()
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
            Point hub = At(graph, window, graph.PointOf(workspace.GraphDisplay.OtherHosts!.Key)!.Value);

            // A double click on Other hosts draws the browser's two hosts on their own, each named beside it.
            window.MouseDown(hub, MouseButton.Left);
            window.MouseUp(hub, MouseButton.Left);
            window.MouseDown(hub, MouseButton.Left);
            window.MouseUp(hub, MouseButton.Left);
            Dispatch();
            Assert.True(workspace.HostsOpened);
            await workspace.LayoutReady;
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Assert.Null(workspace.GraphDisplay.OtherHosts);
            GraphDisplayNode[] hosts = [.. workspace.GraphDisplay.Nodes.Where(node => node.Kind == GraphNodeKind.Host)];
            Assert.Equal(["142.250.1.1", "8.8.8.8"], hosts.Select(node => node.Label).Order(StringComparer.Ordinal));
            foreach (GraphDisplayNode host in hosts)
            {
                Point at = graph.PointOf(host.Key)!.Value;
                Assert.Contains(graph.PlacedLabels, box => Math.Abs(box.Center.X - at.X) < 80 && Math.Abs(box.Center.Y - at.Y) < 80);
            }

            Save(window, "graph-hosts-opened.png");

            // A newer publication of the session keeps them opened, drawing the host it adds on its own too.
            Publish(session.Store, [Transfer(26, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 12)
                .Between("192.168.1.10:52001", "20.42.65.1:443") with { SessionRelativeTicks = 310 }]);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)));
            Dispatch();
            var newer = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.NotSame(workspace, newer);
            Assert.True(newer.HostsOpened);
            Assert.Equal(["142.250.1.1", "20.42.65.1", "8.8.8.8"],
                newer.GraphDisplay.Nodes.Where(node => node.Kind == GraphNodeKind.Host).Select(node => node.Label).Order(StringComparer.Ordinal));
            workspace = newer;
            await workspace.LayoutReady;
            Dispatch();
            _ = window.CaptureRenderedFrame();
            hosts = [.. workspace.GraphDisplay.Nodes.Where(node => node.Kind == GraphNodeKind.Host)];

            // One on a host folds them back into Other hosts.
            Point first = At(graph, window, graph.PointOf(hosts[0].Key)!.Value);
            window.MouseDown(first, MouseButton.Left);
            window.MouseUp(first, MouseButton.Left);
            window.MouseDown(first, MouseButton.Left);
            window.MouseUp(first, MouseButton.Left);
            Dispatch();
            Assert.False(workspace.HostsOpened);
            Assert.NotNull(workspace.GraphDisplay.OtherHosts);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: Other hosts chosen lists its hosts in the inspector, and Enter on one finds the processes that reached it, the keyboard staying on the list")]
    public async Task TheInspectorListsOtherHostsHosts()
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
            Control panel = window.GetControl<Control>("HostsPanel");
            ListBox hosts = window.GetControl<ListBox>("HostList");
            TextBox search = window.GetControl<TextBox>("SearchBox");
            Assert.False(panel.IsVisible);

            // Chosen, Other hosts lists its two hosts beneath its description, the busiest first, each said whole.
            workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);
            Dispatch();
            Assert.True(panel.IsVisible);
            Assert.Equal("Its 2 hosts, the busiest first", window.GetControl<TextBlock>("HostsHeadingText").Text);
            Assert.False(window.GetControl<TextBlock>("HostsNoteText").IsVisible);
            Assert.Equal(2, hosts.ItemCount);
            Assert.Equal("142.250.1.1, 1 TCP connection · port 443 · 1 record. Press E to list the records of its connections, or "
                + "Enter to find the processes that reached it.", AutomationProperties.GetName(hosts.ContainerFromIndex(0)!));
            Save(window, "inspector-other-hosts.png");

            // Enter on one finds the processes that reached it, as a search for its address does; the keyboard stays on the
            // list, so Down and Enter find the next host's.
            Assert.True(hosts.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("142.250.1.1", search.Text);
            Assert.Equal(["browser.exe · PID 300"], workspace.SearchResults.Select(row => row.Label));
            Assert.True(hosts.IsKeyboardFocusWithin);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("8.8.8.8", search.Text);
            Assert.True(hosts.IsKeyboardFocusWithin);

            // The host the keyboard moved to is chosen: the card counts its records, and E lists them, which hides the list,
            // so the keyboard goes to their table rather than nowhere.
            Assert.Equal("8.8.8.8", workspace.ChosenHostRow?.Address);
            Assert.Equal("8.8.8.8", Assert.IsType<HostRow>(hosts.SelectedItem).Address);
            await workspace.HostRecordsReady;
            Dispatch();
            Assert.Equal(("Selected host", "1 record of 1 UDP flow from 1 process · 40 B sent, no receive recorded"),
                (window.GetControl<TextBlock>("EvidenceHeadingText").Text, window.GetControl<TextBlock>("EvidenceSummaryText").Text));
            workspace.SearchText = string.Empty;
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Dispatch();
            Assert.True(workspace.IsEvidenceRung);
            Assert.False(panel.IsEffectivelyVisible);
            Assert.True(window.GetControl<ListBox>("RungList").IsKeyboardFocusWithin);
            await workspace.EvidenceReady;
            Assert.StartsWith("Records of the connections to 8.8.8.8", workspace.EvidenceScopeText, StringComparison.Ordinal);
            Assert.Single(workspace.RungRows);
            workspace.ReturnTo(0);
            Dispatch();

            // Another node chosen takes the list away.
            workspace.SelectGraphNode(workspace.GraphDisplay.NodeOf(
                workspace.Snapshot.Processes.Single(process => process.ProcessId == 300).Id)!.Key);
            Dispatch();
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.4: a host clicked in the graph heads the inspector's card, which counts its connections' records, and E lists them")]
    public async Task EListsAClickedHostsRecords()
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
            Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.OtherHosts!.Key));
            await workspace.LayoutReady;
            Dispatch();
            _ = window.CaptureRenderedFrame();
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            string host = GraphProjection.HostKey("142.250.1.1");

            // A click chooses it: the card is headed by it, and counts its records once they are read.
            Point at = At(graph, window, graph.PointOf(host)!.Value);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatch();
            Assert.Equal(host, workspace.SelectedGraphNodeKey);
            await workspace.HostRecordsReady;
            Dispatch();
            Assert.Equal("Selected host", window.GetControl<TextBlock>("EvidenceHeadingText").Text);
            Assert.Equal("1 record of 1 TCP connection from 1 process · 500 B sent, no receive recorded",
                window.GetControl<TextBlock>("EvidenceSummaryText").Text);
            Save(window, "inspector-host-chosen.png");

            // E lists them.
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Dispatch();
            Assert.True(workspace.IsEvidenceRung);
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Single(workspace.RungRows);
            Assert.StartsWith("Records of the connections to 142.250.1.1", workspace.EvidenceScopeText, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.4: a host clicked in Other hosts' list stays chosen there as Other hosts opens and its list is given new rows")]
    public async Task AListedHostStaysChosenAsTheListChanges()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TenHosts());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
            workspace.SelectGraphNode(hub.Key);
            Dispatch();
            _ = window.CaptureRenderedFrame();
            ListBox hosts = window.GetControl<ListBox>("HostList");
            Assert.Equal(10, hosts.ItemCount);

            // A click on the ninth host, which the graph never draws on its own, chooses it.
            Control ninth = hosts.ContainerFromIndex(8)!;
            ninth.BringIntoView();
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Point at = At(ninth, window, new Point(ninth.Bounds.Width / 2, ninth.Bounds.Height / 2));
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatch();
            Assert.Equal("52.0.0.9", workspace.ChosenHostRow?.Address);

            // Opened, Other hosts lists the two hosts it still stands for, now one process's, so each row says less; the one
            // chosen is still chosen there.
            Assert.Equal("1 TCP connection from 1 process · port 443 · 2 records", workspace.ChosenHostRow?.Detail);
            Assert.True(workspace.OpenGraphNode(hub.Key));
            await workspace.LayoutReady;
            Dispatch();
            Assert.Equal(2, hosts.ItemCount);
            Assert.Equal(new HostRow("52.0.0.9", "1 TCP connection · port 443 · 2 records"), Assert.IsType<HostRow>(hosts.SelectedItem));
            Assert.Equal("52.0.0.9", workspace.ChosenHostRow?.Address);
            await workspace.HostRecordsReady;
            Dispatch();
            Assert.Equal("2 records of 1 TCP connection from 1 process · 200 B sent, no receive recorded",
                window.GetControl<TextBlock>("EvidenceSummaryText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: under a byte ranking the window's Other hosts list is ranked by the bytes its hosts' connections carried once they are read")]
    public async Task OtherHostsListFollowsAByteRanking()
    {
        using var session = new TemporarySession();
        Publish(session.Store, CarriedRows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);
            Dispatch();
            ListBox hosts = window.GetControl<ListBox>("HostList");
            TextBlock heading = window.GetControl<TextBlock>("HostsHeadingText");
            Assert.Equal("52.0.0.1", Assert.IsType<HostRow>(hosts.Items[0]).Address);

            // Ranked by bytes sent, the list shows the host its process sent the most to first once their bytes are read.
            workspace.RankBy = RankingMetric.BytesSent;
            await workspace.HostTrafficReady;
            Dispatch();
            Assert.Equal("Its 3 hosts, the most bytes sent first", heading.Text);
            Assert.Equal(["52.0.0.2", "52.0.0.1", "52.0.0.3"], hosts.Items.Cast<HostRow>().Select(row => row.Address));

            // The heading is said whole at the inspector's width, on as many lines as it takes, never run past its bounds.
            _ = window.CaptureRenderedFrame();
            Assert.True(heading.TextLayout.Width <= heading.Bounds.Width + 0.5,
                $"The heading's {heading.TextLayout.Width} px run past its {heading.Bounds.Width} px.");
            Save(window, "inspector-hosts-by-bytes.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// browser.exe's TCP to three hosts: 52.0.0.1, three sends of 10 B; 52.0.0.2, one of 5,000 B; and 52.0.0.3, two that
    /// stated no size.
    /// </summary>
    private static ObservationRowV1[] CarriedRows() =>
    [
        Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
        .. Enumerable.Range(0, 3).Select(index => Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 10, 300,
            (ulong)(10 + index)).Between("192.168.1.10:52001", "52.0.0.1:443") with { SessionRelativeTicks = (10 + index) * 100 }),
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 5_000, 300, 20).Between("192.168.1.10:52002", "52.0.0.2:443")
            with { SessionRelativeTicks = 2_000 },
        .. Enumerable.Range(0, 2).Select(index => Transfer(30 + index, ObservationKind.Send, AccountingSide.SendSide, null, 300,
            (ulong)(30 + index)).Between("192.168.1.10:52003", "52.0.0.3:443") with { SessionRelativeTicks = (30 + index) * 100 }),
    ];

    /// <summary>
    /// browser.exe's TCP to ten hosts, 52.0.0.1 to 52.0.0.10, ten records to the first down to one to the last; and
    /// updater.exe's one record to the first, so Other hosts stands for two processes until it is opened and one after.
    /// </summary>
    private static ObservationRowV1[] TenHosts()
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 300, ordinal++) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 310, ordinal++) with { ResourceName = @"C:\Apps\updater.exe", SessionRelativeTicks = 110 },
        };
        for (int host = 1; host <= 10; host++)
        {
            for (int record = 0; record <= 10 - host; record++)
            {
                rows.Add(Transfer(200 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 300, ordinal)
                    .Between($"192.168.1.10:{52_000 + host}", $"52.0.0.{host}:443") with { SessionRelativeTicks = 200 + (long)ordinal });
                ordinal++;
            }
        }

        rows.Add(Transfer(900, ObservationKind.Send, AccountingSide.SendSide, 100, 310, ordinal)
            .Between("192.168.1.10:53000", "52.0.0.1:443") with { SessionRelativeTicks = 900 });
        return [.. rows];
    }

    private static Point At(Control control, Window window, Point point) => control.TranslatePoint(point, window)!.Value;

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
