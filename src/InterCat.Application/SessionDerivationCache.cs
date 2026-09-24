using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The process and relation derivations of one published generation, shared by every query that reads it. A manifest
/// digest pins each segment, source-field table and clock those derivations read, so the result one lease derived is
/// exactly what any later lease on the same manifest would derive. The overview, the channel pages and the evidence
/// pages of one generation then pay for the derivation once instead of once per page.
/// </summary>
/// <remarks>
/// The derived indexes hold no segment or file reference, so keeping them never delays retention. Only the few most
/// recently used generations are kept; a live capture moves to a new generation every publication.
/// </remarks>
internal static class SessionDerivationCache
{
    private const int Capacity = 4;
    private static readonly Lock Gate = new();
    private static readonly List<SessionDerivation> Recent = [];

    /// <summary>The shared derivation slot for one manifest, created on first use.</summary>
    public static SessionDerivation For(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (Gate)
        {
            int index = Recent.FindIndex(entry =>
                entry.SessionId == manifest.SessionId
                && string.Equals(entry.Digest, manifest.Digest, StringComparison.Ordinal));
            SessionDerivation entry;
            if (index >= 0)
            {
                entry = Recent[index];
                Recent.RemoveAt(index);
            }
            else
            {
                entry = new(manifest.SessionId, manifest.Digest);
            }

            Recent.Insert(0, entry);
            if (Recent.Count > Capacity)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }

            return entry;
        }
    }
}

/// <summary>
/// One generation's derivations. Each is computed at most once; a cancelled derivation stores nothing, so the next
/// caller derives again under its own token.
/// </summary>
internal sealed class SessionDerivation(Guid sessionId, string digest)
{
    private readonly Lock gate = new();
    private ProcessInstanceIndex? processes;
    private TransportRelationIndex? relations;

    public Guid SessionId { get; } = sessionId;

    public string Digest { get; } = digest;

    public ProcessInstanceIndex Processes(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return processes ??= ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
        }
    }

    public TransportRelationIndex Relations(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            processes ??= ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
            return relations ??= TransportRelationIndex.Derive(segments, processes, cancellationToken);
        }
    }
}
