using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>The live edge (§12, §19.3): the broker's preview counts for the chunks a view does not show yet.</summary>
public sealed class LiveEdgeTests
{
    [Fact(DisplayName = "§12/§19.3: a live edge previews only the chunks after those shown, on the session clock's own axis")]
    public void ALiveEdgePreviewsOnlyUnshownChunksOnTheSessionAxis()
    {
        // The test clock runs at 10 MHz from zero, so one native tick is one 100-nanosecond presentation tick.
        var preview = new BrokerCapturePreview(1_000, 5, 2, 30, 20, 1,
        [
            new(5, 12, Mechanism.Tcp, 4),
            new(5, 12, Mechanism.Udp, 6),
            new(4, 11, Mechanism.Tcp, 3),
            new(4, 12, Mechanism.Tcp, 2),
            new(3, 10, Mechanism.Tcp, 4),
        ]);
        LiveEdge edge = Assert.IsType<LiveEdge>(LiveEdge.From(preview, shownChunks: 3, TestSessions.TestClock));

        // Chunk 3 is shown, so its counts are not previewed again; chunks 4 and 5 meet in bin 12 and are added there.
        Assert.Equal(new[] { new TimeRange(11_000, 12_000), new TimeRange(12_000, 13_000) }, edge.Bins.Select(bin => bin.Interval));
        Assert.Equal(new[] { (Mechanism.Tcp, 3) }, edge.Bins[0].Counts);
        Assert.Equal(new[] { (Mechanism.Tcp, 6), (Mechanism.Udp, 6) }, edge.Bins[1].Counts);
        Assert.Equal((15L, 6, 0), (edge.PreviewedRecords, edge.Bins[1].CountOf(Mechanism.Udp), edge.Bins[0].CountOf(Mechanism.Udp)));
        Assert.Equal((3, 5, 0, 1L), (edge.ShownChunks, edge.OpenChunk, edge.UncoveredChunks, edge.UnbinnedRecords));

        // A view further behind than the preview reaches is told how many published chunks it neither shows nor previews.
        Assert.Equal(1, LiveEdge.From(preview, shownChunks: 1, TestSessions.TestClock)!.UncoveredChunks);

        // Nothing after the shown chunks is nothing to draw.
        Assert.Null(LiveEdge.From(preview, shownChunks: 5, TestSessions.TestClock));
    }

    [Fact(DisplayName = "§19.3: a live edge's card counts what it can neither show nor place, a chunk and a record in the singular")]
    public void ALiveEdgesCardCountsInTheSingular() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        TestSessions.Publish(session.Store, [TestSessions.Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
            .Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity);
        await workspace.LayoutReady;

        // Chunk 2 is neither shown nor previewed, and one previewed record fell outside the bins.
        var preview = new BrokerCapturePreview(1_000, 5, 2, 30, 20, 1, [new(5, 12, Mechanism.Tcp, 4), new(4, 11, Mechanism.Tcp, 3)]);
        workspace.ShowLivePreview(preview, shownChunks: 1);
        LiveEdge edge = Assert.IsType<LiveEdge>(workspace.LiveEdge);
        IReadOnlyList<string> lines = workspace.DescribeLiveEdgeHover(edge.Bins[0]).Lines;
        Assert.Contains("1 published chunk is neither shown nor previewed yet: this view is behind the capture", lines);
        Assert.Contains("Up to 1 previewed record falls outside the preview's bins", lines);

        // Two of each are plural, and agree with their verb.
        workspace.ShowLivePreview(preview with { UnbinnedRecords = 2 }, shownChunks: 0);
        lines = workspace.DescribeLiveEdgeHover(Assert.IsType<LiveEdge>(workspace.LiveEdge).Bins[0]).Lines;
        Assert.Contains("2 published chunks are neither shown nor previewed yet: this view is behind the capture", lines);
        Assert.Contains("Up to 2 previewed records fall outside the preview's bins", lines);
    });
}
