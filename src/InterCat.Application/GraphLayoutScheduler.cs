using InterCat.Domain;

namespace InterCat.Application;

/// <summary>A frozen graph and constraint set submitted for an off-thread layout.</summary>
public sealed record GraphLayoutRequest(
    string GraphIdentity,
    IReadOnlyList<ProcessGroup> Groups,
    IReadOnlyList<ProcessNode> Nodes,
    IReadOnlyList<CommunicationEdge> Edges,
    IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? Previous,
    IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? Pins);

/// <summary>A frozen drawn graph and constraint set submitted for an off-thread layout.</summary>
public sealed record GraphDisplayLayoutRequest(
    string GraphIdentity,
    GraphDisplay Display,
    IReadOnlyDictionary<string, GraphPoint>? Previous,
    IReadOnlyDictionary<string, GraphPoint>? Pins);

/// <summary>
/// Runs layout outside the UI thread. A later request cancels the preceding one and invalidates its result even
/// if a worker finishes just as cancellation arrives. Only the latest complete result is ever returned for
/// publication; obsolete results return null rather than merging geometry from different graph identities (R7).
/// </summary>
public sealed class GraphLayoutScheduler : IDisposable
{
    private readonly object gate = new();
    private readonly Func<GraphLayoutRequest, CancellationToken, Task<GraphLayoutResult>> compute;
    private readonly Func<GraphDisplayLayoutRequest, CancellationToken, Task<GraphDisplayLayout>> computeDisplay;
    private CancellationTokenSource? pending;
    private long revision;
    private bool disposed;

    public GraphLayoutScheduler() : this((request, token) => Task.Run(() => GraphLayout.Compute(
        request.GraphIdentity, request.Groups, request.Nodes, request.Edges,
        request.Previous, request.Pins, token), token))
    {
    }

    /// <summary>Injectable worker for deterministic scheduler tests; production uses the bounded layout core.</summary>
    public GraphLayoutScheduler(Func<GraphLayoutRequest, CancellationToken, Task<GraphLayoutResult>> compute)
        : this(compute, (request, token) => Task.Run(() => GraphLayout.ComputeDisplay(
            request.GraphIdentity, request.Display, request.Previous, request.Pins, token), token))
    {
    }

    /// <summary>Injectable workers for both request kinds, so a test can hold a layout while it reads the first frame.</summary>
    public GraphLayoutScheduler(
        Func<GraphLayoutRequest, CancellationToken, Task<GraphLayoutResult>> compute,
        Func<GraphDisplayLayoutRequest, CancellationToken, Task<GraphDisplayLayout>> computeDisplay)
    {
        this.compute = compute ?? throw new ArgumentNullException(nameof(compute));
        this.computeDisplay = computeDisplay ?? throw new ArgumentNullException(nameof(computeDisplay));
    }

    public Task<GraphLayoutResult?> RequestAsync(
        string graphIdentity,
        IReadOnlyList<ProcessGroup> groups,
        IReadOnlyList<ProcessNode> nodes,
        IReadOnlyList<CommunicationEdge> edges,
        IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? previous = null,
        IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? pins = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        cancellationToken.ThrowIfCancellationRequested();
        // A worker never observes a list or pin map that a gesture or query can edit underneath it.
        var request = new GraphLayoutRequest(
            graphIdentity, [.. groups], [.. nodes], [.. edges],
            previous is null ? null : new Dictionary<ProcessInstanceId, GraphPoint>(previous),
            pins is null ? null : new Dictionary<ProcessInstanceId, GraphPoint>(pins));
        return Submit(graphIdentity, token => compute(request, token), result => result.GraphIdentity, cancellationToken);
    }

    /// <summary>
    /// Lays out a drawn graph (<see cref="GraphDisplay"/>) under the same supersession rule: a later request of either kind
    /// cancels this one, and only the latest complete result is returned.
    /// </summary>
    public Task<GraphDisplayLayout?> RequestDisplayAsync(
        string graphIdentity,
        GraphDisplay display,
        IReadOnlyDictionary<string, GraphPoint>? previous = null,
        IReadOnlyDictionary<string, GraphPoint>? pins = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        ArgumentNullException.ThrowIfNull(display);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GraphDisplayLayoutRequest(graphIdentity, display,
            previous is null ? null : new Dictionary<string, GraphPoint>(previous, StringComparer.Ordinal),
            pins is null ? null : new Dictionary<string, GraphPoint>(pins, StringComparer.Ordinal));
        return Submit(graphIdentity, token => computeDisplay(request, token), result => result.GraphIdentity, cancellationToken);
    }

    private Task<T?> Submit<T>(
        string graphIdentity,
        Func<CancellationToken, Task<T>> work,
        Func<T, string> identityOf,
        CancellationToken cancellationToken)
        where T : class
    {
        CancellationTokenSource source;
        CancellationTokenSource? obsolete;
        long accepted;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            obsolete = pending;
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pending = source;
            accepted = checked(++revision);
        }

        CancelQuietly(obsolete);
        return ExecuteAsync(graphIdentity, work, identityOf, source, accepted, cancellationToken);
    }

    private async Task<T?> ExecuteAsync<T>(
        string graphIdentity,
        Func<CancellationToken, Task<T>> work,
        Func<T, string> identityOf,
        CancellationTokenSource source,
        long accepted,
        CancellationToken callerToken)
        where T : class
    {
        try
        {
            T result = await work(source.Token).ConfigureAwait(false);
            if (!string.Equals(identityOf(result), graphIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Layout result {identityOf(result)} does not belong to requested graph {graphIdentity}.");
            }

            lock (gate)
            {
                return !disposed && accepted == revision && !source.IsCancellationRequested
                    ? result
                    : null;
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && source.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, source))
                {
                    pending = null;
                }
            }

            source.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancelled;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cancelled = pending;
            pending = null;
        }

        CancelQuietly(cancelled);
    }

    private static void CancelQuietly(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The completed worker owns disposal of its linked token source.
        }
    }
}
