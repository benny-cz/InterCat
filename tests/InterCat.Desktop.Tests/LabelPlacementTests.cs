using Avalonia;
using InterCat.Desktop;
using Xunit;
using static InterCat.Desktop.GraphView;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Where the graph's names go (§6.3, §6.7): those a frame with nothing chosen draws, placed as it places them, then the
/// selection's and the keyboard's, which take a free slot or else the one they prefer, where unchosen names step aside.
/// </summary>
public sealed class LabelPlacementTests
{
    private static readonly Size Name = new(40, 14);

    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    [Fact(DisplayName = "§6.3: with nothing chosen aggregates are named first and then the busiest, up to the budget, and a chosen node's name joins them where they stand")]
    public void ChoosingANodeLeavesTheOtherNamesWhereTheyStood()
    {
        var pane = new Rect(0, 0, 400, 300);
        LabelRequest[] order = [Request(0, "first", 100, 100, rank: 1), Request(1, "group", 200, 100, rank: 0), Request(2, "last", 300, 100, rank: 1)];
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry(order);

        // The group first, then the first process in the keyboard's order: the budget of two leaves the last unnamed.
        List<LabelPlacement> unchosen = Place(pane, order, 2, [], null, points, radii);
        Assert.Equal(["group", "first"], unchosen.Select(label => label.Key));
        Assert.Equal(new Rect(180, 113, 40, 14), unchosen[0].Box);
        Assert.All(unchosen, label => Assert.False(label.Chosen));

        // Chosen, the last is named outside its selection's ring, beside the others where they stood.
        List<LabelPlacement> chosen = Place(pane, order, 2, ["last"], null, points, radii);
        Assert.Equal(unchosen, chosen.Take(2));
        Assert.Equal(new LabelPlacement(2, "last", new Rect(280, 119, 40, 14), Whole: true, Chosen: true), chosen[2]);

        // A chosen name already drawn stays, beyond its ring, or is placed again outside it: the group's, 3 px below its
        // disc, would stand across its ring, and is placed 3 px past the ring.
        List<LabelPlacement> group = Place(pane, order, 2, ["group"], null, points, radii);
        Assert.Equal(new LabelPlacement(1, "group", new Rect(180, 119, 40, 14), Whole: true, Chosen: true), group.Single(label => label.Key == "group"));
        Assert.Equal(unchosen[1], group.Single(label => label.Key == "first"));

        // The keyboard's node, chosen without a ring, keeps a name already drawn just where it stood.
        Assert.Equal(unchosen[0] with { Chosen = true }, Place(pane, order, 2, [], "group", points, radii)[0]);
    }

    [Fact(DisplayName = "§6.3: a chosen name takes a free slot before one an unchosen name holds, and takes that one only where none is free, the unchosen name stepping aside")]
    public void AnUnchosenNameStepsAsideOnlyWhereNoSlotIsFree()
    {
        // In a pane 50 px wide the chosen node's only slot clear of every node is below it, where its neighbour's name goes
        // with nothing chosen: chosen with another, it takes the slot, and the neighbour's name steps aside.
        var narrow = new Rect(0, 0, 50, 80);
        LabelRequest[] order = [Request(0, "neighbour", 25, 66, rank: 1), Request(1, "chosen", 25, 20, rank: 1), Request(2, "far", 25, 300, rank: 1)];
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry(order);
        LabelPlacement neighbour = Assert.Single(Place(narrow, order, 1, [], null, points, radii));
        Assert.Equal(("neighbour", new Rect(5, 39, 40, 14)), (neighbour.Key, neighbour.Box));
        List<LabelPlacement> chosen = Place(narrow, order, 1, ["chosen", "far"], null, points, radii);
        Assert.Equal([("chosen", new Rect(5, 39, 40, 14))], chosen.Select(label => (label.Key, label.Box)));

        // Where the pane leaves it a free slot above, it takes that, and the neighbour's name stays.
        var tall = new Rect(0, -60, 50, 140);
        List<LabelPlacement> free = Place(tall, order, 1, ["chosen", "far"], null, points, radii);
        Assert.Equal(neighbour, free[0]);
        Assert.Equal(("chosen", new Rect(5, -13, 40, 14)), (free[1].Key, free[1].Box));
    }

    [Fact(DisplayName = "§6.3: a chosen name never makes another chosen name step aside, and the keyboard's node is named even then, over a node rather than unnamed")]
    public void TheKeyboardsNodeIsNamedBesideAChosenOne()
    {
        // The neighbour, selected, keeps the only slot both could take; the keyboard's node is still named, below it.
        var narrow = new Rect(0, 0, 50, 80);
        LabelRequest[] order = [Request(0, "neighbour", 25, 66, rank: 1), Request(1, "focused", 25, 20, rank: 1)];
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry(order);
        List<LabelPlacement> placed = Place(narrow, order, 1, ["neighbour"], "focused", points, radii);
        Assert.Equal(("neighbour", new Rect(5, 33, 40, 14), true), (placed[0].Key, placed[0].Box, placed[0].Chosen));
        Assert.Equal(("focused", new Rect(5, 51, 40, 14), true), (placed[1].Key, placed[1].Box, placed[1].Chosen));

        // Without the keyboard on it, a node one of many chosen is left unnamed rather than stand over another chosen name.
        Assert.DoesNotContain(Place(narrow, order, 1, ["neighbour", "focused"], null, points, radii), label => label.Key == "focused");
    }

