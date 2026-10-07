using System.Globalization;
using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// §6.2's time base: a session's instants read in session time, counted from the moment its capture began, or, where the
/// capture recorded its clock calibration, on the wall clock its machine read - the time that machine's own logs keep -
/// in this computer's zone, with the offset from UTC every such time is stated with. Every instant the view states follows
/// the choice: the axis, the cards, the time scope, the interval table, the evidence and the inspector's record. Nothing
/// it counts changes, and a session whose capture recorded no wall clock is read in session time alone.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private bool readsWallClock;
    private SessionClock? timeBase;

    /// <summary>Whether the session's capture recorded the wall clock its machine read, so its instants can be read on it.</summary>
    public bool OffersWallClock => realOverview && wholeSnapshot.WallClock is not null;

    /// <summary>Whether the view reads the session's instants on the wall clock rather than in session time.</summary>
    public bool ReadsWallClock
    {
        get => readsWallClock && OffersWallClock;
        set
        {
            bool reads = value && OffersWallClock;
            if (reads == ReadsWallClock)
            {
                return;
            }

            readsWallClock = reads;
            timeBase = null;
            RaiseTimeBaseChanged();
        }
    }

    /// <summary>The time base every instant the view states is read in.</summary>
    public SessionClock TimeBase => timeBase ??= ReadsWallClock && wholeSnapshot.WallClock is { } wall
        ? SessionClock.Wall(wall, TimeZoneInfo.Local, wholeSnapshot.Extent)
        : SessionClock.Session(TimeZoneInfo.Local);

    /// <summary>
    /// What reading the wall clock means, for the choice's tooltip and a screen reader: the readings it was placed through
    /// and how closely each was taken, and that how right that clock was is not known. Empty where it is not offered.
    /// </summary>
    public string WallClockTip => !OffersWallClock ? string.Empty
        : "Wall clock: read every instant on the clock the capture's machine kept, in this computer's time zone, as that "
            + "machine's own logs record it, rather than in session time since the capture began. "
            + SessionClock.Wall(wholeSnapshot.WallClock!, TimeZoneInfo.Local, wholeSnapshot.Extent).Basis(CultureInfo.CurrentCulture)
            + " Nothing counted changes.";

    /// <summary>Says every instant the view states again, in the time base now chosen.</summary>
    private void RaiseTimeBaseChanged()
    {
        if (evidence is { } list)
        {
            // The evidence's scope names its interval, and each record its time, in the base now read.
            EvidenceScope scope = EvidenceScopes.Resolve(Snapshot, ladder.Current, TimeBase);
            if (SameScope(list.Scope, scope))
            {
                list.Scope = list.Scope with { Description = scope.Description };
            }

            string? selected = IsEvidenceRung ? selectedRung?.Key : null;
            RebuildEvidenceRows();
            RaiseEvidenceChanged();
            if (selected is not null && evidenceRows.FirstOrDefault(row => row.Key == selected) is { } row)
            {
                SelectedRung = row;
            }
        }

        // A call's and an exchange's rows name when each began, and the interval table each interval.
        RebuildRpcRows();
        RebuildHttpRows();
        RefreshIntervalRows(timelineFocusBuckets);
        OnPropertyChanged(nameof(ReadsWallClock));
        OnPropertyChanged(nameof(TimeBase));
        OnPropertyChanged(nameof(IntervalLabel));
        OnPropertyChanged(nameof(RankingScopeText));
        OnPropertyChanged(nameof(IntervalTableScope));
        OnPropertyChanged(nameof(SelectedEvidenceFields));
        OnPropertyChanged(nameof(EvidenceSummary));
        OnPropertyChanged(nameof(CellExplanation));
        RaiseSettingsChanged();
    }
}
