using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The timeline at a zoomed viewport's own resolution: asked for off the input path, superseded by a newer viewport,
/// and not asked for at all where the overview already counts the whole extent (plan §6.2, P25).
/// </summary>
public sealed class TimelineDetailTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact]
    public async Task AZoomedViewportGetsItsOwnResolutionAndTheWholeExtentNeedsNone()
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
        Assert.Equal(detail.Buckets.Select(bucket => bucket.Interval), workspace.Intervals.Select(row => row.Interval));
        Assert.EndsWith("µs", workspace.Intervals[0].Window, StringComparison.Ordinal);
        Assert.StartsWith("Zoomed view", workspace.IntervalTableScope, StringComparison.Ordinal);

        // Only the newest viewport's answer is applied, whichever request finishes first.
        workspace.RequestTimelineDetail(new TimeRange(10, 100), 8);
        Task superseded = workspace.TimelineDetailReady;
        workspace.RequestTimelineDetail(new TimeRange(100, 200), 10);
        await superseded;
        await workspace.TimelineDetailReady;
        Assert.Equal(new TimeRange(100, 200), workspace.TimelineDetail!.Interval);

        // The overview already counts the whole extent: nothing is asked for, and its coarse buckets draw alone.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 24);
        await workspace.TimelineDetailReady;
        Assert.Null(workspace.TimelineDetail);
        Assert.Equal(workspace.Snapshot.Timeline.Select(bucket => bucket.Interval), workspace.Intervals.Select(row => row.Interval));
        Assert.StartsWith("Whole session in", workspace.IntervalTableScope, StringComparison.Ordinal);
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
