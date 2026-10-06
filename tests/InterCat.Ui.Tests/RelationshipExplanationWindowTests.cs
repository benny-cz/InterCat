using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// A relationship chosen in the table is explained beneath it, as its evidence's tooltip explains it to a pointer (R4,
/// §6.2): the rule that derived it and what it rests on, for a person who moves through the table by keyboard.
/// </summary>
public sealed class RelationshipExplanationWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: a relationship chosen in the table by keyboard is explained beneath it as its tooltip explains it, and the line goes when none is chosen")]
    public async Task AChosenRelationshipIsExplainedBeneathTheTable()
    {
        // Two clients that each exchange records with one server, each over a connection of its own: two relationships.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 100)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 101)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 32, 300, 102)
                .Between("127.0.0.1:50001", "127.0.0.1:8080") with { SessionRelativeTicks = 1_200 },
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 200, 103)
                .Between("127.0.0.1:8080", "127.0.0.1:50001") with { SessionRelativeTicks = 1_300 },
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            workspace.ShowTables = true;
            await workspace.TimelineDetailReady;
            Dispatch();
            _ = window.CaptureRenderedFrame();
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
            Assert.Equal(chosen.Explanation, line.Text);
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

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
