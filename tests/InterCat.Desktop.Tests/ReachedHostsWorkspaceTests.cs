using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The other hosts a chosen process reached, in the inspector (§6.3): by address, the busiest first, as `icat processes
/// --pid` lists them, every one on request, and Enter on one finds every process that reached it.
/// </summary>
public sealed class ReachedHostsWorkspaceTests
{
    [Fact(DisplayName = "§6.3: a chosen process's inspector names the other hosts it reached, the busiest first, with what reached each, and finds who else reached one")]
    public void AChosenProcessNamesTheHostsItReached() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;
        // What the inspector last heard of them, so a binding is never left reading a stale list or heading.
        var heard = new List<int>();
        var headings = new List<string>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.ReachedHostRows))
            {
                heard.Add(workspace.ReachedHostRows.Count);
            }
            else if (changed.PropertyName == nameof(WorkspaceViewModel.ReachedHostsListName))
            {
                headings.Add(workspace.ReachedHostsListName);
            }
        };

        // Nothing chosen, nothing named.
        Assert.False(workspace.ShowsReachedHosts);
        Assert.Empty(workspace.ReachedHostRows);

        // The client reached seven hosts: the busiest five named, each with its scope where one is known, what reached it,
        // the ports at the address, the connection it accepted said apart at its own port, and its records.
        workspace.SelectProcess(Process(workspace, 100).Id);
        Assert.True(workspace.ShowsReachedHosts);
        Assert.Equal(
            [
                "203.0.113.1 | 2 TCP connections · port 443 · 4 records",
                "10.0.0.5 | private · 1 TCP connection · accepted here at port 3389 · 2 records",
                "198.51.100.7 | 1 TCP connection · port 80 · 2 records",
                "224.0.0.251 | multicast · 1 UDP flow · port 5353 · 1 record",
                "52.0.0.1 | 1 TCP connection · port 443 · 1 record",
            ],
            workspace.ReachedHostRows.Select(row => row.Address + " | " + row.Detail));
        Assert.Equal(5, heard[^1]);
        Assert.Equal("Other hosts it reached, the busiest first", workspace.ReachedHostsHeading);
        Assert.Equal("Other hosts it reached, the busiest first; Enter on one finds the processes that reached it",
            workspace.ReachedHostsListName);
        Assert.Equal("203.0.113.1, 2 TCP connections · port 443 · 4 records. Press Enter to find the processes that reached it.",
            workspace.ReachedHostRows[0].AccessibleName);

        // A button offers the other two, and then the busiest five again.
        Assert.True(workspace.OffersReachedHostsToggle);
        Assert.Equal("Show all 7 hosts", workspace.ReachedHostsToggleLabel);
        Assert.True(workspace.ToggleReachedHosts());
        Assert.True(workspace.ListsAllReachedHosts);
        Assert.Equal(["52.0.0.2", "52.0.0.3"], workspace.ReachedHostRows.Skip(5).Select(row => row.Address));
        Assert.Equal(7, heard[^1]);
        Assert.Equal("Show the busiest 5", workspace.ReachedHostsToggleLabel);
        Assert.False(workspace.ShowsReachedHostsMore);
        Assert.False(workspace.ToggleReachedHosts());
        Assert.False(workspace.ListsAllReachedHosts);
        Assert.Equal(5, heard[^1]);

        // Enter on one finds every process that reached it, as a search for its address does, the client still chosen; a
        // host the list does not hold finds nothing.
        Assert.True(workspace.FindReachedHost(workspace.ReachedHostRows[0]));
        Assert.Equal("203.0.113.1", workspace.SearchText);
        Assert.Equal(100, workspace.SelectedProcess?.ProcessId);
        Assert.False(workspace.FindReachedHost(new ReachedHostRow("192.0.2.1", "1 TCP connection")));
        Assert.Equal("203.0.113.1", workspace.SearchText);

        // Under a brush they are still the whole session's, and say so.
        workspace.SelectInterval(new TimeRange(workspace.Snapshot.Extent.StartTicks, workspace.Snapshot.Extent.StartTicks + 5));
        await workspace.IntervalReady;
        Assert.Equal("Other hosts it reached, the busiest first · whole session", workspace.ReachedHostsHeading);
        Assert.Equal("Other hosts it reached, the busiest first, over the whole session; Enter on one finds the processes "
            + "that reached it", headings[^1]);

        // The client's listed whole is the client's alone: a process that reached one host names it and offers no more, and
        // one that reached none names none and offers nothing; neither does a program.
        Assert.True(workspace.ToggleReachedHosts());
        workspace.SelectProcess(Process(workspace, 300).Id);
        Assert.False(workspace.ListsAllReachedHosts);
        Assert.False(workspace.OffersReachedHostsToggle);
        Assert.Equal(["198.51.100.7"], workspace.ReachedHostRows.Select(row => row.Address));
        workspace.SelectProcess(Process(workspace, 200).Id);
        Assert.False(workspace.ShowsReachedHosts);
        Assert.False(workspace.OffersReachedHostsToggle);
        Assert.False(workspace.ToggleReachedHosts());
        Assert.Equal(0, heard[^1]);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.False(workspace.ShowsReachedHosts);

        // At the client's own rung a connection chosen among its rows is what the inspector describes, and the client's
        // hosts give way to it.
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single();
        Assert.True(workspace.Descend());
        await workspace.ConnectionsReady;
        Assert.True(workspace.ShowsReachedHosts);
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.NotNull(workspace.DescribedRow);
        Assert.False(workspace.ShowsReachedHosts);
    });

    [Fact(DisplayName = "§6.3: a process that reached more than a hundred hosts lists a hundred on request and says where to read the rest, and a newer generation keeps them listed")]
    public void AHundredHostsAreListedAndTheRestSaid() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 900, 1) with { ResourceName = @"C:\Apps\crawler.exe", SessionRelativeTicks = 100 },
            .. Enumerable.Range(0, 101).Select(index => Transfer(200 + index, ObservationKind.Connect, AccountingSide.EndpointActivity,
                0, 900, (ulong)(10 + index)).Between($"192.168.1.10:{40000 + index}", $"52.0.{index / 100}.{index % 100}:443")
                with { SessionRelativeTicks = 200 + index }),
        ]);
        using WorkspaceViewModel workspace = Open(session, 1);
        await workspace.LayoutReady;
        workspace.SelectProcess(Process(workspace, 900).Id);
        Assert.Equal(5, workspace.ReachedHostRows.Count);
        Assert.Equal("Show the busiest 100 of 101 hosts", workspace.ReachedHostsToggleLabel);
        Assert.False(workspace.ShowsReachedHostsMore);
        Assert.True(workspace.ToggleReachedHosts());
        Assert.Equal(100, workspace.ReachedHostRows.Count);
        Assert.Equal("1 more, quieter: icat processes --pid 900 --json lists them all", workspace.ReachedHostsMore);
        Assert.True(workspace.ShowsReachedHostsMore);

        // A newer generation keeps them listed.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal(Process(workspace, 900).Id, saved.ReachedHostsListedWhole);
        using WorkspaceViewModel next = Open(session, 2);
        await next.LayoutReady;
        Assert.Null(next.RestoreNavigation(saved));
        Assert.Equal(100, next.ReachedHostRows.Count);
        Assert.True(next.ListsAllReachedHosts);
    });

    private static WorkspaceViewModel Open(TemporarySession session, int generation)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), $"{overview.GraphIdentity}:{generation}",
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    /// <summary>
    /// client.exe's PID 100 connects to 203.0.113.1's port 443 twice and sends on each, to 198.51.100.7's port 80 once and
    /// sends, and to 52.0.0.1, .2 and .3 once each; it accepts a connection from 10.0.0.5 at its port 3389 and receives on
    /// it, and sends a datagram to the multicast group 224.0.0.251's port 5353. server.exe's PID 200 only starts, and
    /// helper.exe's PID 300 connects to 198.51.100.7's port 80.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\helper.exe", SessionRelativeTicks = 120 },
        Transfer(70, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 60).Between("192.168.1.11:53000", "198.51.100.7:80")
            with { SessionRelativeTicks = 700 },
        .. Enumerable.Range(0, 2).SelectMany(index => new[]
        {
            Transfer(20 + (2 * index), ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, (ulong)(10 + (2 * index)))
                .Between($"192.168.1.10:{52000 + index}", "203.0.113.1:443") with { SessionRelativeTicks = 200 + (2 * index) },
            Transfer(21 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 100, 100, (ulong)(11 + (2 * index)))
                .Between($"192.168.1.10:{52000 + index}", "203.0.113.1:443") with { SessionRelativeTicks = 201 + (2 * index) },
        }),
        Transfer(30, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between("192.168.1.10:52010", "198.51.100.7:80")
            with { SessionRelativeTicks = 300 },
        Transfer(31, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 21).Between("192.168.1.10:52010", "198.51.100.7:80")
            with { SessionRelativeTicks = 310 },
        Transfer(40, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 100, 30).Between("192.168.1.10:3389", "10.0.0.5:50000")
            with { SessionRelativeTicks = 400 },
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 100, 31).Between("192.168.1.10:3389", "10.0.0.5:50000")
            with { SessionRelativeTicks = 410 },
        Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 40).Between("192.168.1.10:5353", "224.0.0.251:5353")
            with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 500 },
        .. Enumerable.Range(1, 3).Select(index => Transfer(60 + index, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100,
            (ulong)(50 + index)).Between($"192.168.1.10:{52100 + index}", $"52.0.0.{index}:443") with { SessionRelativeTicks = 600 + index }),
    ];
}
