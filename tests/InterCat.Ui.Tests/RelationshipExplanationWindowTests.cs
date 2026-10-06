using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            Assert.Equal($"{chosen.Explanation} Enter opens its channel.", line.Text);
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

    /// <summary>Two clients that each exchange records with one server, each over a connection of its own: two relationships.</summary>
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
