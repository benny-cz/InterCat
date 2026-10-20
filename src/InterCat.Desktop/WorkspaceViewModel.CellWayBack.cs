using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// A rung left with a timeline cell chosen (§3.2, §6.8): the rung - its depth, level and focus - and the lane the cell
/// was chosen in, whose interval the rung brings back when the way back or forward comes to it.
/// </summary>
public sealed record CellOrigin(int Depth, DetailLevel Level, string? Focus, TimelineCellLane Lane);

/// <summary>
/// The way back to a chosen timeline cell (§3.2, §6.8). E from a chosen cell lists its lane's records, a record listed
/// beside it opens in E, and Enter opens a row over its interval; Esc or the breadcrumb back to the rung that way began
/// chooses the cell again there, explained as a cell of the lane it was chosen in and listing its records, as the way back
/// to a relationship chooses it again - and forward to a rung an ascent left with a cell chosen chooses it again, as the
/// rung was left. Each rung keeps its own, taken when the way comes back to it, so a cell let go there since is not
/// chosen again later; a way down that begins at a rung or above it is a way of its own, and a newer generation keeps
/// them.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // The ways back to a chosen cell, one for each rung left with one chosen and not yet come back to.
    private readonly List<CellOrigin> cellOrigins = [];

    // The rung the chosen cell was chosen at, so leaving it keeps the way back to the cell.
    private CellOrigin? chosenCellOrigin;

    /// <summary>The way back to a cell of <paramref name="lane"/> chosen at this rung.</summary>
    private CellOrigin CellOriginHere(TimelineCellLane lane) =>
        new(ladder.Depth, ladder.Current.Level, ladder.Current.Focus?.Key, lane);

    /// <summary>
    /// As a navigation leaves the rung the chosen cell was chosen at - before the rung it comes to brings back its own
    /// interval, which lets the cell go - keeps the way back to the cell there, in place of one that rung kept before.
    /// </summary>
    private void KeepCellWayBack()
    {
        if (chosenCell is null || chosenCellOrigin is not { } origin || !ReferenceEquals(origin.Lane, chosenCell))
        {
            return;
        }

        cellOrigins.RemoveAll(kept => kept.Depth == origin.Depth);
        cellOrigins.Add(origin);
    }

    /// <summary>
    /// Takes the way back to the rung shown, where it was left with a cell chosen, and chooses the cell again where the
    /// rung is still the one it was chosen at.
    /// </summary>
    private void ResumeCellOrigin()
    {
        int kept = cellOrigins.FindIndex(origin => origin.Depth == ladder.Depth);
        if (kept < 0)
        {
            return;
        }

        // A way down along the ladder from its top may pass another rung at that depth: it is checked.
        CellOrigin origin = cellOrigins[kept];
        cellOrigins.RemoveAt(kept);
        if (ladder.Current.Level != origin.Level || !string.Equals(ladder.Current.Focus?.Key, origin.Focus, StringComparison.Ordinal))
        {
            return;
        }

        chosenCell = origin.Lane;
        chosenCellOrigin = origin;
        RaiseCellExplanationChanged();
    }
}
