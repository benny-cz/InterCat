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
/// §6.2's time base in the window's model: a session whose capture recorded its wall clock offers it, and read on it every
/// instant the view states follows - the time scope, a card, the evidence's scope and rows, and the inspector's record -
/// named as a changed setting, kept across a later publication, and never offered where the capture recorded none.
/// </summary>
public sealed class WallClockTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§6.2: read on the wall clock, every instant the view states follows: the time scope, a card, the evidence and the inspector's record")]
    public void EveryInstantFollowsTheWallClock() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Records(), calibration: Calibration());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        CultureInfo culture = CultureInfo.CurrentCulture;

        // Session time by default, the wall clock offered beside it.
        Assert.True(workspace.OffersWallClock);
        Assert.False(workspace.ReadsWallClock);
        Assert.False(workspace.TimeBase.IsWallClock);
        var interval = new TimeRange(1_000, 3_000);
        workspace.SelectInterval(interval);
        Assert.Equal(WorkspaceTime.FormatRange(interval, culture), workspace.IntervalLabel);
        Assert.Contains("±200 ns", workspace.WallClockTip, StringComparison.Ordinal);

        // Read on the wall clock, the time scope says so at once, and the setting is named as changed.
        var said = new List<string?>();
        workspace.PropertyChanged += (_, change) => said.Add(change.PropertyName);
        workspace.ReadsWallClock = true;
        SessionClock wall = SessionClock.Wall(overview.WallClock!, TimeZoneInfo.Local, workspace.Snapshot.Extent);
        Assert.True(workspace.ReadsWallClock);
        Assert.True(workspace.TimeBase.IsWallClock);
        Assert.Equal(wall.Range(interval, culture), workspace.IntervalLabel);
        Assert.EndsWith(SessionClock.OffsetText(TimeZoneInfo.Local.GetUtcOffset(Noon)), workspace.IntervalLabel, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkspaceViewModel.IntervalLabel), said);
        Assert.Contains("its times read on the wall clock", workspace.ChangedSettings);

        // A card states its interval on it.
        TimelineBucket bucket = workspace.Snapshot.Timeline.First(candidate => candidate.ObservationCount > 0);
        Assert.Equal(wall.HalfOpenRange(bucket.Interval, culture), workspace.DescribeTimelineHover(bucket, 1.0).Title);

        // E lists the brushed interval's records, its scope and each record's time on the wall clock; the record chosen
        // states its date and offset too.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.EndsWith(" · " + wall.Range(interval, culture), workspace.EvidenceScopeText, StringComparison.Ordinal);
        RungRow first = workspace.RungRows[0];
        Assert.StartsWith(wall.Record(110_000, culture) + " · PID 100", first.Detail, StringComparison.Ordinal);
        workspace.SelectedRung = first;
        Assert.Equal(wall.Moment(110_000, culture), workspace.SelectedEvidenceFields.Single(field => field.Label == "When").Value);

        // Back in session time the rows say so again, the record chosen still chosen, and nothing is named as changed.
        workspace.ReadsWallClock = false;
        Assert.StartsWith(SessionClock.Session(TimeZoneInfo.Local).Record(110_000, culture) + " · PID 100",
            workspace.RungRows[0].Detail, StringComparison.Ordinal);
        Assert.Equal(first.Key, workspace.SelectedRung?.Key);
        Assert.Contains(workspace.SelectedRung, workspace.RungRows);
        Assert.Equal("+0.000110 s", workspace.SelectedEvidenceFields.Single(field => field.Label == "When").Value
            .Replace(culture.NumberFormat.NumberDecimalSeparator, ".", StringComparison.Ordinal));
        Assert.EndsWith(" · " + WorkspaceTime.FormatRange(interval, culture), workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Empty(workspace.ChangedSettings);
    });

    [Fact(DisplayName = "§6.2: the wall clock is kept across a later publication of the session, and a session that recorded none never reads it")]
    public void TheChoiceIsKeptAndNeverGuessed() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Records(), calibration: Calibration());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        workspace.ReadsWallClock = true;
        using var later = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        Assert.Null(later.RestoreNavigation(workspace.CaptureNavigation()));
        Assert.True(later.ReadsWallClock);

        // An import recorded no wall clock: it is not offered, a choice of it reads session time still, and nothing says
        // a setting changed.
        using var imported = new TemporarySession();
        Publish(imported.Store, Records());
        SessionOverviewBundle plain = SessionOverviewProjector.Project(imported.Store);
        using var none = new WorkspaceViewModel(OverviewWorkspace.From(plain), plain.GraphIdentity,
            new SessionEvidenceSource(imported.Path, plain.SessionId, plain.Generation));
        await none.LayoutReady;
        Assert.False(none.OffersWallClock);
        none.ReadsWallClock = true;
        Assert.False(none.ReadsWallClock);
        Assert.False(none.TimeBase.IsWallClock);
        Assert.Equal(string.Empty, none.WallClockTip);
        Assert.Empty(none.ChangedSettings);
        _ = none.RestoreNavigation(workspace.CaptureNavigation());
        Assert.False(none.ReadsWallClock);

        // The tour is no session, and reads session time.
        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "tour");
        Assert.False(tour.OffersWallClock);
    });

    [Fact(DisplayName = "§6.2: read on the wall clock, an RPC call's row and the interval table's rows name their times on it")]
    public void CallsAndIntervalsFollowTheWallClock() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls(), calibration: Calibration());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        CultureInfo culture = CultureInfo.CurrentCulture;
        ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
        foreach (string key in new[] { caller.GroupKey, caller.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => RpcChannelKeys.IsRpc(row.Key));
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        Assert.StartsWith("+0.000010 s", workspace.RungRows[0].Detail.Replace(culture.NumberFormat.NumberDecimalSeparator, ".",
            StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(WorkspaceTime.FormatRange(workspace.Intervals[0].Interval, culture), workspace.Intervals[0].Window);

        // The call is named at the time of day its start was read, and each interval of the table on the wall clock.
        workspace.ReadsWallClock = true;
        SessionClock wall = SessionClock.Wall(overview.WallClock!, TimeZoneInfo.Local, workspace.Snapshot.Extent);
        Assert.StartsWith(wall.Record(10_000, culture) + " · ", workspace.RungRows[0].Detail, StringComparison.Ordinal);
        Assert.StartsWith("RPC call at " + wall.Record(10_000, culture), workspace.RungRows[0].SpokenName, StringComparison.Ordinal);
        Assert.Equal(wall.Range(workspace.Intervals[0].Interval, culture), workspace.Intervals[0].Window);
    });

    /// <summary>A caller's two RPC calls to the service control manager, 10 µs and 20 µs into the session.</summary>
    private static ObservationRowV1[] Calls() =>
    [
        Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\caller.exe", SessionRelativeTicks = 100 },
        .. new (long Ticks, ObservationKind Kind, int Activity)[]
        {
            (100, ObservationKind.RequestStart, 1), (120, ObservationKind.RequestEnd, 1),
            (200, ObservationKind.RequestStart, 2), (210, ObservationKind.RequestEnd, 2),
        }.Select((call, index) => RpcCall(call.Ticks, call.Kind, Direction.Outbound, 400, (ulong)(10 + index),
            new Guid(call.Activity, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1),
            call.Kind == ObservationKind.RequestStart ? Guid.Parse("367abb81-9844-35f1-ad32-98f038001003") : null,
            call.Kind == ObservationKind.RequestEnd ? 0 : null) with { SessionRelativeTicks = call.Ticks * 100 }),
    ];

    /// <summary>A client's sends to a server, 110 µs, 150 µs and 250 µs into the session.</summary>
    private static ObservationRowV1[] Records() =>
    [
        .. new long[] { 1_100, 1_500, 2_500 }.Select((ticks, index) => Transfer(ticks, ObservationKind.Send,
            AccountingSide.SendSide, 64, 100, (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080") with
        {
            SessionRelativeTicks = ticks * 100,
        }),
    ];

    /// <summary>The wall clock read at the session's start, noon, and at its stop, 2 s in.</summary>
    private static ClockCalibrationV1 Calibration() => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = TestSessions.Capture.Value,
        ClockId = TestClock.Id.Value,
        WallClock = "test-wall-clock",
        Samples =
        [
            new() { NativeTicks = 0, Utc = Noon, AcquisitionUncertaintyNanoseconds = 200 },
            new() { NativeTicks = 20_000_000, Utc = Noon.AddSeconds(2), AcquisitionUncertaintyNanoseconds = 200 },
        ],
    };
}
