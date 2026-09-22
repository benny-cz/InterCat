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

    /// <summary>The next instance of the PID was created with no exit witnessed first, so this one ends at that creation.</summary>
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
/// order of their native readings. A PID that no lifecycle record names but that records are attributed to gets one
/// provisional instance witnessed by the first of those records: evidence that it existed, never an invented start.
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
/// The rule assumes the capture lost no lifecycle record. A lost exit and creation pair would merge two instances,
/// and no session publishes a coverage ledger yet to say whether one was lost.
/// </para>
/// </remarks>
public sealed class ProcessInstanceIndex
{
    /// <summary>The binding rule's identity. A change to what binds, or how strongly, is a new rule (§24 entityRevision).</summary>
    public const string BindingRule = "process-binding-v1";

    private readonly Dictionary<int, int[]> instancesByPid;

    private ProcessInstanceIndex(
        IReadOnlyList<ProcessInstance> instances,
        Dictionary<int, int[]> instancesByPid,
        HostId host,
        BootId boot,
        ClockId clock,
        long lifecycleRecords,
        long lifecycleRecordsWithoutOwner)
    {
        Instances = instances;
        this.instancesByPid = instancesByPid;
        Host = host;
        Boot = boot;
        Clock = clock;
        LifecycleRecords = lifecycleRecords;
        LifecycleRecordsWithoutOwner = lifecycleRecordsWithoutOwner;
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
    /// Binds one record, given the owner its payload names, its native reading and whether it is a process
    /// lifecycle record. Pure: the same inputs bind the same way every time.
    /// </summary>
    public ProcessBinding Bind(int? ownerProcessId, long nativeTicks, bool isLifecycleRecord)
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

        // Lifetimes of one PID are disjoint and ordered, so the only candidate is the last one starting at or before
        // the reading.
        int low = 0;
        int high = indexes.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            long? start = Instances[indexes[middle]].LifetimeStartNativeTicks;
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

        ProcessInstance instance = Instances[indexes[found]];
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

    /// <summary>Whether a row is a process lifecycle record: one that creates, ends or confirms an instance.</summary>
    public static bool IsLifecycleRecord(Mechanism mechanism, ObservationKind kind) =>
        mechanism == Mechanism.ProcessLifecycle
        && kind is ObservationKind.Create or ObservationKind.Exit or ObservationKind.Inventory;

    /// <summary>
    /// Derives the instances one capture's published segments support. Every segment must be of one capture on the
    /// clock given, because a PID is only meaningful inside one host's boot and one capture's timeline.
    /// </summary>
    public static ProcessInstanceIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        SourceClockDescriptor clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var captures = new HashSet<CaptureId>();
        foreach (SegmentReaderV1 segment in segments)
        {
            if (segment.ClockId != clock.Id)
            {
                throw new ArgumentException(
                    $"Segment {segment.SegmentId} is on clock {segment.ClockId} and the instances are being derived on "
                    + $"{clock.Id}. Process lifetimes are compared on one clock only (I8).",
                    nameof(segments));
            }

            _ = captures.Add(segment.CaptureId);
        }

        if (captures.Count > 1)
        {
            throw new ArgumentException(
                $"These segments hold {captures.Count} captures. Process instances are derived per capture, because a "
                + "PID from one capture says nothing about a process in another.",
                nameof(segments));
        }

        var lifecycle = new List<LifecycleFact>();
        var firstActivity = new Dictionary<int, RecordKey>();
        long withoutOwner = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            withoutOwner += Collect(segment, lifecycle, firstActivity);
        }

        lifecycle.Sort(static (left, right) =>
        {
            int order = left.ProcessId.CompareTo(right.ProcessId);
            return order != 0 ? order : RecordKey.Compare(left.Record, right.Record);
        });

        HostId host = clock.HostId;
        BootId boot = BootId.DeriveFromClock(clock.Id);
        var instances = new List<ProcessInstance>();
        var byPid = new Dictionary<int, int[]>();
        int cursor = 0;
        while (cursor < lifecycle.Count)
        {
            int processId = lifecycle[cursor].ProcessId;
            int end = cursor;
            while (end < lifecycle.Count && lifecycle[end].ProcessId == processId)
            {
                end++;
            }

            int first = instances.Count;
            BuildEpochs(host, boot, lifecycle, cursor, end, instances);
            byPid[processId] = [.. Enumerable.Range(first, instances.Count - first)];
            cursor = end;
        }

