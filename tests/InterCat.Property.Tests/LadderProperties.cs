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

    [Theory(DisplayName = "§6.7: going forward re-enters every rung an ascent or a crumb left, the stored value itself")]
    [MemberData(nameof(Seeds))]
    public void ForwardReentersWhatWasLeft(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(1, 6));
            if (ladder.Current.Level != DetailLevel.Evidence && random.Next(3) == 0)
            {
                Assert.True(ladder.TryDescend(
                    LadderProjection.EvidenceDescentFor(ladder.Current, ladder.Current.Viewport), out _));
            }

            NavigationState[] deepest = [.. ladder.Breadcrumb];

            // Climb back to the machine by any mix of ascents and crumbs.
            while (ladder.CanAscend)
            {
                if (random.Next(2) == 0)
                {
                    Assert.True(ladder.TryAscend(out _));
                }
                else
                {
                    Assert.True(ladder.TryReturnTo(random.Next(0, ladder.Depth), out _));
                }

                Assert.Equal(deepest.Skip(ladder.Depth + 1), ladder.Forward);
            }

            // One forward step per rung, nearest first, until the ladder stands where the climb began.
            while (ladder.CanGoForward)
            {
                NavigationState expected = ladder.Forward[0];
                long revision = ladder.Revision;
                Assert.True(ladder.TryGoForward(out NavigationState entered), Describe(seed, index, "forward refused"));
                Assert.Same(expected, entered);
                Assert.Same(deepest[ladder.Depth], entered);
                Assert.Equal(revision + 1, ladder.Revision);
            }

            Assert.Equal(deepest.Length, ladder.Breadcrumb.Count);
            for (int rung = 0; rung < deepest.Length; rung++)
            {
                Assert.Same(deepest[rung], ladder.Breadcrumb[rung]);
            }

            Assert.False(ladder.TryGoForward(out NavigationState stayed));
            Assert.Same(ladder.Current, stayed);
        }
    }

    [Theory(DisplayName = "§6.7: a descent elsewhere ends forward history, and the same step taken by hand keeps the rest")]
    [MemberData(nameof(Seeds))]
    public void ADescentElsewhereEndsForwardHistory(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, ladder, random.Next(2, 6));
            if (ladder.Depth < 2)
            {
                continue;
            }

            int depth = ladder.Depth;
            Assert.True(ladder.TryReturnTo(0, out _));
            Assert.Equal(depth, ladder.Forward.Count);

            // Enter on the row forward names, over the same time, is the forward step itself.
            NavigationState next = ladder.Forward[0];
            LadderRow same = LadderProjection.Project(snapshot, ladder.Current).Rows.Single(
                row => row.Key == next.Focus!.Value.Key && row.DescendsTo == next.Level);
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(same, ladder.Current, next.Viewport), out _));
            Assert.Equal(depth - 1, ladder.Forward.Count);
            Assert.True(ladder.CanGoForward);

            // The same row over another time is a different way down, and so is another row.
            Assert.True(ladder.TryAscend(out _));
            Assert.Equal(depth, ladder.Forward.Count);
            LadderRow[] others = [.. LadderProjection.Project(snapshot, ladder.Current).Rows.Where(row => row.Key != same.Key)];
            LadderDescent elsewhere = others.Length > 0 && random.Next(2) == 0
                ? LadderProjection.DescentFor(others[random.Next(others.Length)], ladder.Current, next.Viewport)
                : LadderProjection.DescentFor(same, ladder.Current, Retimed(next.Viewport));
            Assert.True(ladder.TryDescend(elsewhere, out _));
            Assert.False(ladder.CanGoForward, Describe(seed, index, "a descent elsewhere kept forward history"));
            Assert.Empty(ladder.Forward);
        }
    }

    [Theory(DisplayName = "§6.7: forward history is always a way down from the current rung, never longer than the rungs below it")]
    [MemberData(nameof(Seeds))]
    public void ForwardHistoryIsAlwaysAWayDown(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            for (int step = 0; step < 24; step++)
            {
                int forwardBefore = ladder.Forward.Count;
                switch (random.Next(6))
                {
                    case 0:
                        Descend(random, snapshot, ladder, 1);
                        break;
                    case 1:
                        if (ladder.Current.Level != DetailLevel.Evidence)
                        {
                            Assert.True(ladder.TryDescend(
                                LadderProjection.EvidenceDescentFor(ladder.Current, ladder.Current.Viewport), out _));
                        }

                        break;
                    case 2:
                        _ = ladder.TryAscend(out _);
                        break;
                    case 3:
                        _ = ladder.TryReturnTo(random.Next(0, ladder.Depth + 1), out _);
                        break;
                    case 4:
                        _ = ladder.TryGoForward(out _);
                        break;
                    default:
                        if (ladder.Current.Filters.Count > 0)
                        {
                            string field = ladder.Current.Filters[random.Next(ladder.Current.Filters.Count)].Field;
                            Assert.True(ladder.TryRemoveFilter(field));
                            Assert.False(ladder.CanGoForward,
                                Describe(seed, index, $"removing {field} kept {forwardBefore} forward rungs"));
                        }

                        break;
                }

                DetailLevel above = ladder.Current.Level;
                foreach (NavigationState rung in ladder.Forward)
                {
                    Assert.True(rung.Level > above, Describe(seed, index, $"{rung.Level} is not below {above}"));
                    Assert.True(rung.Level == DetailLevel.Evidence || rung.Level == above + 1,
                        Describe(seed, index, $"forward skips from {above} to {rung.Level}"));
                    Assert.Equal(rung.Level, rung.Focus?.Level);
                    above = rung.Level;
                }

                Assert.True(ladder.Forward.Count <= DetailLevel.Evidence - ladder.Current.Level,
                    Describe(seed, index, $"{ladder.Forward.Count} forward rungs below {ladder.Current.Level}"));
            }
        }
    }

    [Theory(DisplayName = "§6.7: a rung comes back with the interval the user had on it when they left it, by any way back")]
    [MemberData(nameof(Seeds))]
    public void ARungComesBackWithTheIntervalItWasLeftWith(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            var leftWith = new List<TimeRange?>();
            int steps = random.Next(1, 5);
            for (int step = 0; step < steps; step++)
            {
                LadderView view = LadderProjection.Project(snapshot, ladder.Current);
                if (view.Rows.Count == 0)
                {
                    break;
                }

                TimeRange? interval = random.Next(3) == 0 ? null : Viewport(random, snapshot.Extent);
                long revision = ladder.Revision;
                ladder.RecordInterval(interval);
                Assert.Equal(revision, ladder.Revision);
                leftWith.Add(interval);
                LadderRow row = view.Rows[random.Next(view.Rows.Count)];
                Assert.True(ladder.TryDescend(
                    LadderProjection.DescentFor(row, ladder.Current, interval ?? ladder.Current.Viewport), out _));
            }

            // Every way back up brings each rung's interval back: an ascent, or a crumb straight to it.
            var leftBelow = new List<TimeRange?>();
            while (ladder.CanAscend)
            {
                TimeRange? here = random.Next(3) == 0 ? null : Viewport(random, snapshot.Extent);
                ladder.RecordInterval(here);
                leftBelow.Insert(0, here);
                NavigationState restored;
                if (random.Next(2) == 0 || ladder.Depth == 1)
                {
                    Assert.True(ladder.TryAscend(out restored));
                }
                else
                {
                    int target = ladder.Depth - 1;
                    Assert.True(ladder.TryReturnTo(target, out restored));
                }

                Assert.Equal(leftWith[ladder.Depth], restored.IntervalWhenLeft);
            }

            // Going forward again brings back the interval each rung had when the climb left it.
            Assert.Equal(leftBelow.Count, ladder.Forward.Count);
            for (int step = 0; ladder.TryGoForward(out NavigationState entered); step++)
            {
                Assert.Equal(leftBelow[step], entered.IntervalWhenLeft);
            }
        }
    }

    [Theory(DisplayName = "§6.7: forward history carried to another generation is put back only as a way down, or not at all")]
    [MemberData(nameof(Seeds))]
    public void CarriedForwardHistoryMustBeAWayDown(int seed)
    {
        var random = new Random(seed);
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        for (int index = 0; index < Cases; index++)
        {
            var source = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            Descend(random, snapshot, source, random.Next(1, 6));
            if (source.Depth == 0)
            {
                continue;
            }

            int depth = random.Next(0, source.Depth);
            Assert.True(source.TryReturnTo(depth, out _));
            NavigationState[] carried = [.. source.Forward];

            var restored = new DetailLadder(SyntheticWorkspace.Root(snapshot));
            foreach (NavigationState rung in source.Breadcrumb.Skip(1))
            {
                Assert.True(restored.TryDescend(new LadderDescent
                {
                    Target = rung.Focus!.Value,
                    Viewport = rung.Viewport,
                    Lanes = rung.Lanes,
                    GraphFocusKey = rung.GraphFocusKey,
                }, out _));
            }

            Assert.True(restored.TryRestoreForward(carried), Describe(seed, index, "a real way down was refused"));
            Assert.Equal(carried, restored.Forward);

            // A way that starts at the current rung, or skips a rung other than the step to evidence, is refused whole.
            Assert.False(restored.TryRestoreForward([restored.Current, .. carried]),
                Describe(seed, index, "a way starting at the current rung was accepted"));
            Assert.Empty(restored.Forward);
            if (carried.Length > 2 && carried[2].Level != DetailLevel.Evidence)
            {
                Assert.True(restored.TryRestoreForward(carried));
                Assert.False(restored.TryRestoreForward([carried[0], .. carried.Skip(2)]),
                    Describe(seed, index, "a way that skips a rung was accepted"));
                Assert.Empty(restored.Forward);
            }
        }
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

    /// <summary>Another time than <paramref name="viewport"/>, still inside it where it can be.</summary>
    private static TimeRange Retimed(TimeRange viewport) => viewport.SpanTicks > 1
        ? new(viewport.StartTicks, viewport.EndTicks - 1)
        : new(viewport.StartTicks, viewport.EndTicks + 1);

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
