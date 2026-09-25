using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>What evidence an instance's existence rests on. It decides the key the instance is identified by.</summary>
public enum ProcessWitness
{
    /// <summary>The capture witnessed the process being created.</summary>
    Created = 1,

    /// <summary>A capture-state rundown witnessed the process as already running. Presence, not creation (§18.5).</summary>
    Rundown = 2,

    /// <summary>The capture witnessed only the process's exit: it was running before any other evidence of it.</summary>
    ExitOnly = 3,

    /// <summary>
    /// No lifecycle record names the process at all; records attributed to its PID are the only witness that it
    /// existed. It is a provisional instance, never an invented start (§7.2).
    /// </summary>
    ActivityOnly = 4,
}

/// <summary>What is unusual about how an instance's lifetime was bounded. Each flag is a gap in the evidence.</summary>
[Flags]
public enum ProcessEvidenceGaps
{
    None = 0,

    /// <summary>
    /// The instance ended with no exit witnessed: the next instance of the PID was created, or a lifecycle record
    /// carrying another start key arrived, while this one was still live.
    /// </summary>
    ExitNotWitnessed = 1 << 0,

    /// <summary>The instance follows an earlier instance of its PID with no creation witnessed between them.</summary>
    CreationNotWitnessed = 1 << 1,
}

/// <summary>Why a record could not be bound to a process instance.</summary>
public enum ProcessBindingReason
{
    /// <summary>It was bound.</summary>
    Bound = 0,

    /// <summary>
    /// The record's payload names no owner. The event header's process is context and is never promoted into an
    /// owner (§4.1), so a record without a payload owner has no process to bind to.
    /// </summary>
    NoOwner = 1,

    /// <summary>
    /// The record names its PID before the first lifecycle evidence for that PID, and no exit separates it from that
    /// evidence: it belongs to an earlier holder the capture has no lifecycle record of.
    /// </summary>
    BeforeFirstEvidence = 2,

    /// <summary>Between one instance's witnessed exit and the next one's creation, no process held the PID.</summary>
    BetweenInstances = 3,

    /// <summary>After the PID's last instance exited, no process held it.</summary>
    AfterExit = 4,

    /// <summary>
    /// The record lies in the lifetime of a later instance of a PID this capture reused, so it could also be a late
    /// record of the earlier instance: the binding is a candidate, and the evidence policy asked for did not admit it.
    /// </summary>
    NotAdmittedByPolicy = 5,

    /// <summary>The process binds, but no lifecycle record supplies an executable name for grouping.</summary>
    ExecutableUnknown = 6,

    /// <summary>
    /// The contribution belongs to the process at the record's other end, and the record does not carry a complete
    /// endpoint pair to find that end by (`relations-v1`).
    /// </summary>
    PeerEndpointIncomplete = 7,

    /// <summary>
    /// No record in this capture holds the other end of the record's connection: the peer is remote, or it is local
    /// and the capture holds none of its records. Nothing is inferred from the address alone.
    /// </summary>
    PeerNotObserved = 8,

    /// <summary>
    /// The records at the other end name more than one process, or bind to more than one instance, so which one was
    /// the peer is not decided. A reused port is never resolved by picking the nearest holder.
    /// </summary>
    PeerAmbiguous = 9,

    /// <summary>The other end's records name no process that binds to an instance.</summary>
    PeerUnbound = 10,

    /// <summary>No relation rule covers the record's mechanism, so its other end is unknown rather than guessed.</summary>
    NoRelationRule = 11,
}

/// <summary>
/// How one record binds to a process instance, and how strongly (§7.3's <c>EntityBindingRevision</c>, `EN-RelationStrength`).
/// </summary>
public readonly record struct ProcessBinding(int Instance, RelationStrength Strength, ProcessBindingReason Reason)
{
    public bool IsBound => Instance >= 0;

    public static ProcessBinding Unresolved(ProcessBindingReason reason) => new(-1, RelationStrength.Unresolved, reason);

    /// <summary>
    /// Whether an evidence policy admits this binding. A candidate is admitted only when candidates are asked for, so
    /// the default never attributes a record to one of two instances that shared its PID (identity-v1, P6).
    /// </summary>
    public bool IsAdmittedUnder(EvidencePolicy policy) => IsBound && Strength switch
    {
        RelationStrength.Direct => true,
        RelationStrength.Correlated => policy >= EvidencePolicy.IncludeCorrelated,
        RelationStrength.Candidate => policy >= EvidencePolicy.IncludeCandidates,
        RelationStrength.Conflicting => policy >= EvidencePolicy.AllIncludingConflicting,
        _ => false,
    };
}

