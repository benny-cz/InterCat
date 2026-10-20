using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Where a way down from a row the interval table lists began (§3.2, R15): the rung - its depth, level and focus - and the
/// interval the row stands for.
/// </summary>
public sealed record IntervalRowOrigin(int Depth, DetailLevel Level, string? Focus, TimeRange Interval);

/// <summary>
/// The way back to a row the interval table lists (§3.2, R15). Enter on the row, or E with the keyboard on it, lists its
/// moment's records in E, and E's table takes the keyboard; Esc or the breadcrumb back to the rung that way began asks for
/// the keyboard to go to the row again, where the tables are shown and it is still the analysis interval's, so the next
/// moment is a key away - as the way back to a relationship gives the keyboard to its row, each time the way comes back,
/// forward along it and back included. A way down that begins at that rung or above it is a way of its own, and a newer
/// generation keeps it.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // Where the latest way down from the interval table's row began, until a way down of its own from that rung or above
    // it, or a way back to another rung at its depth; and the interval a way back came to until the table lists its row,
    // the tables are hidden, another interval is chosen or the view navigates.
    private IntervalRowOrigin? intervalRowOrigin;
    private TimeRange? askedIntervalRow;

    /// <summary>
    /// How many times a way back has come to the interval table's row it began at and asked for the keyboard to go to it
    /// (§3.2, R15); the window gives it there each time this changes.
    /// </summary>
    public int IntervalRowAsked { get; private set; }

    /// <summary>
    /// Lists in E the records of the moment the interval table's chosen row stands for, as E does with the analysis
    /// interval it sets, keeping the way back to the row (§6.4, §3.2).
    /// </summary>
    public bool ShowIntervalRecords()
    {
        IntervalRowOrigin? origin = selectedInterval is { } interval
            ? new(ladder.Depth, ladder.Current.Level, ladder.Current.Focus?.Key, interval)
            : null;
        if (!ShowEvidence())
        {
            return false;
        }

        intervalRowOrigin = origin;
        return true;
    }

    /// <summary>
    /// At the rung a way down from the interval table's row began, asks for the keyboard to go to the row again, where the
    /// tables are shown - at once, or once the rung's rows are counted again and list it - keeping the way back for the
    /// next time it comes there; at another rung at that depth the way back is let go.
    /// </summary>
    private void ResumeIntervalRowOrigin()
    {
        if (intervalRowOrigin is not { } origin || ladder.Depth != origin.Depth)
        {
            return;
        }

        if (ladder.Current.Level != origin.Level || !string.Equals(ladder.Current.Focus?.Key, origin.Focus, StringComparison.Ordinal))
        {
            intervalRowOrigin = null;
            return;
        }

        if (showTables)
        {
            askedIntervalRow = origin.Interval;
            OfferAskedIntervalRow();
        }
    }

    /// <summary>Asks for the keyboard to go to the row a way back came for, once the shown table lists it as the chosen row.</summary>
    private void OfferAskedIntervalRow()
    {
        if (askedIntervalRow is not { } interval || SelectedIntervalRow?.Interval != interval)
        {
            return;
        }

        askedIntervalRow = null;
        IntervalRowAsked++;
        OnPropertyChanged(nameof(IntervalRowAsked));
    }
}
