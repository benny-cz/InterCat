using InterCat.Analysis.Tests;
using InterCat.CaptureBroker;
using InterCat.Desktop;
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
}