    [Fact(DisplayName = "§6.3: a name too tall with its second line is placed alone where only that fits")]
    public void ANameStandsAloneWhereItsSecondLineDoesNotFit()
    {
        var pane = new Rect(0, 0, 50, 38);
        LabelRequest request = Request(0, "node", 25, 10, rank: 0) with { Whole = new Size(40, 24) };
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry([request]);
        LabelPlacement placed = Assert.Single(Place(pane, [request], 1, [], null, points, radii));
        Assert.Equal((new Rect(5, 23, 40, 14), false), (placed.Box, placed.Whole));
    }

    [Fact(DisplayName = "§6.3: names keep room between them where a slot leaves it, so two never read as one, and stand nearer only where no slot does")]
    public void NamesKeepRoomBetweenThem()
    {
        // Side by side, the right node's name below would stand against the left's - "chrome.exechrome.exe" - so it is
        // named above instead.
        LabelRequest[] order = [Request(0, "left", 60, 30, rank: 1), Request(1, "right", 100, 30, rank: 1)];
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry(order);
        List<LabelPlacement> roomy = Place(new Rect(0, 0, 200, 100), order, 2, [], null, points, radii);
        Assert.Equal([("left", new Rect(40, 43, 40, 14)), ("right", new Rect(80, 3, 40, 14))],
            roomy.Select(label => (label.Key, label.Box)));

        // In a pane with no other slot it still takes the one below, since a name near another is better than none.
        List<LabelPlacement> tight = Place(new Rect(40, 40, 80, 20), order, 2, [], null, points, radii);
        Assert.Equal([("left", new Rect(40, 43, 40, 14)), ("right", new Rect(80, 43, 40, 14))],
            tight.Select(label => (label.Key, label.Box)));
    }

    [Fact(DisplayName = "§6.3: a chosen name with no slot that keeps its room takes the one it prefers, and an unchosen name it would stand against steps aside")]
    public void AnUnchosenNameStandingAgainstAChosenOneStepsAside()
    {
        // The neighbour's name stands above it, a pixel below the slot the chosen node's name prefers: chosen, that name
        // takes the slot and the neighbour's steps aside rather than read as one with it.
        var narrow = new Rect(0, 0, 50, 100);
        LabelRequest[] order = [Request(0, "neighbour", 25, 81, rank: 1), Request(1, "chosen", 25, 20, rank: 1), Request(2, "far", 25, 300, rank: 1)];
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry(order);
        LabelPlacement neighbour = Assert.Single(Place(narrow, order, 1, [], null, points, radii));
        Assert.Equal(("neighbour", new Rect(5, 54, 40, 14)), (neighbour.Key, neighbour.Box));
        List<LabelPlacement> chosen = Place(narrow, order, 1, ["chosen", "far"], null, points, radii);
        Assert.Equal([("chosen", new Rect(5, 39, 40, 14))], chosen.Select(label => (label.Key, label.Box)));
    }

    [Fact(DisplayName = "§6.7: the search's chosen hit's name stands outside its ring, as a selected node's stands outside the selection's")]
    public void AChosenHitsNameStandsOutsideItsRing()
    {
        var pane = new Rect(0, 0, 200, 200);
        LabelRequest[] order = [Request(0, "node", 100, 50, rank: 1)];
        (Dictionary<string, Point> points, Dictionary<string, double> radii) = Geometry(order);
        Rect Placed(NodeRings rings) => Single(pane, order, rings, points, radii).Box;

        // Nothing ringed, the name stands 3 px below the disc; selected, 3 px past the selection's ring; the chosen hit,
        // whole or in part and whether or not it is selected, 3 px past the hit's ring, outside the selection's.
        HashSet<string> node = new(["node"], StringComparer.Ordinal);
        Assert.Equal(new Rect(80, 63, 40, 14), Placed(new(None, None, None, None)));
        Assert.Equal(new Rect(80, 69, 40, 14), Placed(new(node, None, None, None)));
        Assert.Equal(new Rect(80, 73, 40, 14), Placed(new(None, None, node, None)));
        Assert.Equal(new Rect(80, 73, 40, 14), Placed(new(None, None, None, node)));
        Assert.Equal(new Rect(80, 73, 40, 14), Placed(new(node, None, node, None)));
    }

    private static LabelPlacement Single(Rect pane, IReadOnlyList<LabelRequest> order, NodeRings rings,
        Dictionary<string, Point> points, Dictionary<string, double> radii)
    {
        var placed = new List<LabelPlacement>();
        PlaceLabels(pane, order, 1, rings, null, points, radii, [], placed, [], []);
        return Assert.Single(placed);
    }

    private static LabelRequest Request(int index, string key, double x, double y, int rank) =>
        new(index, key, new Point(x, y), 10, rank, Name, Name);

    private static (Dictionary<string, Point>, Dictionary<string, double>) Geometry(IEnumerable<LabelRequest> requests) => (
        requests.ToDictionary(request => request.Key, request => request.Point, StringComparer.Ordinal),
        requests.ToDictionary(request => request.Key, request => request.Radius, StringComparer.Ordinal));

    private static List<LabelPlacement> Place(
        Rect pane,
        IReadOnlyList<LabelRequest> order,
        int budget,
        string[] selected,
        string? focused,
        Dictionary<string, Point> points,
        Dictionary<string, double> radii)
    {
        var placed = new List<LabelPlacement>();
        var boxes = new List<Rect>();
        PlaceLabels(pane, order, budget, new(new HashSet<string>(selected, StringComparer.Ordinal), None, None, None),
            focused, points, radii, [], placed, boxes, []);
        Assert.Equal(placed.Select(label => label.Box), boxes);
        return placed;
    }
}
