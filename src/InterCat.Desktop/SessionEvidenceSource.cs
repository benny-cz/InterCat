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
    private readonly Lock storeGate = new();
    private SessionStore? store;

    public string SessionPath { get; } = sessionPath ?? throw new ArgumentNullException(nameof(sessionPath));

    public Guid SessionId { get; } = sessionId;

    /// <summary>The generation the workspace was projected from; a page may continue in a newer one.</summary>
    public long Generation { get; } = generation;

    /// <summary>Counts each edge's and channel's records inside an analysis interval, for a brushed ranking.</summary>
    public Task<SessionIntervalCounts> CountAsync(TimeRange interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalQuery.Count(
            Store(),
            interval,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>The timeline over a viewport at the resolution it is drawn at, for zoomed detail.</summary>
    public Task<SessionTimelineDetail> TimelineAsync(TimeRange interval, int columns, CancellationToken cancellationToken) =>
        Task.Run(() => SessionTimelineQuery.Detail(
            Store(),
            interval,
            columns,
            cancellationToken), cancellationToken);

    /// <summary>The timeline over a viewport together with the records a focused rung reads, counted in one pass.</summary>
    public Task<SessionFocusedTimeline> FocusedTimelineAsync(
        TimeRange interval, int columns, TimelineFocus focus, CancellationToken cancellationToken) =>
        Task.Run(() => SessionTimelineQuery.Focused(
            Store(),
            interval,
            columns,
            focus,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// The session's store, opened once and shared by every read. Opening verifies the pointer, the manifest and the
    /// directory, which costs several times more than a timeline or page read itself; each read still leases whichever
    /// generation is current. A failed open is not kept, so the next read tries again.
    /// </summary>
    private SessionStore Store()
    {
        lock (storeGate)
        {
            return store ??= SessionStore.OpenExisting(LocalOwnedDirectory.Open(SessionPath));
        }
    }

    /// <summary>Every record of a scope up to a limit, in one pass under one lease, for an export of the whole scope.</summary>
    public Task<SessionEvidencePage> ReadScopeAsync(EvidenceScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.Run(() => SessionEvidenceQuery.ReadScope(
            Store(),
            limit,
            scope.ChannelKey,
            scope.Interval,
            scope.OwnerProcesses.Count == 0 ? null : scope.OwnerProcesses,
            resolveOwners: true,
            cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<SessionEvidencePage> ReadAsync(EvidenceScope scope, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.Run(() => SessionEvidenceQuery.Read(
            Store(),
            scope.ChannelKey,
            scope.Interval,
            pageSize: SessionEvidenceQuery.DefaultPageSize,
            cursor: cursor,
            ownerProcesses: scope.OwnerProcesses.Count == 0 ? null : scope.OwnerProcesses,
            resolveOwners: true,
            cancellationToken: cancellationToken), cancellationToken);
    }
}
