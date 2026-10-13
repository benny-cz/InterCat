using InterCat.Application;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.8's motion of the graph's nodes: a new place is reached along one eased path over the level change's duration and
/// drawn exactly there once it has passed; reduced motion draws it there at once; a change mid-motion starts from where a
/// node is drawn; and nothing moves when no place changes.
/// </summary>
public sealed class GraphMotionTests
{
    private static readonly TimeSpan Length = TimeSpan.FromMilliseconds(200);
    private static readonly GraphPoint A = new(0.2, 0.3);
    private static readonly GraphPoint B = new(0.8, 0.7);
    private static readonly GraphPoint C = new(0.1, 0.9);

    [Fact(DisplayName = "§6.8: a node moves from where it is drawn to its new place along an eased path, and is drawn exactly there once the duration has passed")]
    public void ANodeMovesAlongAnEasedPathAndEndsExactlyAtItsPlace()
    {
        var motion = new GraphMotion();
        Assert.Null(motion.At("a", At(0)));

        // A drawing's first places are drawn at once: nothing was drawn anywhere else before them.
        motion.MoveTo(Places(("a", A)), At(0), Length, reduced: false);
        Assert.Equal(A, motion.At("a", At(0)));
        Assert.False(motion.IsMoving(At(0)));

        motion.MoveTo(Places(("a", B)), At(1_000), Length, reduced: false);
        Assert.Equal(A, motion.At("a", At(1_000)));
        Assert.True(motion.IsMoving(At(1_000)));

        // Halfway through its time the node has covered most of its way, as an eased move does, and every later moment is
        // nearer its place than the one before.
        GraphPoint half = motion.At("a", At(1_100))!.Value;
        Assert.Equal(A.X + ((B.X - A.X) * 0.875), half.X, 12);
        Assert.Equal(A.Y + ((B.Y - A.Y) * 0.875), half.Y, 12);
        double[] distances = [.. Enumerable.Range(0, 21).Select(step => Distance(motion.At("a", At(1_000 + (step * 10)))!.Value, B))];
        Assert.All(distances.Zip(distances.Skip(1)), pair => Assert.True(pair.Second <= pair.First));

        // At its duration it is drawn exactly at its place, not a rounding away from it, and it stays there.
        Assert.Equal(B, motion.At("a", At(1_200)));
        Assert.Equal(B, motion.At("a", At(9_000)));
        Assert.False(motion.IsMoving(At(1_200)));
        Assert.True(motion.IsMoving(At(1_199)));
    }

    [Fact(DisplayName = "§6.8: under reduced motion a new place is drawn at once")]
    public void ReducedMotionDrawsANewPlaceAtOnce()
    {
        var motion = new GraphMotion();
        motion.MoveTo(Places(("a", A)), At(0), Length, reduced: true);
        motion.MoveTo(Places(("a", B)), At(1_000), Length, reduced: true);
        Assert.Equal(B, motion.At("a", At(1_000)));
        Assert.False(motion.IsMoving(At(1_000)));

        // A motion already under way when motion is reduced ends at once at the next change, where that change ends.
        motion.MoveTo(Places(("a", C)), At(2_000), Length, reduced: false);
        Assert.True(motion.IsMoving(At(2_050)));
        motion.MoveTo(Places(("a", A)), At(2_050), Length, reduced: true);
        Assert.Equal(A, motion.At("a", At(2_050)));
        Assert.False(motion.IsMoving(At(2_050)));

        // No length is no motion either.
        motion.MoveTo(Places(("a", B)), At(3_000), TimeSpan.Zero, reduced: false);
        Assert.Equal(B, motion.At("a", At(3_000)));
    }

    [Fact(DisplayName = "§6.8: a change mid-motion starts from where the node is drawn, never from where it was going")]
    public void AChangeMidMotionStartsFromWhereTheNodeIsDrawn()
    {
        var motion = new GraphMotion();
        motion.MoveTo(Places(("a", A)), At(0), Length, reduced: false);
        motion.MoveTo(Places(("a", B)), At(0), Length, reduced: false);
        GraphPoint drawn = motion.At("a", At(100))!.Value;

        motion.MoveTo(Places(("a", C)), At(100), Length, reduced: false);
        Assert.Equal(drawn, motion.At("a", At(100)));
        Assert.True(Distance(motion.At("a", At(150))!.Value, C) < Distance(drawn, C));
        Assert.Equal(C, motion.At("a", At(300)));
    }

