using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
/// A real session's interval table in the window: its timeline sums no bytes, so once the table is shown each row states
/// what its interval's records sent and received, read for exactly the records it lists (R15, R21).
/// </summary>
public sealed class IntervalBytesWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R21: a real session's interval table states each interval's bytes once the table is shown")]
    public async Task TheIntervalTableStatesEachIntervalsBytes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30)]);
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);

        // Shown, the table reads its rows' bytes and states them in its own column, which a real session now has.
        workspace.ShowTables = true;
        Dispatch();
        await workspace.IntervalBytesReady;
        Dispatch();
        Assert.True(workspace.IntervalTableShowsBytes);
        ListBox list = window.GetControl<ListBox>("IntervalList");
        IntervalRow sending = workspace.Intervals.First(row => row.KnownBytes.StartsWith("64 B sent", StringComparison.Ordinal));
        IntervalRow receiving = workspace.Intervals.First(row => row.KnownBytes.EndsWith("64 B received", StringComparison.Ordinal));
        Assert.Equal("64 B sent · no receive recorded", sending.KnownBytes);
        Assert.Equal("no send recorded · 64 B received", receiving.KnownBytes);
        Assert.Contains(workspace.Intervals, row => row.ObservationCount == 0 && row.KnownBytes == "no transfer recorded");
        list.ScrollIntoView(sending);
        Dispatch();
        TextBlock cell = Assert.Single(list.ContainerFromItem(sending)!.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == sending.KnownBytes);
        Assert.True(cell.IsEffectivelyVisible && cell.Bounds.Width > 0, "The row's bytes are drawn in its Bytes column.");
        Assert.Equal(sending.KnownBytes, ToolTip.GetTip(cell));
        Assert.EndsWith(" · bytes each interval's records sent and received", workspace.IntervalTableScope,
            StringComparison.Ordinal);
        Save(window, "interval-bytes.png");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a timeline counted a column per pixel lists each interval holding a record as a row and each run of empty ones as one, which a choice selects whole")]
    public async Task EachRunOfEmptyIntervalsIsOneRow()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Sparse()]);
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        await DrawnTimeline.Counted(window, workspace, timeline);
        workspace.ShowTables = true;
        Dispatch();
        await workspace.IntervalBytesReady;
        Dispatch();

        // The whole session in a column per pixel, most of them empty: each interval holding a record is a row, and each
        // run of empty ones between them one row, so the table holds a handful of rows rather than hundreds.
        IReadOnlyList<TimelineBucket> drawn = DrawnTimeline.Machine(workspace);
        Assert.Equal(timeline.DeviceColumns, drawn.Count);
        int held = drawn.Count(bucket => bucket.ObservationCount > 0);
        Assert.InRange(workspace.Intervals.Count, held + 1, (2 * held) + 1);
        ListBox list = window.GetControl<ListBox>("IntervalList");
        Assert.Equal(workspace.Intervals.Count, list.ItemCount);
        Assert.StartsWith(string.Create(System.Globalization.CultureInfo.CurrentCulture,
            $"Whole session in {drawn.Count:N0} intervals · each run of empty ones as one row · "), workspace.IntervalTableScope,
            StringComparison.Ordinal);

        // A run reads as one row, to the eye and aloud, and choosing it makes its whole span the analysis interval.
        IntervalRow quiet = workspace.Intervals.First(row => row.Cells > 1);
        Assert.Equal("no transfer recorded", quiet.KnownBytes);
        list.ScrollIntoView(quiet);
        Dispatch();
        Control container = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(quiet));
        Assert.Contains(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"0 observations in {quiet.Cells:N0} intervals"),
            AutomationProperties.GetName(container), StringComparison.Ordinal);
        list.SelectedItem = quiet;
        Dispatch();
        Assert.Equal(quiet.Interval, workspace.SelectedInterval);
        Save(window, "interval-runs.png");
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

    /// <summary>A client sending 64 bytes and a server receiving them, <paramref name="count"/> times.</summary>
    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    /// <summary>Three exchanges far apart, so most of the session's time holds no record.</summary>
    private static ObservationRowV1[] Sparse() =>
    [
        .. new long[] { 1_000, 50_000, 100_000 }.SelectMany((tick, index) => new[]
        {
            Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = tick * 100L },
            Transfer(tick + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (2 * index)))
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (tick + 1) * 100L },
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
