using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The paired-channel browser: each channel reads by the processes it joins, not by endpoints alone, the list says what
/// Enter does, and Enter opens the selected channel as a double click does.
/// </summary>
public sealed class ChannelBrowserWindowTests
{
    [AvaloniaFact(DisplayName = "§3.2: the channel browser names each channel's processes and opens the selected one on Enter")]
    public async Task TheBrowserNamesProcessesAndOpensOnEnter()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var browser = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null,
            id => snapshot.Processes.FirstOrDefault(process => process.Id == id)?.NameWithPid);
        Task<Channel?> chosen = browser.ShowDialog<Channel?>(owner);
        ListBox list = browser.GetVisualDescendants().OfType<ListBox>().Single();
        for (int wait = 0; wait < 250 && list.ItemCount == 0; wait++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        // The server's port sorts first, so its end is named first; the endpoints follow, compacted, then the records.
        Assert.Equal("server.exe · PID 200 ↔ client.exe · PID 100 · :8080 ↔ :50000 on 127.0.0.1 · 2 records",
            Assert.IsType<string>(Assert.Single(list.Items)));
        Assert.Equal("Paired TCP channels. Enter shows the selected channel's source records.", AutomationProperties.GetName(list));

        // Enter on the selected row opens it, as a double click does.
        Dispatch();
        Assert.True(list.ContainerFromIndex(0)!.Focus());
        browser.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Channel opened = Assert.IsType<Channel>(await chosen);
        Assert.Equal(Assert.Single(snapshot.Channels).Key, opened.Key);

        // Escape closes the browser with no channel chosen, as it cancels InterCat's prompts.
        using var again = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null, null);
        Task<Channel?> cancelled = again.ShowDialog<Channel?>(owner);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(await cancelled);
        owner.Close();
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
