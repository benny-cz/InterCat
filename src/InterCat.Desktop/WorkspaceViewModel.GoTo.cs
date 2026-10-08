using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Going to a moment (§6.2's time base, §6.7's navigation): a time typed into the search - a time of day on the wall clock
/// the capture's machine read, as that machine's own logs keep it, or session time - is offered first among its hits, and
/// going to it centres the timeline on it at its present zoom and makes the cell holding it the analysis interval, as a
/// step with [ or ] would: the selected lane's cell, which the inspector explains and E lists, else the machine row's,
/// else at the machine rung the column holding it. A time the session cannot place is said, with why, rather than
/// matched to nothing.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private long? searchedMoment;
    private string? searchedMomentProblem;
    private long? goingTo;

    /// <summary>The view the timeline is asked to show, centred on a moment gone to; its pane moves to it.</summary>
    public event EventHandler<TimeRange>? ViewportRequested;

    /// <summary>Completes once a moment gone to has had its cell chosen, or was found in no cell drawn.</summary>
    public Task GoToReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Goes to <paramref name="ticks"/> of session time: the timeline centres on it, at its present span within the
    /// session, and once it is counted there the cell holding it becomes the analysis interval. False for an instant
    /// outside the session.
    /// </summary>
    public bool GoTo(long ticks)
    {
        TimeRange extent = wholeSnapshot.Extent;
        if (!realOverview || ticks < extent.StartTicks || ticks >= extent.EndTicks)
        {
            return false;
        }

        (TimeRange viewport, int columns) = drawnTimeline ?? (extent, Math.Max(1, wholeSnapshot.Timeline.Count));
        long start = ticks - (viewport.SpanTicks / 2);
        TimeRange target = new TimeRange(start, start + viewport.SpanTicks).ClampInside(extent);
        goingTo = ticks;

        // The timeline shows the view and asks for its count at once; asked again here, the same request is not repeated.
        ViewportRequested?.Invoke(this, target);
        RequestTimelineDetail(target, columns);
        GoToReady = ChooseCellAtAsync(ticks);
        return true;
    }

    /// <summary>
    /// Makes the cell holding <paramref name="ticks"/> the analysis interval once the view's count has arrived, in the lane
    /// a step follows - the selected lane, else the machine row - or, at the machine rung with no lane selected, which
    /// draws no machine row, the column holding it. A later moment gone to before then chooses instead.
    /// </summary>
    private async Task ChooseCellAtAsync(long ticks)
    {
        await TimelineDetailReady;
        if (disposed || goingTo != ticks)
        {
            return;
        }

        goingTo = null;
        if ((CellAt(SelectedCellLane ?? MachineRow, ticks)?.Bucket ?? ColumnAt(ticks)) is { } cell)
        {
            SelectInterval(cell.Interval);
        }
    }

    /// <summary>The machine's column holding <paramref name="ticks"/>: the zoomed view's own where it has arrived, else the overview's.</summary>
    private TimelineBucket? ColumnAt(long ticks)
    {
        IReadOnlyList<TimelineBucket> columns = timelineDetail is { } detail
            && detail.Interval.StartTicks <= ticks && ticks < detail.Interval.EndTicks
                ? detail.Buckets
                : Snapshot.Timeline;
        return columns.FirstOrDefault(column => column.Interval.StartTicks <= ticks && ticks < column.Interval.EndTicks);
    }

    /// <summary>
    /// Reads the search's text as a moment where it is one (<see cref="SessionMoment.IsMoment"/>): placed in the session,
    /// or why it cannot be.
    /// </summary>
    private void ReadSearchedMoment(string text)
    {
        searchedMoment = null;
        searchedMomentProblem = null;
        if (!realOverview || !SessionMoment.IsMoment(text, CultureInfo.CurrentCulture))
        {
            return;
        }

        if (SessionMoment.TryPlace(text, wholeSnapshot.WallClock, TimeZoneInfo.Local, wholeSnapshot.Extent,
                CultureInfo.CurrentCulture, out long ticks, out string? problem, wholeSnapshot.RetainedFromNanoseconds))
        {
            searchedMoment = ticks;
        }
        else
        {
            searchedMomentProblem = problem;
        }
    }

    /// <summary>
    /// The moment typed as a search's first hit: named where it falls on the wall clock, with its date and offset, and in
    /// session time - "Go to 09/29/2026 12:00:05.000000 UTC+02:00", "session time +5.000000 s".
    /// </summary>
    private SearchRow? SearchedMomentRow()
    {
        if (searchedMoment is not { } ticks)
        {
            return null;
        }

        long nanoseconds = ticks * 100;
        SessionClock session = SessionClock.Session(TimeZoneInfo.Local);
        string sessionTime = "session time " + session.Record(nanoseconds, CultureInfo.CurrentCulture);
        string label = wholeSnapshot.WallClock is { } wall
            ? "Go to " + SessionClock.Wall(wall, TimeZoneInfo.Local, wholeSnapshot.Extent).Moment(nanoseconds, CultureInfo.CurrentCulture)
            : "Go to " + sessionTime;
        string detail = (wholeSnapshot.WallClock is null ? string.Empty : sessionTime + " · ")
            + "centres the timeline on it and makes the cell holding it the analysis interval";
        return SearchRow.Of(new SearchHit(SearchHitKind.Moment, MomentKey(ticks), label, detail, 0, []));
    }

    private static string MomentKey(long ticks) => string.Create(CultureInfo.InvariantCulture, $"moment:{ticks}");

    private static long? MomentOf(SearchHit hit) => hit.Kind == SearchHitKind.Moment
        && long.TryParse(hit.Key.AsSpan("moment:".Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ticks)
            ? ticks
            : null;
}
