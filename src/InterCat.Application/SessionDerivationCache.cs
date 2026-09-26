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
/// recently used generations are kept; a live capture moves to a new generation every publication, and a generation's
/// derivations extend those of the latest earlier generation of the session that has them (IC-015).
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
                entry = new(manifest.SessionId, manifest.Digest, manifest.Generation);
            }

            Recent.Insert(0, entry);
            if (Recent.Count > Capacity)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }

            return entry;
        }
    }

    /// <summary>Forgets every kept derivation, so the next query derives in full; a test compares that with an extension.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Recent.Clear();
        }
    }

    /// <summary>
    /// The latest derivation of an earlier generation of the same session that <paramref name="pick"/> finds a result
    /// in, for a later generation to extend; null when none is kept.
    /// </summary>
    public static T? Earlier<T>(SessionDerivation later, Func<SessionDerivation, T?> pick)
        where T : class
    {
        lock (Gate)
        {
            T? found = null;
            long generation = long.MinValue;
            foreach (SessionDerivation entry in Recent)
            {
                if (entry.SessionId == later.SessionId
                    && entry.Generation < later.Generation
                    && entry.Generation > generation
                    && pick(entry) is { } result)
                {
                    found = result;
                    generation = entry.Generation;
                }
            }

            return found;
        }
    }
}

/// <summary>
/// One generation's derivations. Each is computed at most once; a cancelled derivation stores nothing, so the next
/// caller derives again under its own token. A derivation extends the latest earlier generation's when that one is
/// kept and every segment it read is still named: exactly the result a full derivation gives, for the cost of the
/// segments added since. Anything an extension cannot add exactly - a compaction, a retention, a late record that would
/// change an earlier decision - is derived in full.
/// </summary>
internal sealed class SessionDerivation(Guid sessionId, string digest, long generation)
{
    private readonly Lock gate = new();
    private ProcessInstanceIndex? processes;
    private TransportRelationIndex? relations;

    public Guid SessionId { get; } = sessionId;

    public string Digest { get; } = digest;

    public long Generation { get; } = generation;

    /// <summary>The instances, once derived; read by a later generation's derivation without waiting for this one.</summary>
    public ProcessInstanceIndex? DerivedProcesses => Volatile.Read(ref processes);

    /// <summary>The relations, once derived.</summary>
    public TransportRelationIndex? DerivedRelations => Volatile.Read(ref relations);

    /// <summary>Whether the instances were extended from an earlier generation's rather than derived in full.</summary>
    internal bool ProcessesExtended { get; private set; }

    /// <summary>Whether the relations were extended from an earlier generation's rather than derived in full.</summary>
    internal bool RelationsExtended { get; private set; }

    public ProcessInstanceIndex Processes(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ProcessesLocked(segments, clock, fields, cancellationToken);
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
            if (relations is { } known)
            {
                return known;
            }

            ProcessInstanceIndex instances = ProcessesLocked(segments, clock, fields, cancellationToken);
            TransportRelationIndex? extended =
                SessionDerivationCache.Earlier(this, entry => entry.DerivedRelations)?.Extend(segments, instances, cancellationToken);
            RelationsExtended = extended is not null;
            TransportRelationIndex derived = extended ?? TransportRelationIndex.Derive(segments, instances, cancellationToken);
            Volatile.Write(ref relations, derived);
            return derived;
        }
    }

    private ProcessInstanceIndex ProcessesLocked(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        if (processes is { } known)
        {
            return known;
        }

        ProcessInstanceIndex? extended =
            SessionDerivationCache.Earlier(this, entry => entry.DerivedProcesses is { } earlier && earlier.Clock == clock.Id ? earlier : null)
                ?.Extend(segments, clock, fields, cancellationToken);
        ProcessesExtended = extended is not null;
        ProcessInstanceIndex derived = extended ?? ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
        Volatile.Write(ref processes, derived);
        return derived;
    }
}
