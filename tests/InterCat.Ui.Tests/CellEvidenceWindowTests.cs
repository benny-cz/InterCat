using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// §6.4 in the window: selecting a timeline cell opens the exact contributing evidence. A click on a cell of a mechanism's
/// lane, then E, lists exactly that cell's records, behind a chip naming the mechanism, which the card named before.
/// </summary>
public sealed class CellEvidenceWindowTests
{
    [AvaloniaFact(DisplayName = "§6.4: a click on a mechanism lane's cell, then E, lists exactly its records behind a chip naming the mechanism")]
    public async Task EvidenceFromAClickedCellIsItsRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(workspace.ShowsMechanismLanes);
        await DrawnTimeline.Counted(window, workspace, timeline);

        // A UDP cell, clicked: the card beneath the time scope names the records E will list.
        TimelineBucket cell = DrawnTimeline.Lane(workspace, Mechanism.Udp).First(bucket => bucket.ObservationCount > 0);
        Point at = timeline.TranslatePoint(timeline.PointOf(cell)!.Value, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        Assert.EndsWith(" · UDP records only", window.GetControl<TextBlock>("EvidenceSummaryText").Text, StringComparison.Ordinal);

        // The inspector lists the cell's own records beneath the time scope, each named for a screen reader.
        await workspace.CellRecordsReady;
        Dispatch();
        Assert.True(window.GetControl<StackPanel>("CellRecordsPanel").IsEffectivelyVisible);
        Assert.Equal("Its " + (cell.ObservationCount == 1 ? "1 record" : $"{cell.ObservationCount} records"),
            window.GetControl<TextBlock>("CellRecordsHeadingText").Text);
        ItemsControl records = window.GetControl<ItemsControl>("CellRecordsList");
        Assert.Equal(cell.ObservationCount, records.ItemCount);
        CellRecordRow first = Assert.IsType<CellRecordRow>(records.Items[0]);
        Assert.StartsWith("UDP ", first.Title, StringComparison.Ordinal);
        Assert.Contains(records.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == first.Title && block.IsEffectivelyVisible);
        Assert.Contains(records.GetVisualDescendants().OfType<Control>(),
            control => AutomationProperties.GetName(control) == first.AccessibleName);
        Assert.Contains(", at ", first.AccessibleName, StringComparison.Ordinal);

        // E lists exactly them, and the filter bar says why.
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Dispatch();
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        Assert.Contains(workspace.Filters, filter => filter.Chip == "Mechanism: UDP");
        Assert.Equal(cell.ObservationCount, workspace.EvidenceMarkTicks.Count);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.4: a record listed beside a chosen cell opens in E on Enter or a double click, selected there with the keyboard on it")]
    public async Task AListedRecordOpensInE()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Rows(), .. Burst()]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        await DrawnTimeline.Counted(window, workspace, timeline);
        TimelineBucket cell = DrawnTimeline.Lane(workspace, Mechanism.Udp).Single(bucket => bucket.ObservationCount == 3);
        ListBox records = await Choose(cell);
        Assert.Equal("Enter or a double click opens one in E", window.GetControl<TextBlock>("CellRecordsNoteText").Text);

        // Enter on the second record opens E with it selected, and E's table has the keyboard on it.
        CellRecordRow second = Assert.IsType<CellRecordRow>(records.Items[1]);
        Assert.True(records.ContainerFromIndex(1)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(second.Key, workspace.SelectedRung?.Key);
        Assert.True(workspace.HasSelectedEvidence);
        ListBox rungs = window.GetControl<ListBox>("RungList");
        Assert.Same(workspace.SelectedRung, rungs.SelectedItem);
        Assert.True(rungs.ContainerFromItem(rungs.SelectedItem!)!.IsKeyboardFocusWithin);

        // Back at the cell, a double click on the third opens it alike.
        Assert.True(workspace.Ascend());
        Dispatch();
        records = await Choose(cell);
        CellRecordRow third = Assert.IsType<CellRecordRow>(records.Items[2]);
        Control item = Assert.IsAssignableFrom<Control>(records.ContainerFromIndex(2));
        item.BringIntoView();
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Point on = item.TranslatePoint(new(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        Assert.True(window.InputHitTest(on) is Visual hit && item.IsVisualAncestorOf(hit));
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(third.Key, workspace.SelectedRung?.Key);
        window.Close();

        async Task<ListBox> Choose(TimelineBucket chosen)
        {
            Point at = timeline.TranslatePoint(timeline.PointOf(chosen)!.Value, window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatch();
            await workspace.CellRecordsReady;
            Dispatch();
            ListBox list = window.GetControl<ListBox>("CellRecordsList");
            Assert.Equal(3, list.ItemCount);
            return list;
        }
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>client.exe sending to server.exe over one paired connection, and a datagram to a resolver after each send.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, 20).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (3 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(11 + (5 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (3 * index)))
                .Between("127.0.0.1:8080", "127.0.0.1:50000")),
            Timed(Transfer(12 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 16, 100, (ulong)(102 + (3 * index)))
                .Between("127.0.0.1:50001", "127.0.0.1:53") with { Mechanism = Mechanism.Udp }),
        }),
    ];

    /// <summary>Three datagrams client.exe sent to the resolver in one tick, after the exchange: a cell of three records.</summary>
    private static ObservationRowV1[] Burst() =>
    [
        .. Enumerable.Range(0, 3).Select(index => Timed(Transfer(150, ObservationKind.Send, AccountingSide.SendSide, 20 + index, 100,
                (ulong)(300 + index)).Between("127.0.0.1:50002", "127.0.0.1:53") with { Mechanism = Mechanism.Udp })),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
