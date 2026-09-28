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
                entry = new(manifest);
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
/// segments added since. Otherwise it is built from the derivation checkpoint the generation names, extended by the
/// segments the checkpoint does not cover (`contracts/derivation-checkpoint-v1.md`). Anything neither can add exactly -
/// a compaction, a retention, a late record that would change an earlier decision - is derived in full.
/// </summary>
internal sealed class SessionDerivation(SessionManifestV1 manifest)
{
    private readonly Lock gate = new();
    private ProcessInstanceIndex? processes;
    private TransportRelationIndex? relations;
    private ProcessActivityIndex? activity;
    private RpcCallIndex? rpcCalls;
    private RpcPeerIndex? rpcPeers;
    private DerivationCheckpoint? checkpoint;
    private bool checkpointRead;
    private string? checkpointProblem;
    private OverviewCounts? overview;
    private bool overviewRead;
    private string? overviewProblem;

    /// <summary>The generation these derivations are of.</summary>
    public SessionManifestV1 Manifest { get; } = manifest;

    public Guid SessionId => Manifest.SessionId;

    public string Digest => Manifest.Digest;

    public long Generation => Manifest.Generation;

    /// <summary>The instances, once derived; read by a later generation's derivation without waiting for this one.</summary>
    public ProcessInstanceIndex? DerivedProcesses => Volatile.Read(ref processes);

    /// <summary>The relations, once derived.</summary>
    public TransportRelationIndex? DerivedRelations => Volatile.Read(ref relations);

    /// <summary>Each instance's records, once counted.</summary>
    public ProcessActivityIndex? DerivedActivity => Volatile.Read(ref activity);

    /// <summary>Whether the counts were extended from an earlier generation's rather than counted in full.</summary>
    internal bool ActivityExtended { get; private set; }

    /// <summary>Whether the counts were taken from this generation's checkpoint, as they stand or extended.</summary>
    internal bool ActivityFromCheckpoint { get; private set; }

    /// <summary>
    /// Why the derivation checkpoint this generation names could not be read, once one was asked for; null when it names
    /// none, or it was read. The derivations are then made without it, and are the same.
    /// </summary>
    public string? CheckpointProblem => Volatile.Read(ref checkpointProblem);

    /// <summary>
    /// Why the persisted overview this generation names could not be read, once it was asked for; null when it names
    /// none, or it was read. The overview is then counted from the segments, and is the same.
    /// </summary>
    public string? OverviewProblem => Volatile.Read(ref overviewProblem);

    /// <summary>Whether the instances were extended from an earlier generation's rather than derived in full.</summary>
    internal bool ProcessesExtended { get; private set; }

    /// <summary>Whether the relations were extended from an earlier generation's rather than derived in full.</summary>
    internal bool RelationsExtended { get; private set; }

    /// <summary>Whether the instances were built from this generation's checkpoint, as it stands or extended.</summary>
    internal bool ProcessesFromCheckpoint { get; private set; }

    /// <summary>Whether the relations were built from this generation's checkpoint, as it stands or extended.</summary>
    internal bool RelationsFromCheckpoint { get; private set; }

