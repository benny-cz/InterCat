using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// A search's hits in the window's rail (§6.7): a channel found by its endpoint is named by the processes at its ends, who
/// connected to whom, and a hit's name wraps within the rail rather than being cut, at the smallest window too.
/// </summary>
public sealed class SearchHitWindowTests
{
    [AvaloniaFact(DisplayName = "§6.7: a channel hit names who connected to whom, its name wrapping within the rail at the smallest window rather than cut")]
    public async Task AChannelHitIsNamedWholeInTheRail()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\diagnostics-collector.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        ]);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            workspace.SearchText = "50000";
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Dispatch();

            // The channel's hit names both its processes, the maker first, and says what found it without the endpoint.
            const string Named = "diagnostics-collector.exe · PID 100 → server.exe · PID 200";
            ListBox hits = window.GetControl<ListBox>("SearchResultsList");
            int index = workspace.SearchResults.ToList().FindIndex(row => row.Hit.Kind == SearchHitKind.Channel);
            Assert.True(index >= 0);
            SearchRow row = workspace.SearchResults[index];
            Assert.Equal((Named, "TCP channel whose endpoint the search names"), (row.Label, row.Detail));
            Assert.Contains(Named, row.AccessibleName, StringComparison.Ordinal);

            // Its name wraps within the rail, every word of it shown, rather than being cut where the rail ends.
            Control container = hits.ContainerFromIndex(index)!;
            TextBlock label = container.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "SearchHitLabel");
            Assert.Equal((Named, TextWrapping.Wrap, TextTrimming.None), (label.Text, label.TextWrapping, label.TextTrimming));
            Assert.True(label.Bounds.Height > 1.5 * label.FontSize * 1.2, $"{label.Bounds.Height} is one line");
            Assert.True(label.Bounds.Width <= container.Bounds.Width, $"{label.Bounds.Width} > {container.Bounds.Width}");
        }
        finally
        {
            window.Close();
        }
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
