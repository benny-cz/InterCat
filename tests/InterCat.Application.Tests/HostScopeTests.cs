using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// One other host's records as a scope (§6.3, §6.4): every record of the connections whose other end is its address,
/// whichever process held them, read by an evidence page, counted by the timeline's focus and measured in an interval's
/// bytes as one channel's are, and never a relationship's.
/// </summary>
public sealed class HostScopeTests
{
    private const string Host = "142.250.1.1";

    [Fact(DisplayName = "§6.4: a host's scope lists every record of the connections to its address, whichever process held them, and the timeline's focus counts the same")]
    public void AHostsScopeListsItsConnectionsRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessInstanceId browser = whole.Processes.Single(node => node.ProcessId == 300).Id;
        ProcessInstanceId updater = whole.Processes.Single(node => node.ProcessId == 310).Id;
        string key = HostScopeKeys.Of(Host);
        Assert.Equal("host:142.250.1.1", key);
        Assert.True(HostScopeKeys.IsHost(key));
        Assert.Equal(Host, HostScopeKeys.AddressOf(key));
        Assert.False(HostScopeKeys.IsHost("host:"));
        Assert.False(HostScopeKeys.IsHost(whole.Channels.Single().Key));

        // Every record of its four connections - the browser's two over TCP at two ports and one over UDP, the updater's
        // one - and none of another host's or of the loopback relationship.
        SessionEvidenceRecord[] records = Read(session.Store, key);
        Assert.Equal([12UL, 13UL, 14UL, 15UL, 17UL, 18UL, 19UL], records.Select(record => record.Observation.RawRecordOrdinal).Order());
        Assert.Equal(new[] { browser, updater }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            records.Select(record => record.Owner!.Instance!.Value.ToString()).Distinct().Order(StringComparer.Ordinal));
        Assert.Single(records, record => record.Observation.Mechanism == Mechanism.Udp);

        // Narrowed as a lane is, by owner or mechanism, it keeps those of its records alone.
        Assert.Equal(4, Read(session.Store, key, owners: [browser]).Length);
        Assert.Single(Read(session.Store, key, mechanism: Mechanism.Udp));

        // An IPv6 host is named as every surface writes it, in either case.
        Assert.Single(Read(session.Store, HostScopeKeys.Of("2001:DB8::1")));

        // The timeline's focus counts exactly the page's records.
        int Focused(TimelineFocus focus) => SessionTimelineQuery.Focused(session.Store, whole.Extent, 64, focus).Focus
            .Sum(bucket => bucket.ObservationCount);
        Assert.Equal(records.Length, Focused(new TimelineFocus(key, [])));
        Assert.Equal(4, Focused(new TimelineFocus(key, [browser])));

