using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A redacted package of a session that released an interval (ADR-043): its policy states the release's boundary, so the
/// package's readers say its coverage before it is a partial gap, as its source's do, rather than read it as quiet.
/// </summary>
public sealed partial class RedactedSessionPackageTests
{
    [Fact(DisplayName = "R21: a redacted package of a released session states the release, so before its boundary it is a partial gap too")]
    public void APackageStatesItsSourcesRelease()
    {
        using var source = new TemporarySession();
        Publish(source.Store, AtSessionTime(
            Lifecycle(10, ObservationKind.Create, 100, 1),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between("127.0.0.1:50000", "10.0.0.9:443"),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3).Between("127.0.0.1:50000", "10.0.0.9:443")));
        Publish(source.Store, AtSessionTime(
            Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4).Between("127.0.0.1:50000", "10.0.0.9:443"),
            Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 5).Between("127.0.0.1:50000", "10.0.0.9:443")),
            coverage: TransportLedger(tcp: true, udp: false));
        _ = IntervalRelease.Release(source.Store, 10_000, "older than the retained window", Committed, Committed);
        SessionStore released = SessionStore.OpenExisting(LocalOwnedDirectory.Open(source.Path));

        // The whole session: its policy states the boundary and says what its source no longer held.
        using var whole = new PackageDirectory();
        RedactedSessionPackageResult result = RedactedSessionPackage.Create(released, whole.Path, Committed);
        Assert.Equal(3_001L, result.Source.ReleasedBeforeNanoseconds);
        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(whole.Path));
        RedactedSessionPolicyV1 policy = RedactedSessionPolicyV1.Decode(SessionSegments.RedactionPolicy(shared.Root, shared.Current!)!);
        Assert.Equal(3_001L, policy.ReleasedBeforeNanoseconds);
        Assert.Contains(policy.Omitted, entry => entry.StartsWith("The records its source released by retention", StringComparison.Ordinal));

        // Its reader places the boundary on the package's own clock, so coverage before it is a partial gap, saying why.
        CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(shared.Root, shared.Current!)!;
        SourceClockDescriptor clock = SessionSegments.SourceClock(shared.Root, shared.Current!)!.Value;
        long boundary = SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(3_001));
        Assert.Equal(new LedgerRelease(boundary, 3_001), ledger.ReleasedBefore);
        MechanismCoverage tcp = SessionCoverage.Of(ledger, Mechanism.Tcp);
        Assert.Equal(CoverageState.PartialGap, tcp.State);
        Assert.StartsWith("the records read before 0.000003001 s were released by retention", tcp.Reason, StringComparison.Ordinal);
        Assert.Equal(CoverageState.Covered, SessionCoverage.Of(ledger, Mechanism.Tcp, new TimeRange(boundary, boundary + 10)).State);

        // Every reader says it after the package's own summary, before its warning.
        SessionRedaction redaction = SessionRedaction.Read(shared.Root, shared.Current!)!;
        Assert.Equal(
            SessionRedaction.Summary + " " + SessionRedaction.Released(3_001, CultureInfo.CurrentCulture) + " " + RedactedSessionPackage.Warning,
            redaction.Statement(CultureInfo.CurrentCulture));
        Assert.Contains(redaction.Statement(CultureInfo.CurrentCulture), SessionOverviewProjector.Project(shared).Caveats);

        // An interval wholly after the boundary holds only what was kept whole: its policy names no release.
        using var later = new PackageDirectory();
        RedactedSessionPackageResult after = RedactedSessionPackage.Create(released, later.Path, Committed, new TimeRange(200, 301));
        Assert.Null(after.Source.ReleasedBeforeNanoseconds);
        SessionStore afterShared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(later.Path));
        Assert.Null(SessionSegments.CoverageLedger(afterShared.Root, afterShared.Current!)!.ReleasedBefore);
        Assert.Null(SessionRedaction.Read(afterShared.Root, afterShared.Current!)!.ReleasedBeforeNanoseconds);

        // One that starts before it holds some of the released time, and names it.
        using var across = new PackageDirectory();
        Assert.Equal(3_001L, RedactedSessionPackage.Create(released, across.Path, Committed, new TimeRange(25, 301)).Source.ReleasedBeforeNanoseconds);

        // A policy never states a boundary that is not a positive session time.
        Assert.Throws<InvalidDataException>(() => (policy with { ReleasedBeforeNanoseconds = 0 }).Encode());
    }

    private static ObservationRowV1[] AtSessionTime(params ObservationRowV1[] rows) =>
        [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];
}
