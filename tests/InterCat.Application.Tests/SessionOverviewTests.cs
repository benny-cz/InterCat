using InterCat.Application;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionOverviewTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "R7: one leased generation yields a stable graph and timed observed-only timeline")]
    public void OneGenerationProjectsStableOverview()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 2)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 2_000 },
        ]);

        SessionOverviewBundle first = SessionOverviewProjector.Project(session.Store);
        SessionOverviewBundle again = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(first.GraphIdentity, again.GraphIdentity);
        Assert.Equal((2L, 0L, 0L),
            (first.ObservationRows, first.RowsWithoutSessionTime, first.UnresolvedTcpRows));
        Assert.Equal(2, first.Nodes.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<ProcessNode>)first.Nodes).Clear());
        CommunicationEdge edge = Assert.Single(first.Edges);
        Assert.Equal(2, edge.ObservationCount);
        Assert.Null(edge.KnownBytes);
        Assert.Equal(RelationStrength.Correlated, edge.Strength);
        Assert.Equal((long?)10, first.Extent?.StartTicks);
        Assert.Equal((long?)21, first.Extent?.EndTicks);
        Assert.Equal(2, first.Timeline.Sum(bucket => bucket.ObservationCount));
        Assert.All(first.Timeline, bucket =>
        {
            Assert.Equal(CoverageState.UnknownCoverage, bucket.Coverage);
            Assert.Null(bucket.KnownBytes);
        });
        Assert.Equal(first.Edges, again.Edges);
    }

    [Fact(DisplayName = "R21: an unresolved connection and a reading without session time are disclosed, not graphed")]
    public void OneSidedAndUntimedRowsStayVisibleAsLimitations()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 7, 300, 2)
                .Between("127.0.0.1:50001", "127.0.0.1:9090"),
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(2, overview.ObservationRows);
        Assert.Equal(1, overview.RowsWithoutSessionTime);
        Assert.Equal(2, overview.UnresolvedTcpRows);
        Assert.Empty(overview.Edges);
        Assert.Equal(2, overview.Nodes.Count);
        Assert.Single(overview.Timeline);
        Assert.Equal(1, overview.Timeline[0].ObservationCount);
        Assert.Contains(overview.Caveats, caveat => caveat.Contains("1 row has no usable session time", StringComparison.Ordinal));
        Assert.Contains(overview.Caveats, caveat => caveat.Contains("not rendered as guessed edges", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R22: candidate relations enter the overview only under an explicit candidate policy")]
    public void CandidateRelationNeedsOptIn()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 200, 1),
            Lifecycle(20, ObservationKind.Exit, 200, 2),
            Lifecycle(30, ObservationKind.Create, 200, 3),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4)
                .Between(ClientEnd, ServerEnd),
            Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 5)
                .Between(ServerEnd, ClientEnd),
        ]);

        SessionOverviewBundle conservative = SessionOverviewProjector.Project(session.Store);
        SessionOverviewBundle admitted = SessionOverviewProjector.Project(
            session.Store, EvidencePolicy.IncludeCandidates);
        Assert.Empty(conservative.Edges);
        Assert.Equal(1, conservative.RelationshipsNotAdmitted);
        Assert.Equal(RelationStrength.Candidate, Assert.Single(admitted.Edges).Strength);
        Assert.NotEqual(conservative.GraphIdentity, admitted.GraphIdentity);
    }
}