/// <summary>
/// One process instance: a PID held by one process for one lifetime, identified by host, boot, PID, lifecycle epoch
/// and the evidence the instance rests on — never by the PID alone (R22, I12, identity-v1).
/// </summary>
public sealed record ProcessInstance
{
    public required ProcessInstanceId Id { get; init; }

    public required ProcessInstanceKey Key { get; init; }

    public int ProcessId => Key.ProcessId;

    /// <summary>Which instance of its PID this is, in the order the capture witnessed them, from 1.</summary>
    public uint LifecycleEpoch => Key.LifecycleEpoch;

    public required ProcessWitness Witness { get; init; }

    public required ProcessEvidenceGaps Gaps { get; init; }

    /// <summary>The provider's start key, when a lifecycle record of the instance carried one.</summary>
    public ProcessStartKey? StartKey { get; init; }

    /// <summary>The native reading of the witnessed creation, or null when no creation was witnessed.</summary>
    public long? CreatedNativeTicks { get; init; }

    /// <summary>The native reading of the witnessed exit, or null when no exit was witnessed.</summary>
    public long? ExitedNativeTicks { get; init; }

    /// <summary>The exit code the source reported with the exit, or null when there was none.</summary>
    public long? ExitCode { get; init; }

    /// <summary>
    /// The earliest reading the instance can hold, inclusive: its creation, or the end of the instance of its PID
    /// before it. Null means unbounded — it was running before any evidence of it.
    /// </summary>
    public long? LifetimeStartNativeTicks { get; init; }

    /// <summary>
    /// The first reading after the instance, exclusive: one tick after its exit, or the next instance's creation.
    /// Null means it was still running at the last evidence of it.
    /// </summary>
    public long? LifetimeEndNativeTicks { get; init; }

    /// <summary>The image path a creation or rundown record carried, as delivered.</summary>
    public string? ImagePath { get; init; }

    /// <summary>The image file name: the last component of <see cref="ImagePath"/>, or the name an exit carried.</summary>
    public string? ImageName { get; init; }

    /// <summary>The terminal session the process ran in, when its lifecycle records said.</summary>
    public uint? SessionId { get; init; }

    /// <summary>The creation time the source reported, a FILETIME. A wall-clock claim of the source, never our clock.</summary>
    public long? CreateFileTime { get; init; }

    /// <summary>The exit time the source reported, a FILETIME.</summary>
    public long? ExitFileTime { get; init; }

    /// <summary>The PID of the process that created this one, as its creation or rundown record named it.</summary>
    public int? ParentProcessId { get; init; }

    /// <summary>The start sequence number of the parent, as the creation or rundown record named it.</summary>
    public ulong? ParentStartSequence { get; init; }

    /// <summary>The parent instance, when the capture holds it; null when the parent is outside the capture.</summary>
    public ProcessInstanceId? Parent { get; init; }

    /// <summary>How strongly <see cref="Parent"/> is established: by start key, or by PID and time.</summary>
    public RelationStrength? ParentBinding { get; init; }

    /// <summary>The record the instance's key names, or that witnessed its creation.</summary>
    public required ObservationId WitnessRecord { get; init; }

    public ObservationId? CreationRecord { get; init; }

    public ObservationId? ExitRecord { get; init; }

    public ObservationId? RundownRecord { get; init; }

    public bool Contains(long nativeTicks) =>
        (LifetimeStartNativeTicks is not { } start || nativeTicks >= start)
        && (LifetimeEndNativeTicks is not { } end || nativeTicks < end);
}

/// <summary>
/// The process instances one capture's evidence supports, and the rule that binds a record to one of them. It is a
/// derivation over published segments alone, so it is rebuildable from retained evidence (R20) and changes no
/// observation (R1); its identity is <see cref="BindingRule"/> and the generation it was derived from.
/// </summary>
/// <remarks>
/// <para>
/// Instances are built from process lifecycle records — creation, exit and capture-state rundown — per PID, in the
/// order of their native readings. When the records carry the provider's start key, an instance is keyed by it and
/// a lifecycle record whose key differs from the live instance's belongs to another instance; otherwise an instance
/// is keyed by its observed creation or by the record that witnesses it. A PID that no lifecycle record names but
/// that records are attributed to gets one provisional instance witnessed by the first of those records: evidence
/// that it existed, never an invented start.
/// </para>
/// <para>
/// A record binds by where its native reading falls. A lifecycle record binds <see cref="RelationStrength.Direct"/>ly
/// to the instance it creates, ends or confirms. Any other record whose payload names a PID binds to the instance
/// whose lifetime contains its reading: <see cref="RelationStrength.Correlated"/> when that is the PID's first
/// instance in the capture, and <see cref="RelationStrength.Candidate"/> when it is a later one, because a late record
/// of the earlier instance cannot be told apart from a record of the later one by PID and time (identity-v1). A
/// reading no lifetime contains is unresolved, with the reason.
/// </para>
/// <para>
/// The rule assumes the capture lost no lifecycle record that carries no start key. A lost exit and creation pair of
/// such records would merge two instances, and no session publishes a coverage ledger yet to say whether one was lost.
/// </para>
/// </remarks>
public sealed class ProcessInstanceIndex
{
    /// <summary>
    /// The binding rule's identity. A change to what binds, how strongly, or how an instance is keyed is a new rule
    /// (§24 entityRevision). Version 2 keys instances by the provider start key when the evidence carries one, splits
    /// an instance at a lifecycle record whose start key contradicts it, and links parents.
    /// </summary>
    public const string BindingRule = "process-binding-v2";

