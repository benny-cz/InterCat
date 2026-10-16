using Avalonia.Automation;
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
/// The processes an aggregate counts, listed in the window's inspector while it is chosen (§6.3), read whole and opened
/// by the keyboard.
/// </summary>
public sealed class AggregateMembersWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: an aggregate chosen in the window lists its processes in the inspector, each read whole, and Enter on one opens its rung")]
    public async Task TheInspectorListsAnAggregatesProcesses()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Control panel = window.GetControl<Control>("MembersPanel");
            ListBox members = window.GetControl<ListBox>("MemberList");
            Assert.False(panel.IsVisible);

            // Chosen, Other hosts only lists its two programs beneath its description, the busier first, each said whole.
            workspace.SelectGraphNode(workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Key);
            Dispatch();
            Assert.True(panel.IsVisible);
            Assert.Equal("Its 2 processes, the busiest first", window.GetControl<TextBlock>("MembersHeadingText").Text);
            Assert.False(window.GetControl<TextBlock>("MembersNoteText").IsVisible);
            Assert.Equal(2, members.ItemCount);
            Assert.Equal("program1.exe · PID 101, 4 records · 1 TCP connection to 1 host. Press Enter to open it.",
                AutomationProperties.GetName(members.ContainerFromIndex(0)!));
            Save(window, "inspector-aggregate-members.png");

            // Enter on the second opens its rung, through its group, and the list goes with the aggregate's choice.
            Assert.True(members.ContainerFromIndex(1)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(3, workspace.Crumbs.Count);
            Assert.EndsWith("program0.exe · PID 100", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Ten programs of one process each, program0.exe (PID 100) to program9.exe (PID 109), each connecting to a host of its
    /// own and sending to it once more than the one before: the two quietest are counted in Other hosts only.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>();
        for (int index = 0; index < 10; index++)
        {
            rows.Add(Lifecycle(index, ObservationKind.Create, 100 + index, ordinal++)
                with { ResourceName = $@"C:\Apps\program{index}.exe", SessionRelativeTicks = 100 + index });
        }

        for (int index = 0; index < 10; index++)
        {
            string local = $"192.168.1.10:{52_000 + index}";
            string remote = $"52.0.0.{index + 1}:443";
            rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100 + index, ordinal)
                .Between(local, remote) with { SessionRelativeTicks = 10_000 + (long)ordinal });
            ordinal++;
            for (int send = 0; send <= index; send++)
            {
                rows.Add(Transfer(100 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 100 + index, ordinal)
                    .Between(local, remote) with { SessionRelativeTicks = 10_000 + (long)ordinal });
                ordinal++;
            }
        }

        return [.. rows];
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }
}
