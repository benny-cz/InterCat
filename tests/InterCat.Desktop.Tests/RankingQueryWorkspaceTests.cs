using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>The window's export names the icat metric query its rows answer, over the generation it shows (R18, I16).</summary>
public sealed class RankingQueryWorkspaceTests
{
    private static readonly DateTimeOffset Exported = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "I16: the window's export names its rows' query under the policy it counts by, and says why once the session moved on")]
    public void TheWindowsExportNamesItsQuery() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        var tokens = new List<string>();
        foreach (EvidencePolicy policy in new[] { EvidencePolicy.IncludeCorrelated, EvidencePolicy.IncludeCandidates })
        {
            SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store, policy);
            using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
                new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, policy)
                {
                    ManifestDigest = overview.ManifestDigest,
                });
            workspace.RankBy = RankingMetric.BytesReceived;
            await workspace.RankingReady;
            SessionExportResult export = await workspace.ExportAsync(ExportFormat.Json, Exported);

            // The query is the one icat metric is asked under the same policy, written out where it is not the default.
            MetricRequest request = RankingQuery.Request(RankingMetric.BytesReceived, LaneGrouping.Executable, null, policy)!;
            RankingQueryName query = export.Context.Query!;
            Assert.Equal(SessionMetrics.Identify(session.Store, request)!.Token, query.Identity!.Token);
            Assert.Equal(policy == EvidencePolicy.IncludeCandidates, query.Command!.EndsWith(" --evidence-policy include-candidates", StringComparison.Ordinal));
            Assert.Equal(overview.ManifestDigest, export.Context.Manifest);
            tokens.Add(query.Identity.Token);
        }

        // A policy decides which records a program's row holds, so the two views are two queries.
        Assert.Equal(2, tokens.Distinct(StringComparer.Ordinal).Count());

        SessionOverviewBundle shown = SessionOverviewProjector.Project(session.Store);
        using var stale = new WorkspaceViewModel(OverviewWorkspace.From(shown), shown.GraphIdentity,
            new SessionEvidenceSource(session.Path, shown.SessionId, shown.Generation) { ManifestDigest = shown.ManifestDigest });

        // Rows read from a generation the session has since moved past are exported as read, named by no newer identity.
        Publish(session.Store, [Lifecycle(9_000, ObservationKind.Create, 900, 90_000) with { SessionRelativeTicks = 900_000 }]);
        SessionExportResult moved = await stale.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(shown.Generation, moved.Context.Generation);
        Assert.Equal(shown.ManifestDigest, moved.Context.Manifest);
        Assert.Null(moved.Context.Query!.Identity);
        Assert.Equal("The session published a newer generation than these rows were read from; icat metric answers that one now.",
            moved.Context.Query.Unnamed);

        // A session whose folder cannot be read still exports what the window shows, and the file says why no query is named.
        using var unread = new WorkspaceViewModel(OverviewWorkspace.From(shown), shown.GraphIdentity,
            new SessionEvidenceSource(Path.Combine(session.Path, "gone"), shown.SessionId, shown.Generation));
        SessionExportResult gone = await unread.ExportAsync(ExportFormat.Json, Exported);
        Assert.Equal(shown.Groups.Count, gone.Rows);
        Assert.StartsWith("The session could not be read to name the icat metric query these rows answer: ",
            gone.Context.Query!.Unnamed, StringComparison.Ordinal);
        Assert.Null(gone.Context.Manifest);
    });
}
