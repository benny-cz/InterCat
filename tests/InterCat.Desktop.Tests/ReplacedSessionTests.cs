using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A view whose session's folder is written again with another session (§20.1): what it can no longer read it says it
/// could not, in the sentence every read says, and once the folder holds its session again it reads as before.
/// </summary>
public sealed class ReplacedSessionTests
{
    private const string StandIn = " · this view's own count could not be read, so the overview's columns are drawn: ";

    [Fact(DisplayName = "§20.1: a view whose own count cannot be read says the overview's columns stand in, and why, until it is counted again")]
    public void AZoomThatCannotBeCountedSaysSo() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        string kept = Path.Combine(Path.GetTempPath(), "InterCat.Desktop.Tests.Replaced", Guid.NewGuid().ToString("N"));
        string other = kept + "-other";
        try
        {
            // Counted, a zoom says nothing of the overview.
            workspace.RequestTimelineDetail(new TimeRange(10, 20), 64);
            await workspace.TimelineDetailReady;
            Assert.NotNull(workspace.TimelineDetail);
            Assert.DoesNotContain("own count", workspace.TimelineCaption, StringComparison.Ordinal);

            // With another session in the folder, the overview's columns stand in for the next zoom, and the caption
            // says so and why, at the machine rung and at a rung whose filter was taken off.
            Copy(session.Path, kept);
            Directory.CreateDirectory(other);
            Publish(SessionStore.Open(LocalOwnedDirectory.Open(other), Guid.NewGuid(), "replacing-tests"),
                [Lifecycle(1, ObservationKind.Inventory, 700, 1) with { SessionRelativeTicks = 100 }]);
            Move(other, session.Path);
            string another = SessionStore.AnotherSession.TrimEnd('.');
            workspace.RequestTimelineDetail(new TimeRange(20, 30), 64);
            await workspace.TimelineDetailReady;
            Assert.Null(workspace.TimelineDetail);
            Assert.StartsWith($"Observed records by mechanism · 2 lanes{StandIn}{another} · ", workspace.TimelineCaption,
                StringComparison.Ordinal);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
            Assert.True(workspace.Descend());
            workspace.SelectedFilter = workspace.Filters.Single(filter => filter.Field == "group");
            Assert.True(workspace.RemoveSelectedFilter());
            workspace.RequestTimelineDetail(new TimeRange(30, 40), 64);
            await workspace.TimelineDetailReady;
            Assert.Equal($"Observed records{StandIn}{another} · Shift+drag brushes a range · unknown stays unknown",
                workspace.TimelineCaption);

            // The session back in its folder, the next zoom is counted, and the caption no longer says otherwise.
            Move(kept, session.Path);
            workspace.RequestTimelineDetail(new TimeRange(40, 50), 64);
            await workspace.TimelineDetailReady;
            Assert.NotNull(workspace.TimelineDetail);
            Assert.Equal("Observed records · Shift+drag brushes a range · unknown stays unknown", workspace.TimelineCaption);
        }
        finally
        {
            foreach (string folder in new[] { kept, other })
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    });

    /// <summary>Two processes; the client sends to the server every 5 ticks from 10 to 55.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(5, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 500 },
        Lifecycle(6, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 600 },
        .. Enumerable.Range(0, 10).Select(index => Transfer(10 + 5 * index, ObservationKind.Send, AccountingSide.SendSide, 64,
                100, (ulong)(10 + index)).Between("127.0.0.1:50000", "127.0.0.1:443")
            with { SessionRelativeTicks = (10 + 5 * index) * 100L }),
    ];

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    /// <summary>Empties <paramref name="to"/> and moves what <paramref name="from"/> holds into it.</summary>
    private static void Move(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(to))
        {
            File.Delete(file);
        }

        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Move(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }
}
