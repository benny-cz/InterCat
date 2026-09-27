using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The ranked table's Rank by selector in a real window (§6.1): chosen through the control, it re-ranks the rows the rail
/// draws once the bytes are read, states what it measured beneath it, and a live publication keeps the choice.
/// </summary>
public sealed class RankingSelectorWindowTests
{
    [AvaloniaFact(DisplayName = "§6.1: Rank by ranks the rail's rows by bytes, says what it measured, and survives a publication")]
    public async Task RankByRanksTheRailByBytes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;

        ComboBox selector = window.GetControl<ComboBox>("RankBySelector");
        TextBlock note = window.GetControl<TextBlock>("RankingNote");
        ListBox rail = window.GetControl<ListBox>("RungList");
        Assert.True(selector.IsEffectivelyVisible);
        Assert.Equal("Rank groups and processes by", AutomationProperties.GetName(selector));
        Assert.Equal("Records", selector.SelectedItem?.ToString());
        Assert.False(note.IsEffectivelyVisible);
        Assert.Equal("listen.exe", FirstLabel(rail));

        // A person picks Bytes sent: the rail keeps its order until the bytes are read, then ranks by them.
        selector.SelectedIndex = 1;
        Dispatch();
        Assert.Equal(RankingMetric.BytesSent, workspace.RankBy);
        Assert.True(note.IsEffectivelyVisible);
        await workspace.RankingReady;
        Dispatch();
        Assert.Equal("big.exe", FirstLabel(rail));
        Assert.Contains(WorkspaceRowBuilder.DescribeSize(1_750), Texts(rail));
        Assert.Equal(WorkspaceRowBuilder.DescribeSize(1_750) + " on 4 sends · 2 unmeasured", note.Text);
        Assert.Equal(workspace.RankingNoteDetail, AutomationProperties.GetHelpText(note));
        Save(window.CaptureRenderedFrame()!, "l0-rank-by-bytes-sent-1080x700.png");

        // The rail scrolls to the rest: the unmeasured row and the one that sent nothing say so rather than show a zero.
        rail.ScrollIntoView(workspace.RungRows.Count - 1);
        Dispatch();
        Assert.Contains("no sends", Texts(rail));
        rail.ScrollIntoView(2);
        Dispatch();
        Assert.Contains("unmeasured", Texts(rail));
        rail.ScrollIntoView(0);
        Dispatch();

        // The next publication keeps the choice: the last one's bytes rank the rail, marked, until its own are read.
        Publish(session.Store, [Timed(Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 5_000, 200, 30))]);
        window.ApplyCaptureUpdate(Update(session));
        var second = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(workspace, second);
        Assert.Equal("Bytes sent", selector.SelectedItem?.ToString());
        Assert.Equal(("big.exe", RankingMetric.BytesSent), (second.RungRows[0].Label, second.AppliedRanking));
        Assert.EndsWith(" · updating", note.Text, StringComparison.Ordinal);
        await second.RankingReady;
        Dispatch();
        Assert.Equal("zero.exe", FirstLabel(rail));
        Assert.DoesNotContain("updating", note.Text, StringComparison.Ordinal);

        // A process's rung lists channels, which the selector does not rank: it steps aside there and returns above.
        second.SelectedRung = second.RungRows[0];
        Assert.True(second.Descend());
        second.SelectedRung = second.RungRows[0];
        Assert.True(second.Descend());
        Dispatch();
        Assert.False(selector.IsEffectivelyVisible);
        Assert.False(note.IsEffectivelyVisible);
        Assert.True(second.Ascend());
        Dispatch();
        Assert.True(selector.IsEffectivelyVisible);
        window.Close();
    }

    private static string? FirstLabel(ListBox rail) =>
        rail.GetVisualDescendants().OfType<ListBoxItem>().Select(item => (item.DataContext as RungRow)?.Label).FirstOrDefault();

    private static List<string> Texts(ListBox rail) =>
        [.. rail.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty)];

    private static void Save(Avalonia.Media.Imaging.WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// Four executables: big.exe's two processes send 750 and 1,000 bytes, zero.exe sends an empty message, blind.exe
    /// sends twice without recording a size, and listen.exe only receives, six times, so it has the most records.
    /// </summary>
    private static ObservationRowV1[] Traffic() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Tools\zero.exe" }),
        Timed(Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Tools\blind.exe" }),
        Timed(Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\listen.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10)),
        Timed(Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 250, 100, 11)),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 1_000, 101, 12)),
        Timed(Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 0, 200, 13)),
        Timed(Transfer(14, ObservationKind.Send, AccountingSide.SendSide, null, 300, 14)),
        Timed(Transfer(15, ObservationKind.Send, AccountingSide.SendSide, null, 300, 15)),
        .. Enumerable.Range(0, 6).Select(index =>
            Timed(Transfer(20 + index, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 400, (ulong)(20 + index)))),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