        // A PID that records name but no lifecycle record does had one holder for the whole capture as far as the
        // evidence can tell - two would need a witnessed exit and creation between them. It gets one provisional
        // instance witnessed by the first record that names it.
        foreach ((int processId, RecordKey witness) in firstActivity.OrderBy(entry => entry.Key))
        {
            if (byPid.ContainsKey(processId))
            {
                continue;
            }

            ProcessInstanceKey key = ProcessInstanceKey.FromInventoryWitness(host, boot, processId, 1, witness.RawRecordId);
            byPid[processId] = [instances.Count];
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
        var ordered = instances
            .Select((instance, index) => (instance, index))
            .OrderBy(entry => entry.instance.ProcessId)
            .ThenBy(entry => entry.instance.LifecycleEpoch)
            .ToList();
        var remap = new int[instances.Count];
        for (int position = 0; position < ordered.Count; position++)
        {
            remap[ordered[position].index] = position;
        }

        var remapped = new Dictionary<int, int[]>(byPid.Count);
        foreach ((int processId, int[] indexes) in byPid)
        {
            remapped[processId] = [.. indexes.Select(index => remap[index])];
        }

        return new(
            [.. ordered.Select(entry => entry.instance)],
            remapped,
            host,
            boot,
            clock.Id,
            lifecycle.Count + withoutOwner,
            withoutOwner);
    }

    /// <summary>Reads one segment's lifecycle records and the first record that names each PID.</summary>
    private static long Collect(
        SegmentReaderV1 segment,
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
            var record = new RecordKey(
                ticks.SignedAt(row)!.Value,
                (uint)streams.UnsignedAt(row)!.Value,
                (uint)epochs.UnsignedAt(row)!.Value,
                ordinals.UnsignedAt(row)!.Value,
                new FactKey(factHigh.UnsignedAt(row)!.Value, factLow.UnsignedAt(row)!.Value),
                segment.CaptureId,
                segment.Derivation);
            if (isLifecycle)
            {
                lifecycle.Add(new(processId, kind, record, statuses.SignedAt(row)));
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
    /// the live one, or confirms it; a record that contradicts the one before it opens an instance flagged with the
    /// gap rather than being dropped or merged.
    /// </summary>
    private static void BuildEpochs(
        HostId host,
        BootId boot,
        List<LifecycleFact> facts,
        int from,
        int to,
        List<ProcessInstance> instances)
    {
        EpochDraft? live = null;
        EpochDraft? previous = null;
        uint epoch = 0;
        var drafts = new List<EpochDraft>();
        for (int index = from; index < to; index++)
        {
            LifecycleFact fact = facts[index];
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
                    break;
            }
        }

        foreach (EpochDraft draft in drafts)
        {
            int processId = facts[from].ProcessId;
            ProcessInstanceKey key = draft.Witness == ProcessWitness.Created && draft.Creation is { } creation && creation.NativeTicks >= 0
                ? ProcessInstanceKey.FromObservedCreation(host, boot, processId, draft.Epoch, creation.NativeTicks)
                : ProcessInstanceKey.FromInventoryWitness(host, boot, processId, draft.Epoch, draft.WitnessRecord.RawRecordId);
            instances.Add(new()
            {
                Id = ProcessInstanceId.FromKey(key),
                Key = key,
                Witness = draft.Witness,
                Gaps = draft.Gaps,
                CreatedNativeTicks = draft.Creation?.NativeTicks,
                ExitedNativeTicks = draft.Exit?.NativeTicks,
                ExitCode = draft.ExitCode,
                LifetimeStartNativeTicks = draft.Start,
                LifetimeEndNativeTicks = draft.End,
                WitnessRecord = draft.WitnessRecord.ObservationId,
                CreationRecord = draft.Creation?.ObservationId,
                ExitRecord = draft.Exit?.ObservationId,
                RundownRecord = draft.Rundown?.ObservationId,
            });
        }
    }

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

    private readonly record struct LifecycleFact(int ProcessId, ObservationKind Kind, RecordKey Record, long? ExitCode);

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
    }
}
