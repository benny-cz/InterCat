using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How far a box stands from a point: from the box's nearest point.</summary>
    private static double Gap(Rect box, Point point) =>
        Distance(point, new(Math.Clamp(point.X, box.Left, box.Right), Math.Clamp(point.Y, box.Top, box.Bottom)));

    /// <summary>Whether a point lies on a box's edge.</summary>
    private static bool OnEdge(Rect box, Point point) =>
        box.Inflate(0.01).Contains(point) && !box.Deflate(0.01).Contains(point);

    /// <summary>
    /// A busy machine: thirty svchost.exe instances, twelve chrome.exe and other applications; loopback services the
    /// endpoint mapper, SQL Server, PostgreSQL and node.exe dev servers accept connections at; the web, a LAN's SMB and
    /// RDP, and DNS, mDNS and SSDP over UDP.
    /// </summary>
    private static ObservationRowV1[] BusyMachine()
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
