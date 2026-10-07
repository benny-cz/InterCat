using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §6.4's exact contributing evidence of a timeline cell: a page narrowed to one mechanism's records, one source
/// direction's, or those made at one end of a paired channel lists exactly what that lane counts, with an identity of its
/// own, and a timeline focus narrowed the same way counts exactly those records.
/// </summary>
public sealed class EvidenceNarrowingTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const int Exchanges = 12;

    [Fact(DisplayName = "§6.4: an evidence page narrowed as a timeline lane counts lists exactly that lane's records, and a timeline focus narrowed the same way counts them")]
    public void APageNarrowedAsALaneListsItsRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        ProcessInstanceId client = whole.Processes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId server = whole.Processes.Single(node => node.ProcessId == 200).Id;
        string channel = whole.Channels.Single().Key;
        SessionEvidenceRecord[] all = Read(session.Store);
        Assert.Equal(2 + (3 * Exchanges), all.Length);

        // One mechanism's records, whichever process they are bound to.
        SessionEvidenceRecord[] udp = Read(session.Store, mechanism: Mechanism.Udp);
        Assert.Equal(Ids(all.Where(record => record.Observation.Mechanism == Mechanism.Udp)), Ids(udp));
        Assert.Equal(Exchanges, udp.Length);

        // One source direction of one process's records.
        SessionEvidenceRecord[] owned = Read(session.Store, owners: [client]);
        SessionEvidenceRecord[] outbound = Read(session.Store, owners: [client], direction: Direction.Outbound);
        Assert.Equal(Ids(owned.Where(record => record.Observation.Direction == Direction.Outbound)), Ids(outbound));
        Assert.Equal(2 * Exchanges, outbound.Length);
        Assert.Equal(Ids(owned.Where(record => record.Observation.Kind == ObservationKind.Create)),
            Ids(Read(session.Store, owners: [client], direction: Direction.DirectionNotApplicable)));

        // The two ends of the paired channel part its records, each end's bound to the process that holds it.
        SessionEvidenceRecord[] both = Read(session.Store, channel: channel);
        SessionEvidenceRecord[] first = Read(session.Store, channel: channel, end: 0);
        SessionEvidenceRecord[] second = Read(session.Store, channel: channel, end: 1);
        string[] parted = [.. Ids(first).Concat(Ids(second)).Order(StringComparer.Ordinal)];
        Assert.Equal(Ids(both), parted);
        Assert.Equal(Exchanges, first.Length);
        Assert.Equal(Exchanges, second.Length);
        Assert.NotEqual(Assert.Single(first.Select(record => record.Owner!.Instance).Distinct()),
            Assert.Single(second.Select(record => record.Owner!.Instance).Distinct()));
        Assert.All([.. first, .. second], record => Assert.Contains(record.Owner!.Instance!.Value, new[] { client, server }));

        // A timeline focus narrowed the same way counts exactly the page's records, as its lane does.
        int Focused(TimelineFocus focus) => SessionTimelineQuery.Focused(session.Store, whole.Extent, 64, focus).Focus
            .Sum(bucket => bucket.ObservationCount);
        Assert.Equal(udp.Length, Focused(new TimelineFocus(null, [], mechanism: Mechanism.Udp)));
        Assert.Equal(outbound.Length, Focused(new TimelineFocus(null, [client], direction: Direction.Outbound)));
        Assert.Equal(first.Length, Focused(new TimelineFocus(channel, [], end: 0)));
        Assert.Equal(second.Length, Focused(new TimelineFocus(channel, [], end: 1)));
        Assert.Equal(Exchanges, Focused(new TimelineFocus(null, [client], mechanism: Mechanism.Udp, direction: Direction.Outbound)));
    }

    [Fact(DisplayName = "§6.4: a narrowed page has an identity of its own, so its cursor continues it and restarts another query, and an end narrows only a paired channel")]
    public void ANarrowedPageHasAnIdentityOfItsOwn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        string channel = whole.Channels.Single().Key;

        // Its next page continues it, listing only its own records; another query asks for a restart.
        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, pageSize: 5, mechanism: Mechanism.Udp);
        Assert.Equal(Mechanism.Udp, first.Mechanism);
        SessionEvidencePage next = SessionEvidenceQuery.Read(session.Store, pageSize: 5, cursor: first.NextCursor,
            mechanism: Mechanism.Udp);
        Assert.False(next.RestartRequired);
        Assert.Equal(5, next.Records.Count);
        Assert.All(next.Records, record => Assert.Equal(Mechanism.Udp, record.Observation.Mechanism));
        Assert.True(SessionEvidenceQuery.Read(session.Store, pageSize: 5, cursor: first.NextCursor).RestartRequired);
        Assert.True(SessionEvidenceQuery.Read(session.Store, pageSize: 5, cursor: first.NextCursor, mechanism: Mechanism.Tcp)
            .RestartRequired);
        Assert.NotEqual(SessionEvidenceQuery.Read(session.Store, channel, end: 0).QueryIdentity,
            SessionEvidenceQuery.Read(session.Store, channel, end: 1).QueryIdentity);

        // An end narrows a paired channel's records, and nothing else; there are two of them.
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, end: 0));
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, "connection:other", end: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionEvidenceQuery.Read(session.Store, channel, end: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionEvidenceQuery.Read(session.Store, mechanism: (Mechanism)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionEvidenceQuery.Read(session.Store, direction: (Direction)99));
        Assert.Throws<ArgumentException>(() => new TimelineFocus(null, [], end: 0));
        Assert.Throws<ArgumentException>(() => new TimelineFocus(null, []));
    }

    private static SessionEvidenceRecord[] Read(SessionStore store, string? channel = null, ProcessInstanceId[]? owners = null,
        Mechanism? mechanism = null, Direction? direction = null, int? end = null) =>
        [.. SessionEvidenceQuery.ReadScope(store, 10_000, channel, ownerProcesses: owners, resolveOwners: true,
            mechanism: mechanism, direction: direction, end: end).Records];

    private static string[] Ids(IEnumerable<SessionEvidenceRecord> records) =>
        [.. records.Select(record => record.ObservationId.ToString()!).Order(StringComparer.Ordinal)];

    /// <summary>client.exe sending to server.exe over one paired connection, which receives each send, and a datagram after each.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, Exchanges).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                (ulong)(100 + (3 * index))).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11 + (5 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (3 * index))).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(12 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 16, 100,
                (ulong)(102 + (3 * index))).Between("127.0.0.1:50001", "127.0.0.1:53") with { Mechanism = Mechanism.Udp }),
        }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
