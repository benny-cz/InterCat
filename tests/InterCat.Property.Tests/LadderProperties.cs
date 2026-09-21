using System.Globalization;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.PropertyTests;

/// <summary>
/// The §3.2 ladder invariants, generated over random descent paths rather than demonstrated on one
/// example (R10). Every case comes from a printed seed, so a failure is reproducible (§13.6).
/// </summary>
public sealed class LadderProperties
{
    private const int Cases = 200;

    public static TheoryData<int> Seeds => [2, 11, 97, 20_260_921];

    [Theory(DisplayName = "R13: ascending restores the rung that was left, field for field")]
    [MemberData(nameof(Seeds))]
    public void AscendingRestoresTheRungThatWasLeft(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            var expected = new List<NavigationState>();
            int depth = random.Next(1, 6);
            for (int step = 0; step < depth; step++)
            {
                LadderView view = LadderProjection.Project(snapshot, ladder.Current);
                if (view.Rows.Count == 0 || ladder.Current.Level == DetailLevel.Evidence)
                {
                    break;
                }

                expected.Add(ladder.Current);
                LadderRow row = view.Rows[random.Next(view.Rows.Count)];
                TimeRange viewport = Viewport(random, snapshot.Extent);
                Assert.True(
                    ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, viewport), out string? refusal),
                    Describe(seed, index, refusal ?? "descent refused without a reason"));
            }

            for (int step = expected.Count - 1; step >= 0; step--)
            {
                Assert.True(ladder.TryAscend(out NavigationState restored), Describe(seed, index, "ascent refused"));
                Assert.Equal(expected[step], restored);
            }

            Assert.False(ladder.CanAscend, Describe(seed, index, "the ladder did not return to its root"));
        }
    }

    [Theory(DisplayName = "R13: the breadcrumb names every rung from the machine to the current position")]
    [MemberData(nameof(Seeds))]
    public void BreadcrumbAlwaysStatesThePosition(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(0, 5));

            Assert.Equal(ladder.Depth + 1, ladder.Breadcrumb.Count);
            Assert.Equal(DetailLevel.Machine, ladder.Breadcrumb[0].Level);
            Assert.Equal(ladder.Current, ladder.Breadcrumb[^1]);
            for (int rung = 1; rung < ladder.Breadcrumb.Count; rung++)
            {
                NavigationState state = ladder.Breadcrumb[rung];
                Assert.NotNull(state.Focus);
                Assert.Equal(state.Level, state.Focus!.Value.Level);
                Assert.Contains(state.Focus.Value.Label, state.Crumb, StringComparison.Ordinal);
            }

            string breadcrumb = LadderProjection.Breadcrumb(ladder);
            foreach (NavigationState state in ladder.Breadcrumb)
            {
                Assert.Contains(state.Crumb, breadcrumb, StringComparison.Ordinal);
            }
        }
    }

    [Theory(DisplayName = "R13: evidence is one step from every rung, and the step states its scope")]
    [MemberData(nameof(Seeds))]
    public void EvidenceIsAlwaysOneStepAway(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(0, 5));
            if (ladder.Current.Level == DetailLevel.Evidence)
            {
                continue;
            }

            NavigationState before = ladder.Current;
            LadderDescent descent = LadderProjection.EvidenceDescentFor(before, before.Viewport);

            Assert.True(ladder.TryDescend(descent, out string? refusal), Describe(seed, index, refusal ?? "refused"));
            Assert.Equal(DetailLevel.Evidence, ladder.Current.Level);
            Assert.Equal(before.Filters.Count + 1, ladder.Current.Filters.Count);
            Assert.Contains(ladder.Current.Filters, filter => filter.Field == "scope");
            Assert.True(ladder.TryAscend(out NavigationState restored));
            Assert.Equal(before, restored);
        }
    }

    [Theory(DisplayName = "R13: descending adds only filters the filter bar shows and can remove")]
    [MemberData(nameof(Seeds))]
    public void EveryImpliedFilterIsVisibleAndRemovable(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(1, 5));
            IReadOnlyList<ImpliedFilter> filters = ladder.Current.Filters;
            Assert.Equal(ladder.Depth, filters.Count);
            foreach (ImpliedFilter filter in filters)
            {
                Assert.False(string.IsNullOrWhiteSpace(filter.Field));
                Assert.False(string.IsNullOrWhiteSpace(filter.Value));
                Assert.False(string.IsNullOrWhiteSpace(filter.Reason));
            }

            if (filters.Count == 0)
            {
                continue;
            }

            DetailLevel level = ladder.Current.Level;
            string field = filters[random.Next(filters.Count)].Field;
            Assert.True(ladder.TryRemoveFilter(field), Describe(seed, index, $"filter {field} was not removable"));
            Assert.Equal(level, ladder.Current.Level);
            Assert.DoesNotContain(ladder.Current.Filters, filter => filter.Field == field);
        }
    }

    [Theory(DisplayName = "R13: a rung with no rows names the source that would supply them")]
    [MemberData(nameof(Seeds))]
    public void NoRungIsADeadEnd(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(0, 6));
            LadderView view = LadderProjection.Project(snapshot, ladder.Current);

            if (view.Rows.Count > 0)
            {
                Assert.Null(view.EmptyReason);
                continue;
            }

            Assert.False(
                string.IsNullOrWhiteSpace(view.EmptyReason),
                Describe(seed, index, $"{ladder.Current.Level} was empty without naming a source"));
        }
    }

    [Theory(DisplayName = "R13: a level change alters grouping, never the observations in scope")]
    [MemberData(nameof(Seeds))]
    public void ALevelChangeDoesNotChangeTheNumbers(int seed)
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        LadderView machine = LadderProjection.Project(snapshot, ladder.Current);

        Assert.Equal(AccountingSide.CanonicalOwner, machine.Side);
        long constituentTotal = 0;
        foreach (LadderRow group in machine.Rows)
        {
            var scoped = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Assert.True(scoped.TryDescend(
                LadderProjection.DescentFor(group, scoped.Current, snapshot.Extent),
                out string? refusal), refusal);
            LadderView members = LadderProjection.Project(snapshot, scoped.Current);
            Assert.Equal(AccountingSide.CanonicalOwner, members.Side);
            Assert.Equal(group.ObservationCount, members.Rows.Sum(row => row.ObservationCount));
            constituentTotal += members.ObservationCount!.Value;
        }

        // The seed is part of the message so a failure names the case, even though this property is
        // exhaustive over the fixture rather than randomized.
        Assert.Equal(
            machine.ObservationCount!.Value.ToString(CultureInfo.InvariantCulture),
            constituentTotal.ToString(CultureInfo.InvariantCulture));
        Assert.True(seed > 0);
    }

    [Theory(DisplayName = "R13: a descent that skips a rung is refused with a reason, not reinterpreted")]
    [MemberData(nameof(Seeds))]
    public void SkippingARungIsRefused(int seed)
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        var skip = new LadderDescent
        {
            Target = new(DetailLevel.Channel, "chan.https", "skipped"),
            Viewport = snapshot.Extent,
            Lanes = LaneGrouping.Endpoint,
            GraphFocusKey = null,
        };

        Assert.False(ladder.TryDescend(skip, out string? refusal));
        Assert.False(string.IsNullOrWhiteSpace(refusal));
        Assert.Equal(DetailLevel.Machine, ladder.Current.Level);
        Assert.Equal(0, ladder.Depth);
        Assert.True(seed > 0);
    }

    [Theory(DisplayName = "R13: returning to a named rung keeps every rung above it and drops the rest")]
    [MemberData(nameof(Seeds))]
    public void ReturningToACrumbTruncatesTheLadder(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(1, 5));
            if (ladder.Depth == 0)
            {
                continue;
            }

            NavigationState[] before = [.. ladder.Breadcrumb];
            int target = random.Next(0, ladder.Depth);

            Assert.True(ladder.TryReturnTo(target, out NavigationState restored));
            Assert.Equal(before[target], restored);
            Assert.Equal(target, ladder.Depth);
            Assert.Equal(before.Take(target + 1), ladder.Breadcrumb);
        }
    }

    [Theory(DisplayName = "P1: rows that overlap report no total instead of a sum that counts twice")]
    [MemberData(nameof(Seeds))]
    public void OverlappingRowsRefuseATotal(int seed)
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        LadderView machine = LadderProjection.Project(snapshot, ladder.Current);
        Assert.True(ladder.TryDescend(
            LadderProjection.DescentFor(machine.Rows[0], ladder.Current, snapshot.Extent),
            out _));
        LadderView group = LadderProjection.Project(snapshot, ladder.Current);
        Assert.True(ladder.TryDescend(
            LadderProjection.DescentFor(group.Rows[0], ladder.Current, snapshot.Extent),
            out _));

        // A channel belongs to both of its participants, so the process rung's rows overlap.
        LadderView channels = LadderProjection.Project(snapshot, ladder.Current);

        Assert.Equal(AccountingSide.EndpointActivity, channels.Side);
        Assert.Null(channels.ObservationCount);
        Assert.Null(channels.KnownBytes);
        Assert.False(string.IsNullOrWhiteSpace(channels.TotalUnavailableReason));
        Assert.NotEmpty(channels.Rows);
        Assert.True(seed > 0);
    }

    private static void Descend(Random random, WorkspaceSnapshot snapshot, DetailLadder ladder, int steps)
    {
        for (int step = 0; step < steps; step++)
        {
            LadderView view = LadderProjection.Project(snapshot, ladder.Current);
            if (view.Rows.Count == 0 || ladder.Current.Level == DetailLevel.Evidence)
            {
                return;
            }

            LadderRow row = view.Rows[random.Next(view.Rows.Count)];
            if (!ladder.TryDescend(
                LadderProjection.DescentFor(row, ladder.Current, Viewport(random, snapshot.Extent)),
                out _))
            {
                return;
            }
        }
    }

    private static TimeRange Viewport(Random random, TimeRange extent)
    {
        long span = Math.Max(1, extent.SpanTicks / random.Next(1, 8));
        long start = extent.StartTicks + random.NextInt64(0, Math.Max(1, extent.SpanTicks - span));
        return new(start, start + span);
    }

    private static string Describe(int seed, int index, string detail) => string.Create(
        CultureInfo.InvariantCulture,
        $"seed {seed}, case {index}: {detail}");
}
