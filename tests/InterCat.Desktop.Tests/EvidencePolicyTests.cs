using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The evidence policy a window reads a session under (§6.8): every read its evidence source makes binds a record to a
/// process as strongly as that policy allows, so a ranking, a brushed count, the timeline's focus and the records E lists
/// never disagree with the overview about whose a reused PID's records are.
/// </summary>
public sealed class EvidencePolicyTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§6.8: every read of an evidence source binds a reused PID's later holder's records as its policy says")]
    public async Task EveryReadBindsAsItsPolicySays()
    {
        // PID 100 exits and is created again, and its second holder sends twice, 8 bytes each: candidates that could be
        // late records of the first holder. The server receives one of them.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1)),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2)),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 3)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 4)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd)),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId later = overview.Nodes.Single(node => node.PidHolder == 2).Id;
        var scope = new EvidenceScope("Records owned by the later holder", null, [later], null, null);
        TimelineFocus focus = Assert.IsType<TimelineFocus>(TimelineFocus.Of(scope));
        TimeRange extent = overview.Extent!.Value;

        // Correlated evidence counts its creation alone and no send; candidates count its two sends and their bytes too.
        foreach ((EvidencePolicy policy, long records, long sent) in Expected)
        {
            var source = new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, policy);
            Assert.Equal(policy, source.Policy);
            SessionIntervalCounts counts = await source.CountAsync(extent, CancellationToken.None);
            Assert.Equal(records, counts.ProcessRecords.GetValueOrDefault(later)?.Sum(count => count.Records) ?? 0);
            SessionByteMeasures bytes = await source.ByteMeasuresAsync(null, CancellationToken.None);
            Assert.Equal(sent, bytes.ByProcess.GetValueOrDefault(later)?.SentBytes ?? 0);
            SessionFocusedTimeline timeline = await source.FocusedTimelineAsync(extent, 8, focus, CancellationToken.None);
            Assert.Equal(records, timeline.Focus.Sum(bucket => bucket.ObservationCount));
            Assert.Equal(records, (await source.ReadAsync(scope, null, CancellationToken.None)).Records.Count);
            Assert.Equal(records, (await source.ReadScopeAsync(scope, 100, CancellationToken.None)).Records.Count);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, (EvidencePolicy)0));
    }

    /// <summary>Each policy, with the later holder's records and bytes sent that it counts.</summary>
    private static readonly (EvidencePolicy Policy, long Records, long Sent)[] Expected =
    [
        (EvidencePolicy.IncludeCorrelated, 1, 0),
        (EvidencePolicy.IncludeCandidates, 3, 16),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
