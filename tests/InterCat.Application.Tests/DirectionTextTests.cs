using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// Who made a relationship's connections in words (§6.3, §6.6, §6.8): the line a card and an explanation state, the mark a
/// table sets between the ends, and what a row's name reads aloud, all from the same counts, so none of them disagrees
/// with the graph's arrowheads.
/// </summary>
public sealed class DirectionTextTests
{
    [Fact(DisplayName = "§6.8: a relationship's direction line says who connected to whom, counted where both did or some opened unseen, and display order only where none was seen or for RPC calls")]
    public void TheDirectionLineSaysWhoConnected()
    {
        // One way, the source or the target, in all of its connections where it has more than one.
        Assert.Equal("Direction: a connected to b", DirectionText.Line(Mechanism.Tcp, 1, 0, 1, "a", "b"));
        Assert.Equal("Direction: a connected to b, in all 3 of its connections", DirectionText.Line(Mechanism.Tcp, 3, 0, 3, "a", "b"));
        Assert.Equal("Direction: b connected to a, in all 2 of its connections", DirectionText.Line(Mechanism.Tcp, 0, 2, 2, "a", "b"));

        // Both ways, or some opened unseen: each way counted, and how many the capture saw open neither way.
        Assert.Equal("Direction: a connected to b in 2 connections, b connected to a in 1 connection; the capture saw 1 "
            + "connection open neither way", DirectionText.Line(Mechanism.Tcp, 2, 1, 4, "a", "b"));
        Assert.Equal("Direction: a connected to b in 1 connection, b connected to a in 1 connection",
            DirectionText.Line(Mechanism.Tcp, 1, 1, 2, "a", "b"));
        Assert.Equal("Direction: a connected to b in 2 connections; the capture saw 2 connections open neither way",
            DirectionText.Line(Mechanism.Tcp, 2, 0, 4, "a", "b"));
        Assert.Equal("Direction: b connected to a in 1 connection; the capture saw 1 connection open neither way",
            DirectionText.Line(Mechanism.Tcp, 0, 1, 2, "a", "b"));

        // None seen opening, as for a UDP flow, and RPC calls, whose ends' rows say which called.
        Assert.Equal("Direction: display order only, not who initiated or sent", DirectionText.Line(Mechanism.Udp, 0, 0, 3, "a", "b"));
        Assert.Equal("Direction: display order only, not who initiated or sent", DirectionText.Line(Mechanism.Tcp, 0, 0, 2, "a", "b"));
        Assert.Equal("Direction: display order only; each end's RPC rows say which calls and which served",
            DirectionText.Line(Mechanism.Rpc, 0, 0, 0, "a", "b"));
    }

    [Fact(DisplayName = "§6.8: a set of processes made the connections their ends connected by and accepted those the other ends did, one between two of them both, and their hosts' as the capture saw them open")]
    public void ASetsConnectionsAreCountedByWhoOpenedThem()
    {
        ProcessInstanceId a = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        ProcessInstanceId b = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        ProcessInstanceId c = new(Guid.Parse("00000000-0000-0000-0000-000000000003"));
        CommunicationEdge Edge(string key, ProcessInstanceId source, ProcessInstanceId target, int fromSource, int fromTarget) =>
            new(key, source, target, Mechanism.Tcp, 10, null, RelationStrength.Correlated)
            {
                Rule = RelationRule.TransportEndpoint,
                Evidence = [key],
                SourceConnected = fromSource,
                TargetConnected = fromTarget,
            };
        WorkspaceSnapshot snapshot = new("Opened", new TimeRange(0, 10), [], [], [Edge("ab", a, b, 3, 1), Edge("cb", c, b, 0, 2)],
            [], [], [], [])
        {
            HostLinks =
            [
                new HostLink(b, Mechanism.Tcp, [new HostAddress("203.0.113.1", 5, 9) { Connected = 1, Accepted = 4 }],
                    RelationStrength.Correlated),
            ],
        };

        // a made three and accepted one of b's; b accepted those three and c's none but made two to c and one to a, and
        // connected to one host and accepted four from it; c accepted b's two.
        Assert.Equal((3, 1), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { a }));
        Assert.Equal((1 + 2 + 1, 3 + 0 + 4), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { b }));
        Assert.Equal((0, 2), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { c }));

        // Drawn together, a connection between two of them is one they made and one they accepted.
        Assert.Equal((3 + 1 + 2 + 1, 1 + 3 + 4), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { a, b }));
        Assert.Equal((0, 0), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId>()));

        Assert.Equal("Connections: 3 made · 1 accepted, as the capture saw them open", DirectionText.OpenedLine(3, 1));
        Assert.Equal("Connections: 0 made · 2 accepted, as the capture saw them open", DirectionText.OpenedLine(0, 2));
        Assert.Null(DirectionText.OpenedLine(0, 0));
    }

    [Fact(DisplayName = "§6.6: a table's mark between a relationship's ends points as its connections ran, both ways where each end made some, and none where none was seen, as its name read aloud says")]
    public void TheMarkAndTheSpokenWordsFollowTheCounts()
    {
        Assert.Equal(["→", "←", "⇄", ""],
            new[] { (2, 0), (0, 1), (1, 3), (0, 0) }.Select(made => DirectionText.Glyph(made.Item1, made.Item2)));
        Assert.Equal("a connected to b", DirectionText.Spoken(2, 0, "a", "b"));
        Assert.Equal("b connected to a", DirectionText.Spoken(0, 1, "a", "b"));
        Assert.Equal("each connected to the other", DirectionText.Spoken(1, 3, "a", "b"));
        Assert.Null(DirectionText.Spoken(0, 0, "a", "b"));
    }
}