    [Fact(DisplayName = "§6.8: a node new to the drawing appears at its place, a node gone is forgotten, and places that do not change move nothing")]
    public void NewNodesAppearInPlaceAndUnchangedPlacesMoveNothing()
    {
        var motion = new GraphMotion();
        motion.MoveTo(Places(("a", A)), At(0), Length, reduced: false);
        motion.MoveTo(Places(("a", B), ("b", C)), At(1_000), Length, reduced: false);
        Assert.Equal(C, motion.At("b", At(1_000)));
        Assert.Equal(A, motion.At("a", At(1_000)));

        motion.MoveTo(Places(("b", C)), At(2_000), Length, reduced: false);
        Assert.Null(motion.At("a", At(2_000)));

        // A later drawing with every node where it was - more records, the same places - moves nothing: nothing animates
        // per message.
        IReadOnlyDictionary<string, GraphPoint> same = Places(("b", C));
        motion.MoveTo(same, At(3_000), Length, reduced: false);
        Assert.False(motion.IsMoving(At(3_000)));
        Assert.Same(same, motion.Places);
    }

    [Fact(DisplayName = "§6.8: a later drawing's node goes on toward the place it still has; one placed elsewhere, or new to it, is drawn at its place at once")]
    public void AnAdoptedDrawingGoesOnOnlyTowardThePlacesItKeeps()
    {
        var motion = new GraphMotion();
        motion.MoveTo(Places(("a", A), ("b", A)), At(0), Length, reduced: false);
        motion.MoveTo(Places(("a", B), ("b", B)), At(0), Length, reduced: false);
        GraphPoint before = motion.At("a", At(100))!.Value;

        // The next publication remembers the place "a" was moving to, read back through the layout's units, so its last
        // bits differ; "b" is placed elsewhere and "c" is new.
        var kept = new GraphPoint(B.X + 1e-12, B.Y - 1e-12);
        motion.Adopt(Places(("a", kept), ("b", C), ("c", A)), At(50));
        Assert.True(Distance(before, motion.At("a", At(100))!.Value) < 1e-9, "The adopted move did not go on from where it was.");
        Assert.Equal(kept, motion.At("a", At(200)));
        Assert.Equal(C, motion.At("b", At(50)));
        Assert.Equal(A, motion.At("c", At(50)));

        // Another session's drawing takes nothing from this one's moves.
        motion.Adopt(Places(("a", C)), At(60));
        Assert.Equal(C, motion.At("a", At(60)));
        Assert.False(motion.IsMoving(At(60)));
    }

    [Fact(DisplayName = "§6.8: a dropped node is drawn where it was dropped, and a cancelled drag's node is carried back to its place")]
    public void ADroppedNodeStaysWhereItWasDroppedAndACancelledOneGoesBack()
    {
        var motion = new GraphMotion();
        motion.MoveTo(Places(("a", A)), At(0), Length, reduced: false);
        motion.MoveTo(Places(("a", B)), At(0), Length, reduced: false);
        motion.Place("a", C, At(50));
        Assert.Equal(C, motion.At("a", At(50)));
        Assert.Equal(C, motion.At("a", At(500)));
        Assert.False(motion.IsMoving(At(50)));

        // A node no drawing has is not placed.
        motion.Place("z", C, At(50));
        Assert.Null(motion.At("z", At(50)));

        // A drag cancelled away from the node's place carries it back from where the pointer left it.
        motion.Return("a", A, At(1_000), Length, reduced: false);
        Assert.Equal(A, motion.At("a", At(1_000)));
        Assert.True(motion.IsMoving(At(1_100)));
        Assert.Equal(C, motion.At("a", At(1_200)));

        motion.Return("a", A, At(2_000), Length, reduced: true);
        Assert.Equal(C, motion.At("a", At(2_000)));
        Assert.False(motion.IsMoving(At(2_000)));

        // Finished, a move under way is drawn where it ends.
        motion.MoveTo(Places(("a", B)), At(3_000), Length, reduced: false);
        motion.Finish();
        Assert.Equal(B, motion.At("a", At(3_000)));
        Assert.False(motion.IsMoving(At(3_000)));
    }

    [Fact(DisplayName = "§6.8: the easing starts at nothing, ends at exactly all, and never goes back")]
    public void TheEasingIsMonotoneAndExactAtItsEnds()
    {
        Assert.Equal(0, GraphMotion.EaseOut(0));
        Assert.Equal(1, GraphMotion.EaseOut(1));
        Assert.Equal(0, GraphMotion.EaseOut(-0.5));
        Assert.Equal(1, GraphMotion.EaseOut(1.5));
        double[] eased = [.. Enumerable.Range(0, 101).Select(step => GraphMotion.EaseOut(step / 100.0))];
        Assert.All(eased.Zip(eased.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static Dictionary<string, GraphPoint> Places(params (string Key, GraphPoint Place)[] places) =>
        places.ToDictionary(place => place.Key, place => place.Place, StringComparer.Ordinal);

    private static double Distance(GraphPoint from, GraphPoint to) => Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
}
