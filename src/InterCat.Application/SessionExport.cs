using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What a headless export reads: the rung reached by descending through these row keys from the machine rung, an
/// optional analysis interval, whether that rung's ranked rows or its evidence records are exported, and what the
/// machine and group rungs rank by.
/// </summary>
public sealed record SessionExportRequest(
    IReadOnlyList<string> Path,
    TimeRange? Interval,
    bool Evidence,
    ExportFormat Format,
    int EvidenceLimit = SessionExport.DefaultEvidenceLimit,
    bool Redacted = false,
    RankingMetric RankBy = RankingMetric.Records);

/// <summary>The exported text, the snapshot it names, and how many rows or records it holds.</summary>
public sealed record SessionExportResult(string Content, ExportContext Context, int Rows);

/// <summary>
/// The Desktop's export without the Desktop (R18). It projects the same overview, descends the same ladder by row key,
/// ranks within an interval by the same count, and pages the same evidence scope, then writes through the same
/// <see cref="WorkspaceExport"/> contract, so a script and a person exporting one view get the same file.
/// </summary>
public static class SessionExport
{
    /// <summary>Evidence records an export holds unless asked for more; one that stops short says it is incomplete.</summary>
    public const int DefaultEvidenceLimit = 100_000;

    public const int MaximumEvidenceLimit = 1_000_000;

    /// <summary>
    /// What an export ranked by RPC calls adds to its caveats: why no call could rank its rows, or the RPC coverage its
    /// counts rest on when it is not whole; null when RPC was covered over the scope.
    /// </summary>
    public static string? CallCaveat(SessionCallMeasures calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return calls.Unavailable is { } unavailable
            ? $"RPC calls were asked to rank the rows, but {unavailable}; the rows rank by records."
            : calls.Coverage.State == CoverageState.Covered
                ? null
                : $"RPC's capture coverage over this scope is {calls.Coverage.State}: {calls.Coverage.Reason}.";
    }

    private static void SameSession(IRankingMeasures measures, SessionOverviewBundle overview)
    {
        if (measures.SessionId != overview.SessionId)
        {
            throw new InvalidOperationException("This directory began holding another session during the export.");
        }
    }

