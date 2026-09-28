using InterCat.Analysis;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A session's current generation's derivation as the metric engine takes it (<see cref="MetricDerivations"/>): its
/// process instances and transport relations from its checkpoint, or derived once and kept, as the overview has them, and
/// its RPC calls paired on first use. `icat metric` answers with it rather than deriving them from every segment for each
/// query, which on a finished 1M-row session was most of an answer's time.
/// </summary>
public static class SessionMetricDerivations
{
    /// <summary>The current generation's derivation; null for a session with no generation or no source clock.</summary>
    public static MetricDerivations? For(SessionStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Current is null)
        {
            return null;
        }

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
        {
            return null;
        }

        // A checkpoint that covers the generation's segments gives the instances without opening one; the relations and
        // calls are asked for only by an answer that needs them, which holds its own lease on this same generation.
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        SegmentReaderV1[]? opened = null;
        SegmentReaderV1[]? openedFields = null;
        ProcessInstanceIndex processes = derivation.FromCheckpoint(store.Root, clock)?.Processes
            ?? derivation.Processes(store.Root, Segments(), clock, Fields(), cancellationToken);
        return new(
            manifest.SessionId,
            manifest.Generation,
            manifest.Digest,
            processes,
            cancellation => derivation.Relations(store.Root, Segments(), clock, Fields(), cancellation),
            cancellation => derivation.RpcCalls(store.Root, Segments(), clock, Fields(), cancellation))
        {
            Peers = cancellation => derivation.RpcPeers(store.Root, Segments(), clock, Fields(), cancellation),
        };

        SegmentReaderV1[] Segments() => opened ??=
            [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];

        SegmentReaderV1[] Fields() => openedFields ??=
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
    }
}
