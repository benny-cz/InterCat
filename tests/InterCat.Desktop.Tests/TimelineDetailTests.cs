using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The timeline at a view's own resolution, the whole session's included: asked for off the input path, superseded by a
/// newer viewport, and not asked for at all where the overview's columns already answer the view (plan §6.2, P25).
/// </summary>
public sealed class TimelineDetailTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact]
    public void AViewGetsItsOwnResolutionUnlessTheOverviewAnswersIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, 240).Select(index => Timed(Transfer(10 + index, ObservationKind.Send,
                AccountingSide.SendSide, 64, 100, (ulong)(100 + index)).Between(ClientEnd, ServerEnd))),
            Timed(Transfer(4_000, ObservationKind.Send, AccountingSide.SendSide, 3, 100, 9_000).Between(ClientEnd, ServerEnd)),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        Assert.Null(workspace.TimelineDetail);

        var zoomed = new TimeRange(10, 250);
        workspace.RequestTimelineDetail(zoomed, 24);
        await workspace.TimelineDetailReady;
        SessionTimelineDetail detail = Assert.IsType<SessionTimelineDetail>(workspace.TimelineDetail);
        Assert.Equal(zoomed, detail.Interval);
        Assert.Equal(24, detail.Buckets.Count);
        Assert.All(detail.Buckets, bucket => Assert.Equal(10, bucket.ObservationCount));
        Assert.Equal(workspace.DisplayedGeneration, detail.Generation);

        // The interval table is the timeline's table equivalent: it lists what is drawn, in the unit each window needs.
        IntervalRowTests.ListsDrawn(detail.Buckets, workspace.Intervals);
        Assert.Equal(detail.Buckets.Count, workspace.Intervals.Count);
        Assert.EndsWith("µs", workspace.Intervals[0].Window, StringComparison.Ordinal);
        Assert.StartsWith("Zoomed view", workspace.IntervalTableScope, StringComparison.Ordinal);

        // Only the newest viewport's answer is applied, whichever request finishes first.
        workspace.RequestTimelineDetail(new TimeRange(10, 100), 8);
        Task superseded = workspace.TimelineDetailReady;
        workspace.RequestTimelineDetail(new TimeRange(100, 200), 10);
        await superseded;
        await workspace.TimelineDetailReady;
        Assert.Equal(new TimeRange(100, 200), workspace.TimelineDetail!.Interval);

        // The overview already counts the whole extent in more columns than the view draws: nothing is asked for, and its
        // coarse buckets draw alone.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 24);
        await workspace.TimelineDetailReady;
        Assert.Null(workspace.TimelineDetail);
        IntervalRowTests.ListsDrawn(workspace.Snapshot.Timeline, workspace.Intervals);
        Assert.StartsWith("Whole session in", workspace.IntervalTableScope, StringComparison.Ordinal);

        // Drawn finer than the overview, the whole session is counted in the view's own columns, every record in one, and
        // the interval table lists them as the whole session.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 128);
        await workspace.TimelineDetailReady;
        SessionTimelineDetail whole = Assert.IsType<SessionTimelineDetail>(workspace.TimelineDetail);
        Assert.Equal(workspace.Snapshot.Extent, whole.Interval);
        Assert.Equal(128, whole.Buckets.Count);
        Assert.Equal(workspace.Snapshot.Timeline.Sum(bucket => bucket.ObservationCount), whole.Buckets.Sum(bucket => bucket.ObservationCount));
        // The table lists the 128 intervals it is drawn in, each run of empty ones as one row: the long quiet before the
        // last send is one row, not a hundred.
        IntervalRowTests.ListsDrawn(whole.Buckets, workspace.Intervals);
        Assert.True(workspace.Intervals.Count < 20, $"{workspace.Intervals.Count} rows list 128 intervals.");
        Assert.StartsWith("Whole session in 128 intervals · each run of empty ones as one row · ", workspace.IntervalTableScope,
            StringComparison.Ordinal);

        // Zoomed again, the view's own count replaces it.
        workspace.RequestTimelineDetail(zoomed, 24);
        await workspace.TimelineDetailReady;
        Assert.Equal(zoomed, workspace.TimelineDetail!.Interval);
    });

    [Fact(DisplayName = "§6.2: a view of the whole session lists it as the whole session while the previous publication's count stands in for its own")]
    public void AStandInForTheWholeSessionIsListedAsTheWholeSession() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(0, 240).Select(index => Timed(Transfer(10 + index, ObservationKind.Send,
            AccountingSide.SendSide, 64, 100, (ulong)(100 + index)).Between(ClientEnd, ServerEnd)))]);
        using WorkspaceViewModel first = Open(session);
        first.RequestTimelineDetail(first.Snapshot.Extent, 128);
        await first.TimelineDetailReady;
        Assert.StartsWith("Whole session in 128 intervals · ", first.IntervalTableScope, StringComparison.Ordinal);

        // A later publication runs on. Until its own count arrives the earlier one stands in, which covers less than the
        // whole session now, and the table still says it lists the whole session rather than a zoomed view.
        Publish(session.Store, [Timed(Transfer(4_000, ObservationKind.Send, AccountingSide.SendSide, 3, 100, 9_000)
            .Between(ClientEnd, ServerEnd))]);
        using WorkspaceViewModel next = Open(session);
        next.AdoptTimeline(first.CarryTimeline());
        next.RequestTimelineDetail(next.Snapshot.Extent, 128);
        Assert.NotEqual(next.Snapshot.Extent, next.TimelineDetail!.Interval);
        Assert.StartsWith("Whole session in 128 intervals · ", next.IntervalTableScope, StringComparison.Ordinal);
        await next.TimelineDetailReady;
        Assert.Equal(next.Snapshot.Extent, next.TimelineDetail!.Interval);
        Assert.StartsWith("Whole session in 128 intervals · ", next.IntervalTableScope, StringComparison.Ordinal);

        // Zoomed, the stand-in or its own count is a zoomed view.
        var zoomed = new TimeRange(10, 250);
        next.RequestTimelineDetail(zoomed, 24);
        await next.TimelineDetailReady;
        Assert.StartsWith("Zoomed view", next.IntervalTableScope, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "R15: the interval table's selected row is the analysis interval's however it was chosen, and none where no row is exactly it")]
    public void TheTablesSelectedRowFollowsTheAnalysisInterval() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, 240).Select(index => Timed(Transfer(10 + index, ObservationKind.Send,
                AccountingSide.SendSide, 64, 100, (ulong)(100 + index)).Between(ClientEnd, ServerEnd))),
            Timed(Transfer(4_000, ObservationKind.Send, AccountingSide.SendSide, 3, 100, 9_000).Between(ClientEnd, ServerEnd)),
        ]);
        using WorkspaceViewModel workspace = Open(session);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 128);
        await workspace.TimelineDetailReady;
        IReadOnlyList<TimelineBucket> drawn = workspace.TimelineDetail!.Buckets;

        // A row chosen in the table is the analysis interval.
        IntervalRow first = workspace.Intervals[0];
        workspace.SelectedIntervalRow = first;
        Assert.Equal(first.Interval, workspace.SelectedInterval);

        // A cell chosen on the timeline, or a step, selects its own row, and the row chosen before is no longer selected.
        TimelineBucket last = drawn.Last(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(last);
        Assert.Same(workspace.Intervals.Single(row => row.Interval == last.Interval), workspace.SelectedIntervalRow);
        workspace.SelectInterval(drawn[1].Interval);
        Assert.Equal(drawn[1].Interval, workspace.SelectedIntervalRow?.Interval);

        // One cell of a run of empty ones listed as one row, or a brushed range, is no row; the run's row chosen selects
        // the run whole.
        IntervalRow run = workspace.Intervals.First(row => row.Cells > 1);
        workspace.SelectInterval(drawn.First(bucket => run.Interval.Contains(bucket.Interval.StartTicks)).Interval);
        Assert.Null(workspace.SelectedIntervalRow);
        workspace.SelectedIntervalRow = run;
        Assert.Equal(run.Interval, workspace.SelectedInterval);
        workspace.SelectInterval(new TimeRange(drawn[0].Interval.StartTicks, drawn[2].Interval.EndTicks));
        Assert.Null(workspace.SelectedIntervalRow);

        // Clearing the selection clears the row, and a new resolution keeps no row that is not exactly the interval.
        workspace.SelectedIntervalRow = first;
        workspace.ClearSelection();
        Assert.Null(workspace.SelectedIntervalRow);
        workspace.SelectedIntervalRow = first;
        workspace.RequestTimelineDetail(new TimeRange(10, 250), 24);
        await workspace.TimelineDetailReady;
        Assert.Equal(first.Interval, workspace.SelectedInterval);
        Assert.Null(workspace.SelectedIntervalRow);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