    private readonly Dictionary<int, int[]> instancesByPid;
    private readonly Dictionary<ObservationId, int> lifecycleBindings;

    private ProcessInstanceIndex(
        IReadOnlyList<ProcessInstance> instances,
        Dictionary<int, int[]> instancesByPid,
        Dictionary<ObservationId, int> lifecycleBindings,
        HostId host,
        BootId boot,
        ClockId clock,
        long lifecycleRecords,
        long lifecycleRecordsWithoutOwner,
        bool startKeysAvailable)
    {
        Instances = instances;
        this.instancesByPid = instancesByPid;
        this.lifecycleBindings = lifecycleBindings;
        Host = host;
        Boot = boot;
        Clock = clock;
        LifecycleRecords = lifecycleRecords;
        LifecycleRecordsWithoutOwner = lifecycleRecordsWithoutOwner;
        StartKeysAvailable = startKeysAvailable;
    }

    /// <summary>Every instance, ordered by PID and then by lifecycle epoch.</summary>
    public IReadOnlyList<ProcessInstance> Instances { get; }

    public HostId Host { get; }

    /// <summary>The boot the capture's clock scopes. See <see cref="BootId.DeriveFromClock"/>.</summary>
    public BootId Boot { get; }

    public ClockId Clock { get; }

    /// <summary>How many process lifecycle records the evidence holds.</summary>
    public long LifecycleRecords { get; }

    /// <summary>Lifecycle records whose payload named no process, which therefore witness no instance.</summary>
    public long LifecycleRecordsWithoutOwner { get; }

    /// <summary>
    /// Whether the derivation had the source fields that carry start keys, parents and sessions. A session derived
    /// before `source-fields-v1` existed has none, and its instances are keyed by observed creation instead.
    /// </summary>
    public bool StartKeysAvailable { get; }

    /// <summary>How many instances the PID had in the capture. Two or more means it was reused.</summary>
    public int InstancesOf(int processId) =>
        instancesByPid.TryGetValue(processId, out int[]? indexes) ? indexes.Length : 0;

    public ProcessInstance? Find(ProcessInstanceId id)
    {
        foreach (ProcessInstance instance in Instances)
        {
            if (instance.Id == id)
            {
                return instance;
            }
        }

        return null;
    }

    /// <summary>
    /// The canonical owner binding for each normalized row of a segment. This uses the same lifecycle-exact
    /// and reading-time rules as metric process filters; callers must not select rows by a reused numeric PID.
    /// </summary>
    public ProcessBinding[] OwnersOf(SegmentReaderV1 segment) => ProcessRoles.OwnersOf(this, segment);

    /// <summary>
    /// Binds one record, given the owner its payload names, its native reading and whether it is a process
    /// lifecycle record. Pure: the same inputs bind the same way every time.
    /// </summary>
    public ProcessBinding Bind(int? ownerProcessId, long nativeTicks, bool isLifecycleRecord, ObservationId? observationId = null)
    {
        if (ownerProcessId is not { } processId)
        {
            return ProcessBinding.Unresolved(ProcessBindingReason.NoOwner);
        }

        if (!instancesByPid.TryGetValue(processId, out int[]? indexes))
        {
            // Every PID a record names has at least an activity-witnessed instance, so this is a record the index
            // was not derived from. It is refused rather than bound to an instance invented now.
            throw new ArgumentException(
                $"PID {processId} does not occur in the evidence this index was derived from, so no instance of it "
                + "exists to bind to.",
                nameof(ownerProcessId));
        }

        if (isLifecycleRecord && observationId is { } exact)
        {
            if (!lifecycleBindings.TryGetValue(exact, out int instance))
            {
                throw new InvalidDataException($"Lifecycle observation {exact} has no binding in this derivation.");
            }

            if (Instances[instance].ProcessId != processId)
            {
                throw new InvalidDataException($"Lifecycle observation {exact} names PID {processId} but binds to "
                    + $"PID {Instances[instance].ProcessId}.");
            }

            return new(instance, RelationStrength.Direct, ProcessBindingReason.Bound);
        }

        return BindWithin(Instances, indexes, nativeTicks, isLifecycleRecord);
    }

