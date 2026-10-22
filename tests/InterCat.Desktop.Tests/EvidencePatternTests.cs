using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>How the graph marks a relation's evidence strength (§6.6, ADR-055).</summary>
public sealed class EvidencePatternTests
{
    [Fact(DisplayName = "R14: a relation weaker than correlated - candidate, unresolved or conflicting - is dotted and ringed, so none reads as asserted")]
    public void OnlyDirectAndCorrelatedRelationsReadAsAsserted()
    {
        // The pattern carries the strength without hue: solid, dashed, then dotted for everything weaker.
        Assert.Null(GraphView.EvidencePattern(RelationStrength.Direct));
        Assert.Equal((6d, 3d), GraphView.EvidencePattern(RelationStrength.Correlated));
        Assert.False(GraphView.RingsMiddle(RelationStrength.Direct));
        Assert.False(GraphView.RingsMiddle(RelationStrength.Correlated));

        // A conflicting relation is drawn only when a policy admits every strength. Unringed, it would read as more
        // asserted than a candidate, though its evidence disagrees with the join.
        foreach (RelationStrength weaker in new[] { RelationStrength.Candidate, RelationStrength.Unresolved, RelationStrength.Conflicting })
        {
            Assert.Equal((2d, 3d), GraphView.EvidencePattern(weaker));
            Assert.True(GraphView.RingsMiddle(weaker), $"{weaker} carries no ring.");
        }
    }
}
