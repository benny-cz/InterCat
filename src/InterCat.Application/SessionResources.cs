using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A session's resource instances (`contracts/resources-v1.md`, ADR-097) - each named synchronization object and each
/// section by its handles, from its first record to its last, named as its records name it - with the process instances
/// their holders are and the clock their readings are placed by, derived over the leased generation's every segment.
/// </summary>
/// <param name="Instances">Every instance, by mechanism and then by number.</param>
public sealed record SessionResources(
    Guid SessionId,
    long Generation,
    SourceClockDescriptor Clock,
    ProcessInstanceIndex Processes,
    IReadOnlyList<ResourceInstance> Instances)
{
    /// <summary>
    /// The resource instances of <paramref name="store"/>'s published generation, their records bound to its process
    /// instances as <paramref name="policy"/> admits.
    /// </summary>
    public static SessionResources Of(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its processes cannot be identified.");
        var generation = new GenerationSegments(store, manifest, clock);
        ProcessInstanceIndex processes = generation.Processes(cancellationToken);
        ResourceInstanceIndex resources = ResourceInstanceIndex.Derive(
            generation.OnDemand, generation.FieldsOnDemand, processes, policy, cancellationToken);
        return new(manifest.SessionId, manifest.Generation, clock, processes, resources.Instances);
    }

    /// <summary>
    /// The records of the resource a hub's key names (ADR-099) - those its gate numbered it in, whichever process they bind
    /// to - by segment and row, which E lists and the timeline highlights; null where none is in this generation. Gathered
    /// once per key and kept with the generation, as an RPC channel's records are (P25).
    /// </summary>
    internal static OperationRecords? RecordsOf(
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        string resourceKey,
        EvidencePolicy policy,
        CancellationToken cancellationToken) =>
        SessionDerivationCache.For(manifest).Operation(resourceKey, policy, () =>
        {
            if (!ResourceLink.TryParseKey(resourceKey, out Mechanism mechanism, out bool byViews, out long number))
            {
                throw new ArgumentException("This key names no resource hub.", nameof(resourceKey));
            }

            IReadOnlyList<string> names = SessionSegments.Names(manifest);
            OperationRecords records = OperationRecords.Of(
                ResourceInstanceIndex.RecordsOf(segments, fieldSegments, mechanism, byViews, number, cancellationToken)
                    .Select(record => (names[record.Segment], record.Row)));
            return records.Count == 0 ? null : records;
        });
}
