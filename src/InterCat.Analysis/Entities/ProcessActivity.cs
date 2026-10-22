using System.Buffers;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// How many records each process instance was observed making, by mechanism: every record whose own owner binds to the
/// instance (`contracts/entities-v1.md` §4). A lifecycle record binds directly to the instance it creates, ends or
/// confirms, and any other record by its reading, as strongly as the instance's place among its PID's instances allows.
/// The ranked table orders processes by it: what each process did, where a relation counts only what it exchanged with
/// a local peer (revision 166).
/// </summary>
/// <remarks>
/// Its state is one entry per instance and mechanism, and one per PID: the earliest and latest reading of any record
/// naming the PID, and how many of them bind to no instance. A later generation extends it as the relations are
/// extended (IC-015): when every PID's readings bind alike under the new process index, the counts move to the
/// instances they now bind to, and only the added segments are read.
/// </remarks>
public sealed partial class ProcessActivityIndex
{
    /// <summary>The rule identity a result names when it ranks by these counts.</summary>
    public const string CountRule = "process-activity-v1";

    private readonly ProcessInstanceIndex processes;

    /// <summary>By position in <see cref="processes"/>: the records bound to the instance, per mechanism.</summary>
    private readonly Dictionary<int, InstanceActivity> instances;

    /// <summary>Every PID a record names: the span of those records' readings, and how many bind to no instance.</summary>
    private readonly Dictionary<int, PidSpan> pids;

    /// <summary>The published files counted; null when one had no published identity.</summary>
    private readonly HashSet<StoreDependency>? read;

    /// <summary>The records binding to no instance, with the transport bytes they measured (revision 737).</summary>
    private readonly OwnerTally unowned;

    private ProcessActivityIndex(
        ProcessInstanceIndex processes,
        Dictionary<int, InstanceActivity> instances,
        Dictionary<int, PidSpan> pids,
        long withoutOwner,
        HashSet<StoreDependency>? read,
        OwnerTally unowned,
        bool ownersKept)
    {
        this.processes = processes;
        this.instances = instances;
        this.pids = pids;
        RecordsWithoutOwner = withoutOwner;
        this.read = read;
        this.unowned = unowned;
        OwnersKept = ownersKept;
    }

    /// <summary>
    /// Whether it counts every record by the owner it binds to and how strongly, with the transport bytes it measured
    /// (revision 737): wherever the records were counted, and not where the counts were read back from a checkpoint, which
    /// keeps no such counts.
    /// </summary>
    public bool OwnersKept { get; }

    /// <summary>
    /// Whether a byte view refuses a record counted here: one naming no accounting side, one §23 does not define, no unit
    /// or a unit other than bytes. A view then reads the rows, which say why.
    /// </summary>
    public bool BytesRefused => unowned.BytesRefused || instances.Values.Any(activity => activity.Owned.BytesRefused);

    /// <summary>
    /// Every record by the instance it binds to and how strongly - or to none, at instance -1 - with the transport bytes it
    /// measured: exactly what a pass binding each row by its owner counts (`entities-v1` §4, revision 737). The records are
    /// all but a process's or a thread's lifecycle records; the bytes are every record's. Null when these counts keep no
    /// owners (<see cref="OwnersKept"/>).
    /// </summary>
    public IReadOnlyList<RecordOwners>? Owners()
    {
        if (!OwnersKept)
        {
            return null;
        }

        var owners = new List<RecordOwners>();
        foreach (InstanceActivity activity in instances.Values)
        {
            owners.AddRange(activity.Owned.Snapshot());
        }

        owners.AddRange(unowned.Snapshot());
        return owners;
    }

    /// <summary>Records that name no owner under the binding rule: no instance can hold them (§4.1).</summary>
    public long RecordsWithoutOwner { get; private set; }

    /// <summary>Records naming a PID at a reading no instance of it held: before its first evidence, between, or after.</summary>
    public long RecordsNotBound => pids.Values.Sum(span => span.Unbound);

    /// <summary>The process index the positions here are positions in.</summary>
    internal ProcessInstanceIndex Processes => processes;

    /// <summary>
    /// Whether these are the counts of exactly <paramref name="index"/>'s instances, so that a position in it is a position
    /// here; counts made under another derivation's instances may bind the same records elsewhere.
    /// </summary>
    public bool Counts(ProcessInstanceIndex index) => ReferenceEquals(processes, index);

    /// <summary>
    /// The records of the instance at <paramref name="instance"/> that <paramref name="policy"/> admits: its lifecycle
    /// records always, the rest when the policy admits how strongly the instance binds them.
    /// </summary>
    public long RecordsOf(int instance, EvidencePolicy policy) =>
        MechanismsOf(instance, policy).Sum(entry => entry.Records);

