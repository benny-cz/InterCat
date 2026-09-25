using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>The section 3.2 rungs. The numeric order is the descent order and the display order.</summary>
public enum DetailLevel
{
    Machine = 0,
    Group = 1,
    ProcessInstance = 2,
    Channel = 3,
    Operation = 4,
    Evidence = 5,
}

/// <summary>What a rung is focused on: the thing that was selected to reach it.</summary>
public readonly record struct LadderTarget(DetailLevel Level, string Key, string Label);

/// <summary>
/// A filter a descent implies. Descending sets scope and grouping and never silently adds a predicate,
/// so where one is implied it is carried here, shown in the filter bar and removable (section 3.2).
/// </summary>
public sealed record ImpliedFilter(string Field, string Value, string Reason)
{
    /// <summary>
    /// The stable key of what the filter names - a group key, a process-instance ID or a channel key - so a query
    /// can read its scope from the filter the user sees rather than from its label. Null for a label-only filter.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>The rung whose selection the filter names. Evidence reads its scope from the latest such filter.</summary>
    public DetailLevel? Level { get; init; }
}

/// <summary>
/// One restorable rung. Ascending returns this value unchanged, so the viewport, selection, lane
/// grouping, graph focus and filter set all come back as one navigation state (section 3.2, section 6.7).
/// </summary>
public sealed record NavigationState
{
    public required DetailLevel Level { get; init; }

    /// <summary>The selection that produced this rung. Null only at the machine rung.</summary>
    public required LadderTarget? Focus { get; init; }

    public required TimeRange Viewport { get; init; }
    public required LaneGrouping Lanes { get; init; }

    /// <summary>The node or cluster the graph is centred on, or null when the graph shows everything.</summary>
    public required string? GraphFocusKey { get; init; }

    public required IReadOnlyList<ImpliedFilter> Filters { get; init; }

    /// <summary>
    /// The analysis interval the user had on this rung when they last left it, so a return to it by ascent, crumb or
    /// forward brings that time back (section 6.7). Null when none was brushed. <see cref="Viewport"/> stays the time the
    /// rung was entered with, which is what an evidence rung reads its records for.
    /// </summary>
    public TimeRange? IntervalWhenLeft { get; init; }

    /// <summary>The rung as a breadcrumb crumb: the level, and what is selected at it.</summary>
    public string Crumb => Focus is { } focus
        ? string.Create(CultureInfo.InvariantCulture, $"{Name(Level)}: {focus.Label}")
        : Name(Level);

    public static string Name(DetailLevel level) => level switch
    {
        DetailLevel.Machine => "Machine",
        DetailLevel.Group => "Group",
        DetailLevel.ProcessInstance => "Process",
        DetailLevel.Channel => "Channel",
        DetailLevel.Operation => "Operation",
        DetailLevel.Evidence => "Evidence",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}

/// <summary>What a descent asks for: the target rung and the state it should restore on the way back.</summary>
public sealed record LadderDescent
{
    public required LadderTarget Target { get; init; }
    public required TimeRange Viewport { get; init; }
    public required LaneGrouping Lanes { get; init; }
    public required string? GraphFocusKey { get; init; }

    /// <summary>Filters this descent implies. They are added to the rung's visible, removable set.</summary>
    public IReadOnlyList<ImpliedFilter> AddedFilters { get; init; } = [];
}

/// <summary>
/// The section 3.2 ladder: one gesture per rung, a breadcrumb that always states the position, an ascent
/// that restores exactly where the user was, and a forward step that re-enters, exactly, a rung that was
/// left upward (section 6.7). It is a pure data structure — no view, no query, no clock — so its invariants
/// can be generated over rather than demonstrated (R10, R13).
/// </summary>
public sealed class DetailLadder
{
    private readonly List<NavigationState> rungs;

    // The rungs an ascent or a crumb left, deepest first, so the last is the one a forward step re-enters. Each is one
    // valid descent below the one before it, the first below the current rung, so there are never more than the rungs
    // that lie below the current one.
    private readonly List<NavigationState> forward = [];

    public DetailLadder(NavigationState root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Focus is not null && root.Focus.Value.Level != root.Level)
        {
            throw new ArgumentException("A rung's focus must be at the rung's own level.", nameof(root));
        }

        rungs = [root];
    }

    /// <summary>The rung the user is on.</summary>
    public NavigationState Current => rungs[^1];

    /// <summary>Every rung from the machine down to the current one, in descent order.</summary>
    public IReadOnlyList<NavigationState> Breadcrumb => rungs;

    /// <summary>How many rungs were descended from the root.</summary>
    public int Depth => rungs.Count - 1;

    public bool CanAscend => rungs.Count > 1;

    /// <summary>The rungs forward steps re-enter, nearest first: the way back down to where an ascent started.</summary>
    public IReadOnlyList<NavigationState> Forward => [.. Enumerable.Reverse(forward)];

    public bool CanGoForward => forward.Count > 0;

    /// <summary>Revisions increase on every navigation, so a stale view can tell that it is stale (R6).</summary>
    public long Revision { get; private set; }

