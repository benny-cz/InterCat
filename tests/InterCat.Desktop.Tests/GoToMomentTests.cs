using System.Globalization;
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
/// Going to a moment (§6.2, §6.7): a time typed into the search - a time of day on the wall clock the capture's machine
/// read, or session time - is offered first among its hits; going to it moves the timeline there and makes the cell
/// holding it the analysis interval, as a step would; a time the session cannot place is said with why.
/// </summary>
public sealed class GoToMomentTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§6.2: a time of day typed into the search is offered first, and going to it centres the timeline there and makes the cell holding it the analysis interval, as a step would")]
    public void ATimeOfDayIsGoneTo() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Records(), calibration: Calibration());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        var asked = new List<TimeRange>();
        workspace.ViewportRequested += (_, range) => asked.Add(range);

        // Zoomed to the session's first tenth, the timeline is asked to show 1.5 s in, at the same span.
        TimeRange extent = workspace.Snapshot.Extent;
        var zoomed = new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 10));
        workspace.RequestTimelineDetail(zoomed, 40);
        await workspace.TimelineDetailReady;

        // The wall clock read noon at the session's start, so 1.5 s in it read 12:00:01.5 UTC, typed in this computer's zone.
        string typed = TimeZoneInfo.ConvertTime(Noon.AddSeconds(1.5), TimeZoneInfo.Local).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        workspace.SearchText = typed;
        SearchRow offered = workspace.SearchResults[0];
        Assert.Equal("TIME", offered.Kind);
        SessionClock wall = SessionClock.Wall(workspace.Snapshot.WallClock!, TimeZoneInfo.Local, extent);
        Assert.Equal("Go to " + wall.Moment(1_500_000_000, CultureInfo.CurrentCulture), offered.Label);
        Assert.Equal("session time " + SessionClock.Session(TimeZoneInfo.Local).Record(1_500_000_000, CultureInfo.CurrentCulture)
            + " · centres the timeline on it and makes the cell holding it the analysis interval", offered.Detail);
        Assert.Equal(string.Empty, offered.Observations);
        Assert.Equal($"Time, {offered.Label}, {offered.Detail}. Press Enter to go there.", offered.AccessibleName);
        Assert.Equal(offered, workspace.SelectedSearchResult);
        Assert.Equal("A moment of this session · Enter goes there, Esc clears", workspace.SearchSummary);

        // Gone to, the search is cleared and the timeline is asked to centre on it at the span it had. At the machine rung
        // with no lane selected, the column holding it is the analysis interval, which no lane's cell explains, as a
        // step's is not.
        Assert.True(workspace.OpenSearchResult());
        await workspace.GoToReady;
        Assert.Equal(string.Empty, workspace.SearchText);
        TimeRange centred = Assert.Single(asked);
        Assert.Equal(zoomed.SpanTicks, centred.SpanTicks);
        Assert.Equal(15_000_000, centred.StartTicks + (centred.SpanTicks / 2));
        TimeRange column = workspace.SelectedInterval!.Value;
        Assert.True(column.StartTicks <= 15_000_000 && 15_000_000 < column.EndTicks);
        Assert.Contains(workspace.TimelineDetail!.Buckets, bucket => bucket.Interval == column);
        Assert.False(workspace.HasCellExplanation);

        // Its count is drawn in every lane the overview draws, the lifecycle lane empty where it has nothing in view; with
        // a lane missing the timeline would draw none of it.
        Assert.Equal(workspace.Snapshot.MechanismLanes.Select(lane => lane.Mechanism),
            workspace.TimelineDetail.MechanismLanes.Select(lane => lane.Mechanism));
        Assert.All(workspace.TimelineDetail.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.ProcessLifecycle).Buckets,
            bucket => Assert.Equal(0, bucket.ObservationCount));

        // With the TCP lane selected, its cell holding the moment is chosen, the zoom's own, and explained as that lane's;
        // the ranked table counts the records it holds.
        workspace.SelectTimelineLane(Mechanism.Tcp);
        Assert.True(workspace.GoTo(20_000_000));
        await workspace.GoToReady;
        TimeRange sent = workspace.SelectedInterval!.Value;
        Assert.True(sent.StartTicks <= 20_000_000 && 20_000_000 < sent.EndTicks);
        TimelineBucket cell = workspace.TimelineDetail!.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets
            .Single(bucket => bucket.Interval == sent);
        Assert.StartsWith(Counted(cell.ObservationCount, "TCP record"), workspace.CellExplanation, StringComparison.Ordinal);
        Assert.Contains(" here one of this view's own 40;", workspace.CellExplanation, StringComparison.Ordinal);
        await workspace.IntervalReady;
        Assert.Equal(cell.ObservationCount, Assert.Single(workspace.RungRows).Source.ObservationCount);

        // A moment gone to before an earlier one was counted is the one chosen: the earlier chooses nothing.
        var chosen = new List<TimeRange?>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.SelectedInterval)) chosen.Add(workspace.SelectedInterval);
        };
        Assert.True(workspace.GoTo(5_000_000));
        Task earlier = workspace.GoToReady;
        Assert.True(workspace.GoTo(12_000_000));
        await Task.WhenAll(earlier, workspace.GoToReady);
        Assert.NotEmpty(chosen);
        Assert.All(chosen, interval => Assert.True(interval!.Value.StartTicks <= 12_000_000 && 12_000_000 < interval.Value.EndTicks));
    });

    [Fact(DisplayName = "§6.2: a time the session cannot place is said with why, session time is gone to in any session, and a bare number is still a PID")]
    public void WhatCannotBePlacedIsSaid() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Records(), calibration: Calibration());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // A time of day an hour later is outside the session: said, and nothing is offered to go to.
        string later = TimeZoneInfo.ConvertTime(Noon.AddHours(1), TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
        workspace.SearchText = later;
        Assert.DoesNotContain(workspace.SearchResults, row => row.Kind == "TIME");
        Assert.StartsWith($"{later} is outside this session, which the wall clock read from ", workspace.SearchSummary,
            StringComparison.Ordinal);

        // One outside it that a name also matches says why first, then what matched: client.exe runs from a folder 7.0.
        workspace.SearchText = "7.0";
        Assert.DoesNotContain(workspace.SearchResults, row => row.Kind == "TIME");
        Assert.Contains(workspace.SearchResults, row => row.Kind == "GROUP");
        Assert.StartsWith("7.0 is outside this session, which runs ", workspace.SearchSummary, StringComparison.Ordinal);
        Assert.EndsWith(" · Enter opens the selected one, Esc clears", workspace.SearchSummary, StringComparison.Ordinal);

        // A bare number is a PID, as it always was.
        workspace.SearchText = "100";
        Assert.DoesNotContain(workspace.SearchResults, row => row.Kind == "TIME");
        Assert.Contains(workspace.SearchResults, row => row.Kind == "PROCESS");

        // In a session that recorded no wall clock, session time is gone to, and a time of day is refused with why.
        using var imported = new TemporarySession();
        Publish(imported.Store, Records());
        using WorkspaceViewModel plain = Open(imported);
        await plain.LayoutReady;
        plain.SearchText = "0.00015 s";
        SearchRow offered = plain.SearchResults[0];
        Assert.Equal("Go to session time " + SessionClock.Session(TimeZoneInfo.Local).Record(150_000, CultureInfo.CurrentCulture),
            offered.Label);
        Assert.True(plain.OpenSearchResult());
        await plain.GoToReady;
        TimeRange chosen = plain.SelectedInterval!.Value;
        Assert.True(chosen.StartTicks <= 1_500 && 1_500 < chosen.EndTicks);
        plain.SearchText = "12:00";
        Assert.Equal("This session recorded no wall clock, so 12:00 places nothing in it: type a session time, such as 312.5 s.",
            plain.SearchSummary);

        // The tour is no session: nothing there is a moment of one.
        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "tour");
        tour.SearchText = "1.5 s";
        Assert.DoesNotContain(tour.SearchResults, row => row.Kind == "TIME");
        Assert.False(tour.GoTo(15_000_000));
    });

    [Fact(DisplayName = "§6.2: going to a moment with a group's process lane selected, counted coarser than the view, chooses that lane's cell holding it, not the machine column beneath")]
    public void TheSelectedLanesCellIsChosen() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(40));
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        string group = workspace.Snapshot.Processes.First(node => node.ProcessId == 2_000).GroupKey;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == group);
        Assert.True(workspace.Descend());

        // Zoomed in at 1,000 columns, the forty lanes are counted in 500, each cell two of the machine row's columns wide.
        TimeRange extent = workspace.Snapshot.Extent;
        workspace.RequestTimelineDetail(new TimeRange(extent.StartTicks + 1, extent.EndTicks), 1_000);
        await workspace.TimelineDetailReady;
        ProcessInstanceId owner = workspace.ProcessLaneDisplay[0].ProcessId;
        workspace.SelectProcess(owner);
        long moment = workspace.ProcessLaneDisplay[0].Buckets.First(bucket => bucket.ObservationCount > 0).Interval.EndTicks - 1;

        // Gone to, the selected lane's cell holding it is the analysis interval, explained as that lane's.
        Assert.True(workspace.GoTo(moment));
        await workspace.GoToReady;
        TimeRange chosen = workspace.SelectedInterval!.Value;
        Assert.True(chosen.StartTicks <= moment && moment < chosen.EndTicks);
        Assert.Contains(workspace.ProcessLaneDisplay.Single(lane => lane.ProcessId == owner).Buckets, bucket => bucket.Interval == chosen);
        Assert.DoesNotContain(workspace.TimelineDetail!.Buckets, bucket => bucket.Interval == chosen);
        Assert.Contains("coarser than the view's", workspace.CellExplanation, StringComparison.Ordinal);
    });

    private static string Counted(int count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>client.exe (PID 100), from its version's folder, created, then sending every 10 ms for 2.5 s.</summary>
    private static ObservationRowV1[] Records() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\7.0\client.exe", SessionRelativeTicks = 100 },
        .. Enumerable.Range(1, 25_000 / 100).Select(index => Transfer(index * 100_000L, ObservationKind.Send,
            AccountingSide.SendSide, 64, 100, (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080") with
        {
            SessionRelativeTicks = index * 10_000_000L,
        }),
    ];

    /// <summary><paramref name="count"/> instances of pool.exe, each created and then sending ten times across the capture.</summary>
    private static ObservationRowV1[] Pool(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Lifecycle(index + 1, ObservationKind.Create, 2_000 + index, (ulong)(index * 20)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = (index + 1) * 100L,
            },
        }.Concat(Enumerable.Range(0, 10).Select(step =>
            Transfer(100 + (step * 100) + index, ObservationKind.Send, AccountingSide.SendSide, 8, 2_000 + index,
                (ulong)((index * 20) + step + 1)) with { SessionRelativeTicks = (100 + (step * 100) + index) * 100L }))),
    ];

    /// <summary>The wall clock read at the session's start, noon, and at its stop, 2.5 s in.</summary>
    private static ClockCalibrationV1 Calibration() => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = TestSessions.Capture.Value,
        ClockId = TestClock.Id.Value,
        WallClock = "test-wall-clock",
        Samples =
        [
            new() { NativeTicks = 0, Utc = Noon, AcquisitionUncertaintyNanoseconds = 200 },
            new() { NativeTicks = 25_000_000, Utc = Noon.AddSeconds(2.5), AcquisitionUncertaintyNanoseconds = 200 },
        ],
    };
}
