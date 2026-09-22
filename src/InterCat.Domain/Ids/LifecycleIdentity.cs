using System.Globalization;

namespace InterCat.Domain;

public enum ProcessIdentityEvidenceKind
{
    ProviderStartKey = 1,
    ObservedCreationTime = 2,
    ProvisionalInventoryWitness = 3,
}

public readonly record struct ProcessStartKey(ulong SequenceNumber, long? CreateFileTime);

/// <summary>
/// Canonical process identity input. Every factory requires host and boot scope plus evidence that is
/// stronger than a PID, so the type cannot represent a PID-only identity (P6, I12).
/// </summary>
public sealed record ProcessInstanceKey
{
    private ProcessInstanceKey(
        HostId hostId,
        BootId bootId,
        int processId,
        uint lifecycleEpoch,
        ProcessIdentityEvidenceKind evidenceKind,
        ProcessStartKey? providerStartKey,
        long? observedCreationTime,
        RawRecordId? provisionalWitness)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        HostId = hostId;
        BootId = bootId;
        ProcessId = processId;
        LifecycleEpoch = lifecycleEpoch;
        EvidenceKind = evidenceKind;
        ProviderStartKey = providerStartKey;
        ObservedCreationTime = observedCreationTime;
        ProvisionalWitness = provisionalWitness;
    }

    public HostId HostId { get; }
    public BootId BootId { get; }
    public int ProcessId { get; }
    public uint LifecycleEpoch { get; }
    public ProcessIdentityEvidenceKind EvidenceKind { get; }
    public ProcessStartKey? ProviderStartKey { get; }
    public long? ObservedCreationTime { get; }
    public RawRecordId? ProvisionalWitness { get; }

    public string CanonicalForm
    {
        get
        {
            string start = ProviderStartKey is { } key
                ? string.Create(CultureInfo.InvariantCulture, $"{key.SequenceNumber}:{key.CreateFileTime?.ToString(CultureInfo.InvariantCulture) ?? "-"}")
                : "-";
            string observed = ObservedCreationTime?.ToString(CultureInfo.InvariantCulture) ?? "-";
            string witness = ProvisionalWitness?.CanonicalForm ?? "-";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"intercat.process.v1|{HostId}|{BootId}|{ProcessId}|{LifecycleEpoch}|{(int)EvidenceKind}|{start}|{observed}|{witness}");
        }
    }

    public static ProcessInstanceKey FromProviderStart(
        HostId hostId,
        BootId bootId,
        int processId,
        uint lifecycleEpoch,
        ProcessStartKey startKey) =>
        new(hostId, bootId, processId, lifecycleEpoch, ProcessIdentityEvidenceKind.ProviderStartKey, startKey, null, null);

    public static ProcessInstanceKey FromObservedCreation(
        HostId hostId,
        BootId bootId,
        int processId,
        uint lifecycleEpoch,
        long observedCreationTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedCreationTime);
        return new(
            hostId,
            bootId,
            processId,
            lifecycleEpoch,
            ProcessIdentityEvidenceKind.ObservedCreationTime,
            null,
            observedCreationTime,
            null);
    }

    public static ProcessInstanceKey FromInventoryWitness(
        HostId hostId,
        BootId bootId,
        int processId,
        uint lifecycleEpoch,
        RawRecordId inventoryWitness) =>
        new(
            hostId,
            bootId,
            processId,
            lifecycleEpoch,
            ProcessIdentityEvidenceKind.ProvisionalInventoryWitness,
            null,
            null,
            inventoryWitness);
}

/// <summary>A source-scoped resource key. Equal names are intentionally absent from this identity.</summary>
public sealed record ResourceInstanceKey
{
    private ResourceInstanceKey(
        HostId hostId,
        BootId bootId,
        Mechanism mechanism,
        ulong sourceInstanceKey,
        uint lifecycleEpoch)
    {
        if (sourceInstanceKey == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceInstanceKey), "An unavailable source key is not the value zero.");
        }

        HostId = hostId;
        BootId = bootId;
        Mechanism = mechanism;
        SourceInstanceKey = sourceInstanceKey;
        LifecycleEpoch = lifecycleEpoch;
    }

    public HostId HostId { get; }
    public BootId BootId { get; }
    public Mechanism Mechanism { get; }
    public ulong SourceInstanceKey { get; }
    public uint LifecycleEpoch { get; }

    public string CanonicalForm => string.Create(
        CultureInfo.InvariantCulture,
        $"intercat.resource.v1|{HostId}|{BootId}|{(int)Mechanism}|{SourceInstanceKey}|{LifecycleEpoch}");

    public static ResourceInstanceKey FromSourceInstance(
        HostId hostId,
        BootId bootId,
        Mechanism mechanism,
        ulong sourceInstanceKey,
        uint lifecycleEpoch) =>
        new(hostId, bootId, mechanism, sourceInstanceKey, lifecycleEpoch);
}

