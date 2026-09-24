using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionTimelineTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "R21: a viewport timeline counts what a scan counts, and at the overview's resolution it is the overview")]
    public void AViewportTimelineCountsWhatAScanCounts()
    {
        using var session = new TemporarySession();
        long[] ticks = [.. Enumerable.Range(0, 240).Select(index => (long)((index * 7_919) % 10_000)).Distinct().Order()];
        ObservationRowV1[] rows =
        [
            .. ticks.Select((tick, index) => Timed(
                Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1)).Between(ClientEnd, ServerEnd))),

            // A record without a usable session time has no column in any interval.
            Transfer(10_001, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 9_999).Between(ClientEnd, ServerEnd),
        ];
        Publish(session.Store, rows);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;

        SessionTimelineDetail whole = SessionTimelineQuery.Detail(session.Store, extent, SessionOverviewProjector.MaximumTimelineBuckets);
        Assert.Equal(overview.Timeline, whole.Buckets);
        Assert.Equal((overview.SessionId, overview.Generation), (whole.SessionId, whole.Generation));

        var interval = new TimeRange(2_500, 7_500);
        SessionTimelineDetail zoomed = SessionTimelineQuery.Detail(session.Store, interval, 10);
        Assert.Equal(10, zoomed.Buckets.Count);
        Assert.Equal(interval.StartTicks, zoomed.Buckets[0].Interval.StartTicks);
        Assert.Equal(interval.EndTicks, zoomed.Buckets[^1].Interval.EndTicks);
        Assert.All(zoomed.Buckets.Zip(zoomed.Buckets.Skip(1)), pair =>
            Assert.Equal(pair.First.Interval.EndTicks, pair.Second.Interval.StartTicks));
        Assert.All(zoomed.Buckets, bucket => Assert.Equal(
            ticks.Count(tick => bucket.Interval.Contains(tick)), bucket.ObservationCount));
        Assert.Equal(ticks.Count(interval.Contains), zoomed.Buckets.Sum(bucket => bucket.ObservationCount));

        // A span narrower than the columns asked for gets one column per tick, never an empty sliver.
        Assert.Equal(5, SessionTimelineQuery.Detail(session.Store, new TimeRange(100, 105), 50).Buckets.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionTimelineQuery.Detail(session.Store, interval, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionTimelineQuery.Detail(session.Store, interval, SessionTimelineQuery.MaximumColumns + 1));
    }

    [Fact(DisplayName = "R21: the minimap counts every timed record and shows a capture gap even where nothing was observed")]
    public void TheMinimapShowsTheCapturesOwnCoverage()
    {
        using var session = new TemporarySession();
        long[] ticks = [.. Enumerable.Range(0, 30).Select(index => index * 100L), .. Enumerable.Range(60, 30).Select(index => index * 100L), 12_000];
        ObservationRowV1[] rows = [.. ticks.Select((tick, index) => Timed(
            Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1)).Between(ClientEnd, ServerEnd)))];
        Publish(session.Store, rows, coverage: TwoEpochs());

        SessionMinimap minimap = SessionOverviewProjector.Project(session.Store).Minimap!;
        Assert.Equal(new TimeRange(0, 12_001), minimap.Extent);
        Assert.Equal(SessionMinimap.MaximumColumns, minimap.Counts.Count);
        Assert.Equal(ticks.Length, minimap.Counts.Sum());
        Assert.Equal(0, minimap.IntervalOf(0).StartTicks);
        Assert.Equal(12_001, minimap.IntervalOf(minimap.Counts.Count - 1).EndTicks);

        // The first epoch delivered everything; the second reported loss, and so does every column it holds, including
        // the empty stretch the loss may explain. Past the delivered readings the capture's coverage is unknown.
        Assert.Equal(CoverageState.Covered, StateAt(minimap, 1_000));
        Assert.Equal(CoverageState.Covered, StateAt(minimap, 4_000));
        Assert.Equal(0, minimap.Counts[ColumnAt(minimap, 5_500)]);
        Assert.Equal(CoverageState.PartialGap, StateAt(minimap, 5_500));
        Assert.Equal(CoverageState.PartialGap, StateAt(minimap, 8_000));
        Assert.Equal(CoverageState.UnknownCoverage, StateAt(minimap, 11_000));

        using var legacy = new TemporarySession();
        Publish(legacy.Store, rows);
        Assert.All(SessionOverviewProjector.Project(legacy.Store).Minimap!.Capture,
            state => Assert.Equal(CoverageState.UnknownCoverage, state));
    }

    private static int ColumnAt(SessionMinimap minimap, long tick) =>
        Enumerable.Range(0, minimap.Counts.Count).Single(column => minimap.IntervalOf(column).Contains(tick));

    private static CoverageState StateAt(SessionMinimap minimap, long tick) => minimap.Capture[ColumnAt(minimap, tick)];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static CoverageLedgerV1 TwoEpochs() => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [Epoch(1, 0, 4_999, lost: 0), Epoch(2, 5_000, 9_999, lost: 3)],
    };

    private static CoverageEpochV1 Epoch(int number, long first, long last, long lost) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
        ],
        Deliveries = [new CoverageDeliveryV1
        {
            ProviderId = NetworkProvider,
            EventId = 10,
            Version = 0,
            Delivered = 30,
            Admitted = 30,
            Omitted = 0,
        }],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };
}
