using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionIntervalQueryTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const string OtherClient = "127.0.0.1:50001";
    private const string OtherServer = "127.0.0.1:9090";

    [Fact]
    public void AnIntervalCountsOnlyItsOwnRecordsPerEdgeAndChannelAndRanksTheWorkspaceByThem()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            // An early burst on one connection, a later burst on another between different processes.
            Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 4).Between(OtherClient, OtherServer)),
            Timed(Transfer(51, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 400, 5).Between(OtherServer, OtherClient)),
            // A record with no usable session time cannot be placed in any interval.
            Transfer(52, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 6).Between(OtherClient, OtherServer),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot whole = OverviewWorkspace.From(overview);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        CommunicationEdge early = whole.Edges.Single(edge => edge.SourceId == client || edge.TargetId == client);
        CommunicationEdge late = whole.Edges.Single(edge => edge.Key != early.Key);
        Assert.Equal(3, early.ObservationCount);
        Assert.Equal(3, late.ObservationCount);

        SessionIntervalCounts burst = SessionIntervalQuery.Count(session.Store, new TimeRange(10, 13));
        Assert.Equal(3, burst.ObservedRows);
        Assert.Equal(3, burst.GraphRows);
        Assert.Equal(3, burst.EdgeRecords[early.Key]);
        Assert.False(burst.EdgeRecords.ContainsKey(late.Key));
        Assert.Equal(3, burst.ChannelRecords.Values.Sum());

        SessionIntervalCounts afterwards = SessionIntervalQuery.Count(session.Store, new TimeRange(13, 1_000));
        Assert.Equal(2, afterwards.ObservedRows);
        Assert.Equal(2, afterwards.EdgeRecords[late.Key]);

        // The scoped workspace keeps every edge and channel and counts only the interval: the ranking follows the brush.
        WorkspaceSnapshot scoped = OverviewWorkspace.WithinInterval(whole, afterwards);
        Assert.Equal(whole.Edges.Select(edge => edge.Key), scoped.Edges.Select(edge => edge.Key));
        Assert.Equal(0, scoped.Edges.Single(edge => edge.Key == early.Key).ObservationCount);
        Assert.Equal(2, scoped.Edges.Single(edge => edge.Key == late.Key).ObservationCount);
        Assert.Equal(whole.Timeline, scoped.Timeline);
        LadderView ranked = LadderProjection.Project(scoped, SyntheticWorkspace.Root(scoped));
        Assert.Equal(2, ranked.ObservationCount);
        Assert.Equal(2, ranked.Rows[0].ObservationCount);
    }

    [Fact]
    public void ARangeIsStatedInTheUnitItsSpanNeeds()
    {
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        const long Second = WorkspaceTime.TicksPerSecond;
        Assert.Equal("12.3 – 45.6 s", WorkspaceTime.FormatRange(new(123 * Second / 10, 456 * Second / 10), invariant));
        Assert.Equal("1.234 – 3.456 s", WorkspaceTime.FormatRange(new(1_234 * Second / 1_000, 3_456 * Second / 1_000), invariant));
        Assert.Equal("1.000 – 3.000 ms", WorkspaceTime.FormatRange(new(10_000, 30_000), invariant));
        Assert.Equal("1.0 – 3.0 µs", WorkspaceTime.FormatRange(new(10, 30), invariant));
        Assert.Equal("300.0 s", WorkspaceTime.FormatDuration(300 * Second, invariant));
        Assert.Equal("0.020 ms", WorkspaceTime.FormatDuration(200, invariant));
        Assert.Equal("12.3 s", WorkspaceTime.FormatInstant(123 * Second / 10, 60 * Second, invariant));
        Assert.Equal("1.5 µs", WorkspaceTime.FormatInstant(15, 20, invariant));
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
