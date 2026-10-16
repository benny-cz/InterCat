using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The relationship table as the graph's keyboard equivalent (R15): a relationship chosen in it is explained beneath it, as
/// its evidence's tooltip explains it to a pointer (R4, §6.2), and Enter or a double click on it opens it as a double click
/// on its edge does (§6.7).
/// </summary>
public sealed class RelationshipExplanationWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: a relationship chosen in the table by keyboard is explained beneath it as its tooltip explains it, and the line goes when none is chosen")]
    public async Task AChosenRelationshipIsExplainedBeneathTheTable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoClients());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            await ShowTables(window, session);
            TextBlock line = window.GetControl<TextBlock>("SelectedRelationshipText");
            Assert.False(line.IsVisible);

            // Down from the first row's keyboard focus chooses the next relationship, which the line explains whole.
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            Assert.Equal(2, table.ItemCount);
            Assert.True(table.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(1, table.SelectedIndex);
            RelationshipRow chosen = Assert.IsType<RelationshipRow>(table.SelectedItem);
            Assert.True(line.IsEffectivelyVisible);
            Assert.Equal($"{chosen.Explanation} {chosen.DirectionLine}. Enter opens its channel.", line.Text);
            Assert.Contains(". Direction: display order only, not who initiated or sent. Enter opens its channel.", line.Text,
                StringComparison.Ordinal);
            Assert.Contains("derived by transport-endpoint-relation, version 4, from 1 channel: transport:", line.Text,
                StringComparison.Ordinal);

            table.SelectedIndex = -1;
            Dispatch();
            Assert.False(line.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: the relationship table marks who connected between a relationship's ends, says it in the mark's tooltip and beneath the table, and marks none where none was seen")]
    public async Task TheTableMarksWhoConnected()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Makers());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            await ShowTables(window, session);
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            Assert.Equal(2, table.ItemCount);

            // Each row's mark is drawn between its ends, its tooltip the words.
            for (int index = 0; index < table.ItemCount; index++)
            {
                RelationshipRow row = Assert.IsType<RelationshipRow>(table.Items[index]);
                TextBlock mark = table.ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "RelationshipDirection");
                Assert.Equal(row.Direction, mark.Text);
                Assert.Equal(row.DirectionLine, ToolTip.GetTip(mark));
            }

            // The client's connect points at the server, whichever end the row names first; the other client's was not seen.
            RelationshipRow made = table.Items.OfType<RelationshipRow>().Single(row => row.Direction.Length > 0);
            Assert.Equal(made.Source.StartsWith("client", StringComparison.Ordinal) ? "→" : "←", made.Direction);
            Assert.Equal("Direction: client.exe · PID 100 connected to server.exe · PID 200", made.DirectionLine);
            Assert.Equal(string.Empty, table.Items.OfType<RelationshipRow>().Single(row => row != made).Direction);

            // Chosen, the line beneath the table says it as well.
            table.SelectedItem = made;
            Dispatch();
            Assert.Contains(". Direction: client.exe · PID 100 connected to server.exe · PID 200. Enter opens ",
                window.GetControl<TextBlock>("SelectedRelationshipText").Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.7: Enter or a double click on a relationship in the table opens it as a double click on its edge does, and its rung takes the keyboard")]
    public async Task ARelationshipOpensFromTheTable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoClients());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(window, session);
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            RelationshipRow second = Assert.IsType<RelationshipRow>(table.Items[1]);
            Assert.Equal("its channel", second.Opens);
            Assert.EndsWith(". Press Enter to open its channel.", second.AccessibleName, StringComparison.Ordinal);

            // Enter on the row with the keyboard, chosen or not, opens its channel, whose step to its records then has the
            // keyboard, so a second Enter lists them.
            Assert.True(table.ContainerFromIndex(1)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(second.Key, workspace.HighlightedEdgeKey);
            Assert.StartsWith("Channel", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
            Assert.True(window.GetControl<Button>("EmptyEvidenceButton").IsFocused);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.True(workspace.IsEvidenceRung);
            await workspace.EvidenceReady;
            Assert.StartsWith("Paired TCP channel", workspace.EvidenceScopeText, StringComparison.Ordinal);
            Assert.Equal(2, workspace.RungRows.Count);

            // Back at the machine rung, a double click on the first row opens that relationship's channel.
            workspace.ReturnTo(0);
            Dispatch();
            _ = window.CaptureRenderedFrame();
            RelationshipRow first = Assert.IsType<RelationshipRow>(table.Items[0]);
            Control row = table.ContainerFromIndex(0)!;
            Point at = row.TranslatePoint(new(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatch();
            Assert.Equal(first.Key, workspace.HighlightedEdgeKey);
            Assert.StartsWith("Channel", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.2/R15: back from a relationship's records or its channel, the table chooses it again, with the keyboard on its row")]
    public async Task TheWayBackGivesTheRelationshipsRowTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoClients());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(window, session);
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            RelationshipRow second = Assert.IsType<RelationshipRow>(table.Items[1]);

            // E on the row chosen lists its records, which take the keyboard; Esc comes back to it, its row with the keyboard.
            Assert.True(table.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            Assert.Same(second, table.SelectedItem);
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Dispatch();
            Assert.True(workspace.IsEvidenceRung);
            await workspace.EvidenceReady;
            Dispatch();
            Assert.False(table.IsKeyboardFocusWithin);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            KeyboardOn(second.Key);

            // Enter opens its channel, three rungs down, whose step to its records takes the keyboard; three Escs come back.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(4, workspace.Crumbs.Count);
            Assert.False(table.IsKeyboardFocusWithin);
            for (int climb = 0; climb < 3; climb++)
            {
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Dispatch();
            }

            Assert.Single(workspace.Crumbs);
            KeyboardOn(second.Key);
        }
        finally
        {
            window.Close();
        }

        // The machine rung's table restated, the relationship is chosen again in it, its row with the keyboard.
        void KeyboardOn(string key)
        {
            var workspace = (WorkspaceViewModel)window.DataContext!;
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            Assert.Equal(key, workspace.SelectedRelationship?.Key);
            Assert.Same(workspace.SelectedRelationship, table.SelectedItem);
            Assert.Same(table.ContainerFromItem(table.SelectedItem!), window.FocusManager?.GetFocusedElement());
        }
    }

    [AvaloniaFact(DisplayName = "R15: the table lists the graph's edges to other hosts; one chosen by keyboard chooses its source and is explained beneath, and Enter opens what a double click on its edge does")]
    public async Task AnEdgeToOtherHostsIsATableRow()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Makers(),
            Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 400 },
            Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 400, 20)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 2_000 },
            Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 64, 400, 21)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 2_100 }]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(window, session);
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            Assert.Equal(3, table.ItemCount);
            RelationshipRow edge = Assert.IsType<RelationshipRow>(table.Items[2]);
            Assert.Equal("browser.exe · PID 400 → 142.250.1.1:443", edge.Name);

            // Down from the relationships onto it chooses its process, as a click on its edge does, and the line beneath
            // the table says where it ran, who connected and what Enter opens.
            Assert.True(table.ContainerFromIndex(1)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(edge.Key, Assert.IsType<RelationshipRow>(table.SelectedItem).Key);
            Assert.Equal("browser.exe · PID 400", workspace.SelectedProcess?.NameWithPid);

            // The table's rows restated as the bytes of what is chosen are read, the edge stays chosen in it.
            await workspace.SelectionBytesReady;
            Dispatch();
            Assert.Equal(edge.Key, Assert.IsType<RelationshipRow>(table.SelectedItem).Key);
            Assert.Equal(edge.Key, workspace.SelectedTableRow?.Key);
            TextBlock line = window.GetControl<TextBlock>("SelectedRelationshipText");
            Assert.True(line.IsEffectivelyVisible);
            Assert.Equal("1 TCP connection to 1 host. Host: 142.250.1.1. Who is at it is not observed. Direction: browser.exe · "
                + "PID 400 connected to 142.250.1.1:443. Enter opens its process's rung, which lists each connection.", line.Text);

            // Enter opens its process's rung, whose rows take the keyboard.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("Process: browser.exe · PID 400", workspace.Crumbs[^1].Label);
            Assert.True(window.GetControl<ListBox>("RungList").IsKeyboardFocusWithin);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.2/R15: E on an edge to other hosts chosen in the table lets its row go at the records, and Esc back chooses it again, its row with the keyboard")]
    public async Task TheWayBackGivesTheEdgesRowTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Makers(),
            Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 400 },
            Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 400, 20)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 2_000 },
            Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 64, 400, 21)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 2_100 }]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(window, session);
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            RelationshipRow edge = Assert.IsType<RelationshipRow>(table.Items[2]);
            Assert.True(table.ContainerFromIndex(1)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            await workspace.SelectionBytesReady;
            Dispatch();
            Assert.Equal(edge.Key, workspace.SelectedTableRow?.Key);

            // E lists its process's records; the table there, listing the same edge, chooses none, though its rows were
            // restated with the edge chosen.
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Dispatch();
            Assert.True(workspace.IsEvidenceRung);
            await workspace.EvidenceReady;
            await workspace.SelectionBytesReady;
            Dispatch();
            Assert.Contains(workspace.TableRows, row => row.Key == edge.Key);
            Assert.Null(workspace.SelectedTableRow);
            Assert.Null(table.SelectedItem);

            // Esc comes back to it, chosen with its process, its row with the keyboard.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(edge.Key, workspace.SelectedTableRow?.Key);
            Assert.Equal("browser.exe · PID 400", workspace.SelectedProcess?.NameWithPid);
            Assert.Equal(edge.Key, Assert.IsType<RelationshipRow>(table.SelectedItem).Key);
            Assert.Same(table.ContainerFromItem(table.SelectedItem!), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: another rung's relationship table starts at its first row, never at the scroll of the rung left, and the way back brings the row chosen there into view")]
    public async Task AnotherRungsTableStartsAtItsFirstRow()
    {
        using var session = new TemporarySession();
        Publish(session.Store, GraphLabelLeaderTests.BusyMachine());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(window, session);
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            ScrollViewer scroller = table.GetVisualDescendants().OfType<ScrollViewer>().First();

            // The machine's thirty rows scrolled to the last; Enter on it opens its process's rung, whose eight rows, more
            // than the table shows at once, start at the first.
            Assert.True(table.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Dispatch();
            RelationshipRow last = Assert.IsType<RelationshipRow>(table.SelectedItem);
            Assert.True(scroller.Offset.Y > 0);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await workspace.LayoutReady;
            Settle();
            Assert.Equal("Process: MsMpEng.exe · PID 1600", workspace.Crumbs[^1].Label);
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
            Assert.Equal(0, scroller.Offset.Y);

            // So do its group's, a rung up; back at the machine, the row chosen there is chosen and in view again.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await workspace.LayoutReady;
            Settle();
            Assert.Equal(0, scroller.Offset.Y);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await workspace.LayoutReady;
            Settle();
            Assert.Equal(last.Key, workspace.SelectedTableRow?.Key);
            Assert.True(scroller.Offset.Y > 0);
        }
        finally
        {
            window.Close();
        }

        void Settle()
        {
            Dispatch();
            _ = window.CaptureRenderedFrame();
        }
    }

    /// <summary>Two clients that each exchange records with one server, each over a connection of its own: two relationships.</summary>
    [AvaloniaFact(DisplayName = "R15: a relationship table that lists none says why in its place, and says nothing while it lists any")]
    public async Task AnEmptyRelationshipTableSaysWhy()
    {
        // One process's connection to another host relates no two of the session's processes: the table lists it as the
        // graph draws it, an edge to other hosts, and says nothing in its place.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 900 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
                .Between("10.0.0.10:50100", "10.0.0.20:443") with { SessionRelativeTicks = 1_000 },
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(window, session);
            TextBlock why = window.GetControl<TextBlock>("RelationshipsAbsentText");
            RelationshipRow edge = Assert.IsType<RelationshipRow>(Assert.Single(window.GetControl<ListBox>("RelationshipList").Items));
            Assert.Equal(("client.exe · PID 100 ↔ 10.0.0.20:443", true), (edge.Name, edge.ToHosts));
            Assert.False(why.IsVisible);
            Assert.Equal("Why the relationship table lists none", Avalonia.Automation.AutomationProperties.GetName(why));
            Assert.Equal("Whole machine · 0 relationships · 1 edge to other hosts",
                window.GetControl<TextBlock>("RelationshipTableScopeText").Text);
            Assert.StartsWith("No relationship here. No TCP connection was seen with a process of this session at each of its "
                + "two ends.", workspace.RelationshipsAbsent, StringComparison.Ordinal);

            // The graph draws that connection to Other hosts across its pane, so it says nothing beneath it; its header
            // says why it draws no relationship.
            Assert.False(window.GetControl<TextBlock>("GraphEmptyText").IsVisible);
            Assert.NotNull(((WorkspaceViewModel)window.DataContext!).GraphDisplay.OtherHosts);
        }
        finally
        {
            window.Close();
        }

        // A connection to a port on this machine whose other end no record holds is drawn nowhere: the graph says why
        // beneath its one process, where its empty pane would otherwise read as a failure.
        using var unseen = new TemporarySession();
        Publish(unseen.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 900 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
                .Between("127.0.0.1:50100", "127.0.0.1:9443") with { SessionRelativeTicks = 1_000 },
        ]);
        var alone = new MainWindow { Width = 1456, Height = 939 };
        alone.Show();
        try
        {
            await ShowTables(alone, unseen);
            TextBlock beneath = alone.GetControl<TextBlock>("GraphEmptyText");
            Assert.True(beneath.IsEffectivelyVisible);
            Assert.Equal("No TCP connection was seen with a process of this session at each of its two ends. Its 1 TCP record "
                + "belongs to 1 connection on this machine whose other end no record holds. A process's connections are "
                + "listed on its rung.", beneath.Text);
            Assert.False(beneath.IsHitTestVisible);
        }
        finally
        {
            alone.Close();
        }

        // A table listing relationships says nothing in their place; at the rung of a process with none it says why, and
        // back at the machine rung nothing again.
        using var paired = new TemporarySession();
        Publish(paired.Store, [.. TwoClients(), Lifecycle(5, ObservationKind.Create, 500, 5) with
        {
            ResourceName = @"C:\Tools\idle.exe", SessionRelativeTicks = 1_400,
        }]);
        var listing = new MainWindow { Width = 1456, Height = 939 };
        listing.Show();
        try
        {
            WorkspaceViewModel workspace = await ShowTables(listing, paired);
            TextBlock line = listing.GetControl<TextBlock>("RelationshipsAbsentText");
            Assert.Equal(2, listing.GetControl<ListBox>("RelationshipList").ItemCount);
            Assert.False(line.IsVisible);
            Assert.False(listing.GetControl<TextBlock>("GraphEmptyText").IsVisible);
            ProcessNode idle = workspace.Snapshot.Processes.Single(node => node.ProcessId == 500);
            foreach (string key in new[] { idle.GroupKey, idle.Id.ToString() })
            {
                workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
                Assert.True(workspace.Descend());
            }

            Dispatch();
            Assert.Equal(0, listing.GetControl<ListBox>("RelationshipList").ItemCount);
            Assert.True(line.IsEffectivelyVisible);
            Assert.StartsWith("No relationship here: none has an end among the processes this rung draws.", line.Text, StringComparison.Ordinal);
            workspace.ReturnTo(0);
            Dispatch();
            Assert.False(line.IsVisible);
        }
        finally
        {
            listing.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R7/R15: a newer generation published while recording keeps the relationship chosen in the table, with the keyboard on its row")]
    public async Task ANewerGenerationKeepsTheRelationshipChosen()
    {
        using var session = new TemporarySession();
        Publish(session.Store, TwoClients());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            workspace.ShowTables = true;
            await workspace.TimelineDetailReady;
            Dispatch();
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            Assert.True(table.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatch();
            string chosen = Assert.IsType<RelationshipRow>(table.SelectedItem).Key;

            // The next generation replaces the workspace: the table keeps the relationship chosen, and the keyboard its row.
            Publish(session.Store, [Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 16, 100, 200)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 2_000 }]);
            window.ApplyCaptureUpdate(Recording(session));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.NotSame(workspace, next);
            Dispatch();
            Assert.Equal(chosen, next.SelectedRelationship?.Key);
            Assert.Equal(chosen, Assert.IsType<RelationshipRow>(table.SelectedItem).Key);
            Assert.Same(table.ContainerFromItem(table.SelectedItem!), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A recording's publication of the session's newest generation.</summary>
    private static CaptureUiUpdate Recording(TemporarySession session) => new(CaptureUiPhase.Recording, "Recording",
        "A published generation.", SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    private static ObservationRowV1[] TwoClients() =>
    [
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
        Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 101)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 32, 300, 102)
            .Between("127.0.0.1:50001", "127.0.0.1:8080") with { SessionRelativeTicks = 1_200 },
        Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 200, 103)
            .Between("127.0.0.1:8080", "127.0.0.1:50001") with { SessionRelativeTicks = 1_300 },
    ];

    /// <summary>
    /// client.exe connects to server.exe's 8080, the connect seen at its end, and other.exe's connection to it is seen
    /// opening at neither end.
    /// </summary>
    private static ObservationRowV1[] Makers() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\other.exe", SessionRelativeTicks = 300 },
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 11)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_100 },
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 12)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_200 },
        Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 32, 300, 13)
            .Between("127.0.0.1:50001", "127.0.0.1:8080") with { SessionRelativeTicks = 1_300 },
        Transfer(14, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 200, 14)
            .Between("127.0.0.1:8080", "127.0.0.1:50001") with { SessionRelativeTicks = 1_400 },
    ];

    /// <summary>Opens the session in the window with its tables shown, once its timeline has been read and drawn.</summary>
    private static async Task<WorkspaceViewModel> ShowTables(MainWindow window, TemporarySession session)
    {
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.ShowTables = true;
        await workspace.TimelineDetailReady;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        return workspace;
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
