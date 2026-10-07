using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
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

        // A UDP cell, clicked: the card beneath the time scope names the records E will list.
        TimelineBucket cell = workspace.Snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Udp).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        Point at = timeline.TranslatePoint(timeline.PointOf(cell)!.Value, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        Assert.EndsWith(" · UDP records only", window.GetControl<TextBlock>("EvidenceSummaryText").Text, StringComparison.Ordinal);

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

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
