using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.1 and §26.2: one theme definition per mode, followed at runtime. A change of mode must reach the resources the
/// window binds, the canvases that draw with brushes of their own, and the legend that keys hue to mechanism.
/// </summary>
public sealed class ThemeModeTests
{
    [AvaloniaFact(DisplayName = "§6.1: a change of theme mode reaches the resources, the drawn panes and the legend, and back")]
    public async Task AChangeOfModeReachesEveryPane()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(40));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        GraphDisplayNode node = workspace.GraphDisplay.Nodes.First(candidate => candidate.Kind == GraphNodeKind.Process);
        Point nodeAt = graph.TranslatePoint(graph.PointOf(node.Key)!.Value, window)!.Value;
        Point paneAt = graph.TranslatePoint(new(6, graph.Bounds.Height - 6), window)!.Value;
        ItemsControl legend = window.GetControl<ItemsControl>("LegendList");

        try
        {
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                Dispatch();
                WriteableBitmap frame = window.CaptureRenderedFrame()!;
                SurfaceTokens surfaces = ThemePalette.Surfaces(mode);

                // The pane's ground comes from a bound resource; the node's fill from the graph's own brushes.
                Assert.Equal(ThemeResources.ToColor(surfaces.Plot), PixelAt(frame, paneAt));
                Assert.Equal(ThemeResources.ToColor(surfaces.Elevated), PixelAt(frame, nodeAt));

                // The legend keys each hue: its chip glyph in the family's fill, its name in the family's ink.
                FamilyTokens tcp = ThemePalette.TokensFor(mode, MechanismFamily.Tcp);
                LegendEntry entry = Assert.Single(workspace.Legend, candidate => candidate.Label == tcp.Label);
                Control row = Assert.IsAssignableFrom<Control>(legend.ContainerFromItem(entry));
                TextBlock[] texts = [.. row.GetVisualDescendants().OfType<TextBlock>()];
                Assert.Equal(ThemeResources.ToColor(tcp.Fill),
                    Assert.IsAssignableFrom<ISolidColorBrush>(texts.Single(text => text.Text == entry.Glyph).Foreground).Color);
                Assert.Equal(ThemeResources.ToColor(tcp.Ink),
                    Assert.IsAssignableFrom<ISolidColorBrush>(texts.Single(text => text.Text == entry.Label).Foreground).Color);
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    /// <summary>One pixel of a rendered frame, whatever byte order the frame keeps.</summary>
    private static Color PixelAt(WriteableBitmap frame, Point point)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        int value = Marshal.ReadInt32(buffer.Address, ((int)point.Y * buffer.RowBytes) + ((int)point.X * 4));
        byte first = (byte)value;
        byte second = (byte)(value >> 8);
        byte third = (byte)(value >> 16);
        byte alpha = (byte)(value >> 24);
        return buffer.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(alpha, first, second, third)
            : Color.FromArgb(alpha, third, second, first);
    }

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times.</summary>
    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
