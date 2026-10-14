using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterCat.Capture.Journal;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §3.1 step 6 in the window: a viewer that crashed while recording loses no evidence, and the next launch offers to
/// finish its session from the evidence the broker kept.
/// </summary>
public sealed class InterruptedCaptureWindowTests
{
    [AvaloniaFact(DisplayName = "§3.1: the next launch offers to finish a crashed viewer's session, and finishing opens it whole")]
    public async Task TheNextLaunchFinishesACrashedSession()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, string evidence) = await CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Assert.False(card.IsVisible);

            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.True(card.IsVisible);
            Assert.EndsWith("is not finished saving", window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text,
                StringComparison.Ordinal);
            Assert.Contains("not in your session yet", window.GetControl<TextBlock>("UnfinishedCaptureDetail").Text,
                StringComparison.Ordinal);
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.True(finish.IsVisible);
            Assert.Equal("Finish saving", finish.Content);

            // The offer does not take first run's focus, and at the minimum window it lies whole inside the rail, above
            // the Explore card whose Start action stays in view.
            Assert.True(window.GetControl<Button>("StartExploringButton").IsFocused);
            Control rail = window.GetControl<Control>("Rail");
            AssertInside(card, rail, window);
            AssertInside(window.GetControl<Button>("StartExploringButton"), rail, window);
            AssertInside(window.GetControl<Button>("OpenSavedSessionButton"), rail, window);
            Save(window, "unfinished-capture-1080x700.png");

            await window.ActOnInterruptedCaptureAsync();
            Dispatch();

            Assert.False(card.IsVisible);
            Assert.Equal("Session saved", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Equal(session, window.GetControl<TextBlock>("CaptureSessionPath").Text);
            Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
            Assert.Equal(4, JournalRederivation.Verify(SessionStore.OpenExisting(LocalOwnedDirectory.Open(session)))
                .ObservationRows);
            Assert.True(Directory.Exists(evidence));
            Save(window, "unfinished-capture-finished-1080x700.png");

            // Nothing is left to offer at the launch after.
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.False(card.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: with no session shown the saved sessions stand where the ranked table will be, and the rail's actions scroll in the window")]
    public async Task TheEmptyRailShowsSavedSessionsAndKeepsItsActionsInTheWindow()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            Control rail = window.GetControl<Control>("Rail");

            // Nothing is shown, so nothing is ranked or searched: the saved sessions stand where the ranked table will be,
            // and a row of them is in view beside the unfinished capture's card and the Explore card.
            Assert.False(window.GetControl<Control>("RankedTableHeader").IsVisible);
            Assert.False(workspace.ShowsEmptyReason);
            ListBox saved = window.GetControl<ListBox>("RecentSessionsList");
            Assert.True(window.GetControl<Control>("RecentSessionsPanel").IsVisible);
            Control first = Assert.IsAssignableFrom<Control>(saved.ContainerFromIndex(0));
            Assert.True(first.Bounds.Height > 20, $"The first saved session is {first.Bounds.Height:F0} px tall.");
            AssertInside(first, rail, window);

            // The actions below scroll within the rail rather than run past the window, and the last is a scroll away.
            ScrollViewer actions = window.GetControl<ScrollViewer>("RailActions");
            AssertInside(actions, rail, window);
            Assert.True(window.GetControl<Border>("UnfinishedCaptureCard").IsVisible);
            AssertInside(window.GetControl<Button>("StartExploringButton"), rail, window);
            actions.Offset = new Vector(0, Math.Max(0, actions.Extent.Height - actions.Viewport.Height));
            Dispatch();
            _ = window.CaptureRenderedFrame();
            AssertInside(window.GetControl<Button>("InvestigationButton"), actions, window);

            // A capture on its way to its first view says so where the ranked rows will come, under its own heading.
            Assert.Equal("NOTHING RECORDED YET", workspace.EmptyHeading);
            workspace.SetAwaitingCapture("Starting a capture", "The capture is starting; its first view comes within seconds.");
            Assert.True(workspace.ShowsEmptyReason);
            workspace.SetAwaitingCapture(null, null);
            Assert.False(workspace.ShowsEmptyReason);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a session's ranked rows keep their room beside an unfinished capture's card, whose actions scroll")]
    public async Task ASessionsRowsKeepTheirRoomBesideTheCards()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow(new WorkspaceViewModel()) { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Dispatch();
            Control rail = window.GetControl<Control>("Rail");

            // The ranked table, its rows and both cards share the rail: the rows keep a row or two in view, and the cards,
            // which would take the rest and more, scroll within what is left.
            Assert.True(window.GetControl<Control>("RankedTableHeader").IsVisible);
            Assert.True(window.GetControl<Border>("UnfinishedCaptureCard").IsVisible);
            ListBox rows = window.GetControl<ListBox>("RungList");
            Assert.True(rows.Bounds.Height >= MainWindow.RailListRoom - 1, $"The ranked rows have {rows.Bounds.Height:F0} px.");
            ScrollViewer actions = window.GetControl<ScrollViewer>("RailActions");
            AssertInside(actions, rail, window);
            Assert.True(actions.Extent.Height > actions.Viewport.Height, "The cards fit, so nothing here needed to scroll.");
            actions.Offset = new Vector(0, actions.Extent.Height - actions.Viewport.Height);
            Dispatch();
            _ = window.CaptureRenderedFrame();
            AssertInside(window.GetControl<Button>("InvestigationButton"), actions, window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a session's rail keeps its ranked table, and its actions leave the rows room")]
    public void ASessionsRailKeepsItsRankedTable()
    {
        var workspace = new WorkspaceViewModel();
        Assert.True(workspace.ShowsRankedHeader);
        Assert.Equal("NOTHING AT THIS LEVEL", workspace.EmptyHeading);
        Assert.False(new WorkspaceViewModel(InterCat.Application.OverviewWorkspace.Empty(), "empty-workspace").ShowsRankedHeader);

        // The actions take what the rail leaves below its header, less a row or two for the list, and never too little.
        Assert.Equal(666 - 180 - MainWindow.RailListRoom, MainWindow.RailActionsHeight(666, 180));
        Assert.Equal(MainWindow.RailActionsFloor, MainWindow.RailActionsHeight(400, 300));
    }

    [AvaloniaFact(DisplayName = "§3.1: a capture still stopping is waited for, and one forgotten is not offered again")]
    public async Task ACaptureStillStoppingIsWaitedForAndCanBeForgotten()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, string evidence) = await CrashedFollow(root.Path, unfinalized: true);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Assert.True(card.IsVisible);
            Assert.EndsWith("is still stopping", window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text,
                StringComparison.Ordinal);
            Assert.False(window.GetControl<Button>("FinishCaptureButton").IsVisible);
            Button forget = window.GetControl<Button>("ForgetCaptureButton");
            Assert.Equal("Forget", forget.Content);

            // While a capture records, the card waits with the other actions that wait for it to end.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            Dispatch();
            Assert.False(card.IsVisible);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Explore did not start", "Declined."));
            Dispatch();
            Assert.True(card.IsVisible);

            await window.ForgetInterruptedCaptureAsync();
            Dispatch();
            Assert.False(card.IsVisible);
            Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
            Assert.True(Directory.Exists(session));
            Assert.True(Directory.Exists(evidence));
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.False(card.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: an unfinished capture forgotten by keyboard gives Start exploring the keyboard as its card goes")]
    public async Task ACaptureForgottenGivesStartTheKeyboard()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await CrashedFollow(root.Path, unfinalized: true);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Button forget = window.GetControl<Button>("ForgetCaptureButton");
            Assert.True(forget.Focus(Avalonia.Input.NavigationMethod.Tab));
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            for (int wait = 0; wait < 250 && card.IsVisible; wait++)
            {
                Dispatch();
                await Task.Delay(10);
            }

            Dispatch();
            Assert.False(card.IsVisible);
            Assert.True(window.GetControl<Button>("StartExploringButton").IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the card restated after its button waited disabled, as while a finish stops, gives that button the keyboard back")]
    public async Task TheCardRestatedGivesItsButtonTheKeyboardBack()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.True(finish.Focus(Avalonia.Input.NavigationMethod.Tab));

            // The button waits disabled, as it does while a finish it started stops, and the keyboard goes with it; the card
            // restated once the finish has stopped enables it again, with the keyboard.
            finish.IsEnabled = false;
            Dispatch();
            Assert.False(finish.IsFocused);
            await window.RefreshInterruptedOfferAsync();
            Dispatch();
            Assert.True(finish.IsEnabled);
            Assert.True(finish.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a recheck that looked before the user forgot a capture does not bring its card back")]
    public async Task AStaleRecheckDoesNotBringAForgottenCardBack()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, _) = await CrashedFollow(root.Path, unfinalized: true);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        using var release = new ManualResetEventSlim();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Assert.True(card.IsVisible);

            // The card's recheck looks at the folder and reads the ticket, and its answer is held back until the user
            // has forgotten the capture: under load, a look can end after a later one that it began before.
            var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<string, InterruptedFollow?> find = window.InterruptedCaptureFinder;
            int looks = 0;
            window.InterruptedCaptureFinder = folder =>
            {
                InterruptedFollow? found = find(folder);
                if (Interlocked.Increment(ref looks) == 1)
                {
                    looked.SetResult();
                    release.Wait();
                }

                return found;
            };
            Task recheck = window.RefreshInterruptedOfferAsync();
            await looked.Task;

            await window.ForgetInterruptedCaptureAsync();
            Dispatch();
            Assert.False(card.IsVisible);

            release.Set();
            await recheck;
            Dispatch();
            Assert.False(card.IsVisible);
            Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
            Assert.Equal(2, looks);
        }
        finally
        {
            release.Set();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: finishing a capture whose evidence went since its card offered it restates the card as one that can no longer be finished, and what was saved opens")]
    public async Task FinishingACaptureWhoseEvidenceWentRestatesItsCard()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, string evidence) =
            await CrashedFollow(root.Path, unfinalized: false, settled: true);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.Equal("Finish saving", finish.Content);
            Assert.True(finish.Focus(Avalonia.Input.NavigationMethod.Tab));

            // The card says what is there now, never a folder that is not, and offers what can still be done, with the
            // keyboard on it.
            Directory.Delete(evidence, recursive: true);
            await window.ActOnInterruptedCaptureAsync();
            Dispatch();
            Assert.True(card.IsVisible);
            Assert.EndsWith("can no longer be finished", window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text,
                StringComparison.Ordinal);
            Assert.Equal("Its evidence can no longer be read. Your session keeps the 1 published chunk followed before "
                + "InterCat closed.", window.GetControl<TextBlock>("UnfinishedCaptureDetail").Text);
            Assert.Equal("Open what was saved", finish.Content);
            Assert.True(finish.IsFocused);
            Assert.Equal("Ready to explore", window.GetControl<TextBlock>("CaptureStatus").Text);

            await window.ActOnInterruptedCaptureAsync();
            Dispatch();
            Assert.False(card.IsVisible);
            Assert.Equal("Partial session open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Equal(session, window.GetControl<TextBlock>("CaptureSessionPath").Text);
            Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: finishing a capture finished or forgotten elsewhere since its card offered it says so where the status is read, never on another capture's card, and its card goes")]
    public async Task FinishingACaptureTakenElsewhereSaysSo()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string older, _) =
            await CrashedFollow(root.Path, unfinalized: false, startedAgo: TimeSpan.FromMinutes(30));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            TextBlock headline = window.GetControl<TextBlock>("UnfinishedCaptureHeadline");
            TextBlock cardDetail = window.GetControl<TextBlock>("UnfinishedCaptureDetail");
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            (string olderHeadline, string olderDetail) = (headline.Text!, cardDetail.Text!);

            // A newer capture is offered first. Forgotten in another window, nothing is left here to finish it, never
            // another window finishing it, and the card offers the older capture in its own words alone.
            (_, string newer, _) = await CrashedFollow(root.Path, unfinalized: false, capture: 2);
            await window.RefreshInterruptedOfferAsync();
            Dispatch();
            Assert.NotEqual(olderHeadline, headline.Text);
            Assert.True(LiveFollowTicket.Remove(newer));
            await window.ActOnInterruptedCaptureAsync();
            Dispatch();
            Assert.Equal("Could not finish this capture", status.Text);
            Assert.Equal(InterruptedFollow.NoLongerWaiting, detail.Text);
            Assert.True(card.IsVisible);
            Assert.Equal((olderHeadline, olderDetail), (headline.Text, cardDetail.Text));

            // The last one offered goes too, and the keyboard that pressed Finish goes where the window gives it.
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.True(finish.Focus(Avalonia.Input.NavigationMethod.Tab));
            Assert.True(LiveFollowTicket.Remove(older));
            await window.ActOnInterruptedCaptureAsync();
            for (int wait = 0; wait < 20; wait++)
            {
                Dispatch();
                await Task.Delay(10);
            }

            Assert.False(card.IsVisible);
            Assert.Equal(InterruptedFollow.NoLongerWaiting, detail.Text);
            Assert.True(window.GetControl<Button>("StartExploringButton").IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: an unfinished capture's card is read again as the window comes back into use, so one finished or forgotten elsewhere meanwhile goes")]
    public async Task TheCardIsReadAgainAsTheWindowComesBack()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, _) = await CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1080, Height = 700 };
        int looks = 0;
        Func<string, InterruptedFollow?> find = window.InterruptedCaptureFinder;
        window.InterruptedCaptureFinder = folder =>
        {
            _ = Interlocked.Increment(ref looks);
            return find(folder);
        };

        // The folder is taken before the window is shown, as InterCat takes it: its opening is no coming back.
        await window.UseSessionRootAsync(sessions);
        window.Show();
        try
        {
            Dispatch();
            await window.FolderRead;
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Assert.True(card.IsVisible);
            Assert.Equal(1, looks);
            Assert.True(LiveFollowTicket.Remove(session));

            // Back in use, as from another window.
            window.Activate();
            Dispatch();
            await window.FolderRead;
            Dispatch();
            Assert.False(card.IsVisible);
            Assert.Equal(2, looks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a finish that did not go, its card offering the capture as it did, says why on the card and offers it again")]
    public async Task AFinishThatDidNotGoSaysWhyOnItsCard()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await CrashedFollow(root.Path, unfinalized: false);

        // Its ticket names another capture's evidence, which cannot be spliced into this session.
        string other = Path.Combine(root.Path, "broker", "capture-other");
        Directory.CreateDirectory(other);
        _ = await EvidenceRecordings.RecordEvidence(other, ordinals: [1, 2, 3, 4]);
        LiveFollowTicket ticket = Assert.Single(LiveFollowTicket.FindInterrupted(sessions));
        (ticket with { EvidenceDirectory = other }).Hold().Dispose();
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.Equal("Finish saving", finish.Content);
            string headline = window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text!;

            await window.ActOnInterruptedCaptureAsync();
            Dispatch();
            TextBlock cardDetail = window.GetControl<TextBlock>("UnfinishedCaptureDetail");
            Assert.True(window.GetControl<Border>("UnfinishedCaptureCard").IsVisible);
            Assert.Equal(headline, window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text);
            Assert.StartsWith("This directory holds session ", cardDetail.Text, StringComparison.Ordinal);
            Assert.EndsWith(" Your session keeps what was derived before this.", cardDetail.Text,
                StringComparison.Ordinal);
            Assert.Equal("Finish saving", finish.Content);
            Assert.Equal("Ready to explore", window.GetControl<TextBlock>("CaptureStatus").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: opening what a capture saved, deleted, emptied, written over or torn since its card offered it, says why in the card's words or the open's, and the card goes")]
    public async Task OpeningWhatWasSavedOnceChangedSaysWhy()
    {
        foreach (string change in new[] { "deleted", "emptied", "written over", "torn" })
        {
            using var root = new TemporaryDirectory();
            (string sessions, string session, string evidence) =
                await CrashedFollow(root.Path, unfinalized: false, settled: true);
            Directory.Delete(evidence, recursive: true);
            var window = new MainWindow { Width = 1080, Height = 700 };
            window.Show();
            try
            {
                await window.UseSessionRootAsync(sessions);
                Dispatch();
                Assert.Equal("Open what was saved", window.GetControl<Button>("FinishCaptureButton").Content);
                string from = window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text!
                    .Replace("The capture from ", string.Empty, StringComparison.Ordinal)
                    .Replace(" can no longer be finished", string.Empty, StringComparison.Ordinal);
                if (change == "deleted")
                {
                    Directory.Delete(session, recursive: true);
                }
                else if (change == "torn")
                {
                    File.WriteAllText(Path.Combine(session, SessionPointerV1.FileName), "{");
                }
                else
                {
                    foreach (string file in Directory.EnumerateFiles(session))
                    {
                        File.Delete(file);
                    }

                    if (change == "written over")
                    {
                        SessionStore another =
                            SessionStore.Open(LocalOwnedDirectory.Open(session), Guid.NewGuid(), "ui-tests");
                        _ = Publish(another, [
                            Lifecycle(100, ObservationKind.Create, 400, 1) with { SessionRelativeTicks = 100 }]);
                    }
                }

                // Refused as the session the card described: never as a folder that does not exist, one no session was
                // ever published in, or another session; a session that cannot be read says why as any open does.
                await window.ActOnInterruptedCaptureAsync();
                Dispatch();
                TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
                Assert.Equal("Could not open this session", window.GetControl<TextBlock>("CaptureStatus").Text);
                string gone = $"The session of the capture from {from} is no longer in its folder: its files were "
                    + "moved or deleted";
                if (change == "torn")
                {
                    Assert.StartsWith(
                        "This session cannot be read, because no generation of it verifies: ", detail.Text,
                        StringComparison.Ordinal);
                }
                else
                {
                    Assert.Equal(gone + (change == "written over" ? "; another session is there now." : ".")
                        + " What the window shows is unchanged.", detail.Text);
                }

                Assert.True(Assert.IsType<WorkspaceViewModel>(window.DataContext).IsEmptyWorkspace);

                // Read again, nothing of the capture is left to offer: its card goes, and its ticket with it. A folder
                // written over holds a session the card cannot tell from the capture's own.
                if (change != "written over")
                {
                    Assert.False(window.GetControl<Border>("UnfinishedCaptureCard").IsVisible);
                    Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// A capture's evidence, and a session whose viewer followed its first chunk and crashed, leaving its ticket. An
    /// unfinalized capture is the evidence as its first publication left it. Its owner lease has not settled yet, or
    /// lapsed long enough ago that the broker can no longer be recording it.
    /// </summary>
    internal static async Task<(string Sessions, string Session, string Evidence)> CrashedFollow(
        string root, bool unfinalized, bool settled = false, int capture = 1, TimeSpan? startedAgo = null)
    {
        string evidencePath = Path.Combine(root, "broker", $"capture-{capture}");
        Directory.CreateDirectory(evidencePath);
        _ = await EvidenceRecordings.RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4]);
        if (unfinalized)
        {
            _ = EvidenceRecordings.RewindToUnfinalized(evidencePath);
        }

        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        string sessions = Path.Combine(root, "Sessions");
        string session = Path.Combine(sessions, $"explore-20260926T153534Z-{capture}");
        Directory.CreateDirectory(session);
        SessionStore followed = SessionStore.Open(
            LocalOwnedDirectory.Open(session), evidence.Current!.SessionId, evidence.Current.SourceIdentity);
        if (!unfinalized)
        {
            _ = LiveSessionFollower.Open(evidence, followed).CatchUp(maximumChunks: 1);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LiveFollowTicket.For(evidence.Current.SessionId, evidencePath, session,
                now - (startedAgo ?? TimeSpan.FromMinutes(settled ? 10 : 1)),
                settled ? now.AddMinutes(-5) : now.AddSeconds(20))
            .Hold().Dispose();
        return (sessions, session, evidencePath);
    }

    private static void AssertInside(Control inner, Control outer, Window window)
    {
        Avalonia.Rect bounds = WindowBounds(inner, window);
        Avalonia.Rect container = WindowBounds(outer, window);
        Assert.True(
            container.Contains(bounds.TopLeft) && container.Contains(bounds.BottomRight - new Avalonia.Vector(0.5, 0.5)),
            $"{inner.Name} at {bounds} is not inside {outer.Name} at {container}.");
    }

    private static Avalonia.Rect WindowBounds(Control control, Window window) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
