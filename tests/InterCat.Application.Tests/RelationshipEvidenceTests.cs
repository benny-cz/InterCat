using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A relationship carries the rule that derived it, by identity and version, and the keys of what it rests on, so the edge
/// it draws can always be explained (§7.4, §6.3).
/// </summary>
public sealed class RelationshipEvidenceTests
{
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§7.4: a TCP relationship carries the rule that derived it and the key of each connection it rests on, which opens that connection's records at both its ends")]
    public void ATcpRelationshipCarriesItsRuleAndEvidence()
    {
        // Two connections between one client and one server: one relationship, resting on both.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 1)
                .Between("127.0.0.1:50000", ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 2)
                .Between(ServerEnd, "127.0.0.1:50000") with { SessionRelativeTicks = 2_000 },
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 50, 100, 3)
                .Between("127.0.0.1:50002", ServerEnd) with { SessionRelativeTicks = 3_000 },
            Transfer(40, ObservationKind.Receive, AccountingSide.ReceiveSide, 50, 200, 4)
                .Between(ServerEnd, "127.0.0.1:50002") with { SessionRelativeTicks = 4_000 },
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        CommunicationEdge edge = Assert.Single(overview.Edges);
        Assert.Equal(RelationRule.TransportEndpoint, edge.Rule);
        Assert.Equal(("transport-endpoint-relation", 4), (edge.Rule.Identity, edge.Rule.Version));
        Assert.Equal(TransportRelationIndex.RelationRule, edge.Rule.ToString());
        Assert.Equal(
            overview.Channels.Where(channel => channel.EdgeKey == edge.Key).Select(channel => channel.Key).Order(StringComparer.Ordinal),
            edge.Evidence);
        Assert.Equal(2, edge.Evidence.Count);
        string[] ends = [.. new[] { edge.SourceId, edge.TargetId }.Select(id => id.ToString()).Order(StringComparer.Ordinal)];
        foreach (string key in edge.Evidence)
        {
            SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, channelKey: key, resolveOwners: true);
            Assert.Equal(2, page.Records.Count);
            Assert.Equal(ends, page.Records.Select(record => record.Owner!.Instance!.Value.ToString()).Order(StringComparer.Ordinal));
        }

        // One generation read twice yields the same relationship, its evidence compared key by key.
        Assert.Equal(edge, Assert.Single(SessionOverviewProjector.Project(session.Store).Edges));
        Assert.NotEqual(edge, edge with { Evidence = [.. edge.Evidence.Take(1)] });
    }

    [Fact(DisplayName = "§7.4: the tour's relationships name a rule of their own and rest on the tour's channels, so none passes for a capture's")]
    public void TheToursRelationshipsSayTheyAreTheTours()
    {
        WorkspaceSnapshot tour = SyntheticWorkspace.Create();
        Assert.All(tour.Edges, edge =>
        {
            Assert.Equal(RelationRule.Tour, edge.Rule);
            Assert.Equal("synthetic-tour-v1", edge.Rule.ToString());
            Assert.NotEmpty(edge.Evidence);
            Assert.Equal(
                tour.Channels.Where(channel => channel.EdgeKey == edge.Key).Select(channel => channel.Key).Order(StringComparer.Ordinal),
                edge.Evidence);
        });
    }

    [Theory(DisplayName = "§7.4: a rule's recorded name reads back as its identity and version, and a name with no version is refused")]
    [InlineData("transport-endpoint-relation-v4", "transport-endpoint-relation", 4)]
    [InlineData("rpc-call-peer-v1", "rpc-call-peer", 1)]
    [InlineData("a-rule-v12", "a-rule", 12)]
    public void ARulesNameReadsBack(string recorded, string identity, int version)
    {
        RelationRule rule = RelationRule.Parse(recorded);
        Assert.Equal((identity, version), (rule.Identity, rule.Version));
        Assert.Equal(recorded, rule.ToString());
    }

    [Theory(DisplayName = "§7.4: a name that is not an identity, \"-v\" and a version from 1 names no rule")]
    [InlineData("transport-endpoint-relation")]
    [InlineData("-v4")]
    [InlineData("rule-v")]
    [InlineData("rule-v0")]
    [InlineData("rule-v04")]
    [InlineData("rule-v4b")]
    [InlineData("rule-v-4")]
    public void AMalformedNameIsRefused(string recorded) =>
        Assert.Throws<FormatException>(() => RelationRule.Parse(recorded));
}