public readonly record struct LifecycleInterval
{
    public LifecycleInterval(long startInclusive, long? endExclusive)
    {
        if (endExclusive is not null && endExclusive <= startInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(endExclusive), "A lifecycle end must be after its start.");
        }

        StartInclusive = startInclusive;
        EndExclusive = endExclusive;
    }

    public long StartInclusive { get; }
    public long? EndExclusive { get; }

    public bool Contains(long value) =>
        value >= StartInclusive && (EndExclusive is null || value < EndExclusive.Value);
}

public sealed record ProcessInstanceEpoch
{
    public ProcessInstanceEpoch(ProcessInstanceKey key, LifecycleInterval witnessedLifetime)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        Id = ProcessInstanceId.FromKey(key);
        WitnessedLifetime = witnessedLifetime;
    }

    public ProcessInstanceKey Key { get; }
    public ProcessInstanceId Id { get; }
    public LifecycleInterval WitnessedLifetime { get; }
}

public enum ProcessEpochResolutionKind
{
    Unresolved = 0,
    ProviderStartKey = 1,
    UniqueLifecycle = 2,
    AmbiguousReuse = 3,

    /// <summary>
    /// The PID has several epochs and the record lies in the earliest one's lifetime. Only that epoch, or a holder
    /// the capture never witnessed, can have made it - the same risk a PID with one epoch carries - so it resolves.
    /// </summary>
    EarliestLifecycle = 4,
}

public sealed record ProcessEpochResolution(
    ProcessEpochResolutionKind Kind,
    ProcessInstanceId? ProcessInstanceId,
    string Explanation)
{
    public bool IsResolved => ProcessInstanceId is not null;
}

public enum ProcessAliasReason
{
    InventoryReconciledWithProviderStart = 1,
    StrongerCreationEvidence = 2,
}

/// <summary>
/// A versioned derived redirect from a provisional identity to a better-supported identity. The old
/// identity and every source observation remain addressable.
/// </summary>
public sealed record ProcessAliasRevision
{
    public ProcessAliasRevision(
        ProcessInstanceId aliasId,
        ProcessInstanceId canonicalId,
        uint revision,
        ObservationId evidenceId,
        ProcessAliasReason reason)
    {
        if (aliasId == canonicalId)
        {
            throw new ArgumentException("An alias revision must redirect between two distinct identities.");
        }

        ArgumentOutOfRangeException.ThrowIfZero(revision);
        AliasId = aliasId;
        CanonicalId = canonicalId;
        Revision = revision;
        EvidenceId = evidenceId;
        Reason = reason;
    }

    public ProcessInstanceId AliasId { get; }
    public ProcessInstanceId CanonicalId { get; }
    public uint Revision { get; }
    public ObservationId EvidenceId { get; }
    public ProcessAliasReason Reason { get; }
}

public static class ProcessEpochResolver
{
    public static ProcessEpochResolution Resolve(
        IEnumerable<ProcessInstanceEpoch> epochs,
        HostId hostId,
        BootId bootId,
        int processId,
        long localRelativeTime,
        ProcessStartKey? providerStartKey)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        ProcessInstanceEpoch[] scoped =
        [
            .. epochs.Where(epoch =>
                epoch.Key.HostId == hostId
                && epoch.Key.BootId == bootId
                && epoch.Key.ProcessId == processId),
        ];

        if (providerStartKey is { } startKey)
        {
            ProcessInstanceEpoch[] exact = [.. scoped.Where(epoch => epoch.Key.ProviderStartKey == startKey)];
            return exact.Length == 1
                ? new(ProcessEpochResolutionKind.ProviderStartKey, exact[0].Id, "Matched the provider start key; delivery time does not override lifecycle identity.")
                : new(ProcessEpochResolutionKind.Unresolved, null, "The provider start key did not identify exactly one process epoch.");
        }

        ProcessInstanceEpoch[] active = [.. scoped.Where(epoch => epoch.WitnessedLifetime.Contains(localRelativeTime))];
        if (scoped.Length == 1 && active.Length == 1)
        {
            return new(
                ProcessEpochResolutionKind.UniqueLifecycle,
                active[0].Id,
                "A single witnessed lifecycle contains the record time.");
        }

        // With several epochs, only a record in the earliest lifetime is as safe as a record of a PID with one: a
        // record in a later lifetime may be a late record of an earlier epoch, which PID and time cannot rule out.
        if (active.Length == 1
            && scoped.All(epoch => epoch.WitnessedLifetime.StartInclusive >= active[0].WitnessedLifetime.StartInclusive))
        {
            return new(
                ProcessEpochResolutionKind.EarliestLifecycle,
                active[0].Id,
                "The earliest of the PID's witnessed lifecycles contains the record time, and no witnessed epoch "
                + "precedes it.");
        }

        return new(
            scoped.Length > 1 ? ProcessEpochResolutionKind.AmbiguousReuse : ProcessEpochResolutionKind.Unresolved,
            null,
            scoped.Length > 1
                ? "The PID has multiple epochs and the record lies after the first; PID and time alone cannot tell a "
                    + "late record of an earlier epoch from a record of a later one."
                : "No witnessed lifecycle contains the record time.");
    }
}
