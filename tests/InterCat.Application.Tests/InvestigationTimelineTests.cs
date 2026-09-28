using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>An investigation's merged time (§8.2): each session a lane on the investigation's own axis.</summary>
public sealed class InvestigationTimelineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "InterCat.Application.Tests.Timeline", Guid.NewGuid().ToString("N"));

    public InvestigationTimelineTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "R21: an investigation's timeline places each session's records where its alignment puts them, and lists one with no place")]
    public void TheTimelinePlacesEachSessionByItsAlignment()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        Guid a = InvestigationWorkspace.Add(workspace, Session("a", "lab-1", 4), Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session("b", "lab-2", 6), Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Session("c", "lab-3", 2), Now).SessionId;

        // Before any alignment, no session has a place on the investigation's axis.
        InvestigationTimelineView none = InvestigationTimeline.Read(workspace, 10);
        Assert.Null(none.Interval);
        Assert.All(none.Lanes, lane => Assert.Equal((false, WorkspaceTimeGap.NoTimeReference), (lane.Placed, lane.Gap)));

        // B's instant 0 is A's 1 s: its records fall a second after A's, on the axis's far columns.
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 500_000, 10, null, Now);
        InvestigationTimelineView view = InvestigationTimeline.Read(workspace, 10);
        Assert.Equal([a, b, c], view.Lanes.Select(lane => lane.SessionId));
        (InvestigationLane first, InvestigationLane second, InvestigationLane third) = (view.Lanes[0], view.Lanes[1], view.Lanes[2]);
        Assert.Equal((4L, 6L), (first.Records, second.Records));
        Assert.Equal(TimeUncertainty.Exact, first.Uncertainty);
        Assert.Equal(first.Extent!.Value.StartTicks, view.Interval!.Value.StartTicks);
        Assert.Equal(second.Extent!.Value.EndTicks, view.Interval.Value.EndTicks);
        Assert.True(second.Extent.Value.StartTicks >= first.Extent.Value.StartTicks + 10_000_000 - 100);
        Assert.Equal(4, first.Buckets[0].ObservationCount);
        Assert.Equal(6, second.Buckets[^1].ObservationCount);
        Assert.Equal(second.Buckets[^1].Interval, first.Buckets[^1].Interval);
        Assert.InRange(second.Uncertainty!.Value.HalfWidthNanoseconds, 500_000, 500_000 + 10);

        // C, not aligned, is listed without a place, and says why.
        Assert.Equal((false, WorkspaceTimeGap.NotAligned), (third.Placed, third.Gap));
    }

    /// <summary>A session whose process 100 sends <paramref name="records"/> datagrams 100 µs into its capture, 1 µs apart.</summary>
    private string Session(string name, string host, int records)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "timeline-tests");
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, records).Select(index =>
                Transfer(1_000 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                    .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (1_000 + (index * 10)) * 100 }),
        ];
        Publish(store, rows, capture: CaptureId.New(), clock: ClockFor(ClockId.New(), host));
        store.ReleaseSegmentReaders();
        return directory;
    }
}