    public static SessionExportResult Build(
        SessionStore store,
        SessionExportRequest request,
        DateTimeOffset exportedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Path);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.EvidenceLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.EvidenceLimit, MaximumEvidenceLimit);
        if (!Enum.IsDefined(request.RankBy)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Evidence && request.RankBy != RankingMetric.Records)
        {
            throw new ArgumentException("A ranking applies to ranked rows; an evidence export lists its records in reading order.");
        }

        SessionOverviewBundle overview = SessionOverviewProjector.Project(store, cancellationToken: cancellationToken);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        if (request.Interval is { } interval)
        {
            SessionIntervalCounts counts = SessionIntervalQuery.Count(store, interval, cancellationToken: cancellationToken);
            if (counts.SessionId != overview.SessionId)
            {
                throw new InvalidOperationException("This directory began holding another session during the export.");
            }

            snapshot = OverviewWorkspace.WithinInterval(snapshot, counts);
        }

        // A byte or call ranking reads each process's measures over the same scope as the counts, as the Desktop's
        // selector does. Calls a capture did not collect rank nothing, and the export says why its rows rank by records.
        string? rankingCaveat = null;
        switch (RankingMetrics.FamilyOf(request.RankBy))
        {
            case RankingFamily.Bytes:
                SessionByteMeasures bytes = SessionByteRanking.Measure(store, request.Interval, cancellationToken: cancellationToken);
                SameSession(bytes, overview);
                snapshot = OverviewWorkspace.WithBytes(snapshot, bytes);
                break;
            case RankingFamily.Calls:
                SessionCallMeasures calls = SessionCallRanking.Measure(store, request.Interval, cancellationToken: cancellationToken);
                SameSession(calls, overview);
                snapshot = OverviewWorkspace.WithCalls(snapshot, calls);
                rankingCaveat = CallCaveat(calls);
                break;
            case RankingFamily.Peers:
                SessionPeerMeasures peers = SessionPeerRanking.Measure(store, request.Interval, cancellationToken: cancellationToken);
                SameSession(peers, overview);
                snapshot = OverviewWorkspace.WithPeers(snapshot, peers);
                break;
        }

        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        foreach (string key in request.Path)
        {
            LadderView view = LadderProjection.Project(snapshot, ladder.Current);
            LadderRow row = view.Rows.FirstOrDefault(candidate => KeyNames(candidate.Key, key))
                ?? throw new ArgumentException(NoSuchRow(view, key));
            LadderDescent descent = LadderProjection.DescentFor(row, ladder.Current, request.Interval ?? ladder.Current.Viewport);
            if (!ladder.TryDescend(descent, out string? refusal))
            {
                throw new ArgumentException(refusal ?? $"The ladder refused to descend into '{key}'.");
            }
        }

        if (!request.Evidence)
        {
            // Only the machine and group rungs rank by a chosen metric; the export names the ranking its rows use.
            LadderView view = LadderProjection.Project(snapshot, ladder.Current, request.RankBy);
            RankingMetric applied = view.Rows.Any(row => row.Ranked is not null) ? request.RankBy : RankingMetric.Records;
            ExportContext ranked = WorkspaceExport.RankingContext(overview.SessionId, overview.Generation, ladder,
                request.Interval, OverviewWorkspace.DisclosureFor(snapshot), exportedUtc, applied);
            if (rankingCaveat is not null && ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group)
            {
                ranked = ranked with { Caveats = [.. ranked.Caveats, rankingCaveat] };
            }

            IReadOnlyList<LadderRow> rows = view.Rows;
            string content = request.Redacted
                ? RedactedShareExport.Ranking(request.Format, ranked, rows)
                : WorkspaceExport.Ranking(request.Format, ranked, rows);
            return new(content, ranked, rows.Count);
        }

        if (!ladder.TryDescend(LadderProjection.EvidenceDescentFor(ladder.Current, request.Interval ?? ladder.Current.Viewport),
            out string? evidenceRefusal))
        {
            throw new ArgumentException(evidenceRefusal ?? "This rung offers no evidence step.");
        }

        EvidenceScope scope = EvidenceScopes.Resolve(snapshot, ladder.Current);
        if (scope.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        SessionEvidencePage read = SessionEvidenceQuery.ReadScope(store, scope, request.EvidenceLimit, cancellationToken: cancellationToken);
        IReadOnlyList<SessionEvidenceRecord> records = read.Records;
        ExportContext context = WorkspaceExport.EvidenceContext(read.SessionId, read.Generation, ladder, scope,
            read.NextCursor is null, OverviewWorkspace.DisclosureFor(snapshot),
            string.Create(CultureInfo.InvariantCulture,
                $"Only the first {records.Count:N0} records of this scope are included; raise --limit for the rest."),
            exportedUtc);
        string evidenceContent = request.Redacted
            ? RedactedShareExport.Evidence(request.Format, context, records)
            : WorkspaceExport.Evidence(request.Format, context, records);
        return new(evidenceContent, context, records.Count);
    }

    /// <summary>
    /// Whether a requested key names a row: exactly, or as the same process instance written with or without dashes,
    /// since <c>icat overview --json</c> prints instance IDs in the dashed form.
    /// </summary>
    private static bool KeyNames(string rowKey, string requested) =>
        string.Equals(rowKey, requested, StringComparison.Ordinal)
        || (Guid.TryParse(rowKey, out Guid row) && Guid.TryParse(requested, out Guid asked) && row == asked);

    private static string NoSuchRow(LadderView view, string key)
    {
        string[] keys = [.. view.Rows.Take(12).Select(row => row.Key)];
        string rung = NavigationState.Name(view.State.Level);
        return keys.Length == 0
            ? $"'{key}' is not a row of the {rung} rung, which has no rows to descend into."
            : $"'{key}' is not a row of the {rung} rung. Its rows include: {string.Join(", ", keys)}"
                + (view.Rows.Count > keys.Length ? $", and {view.Rows.Count - keys.Length:N0} more." : ".");
    }
}