    /// <summary>Whether a row is a process lifecycle record: one that creates, ends or confirms an instance.</summary>
    public static bool IsLifecycleRecord(Mechanism mechanism, ObservationKind kind) =>
        mechanism == Mechanism.ProcessLifecycle
        && kind is ObservationKind.Create or ObservationKind.Exit or ObservationKind.Inventory;

    /// <summary>
    /// Derives the instances one capture's published segments support. Every segment must be of one capture on the
    /// clock given, because a PID is only meaningful inside one host's boot and one capture's timeline. The capture's
    /// `source-fields-v1` segments, when it has them, give instances their start keys, parents and sessions.
    /// </summary>
    public static ProcessInstanceIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        IReadOnlyList<SegmentReaderV1>? fieldSegments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var captures = new HashSet<CaptureId>();
        var derivations = new HashSet<NormalizerContractVersion>();
        foreach (SegmentReaderV1 segment in segments.Concat(fieldSegments ?? []))
        {
            if (segment.ClockId != clock.Id)
            {
                throw new ArgumentException(
                    $"Segment {segment.SegmentId} is on clock {segment.ClockId} and the instances are being derived on "
                    + $"{clock.Id}. Process lifetimes are compared on one clock only (I8).",
                    nameof(segments));
            }

            _ = captures.Add(segment.CaptureId);
            _ = derivations.Add(segment.Derivation);
        }

        if (captures.Count > 1)
        {
            throw new ArgumentException(
                $"These segments hold {captures.Count} captures. Process instances are derived per capture, because a "
                + "PID from one capture says nothing about a process in another.",
                nameof(segments));
        }

        if (derivations.Count > 1)
        {
            throw new ArgumentException(
                "Process observations and their source fields must belong to one normalizer derivation; fields "
                + "from another derivation cannot be joined by their raw locator.",
                nameof(fieldSegments));
        }

