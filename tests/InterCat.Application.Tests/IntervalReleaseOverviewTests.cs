using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A session after an interval release (ADR-043) read as the window reads it: what it keeps and since when, and the
/// interval before the boundary drawn and said as a partial gap, never as a quiet one (§12.1 S5, R21).
/// </summary>
public sealed class IntervalReleaseOverviewTests
{
    [Fact(DisplayName = "R21: after an interval release the overview says what is kept since when, and before the boundary is a partial gap")]
    public void TheOverviewStatesARelease()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Timed(
            Lifecycle(10, ObservationKind.Create, 100, 1),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between("127.0.0.1:50000", "10.0.0.9:443"),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3).Between("127.0.0.1:50000", "10.0.0.9:443")));
        Publish(session.Store, Timed(
            Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4).Between("127.0.0.1:50000", "10.0.0.9:443"),
            Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 5).Between("127.0.0.1:50000", "10.0.0.9:443")),
            coverage: TransportLedger(tcp: true, udp: false));
        SessionOverviewBundle whole = SessionOverviewProjector.Project(session.Store);
        Assert.Null(whole.Retained);
        Assert.DoesNotContain(whole.Timeline, bucket => bucket.Coverage == CoverageState.PartialGap);

        _ = IntervalRelease.Release(session.Store, 10_000, "older than the retained window", Committed, Committed);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);

        // What is kept, since when, and what went before it, as the window's size line says it.
        Assert.Equal(
            $"Kept from {SessionTimeText.Seconds(3_001, System.Globalization.CultureInfo.CurrentCulture)} on: the records read before it were released "
                + "on 2026-09-22 16:00 UTC - 3 records, keeping 2 rows of them as the evidence of what came after (older than the retained window).",
            overview.Retained);

        // The columns before the boundary are no better than a partial gap, drawn and said as one; those after it are as
        // they were. The kept rows hold the timeline's extent where it was, so its columns are the same.
        long boundary = SourceClockMath.FirstNativeAtOrAfter(TestClock, new SessionTimestamp(3_001));
        Assert.Equal(whole.Timeline.Select(bucket => bucket.Interval), overview.Timeline.Select(bucket => bucket.Interval));
        Assert.Contains(overview.Timeline, bucket => bucket.Coverage == CoverageState.PartialGap);
        Assert.All(whole.Timeline.Zip(overview.Timeline), pair => Assert.Equal(
            pair.First.Interval.StartTicks < boundary ? SessionCoverage.Worst([pair.First.Coverage, CoverageState.PartialGap]) : pair.First.Coverage,
            pair.Second.Coverage));
        MechanismCoverage tcp = overview.MechanismCoverage.Single(entry => entry.Mechanism == Mechanism.Tcp);
        Assert.Equal(CoverageState.PartialGap, tcp.State);
        Assert.Contains("were released by retention", tcp.Reason, StringComparison.Ordinal);
    }

    private static ObservationRowV1[] Timed(params ObservationRowV1[] rows) =>
        [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];
}