    public ProcessInstanceIndex Processes(
        IOwnedDirectory directory,
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ProcessesLocked(directory, segments, clock, fields, cancellationToken);
        }
    }

    /// <summary>
    /// The derivations without opening a segment: those already made, or the checkpoint's when it covers exactly the
    /// segments the generation names. Null when neither is so, and the caller derives with the segments opened. The
    /// activity is null when it was neither counted nor kept, as by a checkpoint written before revision 166.
    /// </summary>
    public (ProcessInstanceIndex Processes, TransportRelationIndex Relations, ProcessActivityIndex? Activity)? FromCheckpoint(
        IOwnedDirectory directory,
        SourceClockDescriptor clock)
    {
        lock (gate)
        {
            if (processes is { } madeProcesses && relations is { } madeRelations)
            {
                return (madeProcesses, madeRelations, activity);
            }

            if (processes is not null
                || relations is not null
                || CheckpointLocked(directory, clock) is not { } saved
                || !saved.Covers(SessionOverviewIndex.ObservationSegments(Manifest), SessionOverviewIndex.FieldSegments(Manifest)))
            {
                return null;
            }

            Volatile.Write(ref processes, saved.Processes);
            Volatile.Write(ref relations, saved.Relations);
            (ProcessesFromCheckpoint, RelationsFromCheckpoint) = (true, true);
            if (saved.Activity is { } counted)
            {
                Volatile.Write(ref activity, counted);
                ActivityFromCheckpoint = true;
            }

            // Everything it holds is taken; counts it does not hold are made from the segments, not from it.
            checkpoint = null;
            return (saved.Processes, saved.Relations, saved.Activity);
        }
    }

    /// <summary>
    /// Each instance's records: those already counted, extended from an earlier generation's, taken from the checkpoint,
    /// or counted from the segments, in that order of preference, as the relations are.
    /// </summary>
    public ProcessActivityIndex Activity(
        IOwnedDirectory directory,
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (activity is { } known)
            {
                return known;
            }

            ProcessInstanceIndex instances = ProcessesLocked(directory, segments, clock, fields, cancellationToken);
            ProcessActivityIndex? extended =
                SessionDerivationCache.Earlier(this, entry => entry.DerivedActivity)?.Extend(segments, instances, cancellationToken);
            ActivityExtended = extended is not null;
            if (extended is null && CheckpointLocked(directory, clock) is { Activity: { } saved } kept)
            {
                extended = ReferenceEquals(instances, kept.Processes)
                    ? saved
                    : saved.Extend(segments, instances, cancellationToken);
                ActivityFromCheckpoint = extended is not null;
            }

            ProcessActivityIndex counted = extended ?? ProcessActivityIndex.Derive(segments, instances, cancellationToken);
            Volatile.Write(ref activity, counted);
            ReleaseCheckpointWhenDone();
            return counted;
        }
    }

    /// <summary>
    /// The generation's RPC calls, paired once on first use (`contracts/operations-v1.md`). They are neither checkpointed nor
    /// extended from an earlier generation's: a call still open in one generation may close in the next.
    /// </summary>
    public RpcCallIndex RpcCalls(
        IOwnedDirectory directory,
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (rpcCalls is { } known)
            {
                return known;
            }

            ProcessInstanceIndex instances = ProcessesLocked(directory, segments, clock, fields, cancellationToken);
            RpcCallIndex paired = RpcCallIndex.Derive(segments, fields, instances, clock, cancellationToken);
            Volatile.Write(ref rpcCalls, paired);
            return paired;
        }
    }

    /// <summary>
    /// The other ends of the generation's RPC calls (`contracts/operations-v1.md` §5c), followed once on first use over the
    /// calls <see cref="RpcCalls"/> paired, from the same segments in the same order.
    /// </summary>
    public RpcPeerIndex RpcPeers(
        IOwnedDirectory directory,
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1> fields,
        CancellationToken cancellationToken)
    {
        RpcCallIndex calls = RpcCalls(directory, segments, clock, fields, cancellationToken);
        lock (gate)
        {
            if (rpcPeers is { } known)
            {
                return known;
            }

            RpcPeerIndex followed = RpcPeerIndex.Derive(calls, segments, fields, cancellationToken);
            Volatile.Write(ref rpcPeers, followed);
            return followed;
        }
    }

    /// <summary>
    /// The counts this generation's persisted overview holds (`contracts/overview-index-v1.md`), read and checked once; null
    /// when it names none, when it covers other segments than the generation names, or when it could not be read, which
    /// <see cref="OverviewProblem"/> then says.
    /// </summary>
    public OverviewCounts? PersistedOverview(IOwnedDirectory directory)
    {
        lock (gate)
        {
            if (overviewRead)
            {
                return overview;
            }

            overviewRead = true;
            try
            {
                if (SessionOverviewIndex.NamedBy(Manifest) is { } named)
                {
                    (OverviewCounts counts, IReadOnlyList<StoreDependency> covered) = SessionOverviewIndex.Read(
                        SessionSegments.ReadVerified(directory, named, SessionOverviewIndex.MaximumBytes), Manifest.SessionId);
                    overview = SessionOverviewIndex.Covers(covered, Manifest) ? counts : null;
                }
            }
            catch (InvalidDataException exception)
            {
                Volatile.Write(ref overviewProblem, exception.Message);
            }
            catch (IOException exception)
            {
                Volatile.Write(ref overviewProblem, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Volatile.Write(ref overviewProblem, exception.Message);
            }

            return overview;
        }
    }

    public TransportRelationIndex Relations(
        IOwnedDirectory directory,
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

            ProcessInstanceIndex instances = ProcessesLocked(directory, segments, clock, fields, cancellationToken);
            TransportRelationIndex? extended =
                SessionDerivationCache.Earlier(this, entry => entry.DerivedRelations)?.Extend(segments, instances, cancellationToken);
            RelationsExtended = extended is not null;
            if (extended is null && CheckpointLocked(directory, clock) is { } saved)
            {
                // Instances taken from the checkpoint as they stand are the ones its relations' holders name; any other
                // instances map those holders onto their own, or decline.
                extended = ReferenceEquals(instances, saved.Processes)
                    ? saved.Relations
                    : saved.Relations.Extend(segments, instances, cancellationToken);
                RelationsFromCheckpoint = extended is not null;
            }

            TransportRelationIndex derived = extended ?? TransportRelationIndex.Derive(segments, instances, cancellationToken);
            Volatile.Write(ref relations, derived);
            ReleaseCheckpointWhenDone();
            return derived;
        }
    }

    /// <summary>
    /// Once every derivation is made the checkpoint has nothing left to give, so it is let go; what was taken from it
    /// stays. The caller holds the gate.
    /// </summary>
    private void ReleaseCheckpointWhenDone()
    {
        if (processes is not null && relations is not null && activity is not null)
        {
            checkpoint = null;
        }
    }

    private ProcessInstanceIndex ProcessesLocked(
        IOwnedDirectory directory,
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
        if (extended is null && CheckpointLocked(directory, clock) is { } saved)
        {
            extended = saved.Covers(segments, fields)
                ? saved.Processes
                : saved.Processes.Extend(segments, clock, fields, cancellationToken);
            ProcessesFromCheckpoint = extended is not null;
        }

        ProcessInstanceIndex derived = extended ?? ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
        Volatile.Write(ref processes, derived);
        return derived;
    }

    /// <summary>
    /// The checkpoint this generation names, read and checked on first use; null when it names none, when it could not
    /// be read (<see cref="CheckpointProblem"/> says why), and once both derivations are made.
    /// </summary>
    private DerivationCheckpoint? CheckpointLocked(IOwnedDirectory directory, SourceClockDescriptor clock)
    {
        if (checkpointRead)
        {
            return checkpoint;
        }

        // A checkpoint saves time and never changes an answer, so one that cannot be read is set aside, and why is kept
        // for the overview to say (derivation-checkpoint-v1 §4). The caller's lease holds the file while it is read.
        try
        {
            if (DerivationCheckpoint.NamedBy(Manifest) is { } named)
            {
                checkpoint = DerivationCheckpoint.Read(
                    SessionSegments.ReadVerified(directory, named, DerivationCheckpoint.MaximumBytes),
                    Manifest.SessionId,
                    clock);
            }
        }
        catch (InvalidDataException exception)
        {
            Volatile.Write(ref checkpointProblem, exception.Message);
        }
        catch (IOException exception)
        {
            Volatile.Write(ref checkpointProblem, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            Volatile.Write(ref checkpointProblem, exception.Message);
        }

        checkpointRead = true;
        return checkpoint;
    }
}
