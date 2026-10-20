using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The graph's names on a crowded machine (§6.3): a name the slots beside its node could not hold, or one standing nearer
/// another node than its own, is tied to its node by a leader, so no name reads as its neighbour's.
/// </summary>
public sealed class GraphLabelLeaderTests
{
    [AvaloniaFact(DisplayName = "§6.3: on a crowded machine a name set away from its node is tied to it by a leader from its rim, and every other stands beside a node")]
    public async Task NamesSetAwayFromTheirNodesAreTiedToThem()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyMachine());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            _ = Settle(window);
            Dictionary<string, (Point Centre, double Radius)> nodes = workspace.GraphDisplay.Nodes
                .Where(node => graph.PointOf(node.Key) is not null && graph.RadiusOf(node.Key) is not null)
                .ToDictionary(node => node.Key, node => (graph.PointOf(node.Key)!.Value, graph.RadiusOf(node.Key)!.Value),
                    StringComparer.Ordinal);
            Assert.Equal(graph.PlacedLabels.Count, graph.PlacedLabelKeys.Count);

            // Some names here could not stand beside their nodes. Each such name is tied to its own node by a leader from
            // the node's rim to the name's box; every other stands within the leader's gap of its own node, and no other
            // node's rim is nearer it.
            Assert.NotEmpty(graph.DrawnLeaders);
            int tied = 0;
            for (int index = 0; index < graph.PlacedLabels.Count; index++)
            {
                Rect box = graph.PlacedLabels[index];
                (Point centre, double radius) = nodes[graph.PlacedLabelKeys[index]];
                double own = Gap(box, centre) - radius;
                bool nearerAnother = nodes.Where(node => node.Key != graph.PlacedLabelKeys[index])
                    .Any(node => Gap(box, node.Value.Centre) - node.Value.Radius < own);
                (Point From, Point To)[] leaders = [.. graph.DrawnLeaders.Where(leader => OnEdge(box, leader.To))];
                if (own > GraphView.LeaderGap || nearerAnother)
                {
                    (Point from, _) = Assert.Single(leaders);
                    Assert.Equal(radius, Distance(centre, from), 6);
                    tied++;
                }
                else
                {
                    Assert.Empty(leaders);
                }
            }

            Assert.Equal(graph.DrawnLeaders.Count, tied);

            Save(window, "label-leaders-1456x939.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: a busy machine's graph names every node a large window has room for, past two dozen, each process by its name alone")]
    public async Task ALargeWindowNamesEveryNodeItHasRoomFor()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyMachine());
        var window = new MainWindow { Width = 1920, Height = 1080 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            _ = Settle(window);

            // Thirty nodes, every one named where it stands.
            GraphDisplay drawn = workspace.GraphDisplay;
            Assert.InRange(drawn.Nodes.Count, 25, 40);
            Assert.Equal(drawn.Nodes.Select(node => node.Key).Order(StringComparer.Ordinal),
                graph.PlacedLabelKeys.Order(StringComparer.Ordinal));

            // So many names leave a process's PID to its card: each process's name is a line high, where an aggregate's says
            // what it counts on a second.
            Dictionary<string, Rect> boxes = graph.PlacedLabelKeys.Zip(graph.PlacedLabels)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            double[] processes = [.. drawn.Nodes.Where(node => node.Kind == GraphNodeKind.Process).Select(node => boxes[node.Key].Height)];
            double[] aggregates = [.. drawn.Nodes.Where(node => node.Kind != GraphNodeKind.Process).Select(node => boxes[node.Key].Height)];
            Assert.True(processes.Max() < aggregates.Min(), $"process names {processes.Max()} high, aggregates' {aggregates.Min()}");
            Save(window, "label-budget-1920x1080.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: a small graph names each process with its PID beneath, as an aggregate says what it counts")]
    public async Task ASmallGraphNamesEachProcessWithItsPid()
    {
        // A client and its server over loopback, and a third process reaching another host: four nodes with Other hosts.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
            Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\updater.exe", SessionRelativeTicks = 300 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 6, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = 1_100 },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 6).Between("192.168.1.10:52000", "52.0.0.1:443")
                with { SessionRelativeTicks = 1_200 },
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            _ = Settle(window);

            // Each process's name has its PID on a second line, as high as Other hosts' name and what it counts.
            Dictionary<string, Rect> boxes = graph.PlacedLabelKeys.Zip(graph.PlacedLabels)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            GraphDisplayNode[] processes = [.. workspace.GraphDisplay.Nodes.Where(node => node.Kind == GraphNodeKind.Process)];
            Assert.Equal(3, processes.Length);
            double hub = boxes[workspace.GraphDisplay.OtherHosts!.Key].Height;
            Assert.All(processes, process => Assert.Equal(hub, boxes[process.Key].Height, 0.5));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: on a crowded machine choosing a node or moving the keyboard leaves every other name where it stood, or steps it aside for the chosen one's")]
    public async Task NamesStayWhereTheyStoodWhenANodeIsChosen()
    {
        // At the smallest window, where the room runs out before the budget: at 1456 × 939 every node is named.
        using var session = new TemporarySession();
        Publish(session.Store, BusyMachine());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            Dictionary<string, Rect> Names()
            {
                _ = Settle(window);

                // No two names overlap, however they were placed.
                IReadOnlyList<Rect> boxes = graph.PlacedLabels;
                Assert.DoesNotContain(Enumerable.Range(0, boxes.Count), index =>
                    Enumerable.Range(index + 1, boxes.Count - index - 1).Any(other => boxes[index].Intersects(boxes[other])));
                return graph.PlacedLabelKeys.Zip(graph.PlacedLabels)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            }

            // With nothing chosen some processes go unnamed, the budget and the room spent on the others.
            Dictionary<string, Rect> unchosen = Names();
            string[] unnamed = [.. workspace.GraphDisplay.Nodes
                .Where(node => node.Kind == GraphNodeKind.Process && !unchosen.ContainsKey(node.Key)).Select(node => node.Key)];
            Assert.NotEmpty(unnamed);

            // Each other name stands where it stood, or, where it would crowd the newly named node's, steps aside.
            void StoodOrSteppedAside(Dictionary<string, Rect> now, string named)
            {
                Rect taken = now[named];
                foreach ((string key, Rect box) in unchosen.Where(entry => entry.Key != named))
                {
                    if (now.TryGetValue(key, out Rect stood))
                    {
                        Assert.Equal(box, stood);
                    }
                    else
                    {
                        Assert.True(taken.Inflate(GraphView.NameSpacing).Intersects(box),
                            $"{key}'s name went elsewhere rather than step aside for {named}'s, which it would crowd");
                    }
                }
            }

            // Chosen, an unnamed process is named, and a leader tying its name to it starts at its selection's ring.
            int ringed = 0;
            foreach (string key in unnamed.Take(4))
            {
                workspace.SelectGraphNode(key);
                Dictionary<string, Rect> now = Names();
                StoodOrSteppedAside(now, key);
                foreach ((Point from, _) in graph.DrawnLeaders.Where(leader => OnEdge(now[key], leader.To)))
                {
                    Assert.Equal(graph.RadiusOf(key)!.Value + GraphView.SelectionHalo, Distance(graph.PointOf(key)!.Value, from), 6);
                    ringed++;
                }
            }

            Assert.NotEqual(0, ringed);

            // Nothing chosen again, every name is back where it stood.
            workspace.ClearSelection();
            Assert.Equal(unchosen, Names());

            // The keyboard moving on from node to node without choosing, through every node, names each where it rests, the
            // same way.
            graph.Focus();
            int newlyNamed = 0;
            for (int step = 0; step < workspace.GraphDisplay.Nodes.Count; step++)
            {
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
                Dictionary<string, Rect> now = Names();
                string[] added = [.. now.Keys.Except(unchosen.Keys)];
                Assert.True(added.Length <= 1, string.Join(", ", added));
                if (added is [{ } focused])
                {
                    StoodOrSteppedAside(now, focused);
                    newlyNamed++;
                }
                else
                {
                    Assert.Equal(unchosen, now);
                }
            }

            Assert.NotEqual(0, newlyNamed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: on a crowded machine no two names stand against each other where a slot leaves room, and an edge passing beneath a name breaks round its letters")]
    public async Task NamesStandApartAndEdgesBreakRoundThem()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyMachine());
        var window = new MainWindow { Width = 1920, Height = 1080 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");

            // Two names standing against each other read as one word - "SearchHost.exespoolsv.exe" - so none does: at
            // 1920 × 1080, where every name has a slot that leaves its room, each keeps it; at 1456 × 939, where one does
            // not, it keeps a word's space, as at the smallest window (CrowdedNamesKeepAWordsSpace).
            void NoneAgainstAnother(Thickness room)
            {
                IReadOnlyList<Rect> placed = graph.PlacedLabels;
                for (int one = 0; one < placed.Count; one++)
                {
                    for (int other = one + 1; other < placed.Count; other++)
                    {
                        Assert.False(placed[one].Inflate(room).Intersects(placed[other]),
                            $"{graph.PlacedLabelKeys[one]} {placed[one]} stands against {graph.PlacedLabelKeys[other]} {placed[other]}");
                    }
                }
            }

            _ = Settle(window);
            NoneAgainstAnother(GraphView.NameSpacing);
            window.Width = 1456;
            window.Height = 939;
            WriteableBitmap frame = Settle(window);
            NoneAgainstAnother(new Thickness(4, 1));
            IReadOnlyList<Rect> names = graph.PlacedLabels;

            // Edges pass beneath some names, and round the letters of every name is the pane's colour, never an edge's ink:
            // each letter's pixels are ringed by a pixel of the name's halo.
            int crossed = 0;
            for (int index = 0; index < names.Count; index++)
            {
                Rect box = names[index];
                if (workspace.GraphDisplay.Edges.Any(edge => graph.PointOf(edge.SourceKey) is { } from
                        && graph.PointOf(edge.TargetKey) is { } to && Crosses(box, from, to)))
                {
                    crossed++;
                }

                Point corner = graph.TranslatePoint(box.TopLeft, window)!.Value;
                for (int y = (int)corner.Y + 1; y < (int)(corner.Y + box.Height) - 1; y++)
                {
                    for (int x = (int)corner.X + 1; x < (int)(corner.X + box.Width) - 1; x++)
                    {
                        if (!IsLetter(RenderedPixels.At(frame, new(x, y)))) continue;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                Avalonia.Media.Color beside = RenderedPixels.At(frame, new(x + dx, y + dy));
                                Assert.False(IsInk(beside), $"{graph.PlacedLabelKeys[index]}: {beside} beside a letter at ({x}, {y})");
                            }
                        }
                    }
                }
            }

            Assert.InRange(crossed, 3, names.Count);

            // svchost.exe's endpoint mapper is its one process drawn on its own, which its group says of one.
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svchost.exe");
            Assert.StartsWith("30 processes · 9 drawn as one node; Enter opens the group · 1 drawn on its own · 20 without a "
                + "relationship", workspace.SelectionSubtitle, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: at the smallest window, where names must stand nearer each other, none stands against another - at the machine, a group or a process rung")]
    public async Task CrowdedNamesKeepAWordsSpace()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyMachine());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            GraphView graph = window.GetControl<GraphView>("GraphSurface");

            // The machine, svchost.exe's group and its busiest instance: names nearer each other than they keep where a slot
            // leaves room - the smallest window's graph has too little - still keep a word's space and a pixel, since two
            // names against each other read as one - "svchost.exeOneDrive.exe".
            var wordSpace = new Thickness(4, 1);
            int crowded = 0;
            for (int rung = 0; rung < 3; rung++)
            {
                await workspace.LayoutReady;
                _ = Settle(window);
                IReadOnlyList<Rect> names = graph.PlacedLabels;
                Assert.NotEmpty(names);
                for (int one = 0; one < names.Count; one++)
                {
                    for (int other = one + 1; other < names.Count; other++)
                    {
                        Assert.False(names[one].Inflate(wordSpace).Intersects(names[other]),
                            $"{workspace.LevelBadge}: {graph.PlacedLabelKeys[one]} {names[one]} stands against "
                            + $"{graph.PlacedLabelKeys[other]} {names[other]}");
                        crowded += names[one].Inflate(GraphView.NameSpacing).Intersects(names[other]) ? 1 : 0;
                    }
                }

                workspace.SelectedRung = workspace.RungRows[0];
                Assert.True(rung == 2 || workspace.Descend(), workspace.LevelBadge);
            }

            Assert.NotEqual(0, crowded);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: an aggregate's edge to other hosts chosen in the table, Enter gives the keyboard to its processes in the inspector, as a double click on the edge does")]
    public async Task AnAggregatesEdgeToOtherHostsOpensItsList()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyMachine());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            workspace.ShowTables = true;
            Dispatch();
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            InterCat.Desktop.Presentation.RelationshipRow edge = workspace.TableRows.Single(row => row.Source == "Other hosts only");
            Assert.Equal("its processes in the inspector", edge.Opens);
            workspace.SelectedTableRow = edge;
            Dispatch();
            table.ScrollIntoView(table.SelectedItem!);
            Dispatch();
            Assert.True(table.ContainerFromItem(table.SelectedItem!)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.True(window.GetControl<ListBox>("MemberList").IsKeyboardFocusWithin);
            Assert.Equal("Other hosts only", workspace.SelectedCluster?.Label);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A pixel of a name's letters: the text's light ink, near white.</summary>
    private static bool IsLetter(Avalonia.Media.Color pixel) => Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) >= 200;

    /// <summary>A pixel of an edge's ink: a mechanism's hue, which neither the text nor the pane is.</summary>
    private static bool IsInk(Avalonia.Media.Color pixel) =>
        Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) >= 90
        && Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= 120;

    /// <summary>Whether the segment from <paramref name="from"/> to <paramref name="to"/> passes through <paramref name="box"/>.</summary>
    private static bool Crosses(Rect box, Point from, Point to)
    {
        for (int step = 0; step <= 200; step++)
        {
            double t = step / 200.0;
            if (box.Contains(new Point(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t))))
            {
                return true;
            }
        }

        return false;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How far a box stands from a point: from the box's nearest point.</summary>
    private static double Gap(Rect box, Point point) =>
        Distance(point, new(Math.Clamp(point.X, box.Left, box.Right), Math.Clamp(point.Y, box.Top, box.Bottom)));

    /// <summary>Whether a point lies on a box's edge.</summary>
    private static bool OnEdge(Rect box, Point point) =>
        box.Inflate(0.01).Contains(point) && !box.Deflate(0.01).Contains(point);

    /// <summary>
    /// A busy machine: thirty svchost.exe instances, twelve chrome.exe and other applications; loopback services the
    /// endpoint mapper, SQL Server, PostgreSQL and node.exe dev servers accept connections at; the web, from a dozen
    /// programs, a LAN's SMB and RDP, and DNS, mDNS, SSDP and a call over UDP.
    /// </summary>
    internal static ObservationRowV1[] BusyMachine()
    {
        var rows = new List<ObservationRowV1>();
        ulong ordinal = 1;
        long tick = 1;
        int port = 50000;
        void Process(int pid, string image) =>
            rows.Add(Lifecycle(tick++, ObservationKind.Create, pid, ordinal++) with { ResourceName = image });

        void Loopback(int client, int server, int serverPort, int transfers)
        {
            string local = $"127.0.0.1:{port++}";
            string remote = $"127.0.0.1:{serverPort}";
            rows.Add(Transfer(tick++, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, client, ordinal++).Between(local, remote));
            rows.Add(Transfer(tick++, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, server, ordinal++).Between(remote, local));
            for (int index = 0; index < transfers; index++)
            {
                rows.Add(Transfer(tick++, ObservationKind.Send, AccountingSide.SendSide, 400, client, ordinal++).Between(local, remote));
                rows.Add(Transfer(tick++, ObservationKind.Receive, AccountingSide.ReceiveSide, 400, server, ordinal++).Between(remote, local));
            }
        }

        void Out(int pid, string remote, int transfers)
        {
            string local = $"192.168.1.10:{port++}";
            rows.Add(Transfer(tick++, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, pid, ordinal++).Between(local, remote));
            for (int index = 0; index < transfers; index++)
            {
                rows.Add(Transfer(tick++, ObservationKind.Send, AccountingSide.SendSide, 700, pid, ordinal++).Between(local, remote));
            }
        }

        void In(int pid, int localPort, string remote)
        {
            string local = $"192.168.1.10:{localPort}";
            rows.Add(Transfer(tick++, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, pid, ordinal++).Between(local, remote));
            rows.Add(Transfer(tick++, ObservationKind.Receive, AccountingSide.ReceiveSide, 4000, pid, ordinal++).Between(local, remote));
        }

        void Udp(int pid, string remote, int sends)
        {
            string local = $"192.168.1.10:{port++}";
            for (int index = 0; index < sends; index++)
            {
                rows.Add(Transfer(tick++, ObservationKind.Send, AccountingSide.SendSide, 60, pid, ordinal++).Between(local, remote)
                    with { Mechanism = Mechanism.Udp, EventId = 42 });
            }
        }

        Process(4, @"C:\Windows\System32\ntoskrnl.exe");
        for (int index = 0; index < 30; index++)
        {
            Process(1000 + index, @"C:\Windows\System32\svchost.exe");
        }

        Process(1500, @"C:\Windows\System32\spoolsv.exe");
        Process(1600, @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe");
        Process(1700, @"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Binn\sqlservr.exe");
        Process(1800, @"C:\Windows\explorer.exe");
        Process(1900, @"C:\Windows\SystemApps\SearchHost.exe");
        for (int index = 0; index < 12; index++)
        {
            Process(2000 + index, @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        }

        for (int index = 0; index < 4; index++)
        {
            Process(2200 + index, @"C:\Users\me\AppData\Local\Microsoft\Teams\ms-teams.exe");
        }

        Process(2300, @"C:\Users\me\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
        for (int index = 0; index < 3; index++)
        {
            Process(2400 + index, @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe");
            Process(2500 + index, @"C:\Program Files\nodejs\node.exe");
        }

        Process(2600, @"C:\Python312\python.exe");
        Process(2601, @"C:\Python312\python.exe");
        Process(2700, @"C:\Program Files\Docker\Docker\resources\com.docker.backend.exe");
        Process(2800, @"C:\Program Files\PostgreSQL\16\bin\postgres.exe");
        Process(2801, @"C:\Program Files\PostgreSQL\16\bin\postgres.exe");
        Process(3000, @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE");
        for (int index = 0; index < 3; index++)
        {
            Process(3100 + index, @"C:\Users\me\AppData\Local\slack\app-4.41.105\slack.exe");
            Process(3700 + index, @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        }

        Process(3200, @"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe");
        Process(3300, @"C:\Users\me\AppData\Roaming\Zoom\bin\Zoom.exe");
        Process(3400, @"C:\Program Files (x86)\Dropbox\Client\Dropbox.exe");
        Process(3500, @"C:\Program Files (x86)\Steam\steam.exe");
        Process(3600, @"C:\Users\me\AppData\Local\Discord\app-1.0.9180\Discord.exe");

        foreach (int client in new[] { 1800, 1900, 2300, 2400, 1500, 1600, 2700 })
        {
            Loopback(client, 1003, 135, 1);
        }

        foreach (int client in new[] { 2600, 2601, 2500 })
        {
            Loopback(client, 1700, 1433, 3);
        }

        Loopback(2501, 2800, 5432, 4);
        Loopback(2502, 2801, 5432, 2);
        Loopback(2400, 2500, 3000, 6);
        Loopback(2401, 2501, 3001, 3);
        Loopback(2402, 2502, 9229, 2);
        Loopback(2000, 2700, 2375, 2);
        Loopback(2600, 2500, 3000, 2);

        string[] web = ["142.250.74.110:443", "172.217.16.142:443", "13.107.42.14:443", "20.42.65.92:443", "52.113.194.132:443",
            "151.101.1.69:443", "104.16.249.249:443", "140.82.112.4:443"];
        for (int index = 0; index < 12; index++)
        {
            Out(2000 + index, web[index % web.Length], 2);
        }

        for (int index = 0; index < 4; index++)
        {
            Out(2200 + index, "52.113.194.132:443", 3);
        }

        Out(2300, "13.107.42.14:443", 5);
        Out(1004, "20.42.65.92:443", 2);
        Out(1600, "140.82.112.4:443", 2);
        Out(3000, "52.96.165.18:443", 4);
        Out(3100, "3.120.218.106:443", 3);
        Out(3101, "3.120.218.106:443", 1);
        Out(3200, "35.186.224.25:443", 6);
        Out(3400, "162.125.19.131:443", 2);
        Out(3500, "23.33.45.12:443", 3);
        Out(3600, "162.159.135.232:443", 2);
        Out(3700, "204.79.197.200:443", 2);
        Out(3701, "13.107.21.200:443", 1);
        Udp(3300, "170.114.52.2:8801", 5);
        In(4, 445, "192.168.1.22:51000");
        In(1010, 3389, "192.168.1.30:52000");
        Udp(1007, "192.168.1.1:53", 8);
        for (int index = 0; index < 6; index++)
        {
            Udp(1008 + index, "224.0.0.251:5353", 2);
        }

        Udp(1015, "239.255.255.250:1900", 3);
        return [.. rows];
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        Settle(window).Save(Path.Combine(directory, name));
    }

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
