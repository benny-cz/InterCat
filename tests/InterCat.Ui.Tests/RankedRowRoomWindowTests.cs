using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The ranked table's rows in the rail (§6.1, R15): each is its two lines and the touch target about them, so a shown
/// session's rail holds as many as it can beside its capture card.
/// </summary>
public sealed class RankedRowRoomWindowTests
{
    [AvaloniaTheory(DisplayName = "§6.1: a ranked row is its two lines and its touch target, so a saved session's rail shows six rows whole at the default window and the start of a third at the smallest")]
    [InlineData(1456, 939, 6)]
    [InlineData(1080, 700, 2)]
    public async Task ARankedRowTakesOnlyItsRoom(int width, int height, int whole)
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(0, 12).Select(index =>
            Lifecycle(1 + index, ObservationKind.Create, 100 + index, (ulong)(1 + index)) with { ResourceName = $@"C:\Tools\app{index}.exe" })]);
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Published generation 1.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _ = window.CaptureRenderedFrame();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // A row is its 44 px touch target and its 5 px margins, and 4 px of padding above and below: 62 px, where the
            // theme's padding made it 75.
            ListBox list = window.GetControl<ListBox>("RungList");
            Control first = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
            Assert.Equal(62, first.Bounds.Height, 0);

            // So many rows stand whole in the list, and the next begins inside it.
            double Bottom(int index) => Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index))
                .TranslatePoint(new Point(0, first.Bounds.Height), list)!.Value.Y;
            Assert.True(Bottom(whole - 1) <= list.Bounds.Height, $"Row {whole} ends at {Bottom(whole - 1):F0} of {list.Bounds.Height:F0} px.");
            double next = Bottom(whole) - first.Bounds.Height;
            Assert.True(next < list.Bounds.Height, $"Row {whole + 1} begins at {next:F0} of {list.Bounds.Height:F0} px.");
        }
        finally
        {
            window.Close();
        }
    }
}
