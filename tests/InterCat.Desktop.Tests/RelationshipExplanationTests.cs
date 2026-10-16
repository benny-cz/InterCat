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
        Assert.EndsWith(", evidence direct, derived by synthetic-tour, version 1. Press Enter to open its source process, "
            + "whose rows list its 2 channels.", browser.AccessibleName, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.8: a relationship's row marks who connected between its ends, says it in the mark's tooltip, and reads it aloud, its ends joined by and rather than to")]
    public void ARelationshipRowSaysWhoConnected()
    {
        ProcessGroup group = new("tools", "tool.exe", LaneGrouping.Executable);
        ProcessNode client = Node(1, 100, group.Key);
        ProcessNode server = Node(2, 200, group.Key);
        ProcessNode helper = Node(3, 300, group.Key);
        CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target, Mechanism mechanism, int fromSource,
            int fromTarget, int connections) => new(key, source.Id, target.Id, mechanism, 10, null, RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [.. Enumerable.Range(0, connections).Select(index => $"{key}:{index}")],
            SourceConnected = fromSource,
            TargetConnected = fromTarget,
        };
        WorkspaceSnapshot snapshot = new("Makers", new TimeRange(0, 10), [group], [client, server, helper],
            [
                Edge("tcp:a", client, server, Mechanism.Tcp, 2, 1, 4),
                Edge("tcp:b", client, helper, Mechanism.Tcp, 0, 2, 2),
                Edge("udp:a", server, helper, Mechanism.Udp, 0, 0, 1),
            ], [], [], [], []);
        RelationshipRow Row(string key) =>
            WorkspaceRowBuilder.Relationships(snapshot, ThemeMode.Light).Single(row => row.Key == key);

        // Each way, counted: both ends marked, and read aloud as each connecting to the other.
        RelationshipRow both = Row("tcp:a");
        Assert.Equal("⇄", both.Direction);
        Assert.Equal("Direction: p1.exe · PID 100 connected to p2.exe · PID 200 in 2 connections, p2.exe · PID 200 connected "
            + "to p1.exe · PID 100 in 1 connection; the capture saw 1 connection open neither way", both.DirectionLine);
        Assert.StartsWith("p1.exe · PID 100 and p2.exe · PID 200 over ", both.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("p1.exe · PID 100 ⇄ p2.exe · PID 200", both.Name);
        Assert.Contains(", each connected to the other, 10 observations, ", both.AccessibleName, StringComparison.Ordinal);

        // The target alone: the mark points back at the source, and the name says the target connected.
        RelationshipRow back = Row("tcp:b");
        Assert.Equal(("←", "Direction: p3.exe · PID 300 connected to p1.exe · PID 100, in all 2 of its connections"),
            (back.Direction, back.DirectionLine));
        Assert.Contains(", p3.exe · PID 300 connected to p1.exe · PID 100, 10 observations, ", back.AccessibleName,
            StringComparison.Ordinal);
        Assert.Equal("p1.exe · PID 100 ← p3.exe · PID 300", back.Name);

        // None seen opening: no mark, display order only, and nothing said of it aloud.
        RelationshipRow flow = Row("udp:a");
        Assert.Equal(("", "Direction: display order only, not who initiated or sent", null),
            (flow.Direction, flow.DirectionLine, flow.SpokenDirection));
        Assert.Contains(" over UDP, 10 observations, ", flow.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("p2.exe · PID 200 ↔ p3.exe · PID 300", flow.Name);
    }

    private static ProcessNode Node(int index, int processId, string group) =>
        new(new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), processId, $"p{index}.exe", "test", group, 0.5,
            0.5, CoverageState.Covered);

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
