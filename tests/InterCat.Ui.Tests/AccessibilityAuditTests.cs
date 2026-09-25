using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The window as UI Automation exposes it to a screen reader (R15, §6.5), audited at every rung from the machine to a
/// channel with the table equivalents shown: what each focusable element and list item is called, and what the drawn
/// panes say about themselves.
/// </summary>
public sealed partial class AccessibilityAuditTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R15: every focusable element and list item is named for the ear at every rung, never by a record's fields")]
    public async Task EveryRungIsNamedForTheEar()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(40));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.ShowTables = true;

        // The machine, the group, one of its processes, and that process's channel.
        var rungs = new List<string>();
        for (int depth = 0; depth < 4; depth++)
        {
            await workspace.TimelineDetailReady;
            Dispatch();
            _ = window.CaptureRenderedFrame();
            rungs.Add(workspace.LevelBadge);
            Audit(window, workspace);
            if (depth < 3)
            {
                workspace.SelectedRung = workspace.RungRows[0];
                Assert.True(workspace.Descend(), $"Could not descend from {workspace.LevelBadge}.");
            }
        }

        Assert.Equal(4, rungs.Distinct(StringComparer.Ordinal).Count());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a lane selector's options and its chosen value read as sentences and labels, not record fields")]
    public void LaneSelectorsReadAsWords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(10));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.ShowTables = true;
        Dispatch();

        ComboBox lanes = window.GetControl<ComboBox>("TimelineLaneSelector");
        Assert.True(lanes.IsVisible);
        lanes.IsDropDownOpen = true;
        Dispatch();
        for (int index = 0; index < workspace.TimelineLaneOptions.Count; index++)
        {
            Control item = Assert.IsAssignableFrom<Control>(lanes.ContainerFromIndex(index));
            Assert.Equal(workspace.TimelineLaneOptions[index].AccessibleName, AutomationProperties.GetName(item));
        }

        lanes.IsDropDownOpen = false;
        workspace.SelectTimelineLane(Mechanism.Tcp);
        Dispatch();
        IValueProvider value = Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(lanes));
        Assert.Equal("TCP", value.Value);
        Assert.Equal("All mechanisms", workspace.TimelineLaneOptions[0].ToString());
        window.Close();
    }

    /// <summary>Walks the window's automation tree and checks every element a screen reader lands on.</summary>
    private static void Audit(MainWindow window, WorkspaceViewModel workspace)
    {
        var problems = new List<string>();
        var listItems = 0;
        Walk(ControlAutomationPeer.CreatePeerForElement(window));
        Assert.True(problems.Count == 0, $"At {workspace.LevelBadge}:\n" + string.Join("\n", problems));
        Assert.True(listItems > 0, $"At {workspace.LevelBadge} no list item was reachable.");

        // Each list's items speak exactly their rows' sentences, in the order the rows are listed.
        foreach (string name in new[] { "RungList", "CrumbList", "RelationshipList", "IntervalList" })
        {
            ListBox list = window.GetControl<ListBox>(name);
            IAccessibleRow[] rows = [.. list.Items.OfType<IAccessibleRow>()];
            for (int index = 0; index < rows.Length; index++)
            {
                if (list.ContainerFromIndex(index) is { } container)
                {
                    Assert.Equal(rows[index].AccessibleName, AutomationProperties.GetName(container));
                }
            }
        }

        // The drawn panes are custom controls with a role, help that names their table, and what they draw now.
        foreach ((string name, string role, string? status) in new[]
        {
            ("GraphSurface", "graph", (string?)workspace.GraphSummary),
            ("TimelineSurface", "timeline", workspace.TimelineCaption),
            ("MinimapSurface", "minimap", null),
        })
        {
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(window.GetControl<Control>(name));
            Assert.Equal(AutomationControlType.Custom, peer.GetAutomationControlType());
            Assert.Equal(role, peer.GetLocalizedControlType());
            Assert.False(string.IsNullOrWhiteSpace(peer.GetHelpText()), $"{name} has no help text.");
            Assert.Equal(status, peer.GetItemStatus());
        }

        void Walk(AutomationPeer peer)
        {
            string? spoken = peer.GetName();
            AutomationControlType type = peer.GetAutomationControlType();
            if (type == AutomationControlType.ListItem) listItems++;
            if ((peer.IsKeyboardFocusable() || type == AutomationControlType.ListItem) && string.IsNullOrWhiteSpace(spoken))
            {
                problems.Add($"{type} {peer.GetClassName()} has no name");
            }

            if (spoken is not null && RecordDump().IsMatch(spoken))
            {
                problems.Add($"{type} {peer.GetClassName()} is named by its fields: {spoken}");
            }

            foreach (AutomationPeer child in peer.GetChildren())
            {
                Walk(child);
            }
        }
    }

    /// <summary>A compiler-written record ToString: "TypeName { Field = …".</summary>
    [GeneratedRegex(@"\b\w+ \{ \w+ = ")]
    private static partial Regex RecordDump();

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times.</summary>
    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
