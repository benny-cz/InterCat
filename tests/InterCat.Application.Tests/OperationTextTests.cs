using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class OperationTextTests
{
    [Fact]
    public void ADurationKeepsItsFiguresInTheUnitItNeeds()
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Assert.Equal("0 ns", OperationText.Duration(0, invariant));
        Assert.Equal("999 ns", OperationText.Duration(999, invariant));
        Assert.Equal("1.0 µs", OperationText.Duration(1_000, invariant));
        Assert.Equal("4.8 µs", OperationText.Duration(4_800, invariant));
        Assert.Equal("26.1 µs", OperationText.Duration(26_100, invariant));
        Assert.Equal("99.9 µs", OperationText.Duration(99_940, invariant));
        Assert.Equal("100 µs", OperationText.Duration(99_950, invariant));
        Assert.Equal("457 µs", OperationText.Duration(456_600, invariant));

        // A value that would round to 1,000 of a unit is written in the next one.
        Assert.Equal("999 µs", OperationText.Duration(999_499, invariant));
        Assert.Equal("1.0 ms", OperationText.Duration(999_500, invariant));
        Assert.Equal("12.3 ms", OperationText.Duration(12_345_678, invariant));
        Assert.Equal("1.2 s", OperationText.Duration(1_234_000_000, invariant));
        Assert.Equal("4,8 µs", OperationText.Duration(4_800, CultureInfo.GetCultureInfo("cs-CZ")));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => OperationText.Duration(-1, invariant));
    }

    [Fact(DisplayName = "R3: a bound is written rounded up at the precision written, so it never reads smaller than it is")]
    public void ABoundNeverReadsSmallerThanItIs()
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Assert.Equal("0 ns", OperationText.DurationAtLeast(0, invariant));
        Assert.Equal("1 ns", OperationText.DurationAtLeast(0.2, invariant));
        Assert.Equal("1.0 µs", OperationText.DurationAtLeast(999.5, invariant));
        Assert.Equal("4.9 µs", OperationText.DurationAtLeast(4_801, invariant));
        Assert.Equal("100 µs", OperationText.DurationAtLeast(99_901, invariant));
        Assert.Equal("501 µs", OperationText.DurationAtLeast(500_050, invariant));
        Assert.Equal("1.0 ms", OperationText.DurationAtLeast(999_001, invariant));
        Assert.Equal("1.3 s", OperationText.DurationAtLeast(1_200_000_001, invariant));
        Assert.Equal("1.2 s", OperationText.DurationAtLeast(1_200_000_000, invariant));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => OperationText.DurationAtLeast(double.NaN, invariant));
    }

    [Fact]
    public void EveryCallStateHasWordsAndNoOtherValueDoes()
    {
        Assert.Equal(
            ["completed", "open at capture end", "start not observed", "no activity id", "ambiguous: its activity id was reused before its stop"],
            Enum.GetValues<RpcCallState>().Select(OperationText.State));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => OperationText.State((RpcCallState)99));
    }

    [Fact(DisplayName = "R5: an operation's state reads as a call's does, and the tour's operations name their kind, direction and state in words")]
    public void AnOperationReadsInWords()
    {
        // Each state in the words a call's state reads, a censored one never called failed; one no version knows by number.
        Assert.Equal(["started", "completed", "failed", "start not observed", "open at capture end", "ambiguous", "evicted unresolved"],
            Enum.GetValues<OperationState>().Select(OperationText.State));
        Assert.Equal(OperationText.State(RpcCallState.OpenAtCaptureEnd), OperationText.State(OperationState.OpenAtBoundary));
        Assert.Equal(OperationText.State(RpcCallState.StartNotObserved), OperationText.State(OperationState.OrphanCompletion));
        Assert.Equal("state 9", OperationText.State((OperationState)9));

        // The tour's operations say what each did, which way and when, and how it ended, with sizes it never measured
        // unknown rather than zero.
        WorkspaceSnapshot tour = SyntheticWorkspace.Create();
        Assert.Equal(
        [
            ("Connect · outbound · 1 s", "completed · no byte domain"),
            ("Receive · inbound · 3 s", "completed · 32,768 of 65,536 B"),
            ("Send · outbound · 11 s", "started · ? of 8,192 B"),
            ("Send · outbound · 2 s", "completed · 4,096 of 4,096 B"),
        ], Operations(tour, "group.app", Instance("5eb7465f-3dd6-4df8-8577-472fa43aab10"), "chan.https"));
        Assert.Contains(("Request start · outbound · 14 s", "open at capture end · no byte domain"),
            Operations(tour, "group.app", Instance("76447f1d-1cfc-4815-b775-cbdb8327f624"), "chan.indexrpc"));
        Assert.Contains(("Map · no data direction · 20 s", "start not observed · no byte domain"),
            Operations(tour, "group.app", Instance("25ecf72c-7d72-4421-943e-eb6d66cd2fe8"), "chan.section"));
    }

    private static string Instance(string id) => new ProcessInstanceId(Guid.Parse(id)).ToString();

    /// <summary>The operation rows of the channel reached by descending through <paramref name="path"/>, by label.</summary>
    private static (string Label, string Detail)[] Operations(WorkspaceSnapshot tour, params string[] path)
    {
        var ladder = new DetailLadder(SyntheticWorkspace.Root(tour));
        foreach (string key in path)
        {
            LadderRow row = LadderProjection.Project(tour, ladder.Current).Rows.Single(candidate => candidate.Key == key);
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, tour.Extent), out _));
        }

        Assert.Equal(DetailLevel.Channel, ladder.Current.Level);
        return [.. LadderProjection.Project(tour, ladder.Current).Rows
            .Select(row => (row.Label, row.Detail))
            .OrderBy(row => row.Label, StringComparer.Ordinal)];
    }
}