    /// <summary>
    /// Descends one rung, or straight to evidence. Anything else is refused with a reason rather than
    /// silently reinterpreted, because a ladder that skips rungs cannot be ascended back through.
    /// </summary>
    public bool TryDescend(LadderDescent descent, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(descent);
        DetailLevel from = Current.Level;
        DetailLevel to = descent.Target.Level;
        if (to <= from)
        {
            refusal = $"{NavigationState.Name(to)} is not below {NavigationState.Name(from)}; use ascent instead.";
            return false;
        }

        // Every rung reaches evidence in at most one step, because "show me the actual records" is the
        // question the product exists to answer (section 3.2). Any other jump must be one rung.
        if (to != DetailLevel.Evidence && to != from + 1)
        {
            refusal =
                $"A descent from {NavigationState.Name(from)} may reach {NavigationState.Name(from + 1)} "
                + $"or {NavigationState.Name(DetailLevel.Evidence)}, not {NavigationState.Name(to)}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(descent.Target.Key))
        {
            refusal = "A descent needs the key of the thing that was selected.";
            return false;
        }

        NavigationState entered = new()
        {
            Level = to,
            Focus = descent.Target,
            Viewport = descent.Viewport,
            Lanes = descent.Lanes,
            GraphFocusKey = descent.GraphFocusKey,
            Filters = [.. Current.Filters, .. descent.AddedFilters],
        };

        // A descent to anywhere else starts a new way down, so forward history ends, as a browser's does after a new
        // page. Descending to exactly the rung forward names is the same step taken by hand; the rest of the way stays.
        if (forward.Count > 0 && SameRung(forward[^1], entered))
        {
            forward.RemoveAt(forward.Count - 1);
        }
        else
        {
            forward.Clear();
        }

        rungs.Add(entered);
        Revision = checked(Revision + 1);
        refusal = null;
        return true;
    }

    /// <summary>Ascends to exactly where the user was. The restored rung is the value that was stored.</summary>
    public bool TryAscend(out NavigationState restored)
    {
        if (!CanAscend)
        {
            restored = Current;
            return false;
        }

        forward.Add(rungs[^1]);
        rungs.RemoveAt(rungs.Count - 1);
        Revision = checked(Revision + 1);
        restored = Current;
        return true;
    }

    /// <summary>
    /// Re-enters the rung the latest ascent or crumb left, exactly as it was left: its selection, time, filters, graph
    /// focus and lane grouping (section 6.7). Nothing is rebuilt, so nothing can come back different.
    /// </summary>
    public bool TryGoForward(out NavigationState entered)
    {
        if (forward.Count == 0)
        {
            entered = Current;
            return false;
        }

        entered = forward[^1];
        forward.RemoveAt(forward.Count - 1);
        rungs.Add(entered);
        Revision = checked(Revision + 1);
        return true;
    }

    /// <summary>Ends forward history, when what it would re-enter no longer exists.</summary>
    public void ClearForward() => forward.Clear();

    /// <summary>
    /// Puts back forward history carried from an earlier generation, nearest rung first. A list that is not a way down
    /// from the current rung, one valid descent at a time, is refused whole and forward history stays empty.
    /// </summary>
    public bool TryRestoreForward(IReadOnlyList<NavigationState> nearestFirst)
    {
        ArgumentNullException.ThrowIfNull(nearestFirst);
        forward.Clear();
        DetailLevel from = Current.Level;
        foreach (NavigationState rung in nearestFirst)
        {
            if (rung is null
                || rung.Focus is not { } focus
                || focus.Level != rung.Level
                || string.IsNullOrWhiteSpace(focus.Key)
                || rung.Level <= from
                || rung.Level != DetailLevel.Evidence && rung.Level != from + 1)
            {
                return false;
            }

            from = rung.Level;
        }

        for (int index = nearestFirst.Count - 1; index >= 0; index--)
        {
            forward.Add(nearestFirst[index]);
        }

        return true;
    }

    /// <summary>
    /// Records on the current rung the analysis interval the user has there, just before they leave it, so a return
    /// brings it back. It changes nothing the rung shows, so it is not a navigation and the revision stays.
    /// </summary>
    public void RecordInterval(TimeRange? interval)
    {
        if (Current.IntervalWhenLeft != interval)
        {
            rungs[^1] = Current with { IntervalWhenLeft = interval };
        }
    }

    /// <summary>Returns to a rung the breadcrumb names. Depth 0 is the machine rung.</summary>
    public bool TryReturnTo(int depth, out NavigationState restored)
    {
        if (depth < 0 || depth > Depth)
        {
            restored = Current;
            return false;
        }

        if (depth == Depth)
        {
            restored = Current;
            return true;
        }

        // Deepest first, so the first forward step re-enters the rung just below the one returned to.
        for (int index = rungs.Count - 1; index > depth; index--)
        {
            forward.Add(rungs[index]);
        }

        rungs.RemoveRange(depth + 1, rungs.Count - depth - 1);
        Revision = checked(Revision + 1);
        restored = Current;
        return true;
    }

    /// <summary>
    /// Removes an implied filter from the current rung without changing the level. A descent may imply a
    /// filter, but the user must be able to see it and take it off where it is shown (section 3.2).
    /// </summary>
    public bool TryRemoveFilter(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        NavigationState current = Current;
        ImpliedFilter[] remaining =
        [
            .. current.Filters.Where(filter => !string.Equals(filter.Field, field, StringComparison.Ordinal)),
        ];
        if (remaining.Length == current.Filters.Count)
        {
            return false;
        }

        // The rungs forward would re-enter were reached with the filter the user just took off; re-entering one would
        // put it back unasked, so forward history ends here.
        rungs[^1] = current with { Filters = remaining };
        forward.Clear();
        Revision = checked(Revision + 1);
        return true;
    }

    /// <summary>Whether two rungs show the same thing: everything but the interval each was last left with.</summary>
    private static bool SameRung(NavigationState left, NavigationState right) =>
        left.Level == right.Level
        && left.Focus == right.Focus
        && left.Viewport == right.Viewport
        && left.Lanes == right.Lanes
        && string.Equals(left.GraphFocusKey, right.GraphFocusKey, StringComparison.Ordinal)
        && left.Filters.SequenceEqual(right.Filters);
}
