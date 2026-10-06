using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A relationship's row explains it as its edge's card does (§6.3): its evidence, the rule and version that derived it,
/// and the channels it rests on, in the evidence column's tooltip and to a screen reader.
/// </summary>
public sealed class RelationshipExplanationTests
{
    private static readonly ProcessInstanceId First = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly ProcessInstanceId Second = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));

    [Fact(DisplayName = "§6.3: a relationship's row says the rule that derived it and the channels it rests on, in its evidence's tooltip and to a screen reader")]
    public void ARelationshipRowExplainsItself()
    {
        RelationshipRow browser = WorkspaceRowBuilder.Relationships(SyntheticWorkspace.Create(), ThemeMode.Light)
            .Single(row => row.Source.StartsWith("Browser", StringComparison.Ordinal));
        Assert.Equal("synthetic-tour, version 1", browser.Rule);
        Assert.Equal("Direct evidence, derived by synthetic-tour, version 1, from 2 channels: chan.health, chan.https.", browser.Explanation);
        Assert.EndsWith(", evidence direct, derived by synthetic-tour, version 1", browser.AccessibleName, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.3: a relationship resting on many channels names its first three and counts the rest, and an RPC one names the key that opens its linked calls")]
    public void AnExplanationNamesItsFirstChannelsAndCountsTheRest()
    {
        var many = new CommunicationEdge("tcp:a:b", First, Second, Mechanism.Tcp, 10, null, RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = ["transport:1", "transport:2", "transport:3", "transport:4", "transport:5"],
        };
        Assert.Equal(
            "Correlated evidence, derived by transport-endpoint-relation, version 4, from 5 channels: transport:1, transport:2, "
                + "transport:3, and 2 more.",
            WorkspaceRowBuilder.Explain(many));

        CommunicationEdge one = many with { Evidence = ["transport:1"] };
        Assert.Equal("Correlated evidence, derived by transport-endpoint-relation, version 4, from 1 channel: transport:1.",
            WorkspaceRowBuilder.Explain(one));

        string key = $"rpc:{First}:{Second}";
        CommunicationEdge rpc = many with { Key = key, Mechanism = Mechanism.Rpc, Rule = RelationRule.RpcCallPeer, Evidence = [key] };
        Assert.Equal($"Correlated evidence, derived by rpc-call-peer, version 1, from the calls its links join: {key}.",
            WorkspaceRowBuilder.Explain(rpc));
        Assert.Equal("Correlated evidence, derived by rpc-call-peer, version 1, from no evidence this overview keeps.",
            WorkspaceRowBuilder.Explain(rpc with { Evidence = [] }));
    }
}
