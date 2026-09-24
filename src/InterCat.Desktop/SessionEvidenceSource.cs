using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// Where the viewer reads evidence rows and original records for the session it shows. Every read acquires its own
/// lease off the UI thread; pages continue by row, so a live publication between two pages does not restart them.
/// </summary>
public sealed class SessionEvidenceSource(string sessionPath, Guid sessionId, long generation)
{
    public string SessionPath { get; } = sessionPath ?? throw new ArgumentNullException(nameof(sessionPath));

    public Guid SessionId { get; } = sessionId;

    /// <summary>The generation the workspace was projected from; a page may continue in a newer one.</summary>
    public long Generation { get; } = generation;

    /// <summary>Counts each edge's and channel's records inside an analysis interval, for a brushed ranking.</summary>
    public Task<SessionIntervalCounts> CountAsync(TimeRange interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalQuery.Count(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(SessionPath)),
            interval,
            cancellationToken: cancellationToken), cancellationToken);

    public Task<SessionEvidencePage> ReadAsync(EvidenceScope scope, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.Run(() => SessionEvidenceQuery.Read(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(SessionPath)),
            scope.ChannelKey,
            scope.Interval,
            pageSize: SessionEvidenceQuery.DefaultPageSize,
            cursor: cursor,
            ownerProcesses: scope.OwnerProcesses.Count == 0 ? null : scope.OwnerProcesses,
            resolveOwners: true,
            cancellationToken: cancellationToken), cancellationToken);
    }
}
