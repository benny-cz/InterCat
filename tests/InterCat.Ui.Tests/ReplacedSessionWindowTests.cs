using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
/// A session's folder written again with another session, or emptied, while the window shows it (§20.1): every read says
/// so in one sentence that says what to do, and nothing of another session is read, pinned, packaged or counted as the
/// session shown.
/// </summary>
public sealed class ReplacedSessionWindowTests
{
    private static readonly Guid Scm = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    /// <summary>The sentence as a caption carries it, inside a line that goes on.</summary>
    private static readonly string Another = SessionStore.AnotherSession.TrimEnd('.');

    [AvaloniaFact(DisplayName = "§20.1: a session replaced under the open window says so in one sentence wherever it is read")]
    public async Task EveryReadSaysTheFolderHoldsAnotherSession()
    {
        using var session = new TemporarySession();
        (MainWindow window, WorkspaceViewModel workspace) = await Shown(session);
        try
        {
            Replace(session);

            // A zoom draws the overview's columns instead of its own count, and the caption says why.
            workspace.RequestTimelineDetail(new TimeRange(100, 200), 64);
            await workspace.TimelineDetailReady;
            Assert.Contains($" · this view's own count could not be read, so the overview's columns are drawn: {Another} · ",
                workspace.TimelineCaption, StringComparison.Ordinal);

            // A row chosen is not highlighted, for the reason the caption has just given, which it does not repeat.
            ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.GroupKey);
            await workspace.HighlightReady;
            Assert.EndsWith(" · the selection, caller.exe, could not be counted", workspace.TimelineCaption,
                StringComparison.Ordinal);

            // A range brushed is not ranked, E lists no record, and a ranking by bytes reads none.
            workspace.SelectInterval(new TimeRange(100, 250));
            await workspace.IntervalReady;
            Assert.EndsWith(": " + SessionStore.AnotherSession, workspace.RankingScopeText, StringComparison.Ordinal);
            window.GetControl<ListBox>("RungList").Focus();
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            await workspace.EvidenceReady;
            Assert.Equal(SessionStore.AnotherSession, workspace.EmptyReason);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            workspace.ClearSelection();
            await workspace.IntervalReady;
            workspace.RankBy = RankingMetric.BytesSent;
            await workspace.RankingReady;
            Assert.Equal("Bytes sent unavailable: " + SessionStore.AnotherSession, workspace.RankingNote);
            workspace.RankBy = RankingMetric.Records;

            // A group's lanes and a process's calls and connections are not counted, and say so the same way.
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.GroupKey);
            Assert.True(workspace.Descend());
            Settle(window);
            window.GetControl<TimelineView>("TimelineSurface").RequestDetailNow();
            await workspace.TimelineDetailReady;
            Assert.StartsWith($"Records owned by the 2 instances of caller.exe could not be counted: {Another}. Every observed "
                + "record is shown.", workspace.TimelineCaption, StringComparison.Ordinal);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.Id.ToString());
            Assert.True(workspace.Descend());
            await workspace.RpcReady;
            await workspace.HttpReady;
            await workspace.ConnectionsReady;
            Assert.Equal($"This process's RPC calls and connections could not be read: {Another}. No admitted paired TCP "
                + "or UDP channel belongs to this process in this generation.", workspace.EmptyReason);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§20.1: nothing of another session written into the open session's folder is pinned, packaged, counted or listed as it")]
    public async Task NothingOfTheOtherSessionIsTakenForTheOneShown()
    {
        using var session = new TemporarySession();
        (MainWindow window, WorkspaceViewModel workspace) = await Shown(session);
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        try
        {
            Replace(session);
            string[] written = Listing(session.Path);

            // Keep records offers nothing, and keeps no record of the other session.
            Assert.Null(await window.PinOfferAsync());
            Assert.Equal(("This session's pins could not be read", SessionStore.AnotherSession), Card(window));
            Assert.Null(await window.PlacePinAsync(10_000, 1L << 30, "kept"));
            Assert.Equal(("No records were pinned", SessionStore.AnotherSession), Card(window));

            // Share original session is refused before it states anything or asks where to save.
            window.GetControl<Button>("ShareOriginalButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Card(window).Detail == "Could not read the session to package it: " + SessionStore.AnotherSession);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(0, choices.Asked);

            // Its copy, made as Save would make it, is refused too, and makes no folder.
            string copy = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Replaced", "copy-" + Guid.NewGuid().ToString("N"));
            Assert.Null(await window.WriteOriginalPackageAsync(session.Path, session.Store.SessionId, copy));
            Assert.Equal("Could not save the original session: " + SessionStore.AnotherSession, Card(window).Detail);
            Assert.False(Directory.Exists(copy));

            // Counting candidates answers the button that asked, and what is shown stays counted as it was.
            Assert.False(await window.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
            Assert.Equal(("The records could not be counted again", SessionStore.AnotherSession), Card(window));
            Assert.Equal(EvidencePolicy.IncludeCorrelated, Assert.IsType<WorkspaceViewModel>(window.DataContext).EvidencePolicy);

            // The channel browser lists none of the other session's channels as this one's.
            using (var browser = new SessionChannelWindow(session.Path, session.Store.SessionId, workspace.DisplayedGeneration!.Value,
                null))
            {
                browser.Show();
                TextBlock status = browser.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => AutomationProperties.GetName(text) == "Channels status");
                await Until(() => status.Text == "Could not read this channel page: " + SessionStore.AnotherSession);
                browser.Close();
            }

            // The folder holds what was written into it, and nothing more.
            Assert.Equal(written, Listing(session.Path));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§20.1: a session's folder emptied under the open window says the session is gone, never that none was published")]
    public async Task AnEmptiedFolderSaysTheSessionIsGone()
    {
        using var session = new TemporarySession();
        (MainWindow window, WorkspaceViewModel workspace) = await Shown(session);
        try
        {
            Release(session);
            foreach (string file in Directory.EnumerateFiles(session.Path))
            {
                File.Delete(file);
            }

            // Read through the store the window holds and through one opened afresh, the session is gone.
            window.GetControl<ListBox>("RungList").Focus();
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            await workspace.EvidenceReady;
            Assert.Equal(SessionStore.SessionGone, workspace.EmptyReason);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            workspace.RequestTimelineDetail(new TimeRange(100, 200), 64);
            await workspace.TimelineDetailReady;
            Assert.Contains(SessionStore.SessionGone.TrimEnd('.'), workspace.TimelineCaption, StringComparison.Ordinal);
            Assert.Null(await window.PinOfferAsync());
            Assert.Equal(("This session's pins could not be read", SessionStore.SessionGone), Card(window));
            Assert.False(await window.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
            Assert.Equal(("The records could not be counted again", SessionStore.SessionGone), Card(window));

            // Nothing was written into the folder by reading it.
            Assert.Empty(Directory.EnumerateFileSystemEntries(session.Path));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The caller, two instances of caller.exe, calls the service control manager twice and sends once to a peer no
    /// other process holds; services.exe serves nothing it records.
    /// </summary>
    private static ObservationRowV1[] Calls() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\caller.exe" }),
        Timed(Lifecycle(2, ObservationKind.Inventory, 1_960, 2) with { ResourceName = @"C:\Windows\System32\services.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 402, 3) with { ResourceName = @"C:\Tools\caller.exe" }),
        Timed(RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), Scm)),
        Timed(RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0)),
        Timed(RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 20, Activity(2), Scm)),
        Timed(RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 21, Activity(2), status: 0)),
        Timed(Transfer(150, ObservationKind.Send, AccountingSide.SendSide, 32, 400, 30).Between("127.0.0.1:51000", "10.0.0.7:443")),
    ];

    private static async Task<(MainWindow Window, WorkspaceViewModel Workspace)> Shown(TemporarySession session)
    {
        Publish(session.Store, Calls());
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        await window.SessionFilesChecked;
        Settle(window);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        return (window, workspace);
    }

    /// <summary>Writes another session over the shown one's folder, as a package made again at the same name is.</summary>
    private static void Replace(TemporarySession session)
    {
        string other = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Replaced", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            SessionStore written = SessionStore.Open(LocalOwnedDirectory.Open(other), Guid.NewGuid(), "replacing-tests");
            Publish(written,
            [
                Timed(Lifecycle(1, ObservationKind.Inventory, 700, 1) with { ResourceName = @"C:\Tools\other.exe" }),
                .. Enumerable.Range(0, 20).Select(index => Timed(Transfer(20 + index, ObservationKind.Send,
                    AccountingSide.SendSide, 32, 700, (ulong)(100 + index)).Between("127.0.0.1:52000", "10.0.0.9:443"))),
            ]);
            written.ReleaseSegmentReaders();
            Release(session);
            foreach (string file in Directory.EnumerateFiles(session.Path))
            {
                File.Delete(file);
            }

            foreach (string file in Directory.EnumerateFiles(other))
            {
                File.Copy(file, Path.Combine(session.Path, Path.GetFileName(file)));
            }
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>Lets go of the files the session's stores hold open, as a folder written again needs on Windows.</summary>
    private static void Release(TemporarySession session)
    {
        session.Store.ReleaseSegmentReaders();
        SharedSessionStores.Open(session.Path).ReleaseSegmentReaders();
    }

    private static string[] Listing(string path) =>
        [.. Directory.EnumerateFiles(path).Select(file => $"{Path.GetFileName(file)} {new FileInfo(file).Length}")
            .Order(StringComparer.Ordinal)];

    private static (string? Status, string? Detail) Card(MainWindow window) =>
        (window.GetControl<TextBlock>("CaptureStatus").Text, window.GetControl<TextBlock>("CaptureDetail").Text);

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
