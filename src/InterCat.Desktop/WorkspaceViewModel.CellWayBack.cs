using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// A rung left with a timeline cell chosen (§3.2, §6.8): the rung - its depth, level and focus - and the lane the cell
/// was chosen in, whose interval the rung brings back when the way back or forward comes to it.
/// </summary>
public sealed record CellOrigin(int Depth, DetailLevel Level, string? Focus, TimelineCellLane Lane);

/// <summary>
/// Where a way down from a record listed beside a cell began (§3.2, §6.4): the rung - its depth, level and focus - and
/// the record opened, by its key.
/// </summary>
public sealed record CellRecordOrigin(int Depth, DetailLevel Level, string? Focus, string Record);

/// <summary>
/// The way back to a chosen timeline cell (§3.2, §6.8). E from a chosen cell lists its lane's records, a record listed
/// beside it opens in E, and Enter opens a row over its interval; Esc or the breadcrumb back to the rung that way began
/// chooses the cell again there, explained as a cell of the lane it was chosen in and listing its records, as the way back
/// to a relationship chooses it again - and forward to a rung an ascent left with a cell chosen chooses it again, as the
/// rung was left. Each rung keeps its own, taken when the way comes back to it, so a cell let go there since is not
/// chosen again later; a way down that begins at a rung or above it is a way of its own, and a newer generation keeps
/// them. A record opened from the cell's list is a way down from that list: the way back gives the keyboard to it again
/// once the cell's records are listed, so the next is a key away.
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

    // Where the latest way down from a record listed beside a cell began, and the record the way back came for until the
    // cell's records are first listed again, another interval is chosen or the view navigates.
    private CellRecordOrigin? cellRecordOrigin;
    private string? askedCellRecord;

    /// <summary>
    /// How many times a way back has come to a record listed beside a cell and asked for the keyboard to go to it (§3.2,
    /// R15); the window gives it there each time this changes.
    /// </summary>
    public int CellRecordAsked { get; private set; }

    /// <summary>The listed record the latest way back asked for the keyboard to go to.</summary>
    public CellRecordRow? AskedCellRecordRow { get; private set; }

    /// <summary>
    /// At the rung a way down from a cell's listed record began, asks for the keyboard to go back to that record where the
    /// cell's records, listed again, hold it; at another rung at that depth the way back is let go.
    /// </summary>
    private void ResumeCellRecordOrigin()
    {
        if (cellRecordOrigin is not { } origin || ladder.Depth != origin.Depth)
        {
            return;
        }

        cellRecordOrigin = null;
        if (ladder.Current.Level != origin.Level || !string.Equals(ladder.Current.Focus?.Key, origin.Focus, StringComparison.Ordinal))
        {
            return;
        }

        // A list already read holds what it will: the record is in it now or not at all. One not yet wanted - the rung's
        // lanes still being counted - is waited for, until another interval is chosen.
        askedCellRecord = origin.Record;
        OfferAskedCellRecord(listed: listedCellRecords is not null && CellRecordsReady.IsCompleted);
    }

    /// <summary>
    /// Asks for the keyboard to go to the record a way back came for, where the cell's records listed hold it; once they
    /// are <paramref name="listed"/>, the way back is spent either way.
    /// </summary>
    private void OfferAskedCellRecord(bool listed)
    {
        if (askedCellRecord is not { } key)
        {
            return;
        }

        if (cellRecordRows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal)) is { } row)
        {
            AskedCellRecordRow = row;
            CellRecordAsked++;
            OnPropertyChanged(nameof(CellRecordAsked));
        }
        else if (!listed)
        {
            return;
        }

        askedCellRecord = null;
    }
}
