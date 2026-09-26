using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.4: selecting an entity highlights its records in the timeline; only a descent or Focus filters. The highlight is
/// the selection's own exact count on the columns the timeline draws, marked as a share of each bar, and it says so.
/// </summary>
public sealed class SelectionHighlightTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R15: a selected process highlights exactly its own records in the timeline, until cleared")]
    public async Task ASelectedProcessIsHighlighted()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(100));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimelineBucket bucket = workspace.Snapshot.Timeline.First(candidate => candidate.ObservationCount > 0);
        Point column = timeline.TranslatePoint(timeline.PointOf(bucket)!.Value, window)!.Value;
        WriteableBitmap before = Settle(window);

        ProcessNode client = workspace.Snapshot.Processes.Single(process => process.ProcessId == 100);
        workspace.SelectedProcess = client;
        Assert.Contains("counting the selection", workspace.TimelineCaption, StringComparison.Ordinal);
        await workspace.HighlightReady;
        Dispatch();

        // The client's own records, each bucket's share never more than the bar it marks, on the bars' own columns.
        IReadOnlyList<TimelineBucket> highlighted = Assert.IsAssignableFrom<IReadOnlyList<TimelineBucket>>(workspace.TimelineHighlightBuckets);
        Assert.Equal(100, highlighted.Sum(item => item.ObservationCount));
        Assert.Equal(workspace.Snapshot.Timeline.Select(item => item.Interval), highlighted.Select(item => item.Interval));
        Assert.All(highlighted.Zip(workspace.Snapshot.Timeline),
            pair => Assert.InRange(pair.First.ObservationCount, 0, pair.Second.ObservationCount));
        Assert.Contains($"selection highlighted: {client.NameWithPid}", workspace.TimelineCaption, StringComparison.Ordinal);

        // The bar under the selection is drawn differently, and its card gives the selection's count in it.
        WriteableBitmap after = Settle(window);
        Save(after, "selection-highlight.png");
        Assert.True(ColumnDiffers(before, after, column, timeline, window), "The highlight changed nothing the user sees.");
        window.MouseMove(column);
        Dispatch();
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        long share = highlighted.Single(item => item.Interval == timeline.HoveredBucket!.Interval).ObservationCount;
        Assert.Contains(card.Lines, line => line.StartsWith($"Selection, {client.NameWithPid}: {share:N0} record", StringComparison.Ordinal));

        // Clearing the selection removes the highlight and what it drew.
        workspace.ClearSelection();
        Dispatch();
        Assert.Null(workspace.TimelineHighlightBuckets);
        Assert.DoesNotContain("highlighted", workspace.TimelineCaption, StringComparison.Ordinal);
        window.MouseMove(new Point(2, 2));
        Assert.False(ColumnDiffers(before, Settle(window), column, timeline, window), "The cleared highlight is still drawn.");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a selected group highlights its members' records; the rung's own focus is not highlighted again")]
    public async Task AGroupIsHighlightedAndAFocusIsNot()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(100)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal(2, workspace.RungRows.Count);

        // The server's group, chosen by its machine-rung row: its instance's records are highlighted.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.Contains("server", StringComparison.OrdinalIgnoreCase));
        await workspace.HighlightReady;
        Dispatch();
        Assert.Equal(100 + 1, workspace.TimelineHighlightBuckets!.Sum(item => item.ObservationCount));
        Assert.Contains("selection highlighted", workspace.TimelineCaption, StringComparison.Ordinal);

        // Descending through the client's group to the client makes it the rung's focus, drawn in colour: the client, still
        // selected, adds no highlight over it.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.Contains("client", StringComparison.OrdinalIgnoreCase));
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        ProcessNode client = workspace.Snapshot.Processes.Single(process => process.ProcessId == 100);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == client.Id.ToString());
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        await workspace.HighlightReady;
        Dispatch();
        Assert.Equal(client.Id, workspace.SelectedProcess?.Id);
        Assert.True(workspace.TimelineShowsFocus);
        Assert.Null(workspace.TimelineHighlightBuckets);
        Assert.DoesNotContain("highlighted", workspace.TimelineCaption, StringComparison.Ordinal);

        // A channel chosen among the client's rows is highlighted there: its records at both ends.
        Channel channel = Assert.Single(workspace.Snapshot.Channels);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == channel.Key);
        await workspace.HighlightReady;
        Dispatch();
        Assert.Equal(channel.ObservationCount, workspace.TimelineHighlightBuckets!.Sum(item => item.ObservationCount));
        Assert.Contains($"selection highlighted: {channel.Name}", workspace.TimelineCaption, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: in mechanism lanes each lane marks the selection's own records of its mechanism")]
    public async Task EachMechanismLaneMarksItsShare()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(60), .. Datagrams(40)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.True(workspace.ShowsMechanismLanes);
        ProcessNode client = workspace.Snapshot.Processes.Single(process => process.ProcessId == 100);
        workspace.SelectedProcess = client;
        await workspace.HighlightReady;
        Dispatch();

        // The client sent 60 of the 120 TCP records and all 40 datagrams: each lane is marked with its own share.
        IReadOnlyList<MechanismTimelineLane> lanes = workspace.TimelineHighlightLanes!;
        Assert.Equal(60, lanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(40, lanes.Single(lane => lane.Mechanism == Mechanism.Udp).Buckets.Sum(bucket => bucket.ObservationCount));

        // A UDP lane's card gives the datagrams' share, not the bucket's total across mechanisms.
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimelineBucket datagrams = workspace.Snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Udp)
            .Buckets.First(bucket => bucket.ObservationCount > 0);
        window.MouseMove(timeline.TranslatePoint(timeline.PointOf(datagrams)!.Value, window)!.Value);
        Dispatch();
        Assert.Same(datagrams, timeline.HoveredBucket);
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.Contains(card.Lines, line => line == $"Selection, {client.NameWithPid}: "
            + $"{datagrams.ObservationCount:N0} {(datagrams.ObservationCount == 1 ? "record" : "records")} of them");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7: a later publication shows the selection's highlight until its own count replaces it")]
    public async Task TheHighlightStandsInAcrossAPublication()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(100));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ProcessNode client = workspace.Snapshot.Processes.Single(process => process.ProcessId == 100);
        workspace.SelectedProcess = client;
        await workspace.HighlightReady;
        Dispatch();
        IReadOnlyList<TimelineBucket> earlier = workspace.TimelineHighlightBuckets!;

        // The next generation's workspace, restored to the same selection before any view has drawn it: the earlier
        // highlight stands in rather than the timeline blinking back to none.
        Publish(session.Store, Exchange(20, first: 100));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var replacement = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        _ = replacement.RestoreNavigation(workspace.CaptureNavigation());
        Assert.Equal(client.Id, replacement.SelectedProcess?.Id);
        Assert.Null(replacement.TimelineHighlightBuckets);
        replacement.AdoptTimeline(workspace.CarryTimeline());
        Assert.Same(earlier, replacement.TimelineHighlightBuckets);

        // Once its timeline draws, the generation's own count replaces the stand-in: all 120 of the client's records.
        replacement.RequestTimelineDetail(overview.Extent!.Value, overview.Timeline.Count);
        await replacement.HighlightReady;
        Assert.Equal(120, replacement.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));
        window.Close();
    }

    /// <summary>Whether a bar's column reads differently in two frames of the same window.</summary>
    private static bool ColumnDiffers(WriteableBitmap before, WriteableBitmap after, Point column, TimelineView timeline, Window window)
    {
        double top = timeline.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        double bottom = top + timeline.Bounds.Height;
        for (double y = top + 1; y < bottom - 1; y++)
        {
            if (At(before, new Point(column.X, y)) != At(after, new Point(column.X, y)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Keeps a frame beside the tests' other renders, for a person to look at.</summary>
    private static void Save(WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>The client and the server as rundowns name them, so each is a group of its own.</summary>
    private static ObservationRowV1[] Named() =>
    [
        Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
    ];

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times from exchange <paramref name="first"/>.</summary>
    private static ObservationRowV1[] Exchange(int count, int first = 0) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    /// <summary>
    /// <paramref name="count"/> datagrams the client sends beside its first exchanges, at the same readings, so a bucket holds
    /// both mechanisms; nothing in the capture receives them.
    /// </summary>
    private static ObservationRowV1[] Datagrams(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index =>
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 32, 100, (ulong)(10_000 + index))
                .Between("127.0.0.1:5353", "10.0.0.9:5354")
                with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (10 + (2 * index)) * 100L }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
