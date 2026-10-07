using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One leased generation's segments, opened only when first asked for, and its instances and channels, taken without
/// opening one wherever the derivation already made them or the generation's checkpoint holds them (§12.1 S1). A query of
/// an interval then opens only the segments the interval meets (P25); a generation neither holds is derived from every
/// segment, as before.
/// </summary>
internal sealed class GenerationSegments(SessionStore store, SessionManifestV1 manifest, SourceClockDescriptor clock)
{
    private SegmentReaderV1[]? segments;
    private SegmentReaderV1[]? fields;
    private bool checkpointRead;
    private (ProcessInstanceIndex Processes, TransportRelationIndex Relations, ProcessActivityIndex? Activity)? saved;

    public SessionDerivation Derivation { get; } = SessionDerivationCache.For(manifest);

    /// <summary>Every observation segment the generation names, opened on first use.</summary>
    public SegmentReaderV1[] All => segments ??=
        [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];

    /// <summary>Every source-field segment the generation names, opened on first use.</summary>
    public SegmentReaderV1[] Fields => fields ??=
        [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];

    public ProcessInstanceIndex Processes(CancellationToken cancellationToken) =>
        Derivation.DerivedProcesses
        ?? Saved()?.Processes
        ?? Derivation.Processes(store.Root, All, clock, Fields, cancellationToken);

    public TransportRelationIndex Relations(CancellationToken cancellationToken) =>
        Derivation.DerivedRelations
        ?? Saved()?.Relations
        ?? Derivation.Relations(store.Root, All, clock, Fields, cancellationToken);

    /// <summary>
    /// The other ends of the RPC calls: followed once per generation over every segment, since a call's request and its
    /// response can lie in different ones, and then read without opening one.
    /// </summary>
    public RpcPeerIndex RpcPeers(CancellationToken cancellationToken) =>
        Derivation.DerivedRpcPeers ?? Derivation.RpcPeers(store.Root, All, clock, Fields, cancellationToken);

    /// <summary>The checkpoint's derivations, read once; null when it names none, or not every segment, or one is made.</summary>
    private (ProcessInstanceIndex Processes, TransportRelationIndex Relations, ProcessActivityIndex? Activity)? Saved()
    {
        if (!checkpointRead)
        {
            saved = Derivation.FromCheckpoint(store.Root, clock);
            checkpointRead = true;
        }

        return saved;
    }
}