    /// <summary>
    /// What <see cref="RecordsOf(int, EvidencePolicy)"/> counts of <paramref name="mechanism"/> alone, as a pass binding
    /// each of the instance's records by its owner and keeping those the policy admits would count them.
    /// </summary>
    public long RecordsOf(int instance, EvidencePolicy policy, Mechanism mechanism)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(instance, processes.Instances.Count);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (!instances.TryGetValue(instance, out InstanceActivity? activity))
        {
            return 0;
        }

        long records = activity.Direct.GetValueOrDefault(mechanism);
        return new ProcessBinding(instance, StrengthOf(instance), ProcessBindingReason.Bound).IsAdmittedUnder(policy)
            ? records + activity.Bound.GetValueOrDefault(mechanism)
            : records;
    }

    /// <summary>What <see cref="RecordsOf"/> counts, by mechanism, largest first and then by mechanism code.</summary>
    public IReadOnlyList<(Mechanism Mechanism, long Records)> MechanismsOf(int instance, EvidencePolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(instance, processes.Instances.Count);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (!instances.TryGetValue(instance, out InstanceActivity? activity))
        {
            return [];
        }

        bool boundAdmitted = new ProcessBinding(instance, StrengthOf(instance), ProcessBindingReason.Bound).IsAdmittedUnder(policy);
        var totals = new Dictionary<Mechanism, long>();
        foreach ((Mechanism mechanism, long records) in activity.Direct)
        {
            totals[mechanism] = totals.GetValueOrDefault(mechanism) + records;
        }

        if (boundAdmitted)
        {
            foreach ((Mechanism mechanism, long records) in activity.Bound)
            {
                totals[mechanism] = totals.GetValueOrDefault(mechanism) + records;
            }
        }

        return [.. totals.Where(entry => entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .Select(entry => (entry.Key, entry.Value))];
    }

    /// <summary>
    /// The records bound to the instance at <paramref name="instance"/> that <paramref name="policy"/> leaves out of
    /// <see cref="RecordsOf"/>: every one but its lifecycle records, when the policy does not admit how strongly the
    /// instance binds them. A later holder of a reused PID binds them only as candidates (identity-v1), so a policy that
    /// admits no candidate counts them in no process. Zero when the policy admits them.
    /// </summary>
    public long WithheldOf(int instance, EvidencePolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(instance, processes.Instances.Count);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        return instances.TryGetValue(instance, out InstanceActivity? activity)
            && !new ProcessBinding(instance, StrengthOf(instance), ProcessBindingReason.Bound).IsAdmittedUnder(policy)
            ? activity.Bound.Values.Sum()
            : 0;
    }

    /// <summary>
    /// The records bound to the instance at <paramref name="instance"/> only as candidates, whatever a policy admits:
    /// every one but its lifecycle records when it is a later holder of a reused PID (identity-v1), and none otherwise.
    /// </summary>
    public long CandidatesOf(int instance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(instance, processes.Instances.Count);
        return StrengthOf(instance) == RelationStrength.Candidate && instances.TryGetValue(instance, out InstanceActivity? activity)
            ? activity.Bound.Values.Sum()
            : 0;
    }

    /// <summary>Counts the records of <paramref name="segments"/>, the segments <paramref name="processes"/> was derived from.</summary>
    public static ProcessActivityIndex Derive(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);
        var activity = new ProcessActivityIndex(processes, [], [], 0, PublishedAs(segments), new(), ownersKept: true);
        foreach (SegmentReaderV1 segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity.Count(segment);
        }

        return activity;
    }

    /// <summary>
    /// The counts of <paramref name="segments"/>, extended from these rather than counted again, when the segments hold
    /// every one counted here and every PID's readings bind alike under <paramref name="processes"/>. The result is
    /// exactly <see cref="Derive"/>'s over the same segments. Null, for a full count, when a counted segment is missing
    /// or a reading already counted would bind otherwise.
    /// </summary>
    public ProcessActivityIndex? Extend(
        IReadOnlyList<SegmentReaderV1> segments,
        ProcessInstanceIndex processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);

        // Which segments were counted is told by the identities the segments declare, so one already counted is not opened
        // again.
        StoreDependency?[] offered = RpcCallIndex.IdentitiesOf(segments);
        if (read is null || offered.Any(identity => identity is null) || !read.IsSubsetOf(offered!))
        {
            return null;
        }

        // A record binds by its PID and reading, so where every PID's readings bind alike, every record counted here binds
        // to the instance the map gives, as strongly; a lifecycle record to the instance whose lifetime it bounds.
        var mapped = new Dictionary<int, int>();
        foreach ((int processId, PidSpan span) in pids)
        {
            if (span.First <= span.Last && !this.processes.BindsAlike(processes, processId, span.First, span.Last, mapped))
            {
                return null;
            }
        }

        var moved = new Dictionary<int, InstanceActivity>(instances.Count);
        foreach ((int position, InstanceActivity activity) in instances)
        {
            if (!mapped.TryGetValue(position, out int to) || moved.ContainsKey(to) || activity.Copy(mapped) is not { } copy)
            {
                return null;
            }

            moved[to] = copy;
        }

        var extended = new ProcessActivityIndex(
            processes,
            moved,
            pids.ToDictionary(entry => entry.Key, entry => entry.Value.Copy()),
            RecordsWithoutOwner,
            [.. read],
            unowned.Copy(mapped: null)!,
            OwnersKept);
        foreach (SegmentReaderV1 segment in Enumerable.Range(0, segments.Count).Where(index => !read.Contains(offered[index]!))
            .Select(index => segments[index]))
        {
            cancellationToken.ThrowIfCancellationRequested();
            extended.Count(segment);
            _ = extended.read!.Add(segment.Published!);
        }

        return extended;
    }

    /// <summary>How strongly the instance binds a record that is not one of its lifecycle records (identity-v1).</summary>
    private RelationStrength StrengthOf(int instance) =>
        processes.Instances[instance].LifecycleEpoch == 1 ? RelationStrength.Correlated : RelationStrength.Candidate;

    private void Count(SegmentReaderV1 segment)
    {
        ProcessBinding[] bindings = ArrayPool<ProcessBinding>.Shared.Rent(segment.RowCount);
        try
        {
            ProcessRoles.OwnersOf(processes, segment, bindings);
            var owners = new RecordOwnerColumns(segment);
            SegmentColumnSlice ticks = segment.Slice(SegmentColumnId.NativeTicks);
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            var measured = new ByteDomainRows(segment, ByteDomain.TransportObserved);
            for (int row = 0; row < segment.RowCount; row++)
            {
                var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;

                // Every record by the owner it binds to, as a pass binding each row by its owner reads it (revision 737).
                OwnedRecord record = OwnedRecord.Of(in measured, row, mechanism);
                if (bindings[row] is { IsBound: true } owned)
                {
                    if (!instances.TryGetValue(owned.Instance, out InstanceActivity? holder))
                    {
                        holder = new();
                        instances[owned.Instance] = holder;
                    }

                    holder.Owned.Add(owned, record);
                }
                else
                {
                    unowned.Add(bindings[row], record);
                }

                if (owners.At(row, mechanism) is not { } processId)
                {
                    RecordsWithoutOwner++;
                    continue;
                }

                long reading = ticks.SignedAt(row)!.Value;
                if (!pids.TryGetValue(processId, out PidSpan? span))
                {
                    span = new();
                    pids[processId] = span;
                }

                span.First = Math.Min(span.First, reading);
                span.Last = Math.Max(span.Last, reading);
                ProcessBinding binding = bindings[row];
                if (!binding.IsBound)
                {
                    span.Unbound++;
                    continue;
                }

                if (!instances.TryGetValue(binding.Instance, out InstanceActivity? activity))
                {
                    activity = new();
                    instances[binding.Instance] = activity;
                }

                Dictionary<Mechanism, long> tally = binding.Strength == RelationStrength.Direct ? activity.Direct : activity.Bound;
                tally[mechanism] = tally.GetValueOrDefault(mechanism) + 1;
            }
        }
        finally
        {
            ArrayPool<ProcessBinding>.Shared.Return(bindings);
        }
    }

    private static HashSet<StoreDependency>? PublishedAs(IReadOnlyList<SegmentReaderV1> segments)
    {
        var published = new HashSet<StoreDependency>(segments.Count);
        foreach (SegmentReaderV1 segment in segments)
        {
            if (segment.Published is not { } file)
            {
                return null;
            }

            _ = published.Add(file);
        }

        return published;
    }

    /// <summary>
    /// One instance's records, lifecycle records apart from the rest, per mechanism; and every record binding to it by how
    /// strongly, with the transport bytes they measured.
    /// </summary>
    private sealed class InstanceActivity
    {
        public Dictionary<Mechanism, long> Direct { get; private init; } = [];

        public Dictionary<Mechanism, long> Bound { get; private init; } = [];

        public OwnerTally Owned { get; private init; } = new();

        /// <summary>A copy sharing nothing, its owner at the position <paramref name="mapped"/> gives it; null where it has none.</summary>
        public InstanceActivity? Copy(IReadOnlyDictionary<int, int>? mapped) =>
            Owned.Copy(mapped) is { } owned ? new() { Direct = new(Direct), Bound = new(Bound), Owned = owned } : null;
    }

    /// <summary>The earliest and latest reading of any record naming one PID, and how many of them bind to nothing.</summary>
    private sealed class PidSpan
    {
        public long First { get; set; } = long.MaxValue;

        public long Last { get; set; } = long.MinValue;

        public long Unbound { get; set; }

        public PidSpan Copy() => new() { First = First, Last = Last, Unbound = Unbound };
    }
}
