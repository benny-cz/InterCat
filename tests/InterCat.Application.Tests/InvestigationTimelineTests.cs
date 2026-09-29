using System.Globalization;
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

    [Fact(DisplayName = "R21: a session aligned at two instants is placed at the rate they measure, its lane uncertain by its wander")]
    public void ASessionAlignedAtTwoInstantsIsPlacedAtTheirRate()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        Guid a = InvestigationWorkspace.Add(workspace, Session("a", "lab-1", 4), Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session("b", "lab-2", 6), Now).SessionId;

        // B's -100 s is A's 0 s and its -90 s A's 10.01 s: B's clock runs 1,000 ppm slow, so its records, 100 µs into its
        // capture, fall at A's 100.1001001 s - a tenth of a second later than an offset alone would put them.
        InvestigationWorkspace.Align(workspace, b, -100_000_000_000, a, 0, 1_000, 0.5, null, Now, (-90_000_000_000, 10_010_000_000));
        InvestigationTimelineView view = InvestigationTimeline.Read(workspace, 10);
        InvestigationLane lane = view.Lanes[1];
        Assert.Equal(1_001_001_001, lane.Extent!.Value.StartTicks);
        Assert.Equal(1_001_001_052, lane.Extent.Value.EndTicks);
        Assert.Equal(6, lane.Buckets.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(view.Interval!.Value.EndTicks, lane.Buckets[^1].Interval.EndTicks);

        // 90 s from the nearer instant and beyond both, a 0.5 ppm wander adds 90 µs and the instants' 1 µs bound, carried
        // along their rate at 2 µs over 10 s, 18 µs: the lane is placed within about ±109 µs.
        Assert.InRange(lane.Uncertainty!.Value.HalfWidthNanoseconds, 109_001, 109_002);
    }

    [Fact(DisplayName = "R22: two captures of one host that ran at once are flagged, never merged, and two hosts' never compared")]
    public void CapturesOfOneHostThatRanAtOnceAreFlagged()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        Guid a = InvestigationWorkspace.Add(workspace, Session("a", "lab-1", 4), Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session("b", "lab-1", 6), Now).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Session("c", "lab-2", 6), Now).SessionId;
        Guid d = InvestigationWorkspace.Add(workspace, Session("d", "lab-1", 2), Now).SessionId;

        // B on A's clock, within 1 µs: their records share 3.1 µs, beyond that bound - one host's captures that ran at once.
        // C, on another host, overlaps too and is no one's concern; D, of A's host but unaligned, cannot be compared.
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 0, null, Now);
        InvestigationWorkspace.Align(workspace, c, 0, a, 0, 1_000, 0, null, Now);
        IReadOnlyList<WorkspaceOverlap> overlaps = InvestigationTimeline.Overlaps(workspace);
        Assert.Equal(
        [
            new WorkspaceOverlap(a, b, OverlapKind.Concurrent, new TimeRange(1_000, 1_031)),
            new WorkspaceOverlap(a, d, OverlapKind.Unknown, null),
            new WorkspaceOverlap(b, d, OverlapKind.Unknown, null),
        ], overlaps);
        Assert.Equal($"Sessions {a.ToString("N")[..8]} and {b.ToString("N")[..8]} of one host ran at once for 3.1 µs: records of one "
            + "event may be in both, so no count across them is summed.", overlaps[0].Statement(CultureInfo.InvariantCulture));
        Assert.Equal(overlaps, InvestigationTimeline.Read(workspace, 10).Overlaps);

        // Placed 5 µs after A's last record within 10 µs, B may have run at the same time; placed a second later, it did not.
        InvestigationWorkspace.Align(workspace, b, 0, a, 8_000, 10_000, 0, null, Now);
        Assert.Equal(OverlapKind.Possible, InvestigationTimeline.Overlaps(workspace)[0].Kind);
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 10_000, 0, null, Now);
        Assert.DoesNotContain(InvestigationTimeline.Overlaps(workspace), overlap => overlap.Second == b && overlap.First == a);
    }

    [Fact(DisplayName = "R22: two boots of one host that overlap in the investigation's time say an alignment is wrong")]
    public void TwoBootsThatOverlapSayAnAlignmentIsWrong()
    {
        string workspace = Path.Combine(root, "boots" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        Guid a = InvestigationWorkspace.Add(workspace, Session("boot-a", "lab-1", 4, Guid.NewGuid()), Now).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session("boot-b", "lab-1", 4, Guid.NewGuid()), Now).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 0, null, Now);
        WorkspaceOverlap overlap = Assert.Single(InvestigationTimeline.Overlaps(workspace));
        Assert.Equal(OverlapKind.Contradictory, overlap.Kind);
        Assert.EndsWith("which two boots cannot: one of their alignments is wrong.", overlap.Statement(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        // Nearer than their uncertainty, two boots are not flagged: one may simply have followed the other.
        InvestigationWorkspace.Align(workspace, b, 0, a, 8_000, 10_000, 0, null, Now);
        Assert.Empty(InvestigationTimeline.Overlaps(workspace));
    }

    /// <summary>
    /// A session whose process 100 sends <paramref name="records"/> datagrams 100 µs into its capture, 1 µs apart; with a
    /// clock calibration naming <paramref name="boot"/> when one is given.
    /// </summary>
    private string Session(string name, string host, int records, Guid? boot = null)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "timeline-tests");
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, records).Select(index =>
                Transfer(1_000 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                    .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (1_000 + (index * 10)) * 100 }),
        ];
        CaptureId capture = CaptureId.New();
        SourceClockDescriptor clock = ClockFor(ClockId.New(), host);
        Publish(store, rows, capture: capture, clock: clock, calibration: boot is null ? null : new ClockCalibrationV1
        {
            Contract = ClockCalibrationV1.ContractName,
            CaptureId = capture.Value,
            ClockId = clock.Id.Value,
            BootToken = boot,
            WallClock = "test-wall-clock",
            Samples = [new() { NativeTicks = 0, Utc = Now, AcquisitionUncertaintyNanoseconds = 200 }],
        });
        store.ReleaseSegmentReaders();
        return directory;
    }
}