        // Its bytes are those records' bytes.
        SessionIntervalByteMeasures bytes = SessionIntervalByteQuery.Measure(session.Store, whole.Extent, 16,
            new IntervalByteScope { ChannelKey = key });
        Assert.Equal(500 + 300 + 70 + 40 + (3 * 100), bytes.Columns.Sum(column => column.SentBytes));
        Assert.Equal("records of the connections to 142.250.1.1", new IntervalByteScope { ChannelKey = key }.Description);
    }

    [Fact(DisplayName = "§6.4: a host's connections are listed with the processes that held them, their records and their bytes: exactly those its scope reads")]
    public void AHostsConnectionsAreListedWithTheirHolders()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessInstanceId browser = whole.Processes.Single(node => node.ProcessId == 300).Id;
        ProcessInstanceId updater = whole.Processes.Single(node => node.ProcessId == 310).Id;

        // Its four connections, the most records first: the updater's three sends, then the browser's two at 443, its one at
        // 8443 and its QUIC flow, holding the very records its scope lists.
        HostConnectionList listed = SessionConnections.ToHost(session.Store, " " + Host + " ");
        Assert.Equal(Host, listed.Address);
        Assert.Equal([3L, 2L, 1L, 1L], listed.Connections.Select(connection => connection.Summary.Records));
        Assert.Equal([updater, browser, browser, browser], listed.Connections.Select(connection => connection.Holder));
        Assert.Equal(Read(session.Store, HostScopeKeys.Of(Host)).Length, listed.Records);
        Assert.Equal(new OtherHost(Host, 3, 1, 2, 7), listed.Host);
        Assert.Equal("1,210 B sent, no receive recorded", listed.Transfers(System.Globalization.CultureInfo.InvariantCulture));

        // An IPv6 host is named in either case; an address no connection reached lists none, and a blank one is no address.
        Assert.Equal("[2001:db8::1]:443", Assert.Single(SessionConnections.ToHost(session.Store, "2001:DB8::1").Connections)
            .Summary.RemoteEndpoint);
        Assert.Empty(SessionConnections.ToHost(session.Store, "192.0.2.1").Connections);
        Assert.Throws<ArgumentException>(() => SessionConnections.ToHost(session.Store, " "));
    }

    [Fact(DisplayName = "§6.4: a host's scope has an identity of its own, refuses an end, and says so where no connection reaches its address")]
    public void AHostsScopeHasAnIdentityAndRefusesWhatItCannotRead()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        string key = HostScopeKeys.Of(Host);

        // A page's cursor continues it, and restarts another host's.
        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, key, pageSize: 3);
        Assert.Equal(3, first.Records.Count);
        SessionEvidencePage next = SessionEvidenceQuery.Read(session.Store, key, pageSize: 3, cursor: first.NextCursor);
        Assert.False(next.RestartRequired);
        Assert.True(SessionEvidenceQuery.Read(session.Store, HostScopeKeys.Of("20.42.65.1"), pageSize: 3, cursor: first.NextCursor)
            .RestartRequired);

        // A host's connections have no two ends to choose one of.
        Assert.Throws<ArgumentException>(() => new TimelineFocus(key, [], end: 0));
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, key, end: 0));
        Assert.Equal("A host's connections have no two ends to choose one of.",
            new IntervalByteScope { ChannelKey = key, End = 0 }.Validate());

        // An address no connection reached is said to be reached by none, rather than read as nothing.
        Assert.Contains("No connection to 192.0.2.1 is in",
            Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store, HostScopeKeys.Of("192.0.2.1"))).Message,
            StringComparison.Ordinal);
        Assert.Contains("No connection to 192.0.2.1 is in",
            Assert.Throws<InvalidOperationException>(() => SessionTimelineQuery.Focused(session.Store, new TimeRange(0, 1_000), 8,
                new TimelineFocus(HostScopeKeys.Of("192.0.2.1"), []))).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HostScopeKeys.AddressOf("connection:other"));
    }

    [Fact(DisplayName = "§6.4: an edge to other hosts' scope names its hosts and the processes whose connections ran to them, and reads theirs to any of its hosts")]
    public void AnEdgesScopeReadsItsProcessesConnectionsToItsHosts()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessInstanceId browser = whole.Processes.Single(node => node.ProcessId == 300).Id;

        // Its key names its hosts, each once, the lowest first, then its processes; with one host and none it is a host's.
        string key = HostScopeKeys.Of(["20.42.65.1", " 142.250.1.1", "142.250.1.1"], [browser]);
        Assert.Equal("host:142.250.1.1,20.42.65.1@" + ProcessSetFilter.KeyOf([browser]), key);
        Assert.True(HostScopeKeys.IsHost(key));
        Assert.Equal(["142.250.1.1", "20.42.65.1"], HostScopeKeys.AddressesOf(key));
        Assert.Equal("142.250.1.1,20.42.65.1", HostScopeKeys.AddressOf(key));
        Assert.Equal([browser], HostScopeKeys.HoldersOf(key));
        Assert.Equal("host:142.250.1.1,20.42.65.1", HostScopeKeys.HostsOf(key));
        Assert.Equal("2 hosts", HostScopeKeys.Named(key));
        Assert.Equal(HostScopeKeys.Of(Host), HostScopeKeys.Of([Host]));
        Assert.Equal(Host, HostScopeKeys.Named(HostScopeKeys.Of(Host)));
        Assert.Empty(HostScopeKeys.HoldersOf(HostScopeKeys.Of(Host)));
        Assert.Throws<ArgumentException>(() => HostScopeKeys.Of(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => HostScopeKeys.Of(["192.0.2.1,192.0.2.2"]));

        // Its hosts' records are any of theirs; its process's alone are its own connections', over TCP its TCP's.
        string hosts = HostScopeKeys.HostsOf(key);
        Assert.Equal(8, Read(session.Store, hosts).Length);
        Assert.Equal(5, Read(session.Store, hosts, owners: [browser]).Length);
        Assert.Equal(4, Read(session.Store, hosts, owners: [browser], mechanism: Mechanism.Tcp).Length);
        Assert.Equal(4, SessionTimelineQuery.Focused(session.Store, whole.Extent, 64,
            new TimelineFocus(hosts, [browser], mechanism: Mechanism.Tcp)).Focus.Sum(bucket => bucket.ObservationCount));
        Assert.Equal("records of the connections to 2 hosts", new IntervalByteScope { ChannelKey = hosts }.Description);

        // Hosts no connection reached are said so, by how many.
        string unreached = HostScopeKeys.Of(["192.0.2.1", "192.0.2.2"]);
        Assert.Contains("No connection to 2 hosts is in",
            Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store, unreached)).Message,
            StringComparison.Ordinal);
        Assert.Contains("No connection to 2 hosts is in",
            Assert.Throws<InvalidOperationException>(() => SessionTimelineQuery.Focused(session.Store, whole.Extent, 8,
                new TimelineFocus(unreached, []))).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.4: a reopened session's host focus counts its rows, never the persisted tiles' tallies of its processes' every record")]
    public void AReopenedSessionsHostFocusCountsItsRows()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        session.Store.ReleaseSegmentReaders();
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(reopened));
        ProcessInstanceId browser = whole.Processes.Single(node => node.ProcessId == 300).Id;
        Assert.NotNull(SessionTileIndex.NamedBy(reopened.Current!));

        // The tiles tally the browser's every record, a lifecycle and five sends; its connections to the host hold four.
        int Focused(TimelineFocus focus) => SessionTimelineQuery.Focused(reopened, whole.Extent, 64, focus).Focus
            .Sum(bucket => bucket.ObservationCount);
        Assert.Equal(6, Focused(new TimelineFocus(null, [browser])));
        Assert.Equal(4, Focused(new TimelineFocus(HostScopeKeys.Of(Host), [browser])));
        Assert.Equal(7, Focused(new TimelineFocus(HostScopeKeys.Of(Host), [])));
        reopened.ReleaseSegmentReaders();
    }

    private static SessionEvidenceRecord[] Read(SessionStore store, string channel, ProcessInstanceId[]? owners = null,
        Mechanism? mechanism = null) =>
        [.. SessionEvidenceQuery.ReadScope(store, 10_000, channel, ownerProcesses: owners, resolveOwners: true,
            mechanism: mechanism).Records];

    /// <summary>
    /// client.exe and server.exe related over loopback; browser.exe's TCP to 142.250.1.1 at 443, two records, and at 8443,
    /// one, its QUIC there, one, and its TCP to 20.42.65.1; updater.exe's TCP to 142.250.1.1, three records, and to an IPv6
    /// host.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Lifecycle(4, ObservationKind.Create, 310, 4) with { ResourceName = @"C:\Apps\updater.exe", SessionRelativeTicks = 130 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080")
            with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000")
            with { SessionRelativeTicks = 210 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12).Between("192.168.1.10:52000", "142.250.1.1:443")
            with { SessionRelativeTicks = 300 },
        Transfer(23, ObservationKind.Send, AccountingSide.SendSide, 300, 300, 13).Between("192.168.1.10:52000", "142.250.1.1:443")
            with { SessionRelativeTicks = 310 },
        Transfer(24, ObservationKind.Send, AccountingSide.SendSide, 70, 300, 14).Between("192.168.1.10:52001", "142.250.1.1:8443")
            with { SessionRelativeTicks = 320 },
        Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 15).Between("192.168.1.10:61000", "142.250.1.1:443")
            with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 330 },
        Transfer(26, ObservationKind.Send, AccountingSide.SendSide, 90, 300, 16).Between("192.168.1.10:52002", "20.42.65.1:443")
            with { SessionRelativeTicks = 340 },
        .. Enumerable.Range(0, 3).Select(index => Transfer(27 + index, ObservationKind.Send, AccountingSide.SendSide, 100, 310,
            (ulong)(17 + index)).Between("192.168.1.10:53000", "142.250.1.1:443") with { SessionRelativeTicks = 400 + index }),
        Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 60, 310, 20).Between("[2001:db8::10]:53001", "[2001:db8::1]:443")
            with { SessionRelativeTicks = 500 },
    ];
}
