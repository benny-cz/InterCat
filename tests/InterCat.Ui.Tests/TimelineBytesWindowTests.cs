using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.2 in the window: under a byte ranking the machine rung's lanes plot the bytes it measures, a bar under the pointer
/// states what it plots against the byte rate its height was scaled by, and a column of sends that recorded no size is
/// keyed in the legend as unmeasured (§6.6).
/// </summary>
public sealed class TimelineBytesWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§6.2: a byte lane's bar under the pointer states what it plots, against the rate its height is scaled by")]
    public async Task AByteLanesBarStatesWhatItPlots()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30)]);
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.False(window.GetControl<StackPanel>("UnmeasuredLegend").IsVisible);

        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        TimelineByteLayer plotted = Assert.IsType<TimelineByteLayer>(workspace.TimelineBytes);

        // The lanes share one scale: the highest rate any lane's column plots, in bytes per tick.
        double RateOf(SessionIntervalByteMeasures lane, int column) =>
            (double)(lane.Columns[column].ValueOf(RankingMetric.BytesSent).Value ?? 0) / lane.IntervalOf(column).SpanTicks;
        double peak = plotted.Overview.Lanes.Max(lane => Enumerable.Range(0, lane.Columns.Count).Max(column => RateOf(lane, column)));
        SessionIntervalByteMeasures tcp = plotted.Overview.Of(Mechanism.Tcp)!;
        int busiest = Enumerable.Range(0, tcp.Columns.Count).MaxBy(column => RateOf(tcp, column));
        TimelineBucket bar = workspace.Snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets
            .Single(bucket => bucket.Interval == tcp.IntervalOf(busiest));

        // The bar under the pointer says what it plots, and reads its height against the rate the view scaled by.
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        window.MouseMove(timeline.TranslatePoint(timeline.PointOf(bar)!.Value, window)!.Value);
        Dispatch();
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.Contains(card.Lines, line => line.StartsWith("Plotted: ", StringComparison.Ordinal)
            && line.Contains(" sent on ", StringComparison.Ordinal));
        Assert.Contains(card.Lines, line => line.EndsWith("height against the busiest mechanism lane in this time view, "
            + $"{WorkspaceRowBuilder.DescribeByteRate(peak * WorkspaceTime.TicksPerSecond)} (shared scale)", StringComparison.Ordinal));

        // The send that recorded no size is drawn unmeasured, which the legend keys beside the graph's.
        Assert.True(workspace.TimelineDrawsUnmeasured);
        Assert.True(window.GetControl<StackPanel>("UnmeasuredLegend").IsVisible);
        Save(window, "timeline-bytes.png");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.2: a group's byte lane under the pointer states its process's bytes against the rate its rows share")]
    public async Task AGroupsByteLaneStatesItsProcesssBytes()
    {
        // client.exe runs twice, so its group has a lane for each instance.
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. SecondClient(10)]);
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        Dispatch();
        await workspace.TimelineBytesReady;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(workspace.ProcessLaneBytes is not null, $"lanes shown: {workspace.ShowsProcessLanes}; lanes: "
            + $"{workspace.ProcessLaneDisplay.Count}; level: {workspace.LevelBadge}; caption: {workspace.TimelineCaption}");
        ProcessLaneByteLayer plotted = Assert.IsType<ProcessLaneByteLayer>(workspace.ProcessLaneBytes);

        // The machine row and the lanes share one scale: the highest rate any of them plots, in bytes per tick.
        double RateOf(SessionIntervalByteMeasures row, int column) =>
            (double)(row.Columns[column].ValueOf(RankingMetric.BytesSent).Value ?? 0) / row.IntervalOf(column).SpanTicks;
        double peak = new[] { plotted.Measures.Machine }.Concat(plotted.Measures.Lanes)
            .Max(row => Enumerable.Range(0, row.Columns.Count).Max(column => RateOf(row, column)));
        Assert.Equal(2, workspace.ProcessLaneDisplay.Count);
        ProcessTimelineLane lane = workspace.ProcessLaneDisplay[0];
        SessionIntervalByteMeasures measured = plotted.Measures.Of(lane.ProcessId)!;
        int busiest = Enumerable.Range(0, measured.Columns.Count).MaxBy(column => RateOf(measured, column));
        TimelineBucket bar = lane.Buckets.Single(bucket => bucket.Interval == measured.IntervalOf(busiest));

        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        window.MouseMove(timeline.TranslatePoint(timeline.PointOf(bar)!.Value, window)!.Value);
        Dispatch();
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.Contains(card.Lines, line => line.StartsWith("Plotted: ", StringComparison.Ordinal)
            && line.Contains(" sent on ", StringComparison.Ordinal));
        Assert.Contains(card.Lines, line => line.EndsWith("height against the busiest visible lane including machine context, "
            + $"{WorkspaceRowBuilder.DescribeByteRate(peak * WorkspaceTime.TicksPerSecond)} (shared scale)", StringComparison.Ordinal));
        Save(window, "timeline-group-bytes.png");
        window.Close();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>The client and the server as rundowns name them.</summary>
    private static ObservationRowV1[] Named() =>
    [
        Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
    ];

    /// <summary>A client sending 64 bytes and a server receiving them, <paramref name="count"/> times, then a send with no size.</summary>
    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
        Transfer(10 + (2 * count), ObservationKind.Send, AccountingSide.SendSide, null, 100, (ulong)(100 + (2 * count)))
            .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * count)) * 100L },
    ];

    /// <summary>A second client.exe, PID 101, sending 128 bytes <paramref name="count"/> times.</summary>
    private static ObservationRowV1[] SecondClient(int count) =>
    [
        Lifecycle(3, ObservationKind.Inventory, 101, 3) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 300 },
        .. Enumerable.Range(0, count).Select(index =>
            Transfer(13 + (4 * index), ObservationKind.Send, AccountingSide.SendSide, 128, 101, (ulong)(300 + index))
                .Between("127.0.0.1:50002", ServerEnd) with { SessionRelativeTicks = (13 + (4 * index)) * 100L }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
