using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The ranked table orders executable groups and processes by what each process was observed doing: its own records
/// (`process-activity-v1`, revision 166). It ranked by paired TCP alone before, so a busy process with no local peer read
/// as zero and sank below a quiet one that happened to talk to a neighbour.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class ProcessRankingTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "R13: groups and processes rank by their own records, so a process with no paired peer is ranked, not zero")]
    public void GroupsAndProcessesRankByTheirOwnRecords()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        ProcessNode busy = snapshot.Processes.Single(node => node.ProcessId == 500);
        ProcessNode client = snapshot.Processes.Single(node => node.ProcessId == 100);
        ProcessNode unnamed = snapshot.Processes.Single(node => node.ProcessId == 700);

        // The busy process talks to no local peer, so no edge is drawn for it, and it still ranks first, by its own UDP.
        LadderView machine = LadderProjection.Project(snapshot, SyntheticWorkspace.Root(snapshot));
        Assert.Equal(
            [(busy.GroupKey, 21L, Mechanism.Udp), (client.GroupKey, 6L, Mechanism.Tcp), (unnamed.GroupKey, 1L, Mechanism.Tcp)],
            machine.Rows.Select(row => (row.Key, row.ObservationCount, row.Mechanism)));
        Assert.Equal(["busy.exe", "pair.exe"], machine.Rows.Take(2).Select(row => row.Label));
        Assert.DoesNotContain(snapshot.Edges, edge => edge.SourceId == busy.Id || edge.TargetId == busy.Id);

        // A group's total is the sum of its processes', and the machine's the sum of its groups': every record a process
        // holds is counted once. The row naming no owner is held by none, and the overview says so.
        Assert.Equal(rows.Length - 1, machine.ObservationCount);
        Assert.Equal(rows.Length - 1, snapshot.Processes.Sum(node => node.Records));
        Assert.Equal(1, overview.RowsNoProcessHolds);
        Assert.Equal(1, snapshot.RowsNoProcessHolds);
        Assert.Contains(overview.Caveats, caveat => caveat.Contains("28 rows bind to a process instance", StringComparison.Ordinal)
            && caveat.Contains("The other 1 name no owner (1)", StringComparison.Ordinal)
            && caveat.Contains(ProcessActivityIndex.CountRule, StringComparison.Ordinal));
        foreach (LadderRow group in machine.Rows)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(group, ladder.Current, snapshot.Extent), out string? refusal), refusal);
            LadderView members = LadderProjection.Project(snapshot, ladder.Current);
            Assert.Equal(group.ObservationCount, members.ObservationCount);
            Assert.Equal(members.Rows.OrderByDescending(row => row.ObservationCount).Select(row => row.Key), members.Rows.Select(row => row.Key));
        }

        // Each process lists its own records by mechanism, most first; each end of the pair made half the exchange.
        Assert.Equal([new MechanismCount(Mechanism.Udp, 20), new MechanismCount(Mechanism.ProcessLifecycle, 1)], busy.Activity);
        Assert.All(snapshot.Processes.Where(node => node.ProcessId is 100 or 200), node =>
            Assert.Equal([new MechanismCount(Mechanism.Tcp, 2), new MechanismCount(Mechanism.ProcessLifecycle, 1)], node.Activity));
    }

    [Fact(DisplayName = "§6.4: a brushed interval ranks each process by its own records inside it, as the whole session does")]
    public void ABrushRanksEachProcessByItsOwnRecordsInside()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot whole = OverviewWorkspace.From(overview);

        // Over the whole extent every row is timed, so the brushed counts are the whole session's, process by process.
        SessionIntervalCounts all = SessionIntervalQuery.Count(session.Store, overview.Extent!.Value);
        WorkspaceSnapshot everything = OverviewWorkspace.WithinInterval(whole, all);
        Assert.Equal(Flatten(whole), Flatten(everything));
        Assert.Equal(whole.RowsNoProcessHolds, everything.RowsNoProcessHolds);

        // Inside the busy burst only the busy process made records: the others count zero, and nothing is left unheld.
        SessionIntervalCounts burst = SessionIntervalQuery.Count(session.Store, new TimeRange(10, 30));
        WorkspaceSnapshot scoped = OverviewWorkspace.WithinInterval(whole, burst);
        ProcessNode busy = scoped.Processes.Single(node => node.ProcessId == 500);
        Assert.Equal([new MechanismCount(Mechanism.Udp, 20)], busy.Activity);
        Assert.All(scoped.Processes.Where(node => node.ProcessId != 500), node => Assert.Equal(0, node.Records));
        Assert.Equal(0, scoped.RowsNoProcessHolds);
        LadderView machine = LadderProjection.Project(scoped, SyntheticWorkspace.Root(scoped));
        Assert.Equal([20L, 0L, 0L], machine.Rows.Select(row => row.ObservationCount));
        Assert.Equal(busy.GroupKey, machine.Rows[0].Key);
        Assert.Equal(Mechanism.UnknownMechanism, machine.Rows[1].Mechanism);

        // Inside the exchange, the row naming no owner is observed and held by no process.
        SessionIntervalCounts exchange = SessionIntervalQuery.Count(session.Store, new TimeRange(40, 51));
        WorkspaceSnapshot talking = OverviewWorkspace.WithinInterval(whole, exchange);
        Assert.Equal(4, talking.Processes.Sum(node => node.Records));
        Assert.Equal(1, talking.RowsNoProcessHolds);
    }

    [Fact(DisplayName = "R22: a brushed count admits a later instance's records only as the evidence policy does, as the whole count does")]
    public void ABrushAdmitsWhatThePolicyAdmits()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();

        // PID 100 exits and is created again. The first holder's send binds as correlated, the second's as a candidate,
        // since it could be a late record of the first; every creation and exit binds directly.
        Publish(session.Store,
        [
            Timed(Lifecycle(10, ObservationKind.Create, 100, 1)),
            Timed(Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between(ClientEnd, ServerEnd)),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 3)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 4)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
        ]);

        foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
        {
            SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store, policy);
            SessionIntervalCounts counts = SessionIntervalQuery.Count(session.Store, overview.Extent!.Value, policy);
            foreach (ProcessNode node in overview.Nodes)
            {
                Assert.Equal(node.Activity, counts.ProcessRecords.GetValueOrDefault(node.Id) ?? []);
            }

            long[] expected = policy switch
            {
                EvidencePolicy.DirectOnly => [1, 2],
                EvidencePolicy.IncludeCorrelated => [1, 3],
                _ => [2, 3],
            };
            Assert.Equal(expected, overview.Nodes.Select(node => node.Records).Order());
            Assert.Equal(5 - expected.Sum(), overview.RowsNoProcessHolds);
        }
    }

    [Fact(DisplayName = "R22: a reused PID's later holder carries how many of its records the evidence policy withholds as candidates, and a reopen says the same")]
    public void AReusedPidsLaterHolderCarriesWhatThePolicyWithholds()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();

        // PID 100 exits and is created again, and its second holder sends twice: each send could be a late record of the
        // first holder, so each binds only as a candidate. PID 200 is held once, and receives.
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1)),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2)),
            Timed(Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 4)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 5)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 6).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 7).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 8).Between(ClientEnd, ServerEnd)),
        ]);

        // By default the later holder counts its creation alone and carries the two sends the policy withheld; the first
        // holder and the PID held once withhold nothing. Each says which holder of its PID it was, and of how many.
        (int, int, int, long, long)[] byDefault = [(100, 1, 2, 3L, 0L), (100, 2, 2, 1L, 2L), (200, 1, 1, 2L, 0L)];
        Assert.Equal(byDefault, Holders(SessionOverviewProjector.Project(session.Store)));

        // A policy that admits candidates counts the sends and withholds none; one that admits only direct evidence
        // withholds every correlated record too, and counts only lifecycle records.
        Assert.Equal([(100, 1, 2, 3L, 0L), (100, 2, 2, 3L, 0L), (200, 1, 1, 2L, 0L)],
            Holders(SessionOverviewProjector.Project(session.Store, EvidencePolicy.IncludeCandidates)));
        Assert.Equal([(100, 1, 2, 2L, 1L), (100, 2, 2, 1L, 2L), (200, 1, 1, 1L, 1L)],
            Holders(SessionOverviewProjector.Project(session.Store, EvidencePolicy.DirectOnly)));

        // A fresh viewer of the finished session takes them from its checkpoint, opening no segment, and says the same.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        Assert.True(SessionDerivationCache.For(reopened.Current!).ActivityFromCheckpoint);
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Equal(byDefault, Holders(overview));
    }

    /// <summary>Each process by PID and which holder of it it was, of how many, with its counted and withheld records.</summary>
    private static (int, int, int, long, long)[] Holders(SessionOverviewBundle overview) =>
        [.. overview.Nodes
            .Select(node => (node.ProcessId, node.PidHolder, node.PidHolders, node.Records, node.WithheldRecords))
            .OrderBy(holder => holder.ProcessId)
            .ThenBy(holder => holder.PidHolder)];

    /// <summary>Every process's own records, by process and mechanism.</summary>
    private static (ProcessInstanceId Id, Mechanism Mechanism, long Records)[] Flatten(WorkspaceSnapshot snapshot) =>
        [.. snapshot.Processes.SelectMany(node => node.Activity.Select(entry => (node.Id, entry.Mechanism, entry.Records)))];

    /// <summary>
    /// A busy process (PID 500) sending twenty UDP datagrams to no local peer; a pair of one executable (PIDs 100 and
    /// 200) exchanging four TCP records; a process with no lifecycle record (PID 700) with one unpaired send; and a
    /// record naming no owner. Every row has a session time.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 500, 1) with { ResourceName = @"C:\Tools\busy.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\pair.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Tools\pair.exe" }),
        .. Enumerable.Range(0, 20).Select(index => Timed(
            Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 32, 500, (ulong)(10 + index))
                .Between("127.0.0.1:60000", "10.0.0.9:53") with { Mechanism = Mechanism.Udp })),
        Timed(Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 40).Between(ClientEnd, ServerEnd)),
        Timed(Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 41).Between(ServerEnd, ClientEnd)),
        Timed(Transfer(42, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 42).Between(ClientEnd, ServerEnd)),
        Timed(Transfer(43, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 43).Between(ServerEnd, ClientEnd)),
        Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, null, 50).Between(ClientEnd, ServerEnd)),
        Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 700, 60).Between("127.0.0.1:50009", "127.0.0.1:9999")),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
