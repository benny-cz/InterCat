using System.Globalization;
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

    [Fact(DisplayName = "§6.8: a set of processes made and accepted its members' connections together, as each member's whole session counts them, and a card says nothing where the capture saw none of theirs open")]
    public void ASetsConnectionsAreItsMembers()
    {
        ProcessNode Node(int pid, long made, long accepted) => new(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{pid:D12}")), pid, $"p{pid}.exe", "Created", $"p{pid}", 0, 0,
            CoverageState.Covered) { ConnectionsMade = made, ConnectionsAccepted = accepted };
        ProcessNode a = Node(1, 3, 1);
        ProcessNode b = Node(2, 4, 7);
        ProcessNode c = Node(3, 0, 2);
        WorkspaceSnapshot snapshot = new("Opened", new TimeRange(0, 10), [], [a, b, c], [], [], [], [], []);

        Assert.Equal((3L, 1L), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { a.Id }));
        Assert.Equal((0L, 2L), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { c.Id }));

        // A connection between two of them is already one each member counts, the one that made it and the one that
        // accepted it, so the set's are its members' added.
        Assert.Equal((7L, 8L), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { a.Id, b.Id }));
        Assert.Equal((0L, 0L), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId>()));
        Assert.Equal((0L, 0L), DirectionText.Opened(snapshot, new HashSet<ProcessInstanceId> { Node(4, 9, 9).Id }));

        Assert.Equal("Connections: 3 made · 1 accepted, as the capture saw them open", DirectionText.OpenedLine(3, 1));
        Assert.Equal("Connections: 0 made · 2 accepted, as the capture saw them open", DirectionText.OpenedLine(0, 2));
        CultureInfo shown = CultureInfo.CurrentCulture;
        try
        {
            // A large count is grouped as the reader's culture groups digits.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Connections: 1,200 made · 0 accepted, as the capture saw them open", DirectionText.OpenedLine(1200, 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = shown;
        }

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

        // A card's title names the two ends as the table does: the maker first, whichever end is given first.
        Assert.Equal(["a → b", "b → a", "a ⇄ b", "a ↔ b"],
            new[] { (2, 0), (0, 1), (1, 3), (0, 0) }.Select(made => DirectionText.Joined(made.Item1, made.Item2, "a", "b")));
    }
}