        Dictionary<RecordAddress, ProcessFields> fields = ReadProcessFields(fieldSegments ?? [], cancellationToken);
        var lifecycle = new List<LifecycleFact>();
        var firstActivity = new Dictionary<int, RecordKey>();
        long withoutOwner = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutOwner += Collect(segment, fields, lifecycle, firstActivity);
        }

        lifecycle.Sort(static (left, right) =>
        {
            int order = left.ProcessId.CompareTo(right.ProcessId);
            return order != 0 ? order : RecordKey.Compare(left.Record, right.Record);
        });

        HostId host = clock.HostId;
        BootId boot = BootId.DeriveFromClock(clock.Id);
        var instances = new List<ProcessInstance>();
        var lifecycleById = new Dictionary<ObservationId, ProcessInstanceId>();
        int cursor = 0;
        while (cursor < lifecycle.Count)
        {
            int end = cursor;
            while (end < lifecycle.Count && lifecycle[end].ProcessId == lifecycle[cursor].ProcessId)
            {
                end++;
            }

            BuildEpochs(host, boot, lifecycle, cursor, end, instances, lifecycleById);
            cursor = end;
        }

        // A PID that records name but no lifecycle record does had one holder for the whole capture as far as the
        // evidence can tell - two would need a witnessed exit and creation between them. It gets one provisional
        // instance witnessed by the first record that names it.
        var lifecyclePids = lifecycle.Select(fact => fact.ProcessId).ToHashSet();
        foreach ((int processId, RecordKey witness) in firstActivity.OrderBy(entry => entry.Key))
        {
            if (lifecyclePids.Contains(processId))
            {
                continue;
            }

            ProcessInstanceKey key = ProcessInstanceKey.FromInventoryWitness(host, boot, processId, 1, witness.RawRecordId);
            instances.Add(new()
            {
                Id = ProcessInstanceId.FromKey(key),
                Key = key,
                Witness = ProcessWitness.ActivityOnly,
                Gaps = ProcessEvidenceGaps.None,
                WitnessRecord = witness.ObservationId,
            });
        }

        // Instances are ordered by PID and epoch, so a listing is stable and identities are assigned independently of
        // which segment a record happened to land in.
        List<ProcessInstance> ordered = [.. instances.OrderBy(instance => instance.ProcessId).ThenBy(instance => instance.LifecycleEpoch)];
        var byPid = new Dictionary<int, int[]>();
        for (int index = 0; index < ordered.Count;)
        {
            int start = index;
            while (index < ordered.Count && ordered[index].ProcessId == ordered[start].ProcessId)
            {
                index++;
            }

            byPid[ordered[start].ProcessId] = [.. Enumerable.Range(start, index - start)];
        }

        List<ProcessInstance> linked = LinkParents(ordered, byPid);
        Dictionary<ProcessInstanceId, int> positions = linked.Select((instance, at) => (instance.Id, at))
            .ToDictionary(entry => entry.Id, entry => entry.at);
        Dictionary<ObservationId, int> exactBindings = lifecycleById.ToDictionary(
            entry => entry.Key,
            entry => positions[entry.Value]);
        return new(
            linked,
            byPid,
            exactBindings,
            host,
            boot,
            clock.Id,
            lifecycle.Count + withoutOwner,
            withoutOwner,
            fields.Values.Any(value => value.Sequence is not null));
    }

    private static ProcessBinding BindWithin(
        IReadOnlyList<ProcessInstance> instances,
        int[] indexes,
        long nativeTicks,
        bool isLifecycleRecord)
    {
        // Lifetimes of one PID are disjoint and ordered, so the only candidate is the last one starting at or before
        // the reading.
        int low = 0;
        int high = indexes.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            long? start = instances[indexes[middle]].LifetimeStartNativeTicks;
            if (start is null || start.Value <= nativeTicks)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
        {
            return ProcessBinding.Unresolved(ProcessBindingReason.BeforeFirstEvidence);
        }

        ProcessInstance instance = instances[indexes[found]];
        if (!instance.Contains(nativeTicks))
        {
            return ProcessBinding.Unresolved(
                found == indexes.Length - 1 ? ProcessBindingReason.AfterExit : ProcessBindingReason.BetweenInstances);
        }

        // Inside the PID's first instance a record can only be that instance's or a late record of a holder the
        // capture never witnessed, which is the same risk a PID that was never reused carries. Inside a later
        // instance it can also be a late record of the earlier one the capture did witness, and PID and time cannot
        // tell the two apart: that is a candidate (identity-v1).
        RelationStrength strength = isLifecycleRecord
            ? RelationStrength.Direct
            : found == 0 ? RelationStrength.Correlated : RelationStrength.Candidate;
        return new(indexes[found], strength, ProcessBindingReason.Bound);
    }

    /// <summary>
    /// Links each instance to the instance that created it. The parent's start key identifies it exactly; without one,
    /// the parent is the instance its PID named at the child's creation, bound as any record of that PID would be.
    /// </summary>
    private static List<ProcessInstance> LinkParents(List<ProcessInstance> instances, Dictionary<int, int[]> byPid)
    {
        var byStartKey = new Dictionary<(int Pid, ulong Sequence), int>();
        for (int index = 0; index < instances.Count; index++)
        {
            if (instances[index].StartKey is { } key)
            {
                byStartKey[(instances[index].ProcessId, key.SequenceNumber)] = index;
            }
        }

        var linked = new List<ProcessInstance>(instances.Count);
        foreach (ProcessInstance instance in instances)
        {
            if (instance.ParentProcessId is not { } parentPid)
            {
                linked.Add(instance);
                continue;
            }

            if (instance.ParentStartSequence is { } parentSequence
                && byStartKey.TryGetValue((parentPid, parentSequence), out int exact))
            {
                linked.Add(instance with { Parent = instances[exact].Id, ParentBinding = RelationStrength.Direct });
                continue;
            }

            long? at = instance.CreatedNativeTicks ?? instance.LifetimeStartNativeTicks;
            if (instance.ParentStartSequence is null && at is { } moment && byPid.TryGetValue(parentPid, out int[]? candidates))
            {
                ProcessBinding binding = BindWithin(instances, candidates, moment, isLifecycleRecord: false);
                if (binding.IsBound)
                {
                    linked.Add(instance with { Parent = instances[binding.Instance].Id, ParentBinding = binding.Strength });
                    continue;
                }
            }

            // The parent is named but is not in the capture, or its start key matches no instance: it is kept as the
            // PID and key the record named, and no instance is invented for it.
            linked.Add(instance);
        }

        return linked;
    }

    /// <summary>The process fields of every lifecycle record, keyed by the observation they belong to.</summary>
    private static Dictionary<RecordAddress, ProcessFields> ReadProcessFields(
        IReadOnlyList<SegmentReaderV1> fieldSegments,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<RecordAddress, ProcessFields>();
        var seen = new HashSet<(RecordAddress Address, SourceField Field)>();
        foreach (SegmentReaderV1 segment in fieldSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Table != SegmentTableId.SourceFieldsV1)
            {
                throw new ArgumentException("A source-field segment must hold source-fields-v1.", nameof(fieldSegments));
            }

            // Most field rows describe other mechanisms. Their code is read from its column and only a process field's
            // row is materialized, and validated, whole: materializing every row cost 42 ms of a 10-minute capture's
            // 80 ms derivation (revision 129). An undefined code still refuses the segment, as a materialized row would.
            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.ProcessStartSequence or SourceField.ProcessCreateTime
                    or SourceField.ParentProcessId or SourceField.ParentStartSequence
                    or SourceField.ProcessSessionId or SourceField.ProcessExitTime))
                {
                    if (!Enum.IsDefined(code))
                    {
                        throw new InvalidDataException(
                            $"Row {row} carries source field {(ushort)code}, which §23 does not define. A required field "
                            + "with an unknown code refuses the artifact (§23).");
                    }

                    continue;
                }

                SourceFieldRowV1 source = segment.FieldRow(row);
                SourceField field = source.Field;

                var address = new RecordAddress(
                    source.RawStreamId,
                    source.RawSourceEpoch,
                    source.RawRecordOrdinal,
                    source.FactKey);
                if (!seen.Add((address, field)))
                {
                    throw new InvalidDataException(
                        $"Observation {address} carries {field} in more than one source-field segment; "
                        + "a duplicated field could change its process identity.");
                }

                if (source.Value is not { } value)
                {
                    continue;
                }

                ProcessFields current = fields.TryGetValue(address, out ProcessFields existing) ? existing : default;
                fields[address] = field switch
                {
                    SourceField.ProcessStartSequence => current with { Sequence = unchecked((ulong)value) },
                    SourceField.ProcessCreateTime => current with { CreateFileTime = value },
                    SourceField.ParentProcessId => current with { ParentProcessId = unchecked((int)value) },
                    SourceField.ParentStartSequence => current with { ParentSequence = unchecked((ulong)value) },
                    SourceField.ProcessSessionId => current with { SessionId = unchecked((uint)value) },
                    _ => current with { ExitFileTime = value },
                };
            }
        }

        return fields;
    }

    /// <summary>Reads one segment's lifecycle records and the first record that names each PID.</summary>
    private static long Collect(
        SegmentReaderV1 segment,
        Dictionary<RecordAddress, ProcessFields> fields,
        List<LifecycleFact> lifecycle,
        Dictionary<int, RecordKey> firstActivity)
    {
        SegmentColumnSlice owners = segment.Slice(SegmentColumnId.OwnerProcessId);
        SegmentColumnSlice ticks = segment.Slice(SegmentColumnId.NativeTicks);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.ObservationKind);
        SegmentColumnSlice statuses = segment.Slice(SegmentColumnId.StatusCode);
        SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
        SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
        SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
        SegmentColumnSlice factHigh = segment.Slice(SegmentColumnId.FactKeyHigh);
        SegmentColumnSlice factLow = segment.Slice(SegmentColumnId.FactKeyLow);
        long withoutOwner = 0;

        for (int row = 0; row < segment.RowCount; row++)
        {
            var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
            var kind = (ObservationKind)kinds.UnsignedAt(row)!.Value;
            bool isLifecycle = IsLifecycleRecord(mechanism, kind);
            if (owners.SignedAt(row) is not { } owner)
            {
                withoutOwner += isLifecycle ? 1 : 0;
                continue;
            }

            int processId = (int)owner;
            long reading = ticks.SignedAt(row)!.Value;

            // Only a record that could be its PID's earliest needs its whole key: the key orders by reading first.
            if (!isLifecycle
                && firstActivity.TryGetValue(processId, out RecordKey known)
                && reading > known.NativeTicks)
            {
                continue;
            }

            var record = new RecordKey(
                reading,
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value),
                segment.CaptureId,
                segment.Derivation);
            if (isLifecycle)
            {
                // The name is read for lifecycle records only; it is the image a creation or rundown carries, or the
                // image file name an exit carries.
                lifecycle.Add(new(
                    processId,
                    kind,
                    record,
                    statuses.SignedAt(row),
                    segment.TextValue(SegmentColumnId.ResourceName, row),
                    fields.TryGetValue(record.Address, out ProcessFields found) ? found : default));
                continue;
            }

            if (!firstActivity.TryGetValue(processId, out RecordKey earliest) || RecordKey.Compare(record, earliest) < 0)
            {
                firstActivity[processId] = record;
            }
        }

        return withoutOwner;
    }

    /// <summary>
    /// Builds one PID's instances from its lifecycle records, in reading order. Each record creates an instance, ends
    /// the live one, or confirms it; a record that contradicts the one before it - a creation while one is live, a
    /// record carrying another start key - opens an instance flagged with the gap rather than being dropped or merged.
    /// </summary>
    private static void BuildEpochs(
        HostId host,
        BootId boot,
        List<LifecycleFact> facts,
        int from,
        int to,
        List<ProcessInstance> instances,
        Dictionary<ObservationId, ProcessInstanceId> lifecycleById)
    {
        EpochDraft? live = null;
        EpochDraft? previous = null;
        uint epoch = 0;
        var drafts = new List<EpochDraft>();
        for (int index = from; index < to; index++)
        {
            LifecycleFact fact = facts[index];
            if (live is not null
                && fact.Kind != ObservationKind.Create
                && fact.Fields.Sequence is { } carried
                && live.Sequence is { } expected
                && carried != expected)
            {
                // The record carries another instance's start key: the live instance ended without its exit being
                // witnessed, and the record's own instance began without its creation being witnessed.
                live.End = fact.Record.NativeTicks;
                live.Gaps |= ProcessEvidenceGaps.ExitNotWitnessed;
                previous = live;
                live = null;
            }

            switch (fact.Kind)
            {
                case ObservationKind.Create:
                    if (live is not null)
                    {
                        // A second creation while one instance is still live: the first one's exit was not witnessed.
                        live.End = fact.Record.NativeTicks;
                        live.Gaps |= ProcessEvidenceGaps.ExitNotWitnessed;
                        previous = live;
                    }

                    live = new(++epoch, ProcessWitness.Created, fact.Record) { Start = fact.Record.NativeTicks, Creation = fact.Record };
                    live.Absorb(fact);
                    drafts.Add(live);
                    break;
                case ObservationKind.Exit:
                    if (live is null)
                    {
                        // An exit with no live instance: the process was running before any evidence of it, or - after
                        // an earlier instance ended - it was created without its creation being witnessed.
                        live = new(++epoch, ProcessWitness.ExitOnly, fact.Record) { Start = previous?.End };
                        if (previous is not null)
                        {
                            live.Gaps |= ProcessEvidenceGaps.CreationNotWitnessed;
                        }

                        drafts.Add(live);
                    }

                    live.Exit = fact.Record;
                    live.ExitCode = fact.ExitCode;
                    live.End = checked(fact.Record.NativeTicks + 1);
                    live.Absorb(fact);
                    previous = live;
                    live = null;
                    break;
                default:
                    if (live is null)
                    {
                        live = new(++epoch, ProcessWitness.Rundown, fact.Record) { Start = previous?.End };
                        if (previous is not null)
                        {
                            live.Gaps |= ProcessEvidenceGaps.CreationNotWitnessed;
                        }

                        drafts.Add(live);
                    }

                    live.Rundown ??= fact.Record;
                    live.Absorb(fact);
                    break;
            }
        }

        int processId = facts[from].ProcessId;
        foreach (EpochDraft draft in drafts)
        {
            ProcessStartKey? startKey = draft.Sequence is { } sequence ? new ProcessStartKey(sequence, draft.CreateFileTime) : null;
            ProcessInstanceKey key = startKey is { } provider
                ? ProcessInstanceKey.FromProviderStart(host, boot, processId, draft.Epoch, provider)
                : draft.Witness == ProcessWitness.Created && draft.Creation is { } creation && creation.NativeTicks >= 0
                    ? ProcessInstanceKey.FromObservedCreation(host, boot, processId, draft.Epoch, creation.NativeTicks)
                    : ProcessInstanceKey.FromInventoryWitness(host, boot, processId, draft.Epoch, draft.WitnessRecord.RawRecordId);
            ProcessInstance instance = new()
            {
                Id = ProcessInstanceId.FromKey(key),
                Key = key,
                Witness = draft.Witness,
                Gaps = draft.Gaps,
                StartKey = startKey,
                CreatedNativeTicks = draft.Creation?.NativeTicks,
                ExitedNativeTicks = draft.Exit?.NativeTicks,
                ExitCode = draft.ExitCode,
                LifetimeStartNativeTicks = draft.Start,
                LifetimeEndNativeTicks = draft.End,
                ImagePath = draft.ImagePath,
                ImageName = draft.ImagePath is { } path ? FileNameOf(path) : draft.ExitName,
                SessionId = draft.SessionId,
                CreateFileTime = draft.CreateFileTime,
                ExitFileTime = draft.ExitFileTime,
                ParentProcessId = draft.ParentProcessId,
                ParentStartSequence = draft.ParentSequence,
                WitnessRecord = draft.WitnessRecord.ObservationId,
                CreationRecord = draft.Creation?.ObservationId,
                ExitRecord = draft.Exit?.ObservationId,
                RundownRecord = draft.Rundown?.ObservationId,
            };
            instances.Add(instance);
            foreach (ObservationId observation in draft.Records)
            {
                if (!lifecycleById.TryAdd(observation, instance.Id))
                {
                    throw new InvalidDataException($"Lifecycle observation {observation} belongs to more than one instance.");
                }
            }
        }
    }

    /// <summary>The last component of an image path, whichever separator it uses.</summary>
    private static string FileNameOf(string path)
    {
        int separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 || separator == path.Length - 1 ? path : path[(separator + 1)..];
    }

    /// <summary>Where one record is: its raw locator and fact key, without the capture every record here shares.</summary>
    private readonly record struct RecordAddress(uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey);

    /// <summary>One record's place in canonical order and the identities it carries.</summary>
    private readonly record struct RecordKey(
        long NativeTicks,
        uint Stream,
        uint Epoch,
        ulong Ordinal,
        FactKey FactKey,
        CaptureId Capture,
        NormalizerContractVersion Derivation)
    {
        public RawRecordId RawRecordId => new(Capture, Stream, Epoch, Ordinal);

        public ObservationId ObservationId => new(RawRecordId, Derivation, FactKey);

        public RecordAddress Address => new(Stream, Epoch, Ordinal, FactKey);

        /// <summary>The canonical row order of segment-v1: native reading, then the raw locator, then the fact key.</summary>
        public static int Compare(RecordKey left, RecordKey right)
        {
            int order = left.NativeTicks.CompareTo(right.NativeTicks);
            order = order != 0 ? order : left.Stream.CompareTo(right.Stream);
            order = order != 0 ? order : left.Epoch.CompareTo(right.Epoch);
            order = order != 0 ? order : left.Ordinal.CompareTo(right.Ordinal);
            order = order != 0 ? order : left.FactKey.High.CompareTo(right.FactKey.High);
            return order != 0 ? order : left.FactKey.Low.CompareTo(right.FactKey.Low);
        }
    }

    /// <summary>The process source fields one lifecycle record carried, each null when it carried none.</summary>
    private readonly record struct ProcessFields(
        ulong? Sequence,
        long? CreateFileTime,
        int? ParentProcessId,
        ulong? ParentSequence,
        uint? SessionId,
        long? ExitFileTime);

    private readonly record struct LifecycleFact(
        int ProcessId,
        ObservationKind Kind,
        RecordKey Record,
        long? ExitCode,
        string? Name,
        ProcessFields Fields);

    private sealed class EpochDraft(uint epoch, ProcessWitness witness, RecordKey witnessRecord)
    {
        public uint Epoch { get; } = epoch;

        public ProcessWitness Witness { get; } = witness;

        public RecordKey WitnessRecord { get; } = witnessRecord;

        public ProcessEvidenceGaps Gaps { get; set; }

        public long? Start { get; set; }

        public long? End { get; set; }

        public RecordKey? Creation { get; set; }

        public RecordKey? Exit { get; set; }

        public RecordKey? Rundown { get; set; }

        public long? ExitCode { get; set; }

        public ulong? Sequence { get; private set; }

        public long? CreateFileTime { get; private set; }

        public long? ExitFileTime { get; private set; }

        public int? ParentProcessId { get; private set; }

        public ulong? ParentSequence { get; private set; }

        public uint? SessionId { get; private set; }

        public string? ImagePath { get; private set; }

        public string? ExitName { get; private set; }

        public List<ObservationId> Records { get; } = [];

        /// <summary>
        /// Takes what a lifecycle record says about the instance. The first record to state a fact states it; a later
        /// one never overwrites it, so an instance's attributes are those of its earliest evidence.
        /// </summary>
        public void Absorb(LifecycleFact fact)
        {
            Records.Add(fact.Record.ObservationId);
            Sequence ??= fact.Fields.Sequence;
            CreateFileTime ??= fact.Fields.CreateFileTime;
            ExitFileTime ??= fact.Fields.ExitFileTime;
            ParentProcessId ??= fact.Fields.ParentProcessId;
            ParentSequence ??= fact.Fields.ParentSequence;
            SessionId ??= fact.Fields.SessionId;
            if (fact.Kind == ObservationKind.Exit)
            {
                ExitName ??= fact.Name;
            }
            else
            {
                ImagePath ??= fact.Name;
            }
        }
    }
}
