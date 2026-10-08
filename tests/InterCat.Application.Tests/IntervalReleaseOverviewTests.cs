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
    [Fact(DisplayName = "R21: after an interval release the overview begins where every record is kept, says what is kept since when, and states the whole session as a partial gap")]
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

        // The timeline and the minimap begin at the first tick wholly after the boundary, where every record is kept: the
        // rows kept from before it are records the session holds, counted among its rows, but the released stretch is not
        // drawn as a long gap before the records kept (ADR-045). No column reaches into it, so each is as it was.
        Assert.Equal(3_001, overview.RetainedFromNanoseconds);
        Assert.Equal(overview.Extent, SessionRecording.RecordsExtent(reopened));
        Assert.Equal(new TimeRange(31, whole.Extent!.Value.EndTicks), overview.Extent);
        Assert.Equal(overview.Extent, overview.Minimap!.Extent);
        Assert.Equal(4, overview.ObservationRows);
        Assert.Equal(2, overview.Timeline.Sum(bucket => bucket.ObservationCount));
        Assert.DoesNotContain(overview.Timeline, bucket => bucket.Coverage == CoverageState.PartialGap);
        Assert.Equal(3_001, OverviewWorkspace.From(overview).RetainedFromNanoseconds);

        // The whole session's coverage is no better than a partial gap, and says why.
        MechanismCoverage tcp = overview.MechanismCoverage.Single(entry => entry.Mechanism == Mechanism.Tcp);
        Assert.Equal(CoverageState.PartialGap, tcp.State);
        Assert.Contains("were released by retention", tcp.Reason, StringComparison.Ordinal);
    }

    private static ObservationRowV1[] Timed(params ObservationRowV1[] rows) =>
        [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];
}
