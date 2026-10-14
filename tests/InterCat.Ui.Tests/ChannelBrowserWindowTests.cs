using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

        // Until its first page is read, the browser's actions wait, each saying so on itself.
        Assert.Equal("A page of channels is being read.", Offers.WhyNot(Labelled(browser, ShowChannel)));
        Assert.Equal("A page of channels is being read.", Offers.WhyNot(Labelled(browser, NextPage)));
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

        // The chosen row has the keyboard once the page is read, and Enter opens it, as a double click does.
        Dispatch();
        Assert.Same(list.ContainerFromIndex(0), browser.FocusManager?.GetFocusedElement());

        // Its channel chosen is offered to show, its label saying what that does; with none chosen, the button says to
        // choose one. The one page is the last, which Next says.
        Assert.Null(Offers.Offered(Labelled(browser, ShowChannel)));
        Assert.Equal("This page is the last: no channel in scope follows it.", Offers.WhyNot(Labelled(browser, NextPage)));
        list.SelectedIndex = -1;
        Assert.Equal("Choose a channel first.", Offers.WhyNot(Labelled(browser, ShowChannel)));
        list.SelectedIndex = 0;
        browser.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Channel opened = Assert.IsType<Channel>(await chosen);
        Assert.Equal(Assert.Single(snapshot.Channels).Key, opened.Key);

        // Beneath a list of channels the caveat ends with what the capture covered of TCP, which this one did not judge (R21).
        Assert.EndsWith(" TCP's coverage over the session is unknown: this generation publishes no coverage ledger.",
            browser.GetVisualDescendants().OfType<TextBlock>().Single(block =>
                block.Text?.StartsWith("Only paired TCP", StringComparison.Ordinal) == true).Text, StringComparison.Ordinal);

        // Escape closes the browser with no channel chosen, as it cancels InterCat's prompts.
        using var again = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null, null);
        Task<Channel?> cancelled = again.ShowDialog<Channel?>(owner);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the channel browser says what the capture covered of TCP, as the reason an empty list holds none")]
    public async Task AnEmptyBrowserSaysWhatTheCaptureCoveredOfTcp()
    {
        // A capture that collected process lifecycle alone holds no channel because it could see none.
        using var session = new TemporarySession();
        Publish(session.Store,
            [Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 }],
            coverage: new CoverageLedgerV1
            {
                Contract = CoverageLedgerV1.ContractName,
                Epochs =
                [
                    new CoverageEpochV1
                    {
                        Epoch = 1,
                        Acquisition = CoverageAcquisition.LiveCapture,
                        FirstDeliveredNativeTicks = 1,
                        LastDeliveredNativeTicks = 1,
                        Collected =
                        [
                            new CoverageCollectedV1
                            {
                                ProviderId = ProcessProvider, ProviderName = "process", EventId = 1, Version = 0,
                                Mechanism = Mechanism.ProcessLifecycle,
                            },
                        ],
                        Deliveries = [new CoverageDeliveryV1 { ProviderId = ProcessProvider, EventId = 1, Version = 0, Delivered = 1, Admitted = 1, Omitted = 0 }],
                        Losses =
                        [
                            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
                            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
                            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
                            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
                        ],
                    },
                ],
            });
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var browser = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null, null);
        Task<Channel?> closed = browser.ShowDialog<Channel?>(owner);
        TextBlock status = browser.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => AutomationProperties.GetName(block) == "Channels status");
        for (int wait = 0; wait < 250 && status.Text?.StartsWith("No admitted", StringComparison.Ordinal) != true; wait++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        // The announced status says why the list is empty, and the caveat beneath it does not say it again.
        Assert.Equal("No admitted paired TCP channel is in this scope. TCP was not collected over the session: no admitted "
            + "descriptor records it.", status.Text);
        Assert.DoesNotContain(browser.GetVisualDescendants().OfType<TextBlock>(), block =>
            block != status && block.Text?.Contains("was not collected", StringComparison.Ordinal) == true);

        // With no channel to choose, Close has the keyboard, and Show and Next each say there is none.
        Dispatch();
        Assert.Equal("Close", Assert.IsType<Button>(browser.FocusManager?.GetFocusedElement()).Content);
        Assert.Equal("No channel is in this scope.", Offers.WhyNot(Labelled(browser, ShowChannel)));
        Assert.Equal("No channel is in this scope.", Offers.WhyNot(Labelled(browser, NextPage)));
        browser.Close();
        Assert.Null(await closed);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Next, which waits disabled while it reads, gives the keyboard to the next page's first channel")]
    public async Task NextGivesTheNextPagesFirstChannelTheKeyboard()
    {
        // One channel more than a page holds, each between its own two ports.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, SessionChannelQuery.DefaultPageSize + 1).SelectMany(index => new[]
            {
                Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(1 + (2 * index)))
                    .Between($"127.0.0.1:{40_000 + index}", $"127.0.0.1:{10_000 + index}"),
                Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(2 + (2 * index)))
                    .Between($"127.0.0.1:{10_000 + index}", $"127.0.0.1:{40_000 + index}"),
            }),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var browser = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null, null);
        Task<Channel?> closed = browser.ShowDialog<Channel?>(owner);
        ListBox list = browser.GetVisualDescendants().OfType<ListBox>().Single();
        Button next = browser.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Next 100 channels"));
        for (int wait = 0; wait < 250 && !next.IsEnabled; wait++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        // Tab goes on from the chosen channel past Show to Next, and Enter there reads the next page; its one channel,
        // chosen, has the keyboard, so Up, Down and Enter act on it at once.
        Dispatch();
        Assert.Equal(SessionChannelQuery.DefaultPageSize, list.ItemCount);
        Assert.Same(list.ContainerFromIndex(0), browser.FocusManager?.GetFocusedElement());
        Assert.Null(Offers.Offered(next));
        browser.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        browser.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.True(next.IsFocused);
        List<(bool, string?)> nextSaid = Offers.Heard(next);
        List<(bool, string?)> showSaid = Offers.Heard(Labelled(browser, ShowChannel));
        browser.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        for (int wait = 0; wait < 250 && list.ItemCount != 1; wait++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        Dispatch();
        Assert.Equal(1, list.ItemCount);

        // While the next page is read, both actions wait, saying so; read, its channel is offered, and Next says this page
        // is the last.
        const string Reading = "A page of channels is being read.";
        Assert.Equal([(false, Reading), (false, "This page is the last: no channel in scope follows it.")], nextSaid);
        Assert.Equal([(false, Reading), (true, null)], showSaid);
        Assert.Same(list.ContainerFromIndex(0), browser.FocusManager?.GetFocusedElement());
        browser.Close();
        Assert.Null(await closed);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a channel page that could not be read leaves Show and Next saying why, never that no channel is in scope")]
    public async Task APageNotReadSaysWhyNothingIsOffered()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        // The folder holds another session than the one asked for, so no page of it is read as that one's.
        using var browser = new SessionChannelWindow(session.Path, Guid.NewGuid(), overview.Generation, null, null);
        Task<Channel?> closed = browser.ShowDialog<Channel?>(owner);
        TextBlock status = browser.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => AutomationProperties.GetName(block) == "Channels status");
        for (int wait = 0; wait < 250 && status.Text?.StartsWith("Could not read", StringComparison.Ordinal) != true; wait++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        Dispatch();
        Assert.Equal("Could not read this channel page: " + SessionStore.AnotherSession, status.Text);
        Assert.Equal(NoPage, Offers.WhyNot(Labelled(browser, ShowChannel)));
        Assert.Equal(NoPage, Offers.WhyNot(Labelled(browser, NextPage)));
        browser.Close();
        Assert.Null(await closed);

        // A folder holding an older generation than the window showed shifts no page, and says so the same way.
        using var older = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation + 1, null, null);
        closed = older.ShowDialog<Channel?>(owner);
        status = older.GetVisualDescendants().OfType<TextBlock>().Single(block => AutomationProperties.GetName(block) == "Channels status");
        for (int wait = 0; wait < 250 && status.Text?.StartsWith("This folder", StringComparison.Ordinal) != true; wait++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        Dispatch();
        Assert.StartsWith("This folder no longer holds the generation InterCat's window shows.", status.Text, StringComparison.Ordinal);
        Assert.Equal(NoPage, Offers.WhyNot(Labelled(older, ShowChannel)));
        Assert.Equal(NoPage, Offers.WhyNot(Labelled(older, NextPage)));
        older.Close();
        Assert.Null(await closed);
        owner.Close();
    }

    private const string NoPage = "No page of channels is shown; the status line says why.";

    private const string ShowChannel = "Show this channel's source records";

    private const string NextPage = "Next 100 channels";

    /// <summary>The browser's button labelled <paramref name="label"/>, shown or not yet.</summary>
    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
