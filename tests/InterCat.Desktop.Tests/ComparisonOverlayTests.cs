using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A comparison shown on the graph in the window's model (`contracts/comparison-v1.md` §8e): taken only of the session
/// shown, its marks worked out once for each drawing, and its line said again whenever what it says changes.
/// </summary>
public sealed class ComparisonOverlayTests
{
    private const long Second = 10_000_000;
    private static readonly DateTimeOffset Noon = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R7: the graph takes a comparison of the session it shows and of no other, and marks each drawing of it afresh")]
    public void TheGraphTakesOnlyItsOwnSessionsComparison() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Records(), calibration: Calibration());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        IntervalComparison comparison = SessionComparison.Compare(session.Store, new TimeRange(0, Second), new TimeRange(Second, 2 * Second));
        var said = new List<string?>();
        workspace.PropertyChanged += (_, change) => said.Add(change.PropertyName);

        // The tour reads no session, and neither a comparison of another session nor none is taken on.
        using (var tour = new WorkspaceViewModel(OverviewWorkspace.Empty(), "empty-workspace"))
        {
            Assert.False(tour.ShowComparison(comparison));
            tour.AdoptComparison(comparison);
            Assert.False(tour.ShowsComparison);
        }

        workspace.AdoptComparison(null);
        workspace.AdoptComparison(comparison with { SessionId = Guid.NewGuid() });
        Assert.False(workspace.ShowsComparison);
        Assert.Null(workspace.ComparisonOverlay);
        Assert.Equal(string.Empty, workspace.ComparisonShownSaid);

        // Clearing none says nothing; showing one says the graph's marks and its line changed.
        workspace.ClearComparison();
        Assert.Empty(said);
        Assert.True(workspace.ShowComparison(comparison));
        Assert.Equal(
            [nameof(WorkspaceViewModel.ComparisonOverlay), nameof(WorkspaceViewModel.ShowsComparison), nameof(WorkspaceViewModel.ComparisonShownSaid)],
            said);
        GraphComparisonOverlay first = workspace.ComparisonOverlay!;
        Assert.Same(first, workspace.ComparisonOverlay);

        // The session published no coverage, so neither interval is known to have covered TCP: each edge is not comparable,
        // and its card says so - the relationship's and the edge to the host the client reached alike.
        Assert.Equal(2, first.Edges.Count);
        Assert.All(first.Edges.Values, marked => Assert.Equal(GraphComparisonMark.NotComparable, marked.Mark));
        string hosts = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts).Key;
        Assert.Contains(first.Edges.Keys, key => key == hosts);
        foreach (string edge in first.Edges.Keys)
        {
            Assert.Contains(workspace.DescribeGraphHover(edge)!.Lines,
                line => line.StartsWith("Compared: ? not comparable: ", StringComparison.Ordinal));
        }
        Assert.StartsWith("The graph marks each edge by how it changed from ", workspace.ComparisonShownSaid, StringComparison.Ordinal);

        // A new drawing - the graph counted over an interval - is marked afresh, alike, and the line is said again.
        said.Clear();
        workspace.SelectInterval(new TimeRange(0, Second));
        await workspace.IntervalReady;
        Assert.Contains(nameof(WorkspaceViewModel.ComparisonOverlay), said);
        Assert.Contains(nameof(WorkspaceViewModel.ComparisonShownSaid), said);
        Assert.NotSame(first, workspace.ComparisonOverlay);
        Assert.Equal(first.Edges.Keys, workspace.ComparisonOverlay!.Edges.Keys);

        // Read on the wall clock, the line names the intervals on it.
        said.Clear();
        workspace.ReadsWallClock = true;
        Assert.Contains(nameof(WorkspaceViewModel.ComparisonShownSaid), said);
        Assert.StartsWith("The graph marks each edge by how it changed from "
            + workspace.TimeBase.HalfOpenRange(new TimeRange(0, Second), CultureInfo.CurrentCulture) + " to ",
            workspace.ComparisonShownSaid, StringComparison.Ordinal);
        Assert.True(workspace.TimeBase.IsWallClock);

        // Cleared, it is said gone, and no card speaks of it; with none shown, a new drawing or time base says nothing of it.
        said.Clear();
        workspace.ClearComparison();
        Assert.Equal(
            [nameof(WorkspaceViewModel.ComparisonOverlay), nameof(WorkspaceViewModel.ShowsComparison), nameof(WorkspaceViewModel.ComparisonShownSaid)],
            said);
        Assert.DoesNotContain(workspace.DescribeGraphHover(hosts)!.Lines, line => line.StartsWith("Compared", StringComparison.Ordinal));
        said.Clear();
        workspace.ReadsWallClock = false;
        workspace.SelectInterval(new TimeRange(Second, 2 * Second));
        await workspace.IntervalReady;
        Assert.DoesNotContain(nameof(WorkspaceViewModel.ComparisonShownSaid), said);
        Assert.DoesNotContain(nameof(WorkspaceViewModel.ComparisonOverlay), said);
    });

    /// <summary>
    /// client.exe's sends to server.exe and their receipt, one each half second of the first two seconds, and its connection
    /// to another host, which it sends to once in each second.
    /// </summary>
    private static ObservationRowV1[] Records()
    {
        var rows = new List<ObservationRowV1>
        {
            Transfer(Second / 8, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100).Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(Second / 2, ObservationKind.Send, AccountingSide.SendSide, 100, 100).Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(3 * Second / 2, ObservationKind.Send, AccountingSide.SendSide, 100, 100).Between("192.168.1.10:52000", "142.250.1.1:443"),
        };
        foreach (long at in new[] { Second / 4, 3 * Second / 4, 5 * Second / 4, 7 * Second / 4 })
        {
            rows.Add(Transfer(at, ObservationKind.Send, AccountingSide.SendSide, 64, 100).Between("127.0.0.1:50000", "127.0.0.1:8080"));
            rows.Add(Transfer(at + 10, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200).Between("127.0.0.1:8080", "127.0.0.1:50000"));
        }

        return [.. rows.OrderBy(row => row.NativeTicks).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];
    }

    /// <summary>The wall clock read at the session's start, noon, and 2 s in.</summary>
    private static ClockCalibrationV1 Calibration() => new()
    {
        Contract = ClockCalibrationV1.ContractName,
        CaptureId = TestSessions.Capture.Value,
        ClockId = TestClock.Id.Value,
        WallClock = "test-wall-clock",
        Samples =
        [
            new() { NativeTicks = 0, Utc = Noon, AcquisitionUncertaintyNanoseconds = 200 },
            new() { NativeTicks = 2 * Second, Utc = Noon.AddSeconds(2), AcquisitionUncertaintyNanoseconds = 200 },
        ],
    };
}
