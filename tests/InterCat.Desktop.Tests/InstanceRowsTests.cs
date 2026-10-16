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
/// One program's instances at its group's rung (§6.1, §6.3): each row is named by its PID, the program's name being the
/// rung's own, and says first what the instance did - its relationships, and where its connections to other hosts ran,
/// naming the host where it reached one - then how it was seen to start.
/// </summary>
public sealed class InstanceRowsTests
{
    [Fact(DisplayName = "§6.3: one program's instances are named by their PIDs at its group's rung, each saying what it did, the host named where it reached one")]
    public void OneProgramsInstancesSayWhatEachDid() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // At the machine rung svc.exe is one row by its name, as every group is.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svc.exe");
        Assert.True(workspace.Descend());
        Assert.Equal(new Dictionary<string, string>
        {
            ["PID 300"] = "1 relationship · created during capture",
            ["PID 301"] = "1 relationship · created during capture",
            ["PID 302"] = "↔ 8.8.8.8:53 · created during capture",
            ["PID 303"] = "← 10.0.0.9 at port 445 · created during capture",
            ["PID 304"] = "2 TCP connections to 2 hosts · created during capture",
            ["PID 305"] = "↔ [2001:db8::1]:53 · created during capture",
            ["PID 306"] = "→ 20.0.0.3:8443 · created during capture",
            ["PID 307"] = "created during capture",
        }, workspace.RungRows.ToDictionary(row => row.Label, row => row.Detail));

        // A screen reader hears the instance's own name, and the tooltip and the crumb keep it.
        RungRow dns = workspace.RungRows.Single(row => row.Label == "PID 302");
        Assert.StartsWith("svc.exe · PID 302, ↔ 8.8.8.8:53 · created during capture, ", dns.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("PID 302\nsvc.exe", dns.Tip);
        workspace.SelectedRung = dns;
        Assert.True(workspace.Descend());
        Assert.Equal("Process: svc.exe · PID 302", workspace.Crumbs[^1].Label);

        // Brushed, the rows count the interval's records, and the connections, which a brush does not count again, say
        // they are the whole session's.
        workspace.ReturnTo(1);
        long start = workspace.Snapshot.Extent.StartTicks;
        workspace.SelectInterval(new TimeRange(start, start + 50));
        await workspace.IntervalReady;
        Assert.Equal("↔ 8.8.8.8:53 · whole session · created during capture",
            workspace.RungRows.Single(row => row.Label == "PID 302").Detail);
        Assert.Equal("1 relationship · created during capture", workspace.RungRows.Single(row => row.Label == "PID 300").Detail);

        // A program with one instance keeps its name on its one row.
        workspace.ReturnTo(0);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        RungRow client = Assert.Single(workspace.RungRows);
        Assert.Equal(("client.exe", "PID 100 · created during capture"), (client.Label, client.Detail));
    });

    [Fact(DisplayName = "§6.3: a group of several programs' processes names each row by its program, as ever")]
    public void SeveralProgramsKeepTheirNames()
    {
        using var workspace = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == "group.app");
        Assert.True(workspace.Descend());
        Assert.Equal(["API host", "Browser", "Cache"], workspace.RungRows.Select(row => row.Label).Order(StringComparer.Ordinal));
    }

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// svc.exe's eight instances: 300 connecting to 301 over loopback; 302 sending DNS to 8.8.8.8; 303 accepting a
    /// connection from 10.0.0.9 at its port 445; 304 connecting to two hosts; 305 sending to an IPv6 host; 306 connecting
    /// to one host at 8443; 307 only starting. client.exe alone reaches another host.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        ObservationRowV1 Udp(long tick, int pid, ulong ordinal, string local, string remote) =>
            Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 60, pid, ordinal).Between(local, remote)
                with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 1_000 + tick };
        ObservationRowV1 Tcp(long tick, ObservationKind kind, int pid, ulong ordinal, string local, string remote) =>
            Transfer(tick, kind, kind is ObservationKind.Send or ObservationKind.Receive
                    ? kind == ObservationKind.Send ? AccountingSide.SendSide : AccountingSide.ReceiveSide
                    : AccountingSide.EndpointActivity,
                kind is ObservationKind.Send or ObservationKind.Receive ? 100 : 0, pid, ordinal).Between(local, remote)
                with { SessionRelativeTicks = 1_000 + tick };
        return
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 10 },
            .. Enumerable.Range(0, 8).Select(index => Lifecycle(2 + index, ObservationKind.Create, 300 + index, (ulong)(2 + index))
                with { ResourceName = @"C:\Windows\System32\svc.exe", SessionRelativeTicks = 20 + index }),
            Tcp(100, ObservationKind.Connect, 300, 100, "127.0.0.1:50000", "127.0.0.1:9000"),
            Tcp(101, ObservationKind.Send, 300, 101, "127.0.0.1:50000", "127.0.0.1:9000"),
            Tcp(102, ObservationKind.Receive, 301, 102, "127.0.0.1:9000", "127.0.0.1:50000"),
            Udp(110, 302, 110, "192.168.1.10:51000", "8.8.8.8:53"),
            Udp(111, 302, 111, "192.168.1.10:51000", "8.8.8.8:53"),
            Tcp(120, ObservationKind.Accept, 303, 120, "192.168.1.10:445", "10.0.0.9:52000"),
            Tcp(121, ObservationKind.Receive, 303, 121, "192.168.1.10:445", "10.0.0.9:52000"),
            Tcp(130, ObservationKind.Connect, 304, 130, "192.168.1.10:52100", "20.0.0.1:443"),
            Tcp(131, ObservationKind.Send, 304, 131, "192.168.1.10:52100", "20.0.0.1:443"),
            Tcp(132, ObservationKind.Connect, 304, 132, "192.168.1.10:52101", "20.0.0.2:443"),
            Tcp(133, ObservationKind.Send, 304, 133, "192.168.1.10:52101", "20.0.0.2:443"),
            Udp(140, 305, 140, "[2001:db8::10]:51500", "[2001:db8::1]:53"),
            Tcp(150, ObservationKind.Connect, 306, 150, "192.168.1.10:52200", "20.0.0.3:8443"),
            Tcp(151, ObservationKind.Send, 306, 151, "192.168.1.10:52200", "20.0.0.3:8443"),
            Tcp(160, ObservationKind.Connect, 100, 160, "192.168.1.10:52300", "20.0.0.4:443"),
            Tcp(161, ObservationKind.Send, 100, 161, "192.168.1.10:52300", "20.0.0.4:443"),
        ];
    }
}
